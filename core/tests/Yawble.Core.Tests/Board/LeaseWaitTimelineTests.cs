using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// A run's time in the heavy-work lease's queue is Waiting for a slot, not Agent working, on its card and
/// on a card-less member's row, while it still waits (live) and after it is given the lease (and after the run ended).
/// A REAL lease queue on a started engine; the runs are scripted fakes with no idle clock, and one fake clock for the
/// bus and the board. Read from the board's timeline directly, and as the shell reads it over the RPC.
/// </summary>
public class LeaseWaitTimelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private static async Task<string> Tool(RunContext run, string tool, object args) => (await ToolResult(run, tool, args)).Text;

    private static async Task<(string Text, bool Error)> ToolResult(RunContext run, string tool, object args)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonSerializer.SerializeToNode(args) },
        };
        var reply = await new McpBusServer(run).HandleAsync(System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
        var result = JsonNode.Parse(reply.Json!)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
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

    private const string Slot = TimeCategories.WaitingForSlot, Working = TimeCategories.AgentWorking;

    /// <summary>A time summary's breakdown (ToJson), zero entries left out.</summary>
    private static Dictionary<string, long> Breakdown(JsonNode time) =>
        time["breakdown"]!.AsArray().Select(b => (C: b!["category"]!.GetValue<string>(), Ms: b["ms"]!.GetValue<long>()))
            .Where(b => b.Ms > 0).ToDictionary(b => b.C, b => b.Ms);

    /// <summary>A timeline row's bands summed per category.</summary>
    private static Dictionary<string, long> Bands(JsonNode row)
    {
        var sums = new Dictionary<string, long>();
        foreach (var b in row["bands"]!.AsArray())
        {
            var ms = (long)(DateTimeOffset.Parse(b!["end"]!.GetValue<string>(), CultureInfo.InvariantCulture)
                - DateTimeOffset.Parse(b["start"]!.GetValue<string>(), CultureInfo.InvariantCulture)).TotalMilliseconds;
            if (ms > 0) sums[b["category"]!.GetValue<string>()] = sums.GetValueOrDefault(b["category"]!.GetValue<string>()) + ms;
        }
        return sums;
    }

    private static string Show(Dictionary<string, long> d) => string.Join(", ", d.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));

    private static void Same(Dictionary<string, long> expected, Dictionary<string, long> actual, string what) =>
        Assert.True(expected.Count == actual.Count && expected.All(e => actual.GetValueOrDefault(e.Key, -1) == e.Value),
            $"{what}: expected [{Show(expected)}] but was [{Show(actual)}]");

    /// <summary>
    /// The same wait as the shell reads it, through the real dispatcher (UiRpc over RpcDispatcher): deliverables.get (the
    /// pane's cards), deliverables.list (the Deliverables page's time bar) and timeline.get (the board's timeline, card
    /// and deliverable rows). Web's Manager holds the lease and tells Ben, whose run waits in the queue; Cy, told by the
    /// person in her own deliverable, queues behind him.
    /// </summary>
    [Fact]
    public async Task LeaseQueueTime_IsWaitingForSlot_OnThePane_TheBoardTimeline_AndTheDeliverablesPage()
    {
        using var db = new TempDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
        var runner = new ScriptedRunner();
        TaskCompletionSource managerRelease = new(TaskCreationOptions.RunContinuationsAsynchronously),
            benEnd = new(TaskCreationOptions.RunContinuationsAsynchronously),
            cyEnd = new(TaskCreationOptions.RunContinuationsAsynchronously),
            manager2End = new(TaskCreationOptions.RunContinuationsAsynchronously),
            all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string? benLease = null, cyLease = null, managerLease2 = null;
        long? cyDeliverable = null, manager2Deliverable = null;
        var manager2Runs = 0;
        runner.On("Web/Manager", async (run, ct) =>
        {
            if (run.Prompt.Contains("lead it", StringComparison.Ordinal))
            {
                Assert.StartsWith("ok: you hold the heavy-work lease", (await ToolResult(run, "lease", new { action = "acquire", what = "full test suite" })).Text);
                Assert.False((await ToolResult(run, "tell", new { member = "Ben", instruction = "Card 1: build the page" })).Error);
                await ToolResult(run, "progress", new { status = "Card 1 sent to Ben; running the suite" });
                await managerRelease.Task.WaitAsync(ct);
                await ToolResult(run, "lease", new { action = "release" });
                return RunResult.Ok;
            }
            // Woken card-less in Cy's deliverable (her run ended without a hand-back): it asks for the lease and waits.
            if (run.Correlation == cyDeliverable && Interlocked.Increment(ref manager2Runs) == 1)
            {
                manager2Deliverable = run.Correlation;
                managerLease2 = (await ToolResult(run, "lease", new { action = "acquire", what = "merge build" })).Text;
                await manager2End.Task.WaitAsync(ct);
                return RunResult.Ok;
            }
            await all.Task.WaitAsync(ct);
            return RunResult.Ok;
        })
        .On("Web/Ben", async (run, ct) =>
        {
            benLease = (await ToolResult(run, "lease", new { action = "acquire", what = "build" })).Text;
            await benEnd.Task.WaitAsync(ct);
            return RunResult.Ok;
        })
        .On("Web/Cy", async (run, ct) =>
        {
            cyLease = (await ToolResult(run, "lease", new { action = "acquire", what = "image build" })).Text;
            await cyEnd.Task.WaitAsync(ct); // ends while still queued, without a hand-back: that wakes the Manager
            return RunResult.Ok;
        });

        var store = new BusStore(db.ConnectionString, clock);
        var board = new BoardStore(db.ConnectionString, clock);
        await using var engine = new BusEngine(store, runner, new BusOptions { MaxConcurrentAgents = () => 4, IdleOffer = false, SafetyPoll = TimeSpan.FromMilliseconds(100) });
        var service = new BoardService(board, engine);
        engine.Board = service;
        await engine.CreateTeamAsync("Web", ct: Ct);
        await engine.AddMemberAsync("Web", "Ben", ct: Ct);
        await engine.AddMemberAsync("Web", "Cy", ct: Ct);
        var terminals = new TerminalHub(new AppEvents());
        var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
        var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
        var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
        var ui = new UiRpc(engine, new PolicyStore(db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
            new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, engine), terminals, 16_384);
        var rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], board: service, ui: ui);
        async Task<JsonObject> Call(string method, object args)
        {
            await service.ProjectAsync(Ct);
            return (await rpc.DispatchAsync(method, JsonSerializer.SerializeToNode(args)!.AsObject(), Ct))!.AsObject();
        }
        async Task<string[]> Holders() => [.. (await engine.LeaseSnapshotAsync(Ct)).Holders.Select(h => h.Member)];
        async Task<JsonObject> ListRow(long w) =>
            (await Call("deliverables.list", new { state = "all" }))["deliverables"]!.AsArray().Single(r => r!["id"]!.GetValue<long>() == w)!.AsObject();
        async Task<JsonObject> PaneCard(long w, string member) =>
            (await Call("deliverables.get", new { deliverable = w }))["cards"]!.AsArray().Single(c => c!["member"]!.GetValue<string>() == member)!.AsObject();
        async Task<JsonNode> CardRow(long cardId) => (await Call("timeline.get", new { card = cardId }))["rows"]![0]!;
        async Task<JsonObject> DeliverableTimeline(long w) => await Call("timeline.get", new { deliverable = w });
        static JsonNode MemberRow(JsonObject timeline, string member, bool cardLess) =>
            timeline["rows"]!.AsArray().Single(r => r!["member"]?.GetValue<string>() == member && (r["card"] is null) == cardLess)!;

        // Every view of one card agrees: the pane (deliverables.get), the card's timeline and its row on the deliverable's timeline.
        async Task CardIs(long w, string member, Dictionary<string, long> expected, string when)
        {
            var pane = await PaneCard(w, member);
            Same(expected, Breakdown(pane["time"]!), $"{when}: {member}'s card on the pane (deliverables.get)");
            var cardId = pane["id"]!.GetValue<long>();
            Same(expected, Bands(await CardRow(cardId)), $"{when}: {member}'s card timeline bands (timeline.get card)");
            Same(expected, Breakdown((await Call("timeline.get", new { card = cardId }))["time"]!), $"{when}: {member}'s card time (timeline.get card)");
            Same(expected, Bands(MemberRow(await DeliverableTimeline(w), member, cardLess: false)), $"{when}: {member}'s row on the deliverable timeline");
        }
        // The Deliverables page's time bar is the row's time from deliverables.list: the same as the deliverable timeline's time.
        async Task PageBarIs(long w, Dictionary<string, long>? expected, string when)
        {
            var bar = Breakdown((await ListRow(w))["time"]!);
            Same(Breakdown((await DeliverableTimeline(w))["time"]!), bar, $"{when}: the Deliverables page bar vs timeline.get deliverable time");
            if (expected is not null) Same(expected, bar, $"{when}: the Deliverables page time bar");
        }

        await engine.StartAsync(Ct);

        // 09:00 the Manager takes the lease and tells Ben; Ben queues for it. Cy, told by the person, queues behind him.
        var lead = (await engine.PersonTellAsync("Web", "Manager", "lead it", ct: Ct)).Seq;
        await Wait.UntilAsync(() => benLease is not null, "Ben asks for the lease");
        var cy = (await engine.PersonTellAsync("Web", "Cy", "build the image", ct: Ct)).Seq;
        cyDeliverable = cy;
        await Wait.UntilAsync(() => cyLease is not null, "Cy asks for the lease");
        Assert.StartsWith("queued, position 1", benLease);
        Assert.StartsWith("queued, position 2", cyLease);
        Assert.Equal(["Manager"], await Holders());

        // 09:05, both still waiting (live): their cards are all Waiting for a slot; the card stays queued in To Do.
        clock.Advance(5 * Min);
        var benCard = await PaneCard(lead, "Ben");
        Assert.Equal(("todo", "queued"), (benCard["lane"]!.GetValue<string>(), benCard["status"]!.GetValue<string>()));
        await CardIs(lead, "Ben", Expect((Slot, 5 * Min)), "09:05 live");
        await CardIs(cy, "Cy", Expect((Slot, 5 * Min)), "09:05 live");
        await CardIs(lead, "Manager", Expect((Working, 5 * Min)), "09:05 live (the holder)");
        await PageBarIs(cy, Expect((Slot, 5 * Min)), "09:05 live");
        await PageBarIs(lead, null, "09:05 live");

        // 09:05 the Manager gives the lease back and its run ends: Ben is given it. 09:08: his wait stays Waiting for a slot.
        managerRelease.SetResult();
        await Wait.UntilAsync(async () => (await Holders()).SequenceEqual(["Ben"]), "Ben is given the lease");
        clock.Advance(3 * Min);
        await CardIs(lead, "Ben", Expect((Slot, 5 * Min), (Working, 3 * Min)), "09:08 after the grant");
        var benBands = MemberRow(await DeliverableTimeline(lead), "Ben", cardLess: false)["bands"]!.AsArray()
            .Where(b => b!["start"]!.GetValue<string>() != b["end"]!.GetValue<string>()).Select(b => b!["category"]!.GetValue<string>());
        Assert.Equal([Slot, Working], benBands);
        await CardIs(cy, "Cy", Expect((Slot, 8 * Min)), "09:08 live");
        await PageBarIs(cy, Expect((Slot, 8 * Min)), "09:08 live");
        await PageBarIs(lead, null, "09:08");

        // 09:08 Cy's run ends while still queued, without a hand-back: her wait is ended by the run end (stored), and the
        // Manager is woken card-less in her deliverable; its run asks for the lease and waits behind Ben.
        cyEnd.SetResult();
        await Wait.UntilAsync(() => managerLease2 is not null, "the Manager asks for the lease in Cy's deliverable");
        Assert.Equal(cy, manager2Deliverable);
        Assert.StartsWith("queued, position 1", managerLease2);
        clock.Advance(4 * Min);
        await CardIs(cy, "Cy", Expect((Slot, 8 * Min)), "09:12 Cy's run ended while queued");
        var cyTimeline = await DeliverableTimeline(cy);
        Assert.DoesNotContain(cyTimeline["rows"]!.AsArray(), r => r!["member"]?.GetValue<string>() == "Manager" && r["card"] is not null);
        Same(Expect((Slot, 4 * Min)), Bands(MemberRow(cyTimeline, "Manager", cardLess: true)), "09:12 live: the Manager's card-less row");
        await PageBarIs(cy, Expect((Slot, 12 * Min)), "09:12 live");

        // 09:12 Ben's run ends (gives the lease back): the Manager is given it. 09:14: its wait stays Waiting for a slot.
        benEnd.SetResult();
        await Wait.UntilAsync(async () => (await Holders()).SequenceEqual(["Manager"]), "the Manager is given the lease");
        clock.Advance(2 * Min);
        Same(Expect((Slot, 4 * Min), (Working, 2 * Min)), Bands(MemberRow(await DeliverableTimeline(cy), "Manager", cardLess: true)), "09:14 the card-less row after the grant");
        await PageBarIs(cy, Expect((Slot, 12 * Min), (Working, 2 * Min)), "09:14");

        // 09:14 the Manager's run ends: what its run end stored (no live run now) says the same; so does the page bar.
        manager2End.SetResult();
        await Wait.UntilAsync(async () => (await Holders()).Length == 0, "the Manager gives the lease back");
        await Wait.UntilAsync(async () => (await engine.DeliverableThreadAsync(cy, Ct))
            .Any(m => m.Type == MessageTypes.Completed && m.Source == "Web/Manager"), "the Manager's run end is published");
        var stored = (await engine.DeliverableThreadAsync(cy, Ct)).Last(m => m.Type == MessageTypes.Completed && m.Source == "Web/Manager");
        var holds = JsonNode.Parse(stored.Payload)!["activity"]!["holds"]!.AsArray();
        Assert.Single(holds);
        clock.Advance(Min);
        Same(Expect((Slot, 4 * Min), (Working, 2 * Min)), Bands(MemberRow(await DeliverableTimeline(cy), "Manager", cardLess: true)), "09:15 from the stored run end");
        await CardIs(cy, "Cy", Expect((Slot, 8 * Min)), "09:15 stored");
        await PageBarIs(cy, null, "09:15 stored");
        all.SetResult();
    }
}
