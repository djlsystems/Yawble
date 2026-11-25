using System.Text.Json;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// A hand-back waits only for the run that sent it (not the sender's next run in another workflow), and the
/// undelivered work's real waiting states: queued (run queue) | waits-for-run (waitsFor: the sender) | held | pending,
/// on the Workflows page's members, in team status and the agent cards, the companion's text and the timeline (only a
/// real run-queue wait is Waiting for a slot). Fake runners only.
/// </summary>
public class HandbackOwnRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BusEngine Engine(TempDb db, ScriptedRunner runner, int slots = 8) =>
        new(db.OpenStore(), runner, new BusOptions
        {
            SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false, MaxConcurrentAgents = () => slots,
        });

    private static async Task<BusEngine> Team(TempDb db, ScriptedRunner runner, int slots = 8)
    {
        var engine = Engine(db, runner, slots);
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);
        return engine;
    }

    private static RunContext[] ManagerRuns(ScriptedRunner runner)
    {
        lock (runner.Admitted) return [.. runner.Admitted.Where(r => r.Member.Role == MemberRole.Manager)];
    }

    private static async Task<WorkflowWorker?> Worker(BusEngine engine, long workflow, string name) =>
        (await engine.WorkflowRowAsync(workflow, Ct))?.Members.FirstOrDefault(m => m.Name == name);

    private static async Task<MemberStatus> Status(BusEngine engine, string name) =>
        (await engine.TeamStatusAsync("Alpha", Ct)).Single(s => s.Name == name);

    // ---------------------------------------------------------------- bug 1: the hand-back's own run

    [Fact]
    public async Task AHandbackWhoseRunEnded_ReachesTheManager_WithoutWaitingForTheSendersNextRunInAnotherWorkflow()
    {
        var second = new TaskCompletionSource();
        var firstGo = new TaskCompletionSource();
        long first = 0;
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            if (run.Correlation == first)
            {
                await firstGo.Task.WaitAsync(ct);
                await run.HandbackAsync("committed on members/ada", ct);
                return RunResult.Ok;
            }
            await second.Task.WaitAsync(ct); // Ada's run in workflow 2 goes on and on
            return RunResult.Ok;
        });
        using var db = new TempDb();
        await using var engine = await Team(db, runner);
        first = (await engine.PersonTellAsync("Alpha", "Ada", "build it", ct: Ct)).CorrelationId;
        await Wait.UntilAsync(() => { lock (runner.Admitted) return runner.Admitted.Count == 1; }, "Ada's first run");
        var other = (await engine.PersonTellAsync("Alpha", "Ada", "and the other thing", ct: Ct)).CorrelationId;
        Assert.NotEqual(first, other);

        firstGo.SetResult(); // Ada hands back, its run ends, and its run in workflow 2 starts at once
        await Wait.UntilAsync(() => ManagerRuns(runner).Length == 1, "the Manager's run while Ada's second run is still going");
        Assert.Contains(runner.Admitted, r => r.Member.Name == "Ada" && r.Correlation == other);
        Assert.False(second.Task.IsCompleted);
        var manager = Assert.Single(ManagerRuns(runner));
        Assert.Equal(first, manager.Correlation);
        Assert.Equal([MessageTypes.Handback, MessageTypes.Completed], manager.Items.Select(i => i.Type)); // with the run's ending row

        second.SetResult();
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus to go quiet");
    }

    [Fact]
    public async Task AHandbackWhoseOwnRunIsActive_WaitsForIt_ShownAsWaitsForRun_ThenArrivesWithTheRunsEnd_InOneRun()
    {
        var release = new TaskCompletionSource();
        var handedBack = new TaskCompletionSource();
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            await run.HandbackAsync("committed on members/ada", ct);
            handedBack.TrySetResult();
            await release.Task.WaitAsync(ct); // the host's fetch-back happens in here, before the run ends
            return RunResult.Ok;
        });
        using var db = new TempDb();
        await using var engine = await Team(db, runner);
        var wf = (await engine.PersonTellAsync("Alpha", "Ada", "build it", ct: Ct)).CorrelationId;
        await handedBack.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await Task.Delay(600, Ct); // several pump passes while Ada's run is still open
        Assert.Empty(ManagerRuns(runner));

        // The hand-back names its run.
        using (var store = db.OpenStore())
        {
            var handback = Assert.Single(store.AllMessages(), m => m.Type == MessageTypes.Handback);
            var adaRun = runner.Admitted.Single(r => r.Member.Name == "Ada");
            Assert.Equal(adaRun.RunId, JsonDocument.Parse(handback.Payload).RootElement.GetProperty("runId").GetInt64());
        }

        // waits-for-run, with the sender's name, on the Workflows page, in team status and on the agent card.
        await Wait.UntilAsync(async () => (await Worker(engine, wf, "Manager"))?.State == WaitStates.WaitsForRun, "the Manager waits for Ada's run");
        var worker = (await Worker(engine, wf, "Manager"))!;
        Assert.Equal("Ada", worker.WaitsFor);
        Assert.Equal(BusEngine.WorkflowStatuses.Running, (await engine.WorkflowRowAsync(wf, Ct))!.Status); // Ada's run still works it
        var status = await Status(engine, "Manager");
        Assert.Equal((1, WaitStates.WaitsForRun, "Ada", "idle"), (status.QueuedInstructions, status.WaitState, status.WaitsFor, status.State));
        var card = (await engine.AgentCardsAsync((await engine.TeamsAsync(Ct)).Single().Id, Ct)).Single(c => c.Member.Name == "Manager");
        Assert.Equal((WaitStates.WaitsForRun, "Ada"), (card.WaitState, card.WaitsFor));
        // The card's own state says it too (the shell's team list and the cloud read state, not waitState).
        Assert.Equal(WaitStates.WaitsForRun, card.State);
        var wire = JsonSerializer.SerializeToNode(card, Yawble.Core.Ipc.RpcDispatcher.Json)!; // teams.agents' row, as UiRpc sends it
        Assert.Equal((WaitStates.WaitsForRun, "Ada"), (wire["state"]!.GetValue<string>(), wire["waitsFor"]!.GetValue<string>()));

        release.SetResult();
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus to go quiet");
        var manager = Assert.Single(ManagerRuns(runner));
        Assert.Equal([MessageTypes.Handback, MessageTypes.Completed], manager.Items.Select(i => i.Type));
    }

    [Fact]
    public async Task AfterARestart_AnUnstartedHandbackWhoseRunEndedBefore_IsRipe()
    {
        var release = new TaskCompletionSource();
        var handedBack = new TaskCompletionSource();
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            await run.HandbackAsync("committed on members/ada", ct);
            handedBack.TrySetResult();
            await release.Task.WaitAsync(ct);
            return RunResult.Ok;
        });
        using var db = new TempDb();
        long wf;
        await using (var engine = await Team(db, runner))
        {
            wf = (await engine.PersonTellAsync("Alpha", "Ada", "build it", ct: Ct)).CorrelationId;
            await handedBack.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            await engine.PauseTeamAsync("Alpha", Ct); // the run going finishes; the Manager's delivery waits in place
            release.SetResult();
            await Wait.UntilAsync(async () => (await Status(engine, "Ada")).State == "idle", "Ada's run to end");
            // held: the team is paused.
            await Wait.UntilAsync(async () => (await Worker(engine, wf, "Manager"))?.State == WaitStates.Held, "the Manager's delivery held");
            Assert.Null((await Worker(engine, wf, "Manager"))!.WaitsFor);
            Assert.Equal(WaitStates.Held, (await engine.WorkflowRowAsync(wf, Ct))!.WaitingFor);
            Assert.Equal(WaitStates.Held, (await Status(engine, "Manager")).WaitState);
            var held = await Card(engine, "Manager");
            Assert.Equal((WaitStates.Held, (string?)null), (held.State, held.WaitsFor));
            Assert.Empty(ManagerRuns(runner));
        }

        var after = new ScriptedRunner();
        await using var restarted = Engine(db, after);
        await restarted.StartAsync(Ct);
        await restarted.ResumeTeamAsync("Alpha", Ct);
        await Wait.UntilAsync(() => ManagerRuns(after).Length == 1, "the Manager's run after the restart");
        var manager = Assert.Single(ManagerRuns(after));
        Assert.Equal(wf, manager.Correlation);
        Assert.Equal([MessageTypes.Handback, MessageTypes.Completed], manager.Items.Select(i => i.Type));
    }

    // ---------------------------------------------------------------- bug 2: the waiting states

    [Fact]
    public async Task Queued_OnlyAMemberInTheRunQueue_IsQueuedForASlot_InEveryView()
    {
        var release = new TaskCompletionSource();
        var runner = new ScriptedRunner().On("Alpha/Ada", async (_, ct) => { await release.Task.WaitAsync(ct); return RunResult.Ok; });
        using var db = new TempDb();
        await using var engine = await Team(db, runner, slots: 1);
        await engine.PersonTellAsync("Alpha", "Ada", "hold the slot", ct: Ct);
        await Wait.UntilAsync(() => { lock (runner.Admitted) return runner.Admitted.Count == 1; }, "Ada's run");
        var wf = (await engine.PersonTellAsync("Alpha", "Manager", "plan the next thing", ct: Ct)).CorrelationId;

        await Wait.UntilAsync(async () => (await Worker(engine, wf, "Manager"))?.State == WaitStates.Queued, "the Manager queued for the slot");
        Assert.Null((await Worker(engine, wf, "Manager"))!.WaitsFor);
        var row = (await engine.WorkflowRowAsync(wf, Ct))!;
        Assert.Equal((BusEngine.WorkflowStatuses.Waiting, "slot"), (row.Status, row.WaitingFor));
        var status = await Status(engine, "Manager");
        Assert.Equal(("queued", 1, WaitStates.Queued), (status.State, status.QueuedInstructions, status.WaitState));
        Assert.Equal(WaitStates.Queued, (await Card(engine, "Manager")).State);
        Assert.Equal("running", (await Card(engine, "Ada")).State);

        // The timeline's live wait: a member in the run queue now is Waiting for a slot for that workflow.
        var live = await engine.LiveActivityAsync(Ct);
        var entry = Assert.Single(live, a => a.Member == "Manager");
        Assert.Equal(wf, entry.Workflow);
        Assert.NotNull(entry.QueuedSince);

        release.SetResult();
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus to go quiet");
    }

    [Fact]
    public async Task Pending_WorkForAMemberRunningElsewhere_IsNotQueuedForASlot()
    {
        var release = new TaskCompletionSource();
        long first = 0;
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            if (run.Correlation == first) await release.Task.WaitAsync(ct);
            return RunResult.Ok;
        });
        using var db = new TempDb();
        await using var engine = await Team(db, runner);
        first = (await engine.PersonTellAsync("Alpha", "Ada", "the long one", ct: Ct)).CorrelationId;
        await Wait.UntilAsync(() => { lock (runner.Admitted) return runner.Admitted.Count == 1; }, "Ada's run");
        var wf = (await engine.PersonTellAsync("Alpha", "Ada", "the next one", ct: Ct)).CorrelationId;

        await Wait.UntilAsync(async () => (await Worker(engine, wf, "Ada"))?.State == WaitStates.Pending, "Ada's second delivery pending");
        var row = (await engine.WorkflowRowAsync(wf, Ct))!;
        Assert.Equal((BusEngine.WorkflowStatuses.Waiting, WaitStates.Pending), (row.Status, row.WaitingFor));
        var status = await Status(engine, "Ada");
        Assert.Equal(("running", 1, WaitStates.Pending), (status.State, status.QueuedInstructions, status.WaitState));
        Assert.Equal("running", (await Card(engine, "Ada")).State); // running wins over its own work waiting
        Assert.DoesNotContain(await engine.LiveActivityAsync(Ct), a => a.QueuedSince is not null);

        release.SetResult();
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus to go quiet");
    }

    [Fact]
    public async Task NoWaitingWork_NoWaitState()
    {
        using var db = new TempDb();
        await using var engine = await Team(db, new ScriptedRunner());
        var ada = await Status(engine, "Ada");
        Assert.Equal((0, (string?)null, (string?)null), (ada.QueuedInstructions, ada.WaitState, ada.WaitsFor));
        var card = await Card(engine, "Ada");
        Assert.Equal(("idle", (string?)null), (card.State, card.WaitsFor));
    }

    private static async Task<AgentCard> Card(BusEngine engine, string member) =>
        (await engine.AgentCardsAsync((await engine.TeamsAsync(Ct)).Single().Id, Ct)).Single(c => c.Member.Name == member);

    [Theory]
    [InlineData(WaitStates.Queued, null, "queued for a run slot")]
    [InlineData(WaitStates.WaitsForRun, "TesterWren", "waits for TesterWren's run to end")]
    [InlineData(WaitStates.Held, null, "waits: team paused")]
    [InlineData(WaitStates.Pending, null, "has work waiting")]
    public void EachStateInWords(string state, string? waitsFor, string expected) =>
        Assert.Equal(expected, WaitStates.Text(state, waitsFor));

    [Fact]
    public void TheWireNames() =>
        Assert.Equal(["queued", "waits-for-run", "held", "pending"], WaitStates.All);

    // ---------------------------------------------------------------- the timeline's Waiting for a slot

    private static DateTimeOffset At(string hhmm) => Yawble.Core.Tests.Board.TimelineCalculatorTests.At(hhmm);

    private static long Share(TimeSummary t, string category) => t.Breakdown.FirstOrDefault(b => b.Category == category)?.Ms ?? 0;

    private static CardTimelineInput Card(IReadOnlyList<RunSpan> runs) =>
        new(1, "Ada", "card", At("10:00"), At("10:00"), null, null, false,
            [new TimelineState(At("10:00"), TimeCategories.WaitingForSlot, false, "Ada")], [], [], runs, []);

    [Fact]
    public void Timeline_ACardWithWorkButNoRunInTheQueue_IsWaitingOnATeammate_NotWaitingForASlot()
    {
        var t = TimelineCalculator.Card(Card([]), At("10:30"));
        Assert.Equal(0, Share(t.Time, TimeCategories.WaitingForSlot));
        Assert.Equal(30 * 60_000, Share(t.Time, TimeCategories.WaitingOnTeammate));
    }

    [Fact]
    public void Timeline_OnlyTheRunQueueWait_IsWaitingForASlot_EndedOrLive()
    {
        // Told 10:00; Ada busy elsewhere until 10:10, then in the run queue 10:10 - 10:15, then working until 10:30.
        var ended = TimelineCalculator.Card(Card([new RunSpan("Ada", At("10:10"), At("10:15"), At("10:30"))]), At("10:30"));
        Assert.Equal(5 * 60_000, Share(ended.Time, TimeCategories.WaitingForSlot));
        Assert.Equal(10 * 60_000, Share(ended.Time, TimeCategories.WaitingOnTeammate));
        Assert.Equal(15 * 60_000, Share(ended.Time, TimeCategories.AgentWorking));

        // Still in the run queue now (a live entry: not started, started = now).
        var live = TimelineCalculator.Card(Card([new RunSpan("Ada", At("10:10"), At("10:20"), null)]), At("10:20"));
        Assert.Equal(10 * 60_000, Share(live.Time, TimeCategories.WaitingForSlot));
        Assert.Equal(10 * 60_000, Share(live.Time, TimeCategories.WaitingOnTeammate));
        Assert.Equal(0, Share(live.Time, TimeCategories.AgentWorking));
    }
}
