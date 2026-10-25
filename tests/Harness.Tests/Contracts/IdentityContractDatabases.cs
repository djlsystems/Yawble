using System.Data.Common;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.SqlServer;
using Harness.Identity;
using Harness.Messaging;
using Harness.Skills;
using Harness.SqlServer;
using Harness.SqlServer.Backlog;
using Harness.SqlServer.Host;
using Harness.SqlServer.Identity;
using Harness.SqlServer.Ledger;
using Harness.SqlServer.Messaging;
using Harness.SqlServer.Skills;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace Harness.Tests.Contracts;

/// <summary>
/// ONE PROVIDER'S IDENTITY STORES OVER ONE FRESH DATABASE, for the identity contracts: the accounts,
/// principals, teams, tenant log, tenant settings and unfinished removals, each the provider's real
/// store; and the skill index and the backlog, whose writes land their tenant rows in the same
/// database. Several behaviours span two of them (a deleted user's keys, a team write and its tenant row),
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

    public abstract ITriggerStore Triggers();

    public abstract ISiteStore Sites();

    public abstract ITeamSolutionStore Solutions();

    /// <summary>The diagnostics log under <paramref name="retention"/>, or a bound no test reaches.</summary>
    public abstract IDiagnosticsLog Diagnostics(DiagnosticRetention? retention = null);

    /// <summary>A bound no test's rows reach unless it asks for one.</summary>
    protected static readonly DiagnosticRetention Unbounded = new(TimeSpan.FromDays(3650), 1_000_000);

    public abstract ISkillStore Skills();

    /// <summary>
    /// The backlog store, its tenant rows appended through <paramref name="audit"/>, or a store made
    /// without a tenant row writer when it is null. <see cref="TenantRowAsync"/> is the provider's own.
    /// </summary>
    public abstract IBacklogStore Backlog(Func<DbConnection, DbTransaction, TriggerAudit, CancellationToken, Task>? audit);

    /// <summary>The message log over the same database, for a behaviour that spans it and the Host's rows (the purge).</summary>
    public abstract IMessageLog Log();

    public abstract IAgentStepStore AgentSteps();

    public abstract IWorkspaceFileChangeStore FileChanges();

    public abstract IPluginMemberSettingsStore MemberSettings();

    public abstract IPluginMemberSecretRows MemberSecretRows();

    public abstract IPluginSecretRows SecretRows();

    public abstract IPluginRemovalRows RemovalRows();

    public abstract IConnectionStore Connections(IDataProtectionProvider protection);

    public abstract IDatabaseHealth Health();

    /// <summary>The health probe of a database that does not exist.</summary>
    public abstract IDatabaseHealth MissingHealth();

    public abstract IDatabaseMaintenance Maintenance();

    /// <summary>The provider's one insert into <c>tenant_events</c>, inside the caller's transaction.</summary>
    public abstract Task TenantRowAsync(DbConnection connection, DbTransaction transaction, TriggerAudit audit, CancellationToken ct);

    /// <summary>An open connection of the provider's own kind, for a store call inside a caller's transaction.</summary>
    public abstract Task<DbConnection> OpenConnectionAsync();

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

    public override ITriggerStore Triggers() => new SqliteTriggerStore(_database.Path);

    public override ISiteStore Sites() => new SqliteSiteStore(_database.Path);

    public override ITeamSolutionStore Solutions() => new SqliteTeamSolutionStore(_database.Path);

    public override IDiagnosticsLog Diagnostics(DiagnosticRetention? retention = null) =>
        new SqliteDiagnosticsLog(_database.Path, retention ?? Unbounded);

    public override ISkillStore Skills() => new SqliteSkillStore(_database.Path);

    public override IBacklogStore Backlog(Func<DbConnection, DbTransaction, TriggerAudit, CancellationToken, Task>? audit) =>
        new SqliteBacklogStore(_database.Path, audit: audit is null ? null : (connection, transaction, row, ct) => audit(connection, transaction, row, ct));

    public override IMessageLog Log() => new SqliteMessageStore(_database.Path);

    public override IAgentStepStore AgentSteps() => new AgentStepStore(_database.Path);

    public override IWorkspaceFileChangeStore FileChanges() => new WorkspaceFileChangeStore(_database.Path);

    public override IPluginMemberSettingsStore MemberSettings() => new SqlitePluginMemberSettings(_database.Path);

    public override IPluginMemberSecretRows MemberSecretRows() => new SqlitePluginMemberSecretRows(_database.Path);

    public override IPluginSecretRows SecretRows() => new SqlitePluginSecretRows(_database.Path);

    public override IPluginRemovalRows RemovalRows() => new SqlitePluginRemovalRows(_database.Path);

    public override IConnectionStore Connections(IDataProtectionProvider protection) => new ConnectionStore(_database.Path, protection);

    public override IDatabaseHealth Health() => new SqliteDatabaseHealth(_database.Path);

    public override IDatabaseHealth MissingHealth() =>
        new SqliteDatabaseHealth(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_database.Path)!, "missing", "messages.db"));

    public override IDatabaseMaintenance Maintenance() => new SqliteDatabaseMaintenance(_database.Path);

    public override Task TenantRowAsync(DbConnection connection, DbTransaction transaction, TriggerAudit audit, CancellationToken ct) =>
        TenantAuditRow.AppendAsync(connection, transaction, audit, ct);

    public override async Task<DbConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection($"Data Source={_database.Path};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        SqlitePragmas.Apply(connection);
        return connection;
    }

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

    public override ITriggerStore Triggers() => new SqlServerTriggerStore(ConnectionString);

    public override ISiteStore Sites() => new SqlServerSiteStore(ConnectionString);

    public override ITeamSolutionStore Solutions() => new SqlServerTeamSolutionStore(ConnectionString);

    public override IDiagnosticsLog Diagnostics(DiagnosticRetention? retention = null) =>
        new SqlServerDiagnosticsLog(ConnectionString, retention ?? Unbounded);

    public override ISkillStore Skills() => new SqlServerSkillStore(ConnectionString);

    public override IBacklogStore Backlog(Func<DbConnection, DbTransaction, TriggerAudit, CancellationToken, Task>? audit) =>
        new SqlServerBacklogStore(ConnectionString, audit: audit is null ? null : (connection, transaction, row, ct) => audit(connection, transaction, row, ct));

    public override IMessageLog Log() => new SqlServerMessageStore(ConnectionString, new SqlServerAppendRows());

    public override IAgentStepStore AgentSteps() => new SqlServerAgentStepStore(ConnectionString);

    public override IWorkspaceFileChangeStore FileChanges() => new SqlServerWorkspaceFileChangeStore(ConnectionString);

    public override IPluginMemberSettingsStore MemberSettings() => new SqlServerPluginMemberSettings(ConnectionString);

    public override IPluginMemberSecretRows MemberSecretRows() => new SqlServerPluginMemberSecretRows(ConnectionString);

    public override IPluginSecretRows SecretRows() => new SqlServerPluginSecretRows(ConnectionString);

    public override IPluginRemovalRows RemovalRows() => new SqlServerPluginRemovalRows(ConnectionString);

    public override IConnectionStore Connections(IDataProtectionProvider protection) => new SqlServerConnectionStore(ConnectionString, protection);

    public override IDatabaseHealth Health() => new SqlServerDatabaseHealth(ConnectionString);

    public override IDatabaseHealth MissingHealth() =>
        new SqlServerDatabaseHealth(new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = $"harness_missing_{Guid.NewGuid():N}",
            ConnectTimeout = 5,
        }.ConnectionString);

    public override IDatabaseMaintenance Maintenance() => new SqlServerDatabaseMaintenance();

    public override Task TenantRowAsync(DbConnection connection, DbTransaction transaction, TriggerAudit audit, CancellationToken ct) =>
        SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, ct);

    public override async Task<DbConnection> OpenConnectionAsync() => await OpenAsync();

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
