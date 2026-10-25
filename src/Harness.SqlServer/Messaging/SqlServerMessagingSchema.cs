using Harness.Contracts;

namespace Harness.SqlServer.Messaging;

/// <summary>
/// THE MESSAGE LOG'S TABLES ON SQL SERVER, as one baseline step: the tables of
/// <c>MessageSchema.Steps</c> (<c>messages-001</c> to <c>messages-006</c>) as they stand after the last
/// of them, not a replay of each. No SQL Server database exists from before this step, so there is
/// nothing to adopt or alter. A SQLite step written after this one gets a SQL Server step with the
/// same id; this id is permanent.
///
/// <para>
/// NAMES AND THEIR CASE ARE SQLITE'S EXACTLY. Under the required binary collation identifiers are
/// case-sensitive, so every store addresses these tables and columns in exactly these spellings, and
/// <c>SchemaParityTests.Tables_match</c> holds the columns to SQLite's.
/// </para>
///
/// <para>
/// TYPES, by the plan's rules: INTEGER is BIGINT and the AUTOINCREMENT keys IDENTITY; instants stay
/// round-trip text in NVARCHAR(40), so ordering and parsing are SQLite's; text that is keyed or indexed
/// is NVARCHAR(450) or narrower (an index key is at most 900 bytes clustered, 1,700 otherwise), and
/// other text NVARCHAR(MAX). A column SQLite declares <c>COLLATE NOCASE</c> takes
/// <see cref="SqlServerSql.NoCase"/>, so its key and its index compare without case as SQLite's do.
/// </para>
///
/// <para>
/// <c>team</c> is SQLite's generated column (<c>messages-006</c>) as a computed one: the source up to
/// its first <c>/</c>, case-insensitive, indexed for the team-scoped reads. <c>causation_seq</c> keeps
/// its foreign key, which the SQLite stores enforce (<c>SqlitePragmas</c> turns foreign keys on for
/// every connection).
/// </para>
/// </summary>
public static class SqlServerMessagingSchema
{
    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "messages.sqlserver-baseline-1",
            $"""
            CREATE TABLE messages (
                seq            BIGINT        IDENTITY(1,1) NOT NULL CONSTRAINT pk_messages PRIMARY KEY,
                type           NVARCHAR(450) NOT NULL,
                payload        NVARCHAR(MAX) NOT NULL,
                source         NVARCHAR(450) NOT NULL,
                correlation_id BIGINT        NOT NULL CONSTRAINT df_messages_correlation_id DEFAULT 0,
                causation_seq  BIGINT        NULL CONSTRAINT fk_messages_causation_seq REFERENCES messages(seq),
                depth          BIGINT        NOT NULL,
                occurred_at    NVARCHAR(40)  NOT NULL,
                team AS (CASE WHEN CHARINDEX(N'/', source) > 0
                              THEN SUBSTRING(source, 1, CHARINDEX(N'/', source) - 1) END COLLATE {SqlServerSql.NoCase})
            );

            CREATE INDEX ix_messages_seq_type ON messages(seq, type);
            CREATE INDEX ix_messages_correlation ON messages(correlation_id, seq);
            CREATE INDEX ix_messages_causation ON messages(causation_seq);
            CREATE INDEX ix_messages_team_seq ON messages(team, seq);
            CREATE INDEX ix_messages_correlation_type ON messages(correlation_id, type, seq);

            CREATE TABLE cursors (
                subscriber NVARCHAR(200) NOT NULL CONSTRAINT pk_cursors PRIMARY KEY,
                position   BIGINT        NOT NULL
            );

            CREATE TABLE subscriptions (
                subscriber NVARCHAR(200) NOT NULL,
                type       NVARCHAR(250) NOT NULL,
                CONSTRAINT pk_subscriptions PRIMARY KEY (subscriber, type)
            );

            CREATE TABLE pending_deliveries (
                subscriber        NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                seq               BIGINT NOT NULL,
                started           BIGINT NOT NULL CONSTRAINT df_pending_deliveries_started DEFAULT 0,
                deferred_from_run BIGINT NULL,
                CONSTRAINT pk_pending_deliveries PRIMARY KEY (subscriber, seq)
            );

            CREATE TABLE wakes (
                id        BIGINT        IDENTITY(1,1) NOT NULL CONSTRAINT pk_wakes PRIMARY KEY,
                team      NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                member    NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                workflow  BIGINT        NOT NULL,
                asked_seq BIGINT        NOT NULL,
                asked_at  NVARCHAR(40)  NOT NULL,
                due_at    NVARCHAR(40)  NOT NULL,
                note      NVARCHAR(MAX) NOT NULL
            );

            CREATE INDEX ix_wakes_due ON wakes(due_at);
            CREATE INDEX ix_wakes_member ON wakes(team, member);
            CREATE INDEX ix_wakes_workflow ON wakes(workflow);

            CREATE TABLE file_changes (
                id           BIGINT        IDENTITY(1,1) NOT NULL CONSTRAINT pk_file_changes PRIMARY KEY,
                team         NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                member       NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                repo         NVARCHAR(MAX) NOT NULL,
                at           NVARCHAR(40)  NOT NULL,
                correlation  BIGINT        NULL,
                files_json   NVARCHAR(MAX) NOT NULL,
                commits_json NVARCHAR(MAX) NOT NULL,
                more         BIGINT        NOT NULL
            );

            CREATE INDEX ix_file_changes_at ON file_changes(at, id);
            CREATE INDEX ix_file_changes_member ON file_changes(team, member);

            CREATE TABLE run_steps (
                id       BIGINT        IDENTITY(1,1) NOT NULL CONSTRAINT pk_run_steps PRIMARY KEY,
                team     NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                member   NVARCHAR(200) COLLATE {SqlServerSql.NoCase} NOT NULL,
                delivery BIGINT        NOT NULL,
                workflow BIGINT        NULL,
                at       NVARCHAR(40)  NOT NULL,
                kind     NVARCHAR(MAX) NOT NULL,
                label    NVARCHAR(MAX) NOT NULL,
                mcp_tool NVARCHAR(MAX) NULL,
                count    BIGINT        NOT NULL
            );

            CREATE INDEX ix_run_steps_at ON run_steps(at, id);
            CREATE INDEX ix_run_steps_member ON run_steps(team, member);
            """),
    ];
}
