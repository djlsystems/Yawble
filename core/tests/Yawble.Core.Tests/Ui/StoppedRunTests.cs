using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Link;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Scheduling;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>
/// A run the person stopped is recorded failed with failure class "stopped" (runs.state is left
/// as it is), and everything the window and the Cloud Link get says "stopped", not "failed": the member's attention
/// ("stopped", below failed), lastRunFailureClass on every member node, the feed kind, failureClass on a run's end row,
/// the remote summary (not errored, not a failed deliverable), and no Needs-you item (board, Concierge watch).
/// </summary>
public class StoppedRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HashSet<string> Nobody = [];
    private static readonly Dictionary<string, Message> NoReports = new(StringComparer.OrdinalIgnoreCase);

    private static AgentCard Card(string? lastRun, string? failureClass, string state = "idle", string name = "Tomas") =>
        new(new MemberRow(1, 1, "web", name, "member", "claude", "", "", "default"), state, null, 0, 0, null, null, null, lastRun, null,
            LastRunFailureClass: failureClass);

    private static Message M(string type, object payload, string source = "web/Tomas", long seq = 7, long deliverable = 3) =>
        new(seq, type, JsonSerializer.Serialize(payload), source, deliverable, null, 0, DateTimeOffset.Parse("2026-11-18T12:00:00Z"));

    // ------------------------------------------------------------------ attention

    [Fact]
    public void AStoppedRunGivesAttentionStopped_TheOtherFailureClassesStayFailed()
    {
        Assert.Equal("stopped", UiRpc.Attention(Card("failed", FailureClasses.Stopped), Nobody, NoReports));
        foreach (var fc in new[] { FailureClasses.Timeout, FailureClasses.Idle, FailureClasses.Credential, FailureClasses.StartFailed, FailureClasses.Failed, null })
            Assert.Equal("failed", UiRpc.Attention(Card("failed", fc), Nobody, NoReports));
        // An interrupted run (runs.state 'interrupted', class interrupted) is unchanged: no ring.
        Assert.Null(UiRpc.Attention(Card("interrupted", FailureClasses.Interrupted), Nobody, NoReports));
        Assert.Null(UiRpc.Attention(Card("completed", null), Nobody, NoReports));
        Assert.Null(UiRpc.Attention(Card("failed", FailureClasses.Stopped, state: "running"), Nobody, NoReports)); // running again
    }

    [Fact]
    public void StoppedIsTheQuietest_DecisionAndBlockedComeFirst()
    {
        var stopped = Card("failed", FailureClasses.Stopped);
        Assert.Equal("decision", UiRpc.Attention(stopped, new HashSet<string>(["tomas"], StringComparer.OrdinalIgnoreCase), NoReports));
        var reports = new Dictionary<string, Message>(StringComparer.OrdinalIgnoreCase) { ["Tomas"] = M(MessageTypes.Blocked, new { reason = "no access" }) };
        Assert.Equal("blocked", UiRpc.Attention(stopped, Nobody, reports));
    }

    // ------------------------------------------------------------------ the Monitor's feed

    [Fact]
    public void TheFeedKindIsStoppedForAStoppedRun_AndFailedForAFailedOne_EachWithItsFailureClass()
    {
        var stopped = UiRpc.MonitorRow(M(MessageTypes.Failed, new { reason = BusEngine.StoppedReason, failureClass = FailureClasses.Stopped }))!;
        Assert.Equal("stopped", stopped["kind"]!.GetValue<string>());
        Assert.Equal(FailureClasses.Stopped, stopped["failureClass"]!.GetValue<string>());

        var failed = UiRpc.MonitorRow(M(MessageTypes.Failed, new { reason = "wall-clock timeout", failureClass = FailureClasses.Timeout }))!;
        Assert.Equal("failed", failed["kind"]!.GetValue<string>());
        Assert.Equal(FailureClasses.Timeout, failed["failureClass"]!.GetValue<string>());

        var old = UiRpc.MonitorRow(M(MessageTypes.Failed, new { reason = "crashed" }))!; // a notice from before failureClass
        Assert.Equal(("failed", FailureClasses.Failed), (old["kind"]!.GetValue<string>(), old["failureClass"]!.GetValue<string>()));

        var completed = UiRpc.MonitorRow(M(MessageTypes.Completed, new { reason = (string?)null }))!;
        Assert.True(completed.ContainsKey("failureClass"));
        Assert.Null(completed["failureClass"]);
    }

    // ------------------------------------------------------------------ Needs you

    [Fact]
    public void TheConciergeWatchRaisesNothingForAStoppedRun_ButStillForAFailedOne()
    {
        Assert.Null(ConciergeWatcher.Describe(M(MessageTypes.Failed, new { reason = BusEngine.StoppedReason, failureClass = FailureClasses.Stopped }), "web"));
        var timeout = ConciergeWatcher.Describe(M(MessageTypes.Failed, new { reason = "wall-clock timeout", failureClass = FailureClasses.Timeout }), "web");
        Assert.Equal("failed", timeout!.Value.Kind);
    }

    [Fact]
    public void AStoppedRunLeavesTheCardOutOfNeedsYou_AFailedRunStillPutsItThere()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var p = new BoardProjector(store);
        p.Apply(M("agent.instruction.web/Tomas", new { text = "Build it" }, source: "web/Manager", seq: 1, deliverable: 5));
        p.Apply(M("agent.instruction.web/Ines", new { text = "Test it" }, source: "web/Manager", seq: 2, deliverable: 5));
        p.Apply(M(MessageTypes.Progress, new { status = "coding" }, source: "web/Tomas", seq: 3, deliverable: 5));
        p.Apply(M(MessageTypes.Failed, new { reason = BusEngine.StoppedReason, failureClass = FailureClasses.Stopped }, source: "web/Tomas", seq: 4, deliverable: 5));
        p.Apply(M(MessageTypes.Failed, new { reason = "wall-clock timeout", failureClass = FailureClasses.Timeout }, source: "web/Ines", seq: 5, deliverable: 5));

        var cards = store.Cards("web").ToDictionary(c => c.Member!);
        Assert.Equal(("stopped", Lanes.InProgress), (cards["Tomas"].Status, cards["Tomas"].Lane));
        Assert.Contains(store.Trail(cards["Tomas"].Id), t => t.Kind == "stopped");
        Assert.DoesNotContain(store.Trail(cards["Tomas"].Id), t => t.Kind == "failed");
        Assert.Equal(("failed", Lanes.NeedsYou), (cards["Ines"].Status, cards["Ines"].Lane));
    }

    // ------------------------------------------------------------------ the card's own status (round 2)

    [Fact]
    public void StoppedIsAKnownStatus_InProgress_NeverNeedsYou_AndQuiet()
    {
        Assert.Equal("stopped", Lanes.Stopped);
        Assert.Contains(Lanes.Stopped, Lanes.Statuses);
        Assert.Equal(Lanes.InProgress, Lanes.Of(Lanes.Stopped));
        Assert.Equal(0, Lanes.Loudness(Lanes.Stopped));
        Assert.Equal(TimeCategories.Idle, TimeCategories.OfState("Tomas", Lanes.Stopped, Lanes.InProgress));
    }

    [Fact]
    public void AQueuedCardStopped_SaysStopped_AndStaysInToDo()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var p = new BoardProjector(store);
        p.Apply(M("agent.instruction.web/Tomas", new { text = "Build it" }, source: "web/Manager", seq: 1, deliverable: 5));
        p.Apply(M(MessageTypes.Failed, new { reason = BusEngine.StoppedReason, failureClass = FailureClasses.Stopped }, source: "web/Tomas", seq: 2, deliverable: 5));
        var card = store.Cards("web").Single();
        Assert.Equal(("stopped", Lanes.Todo), (card.Status, card.Lane));
    }

    [Fact]
    public void ANewInstructionAfterAStop_ReQueuesTheCard()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var p = new BoardProjector(store);
        p.Apply(M("agent.instruction.web/Tomas", new { text = "Build it" }, source: "web/Manager", seq: 1, deliverable: 5));
        p.Apply(M(MessageTypes.Progress, new { status = "coding" }, source: "web/Tomas", seq: 2, deliverable: 5));
        p.Apply(M(MessageTypes.Failed, new { reason = BusEngine.StoppedReason, failureClass = FailureClasses.Stopped }, source: "web/Tomas", seq: 3, deliverable: 5));
        Assert.Equal("stopped", store.Cards("web").Single().Status);

        p.Apply(M("agent.instruction.web/Tomas", new { text = "Carry on" }, source: "web/Manager", seq: 4, deliverable: 5));
        var card = store.Cards("web").Single();
        Assert.Equal(("queued", Lanes.Todo), (card.Status, card.Lane));
        p.Apply(M(MessageTypes.Progress, new { status = "coding again" }, source: "web/Tomas", seq: 5, deliverable: 5));
        Assert.Equal(("running", Lanes.InProgress), (store.Cards("web").Single().Status, store.Cards("web").Single().Lane));
    }

    [Fact]
    public void AStopKeeps_AQuestionStillOpen_AndAHandedBackCard()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var p = new BoardProjector(store);
        p.Apply(M("agent.instruction.web/Tomas", new { text = "Build it" }, source: "web/Manager", seq: 1, deliverable: 5));
        p.Apply(M("agent.instruction.web/Ines", new { text = "Test it" }, source: "web/Manager", seq: 2, deliverable: 5));
        p.Apply(M(MessageTypes.NeedsDecision, new { question = "MIT?" }, source: "web/Tomas", seq: 3, deliverable: 5));
        p.Apply(M(MessageTypes.Handback, new { delivered = "tested" }, source: "web/Ines", seq: 4, deliverable: 5));
        foreach (var (who, seq) in new[] { ("Tomas", 5L), ("Ines", 6L) })
            p.Apply(M(MessageTypes.Failed, new { reason = BusEngine.StoppedReason, failureClass = FailureClasses.Stopped }, source: "web/" + who, seq: seq, deliverable: 5));
        var cards = store.Cards("web").ToDictionary(c => c.Member!);
        Assert.Equal(("needs-decision", Lanes.NeedsYou), (cards["Tomas"].Status, cards["Tomas"].Lane));
        Assert.Equal(("handback", Lanes.Done), (cards["Ines"].Status, cards["Ines"].Lane));
        Assert.Contains(store.Trail(cards["Ines"].Id), t => t.Kind == "stopped");
    }

    // ------------------------------------------------------------------ the remote summary

    [Fact]
    public void TheRemoteSummaryCountsAStoppedAgentAsIdleAndStopped_NotErrored()
    {
        var facts = new OrgSummaryFacts(new OrgComputer("DESK", null, "1", "Windows", DateTimeOffset.UtcNow, 0, 4, 0),
            [new OrgTeam("web", false, false, [
                new OrgAgent("Tomas", "Member", "idle", LastRunFailed: false, LastRunStopped: true),
                new OrgAgent("Ines", "Member", "idle", LastRunFailed: true),
                new OrgAgent("Wren", "Member", "idle", LastRunFailed: false),
            ])], [], [], []);
        var agents = OrgSummary.Build(facts, DateTimeOffset.UtcNow)["teams"]![0]!["agents"]!;
        Assert.Equal((2, 1, 1), (agents["idle"]!.GetValue<int>(), agents["errored"]!.GetValue<int>(), agents["stopped"]!.GetValue<int>()));
    }

    // ------------------------------------------------------------------ end to end on the bus

    /// <summary>
    /// A real engine: one run stopped by the person, one timed out, one completed. The member cards carry the failure
    /// class; teams.agents' nodes say attention stopped / failed and lastRunFailureClass; the Cloud Link's summary
    /// counts the stopped one as idle (stopped), never errored, and its deliverable is not "failed".
    /// </summary>
    [Fact]
    public async Task AStoppedRunOnTheBus_IsStoppedOnTheCard_InTheNodes_AndInTheRemoteSummary()
    {
        using var db = new TempDb();
        var runner = new ScriptedRunner();
        runner.On("Alpha/stop", async (run, ct) => { await Task.Delay(Timeout.Infinite, ct); return RunResult.Ok; })
            .On("Alpha/slow", (run, ct) => Task.FromResult(new RunResult(false, "wall-clock timeout (15 min)", FailureClasses.Timeout)))
            .On("Alpha/fine", (run, ct) => Task.FromResult(RunResult.Ok));
        var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { MaxConcurrentAgents = () => 3, SafetyPoll = TimeSpan.FromSeconds(30) });
        try
        {
            await engine.CreateTeamAsync("Alpha", ct: Ct);
            foreach (var n in new[] { "stop", "slow", "fine" }) await engine.AddMemberAsync("Alpha", n, ct: Ct);
            foreach (var n in new[] { "stop", "slow", "fine" }) await engine.PersonTellAsync("Alpha", n, "job " + n, ct: Ct);
            await engine.StartAsync(Ct);
            await Wait.UntilAsync(() => runner.AdmittedNames().Contains("Alpha/stop"), "stop running");
            await Wait.UntilAsync(async () => await engine.StopMemberAsync("Alpha", "stop", Ct), "stop asked");
            await Wait.UntilAsync(async () => await engine.IsQuiescentAsync(Ct) && runner.AdmittedNames().Count(n => n != "Alpha/Manager") == 3,
                "all three ran", TimeSpan.FromSeconds(20));

            var teamId = (await engine.TeamsAsync(Ct)).Single(t => t.Name == "Alpha").Id;
            var cards = (await engine.AgentCardsAsync(teamId, Ct)).ToDictionary(c => c.Member.Name);
            Assert.Equal(("failed", FailureClasses.Stopped), (cards["stop"].LastRunState, cards["stop"].LastRunFailureClass));
            Assert.Equal(("failed", FailureClasses.Timeout), (cards["slow"].LastRunState, cards["slow"].LastRunFailureClass));
            Assert.Equal(("completed", (string?)null), (cards["fine"].LastRunState, cards["fine"].LastRunFailureClass));
            Assert.Equal("stopped", UiRpc.Attention(cards["stop"], Nobody, NoReports));
            Assert.Equal("failed", UiRpc.Attention(cards["slow"], Nobody, NoReports));
            Assert.Null(UiRpc.Attention(cards["fine"], Nobody, NoReports));

            // The member node as the window gets it: every one carries lastRunFailureClass (null when none).
            var nodes = Ipc.RpcDispatcher.Json is { } json
                ? cards.Values.Select(c => JsonSerializer.SerializeToNode(c, json)!.AsObject()).ToDictionary(n => n["member"]!["name"]!.GetValue<string>())
                : throw new InvalidOperationException();
            Assert.Equal(FailureClasses.Stopped, nodes["stop"]["lastRunFailureClass"]!.GetValue<string>());
            Assert.True(nodes["fine"].ContainsKey("lastRunFailureClass"));
            Assert.Null(nodes["fine"]["lastRunFailureClass"]);
            Assert.False(nodes["stop"].ContainsKey("lastRunStopped")); // helpers stay off the wire

            // The Cloud Link: stopped is not errored, not a failed deliverable, and its summary says stopped.
            var facts = await new AppOrgSummarySource(engine, null, null, null, null, null, () => null, DateTimeOffset.UtcNow).ReadAsync(Ct);
            var agents = facts.Teams.Single().Agents.ToDictionary(a => a.Member);
            Assert.Equal((false, true), (agents["stop"].LastRunFailed, agents["stop"].LastRunStopped));
            Assert.Equal((true, false), (agents["slow"].LastRunFailed, agents["slow"].LastRunStopped));
            var summary = OrgSummary.Build(facts, DateTimeOffset.UtcNow);
            var counted = summary["teams"]![0]!["agents"]!;
            Assert.Equal(1, counted["errored"]!.GetValue<int>());
            Assert.Equal(1, counted["stopped"]!.GetValue<int>());
            Assert.DoesNotContain(facts.Deliverables, w => w.Owner == "stop" && w.State == OrgSummary.Failed);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }
}
