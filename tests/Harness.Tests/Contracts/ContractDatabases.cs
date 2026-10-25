using Harness.Host;
using Harness.Messaging;
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
/// server) with READ_COMMITTED_SNAPSHOT on, as Azure SQL Database runs by default.
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
        var database = await SqlServerTestDatabase.CreateAsync(ct);
        var name = new SqlConnectionStringBuilder(database.ConnectionString).InitialCatalog;

        // The migrator's pooled connection would hold the database open against the change.
        SqlConnection.ClearAllPools();

        await using var connection = new SqlConnection(Environment.GetEnvironmentVariable("HARNESS_TEST_SQLSERVER"));
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE";
        await command.ExecuteNonQueryAsync(ct);

        return new SqlServerContractDatabase(database);
    }

    /// <summary>
    /// THE TWO LEDGER TABLES THE LOG'S SPEND READS SELECT FROM (<c>usage_ledger</c>,
    /// <c>nudge_ledger</c>), created empty when no schema module has created them. They belong to the
    /// outcome module, whose SQL Server baseline is written with the ledger's stores; until it exists
    /// a workflow projection here reads them empty and so reports no spend. Only the columns the
    /// spend reads name. A no-op once the outcome baseline creates the real tables.
    /// </summary>
    public async Task EnsureLedgerReadTablesAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            IF OBJECT_ID(N'usage_ledger', N'U') IS NULL
            CREATE TABLE usage_ledger (
                run_seq          BIGINT        NOT NULL PRIMARY KEY,
                correlation      BIGINT        NOT NULL,
                measured         BIGINT        NOT NULL,
                billable         BIGINT        NULL,
                trigger_source   NVARCHAR(450) NULL,
                trigger_fired_at NVARCHAR(40)  NULL
            );

            IF OBJECT_ID(N'nudge_ledger', N'U') IS NULL
            CREATE TABLE nudge_ledger (
                nudge_seq   BIGINT       NOT NULL PRIMARY KEY,
                correlation BIGINT       NOT NULL,
                nudged_at   NVARCHAR(40) NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
