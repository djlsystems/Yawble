using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// Identity's tables, as one step: everything in this database that is not the message log, the
/// skill index or the backlog.
///
/// ONE BASE STEP holding the tables as they stand. A database that recorded step ids this build
/// does not hold is refused as "written by a newer build", and the answer is to recreate the
/// volume. A change is a NEW step after the ones below, and every shipped id is permanent.
///
/// No pragma appears below, and that is not an omission. busy_timeout and foreign_keys are
/// per-CONNECTION: the migrator sets them on its own connection, and every store sets both again on
/// every connection it opens (Pooling=false makes each Open() a brand-new connection). foreign_keys
/// is what makes the ON DELETE CASCADEs below delete anything at all.
/// </summary>
public static class AuthSchema
{
    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "auth-001",
            """
            -- A person. `current_team` is per person, not per credential: the browser and the
            -- Concierge are two principals for one human.
            CREATE TABLE users (
                id            TEXT PRIMARY KEY,
                email         TEXT NOT NULL UNIQUE,   -- stored normalised: trimmed, lowercased
                password_hash TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                current_team  TEXT NULL
            );

            -- A machine principal: a container, a Concierge session, or an API key.
            --
            -- owner_user_id NULL means authority of its own - a container, bound to `team`. SET
            -- means the credential ACTS AS that user, and `team` is only where it was born. The
            -- cascade is what revokes a person's keys when the person is deleted.
            CREATE TABLE principals (
                id            TEXT PRIMARY KEY,
                kind          TEXT NOT NULL,
                team          TEXT NULL,
                hash          TEXT NOT NULL,
                prefix        TEXT NOT NULL,
                permits       TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                last_used_at  TEXT NULL,
                owner_user_id TEXT NULL REFERENCES users(id) ON DELETE CASCADE,
                label         TEXT NULL
            );

            CREATE UNIQUE INDEX ix_principals_hash ON principals(hash);

            -- A team. `name` NULL means NEVER RENAMED. `root` NULL means the instance root.
            -- `member_agents`, `repos` and `env` are JSON. `budget_tokens` NULL means the team
            -- chose nothing and inherits the instance limit; 0 means it chose unlimited.
            CREATE TABLE teams (
                id            TEXT    PRIMARY KEY COLLATE NOCASE,
                name          TEXT    NULL,
                member_agent  TEXT    NULL,
                member_agents TEXT    NULL,
                member_prompt TEXT    NULL,
                root          TEXT    NULL,
                repos         TEXT    NULL,
                env           TEXT    NULL,
                paused        INTEGER NOT NULL DEFAULT 0,
                budget_tokens INTEGER NULL,
                created_utc   TEXT    NOT NULL
            );

            -- A container, as the thing that recreates it needs to know it. `system_prompt` holds
            -- what a person TYPED, never the composed prompt. `floor_seq` is what makes a restored
            -- container the SAME container - see ContainerHost.RestoreAsync.
            CREATE TABLE team_members (
                team          TEXT    NOT NULL COLLATE NOCASE REFERENCES teams(id) ON DELETE CASCADE,
                name          TEXT    NOT NULL COLLATE NOCASE,
                label         TEXT    NULL,
                agent         TEXT    NOT NULL,
                system_prompt TEXT    NULL,
                prompt        TEXT    NULL,
                subscribes    TEXT    NOT NULL,
                permits       TEXT    NOT NULL,
                floor_seq     INTEGER NOT NULL,
                hired_for     TEXT    NULL,
                created_utc   TEXT    NOT NULL,
                PRIMARY KEY (team, name)
            );

            -- The tenant-wide Concierge launch settings. One row, always present.
            CREATE TABLE tenant_interactive_agent_settings (
                id                      INTEGER PRIMARY KEY CHECK (id = 1),
                interactive_agent       TEXT NOT NULL,
                interactive_prompt      TEXT NULL,
                interactive_prompt_name TEXT NULL
            );

            INSERT INTO tenant_interactive_agent_settings
                (id, interactive_agent, interactive_prompt, interactive_prompt_name)
            VALUES (1, 'claude', NULL, NULL);

            -- The tenant log: who did what, administratively. It OUTLIVES its subjects on purpose,
            -- so there are no foreign keys and actor_email and subject_name are denormalised copies.
            -- `detail` is JSON and NEVER a credential, a password hash or an Agent's env.
            CREATE TABLE tenant_events (
                seq          INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at  TEXT    NOT NULL,
                actor_id     TEXT    NULL,
                actor_email  TEXT    NULL,
                action       TEXT    NOT NULL,
                subject      TEXT    NULL,
                subject_name TEXT    NULL,
                detail       TEXT    NULL
            );

            CREATE INDEX ix_tenant_events_seq ON tenant_events (seq DESC);

            CREATE INDEX ix_tenant_events_action_subject_seq
                ON tenant_events (action, subject, seq DESC);

            -- A trigger wakes a container on a clock (cron / every / once) or on an event type.
            -- `kind` says which arm is in use; `filter` is evaluated BEFORE a wake is enqueued.
            CREATE TABLE triggers (
                id               TEXT    PRIMARY KEY,
                team             TEXT    NOT NULL COLLATE NOCASE REFERENCES teams(id) ON DELETE CASCADE,
                container        TEXT    NOT NULL COLLATE NOCASE,
                name             TEXT    NOT NULL,
                instruction      TEXT    NOT NULL,
                kind             TEXT    NOT NULL,
                expression       TEXT    NULL,
                timezone         TEXT    NULL,
                interval_seconds INTEGER NULL,
                fire_at          TEXT    NULL,
                event_type       TEXT    NULL,
                filter           TEXT    NULL,
                idle_only        INTEGER NOT NULL,
                enabled          INTEGER NOT NULL,
                next_due_at      TEXT    NULL,
                last_fired_at    TEXT    NULL,
                last_outcome     TEXT    NULL,
                last_seq         INTEGER NULL,
                missed_count     INTEGER NOT NULL,
                created_at       TEXT    NOT NULL,
                created_by       TEXT    NOT NULL
            );

            CREATE INDEX ix_triggers_enabled_next_due_at ON triggers(enabled, next_due_at);

            -- The diagnostics log. No foreign keys, for the reason tenant_events has none, and no
            -- correlation or causation: that is the message log's job. Two indexes and no more,
            -- because this is the highest-volume table in the file.
            CREATE TABLE diagnostic_events (
                seq            INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at    TEXT    NOT NULL,   -- round-trip ("O"), like every other timestamp
                severity       TEXT    NOT NULL,   -- DiagnosticSeverity, lowercased
                kind           TEXT    NOT NULL,   -- a DiagnosticKinds constant
                source         TEXT    NULL,       -- a DiagnosticSources constant
                route          TEXT    NULL,       -- the route TEMPLATE, never a raw URL
                status         INTEGER NULL,
                exception_type TEXT    NULL,
                message        TEXT    NULL,       -- redacted at the write
                detail         TEXT    NULL        -- redacted at the write
            );

            CREATE INDEX idx_diagnostic_events_occurred_at ON diagnostic_events (occurred_at);

            CREATE INDEX idx_diagnostic_events_kind ON diagnostic_events (kind);
            """),

        // Instance-wide settings a person changes from the Tenant Settings dialog. A name
        // with no row falls back to appsettings.json, then to the built-in default - so an empty
        // table is an instance nobody has configured, not a broken one. Every write also appends a
        // `tenant_events` row naming the setting, the old value, the new value and the person.
        new MigrationStep(
            "auth-002",
            """
            CREATE TABLE tenant_settings (
                name       TEXT PRIMARY KEY,
                value      TEXT NOT NULL,
                updated_at TEXT NOT NULL,   -- round-trip ("O")
                updated_by TEXT NOT NULL    -- the person's email, denormalised like tenant_events
            );
            """),

        // The Concierge's agent is NULL until a person chooses one - NULL means nobody has
        // chosen (AGENTS.md, Agents) - so ConciergeAgentDefault picks an installed, signed-in
        // preset at launch. auth-001 declares the column NOT NULL with a `claude` seed, and SQLite
        // cannot drop NOT NULL in place, so this step rebuilds the table.
        //
        // An existing `claude` is the seed, not a choice, unless the tenant log holds a
        // `team.concierge-changed` row: every person's PUT /api/concierge writes one, the seed
        // does not. Anything else stored is kept as it is.
        new MigrationStep(
            "auth-003",
            """
            CREATE TABLE tenant_interactive_agent_settings_new (
                id                      INTEGER PRIMARY KEY CHECK (id = 1),
                interactive_agent       TEXT NULL,
                interactive_prompt      TEXT NULL,
                interactive_prompt_name TEXT NULL
            );

            INSERT INTO tenant_interactive_agent_settings_new
                (id, interactive_agent, interactive_prompt, interactive_prompt_name)
            SELECT
                id,
                CASE
                    WHEN interactive_agent = 'claude'
                        AND NOT EXISTS (
                            SELECT 1 FROM tenant_events WHERE action = 'team.concierge-changed')
                    THEN NULL
                    ELSE interactive_agent
                END,
                interactive_prompt,
                interactive_prompt_name
            FROM tenant_interactive_agent_settings;

            DROP TABLE tenant_interactive_agent_settings;

            ALTER TABLE tenant_interactive_agent_settings_new
                RENAME TO tenant_interactive_agent_settings;
            """),

        // The prompt is chosen by role and compiled into the host, so `teams.member_prompt`,
        // `team_members.prompt` and the Concierge's `interactive_prompt` / `interactive_prompt_name`
        // are not read or written. They are left in place rather than dropped: nothing reads
        // them, and a column nothing touches costs nothing. What a person may still say about a
        // team is its additional instructions, appended after the role prompt.
        new MigrationStep(
            "auth-004",
            """
            ALTER TABLE teams ADD COLUMN additional_instructions TEXT NULL;
            """),

        // The folder-change trigger. Every column is NULL for the other four kinds.
        // `last_listing` and the two `pending_*` columns are the watch's own memory between polls
        // and never reach the API: the listing the fingerprint was taken from (so a change can
        // name which files moved) and a change still inside its quiet period.
        new MigrationStep(
            "auth-005",
            """
            ALTER TABLE triggers ADD COLUMN watch_root           TEXT    NULL;
            ALTER TABLE triggers ADD COLUMN watch_path           TEXT    NULL;
            ALTER TABLE triggers ADD COLUMN watch_glob           TEXT    NULL;
            ALTER TABLE triggers ADD COLUMN poll_seconds         INTEGER NULL;
            ALTER TABLE triggers ADD COLUMN quiet_seconds        INTEGER NULL;
            ALTER TABLE triggers ADD COLUMN min_interval_seconds INTEGER NULL;
            ALTER TABLE triggers ADD COLUMN last_fingerprint     TEXT    NULL;
            ALTER TABLE triggers ADD COLUMN last_listing         TEXT    NULL;
            ALTER TABLE triggers ADD COLUMN pending_fingerprint  TEXT    NULL;
            ALTER TABLE triggers ADD COLUMN pending_since        TEXT    NULL;
            ALTER TABLE triggers ADD COLUMN last_poll_at         TEXT    NULL;
            ALTER TABLE triggers ADD COLUMN last_poll_ms         INTEGER NULL;
            ALTER TABLE triggers ADD COLUMN last_poll_entries    INTEGER NULL;
            ALTER TABLE triggers ADD COLUMN last_poll_error      TEXT    NULL;
            ALTER TABLE triggers ADD COLUMN last_change_at       TEXT    NULL;
            """),

        // Each team repository's default branch. `teams.repos` is a JSON list of URLs, so
        // the branch lives beside it keyed by the derived repository name. `from_remote` is what
        // origin's HEAD named on the last clone or successful Fetch; `set_by_person` is a person's
        // choice in Team settings and wins until cleared. No row, or NULL in both, is not known -
        // nothing is backfilled, and `main` is never assumed.
        new MigrationStep(
            "auth-006",
            """
            CREATE TABLE team_repo_default_branches (
                team          TEXT NOT NULL COLLATE NOCASE REFERENCES teams(id) ON DELETE CASCADE,
                repo          TEXT NOT NULL COLLATE NOCASE,
                from_remote   TEXT NULL,
                set_by_person TEXT NULL,
                PRIMARY KEY (team, repo)
            );
            """),

        // Contributor mode, per team repository. A row with an `upstream_url` is a
        // repository the team contributes to: `origin` is the fork, `upstream` the original.
        // `fork_owner` is the account the fork belongs to. `dco_sign_off` installs the commit-msg
        // hook; `cla_signed_note` is a person's record, never a signature. The `pr_*` columns are
        // the one pull request recorded for the repository, stored here and written by nothing
        // that asks GitHub. No row is an owned repository.
        new MigrationStep(
            "auth-007",
            """
            CREATE TABLE team_repo_contributors (
                team            TEXT    NOT NULL COLLATE NOCASE REFERENCES teams(id) ON DELETE CASCADE,
                repo            TEXT    NOT NULL COLLATE NOCASE,
                upstream_url    TEXT    NULL,
                fork_owner      TEXT    NULL,
                dco_sign_off    INTEGER NOT NULL DEFAULT 0,
                cla_signed_note TEXT    NULL,
                pr_url          TEXT    NULL,
                pr_number       INTEGER NULL,
                pr_state        TEXT    NULL,
                pr_read_at      TEXT    NULL,
                PRIMARY KEY (team, repo)
            );
            """),

        // A PLUGIN member's own settings, beside its `team_members` row and dying with it.
        // `config_json` holds ordinary configuration, checked against the plugin's manifest at
        // hire. `secrets_json` maps each secret the manifest names to a LOGICAL KEY - never a value:
        // the value is resolved from the Host at each run. An agent member has no row.
        new MigrationStep(
            "auth-008",
            """
            CREATE TABLE team_member_config (
                team         TEXT NOT NULL COLLATE NOCASE,
                name         TEXT NOT NULL COLLATE NOCASE,
                config_json  TEXT NOT NULL DEFAULT '{}',
                secrets_json TEXT NOT NULL DEFAULT '{}',
                PRIMARY KEY (team, name),
                FOREIGN KEY (team, name) REFERENCES team_members(team, name) ON DELETE CASCADE
            );
            """),

        // WHO LAST SET A MEMBER'S OWN INSTRUCTIONS (`system_prompt`), and when. Written at hire
        // (the hiring principal) and by every edit that changes the text, a clear included.
        // `system_prompt_set_by` is a Manager's member id or a person's email;
        // `system_prompt_set_by_kind` says which (`manager` or `person`); `system_prompt_set_at` is
        // ISO-8601 UTC. All NULL for a member from before this step until it is next edited -
        // nothing is backfilled, because nobody knows who wrote those.
        new MigrationStep(
            "auth-009",
            """
            ALTER TABLE team_members ADD COLUMN system_prompt_set_by      TEXT NULL;
            ALTER TABLE team_members ADD COLUMN system_prompt_set_by_kind TEXT NULL;
            ALTER TABLE team_members ADD COLUMN system_prompt_set_at      TEXT NULL;
            """),

        // WHAT A TRIGGER'S RUNS COST, AND WHOM THEY WAKE. `wake_manager` is what a run the trigger
        // started does to the Manager when it ends: `always`, `onHandbackOrFailure` or `never`.
        // Every row from before this step keeps `always` - today's behaviour - through the column
        // default; a new trigger is given `onHandbackOrFailure` by the create route, not here.
        // `daily_token_cap` is billable tokens per day in the trigger's timezone, NULL for none.
        new MigrationStep(
            "auth-010",
            """
            ALTER TABLE triggers ADD COLUMN wake_manager    TEXT    NOT NULL DEFAULT 'always';
            ALTER TABLE triggers ADD COLUMN daily_token_cap INTEGER NULL;
            """),

        // HOW OFTEN THE DAILY CAP SKIPPED A TRIGGER TODAY. Only the first skip of a day writes a
        // `schedule.skipped` row and a tenant row; the rest are counted here. `capped_skips_day` is
        // the start of the day counted (ISO-8601 UTC, the instant midnight falls in the trigger's
        // timezone), and a skip on another day starts again from one. NULL and 0 for a row from
        // before this step: it has skipped nothing yet today.
        new MigrationStep(
            "auth-011",
            """
            ALTER TABLE triggers ADD COLUMN capped_skips_day TEXT    NULL;
            ALTER TABLE triggers ADD COLUMN capped_skips     INTEGER NOT NULL DEFAULT 0;
            """),

        // A FOLDER WHOSE REMOVAL DID NOT FINISH: a deleted team's root, a deleted member's
        // workspace, or a folder a reset emptied. `path` is absolute. `kind` is `team-root`,
        // `workspace` or `emptied`; `member` is set for a workspace only. `remaining` is a JSON list
        // of the paths still on disk after the last attempt. No foreign key: the team is usually
        // gone, which is when this row matters. The Host retries every row at start.
        new MigrationStep(
            "auth-012",
            """
            CREATE TABLE unfinished_removals (
                path        TEXT    PRIMARY KEY,
                kind        TEXT    NOT NULL,
                team        TEXT    NOT NULL,
                member      TEXT    NULL,
                remaining   TEXT    NOT NULL DEFAULT '[]',
                recorded_at TEXT    NOT NULL,
                attempts    INTEGER NOT NULL DEFAULT 1
            );
            """),

        // WHAT THE HOST'S OWN LAST ATTEMPT LEFT: for a folder a reset emptied, a JSON object of
        // directory path to the modification time (UTC ticks) the attempt's deletes left on it. A
        // retry that needed more than one attempt judges a directory above a named path by this as
        // well as by `recorded_at`, so its own earlier deletes do not make it look written since.
        // NULL for a row from before the step and for the other kinds.
        new MigrationStep(
            "auth-013",
            """
            ALTER TABLE unfinished_removals ADD COLUMN host_left TEXT NULL;
            """),

        // CONNECTIONS: a person's account at an OAuth provider, held by the Host so a plugin gets
        // only a fresh access token per run. Every `*_protected` column is Data Protection
        // ciphertext (the instance's `<dataRoot>/keys`), never plain text.
        //
        // `oauth_providers`: one row per configured provider - `google`, `microsoft` (built in; a
        // row only once a person sets its client) or `custom-<id>`. URLs are NULL for a built-in,
        // whose endpoints are the build's.
        //
        // `connections`: one row per connected account. `status` is `ok` or `needs-reconnect`, with
        // the provider's reason. The access token is cached until it is within 5 minutes of expiry.
        //
        // `connection_flows`: a `start` waiting for its `callback`/`complete`. `state` is issued to
        // `user_id`, single use (`used_at`), and refused 10 minutes after `issued_at`. The PKCE
        // verifier stays here, protected; no client ever sees it.
        //
        // `team_member_config.connections_json`: a plugin member's slot -> connection id. Never a
        // token.
        new MigrationStep(
            "auth-014",
            """
            CREATE TABLE oauth_providers (
                id                      TEXT PRIMARY KEY,
                kind                    TEXT NOT NULL,
                name                    TEXT NULL,
                client_id               TEXT NULL,
                client_secret_protected TEXT NULL,
                tenant                  TEXT NULL,
                authorize_url           TEXT NULL,
                token_url               TEXT NULL,
                userinfo_url            TEXT NULL,
                default_scopes          TEXT NOT NULL DEFAULT '[]',
                updated_at              TEXT NOT NULL
            );

            CREATE TABLE connections (
                id                      TEXT PRIMARY KEY,
                name                    TEXT NOT NULL,
                provider                TEXT NOT NULL,
                account                 TEXT NOT NULL,
                scopes                  TEXT NOT NULL DEFAULT '[]',
                refresh_token_protected TEXT NULL,
                access_token_protected  TEXT NULL,
                access_expires_at       TEXT NULL,
                connected_at            TEXT NOT NULL,
                refreshed_at            TEXT NULL,
                status                  TEXT NOT NULL DEFAULT 'ok',
                status_reason           TEXT NULL,
                connected_by            TEXT NULL
            );

            CREATE TABLE connection_flows (
                state             TEXT PRIMARY KEY,
                user_id           TEXT NOT NULL,
                provider          TEXT NOT NULL,
                scopes            TEXT NOT NULL DEFAULT '[]',
                name              TEXT NULL,
                reconnect_id      TEXT NULL,
                redirect_uri      TEXT NOT NULL,
                loopback          INTEGER NOT NULL DEFAULT 0,
                verifier_protected TEXT NOT NULL,
                issued_at         TEXT NOT NULL,
                used_at           TEXT NULL
            );

            ALTER TABLE team_member_config ADD COLUMN connections_json TEXT NOT NULL DEFAULT '{}';
            """),

        // A TEAM'S SITES: static files the Host serves and backs. `sites` is one row per site,
        // unique by name within its team; `live_version` NULL is an unpublished site, and
        // `next_version` is never reused, so a folder `v<n>` always means one copy. `site_versions`
        // is one row per copy kept on disk. `site_documents` is the site's data: named collections
        // of JSON documents, with `bytes` stored so the per-site limit is a SUM rather than a
        // re-measure. No foreign keys to `teams`: a team deletion removes its sites itself, and the
        // rows are keyed by the team's stored spelling.
        new MigrationStep(
            "auth-015",
            """
            CREATE TABLE sites (
                team         TEXT    NOT NULL COLLATE NOCASE,
                name         TEXT    NOT NULL,
                live_version INTEGER NULL,
                next_version INTEGER NOT NULL DEFAULT 1,
                created_at   TEXT    NOT NULL,
                created_by   TEXT    NOT NULL,
                PRIMARY KEY (team, name)
            );

            CREATE TABLE site_versions (
                team         TEXT    NOT NULL COLLATE NOCASE,
                site         TEXT    NOT NULL,
                version      INTEGER NOT NULL,
                published_at TEXT    NOT NULL,
                published_by TEXT    NOT NULL,
                source       TEXT    NOT NULL,
                files        INTEGER NOT NULL,
                bytes        INTEGER NOT NULL,
                PRIMARY KEY (team, site, version)
            );

            CREATE TABLE site_documents (
                team       TEXT    NOT NULL COLLATE NOCASE,
                site       TEXT    NOT NULL,
                collection TEXT    NOT NULL,
                id         TEXT    NOT NULL,
                doc        TEXT    NOT NULL,
                bytes      INTEGER NOT NULL,
                updated_at TEXT    NOT NULL,
                updated_by TEXT    NOT NULL,
                PRIMARY KEY (team, site, collection, id)
            );
            """),

        // WHICH SOLUTION PACKAGE A TEAM CAME FROM: one row per team installed from a
        // package, keyed by the team's stored id. `manifest` is the package's `solution.json` as
        // installed, and `digests` the SHA-256 of each skill, site and tool file it installed, so an
        // update can show what changed without the earlier folder. `members` maps each package
        // member name to the member it became, and `triggers` each trigger name to its row id, so an
        // update changes those and nothing a person made by hand. `plugins` maps each of the package's
        // plugin ids to the version it shipped. No foreign key to `teams`: a team deletion removes its row itself.
        new MigrationStep(
            "auth-016",
            """
            CREATE TABLE team_solutions (
                team         TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                package_id   TEXT NOT NULL,
                name         TEXT NOT NULL,
                version      TEXT NOT NULL,
                folder       TEXT NOT NULL,
                installed_at TEXT NOT NULL,
                installed_by TEXT NULL,
                updated_at   TEXT NULL,
                manifest     TEXT NOT NULL,
                digests      TEXT NOT NULL DEFAULT '{}',
                members      TEXT NOT NULL DEFAULT '{}',
                triggers     TEXT NOT NULL DEFAULT '{}',
                plugins      TEXT NOT NULL DEFAULT '{}'
            );
            """),

        // THE OUTCOME A TRIGGER'S FIRES SERVE. NULL for none, and for every trigger from before the
        // step. A fire of a trigger that names one links the workflow it roots to it, attributed to
        // the person who configured the trigger. No foreign key: `outcomes` is the outcome module's.
        new MigrationStep(
            "auth-017",
            """
            ALTER TABLE triggers ADD COLUMN outcome_id TEXT NULL;
            """),

        // THE EMAIL OF THE PERSON WHO LAST CONFIGURED A TRIGGER, snapshotted when a person creates or
        // changes it, so a fire's outcome link names that person (`set_by`) even after their user is
        // deleted. NULL for every trigger from before the step, which resolves `created_by` through
        // `users` as before.
        new MigrationStep(
            "auth-018",
            """
            ALTER TABLE triggers ADD COLUMN configured_by_email TEXT NULL;
            """),

        // A CREDENTIAL ISSUED TO AN AGENT CLI in Admin > Agents, one row per command (`claude`,
        // `codex`, ...), used by every preset launching that command whose source is `issued`.
        // `value_protected` is Data Protection ciphertext under the instance's `<dataRoot>/keys`;
        // no column holds the value or any part of it. Beside `tenant_events`, so every set,
        // replace and clear lands with its row in one transaction or not at all.
        new MigrationStep(
            "auth-019",
            """
            CREATE TABLE agent_credentials (
                command         TEXT PRIMARY KEY COLLATE NOCASE,
                kind            TEXT NOT NULL,
                value_protected TEXT NOT NULL,
                set_by          TEXT NOT NULL,
                set_at          TEXT NOT NULL
            );
            """),

        // A MAILBOX CONNECTION: an account at a mail server reached with an app password over IMAP
        // and SMTP. Its `connections` row has provider `imap`, no scopes and no tokens; this row
        // holds the servers, the username, the preset it was made from (NULL for none) and the app
        // password as Data Protection ciphertext under the instance's `<dataRoot>/keys`. No column
        // holds the password or any part of it. Removed with its connection, in the same
        // transaction.
        new MigrationStep(
            "auth-020",
            """
            CREATE TABLE imap_connections (
                id                 TEXT PRIMARY KEY,
                username           TEXT NOT NULL,
                imap_host          TEXT NOT NULL,
                imap_port          INTEGER NOT NULL,
                imap_security      TEXT NOT NULL,
                smtp_host          TEXT NOT NULL,
                smtp_port          INTEGER NOT NULL,
                smtp_security      TEXT NOT NULL,
                preset             TEXT NULL,
                password_protected TEXT NOT NULL
            );
            """),
    ];
}
