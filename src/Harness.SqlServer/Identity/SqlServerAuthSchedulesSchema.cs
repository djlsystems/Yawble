using Harness.Contracts;

namespace Harness.SqlServer.Identity;

/// <summary>
/// The identity module's triggers, sites, solution rows and diagnostics log on SQL Server, as SQLite's
/// <c>AuthSchema</c> leaves them after <c>auth-029</c>, in the module's second baseline step. That
/// class's remarks are the argument for each table and column; this one says only where the dialect
/// makes a difference. The accounts, teams and tenant tables are <c>auth.sqlserver-baseline-1</c>'s
/// (<see cref="SqlServerAuthSchema"/>), which this step follows: <c>triggers.team</c> references
/// <c>teams</c>.
///
/// <para>
/// Types follow the first baseline's rules: BIGINT for INTEGER, IDENTITY for an AUTOINCREMENT key,
/// NVARCHAR(40) for an instant, NVARCHAR(MAX) for text no key or index holds, and every column SQLite
/// declares <c>COLLATE NOCASE</c> takes <see cref="SqlServerSql.NoCase"/>. Keyed text is as wide as
/// what bounds it, and no wider:
/// </para>
/// <list type="bullet">
///   <item><c>triggers.id</c> NVARCHAR(450): a GUID the create route mints. <c>triggers.team</c> is
///     NVARCHAR(200), the width of <c>teams.id</c> it references.</item>
///   <item>THE SITE TABLES' KEYS ARE THE SITE RULES' OWN BOUNDS: a site and a collection are slugs of
///     at most 63 characters and a document id at most 128 (<see cref="SiteRules"/>, checked before any
///     store write). A team is a container name of at most 32 characters, held in NVARCHAR(100) here:
///     a clustered key is capped at 900 bytes, and <c>site_documents</c>' four columns at
///     <c>teams.id</c>'s 200 would pass it.</item>
///   <item><c>team_solutions.team</c> NVARCHAR(200), a team's stored id.</item>
///   <item><c>diagnostic_events.kind</c> NVARCHAR(200): a <c>DiagnosticKinds</c> constant, the closed
///     vocabulary every writer takes its kind from.</item>
/// </list>
///
/// <para>
/// ONE INDEX SQLITE DOES NOT HAVE: <c>ix_triggers_team</c>. Deleting a team cascades to its triggers,
/// and without an index the cascade reads, and locks as it goes, every team's triggers to find the
/// few that are its own. SQLite runs one writer at a time and has nothing to lock.
/// </para>
/// </summary>
public static class SqlServerAuthSchedulesSchema
{
    /// <summary>A JSON column's default: the empty object, as SQLite's <c>DEFAULT '{}'</c>.</summary>
    private const string EmptyObject = "{}";

    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "auth.sqlserver-baseline-2",
            $"""
             CREATE TABLE triggers (
                 id                   NVARCHAR(450) NOT NULL PRIMARY KEY,
                 team                 NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL
                                      REFERENCES teams(id) ON DELETE CASCADE,
                 container            NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 name                 NVARCHAR(MAX) NOT NULL,
                 instruction          NVARCHAR(MAX) NOT NULL,
                 kind                 NVARCHAR(MAX) NOT NULL,
                 expression           NVARCHAR(MAX) NULL,
                 timezone             NVARCHAR(MAX) NULL,
                 interval_seconds     BIGINT        NULL,
                 fire_at              NVARCHAR(40)  NULL,
                 event_type           NVARCHAR(MAX) NULL,
                 filter               NVARCHAR(MAX) NULL,
                 idle_only            BIGINT        NOT NULL,
                 enabled              BIGINT        NOT NULL,
                 next_due_at          NVARCHAR(40)  NULL,
                 last_fired_at        NVARCHAR(40)  NULL,
                 last_outcome         NVARCHAR(MAX) NULL,
                 last_seq             BIGINT        NULL,
                 missed_count         BIGINT        NOT NULL,
                 created_at           NVARCHAR(40)  NOT NULL,
                 created_by           NVARCHAR(MAX) NOT NULL,
                 watch_root           NVARCHAR(MAX) NULL,
                 watch_path           NVARCHAR(MAX) NULL,
                 watch_glob           NVARCHAR(MAX) NULL,
                 poll_seconds         BIGINT        NULL,
                 quiet_seconds        BIGINT        NULL,
                 min_interval_seconds BIGINT        NULL,
                 last_fingerprint     NVARCHAR(MAX) NULL,
                 last_listing         NVARCHAR(MAX) NULL,
                 pending_fingerprint  NVARCHAR(MAX) NULL,
                 pending_since        NVARCHAR(40)  NULL,
                 last_poll_at         NVARCHAR(40)  NULL,
                 last_poll_ms         BIGINT        NULL,
                 last_poll_entries    BIGINT        NULL,
                 last_poll_error      NVARCHAR(MAX) NULL,
                 last_change_at       NVARCHAR(40)  NULL,
                 wake_manager         NVARCHAR(MAX) NOT NULL DEFAULT N'always',
                 daily_token_cap      BIGINT        NULL,
                 capped_skips_day     NVARCHAR(40)  NULL,
                 capped_skips         BIGINT        NOT NULL DEFAULT 0,
                 outcome_id           NVARCHAR(MAX) NULL,
                 configured_by_email  NVARCHAR(MAX) NULL
             );

             CREATE INDEX ix_triggers_enabled_next_due_at ON triggers (enabled, next_due_at);

             CREATE INDEX ix_triggers_team ON triggers (team);

             CREATE TABLE diagnostic_events (
                 seq            BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                 occurred_at    NVARCHAR(40)  NOT NULL,
                 severity       NVARCHAR(MAX) NOT NULL,
                 kind           NVARCHAR(200) NOT NULL,
                 source         NVARCHAR(MAX) NULL,
                 route          NVARCHAR(MAX) NULL,
                 status         BIGINT        NULL,
                 exception_type NVARCHAR(MAX) NULL,
                 message        NVARCHAR(MAX) NULL,
                 detail         NVARCHAR(MAX) NULL
             );

             CREATE INDEX idx_diagnostic_events_occurred_at ON diagnostic_events (occurred_at);

             CREATE INDEX idx_diagnostic_events_kind ON diagnostic_events (kind);

             CREATE TABLE sites (
                 team         NVARCHAR(100) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 name         NVARCHAR(63)  NOT NULL,
                 live_version BIGINT        NULL,
                 next_version BIGINT        NOT NULL DEFAULT 1,
                 created_at   NVARCHAR(40)  NOT NULL,
                 created_by   NVARCHAR(MAX) NOT NULL,
                 PRIMARY KEY (team, name)
             );

             CREATE TABLE site_versions (
                 team         NVARCHAR(100) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 site         NVARCHAR(63)  NOT NULL,
                 version      BIGINT        NOT NULL,
                 published_at NVARCHAR(40)  NOT NULL,
                 published_by NVARCHAR(MAX) NOT NULL,
                 source       NVARCHAR(MAX) NOT NULL,
                 files        BIGINT        NOT NULL,
                 bytes        BIGINT        NOT NULL,
                 PRIMARY KEY (team, site, version)
             );

             CREATE TABLE site_documents (
                 team       NVARCHAR(100) COLLATE {SqlServerSql.NoCase} NOT NULL,
                 site       NVARCHAR(63)  NOT NULL,
                 collection NVARCHAR(63)  NOT NULL,
                 id         NVARCHAR(128) NOT NULL,
                 doc        NVARCHAR(MAX) NOT NULL,
                 bytes      BIGINT        NOT NULL,
                 updated_at NVARCHAR(40)  NOT NULL,
                 updated_by NVARCHAR(MAX) NOT NULL,
                 PRIMARY KEY (team, site, collection, id)
             );

             CREATE TABLE team_solutions (
                 team            NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL PRIMARY KEY,
                 package_id      NVARCHAR(MAX) NOT NULL,
                 name            NVARCHAR(MAX) NOT NULL,
                 version         NVARCHAR(MAX) NOT NULL,
                 folder          NVARCHAR(MAX) NOT NULL,
                 installed_at    NVARCHAR(40)  NOT NULL,
                 installed_by    NVARCHAR(MAX) NULL,
                 updated_at      NVARCHAR(40)  NULL,
                 manifest        NVARCHAR(MAX) NOT NULL,
                 digests         NVARCHAR(MAX) NOT NULL DEFAULT N'{EmptyObject}',
                 members         NVARCHAR(MAX) NOT NULL DEFAULT N'{EmptyObject}',
                 triggers        NVARCHAR(MAX) NOT NULL DEFAULT N'{EmptyObject}',
                 plugins         NVARCHAR(MAX) NOT NULL DEFAULT N'{EmptyObject}',
                 uninstalled_at  NVARCHAR(40)  NULL,
                 answers         NVARCHAR(MAX) NULL,
                 plugin_minimums NVARCHAR(MAX) NOT NULL DEFAULT N'{EmptyObject}'
             );
             """),
    ];
}
