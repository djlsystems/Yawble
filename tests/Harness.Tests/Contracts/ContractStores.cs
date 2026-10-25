using System.Data.Common;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.SqlServer;
using Harness.Messaging;
using Harness.SqlServer;
using Harness.SqlServer.Ledger;
using Harness.SqlServer.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace Harness.Tests.Contracts;

/// <summary>
/// ONE PROVIDER'S STORES OVER ONE FRESH DATABASE, for the ledger and outcome contracts, whose
/// behaviours span several stores at once: a run appended to the log is read back from the usage
/// ledger, a delivery accepted onto the pending table is billed when its run ends. Each store is the
/// provider's real one, wired as the Host wires it (the log and the pending deliveries write the
/// ledger and outcome rows in their own transactions).
///
/// <para>
/// THE RAW STATEMENTS a contract runs are written once for both providers: plain SQL the two dialects
/// share (no parameters, no concatenation), so a test reads the same table the same way on each. Only
/// a raw log insert differs, because SQLite answers the new seq with RETURNING and SQL Server with
/// SCOPE_IDENTITY.
/// </para>
/// </summary>
public abstract class ContractStores : IAsyncDisposable
{
    public abstract IMessageLog Log { get; }

    public abstract IPendingDeliveries Pending { get; }

    public abstract IUsageLedger UsageLedger { get; }

    public abstract ILogSpend LogSpend { get; }

    public abstract IAdmissionHoldLedger AdmissionHolds { get; }

    public abstract ILogRetention Retention { get; }

    public abstract ILedgerStartRows LedgerStart { get; }

    public abstract IOutcomeStartRows OutcomeStart { get; }

    public abstract IOutcomeStore Outcomes { get; }

    public abstract IOutcomeLinkRows OutcomeLinkRows { get; }

    public abstract ILedger Ledger(IPluginEventRegistry? plugins = null);

    /// <summary>A statement's affected rows.</summary>
    public abstract Task<int> ExecuteAsync(string sql);

    /// <summary>A statement's rows, every column as its value (null for NULL).</summary>
    public abstract Task<IReadOnlyList<object?[]>> RowsAsync(string sql);

    /// <summary>
    /// A log row written straight into <c>messages</c>, past the store and so past its ledger and
    /// outcome rows: what a volume from before the ledger holds. Correlation and depth follow the cause.
    /// </summary>
    public abstract Task<long> InsertRawMessageAsync(string type, string payload, string source, long? causation);

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

    public abstract ValueTask DisposeAsync();

    protected static async Task<IReadOnlyList<object?[]>> ReadRowsAsync(DbCommand command)
    {
        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }

        return rows;
    }
}

public sealed class SqliteContractStores : ContractStores
{
    private readonly SqliteContractDatabase _database = new();
    private readonly Lazy<SqliteMessageStore> _log;

    public SqliteContractStores() => _log = new(() => new SqliteMessageStore(_database.Path));

    public string Path => _database.Path;

    public override IMessageLog Log => _log.Value;

    public override IPendingDeliveries Pending => new SqlitePendingDeliveries(_database.Path);

    public override IUsageLedger UsageLedger => new SqliteUsageLedger(_database.Path);

    public override ILogSpend LogSpend => new LogSpend(_database.Path);

    public override IAdmissionHoldLedger AdmissionHolds => new SqliteAdmissionHolds(_database.Path);

    public override ILogRetention Retention => new SqliteLogRetention(_database.Path);

    public override ILedgerStartRows LedgerStart => new SqliteLedgerStartRows(_database.Path);

    public override IOutcomeStartRows OutcomeStart => new SqliteOutcomeStartRows(_database.Path);

    public override IOutcomeStore Outcomes => new SqliteOutcomeStore(_database.Path, TenantAuditRow.AppendAsync);

    public override IOutcomeLinkRows OutcomeLinkRows => new SqliteOutcomeLinkRows();

    public override ILedger Ledger(IPluginEventRegistry? plugins = null) => new SqliteLedger(_database.Path, plugins);

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={_database.Path};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public override async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public override async Task<IReadOnlyList<object?[]>> RowsAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await ReadRowsAsync(command);
    }

    public override async Task<long> InsertRawMessageAsync(string type, string payload, string source, long? causation)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync();

        var (correlation, depth) = causation is { } cause ? await ParentAsync(connection, cause) : (0L, 0);

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO messages (type, payload, source, correlation_id, causation_seq, depth, occurred_at)
            VALUES ($type, $payload, $source, $correlation, $causation, $depth, $at)
            RETURNING seq
            """;
        insert.Parameters.AddWithValue("$type", type);
        insert.Parameters.AddWithValue("$payload", payload);
        insert.Parameters.AddWithValue("$source", source);
        insert.Parameters.AddWithValue("$correlation", correlation);
        insert.Parameters.AddWithValue("$causation", (object?)causation ?? DBNull.Value);
        insert.Parameters.AddWithValue("$depth", depth);
        insert.Parameters.AddWithValue("$at", Instant.Now());
        var seq = (long)(await insert.ExecuteScalarAsync(ct))!;

        if (causation is null)
        {
            await using var settle = connection.CreateCommand();
            settle.CommandText = $"UPDATE messages SET correlation_id = {seq} WHERE seq = {seq}";
            await settle.ExecuteNonQueryAsync(ct);
        }

        return seq;
    }

    private static async Task<(long, int)> ParentAsync(SqliteConnection connection, long cause)
    {
        await using var parent = connection.CreateCommand();
        parent.CommandText = $"SELECT correlation_id, depth FROM messages WHERE seq = {cause}";
        await using var reader = await parent.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        return (reader.GetInt64(0), reader.GetInt32(1) + 1);
    }

    public override ValueTask DisposeAsync() => _database.DisposeAsync();
}

public sealed class SqlServerContractStores : ContractStores
{
    private readonly SqlServerContractDatabase _database;
    private readonly SqlServerAppendRows _appendRows = new();
    private readonly SqlServerMessageStore _log;

    private SqlServerContractStores(SqlServerContractDatabase database)
    {
        _database = database;
        _log = new SqlServerMessageStore(database.ConnectionString, _appendRows);
    }

    public static async Task<SqlServerContractStores> CreateAsync() =>
        new(await SqlServerContractDatabase.CreateAsync(TestContext.Current.CancellationToken));

    public string ConnectionString => _database.ConnectionString;

    public override IMessageLog Log => _log;

    public override IPendingDeliveries Pending => new SqlServerPendingDeliveries(ConnectionString, _appendRows);

    public override IUsageLedger UsageLedger => new SqlServerUsageLedger(ConnectionString);

    public override ILogSpend LogSpend => new SqlServerLogSpend(ConnectionString);

    public override IAdmissionHoldLedger AdmissionHolds => new SqlServerAdmissionHolds(ConnectionString);

    public override ILogRetention Retention => new SqlServerLogRetention(ConnectionString);

    public override ILedgerStartRows LedgerStart => new SqlServerLedgerStartRows(ConnectionString);

    public override IOutcomeStartRows OutcomeStart => new SqlServerOutcomeStartRows(ConnectionString);

    public override IOutcomeStore Outcomes => new SqlServerOutcomeStore(ConnectionString, SqlServerTenantAuditRow.AppendAsync);

    public override IOutcomeLinkRows OutcomeLinkRows => new SqlServerOutcomeLinkRows();

    public override ILedger Ledger(IPluginEventRegistry? plugins = null) => new SqlServerLedger(ConnectionString, plugins);

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public override async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public override async Task<IReadOnlyList<object?[]>> RowsAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await ReadRowsAsync(command);
    }

    public override async Task<long> InsertRawMessageAsync(string type, string payload, string source, long? causation)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync();

        long correlation = 0;
        long depth = 0;
        if (causation is { } cause)
        {
            await using var parent = connection.CreateCommand();
            parent.CommandText = $"SELECT correlation_id, depth FROM messages WHERE seq = {cause}";
            await using var reader = await parent.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            correlation = reader.GetInt64(0);
            depth = reader.GetInt64(1) + 1;
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO messages (type, payload, source, correlation_id, causation_seq, depth, occurred_at)
            VALUES (@type, @payload, @source, @correlation, @causation, @depth, @at);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """;
        insert.Parameters.AddWithValue("@type", type);
        insert.Parameters.AddWithValue("@payload", payload);
        insert.Parameters.AddWithValue("@source", source);
        insert.Parameters.AddWithValue("@correlation", correlation);
        insert.Parameters.Add("@causation", System.Data.SqlDbType.BigInt).Value = (object?)causation ?? DBNull.Value;
        insert.Parameters.AddWithValue("@depth", depth);
        insert.Parameters.AddWithValue("@at", Instant.Now());
        var seq = (long)(await insert.ExecuteScalarAsync(ct))!;

        if (causation is null)
        {
            await using var settle = connection.CreateCommand();
            settle.CommandText = $"UPDATE messages SET correlation_id = {seq} WHERE seq = {seq}";
            await settle.ExecuteNonQueryAsync(ct);
        }

        return seq;
    }

    public override ValueTask DisposeAsync() => _database.DisposeAsync();
}

/// <summary>
/// The shape of every ledger and outcome contract: a fresh <see cref="ContractStores"/> per test,
/// opened by the provider's sealed subclass (<see cref="OpenAsync"/>), and each contract's own
/// <c>CreateAsync</c> answering the interface it is about from it.
/// </summary>
public abstract class ProviderContract : IAsyncDisposable
{
    private ContractStores? _stores;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The provider's stores over a database of its own.</summary>
    protected abstract Task<ContractStores> OpenAsync();

    /// <summary>This test's stores, opened on first use.</summary>
    protected async Task<ContractStores> StoresAsync() => _stores ??= await OpenAsync();

    public async ValueTask DisposeAsync()
    {
        if (_stores is not null) await _stores.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
