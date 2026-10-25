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
