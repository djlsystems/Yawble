using Harness.Contracts;

namespace Harness.SqlServer.Identity;

/// <summary>
/// The identity module's accounts, teams, tenant log and tenant settings on SQL Server, as SQLite's
/// <c>AuthSchema</c> leaves them after <c>auth-029</c>, in one baseline step. That class's remarks
/// are the argument for each table and column; this one says only where the dialect makes a
/// difference.
///
/// <para>
/// THE ID IS THE SQLITE MODULE'S OWN PREFIX (<c>auth-</c>), so a later SQLite step and its SQL Server
/// twin share one id. This step holds the tables the accounts, principals, teams, tenant log, tenant
/// settings and unfinished-removal stores use. The module's other tables (triggers, sites, solution
/// rows, diagnostics, connections and the plugin rows) belong to the stores that use them and land in
/// steps of their own.
/// </para>
///
/// <para>
/// Types follow the plan's rules: BIGINT for INTEGER, IDENTITY for an AUTOINCREMENT key,
/// NVARCHAR(40) for an instant, NVARCHAR(200) for a team or member id, at most NVARCHAR(450) for
/// other keyed text, NVARCHAR(MAX) for the rest. Every column SQLite declares <c>COLLATE NOCASE</c>
/// takes <see cref="SqlServerSql.NoCase"/>, so its key and its comparisons fold case as SQLite's do;
/// every other column compares by code point, as SQLite's BINARY does. So <c>teams.id</c> is
/// case-insensitive and <c>teams.name</c> is not, and <c>users.email</c> is exact: the store
/// lower-cases an address before it is written or looked up, so the unique key sees one spelling.
/// </para>
///
/// <para>
/// <c>tenant_events.subject</c> IS NOT A KEY COLUMN HERE. A subject can be a document path of any
/// length, and an index key is capped at 1,700 bytes, so SQLite's <c>(action, subject, seq DESC)</c>
/// index is <c>(action, seq DESC)</c> here: a lookup by action and subject reads that action's rows
/// newest first. The fresh volume's Concierge row is seeded as SQLite's <c>auth-003</c> leaves it,
/// with no agent chosen.
/// </para>
/// </summary>
public static class SqlServerAuthSchema
{
    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "auth.sqlserver-baseline-1",
            $"""
             CREATE TABLE users (
                 id            NVARCHAR(450) NOT NULL PRIMARY KEY,
                 email         NVARCHAR(450) NOT NULL CONSTRAINT ux_users_email UNIQUE,
                 password_hash NVARCHAR(MAX) NOT NULL,
                 created_at    NVARCHAR(40)  NOT NULL,
                 current_team  NVARCHAR(MAX) NULL,
                 appearance    NVARCHAR(MAX) NULL
             );

             CREATE TABLE principals (
                 id            NVARCHAR(450) NOT NULL PRIMARY KEY,
                 kind          NVARCHAR(MAX) NOT NULL,
                 team          NVARCHAR(MAX) NULL,
                 hash          NVARCHAR(450) NOT NULL,
                 prefix        NVARCHAR(MAX) NOT NULL,
                 permits       NVARCHAR(MAX) NOT NULL,
                 created_at    NVARCHAR(40)  NOT NULL,
                 last_used_at  NVARCHAR(40)  NULL,
                 owner_user_id NVARCHAR(450) NULL REFERENCES users(id) ON DELETE CASCADE,
                 label         NVARCHAR(MAX) NULL
             );

             CREATE UNIQUE INDEX ix_principals_hash ON principals(hash);

             CREATE TABLE teams (
                 id                      NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL PRIMARY KEY,
                 name                    NVARCHAR(MAX) NULL,
                 member_agent            NVARCHAR(MAX) NULL,
                 member_agents           NVARCHAR(MAX) NULL,
                 member_prompt           NVARCHAR(MAX) NULL,
                 root                    NVARCHAR(MAX) NULL,
                 repos                   NVARCHAR(MAX) NULL,
                 env                     NVARCHAR(MAX) NULL,
                 paused                  BIGINT        NOT NULL DEFAULT 0,
                 budget_tokens           BIGINT        NULL,
                 created_utc             NVARCHAR(40)  NOT NULL,
                 additional_instructions NVARCHAR(MAX) NULL,
                 archived_at             NVARCHAR(40)  NULL,
                 archived_by             NVARCHAR(MAX) NULL,
                 activity_from           NVARCHAR(40)  NULL,
                 activity_from_by        NVARCHAR(MAX) NULL
             );

             CREATE TABLE team_members (
                 team                      NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL
                                           REFERENCES teams(id) ON DELETE CASCADE,
                 name                      NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 label                     NVARCHAR(MAX) NULL,
                 agent                     NVARCHAR(MAX) NOT NULL,
                 system_prompt             NVARCHAR(MAX) NULL,
                 prompt                    NVARCHAR(MAX) NULL,
                 subscribes                NVARCHAR(MAX) NOT NULL,
                 permits                   NVARCHAR(MAX) NOT NULL,
                 floor_seq                 BIGINT        NOT NULL,
                 hired_for                 NVARCHAR(MAX) NULL,
                 created_utc               NVARCHAR(40)  NOT NULL,
                 system_prompt_set_by      NVARCHAR(MAX) NULL,
                 system_prompt_set_by_kind NVARCHAR(MAX) NULL,
                 system_prompt_set_at      NVARCHAR(40)  NULL,
                 PRIMARY KEY (team, name)
             );

             CREATE TABLE tenant_interactive_agent_settings (
                 id                      BIGINT        NOT NULL PRIMARY KEY CHECK (id = 1),
                 interactive_agent       NVARCHAR(MAX) NULL,
                 interactive_prompt      NVARCHAR(MAX) NULL,
                 interactive_prompt_name NVARCHAR(MAX) NULL
             );

             INSERT INTO tenant_interactive_agent_settings
                 (id, interactive_agent, interactive_prompt, interactive_prompt_name)
             VALUES (1, NULL, NULL, NULL);

             CREATE TABLE tenant_events (
                 seq          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                 occurred_at  NVARCHAR(40)  NOT NULL,
                 actor_id     NVARCHAR(MAX) NULL,
                 actor_email  NVARCHAR(MAX) NULL,
                 action       NVARCHAR(200) NOT NULL,
                 subject      NVARCHAR(MAX) NULL,
                 subject_name NVARCHAR(MAX) NULL,
                 detail       NVARCHAR(MAX) NULL
             );

             CREATE INDEX ix_tenant_events_seq ON tenant_events (seq DESC);

             CREATE INDEX ix_tenant_events_action_subject_seq ON tenant_events (action, seq DESC);

             CREATE TABLE tenant_settings (
                 name       NVARCHAR(450) NOT NULL PRIMARY KEY,
                 value      NVARCHAR(MAX) NOT NULL,
                 updated_at NVARCHAR(40)  NOT NULL,
                 updated_by NVARCHAR(MAX) NOT NULL
             );

             CREATE TABLE team_repo_default_branches (
                 team          NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL
                               REFERENCES teams(id) ON DELETE CASCADE,
                 repo          NVARCHAR(250) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 from_remote   NVARCHAR(MAX) NULL,
                 set_by_person NVARCHAR(MAX) NULL,
                 PRIMARY KEY (team, repo)
             );

             CREATE TABLE team_repo_contributors (
                 team            NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL
                                 REFERENCES teams(id) ON DELETE CASCADE,
                 repo            NVARCHAR(250) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 upstream_url    NVARCHAR(MAX) NULL,
                 fork_owner      NVARCHAR(MAX) NULL,
                 dco_sign_off    BIGINT        NOT NULL DEFAULT 0,
                 cla_signed_note NVARCHAR(MAX) NULL,
                 pr_url          NVARCHAR(MAX) NULL,
                 pr_number       BIGINT        NULL,
                 pr_state        NVARCHAR(MAX) NULL,
                 pr_read_at      NVARCHAR(40)  NULL,
                 PRIMARY KEY (team, repo)
             );

             CREATE TABLE unfinished_removals (
                 path        NVARCHAR(450) NOT NULL PRIMARY KEY,
                 kind        NVARCHAR(MAX) NOT NULL,
                 team        NVARCHAR(MAX) NOT NULL,
                 member      NVARCHAR(MAX) NULL,
                 remaining   NVARCHAR(MAX) NOT NULL DEFAULT N'[]',
                 recorded_at NVARCHAR(40)  NOT NULL,
                 attempts    BIGINT        NOT NULL DEFAULT 1,
                 host_left   NVARCHAR(MAX) NULL
             );
             """),
    ];
}
