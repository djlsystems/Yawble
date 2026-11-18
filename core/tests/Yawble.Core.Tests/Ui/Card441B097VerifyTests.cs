using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Scheduling;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>
/// Card 441 (B097 verify, TesterKofi): the stopped-run contract as the window really gets it, from a real engine run the
/// person stopped. Card 437's own tests build the messages by hand; these read the engine's own agent.failed message
/// through the feed row (UiRpc.MonitorRow), the Concierge watch and the board's card node (board.list).
/// </summary>
public class Card441B097VerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(BusEngine Engine, TempDb Db)> RunThreeAsync()
    {
        var db = new TempDb();
        var runner = new ScriptedRunner();
        runner.On("Alpha/stop", async (run, ct) => { await Task.Delay(Timeout.Infinite, ct); return RunResult.Ok; })
            .On("Alpha/slow", (run, ct) => Task.FromResult(new RunResult(false, "wall-clock timeout (15 min)", FailureClasses.Timeout)))
            .On("Alpha/fine", (run, ct) => Task.FromResult(RunResult.Ok));
        var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { MaxConcurrentAgents = () => 3, SafetyPoll = TimeSpan.FromSeconds(30) });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        foreach (var n in new[] { "stop", "slow", "fine" }) await engine.AddMemberAsync("Alpha", n, ct: Ct);
        foreach (var n in new[] { "stop", "slow", "fine" }) await engine.PersonTellAsync("Alpha", n, "job " + n, ct: Ct);
        await engine.StartAsync(Ct);
        await Wait.UntilAsync(() => runner.AdmittedNames().Contains("Alpha/stop"), "stop running");
        await Wait.UntilAsync(async () => await engine.StopMemberAsync("Alpha", "stop", Ct), "stop asked");
        await Wait.UntilAsync(async () => await engine.IsQuiescentAsync(Ct) && runner.AdmittedNames().Count(n => n != "Alpha/Manager") == 3,
            "all three ran", TimeSpan.FromSeconds(20));
        return (engine, db);
    }

    /// <summary>The engine's own agent.failed for a stop carries class "stopped": the feed says "stopped", the watch says nothing.</summary>
    [Fact]
    public async Task TheEnginesOwnStopMessage_IsAStoppedFeedRow_AndNoConciergeAlert()
    {
        var (engine, db) = await RunThreeAsync();
        try
        {
            var msgs = await engine.MessagesFromAsync(0, 1000, Ct);
            Message Ended(string who) => msgs.Single(m => m.Type == MessageTypes.Failed && m.Source == "Alpha/" + who);

            var stopped = UiRpc.MonitorRow(Ended("stop"))!;
            Assert.Equal(("stopped", "stopped"), (stopped["kind"]!.GetValue<string>(), stopped["failureClass"]!.GetValue<string>()));
            Assert.Equal("stop", stopped["member"]!.GetValue<string>());

            var timeout = UiRpc.MonitorRow(Ended("slow"))!;
            Assert.Equal(("failed", FailureClasses.Timeout), (timeout["kind"]!.GetValue<string>(), timeout["failureClass"]!.GetValue<string>()));

            var fine = UiRpc.MonitorRow(msgs.Single(m => m.Type == MessageTypes.Completed && m.Source == "Alpha/fine"))!;
            Assert.Equal("completed", fine["kind"]!.GetValue<string>());
            Assert.Null(fine["failureClass"]);

            Assert.Null(ConciergeWatcher.Describe(Ended("stop"), "Alpha"));
            Assert.Equal("failed", ConciergeWatcher.Describe(Ended("slow"), "Alpha")!.Value.Kind);
        }
        finally { await engine.DisposeAsync(); db.Dispose(); }
    }

    /// <summary>
    /// What the board view gets for the stopped member's card (board.list): the member state says "stopped", the card is
    /// not in Needs You and its status is not "failed". This pins the core's side of the seam the window reads
    /// (cards.ts cardStopped); see shell tests/unit/card441-b097-verify.test.ts for what the window then shows.
    /// </summary>
    [Fact]
    public async Task TheBoardNode_ForAStoppedRun_HasMemberAttentionStopped_AndIsNotFailedOrNeedsYou()
    {
        var (engine, db) = await RunThreeAsync();
        try
        {
            var board = new BoardService(new BoardStore(db.ConnectionString), engine);
            var list = (await board.DispatchAsync("board.list", new JsonObject { ["team"] = "Alpha" }, Ct))!["cards"]!.AsArray()
                .Select(n => n!.AsObject()).ToDictionary(n => n["member"]!.GetValue<string>());

            var stop = list["stop"];
            Assert.Equal("stopped", stop["memberState"]!["attention"]!.GetValue<string>());
            Assert.Equal("idle", stop["memberState"]!["state"]!.GetValue<string>());
            Assert.NotEqual("failed", stop["status"]!.GetValue<string>());
            Assert.NotEqual(Lanes.NeedsYou, stop["lane"]!.GetValue<string>());

            var slow = list["slow"];
            Assert.Equal(("failed", Lanes.NeedsYou, "failed"),
                (slow["status"]!.GetValue<string>(), slow["lane"]!.GetValue<string>(), slow["memberState"]!["attention"]!.GetValue<string>()));
        }
        finally { await engine.DisposeAsync(); db.Dispose(); }
    }

    /// <summary>
    /// B097 on the board: a card whose run the person stopped must not keep saying it is running (nothing runs any
    /// more). Today the projector keeps the card's status, so a card that had a progress line still says "running"
    /// in In Progress after the stop. FAILS on 742c73e + 96ddd99 (card 441).
    /// </summary>
    [Fact]
    public void AStoppedCard_DoesNotStaySaying_Running()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var p = new BoardProjector(store);
        var at = DateTimeOffset.Parse("2026-11-18T12:00:00Z");
        p.Apply(new Message(1, "agent.instruction.web/Tomas", JsonSerializer.Serialize(new { text = "Build it" }), "web/Manager", 5, null, 0, at));
        p.Apply(new Message(2, MessageTypes.Progress, JsonSerializer.Serialize(new { status = "coding" }), "web/Tomas", 5, null, 0, at.AddMinutes(1)));
        p.Apply(new Message(3, MessageTypes.Failed, JsonSerializer.Serialize(new { reason = BusEngine.StoppedReason, failureClass = FailureClasses.Stopped }),
            "web/Tomas", 5, null, 0, at.AddMinutes(2)));
        var card = store.Cards("web").Single();
        Assert.NotEqual(Lanes.NeedsYou, card.Lane);
        Assert.NotEqual("failed", card.Status);
        Assert.NotEqual("running", card.Status);
    }
}
