using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="ILedgerStartRows"/>, the ledger's one-time start, as each provider must keep it: the two
/// <c>LedgerStartTests</c>, with the same names and values, run through the interface rather than a
/// Host start. The log is written past the store, as a volume from before the ledger holds it, so
/// only the backfill can give it ledger rows.
/// </summary>
public abstract class LedgerStartRowsContract : ProviderContract
{
    protected async Task<ILedgerStartRows> CreateAsync() => (await StoresAsync()).LedgerStart;

    [Fact]
    public async Task The_backfill_recovers_every_terminal_row_and_close_once_and_records_when_the_ledger_began()
    {
        var start = await CreateAsync();
        var stores = await StoresAsync();

        Task<long> InsertAsync(string type, string payload, string source, long? causation) =>
            stores.InsertRawMessageAsync(type, payload, source, causation);

        var root = await InsertAsync(MessageTypes.InstructionFor(new ContainerId("Alpha", "Dev")), """{"instruction":"go"}""", "console", null);
        await InsertAsync(MessageTypes.Started, "{}", "Alpha/Dev", root);
        var measured = await InsertAsync(MessageTypes.Completed, """{"tokensIn":100,"tokensOut":40,"tokensCachedIn":50,"tokensSource":"claude"}""", "Alpha/Dev", root);
        await InsertAsync(MessageTypes.Completed, $$"""{"tokensIn":null,"tokensOut":null,"usageCountedOn":{{root}}}""", "Alpha/Dev", root);
        var second = await InsertAsync(MessageTypes.InstructionFor(new ContainerId("Alpha", "Dev")), """{"instruction":"more"}""", "console", root);
        await InsertAsync(MessageTypes.Failed, """{"tokensIn":null,"tokensOut":null}""", "Alpha/Dev", second);
        var echo = await InsertAsync(MessageTypes.InstructionFor(new ContainerId("Alpha", "Echo")), """{"instruction":"echo"}""", "console", root);
        await InsertAsync(MessageTypes.Completed, """{"tokensSource":"none"}""", "Alpha/Echo", echo);
        await InsertAsync(MessageTypes.WorkflowCompleted, "{}", "Alpha/Manager", measured);
        await InsertAsync(MessageTypes.WorkflowClosed, """{"team":"Alpha"}""", "person-1", root);
        await InsertAsync(MessageTypes.WorkflowCompleted, "{}", "Alpha/Manager", measured);

        var terminalRows = 3;
        var closes = 3;

        var first = await start.RunAsync(Ct);
        Assert.Equal(terminalRows, first.BackfilledRuns);
        Assert.Equal(closes, first.BackfilledWorkflows);

        Assert.Equal(terminalRows, await stores.LongAsync("SELECT COUNT(*) FROM usage_ledger WHERE backfilled = 1"));
        Assert.Equal(closes, await stores.LongAsync("SELECT COUNT(*) FROM workflow_ledger WHERE backfilled = 1"));
        Assert.Equal(0, await stores.LongAsync("SELECT COUNT(*) FROM usage_ledger WHERE backfilled = 0"));

        var startedAt = await SettingAsync(stores, LedgerStart.LedgerStartedAtName);
        Assert.NotNull(startedAt);
        Assert.Equal(startedAt, first.LedgerStartedAt);
        Assert.Equal(terminalRows.ToString(System.Globalization.CultureInfo.InvariantCulture), await SettingAsync(stores, LedgerStart.LedgerBackfilledRunsName));

        // Each setting was written with its tenant row, as every settings write is.
        Assert.Equal(1, await stores.LongAsync(
            $"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.TenantSettingChanged}' AND subject = '{LedgerStart.LedgerStartedAtName}'"));

        // A recovered row is built as a live one is: the batched run's extra row wrote nothing,
        // the measured run is weighted, unmeasured is null.
        Assert.Equal(100 + 40 + (50 / 10), await stores.LongAsync($"SELECT billable FROM usage_ledger WHERE run_seq = {measured}"));
        Assert.Equal(1, await stores.LongAsync("SELECT COUNT(*) FROM usage_ledger WHERE billable IS NULL AND measured = 0"));
        Assert.Equal(1, await stores.LongAsync("SELECT COUNT(*) FROM usage_ledger WHERE member_kind = 'plugin' AND billable = 0 AND tokens_in IS NULL"));

        // Second start: nothing is recovered twice and the settings are as they were.
        var again = await start.RunAsync(Ct);
        Assert.Null(again.BackfilledRuns);
        Assert.Equal(terminalRows, await stores.LongAsync("SELECT COUNT(*) FROM usage_ledger"));
        Assert.Equal(closes, await stores.LongAsync("SELECT COUNT(*) FROM workflow_ledger"));
        Assert.Equal(startedAt, await SettingAsync(stores, LedgerStart.LedgerStartedAtName));
        Assert.Equal(1, await stores.LongAsync(
            $"SELECT COUNT(*) FROM tenant_events WHERE subject = '{LedgerStart.LedgerBackfilledRunsName}'"));
    }

    [Fact]
    public async Task The_instance_id_is_created_once_and_unchanged_across_restarts()
    {
        var start = await CreateAsync();
        var stores = await StoresAsync();

        var first = (await start.RunAsync(Ct)).InstanceId;

        var stored = await SettingAsync(stores, LedgerStart.InstanceIdName);
        Assert.NotNull(stored);
        Assert.True(Guid.TryParse(stored, out _));
        Assert.Equal(stored, first);

        var second = (await start.RunAsync(Ct)).InstanceId;

        Assert.Equal(stored, second);
        Assert.Equal(stored, await SettingAsync(stores, LedgerStart.InstanceIdName));
        Assert.Equal(1, await stores.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE subject = '{LedgerStart.InstanceIdName}'"));
    }

    [Fact]
    public async Task The_rate_epoch_is_written_once_with_the_stored_rate_and_a_later_start_or_rate_change_does_not_move_it()
    {
        var start = await CreateAsync();
        var stores = await StoresAsync();
        await StoreSettingAsync(stores, "outcomes.agentHourlyRate", "60");
        await StoreSettingAsync(stores, "outcomes.currency", "eur");

        // A configured rate is not read while a rate row is stored.
        var first = await start.RecordRateEpochAsync(new RateEpochConfiguration("90", "usd"), Ct);
        Assert.Equal(("60", "EUR", "tenant"), (first.RateAmount, first.RateCurrency, first.RateSource));

        // The rate changes, and the Host starts again: the row is the one written first.
        await stores.ExecuteAsync("UPDATE tenant_settings SET value = '75' WHERE name = 'outcomes.agentHourlyRate'");
        var second = await start.RecordRateEpochAsync(new RateEpochConfiguration("90", "usd"), Ct);

        Assert.Equal(first, second);
        Assert.Equal(1, await stores.LongAsync("SELECT COUNT(*) FROM rate_epochs"));
        Assert.Equal("60", await stores.TextAsync("SELECT rate_amount FROM rate_epochs"));
    }

    [Fact]
    public async Task With_no_stored_rate_the_epoch_takes_a_configured_positive_rate_and_otherwise_records_none()
    {
        var stores = await StoresAsync();
        var configured = await (await CreateAsync()).RecordRateEpochAsync(new RateEpochConfiguration("62.50", null), Ct);
        Assert.Equal(("62.5", "USD", "configuration"), (configured.RateAmount, configured.RateCurrency, configured.RateSource));
        Assert.Equal("configuration", await stores.TextAsync("SELECT rate_source FROM rate_epochs"));
    }

    [Fact]
    public async Task A_stored_zero_rate_hides_a_configured_one_and_the_epoch_records_none()
    {
        var stores = await StoresAsync();
        await StoreSettingAsync(stores, "outcomes.agentHourlyRate", "0");

        var epoch = await (await CreateAsync()).RecordRateEpochAsync(new RateEpochConfiguration("90", "usd"), Ct);

        Assert.Equal(((string?)null, (string?)null, "none"), (epoch.RateAmount, epoch.RateCurrency, epoch.RateSource));
        Assert.Equal(1, await stores.LongAsync("SELECT COUNT(*) FROM rate_epochs WHERE rate_amount IS NULL AND rate_currency IS NULL"));
    }

    [Fact]
    public async Task The_rate_epoch_is_never_updated_deleted_or_joined_by_a_second_row()
    {
        var stores = await StoresAsync();
        await (await CreateAsync()).RecordRateEpochAsync(new RateEpochConfiguration("60", null), Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => stores.ExecuteAsync("UPDATE rate_epochs SET rate_amount = '90'"));
        await Assert.ThrowsAnyAsync<Exception>(() => stores.ExecuteAsync("DELETE FROM rate_epochs"));
        await Assert.ThrowsAnyAsync<Exception>(() => stores.ExecuteAsync(
            "INSERT INTO rate_epochs (id, recorded_at, rate_amount, rate_currency, rate_source) VALUES (2, 'x', '90', 'USD', 'tenant')"));

        Assert.Equal(1, await stores.LongAsync("SELECT COUNT(*) FROM rate_epochs"));
        Assert.Equal("60", await stores.TextAsync("SELECT rate_amount FROM rate_epochs"));
    }

    private static Task StoreSettingAsync(ContractStores stores, string name, string value) =>
        stores.ExecuteAsync(
            $"INSERT INTO tenant_settings (name, value, updated_at, updated_by) VALUES ('{name}', '{value}', '2026-01-01T00:00:00.0000000+00:00', 'p')");

    private static Task<string?> SettingAsync(ContractStores stores, string name) =>
        stores.TextAsync($"SELECT value FROM tenant_settings WHERE name = '{name}'");
}

public sealed class SqliteLedgerStartRowsTests : LedgerStartRowsContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());
}

public sealed class SqlServerLedgerStartRowsTests : LedgerStartRowsContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();
}
