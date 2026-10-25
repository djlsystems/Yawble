using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

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
        return new SqlServerContractDatabase(await SqlServerTestDatabase.CreateAsync(ct));
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
