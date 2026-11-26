using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// TesterKofi's check of the lease wait: DeliverablesLiveStatesVerifyTests' O1 scenario re-run against the fix. Web's
/// Manager holds the heavy-work lease and tells Ben "Card 1: build the page"; Ben's run asks for the lease and waits in
/// its queue (in DeliverablesLiveStatesVerifyTests' get-running.json, Ben's card 4 counted that wait as agent-working). Cy, told by the person in her own deliverable, queues
/// behind him. Everything is read the way the shell reads it, through the REAL dispatcher (UiRpc over RpcDispatcher):
/// deliverables.get (the pane's cards), deliverables.list (the Deliverables page's time bar) and timeline.get (the board's
/// timeline, card and deliverable rows). One fake clock for the bus and the board; the runs are scripted fakes (no idle clock).
/// </summary>
public class LeaseWaitTimelineVerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);
    private const string Slot = "waiting-for-slot", Working = "agent-working";

    private static async Task<(string Text, bool Error)> Tool(RunContext run, string tool, object args)
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

    private static Dictionary<string, long> Expect(params (string Category, TimeSpan Time)[] parts) =>
        parts.ToDictionary(p => p.Category, p => (long)p.Time.TotalMilliseconds);

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
                Assert.StartsWith("ok: you hold the heavy-work lease", (await Tool(run, "lease", new { action = "acquire", what = "full test suite" })).Text);
                Assert.False((await Tool(run, "tell", new { member = "Ben", instruction = "Card 1: build the page" })).Error);
                await Tool(run, "progress", new { status = "Card 1 sent to Ben; running the suite" });
                await managerRelease.Task.WaitAsync(ct);
                await Tool(run, "lease", new { action = "release" });
                return RunResult.Ok;
            }
            // Woken card-less in Cy's deliverable (her run ended without a hand-back): it asks for the lease and waits.
            if (run.Correlation == cyDeliverable && Interlocked.Increment(ref manager2Runs) == 1)
            {
                manager2Deliverable = run.Correlation;
                managerLease2 = (await Tool(run, "lease", new { action = "acquire", what = "merge build" })).Text;
                await manager2End.Task.WaitAsync(ct);
                return RunResult.Ok;
            }
            await all.Task.WaitAsync(ct);
            return RunResult.Ok;
        })
        .On("Web/Ben", async (run, ct) =>
        {
            benLease = (await Tool(run, "lease", new { action = "acquire", what = "build" })).Text;
            await benEnd.Task.WaitAsync(ct);
            return RunResult.Ok;
        })
        .On("Web/Cy", async (run, ct) =>
        {
            cyLease = (await Tool(run, "lease", new { action = "acquire", what = "image build" })).Text;
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
