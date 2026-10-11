using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A REAL DEFERRAL IS LISTED AS DEFERRED. The member defers item 2 of a batched run through
/// the runtime; between that run and the item's own next run, the listing `status` shows reads the
/// item back as deferred from exactly that run - off the durable queue the defer wrote, not off a
/// stand-in.
///
/// The team is paused from inside the batched run, so the deferred item is held on the queue where
/// the test can read it instead of starting at once.
/// </summary>
public sealed class DeferredItemQueueListingTests
{
    private static readonly ContainerId Manager = new("Alpha", "Manager");
    private static readonly ContainerId Dev = new("Alpha", "DeveloperTobias");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_item_deferred_from_a_batched_run_is_listed_as_deferred_from_that_run()
    {
        await using var bed = new ContainerTestBed(pending: true);
        var reports = new MemberReports(bed.Host, bed.Store, new RunHeartbeat());
        await bed.AddAsync(Manager, [MessageTypes.Completed, MessageTypes.Failed, MessageTypes.Handback]);
        await bed.AddAsync(Dev);

        var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        MemberReportOutcome? deferral = null;

        bed.Agent.Behaviour = async invocation =>
        {
            if (invocation.Container != Dev) return new AgentResult(0, "noted");

            switch (Interlocked.Increment(ref runs))
            {
                case 1:
                    await firstRun.Task;
                    return new AgentResult(0, "X is done.");
                case 2:
                    deferral = await reports.DeferAsync(Dev, 2, "After X is accepted.", Ct);
                    await bed.Host.SetPausedAsync(Dev.Team, true);
                    return new AgentResult(0, "Answered the first.");
                default:
                    return new AgentResult(0, "Y is done.");
            }
        };

        var x = await bed.Store.AppendAsync(Instruction("do X"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => Volatile.Read(ref runs) == 1));

        await bed.Store.AppendAsync(Instruction("an update on X", x.Seq), Ct);
        var y = await bed.Store.AppendAsync(Instruction("do Y after X\nwith the details", x.Seq), Ct);
        await bed.SettleAsync();
        firstRun.SetResult();

        // The batched run ends and puts Y back on the queue; the pause holds it there.
        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.Pending!.ForAsync(Dev, Ct)).Any(r => r.Seq == y.Seq && !r.Started && r.DeferredFromRun is not null)));
        Assert.True(deferral!.Accepted);

        var batchRun = (await bed.OfTypeAsync(MessageTypes.Started)).Where(r => r.Source == Dev.ToString()).ElementAt(1);

        var entry = Assert.Single(await QueuedInstructions.ForTeamAsync(bed.Pending!, bed.Store, Dev.Team, Dev.Name, Ct));
        Assert.Equal(y.Seq, entry.Seq);
        Assert.Equal(QueuedInstructions.Deferred, entry.State);
        Assert.Equal(batchRun.Seq, entry.DeferredFromRun);
        Assert.Equal("do Y after X", entry.Line);
        Assert.Equal(Manager.ToString(), entry.Source);
        Assert.Equal(x.CorrelationId, entry.Correlation);
        Assert.Equal(2, Volatile.Read(ref runs));

        // Resumed, Y runs as its own run and is no longer waiting.
        await bed.Host.SetPausedAsync(Dev.Team, false);
        Assert.True(await bed.PumpUntilAsync(() => Volatile.Read(ref runs) == 3));
        Assert.True(await bed.PumpUntilAsync(async () =>
            (await QueuedInstructions.ForTeamAsync(bed.Pending!, bed.Store, Dev.Team, Dev.Name, Ct)).Count == 0));
    }

    private static NewMessage Instruction(string text, long? causation = null) =>
        new(MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = text }), Manager.ToString(), causation);
}
