using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// A run's time in the heavy-work lease's queue is Waiting for a slot, not Agent working, on its card and
/// on a card-less member's row, while it still waits (live) and after it is given the lease (and after the run ended).
/// A REAL lease queue on a started engine; the runs are scripted fakes with no idle clock (as in DeliverablesLiveStatesVerifyTests,
/// where Ben's card 4 counted its wait as Agent working), and one fake clock for the bus and the board.
/// </summary>
public class LeaseWaitTimelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private static async Task<string> Tool(RunContext run, string tool, object args)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonSerializer.SerializeToNode(args) },
        };
        var reply = await new McpBusServer(run).HandleAsync(System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
        return JsonNode.Parse(reply.Json!)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
    }

    private static Dictionary<string, long> Sums(IEnumerable<TimeBand> bands)
    {
        var sums = new Dictionary<string, long>();
        foreach (var b in bands) sums[b.Category] = sums.GetValueOrDefault(b.Category) + (long)(b.End - b.Start).TotalMilliseconds;
        return sums;
    }

    private static Dictionary<string, long> Sums(TimeSummary t) => t.Breakdown.ToDictionary(b => b.Category, b => b.Ms);

    private static Dictionary<string, long> Expect(params (string Category, TimeSpan Time)[] parts) =>
        parts.ToDictionary(p => p.Category, p => (long)p.Time.TotalMilliseconds);

    [Fact]
    public async Task LeaseQueueTime_IsWaitingForSlot_LiveAndAfterTheGrant_OnTheCard_AndOnACardLessRow()
    {
        using var db = new TempDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
        var runner = new ScriptedRunner();
        TaskCompletionSource adaRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
            adaAgain = new(TaskCreationOptions.RunContinuationsAsynchronously),
            adaRelease2 = new(TaskCreationOptions.RunContinuationsAsynchronously),
            benEnd = new(TaskCreationOptions.RunContinuationsAsynchronously),
            managerEnd = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string? adaLease = null, adaLease2 = null, benLease = null, managerLease = null;
        var managerRuns = 0;
        long? managerDeliverable = null;
        runner.On("Web/Ada", async (run, ct) =>
        {
            adaLease = await Tool(run, "lease", new { action = "acquire", what = "full test suite" });
            await adaRelease.Task.WaitAsync(ct);
            await Tool(run, "lease", new { action = "release" });
            await adaAgain.Task.WaitAsync(ct);
            adaLease2 = await Tool(run, "lease", new { action = "acquire", what = "full test suite again" });
            await adaRelease2.Task.WaitAsync(ct);
            await Tool(run, "lease", new { action = "release" });
            await Task.Delay(Timeout.Infinite, ct); // runs on: its end would wake the Manager in Ada's deliverable
            return RunResult.Ok;
        })
        .On("Web/Ben", async (run, ct) =>
        {
            benLease = await Tool(run, "lease", new { action = "acquire", what = "build" });
            await benEnd.Task.WaitAsync(ct); // holds the lease until the run ends, without handing back: that wakes the Manager
            return RunResult.Ok;
        })
        .On("Web/Manager", async (run, ct) =>
        {
            if (Interlocked.Increment(ref managerRuns) > 1) return RunResult.Ok;
            managerDeliverable = run.Correlation;
            managerLease = await Tool(run, "lease", new { action = "acquire", what = "merge build" });
            await managerEnd.Task.WaitAsync(ct);
            return RunResult.Ok;
        });

        var store = new BusStore(db.ConnectionString, clock);
        var board = new BoardStore(db.ConnectionString, clock);
        await using var engine = new BusEngine(store, runner, new BusOptions { MaxConcurrentAgents = () => 4, IdleOffer = false, SafetyPoll = TimeSpan.FromMilliseconds(100) });
        var service = new BoardService(board, engine);
        engine.Board = service;
        await engine.CreateTeamAsync("Web", ct: Ct);
        await engine.AddMemberAsync("Web", "Ada", ct: Ct);
        await engine.AddMemberAsync("Web", "Ben", ct: Ct);
        await engine.StartAsync(Ct);
        var timeline = new BoardTimeline(board);

        async Task<string[]> Holders() => [.. (await engine.LeaseSnapshotAsync(Ct)).Holders.Select(h => h.Member)];
        async Task<string[]> Queue() => [.. (await engine.LeaseSnapshotAsync(Ct)).Queue.Select(h => h.Member)];
        async Task<CardTimeline> BenCard(long deliverable)
        {
            await service.ProjectAsync(Ct);
            return timeline.OfCard(board.CardOf("Web", "Ben", deliverable)!, await engine.LiveActivityAsync(Ct), clock.Now);
        }
        async Task<Dictionary<string, long>> ManagerRow(long deliverable)
        {
            await service.ProjectAsync(Ct);
            var w = timeline.OfDeliverable(deliverable, await engine.DeliverableThreadAsync(deliverable, Ct), await engine.LiveActivityAsync(Ct), clock.Now)!;
            Assert.DoesNotContain(w.Rows, r => r.Card is not null && r.Member == "Manager"); // the Manager has no card in it
            return Sums(w.Rows.Single(r => r.Card is null && r.Member == "Manager").Bands);
        }

        // 09:00 Ada takes the lease.
        await engine.PersonTellAsync("Web", "Ada", "run the suite", ct: Ct);
        await Wait.UntilAsync(() => adaLease is not null, "Ada asks for the lease");
        Assert.StartsWith("ok: you hold the heavy-work lease", adaLease);

        // 09:01 Ben is told; his run asks for the lease and waits in its queue (his card stays queued: no progress).
        clock.Advance(Min);
        var benDeliverable = (await engine.PersonTellAsync("Web", "Ben", "build the page", ct: Ct)).Seq;
        await Wait.UntilAsync(() => benLease is not null, "Ben asks for the lease");
        Assert.StartsWith("queued, position 1", benLease);

        // 09:06, still waiting: all of the card's time so far is Waiting for a slot (live).
        clock.Advance(5 * Min);
        var live = await BenCard(benDeliverable);
        Assert.True(live.Time.Open);
        Assert.Equal(Expect((TimeCategories.WaitingForSlot, 5 * Min)), Sums(live.Time));

        // 09:06 Ada releases: Ben is given the lease. 09:09: the wait stays Waiting for a slot, the time since is Agent working.
        adaRelease.SetResult();
        await Wait.UntilAsync(async () => (await Holders()).SequenceEqual(["Ben"]), "Ben is given the lease");
        clock.Advance(3 * Min);
        var granted = await BenCard(benDeliverable);
        Assert.Equal(Expect((TimeCategories.WaitingForSlot, 5 * Min), (TimeCategories.AgentWorking, 3 * Min)), Sums(granted.Time));
        Assert.Equal([TimeCategories.WaitingForSlot, TimeCategories.AgentWorking], granted.Bands.Select(b => b.Category));
        Assert.Equal(clock.Now - 3 * Min, granted.Bands[0].End);

        // 09:09 Ada asks again and queues behind Ben. Ben's run ends (his card is done, the lease goes to Ada) without handing
        // back, which wakes the Manager in Ben's deliverable, where it has no card: its run asks for the lease and waits.
        adaAgain.SetResult();
        await Wait.UntilAsync(() => adaLease2 is not null, "Ada asks for the lease again");
        Assert.StartsWith("queued, position 1", adaLease2);
        benEnd.SetResult();
        await Wait.UntilAsync(() => managerLease is not null, "the Manager asks for the lease");
        Assert.Equal(benDeliverable, managerDeliverable);
        Assert.StartsWith("queued, position 1", managerLease);
        Assert.Equal(["Ada"], await Holders());

        // 09:13, the Manager still waiting: its card-less row is Waiting for a slot (live). Ben's card ended at 09:09.
        clock.Advance(4 * Min);
        var done = await BenCard(benDeliverable);
        Assert.False(done.Time.Open);
        Assert.Equal(Expect((TimeCategories.WaitingForSlot, 5 * Min), (TimeCategories.AgentWorking, 3 * Min)), Sums(done.Time));
        Assert.Equal(Expect((TimeCategories.WaitingForSlot, 4 * Min)), await ManagerRow(benDeliverable));

        // 09:13 Ada releases: the Manager is given the lease. 09:15: the wait stays Waiting for a slot.
        adaRelease2.SetResult();
        await Wait.UntilAsync(async () => (await Holders()).SequenceEqual(["Manager"]) && (await Queue()).Length == 0, "the Manager is given the lease");
        clock.Advance(2 * Min);
        Assert.Equal(Expect((TimeCategories.WaitingForSlot, 4 * Min), (TimeCategories.AgentWorking, 2 * Min)), await ManagerRow(benDeliverable));

        // 09:15 the Manager's run ends: what is stored from its end (no live run any more) says the same, and so does Ben's card.
        managerEnd.SetResult();
        await Wait.UntilAsync(async () => (await Holders()).Length == 0, "the Manager's run ends and gives the lease back");
        await Wait.UntilAsync(async () => (await engine.DeliverableThreadAsync(benDeliverable, Ct))
            .Count(m => m.Type == MessageTypes.Completed && m.Source == "Web/Manager") >= 1, "the Manager's run end is published");
        clock.Advance(Min);
        Assert.Equal(Expect((TimeCategories.WaitingForSlot, 4 * Min), (TimeCategories.AgentWorking, 2 * Min)), await ManagerRow(benDeliverable));
        Assert.Equal(Expect((TimeCategories.WaitingForSlot, 5 * Min), (TimeCategories.AgentWorking, 3 * Min)), Sums((await BenCard(benDeliverable)).Time));
    }

    [Fact]
    public void JoinSpans_JoinsOverlappingAndTouchingHolds_InStartOrder()
    {
        var t = new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
        var joined = BusEngine.JoinSpans([new(t.AddMinutes(10), t.AddMinutes(12)), new(t, t.AddMinutes(5)), new(t.AddMinutes(1), t.AddMinutes(4)),
            new(t.AddMinutes(5), t.AddMinutes(6))]);
        Assert.Equal([new ActivitySpan(t, t.AddMinutes(6)), new ActivitySpan(t.AddMinutes(10), t.AddMinutes(12))], joined);
    }
}
