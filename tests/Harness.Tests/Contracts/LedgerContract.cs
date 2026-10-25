using System.Text.Json;
using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="ILedger"/>, a member's memory read off the log, as each provider must keep it. The first
/// three are <c>LedgerExclusionTests</c>' and <c>PluginHighVolumeLedgerTests</c>' with the same names
/// and values (a source in another case is the member's own; progress stays out and blocked stays in;
/// an installed plugin's exclusions are read on every query, here through a registry the test
/// changes); the rest are the interface's remarks (the floor, the bound from the recent end, the
/// waking row left out, the workflow scope applied inside the bound).
/// </summary>
public abstract class LedgerContract : ProviderContract
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");

    protected async Task<ILedger> CreateAsync(IPluginEventRegistry? plugins = null) => (await StoresAsync()).Ledger(plugins);

    [Fact]
    public async Task A_row_whose_source_differs_only_in_case_is_still_the_members_own()
    {
        var ledger = await CreateAsync();
        var store = (await StoresAsync()).Log;

        await store.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"stuck"}""", new ContainerId("Alpha", "Dev").ToString()), Ct);

        var read = await ledger.ReadAsync(new ContainerId("alpha", "dev"), 0, null, long.MaxValue, 100, Ct);

        Assert.Contains(read, m => m.Type == MessageTypes.Blocked);
    }

    [Fact]
    public async Task Progress_stays_on_the_log_and_blocked_stays_in_the_ledger()
    {
        var ledger = await CreateAsync();
        var store = (await StoresAsync()).Log;

        await store.AppendAsync(new NewMessage(MessageTypes.Progress, """{"status":"working"}""", Dev.ToString()), Ct);
        await store.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"stuck"}""", Dev.ToString()), Ct);

        var read = await ledger.ReadAsync(Dev, 0, null, long.MaxValue, 100, Ct);
        Assert.DoesNotContain(read, m => m.Type == MessageTypes.Progress);
        Assert.Contains(read, m => m.Type == MessageTypes.Blocked);

        // On the log all the same: the board and the feed read it there.
        Assert.Single(await store.ReadAfterAsync(0, [MessageTypes.Progress], 10, Ct));
    }

    [Fact]
    public async Task A_ledger_given_the_installed_plugins_reads_their_exclusions_on_every_query()
    {
        var plugins = new Plugins();
        var ledger = await CreateAsync(plugins);
        var store = (await StoresAsync()).Log;
        var member = new ContainerId("Alpha", "Fx");

        await store.AppendAsync(new NewMessage("plugin.fx.tick", "{}", member.ToString()), Ct);
        await store.AppendAsync(new NewMessage("plugin.fx.done", "{}", member.ToString()), Ct);

        // Nothing installed yet: nothing of the plugin's is excluded.
        Assert.Equal(2, (await ledger.ReadAsync(member, 0, null, long.MaxValue, 100, Ct)).Count);

        plugins.Install(
            new EventDefinition("plugin.fx.tick", EventPublisher.Plugin, HighVolume: true, InLedger: false, [], "tick"),
            new EventDefinition("plugin.fx.done", EventPublisher.Plugin, HighVolume: false, InLedger: true, [], "done"));

        Assert.Equal(["plugin.fx.done"], (await ledger.ReadAsync(member, 0, null, long.MaxValue, 100, Ct)).Select(m => m.Type));
    }

    [Fact]
    public async Task The_window_is_the_newest_rows_above_the_floor_and_below_the_waking_row_oldest_first()
    {
        var ledger = await CreateAsync();
        var store = (await StoresAsync()).Log;

        var before = await store.AppendAsync(Told("before the floor"), Ct);
        var told = await store.AppendAsync(Told("one"), Ct);
        var own = await store.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"two"}""", Dev.ToString(), told.Seq), Ct);
        await store.AppendAsync(new NewMessage(MessageTypes.Blocked, "{}", new ContainerId("Alpha", "Other").ToString()), Ct);
        var three = await store.AppendAsync(Told("three"), Ct);
        var four = await store.AppendAsync(Told("four"), Ct);
        var waking = await store.AppendAsync(Told("wakes it"), Ct);

        Assert.Equal(
            [told.Seq, own.Seq, three.Seq, four.Seq],
            (await ledger.ReadAsync(Dev, before.Seq, null, waking.Seq, 100, Ct)).Select(m => m.Seq));

        // The bound keeps the RECENT end, returned oldest first.
        Assert.Equal(
            [three.Seq, four.Seq],
            (await ledger.ReadAsync(Dev, before.Seq, null, waking.Seq, 2, Ct)).Select(m => m.Seq));

        Assert.Empty(await ledger.ReadAsync(Dev, 0, null, long.MaxValue, 0, Ct));
    }

    [Fact]
    public async Task A_workflow_scope_is_applied_inside_the_bound_so_a_busy_workflow_cannot_starve_a_quiet_one()
    {
        var ledger = await CreateAsync();
        var store = (await StoresAsync()).Log;

        var quiet = await store.AppendAsync(Told("quiet"), Ct);
        var busy = await store.AppendAsync(Told("busy"), Ct);
        for (var i = 0; i < 6; i++) await store.AppendAsync(Told($"busy {i}", busy.Seq), Ct);

        Assert.Equal([quiet.Seq], (await ledger.ReadAsync(Dev, 0, quiet.CorrelationId, long.MaxValue, 3, Ct)).Select(m => m.Seq));
        Assert.Equal(3, (await ledger.ReadAsync(Dev, 0, busy.CorrelationId, long.MaxValue, 3, Ct)).Count);
        Assert.Equal(3, (await ledger.ReadAsync(Dev, 0, null, long.MaxValue, 3, Ct)).Count);
    }

    private static NewMessage Told(string text, long? causation = null) =>
        new(MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = text }), "console", causation);

    /// <summary>The installed plugins' events, changed by the test as an install would.</summary>
    private sealed class Plugins : IPluginEventRegistry
    {
        private volatile IReadOnlyList<EventDefinition> _events = [];

        public IReadOnlyList<EventDefinition> Events => _events;

        public EventDefinition? For(string type) => _events.FirstOrDefault(e => e.Type == type);

        public void Install(params EventDefinition[] events) => _events = events;
    }
}

public sealed class SqliteLedgerTests : LedgerContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());
}

public sealed class SqlServerLedgerTests : LedgerContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();
}
