using System.Data.Common;
using Harness.Contracts;
using Harness.Identity;
using Harness.SqlServer.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace Harness.Tests.Contracts;

/// <summary>
/// ONE PROVIDER'S IDENTITY STORES OVER ONE FRESH DATABASE, for the identity contracts: the accounts,
/// principals, teams, tenant log, tenant settings and unfinished removals, each the provider's real
/// store. Several behaviours span two of them (a deleted user's keys, a team write and its tenant row),
/// so a contract asks for the database and takes the stores it needs from it.
///
/// <para>
/// THE RAW STATEMENTS a contract runs are written once for both providers, as plain SQL the two
/// dialects share (no parameters), so a test reads or breaks the same table the same way on each.
/// </para>
/// </summary>
public abstract class IdentityDatabase : IAsyncDisposable
{
    /// <summary>One PBKDF2 iteration unless asked: the hash's form is the same, and the suite stays fast.</summary>
    public abstract IUserStore Users(int? hashIterations = 1);

    public abstract IPrincipalStore Principals(TimeProvider? clock = null);

    public abstract ITeamStore Teams();

    public abstract ITenantLog TenantLog();

    public abstract ITenantSettingsStore TenantSettings();

    public abstract IUnfinishedRemovals Removals();

    /// <summary>A statement's affected rows.</summary>
    public abstract Task<int> ExecuteAsync(string sql);

    /// <summary>A statement's rows, every column as its value (null for NULL).</summary>
    public abstract Task<IReadOnlyList<object?[]>> RowsAsync(string sql);

    /// <summary>The first column of the first row, as a number (0 for no row or NULL).</summary>
    public async Task<long> LongAsync(string sql)
    {
        var rows = await RowsAsync(sql);
        return rows.Count == 0 || rows[0][0] is null ? 0 : Convert.ToInt64(rows[0][0], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The first column of the first row, as text, or null.</summary>
    public async Task<string?> TextAsync(string sql)
    {
        var rows = await RowsAsync(sql);
        return rows.Count == 0 ? null : rows[0][0] as string;
    }

    /// <summary>The first column of every row, as text.</summary>
    public async Task<IReadOnlyList<string?>> ColumnAsync(string sql) =>
        [.. (await RowsAsync(sql)).Select(row => row[0] as string)];

    public abstract ValueTask DisposeAsync();

    protected static async Task<int> ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    protected static async Task<IReadOnlyList<object?[]>> RowsAsync(DbConnection connection, string sql)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }

        return rows;
    }
}

public sealed class SqliteIdentityDatabase : IdentityDatabase
{
    private readonly SqliteContractDatabase _database = new();

    public string Path => _database.Path;

    public override IUserStore Users(int? hashIterations = 1) => new SqliteUserStore(_database.Path, hashIterations);

    public override IPrincipalStore Principals(TimeProvider? clock = null) => new SqlitePrincipalStore(_database.Path, clock: clock);

    public override ITeamStore Teams() => new SqliteTeamStore(_database.Path);

    public override ITenantLog TenantLog() => new SqliteTenantLog(_database.Path);

    public override ITenantSettingsStore TenantSettings() => new SqliteTenantSettingsStore(_database.Path);

    public override IUnfinishedRemovals Removals() => new SqliteUnfinishedRemovals(_database.Path);

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={_database.Path};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public override async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        return await ExecuteAsync(connection, sql);
    }

    public override async Task<IReadOnlyList<object?[]>> RowsAsync(string sql)
    {
        await using var connection = await OpenAsync();
        return await RowsAsync(connection, sql);
    }

    public override ValueTask DisposeAsync() => _database.DisposeAsync();
}

public sealed class SqlServerIdentityDatabase : IdentityDatabase
{
    private readonly SqlServerContractDatabase _database;

    private SqlServerIdentityDatabase(SqlServerContractDatabase database) => _database = database;

    public static async Task<SqlServerIdentityDatabase> CreateAsync() =>
        new(await SqlServerContractDatabase.CreateAsync(TestContext.Current.CancellationToken));

    public string ConnectionString => _database.ConnectionString;

    public override IUserStore Users(int? hashIterations = 1) => new SqlServerUserStore(ConnectionString, hashIterations);

    public override IPrincipalStore Principals(TimeProvider? clock = null) => new SqlServerPrincipalStore(ConnectionString, clock);

    public override ITeamStore Teams() => new SqlServerTeamStore(ConnectionString);

    public override ITenantLog TenantLog() => new SqlServerTenantLog(ConnectionString);

    public override ITenantSettingsStore TenantSettings() => new SqlServerTenantSettingsStore(ConnectionString);

    public override IUnfinishedRemovals Removals() => new SqlServerUnfinishedRemovals(ConnectionString);

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public override async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        return await ExecuteAsync(connection, sql);
    }

    public override async Task<IReadOnlyList<object?[]>> RowsAsync(string sql)
    {
        await using var connection = await OpenAsync();
        return await RowsAsync(connection, sql);
    }

    public override ValueTask DisposeAsync() => _database.DisposeAsync();
}

/// <summary>
/// The shape of every identity contract: a fresh <see cref="IdentityDatabase"/> per test, opened by
/// the provider's sealed subclass (<see cref="OpenAsync"/>), and each contract's own
/// <c>CreateAsync</c> answering the interface it is about from it.
/// </summary>
public abstract class IdentityContract : IAsyncDisposable
{
    private IdentityDatabase? _database;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The provider's identity stores over a database of its own.</summary>
    protected abstract Task<IdentityDatabase> OpenAsync();

    /// <summary>This test's database, opened on first use.</summary>
    protected async Task<IdentityDatabase> DatabaseAsync() => _database ??= await OpenAsync();

    /// <summary>
    /// <paramref name="writers"/> calls of <paramref name="write"/> released at one moment, each on its
    /// own thread and connection, with every answer or exception.
    /// </summary>
    protected static async Task<T[]> RaceAsync<T>(int writers, Func<int, Task<T>> write)
    {
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
        {
            start.Wait(Ct);
            return await write(i);
        }, Ct)).ToList();

        start.Set();
        return await Task.WhenAll(tasks);
    }

    /// <summary>A person's tenant row for <paramref name="action"/> on <paramref name="subject"/>.</summary>
    protected static TriggerAudit Audit(string action, string subject) =>
        new(null, "person@example.test", action, subject, subject, null);

    /// <summary>Text over 200 KB holding an emoji, accents, CJK and a <c>'</c>: what must come back byte for byte.</summary>
    protected static string LongUnicode(string lead) =>
        $"{lead} O'Brien's 🚀 café 日本語 " + string.Concat(Enumerable.Repeat("progress line 🧪 'quoted' ünïcödé ", 6_000));

    public async ValueTask DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
