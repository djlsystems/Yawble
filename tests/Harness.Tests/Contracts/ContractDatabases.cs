using Harness.Host;
using Harness.Messaging;
using Harness.SqlServer;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace Harness.Tests.Contracts;

/// <summary>
/// The SQLite database a provider contract's SQLite half runs against: a temp <c>messages.db</c> with
/// every schema module applied, as the existing store tests build theirs, removed on dispose.
/// </summary>
internal sealed class SqliteContractDatabase : IAsyncDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"harness-contract-{Guid.NewGuid():N}");

    public SqliteContractDatabase()
    {
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "messages.db");
        new SchemaMigrator(Path).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
    }

    /// <summary>The database file.</summary>
    public string Path { get; }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The SQL Server database a provider contract's SQL Server half runs against: a
/// <see cref="SqlServerTestDatabase"/> (every SQL Server schema module applied, skipped without a
/// server), which has READ_COMMITTED_SNAPSHOT on, as Azure SQL Database runs by default and the
/// migrator requires.
///
/// <para>
/// ROW VERSIONING IS THE STRICTER SETTING FOR A STORE. Under it a reader never waits on a writer and
/// so reads straight past a row that is not committed yet; that is how a gap between allocated and
/// committed seqs becomes a reader skipping a row, and a store that is correct here is correct under
/// locking reads too.
/// </para>
/// </summary>
internal sealed class SqlServerContractDatabase : IAsyncDisposable
{
    private readonly SqlServerTestDatabase _database;

    private SqlServerContractDatabase(SqlServerTestDatabase database) => _database = database;

    public string ConnectionString => _database.ConnectionString;

    public static async Task<SqlServerContractDatabase> CreateAsync(CancellationToken ct)
    {
        var database = new SqlServerContractDatabase(await SqlServerTestDatabase.CreateAsync(ct));
        await database.EnsureOtherModulesTablesAsync(ct);
        return database;
    }

    /// <summary>
    /// THE OTHER MODULES' TABLES THE LEDGER AND OUTCOME WRITERS READ, created when no SQL Server
    /// schema module has created them yet: <c>teams</c>, <c>team_members</c>, <c>users</c>,
    /// <c>tenant_events</c> and <c>tenant_settings</c> (identity), <c>triggers</c>, and
    /// <c>backlog_items</c> and <c>backlog_dispatches</c> (backlog). Every append now writes its ledger
    /// and outcome rows (<c>SqlServerAppendRows</c>), and those read a run's member, a team's name, a
    /// trigger's outcome and an item's; until those modules' baselines exist, a test here would fail
    /// on the first append. Their columns and nullability as SQLite's, so a test's raw insert reads
    /// the same on both. Each is a no-op once the real table exists, and this goes when the last of
    /// those baselines lands.
    /// </summary>
    private async Task EnsureOtherModulesTablesAsync(CancellationToken ct)
    {
        const string ci = SqlServerSql.NoCase;

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             IF OBJECT_ID(N'teams', N'U') IS NULL
             CREATE TABLE teams (
                 id NVARCHAR(200) COLLATE {ci} NOT NULL PRIMARY KEY, name NVARCHAR(MAX) NULL,
                 member_agent NVARCHAR(MAX) NULL, member_agents NVARCHAR(MAX) NULL, member_prompt NVARCHAR(MAX) NULL,
                 root NVARCHAR(MAX) NULL, repos NVARCHAR(MAX) NULL, env NVARCHAR(MAX) NULL,
                 paused BIGINT NOT NULL DEFAULT 0, budget_tokens BIGINT NULL, created_utc NVARCHAR(40) NOT NULL,
                 additional_instructions NVARCHAR(MAX) NULL, archived_at NVARCHAR(40) NULL, archived_by NVARCHAR(MAX) NULL,
                 activity_from NVARCHAR(40) NULL, activity_from_by NVARCHAR(MAX) NULL);

             IF OBJECT_ID(N'team_members', N'U') IS NULL
             CREATE TABLE team_members (
                 team NVARCHAR(200) COLLATE {ci} NOT NULL, name NVARCHAR(200) COLLATE {ci} NOT NULL,
                 label NVARCHAR(MAX) NULL, agent NVARCHAR(MAX) NOT NULL, system_prompt NVARCHAR(MAX) NULL,
                 prompt NVARCHAR(MAX) NULL, subscribes NVARCHAR(MAX) NOT NULL, permits NVARCHAR(MAX) NOT NULL,
                 floor_seq BIGINT NOT NULL, hired_for NVARCHAR(MAX) NULL, created_utc NVARCHAR(40) NOT NULL,
                 system_prompt_set_by NVARCHAR(MAX) NULL, system_prompt_set_by_kind NVARCHAR(MAX) NULL,
                 system_prompt_set_at NVARCHAR(40) NULL,
                 PRIMARY KEY (team, name));

             IF OBJECT_ID(N'users', N'U') IS NULL
             CREATE TABLE users (
                 id NVARCHAR(450) NOT NULL PRIMARY KEY, email NVARCHAR(450) NOT NULL UNIQUE,
                 password_hash NVARCHAR(MAX) NOT NULL, created_at NVARCHAR(40) NOT NULL,
                 current_team NVARCHAR(200) NULL, appearance NVARCHAR(MAX) NULL);

             IF OBJECT_ID(N'tenant_events', N'U') IS NULL
             CREATE TABLE tenant_events (
                 seq BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY, occurred_at NVARCHAR(40) NOT NULL,
                 actor_id NVARCHAR(MAX) NULL, actor_email NVARCHAR(MAX) NULL, action NVARCHAR(450) NOT NULL,
                 subject NVARCHAR(450) NULL, subject_name NVARCHAR(MAX) NULL, detail NVARCHAR(MAX) NULL);

             IF OBJECT_ID(N'tenant_settings', N'U') IS NULL
             CREATE TABLE tenant_settings (
                 name NVARCHAR(450) NOT NULL PRIMARY KEY, value NVARCHAR(MAX) NOT NULL,
                 updated_at NVARCHAR(40) NOT NULL, updated_by NVARCHAR(MAX) NOT NULL);

             IF OBJECT_ID(N'triggers', N'U') IS NULL
             CREATE TABLE triggers (
                 id NVARCHAR(450) NOT NULL PRIMARY KEY, team NVARCHAR(200) COLLATE {ci} NOT NULL,
                 container NVARCHAR(200) COLLATE {ci} NOT NULL, name NVARCHAR(MAX) NOT NULL,
                 instruction NVARCHAR(MAX) NOT NULL, kind NVARCHAR(MAX) NOT NULL, expression NVARCHAR(MAX) NULL,
                 timezone NVARCHAR(MAX) NULL, interval_seconds BIGINT NULL, fire_at NVARCHAR(40) NULL,
                 event_type NVARCHAR(MAX) NULL, filter NVARCHAR(MAX) NULL, idle_only BIGINT NOT NULL,
                 enabled BIGINT NOT NULL, next_due_at NVARCHAR(40) NULL, last_fired_at NVARCHAR(40) NULL,
                 last_outcome NVARCHAR(MAX) NULL, last_seq BIGINT NULL, missed_count BIGINT NOT NULL,
                 created_at NVARCHAR(40) NOT NULL, created_by NVARCHAR(450) NOT NULL,
                 watch_root NVARCHAR(MAX) NULL, watch_path NVARCHAR(MAX) NULL, watch_glob NVARCHAR(MAX) NULL,
                 poll_seconds BIGINT NULL, quiet_seconds BIGINT NULL, min_interval_seconds BIGINT NULL,
                 last_fingerprint NVARCHAR(MAX) NULL, last_listing NVARCHAR(MAX) NULL,
                 pending_fingerprint NVARCHAR(MAX) NULL, pending_since NVARCHAR(40) NULL,
                 last_poll_at NVARCHAR(40) NULL, last_poll_ms BIGINT NULL, last_poll_entries BIGINT NULL,
                 last_poll_error NVARCHAR(MAX) NULL, last_change_at NVARCHAR(40) NULL,
                 wake_manager NVARCHAR(MAX) NOT NULL DEFAULT N'always', daily_token_cap BIGINT NULL,
                 capped_skips_day NVARCHAR(MAX) NULL, capped_skips BIGINT NOT NULL DEFAULT 0,
                 outcome_id NVARCHAR(450) NULL, configured_by_email NVARCHAR(MAX) NULL);

             IF OBJECT_ID(N'backlog_items', N'U') IS NULL
             CREATE TABLE backlog_items (
                 id NVARCHAR(450) NOT NULL PRIMARY KEY, team NVARCHAR(200) COLLATE {ci} NULL,
                 title NVARCHAR(MAX) NOT NULL, body NVARCHAR(MAX) NOT NULL, state NVARCHAR(MAX) NOT NULL,
                 position FLOAT NOT NULL, archived_at NVARCHAR(40) NULL, created_at NVARCHAR(40) NOT NULL,
                 updated_at NVARCHAR(40) NOT NULL, created_by NVARCHAR(MAX) NOT NULL, outcome_id NVARCHAR(450) NULL);

             IF OBJECT_ID(N'backlog_dispatches', N'U') IS NULL
             CREATE TABLE backlog_dispatches (
                 id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY, item NVARCHAR(450) NOT NULL,
                 team_id NVARCHAR(200) COLLATE {ci} NOT NULL, team_name NVARCHAR(MAX) NOT NULL,
                 correlation BIGINT NOT NULL, dispatched_at NVARCHAR(40) NOT NULL, dispatched_by NVARCHAR(MAX) NOT NULL,
                 frozen_at NVARCHAR(40) NULL, frozen_stats NVARCHAR(MAX) NULL, landed_at NVARCHAR(40) NULL,
                 landed_sha NVARCHAR(MAX) NULL, landed_branch NVARCHAR(MAX) NULL);
             """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
