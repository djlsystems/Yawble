using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Link;
using Yawble.Core.Storage;
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
/// On the board the stopped run's card has its own status "stopped": only that run's card is marked, Done and louder
/// statuses keep theirs (Lanes.StopKeeps), and a new instruction queues the same card again. Migration 0048, which
/// lets board_cards hold the status, is run as an upgrade of a database at 0047 with data.
/// </summary>
public class StoppedRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HashSet<string> Nobody = [];
    private static readonly Dictionary<string, Message> NoReports = new(StringComparer.OrdinalIgnoreCase);

    private static AgentCard Card(string? lastRun, string? failureClass, string state = "idle", string name = "Tomas") =>
        new(new MemberRow(1, 1, "web", name, "member", "claude", "", "", "default"), state, null, 0, 0, null, null, null, lastRun, null,
            LastRunFailureClass: failureClass);

    private static Message Stopped(string member, long seq, long deliverable) =>
        M(MessageTypes.Failed, new { reason = BusEngine.StoppedReason, failureClass = FailureClasses.Stopped }, source: "web/" + member, seq: seq, deliverable: deliverable);

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

        var unclassed = UiRpc.MonitorRow(M(MessageTypes.Failed, new { reason = "crashed" }))!; // a notice with no failureClass
        Assert.Equal(("failed", FailureClasses.Failed), (unclassed["kind"]!.GetValue<string>(), unclassed["failureClass"]!.GetValue<string>()));

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

    // ------------------------------------------------------------------ the card's own status

    [Fact]
    public void StoppedIsAKnownStatus_InProgress_NeverNeedsYou_AndQuiet()
    {
        Assert.Equal("stopped", Lanes.Stopped);
        Assert.Contains(Lanes.Stopped, Lanes.Statuses);
        Assert.Equal(Lanes.InProgress, Lanes.Of(Lanes.Stopped));
        Assert.Equal(0, Lanes.Loudness(Lanes.Stopped));
        Assert.Equal(TimeCategories.Idle, TimeCategories.OfState("Tomas", Lanes.Stopped, Lanes.InProgress));
        Assert.True(Lanes.StopKeeps("handback", Lanes.Done));
        Assert.True(Lanes.StopKeeps("failed", Lanes.NeedsYou));
        Assert.False(Lanes.StopKeeps("running", Lanes.InProgress));
        Assert.False(Lanes.StopKeeps("queued", Lanes.Todo));
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

    [Fact]
    public void AStopKeeps_ABlockedCard()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var p = new BoardProjector(store);
        p.Apply(M("agent.instruction.web/Tomas", new { text = "Build it" }, source: "web/Manager", seq: 1, deliverable: 5));
        p.Apply(M(MessageTypes.Blocked, new { reason = "no access" }, source: "web/Tomas", seq: 2, deliverable: 5));
        p.Apply(Stopped("Tomas", 3, 5));
        var card = store.CardOf("web", "Tomas", 5)!;
        Assert.Equal(("blocked", Lanes.NeedsYou), (card.Status, card.Lane));
    }

    [Fact]
    public void AStopMarksOnlyThatRunsCard_NotTheMembersOlderCard()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var p = new BoardProjector(store);
        p.Apply(M("agent.instruction.web/Tomas", new { text = "Old work" }, source: "web/Manager", seq: 1, deliverable: 4));
        p.Apply(M(MessageTypes.Progress, new { status = "older" }, source: "web/Tomas", seq: 2, deliverable: 4)); // the older card, still running
        p.Apply(M("agent.instruction.web/Tomas", new { text = "New work" }, source: "web/Manager", seq: 3, deliverable: 5));
        p.Apply(M(MessageTypes.Progress, new { status = "coding" }, source: "web/Tomas", seq: 4, deliverable: 5));
        p.Apply(M("agent.instruction.web/Ines", new { text = "Other work" }, source: "web/Manager", seq: 5, deliverable: 5));
        p.Apply(Stopped("Tomas", 6, 5));

        var old = store.CardOf("web", "Tomas", 4)!;
        var cur = store.CardOf("web", "Tomas", 5)!;
        var ines = store.CardOf("web", "Ines", 5)!;
        Assert.Equal(("running", Lanes.InProgress), (old.Status, old.Lane));
        Assert.Equal(("stopped", Lanes.InProgress), (cur.Status, cur.Lane));
        Assert.Equal("queued", ines.Status);
        Assert.Equal("stopped", store.Trail(cur.Id).Last().Kind);
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
    /// class; teams.agents' nodes say attention stopped / failed and lastRunFailureClass; the engine's own agent.failed
    /// message is a "stopped" feed row and no Concierge alert; board.list's card node says attention stopped and is
    /// not failed, running or in Needs You; the Cloud Link's summary counts the stopped one as idle (stopped), never
    /// errored, and its deliverable is not "failed".
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

            // The engine's own messages, through the feed row and the Concierge watch.
            var msgs = await engine.MessagesFromAsync(0, 1000, Ct);
            Message Ended(string who) => msgs.Single(m => m.Type == MessageTypes.Failed && m.Source == "Alpha/" + who);
            var stoppedRow = UiRpc.MonitorRow(Ended("stop"))!;
            Assert.Equal(("stopped", "stopped"), (stoppedRow["kind"]!.GetValue<string>(), stoppedRow["failureClass"]!.GetValue<string>()));
            Assert.Equal("stop", stoppedRow["member"]!.GetValue<string>());
            var timeoutRow = UiRpc.MonitorRow(Ended("slow"))!;
            Assert.Equal(("failed", FailureClasses.Timeout), (timeoutRow["kind"]!.GetValue<string>(), timeoutRow["failureClass"]!.GetValue<string>()));
            var fineRow = UiRpc.MonitorRow(msgs.Single(m => m.Type == MessageTypes.Completed && m.Source == "Alpha/fine"))!;
            Assert.Equal("completed", fineRow["kind"]!.GetValue<string>());
            Assert.Null(fineRow["failureClass"]);
            Assert.Null(ConciergeWatcher.Describe(Ended("stop"), "Alpha"));
            Assert.Equal("failed", ConciergeWatcher.Describe(Ended("slow"), "Alpha")!.Value.Kind);

            // The board view's card nodes (board.list), which the window reads the member's state from.
            var board = new BoardService(new BoardStore(db.ConnectionString), engine);
            var list = (await board.DispatchAsync("board.list", new JsonObject { ["team"] = "Alpha" }, Ct))!["cards"]!.AsArray()
                .Select(n => n!.AsObject()).ToDictionary(n => n["member"]!.GetValue<string>());
            Assert.Equal("stopped", list["stop"]["memberState"]!["attention"]!.GetValue<string>());
            Assert.Equal("idle", list["stop"]["memberState"]!["state"]!.GetValue<string>());
            Assert.NotEqual("failed", list["stop"]["status"]!.GetValue<string>());
            Assert.NotEqual("running", list["stop"]["status"]!.GetValue<string>());
            Assert.NotEqual(Lanes.NeedsYou, list["stop"]["lane"]!.GetValue<string>());
            Assert.Equal(("failed", Lanes.NeedsYou, "failed"),
                (list["slow"]["status"]!.GetValue<string>(), list["slow"]["lane"]!.GetValue<string>(), list["slow"]["memberState"]!["attention"]!.GetValue<string>()));

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

    // ------------------------------------------------------------------ a real stop, through board.list

    [Fact]
    public async Task ARealStop_ReadsStoppedOnTheBoard_AndTheManagersNewInstructionRunsTheSameCardAgain()
    {
        using var db = new TempDb();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner();
        runner.On("Alpha/stop", async (run, ct) => { await Task.Delay(Timeout.Infinite, ct); return RunResult.Ok; })
            .On("Alpha/fine", (run, ct) => Task.FromResult(RunResult.Ok))
            // The Manager, woken by the stop notice, tells the member again in the same deliverable (once the test has looked).
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (run.Items.Any(i => i.Type == MessageTypes.Failed && i.Source == "Alpha/stop"))
                {
                    await gate.Task.WaitAsync(ct);
                    await run.TellAsync("stop", "carry on", ct);
                }
                return RunResult.Ok;
            });
        var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { MaxConcurrentAgents = () => 3, SafetyPoll = TimeSpan.FromSeconds(30) });
        try
        {
            await engine.CreateTeamAsync("Alpha", ct: Ct);
            foreach (var n in new[] { "stop", "fine" }) await engine.AddMemberAsync("Alpha", n, ct: Ct);
            foreach (var n in new[] { "stop", "fine" }) await engine.PersonTellAsync("Alpha", n, "job " + n, ct: Ct);
            await engine.StartAsync(Ct);
            await Wait.UntilAsync(() => runner.AdmittedNames().Contains("Alpha/stop"), "stop running");
            var board = new BoardService(new BoardStore(db.ConnectionString), engine);
            await Wait.UntilAsync(async () => (await Of("stop"))["status"]!.GetValue<string>() == "running", "card says running while it runs");
            var id = (await Of("stop"))["id"]!.GetValue<long>();
            // While it runs, board.list shows the queued card running in In Progress (BoardService, display only). This script
            // sends no progress line, so its stored lane stays To Do and that is the lane the stopped card keeps: the
            // card moves back from In Progress to To Do on the stop.
            Assert.Equal(Lanes.InProgress, (await Of("stop"))["lane"]!.GetValue<string>());

            await Wait.UntilAsync(async () => await engine.StopMemberAsync("Alpha", "stop", Ct), "stop asked");
            await Wait.UntilAsync(async () => (await Card(id))["status"]!.GetValue<string>() == "stopped", "card says stopped", TimeSpan.FromSeconds(20));
            await Wait.UntilAsync(() => runner.AdmittedNames().Contains("Alpha/fine"), "fine ran", TimeSpan.FromSeconds(20));

            var stopped = await Card(id);
            Assert.Equal(("stopped", Lanes.Todo), (stopped["status"]!.GetValue<string>(), stopped["lane"]!.GetValue<string>())); // keeps its lane
            Assert.Equal("stopped", stopped["memberState"]!["attention"]!.GetValue<string>());
            await Wait.UntilAsync(async () => (await Of("fine"))["status"]!.GetValue<string>() is "completed" or "done", "fine finished");

            // The Manager's new instruction, same deliverable: the same card leaves "stopped" and runs again.
            gate.SetResult();
            await Wait.UntilAsync(() => runner.AdmittedNames().Count(n => n == "Alpha/stop") == 2, "stop running again", TimeSpan.FromSeconds(20));
            await Wait.UntilAsync(async () => (await Card(id))["status"]!.GetValue<string>() == "running", "card says running again", TimeSpan.FromSeconds(20));
            var again = await Card(id);
            Assert.Equal(Lanes.InProgress, again["lane"]!.GetValue<string>());
            Assert.Single(await All(), n => n["member"]!.GetValue<string>() == "stop"); // no second card

            async Task<List<JsonObject>> All() =>
                (await board.DispatchAsync("board.list", new JsonObject { ["team"] = "Alpha" }, Ct))!["cards"]!.AsArray().Select(n => n!.AsObject()).ToList();
            async Task<JsonObject> Of(string member) => (await All()).Single(n => n["member"]!.GetValue<string>() == member);
            async Task<JsonObject> Card(long cardId) => (await All()).Single(n => n["id"]!.GetValue<long>() == cardId);
        }
        finally { gate.TrySetResult(); await engine.DisposeAsync(); }
    }

    // ------------------------------------------------------------------ migration 0048 as an upgrade

    /// <summary>
    /// A database made by the migrations up to 0047 (board_cards as 0014 + 0037/0045/0047 columns, its CHECK without 'stopped'), with
    /// cards, trail lines, timeline rows and a deleted card, upgraded by the real migrator.
    /// </summary>
    [Fact]
    public void Migration0048_UpgradesA0047DatabaseWithData_KeepingEverything()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-0048-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "old.db")};Pooling=False";
        try
        {
            using (var c = Open(cs))
            {
                Exec(c, "CREATE TABLE IF NOT EXISTS schema_migrations (name TEXT PRIMARY KEY NOT NULL, applied_at TEXT NOT NULL) STRICT;");
                foreach (var name in SchemaMigrator.Available.Where(n => string.CompareOrdinal(n, "0048") < 0))
                {
                    using var tx = c.BeginTransaction();
                    Exec(c, Embedded(name), tx);
                    Exec(c, $"INSERT INTO schema_migrations (name, applied_at) VALUES ('{name}', 't');", tx);
                    tx.Commit();
                }
                // The CHECK of the 0047 schema refuses 'stopped'.
                // (Its column is still named workflow here: 0060 renames it to deliverable.)
                Assert.Throws<SqliteException>(() => Exec(c, "INSERT INTO board_cards (team, title, status, created_at, updated_at) VALUES ('w','x','stopped','t','t');"));
                Exec(c, """
                    INSERT INTO board_cards (team, member, item, title, body, status, lane, placed, awaiting_manager, workflow, created_at, updated_at,
                                             cleared_at, ended_at, ended_how, started_at, timeline_rebuild, timeline_rebuilt)
                        VALUES ('web', 'Tomas', 'B007', 'A', 'body a', 'running', 'in-progress', 0, 1, 5, 't0', 't1', NULL, NULL, NULL, 't0', 0, 1),
                               ('web', 'Ines', NULL, 'B', '', 'failed', 'blocked', 1, 0, 5, 't0', 't2', NULL, 't3', 'expired', NULL, 1, 0),
                               ('web', 'Kofi', NULL, 'C', '', 'queued', 'todo', 0, 0, 6, 't0', 't1', NULL, NULL, NULL, NULL, 0, 0),
                               ('web', NULL, NULL, 'D', '', 'done', 'done', 0, 0, NULL, 't0', 't4', 't5', NULL, NULL, NULL, 0, 0);
                    INSERT INTO board_trail (card_id, at, source, kind, text) VALUES
                        (1, 't0', 'Manager', 'told', 'Build it'), (1, 't1', 'Tomas', 'progress', 'coding'),
                        (2, 't1', 'Ines', 'failed', 'timeout'), (3, 't0', 'Manager', 'told', 'Test'), (4, 't0', 'you', 'comment', 'ok');
                    INSERT INTO board_timeline (card_id, workflow, member, kind, at) SELECT 1, 5, 'Tomas', 'state', 't0';
                    DELETE FROM board_cards WHERE id = 3;
                    """);
            }
            string Dump(SqliteConnection c, string sql) => string.Join("\n", Rows(c, sql));
            string cardsBefore, trailBefore, timelineBefore, seqBefore;
            using (var c = Open(cs))
            {
                cardsBefore = Dump(c, "SELECT * FROM board_cards ORDER BY id;");
                trailBefore = Dump(c, "SELECT * FROM board_trail ORDER BY id;");
                timelineBefore = Dump(c, "SELECT * FROM board_timeline ORDER BY id;");
                seqBefore = Dump(c, "SELECT name, seq FROM sqlite_sequence WHERE name LIKE 'board_%' ORDER BY name;");
            }
            Assert.Equal(4, trailBefore.Split('\n').Length); // card 3's trail went with its card

            string? firstPending = null;
            var applied = SchemaMigrator.Migrate(cs, (_, name) => firstPending = name);
            Assert.Equal("0048_board_card_stopped", firstPending); // the backup hook runs before the rebuild
            Assert.Contains("0048_board_card_stopped", applied);

            using var db = Open(cs);
            Assert.Equal(cardsBefore, Dump(db, "SELECT * FROM board_cards ORDER BY id;"));
            Assert.Equal(trailBefore, Dump(db, "SELECT * FROM board_trail ORDER BY id;"));
            Assert.Equal(timelineBefore, Dump(db, "SELECT * FROM board_timeline ORDER BY id;"));
            Assert.Equal(seqBefore, Dump(db, "SELECT name, seq FROM sqlite_sequence WHERE name LIKE 'board_%' ORDER BY name;"));
            Assert.Equal(["board_trail_card", "ix_board_cards_deliverable", "ix_board_cards_team_member"],
                Rows(db, "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name IN ('board_cards', 'board_trail') AND sql IS NOT NULL ORDER BY name;"));
            Assert.Equal(["ok"], Rows(db, "PRAGMA integrity_check;"));
            Assert.Empty(Rows(db, "PRAGMA foreign_key_check;"));
            Assert.Empty(Rows(db, "SELECT name FROM sqlite_master WHERE name LIKE '%0048%';"));
            Assert.Empty(Rows(db, "SELECT name FROM sqlite_temp_master;"));

            // The app's own store and projector work on the upgraded db: a stop saves "stopped".
            var store = new BoardStore(cs);
            var card = store.Card(1)!;
            Assert.Equal(("A", "B007", 5L, true), (card.Title, card.Item, card.Deliverable!.Value, card.AwaitingManager));
            new BoardProjector(store).Apply(Stopped("Tomas", 1, 5));
            Assert.Equal(("stopped", Lanes.InProgress), (store.Card(1)!.Status, store.Card(1)!.Lane));
            Assert.Throws<SqliteException>(() => Exec(db, "UPDATE board_cards SET status = 'halted' WHERE id = 1;"));
            Assert.Equal(5L, store.AddCard("web", null, null, "E", "", "queued", Lanes.Todo, null).Id); // id 3 and 4 never reused

            // Running the migrator again changes nothing.
            var again = SchemaMigrator.Migrate(cs);
            Assert.Equal(applied.Count, again.Count);
            Exec(db, "DELETE FROM board_cards WHERE id = 1;");
            Assert.Equal(0L, (long)Scalar(db, "SELECT count(*) FROM board_trail WHERE card_id = 1;")!); // still cascades
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    private static string Embedded(string name)
    {
        using var s = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("Yawble.Core.Migrations." + name + ".sql")!;
        return new StreamReader(s).ReadToEnd();
    }

    private static SqliteConnection Open(string cs)
    {
        var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, "PRAGMA foreign_keys=ON;");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static List<string> Rows(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "<null>" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        return rows;
    }
}
