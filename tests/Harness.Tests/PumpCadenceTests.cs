using Harness.Containers;
using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// The pump reads the log only when it moved: a pass over an unchanged log reads the highest seq
/// and nothing else; a subscription or membership change is a reason to read again; each cursor
/// moves once per pass, however many rows the pass delivered.
/// </summary>
public sealed class PumpCadenceTests
{
    private static readonly ContainerId Plug = new("alpha", "plug");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A runner whose run never ends, so no row of its own moves the log.</summary>
    private sealed class Blocking : IMemberRunner
    {
        public int Started;

        public async Task<MemberResult> RunAsync(MemberInvocation invocation, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Started);
            await Task.Delay(Timeout.Infinite, ct);
            return new MemberResult(true, 0, "");
        }
    }

    private static (ContainerHost Host, IMessageLog Log, ICursors Cursors) HostOver(ContainerTestBed bed)
    {
        var log = CountingProxy.Over<IMessageLog>(bed.Store);
        var cursors = CountingProxy.Over<ICursors>(bed.Store);
        ContainerHost host = null!;
        host = new ContainerHost(
            log, cursors, bed.Store, new InMemoryTranscriptStore(),
            onRegistered: (id, ct) => bed.Store.SetAsync(id, host.Find(id)!.Snapshot().Subscribes, ct));
        return (host, log, cursors);
    }

    [Fact]
    public async Task An_idle_pass_reads_the_highest_seq_and_nothing_else()
    {
        await using var bed = new ContainerTestBed();
        var (host, log, _) = HostOver(bed);
        await host.AddAsync(ContainerTestBed.Definition(Plug, ["nothing.ever"]), new Blocking(), Ct);

        await host.PumpOnceAsync(Ct);
        var afterFirst = CountingProxy.Of(log).CallsTo(nameof(IMessageLog.ReadAfterAsync));
        await host.PumpOnceAsync(Ct);
        await host.PumpOnceAsync(Ct);

        Assert.Equal(afterFirst, CountingProxy.Of(log).CallsTo(nameof(IMessageLog.ReadAfterAsync)));
        Assert.True(CountingProxy.Of(log).CallsTo(nameof(IMessageLog.HighestSeqAsync)) >= 3);
    }

    [Fact]
    public async Task A_subscription_change_is_read_on_the_next_pass_without_a_log_change()
    {
        await using var bed = new ContainerTestBed();
        var (host, log, _) = HostOver(bed);
        await host.AddAsync(ContainerTestBed.Definition(Plug, ["nothing.ever"]), new Blocking(), Ct);
        await host.PumpOnceAsync(Ct);
        await host.PumpOnceAsync(Ct);
        var before = CountingProxy.Of(log).CallsTo(nameof(IMessageLog.ReadAfterAsync));

        await bed.Store.SetAsync(Plug, ["nothing.ever", "something.else"], Ct);
        host.Resubscribe(Plug, ["nothing.ever", "something.else"]);
        await host.PumpOnceAsync(Ct);

        Assert.Equal(before + 1, CountingProxy.Of(log).CallsTo(nameof(IMessageLog.ReadAfterAsync)));
    }

    [Fact]
    public async Task A_pass_advances_each_cursor_once_however_many_rows_it_delivered()
    {
        await using var bed = new ContainerTestBed();
        var (host, _, cursors) = HostOver(bed);
        await host.AddAsync(ContainerTestBed.Definition(Plug), new Blocking(), Ct);
        for (var i = 0; i < 3; i++)
        {
            await bed.Store.AppendAsync(new NewMessage(MessageTypes.InstructionFor(Plug), $$"""{"instruction":"{{i}}"}""", "console"), Ct);
        }
        CountingProxy.Of(cursors).Calls.Clear();

        var delivered = await host.PumpOnceAsync(Ct);

        Assert.Equal(3, delivered);
        Assert.Equal(1, CountingProxy.Of(cursors).CallsTo(nameof(ICursors.AdvanceAsync)));
        var position = await bed.Store.PositionAsync(Plug, Ct);
        Assert.Equal(await bed.Store.HighestSeqAsync(Ct), position);
    }

    [Fact]
    public async Task A_pass_that_filled_its_batch_reads_again_without_a_log_change()
    {
        await using var bed = new ContainerTestBed();
        var (host, log, _) = HostOver(bed);
        await host.AddAsync(ContainerTestBed.Definition(Plug), new Blocking(), Ct);
        for (var i = 0; i < 40; i++)
        {
            await bed.Store.AppendAsync(new NewMessage(MessageTypes.InstructionFor(Plug), $$"""{"instruction":"{{i}}"}""", "console"), Ct);
        }

        var first = await host.PumpOnceAsync(Ct);
        var reads = CountingProxy.Of(log).CallsTo(nameof(IMessageLog.ReadAfterAsync));
        var second = await host.PumpOnceAsync(Ct);

        Assert.True(first < 40, $"the first pass delivered {first}: the batch was not the bound");
        Assert.Equal(40 - first, second);
        Assert.Equal(reads + 1, CountingProxy.Of(log).CallsTo(nameof(IMessageLog.ReadAfterAsync)));
    }

    [Fact]
    public async Task A_resumed_team_is_read_on_the_next_pass_without_a_log_change()
    {
        await using var bed = new ContainerTestBed();
        var (host, log, _) = HostOver(bed);
        await host.AddAsync(ContainerTestBed.Definition(Plug), new Blocking(), Ct);
        await host.SetPausedAsync(Plug.Team, true);
        await bed.Store.AppendAsync(new NewMessage(MessageTypes.InstructionFor(Plug), """{"instruction":"go"}""", "console"), Ct);
        Assert.Equal(0, await host.PumpOnceAsync(Ct));
        Assert.Equal(0, await host.PumpOnceAsync(Ct));

        await host.SetPausedAsync(Plug.Team, false);

        Assert.Equal(1, await host.PumpOnceAsync(Ct));
    }
}
