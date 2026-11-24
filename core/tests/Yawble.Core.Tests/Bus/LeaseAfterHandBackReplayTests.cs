using Yawble.Core.Bus;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// Card 566 (B126 verify) replay: Ada hands back in workflow 1, its run ends, Ada at once starts a run in workflow 2
/// that takes the heavy-work lease. The Manager's delivery starts right after the first run ends; nobody is "queued"
/// unless the run queue has them. Fake runners only.
/// </summary>
public class LeaseAfterHandBackReplayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManagerRunsRightAfterTheFirstRunEnds_WhileTheSendersSecondRunHoldsTheLease()
    {
        var second = new TaskCompletionSource();
        var firstGo = new TaskCompletionSource();
        var leased = new TaskCompletionSource<string>();
        long first = 0;
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            if (run.Correlation == first)
            {
                await firstGo.Task.WaitAsync(ct);
                await run.HandbackAsync("committed on members/ada", ct);
                return RunResult.Ok;
            }
            leased.TrySetResult(await run.LeaseAsync("acquire", "full test suite", ct));
            await second.Task.WaitAsync(ct);
            return RunResult.Ok;
        });
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions
        {
            SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false, MaxConcurrentAgents = () => 8,
        });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);

        first = (await engine.PersonTellAsync("Alpha", "Ada", "build it", ct: Ct)).CorrelationId;
        await Wait.UntilAsync(() => { lock (runner.Admitted) return runner.Admitted.Count == 1; }, "Ada's first run");
        var other = (await engine.PersonTellAsync("Alpha", "Ada", "and the other thing", ct: Ct)).CorrelationId;

        firstGo.SetResult();
        await leased.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await Wait.UntilAsync(() => { lock (runner.Admitted) return runner.Admitted.Any(r => r.Member.Role == MemberRole.Manager); },
            "the Manager's run while Ada's second run holds the lease");
        Assert.False(second.Task.IsCompleted);
        RunContext manager;
        lock (runner.Admitted) manager = runner.Admitted.Single(r => r.Member.Role == MemberRole.Manager);
        Assert.Equal(first, manager.Correlation);
        Assert.Contains(manager.Items, i => i.Type == MessageTypes.Handback); // the hand-back itself, not only the run's end

        // Nobody is in the run queue, so no view says queued for a run slot.
        foreach (var s in await engine.TeamStatusAsync("Alpha", Ct)) Assert.NotEqual(WaitStates.Queued, s.WaitState);
        foreach (var wf in new[] { first, other })
            foreach (var w in (await engine.WorkflowRowAsync(wf, Ct))?.Members ?? [])
                Assert.NotEqual(WaitStates.Queued, w.State);

        second.SetResult();
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus to go quiet");
    }
}
