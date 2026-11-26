using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// deliverables.list and deliverables.get, the Deliverables page's reads across every team (the Deliverables page's
/// contract), through the real board store and the real UiRpc over RpcDispatcher. Two rigs, fakes only: Rig is a temp
/// database with a fake clock and an engine that is never started (each deliverable's messages are written straight
/// to the log); Live is a started engine whose runs are a scripted fake runner (never a real agent CLI), so the live
/// states are real: a run that is on it (running), a run waiting in the heavy-work lease queue (waiting / lease), a
/// delivery waiting for a run slot (waiting / slot), a question with a run still on it (needs-you, working).
/// With DELIVERABLES_FIXTURE_OUT set, the Live answers are written there as JSON (shell/tests/unit/fixtures/deliverables)
/// for the shell's Deliverables page test to render.
/// </summary>
public class DeliverablesPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public DeliverableExpiryTests.ManualClock Clock { get; } = new(Start);
        public BusStore Store { get; private set; } = null!;
        public BoardStore Board { get; private set; } = null!;
        public BusEngine Engine { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;
        public Dictionary<string, long> Teams { get; } = [];

        public static async Task<Rig> CreateAsync(params string[] teams)
        {
            var rig = new Rig();
            rig.Store = new BusStore(rig.Db.ConnectionString, rig.Clock);
            rig.Engine = new BusEngine(rig.Store, new ScriptedRunner(), new BusOptions { IdleOffer = false });
            rig.Board = new BoardStore(rig.Db.ConnectionString, rig.Clock);
            rig.Engine.Board = new BoardService(rig.Board, rig.Engine);
            foreach (var spec in teams)
            {
                var parts = spec.Split(':');
                rig.Teams[parts[0]] = await rig.Engine.CreateTeamAsync(parts[0], ct: Ct);
                foreach (var member in parts.Skip(1)) await rig.Engine.AddMemberAsync(parts[0], member, ct: Ct);
            }
            var terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(rig.Engine, new PolicyStore(rig.Db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Engine), terminals, 16_384);
            rig.Rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
            return rig;
        }

        /// <summary>A deliverable started by the person telling a member something; payload extras (item) merged in.</summary>
        public long Tell(string team, string member, string text, string? item = null)
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            var payload = new JsonObject { ["text"] = text };
            if (item is not null) payload["item"] = item;
            return Store.Append(new NewMessage(MessageTypes.InstructionPrefix + team + "/" + member, payload.ToJsonString(), MessageTypes.PersonSource, null)).Seq;
        }

        public Message Say(long deliverable, string type, string source, object payload)
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            var last = Store.Deliverable(deliverable)[^1].Seq;
            return Store.Append(new NewMessage(type, JsonSerializer.Serialize(payload), source, last));
        }

        public void Complete(long w, string by) => Say(w, MessageTypes.DeliverableCompleted, by, new { delivered = "done and dusted" });

        public void Close(long w) => Say(w, MessageTypes.DeliverableClosed, MessageTypes.PersonSource, new { reason = "not needed" });

        public void Expire(long w) =>
            Say(w, MessageTypes.DeliverableExpired, MessageTypes.HostSource, new { reason = "expired", detail = "no activity for 3 days", endedAt = Start.ToString("O"), expiredAt = Clock.Now.ToString("O") });

        public async Task<JsonObject> Call(string method, object? args = null) =>
            (await Rpc.DispatchAsync(method, args is null ? null : JsonSerializer.SerializeToNode(args)!.AsObject(), Ct))!.AsObject();

        public async Task<JsonObject> List(object? args = null) => await Call("deliverables.list", args ?? new { });

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static List<long> Ids(JsonObject page) => [.. page["deliverables"]!.AsArray().Select(w => w!["id"]!.GetValue<long>())];

    private static JsonObject Row(JsonObject page, long id) => page["deliverables"]!.AsArray().Single(w => w!["id"]!.GetValue<long>() == id)!.AsObject();

    private static string? S(JsonNode? n) => n?.GetValue<string>();

    [Fact]
    public async Task TheList_SpansEveryTeam_AndFiltersByTeamTextAndState()
    {
        await using var rig = await Rig.CreateAsync("Alpha:Ada", "Beta:Bob");
        var idle = rig.Tell("Alpha", "Ada", "Fix the login bug\nwith details below");
        var done = rig.Tell("Alpha", "Ada", "Write the release notes");
        rig.Complete(done, "Alpha/Ada");
        var closed = rig.Tell("Alpha", "Ada", "Try the old parser");
        rig.Close(closed);
        var asking = rig.Tell("Beta", "Bob", "Pick a colour scheme");
        rig.Say(asking, MessageTypes.NeedsDecision, "Beta/Bob", new { question = "dark or light?" });
        var expired = rig.Tell("Beta", "Bob", "Look at the flaky test");
        rig.Expire(expired);
        var stopped = rig.Tell("Alpha", "Ada", "Refactor the store");
        rig.Say(stopped, MessageTypes.Failed, "Alpha/Ada", new { reason = "stopped by the person", failureClass = FailureClasses.Stopped });
        var queued = rig.Tell("Beta", "Bob", "Queue me");
        rig.Store.AddPending("Beta/Bob", queued);

        var all = await rig.List();
        Assert.Equal([queued, stopped, expired, asking, closed, done, idle], Ids(all)); // newest activity first
        Assert.Equal((false, true), (all["truncated"]!.GetValue<bool>(), all["backlog"]!.GetValue<bool>()));
        Assert.Equal(rig.Clock.Now, DateTimeOffset.Parse(S(all["now"])!, System.Globalization.CultureInfo.InvariantCulture));

        var r = Row(all, idle);
        Assert.Equal(("Alpha", rig.Teams["Alpha"], "Fix the login bug", "open", "idle"),
            (S(r["team"]), r["teamId"]!.GetValue<long>(), S(r["title"]), S(r["state"]), S(r["status"])));
        Assert.Equal("Ada", S(r["owner"]));
        Assert.NotNull(r["ownerId"]);
        Assert.NotNull(r["expiresAt"]); // idle: the expiry countdown
        Assert.Null(r["item"]);         // started by a tell, not the backlog
        Assert.Empty(r["members"]!.AsArray());
        Assert.False(r["working"]!.GetValue<bool>());
        Assert.NotNull(r["time"]);      // the time object from the board
        Assert.Equal("completed", S(Row(all, done)["status"]));
        Assert.Equal("closed", S(Row(all, closed)["status"]));
        Assert.Equal("expired", S(Row(all, expired)["status"]));
        Assert.Equal(("needs-you", "Beta"), (S(Row(all, asking)["status"]), S(Row(all, asking)["team"])));
        Assert.NotNull(Row(all, asking)["question"]);
        Assert.Equal("stopped", S(Row(all, stopped)["status"]));
        // An undelivered delivery whose member is not in the run queue is pending, not "queued for a run slot".
        var q = Row(all, queued);
        Assert.Equal(("waiting", "pending"), (S(q["status"]), S(q["waitingFor"])));
        var worker = Assert.Single(q["members"]!.AsArray())!;
        Assert.Equal(("Bob", "pending", null), (S(worker["name"]), S(worker["state"]), S(worker["waitsFor"])));
        rig.Store.Enqueue(rig.Store.MemberBySubscriber("Beta/Bob")!.Id); // in the run queue: queued for a slot
        q = Row(await rig.List(), queued);
        Assert.Equal(("waiting", "slot"), (S(q["status"]), S(q["waitingFor"])));
        Assert.Equal("queued", S(Assert.Single(q["members"]!.AsArray())!["state"]));
        // No tokens, cost or usage anywhere.
        Assert.DoesNotContain("token", all.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cost", all.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        var counts = all["counts"]!.AsObject();
        Assert.Equal((7, 4, 1, 1, 2), (counts["all"]!.GetValue<int>(), counts["open"]!.GetValue<int>(), counts["needsYou"]!.GetValue<int>(),
            counts["done"]!.GetValue<int>(), counts["closed"]!.GetValue<int>()));
        Assert.Equal(1, counts["completedToday"]!.GetValue<int>());

        // Team filter.
        Assert.Equal([queued, expired, asking], Ids(await rig.List(new { teamId = rig.Teams["Beta"] })));
        Assert.Equal("no_such_team", (await Assert.ThrowsAsync<RpcException>(() => rig.List(new { teamId = 999 }))).Code);

        // Text filter: title words, a member's name, the team, #id; every word must match.
        Assert.Equal([idle], Ids(await rig.List(new { q = "LOGIN" })));
        Assert.Equal([queued, expired, asking], Ids(await rig.List(new { q = "bob" })));
        Assert.Equal([asking], Ids(await rig.List(new { q = $"#{asking}" })));
        Assert.Equal([asking], Ids(await rig.List(new { q = "beta colour" })));
        Assert.Empty(Ids(await rig.List(new { q = "alpha colour" })));
        Assert.Equal(1, (await rig.List(new { q = "bob colour" }))["counts"]!["all"]!.GetValue<int>()); // counts follow the text filter

        // State filter: the chips, and one status.
        Assert.Equal([queued, stopped, asking, idle], Ids(await rig.List(new { state = "open" })));
        Assert.Equal([asking], Ids(await rig.List(new { state = "needs-you" })));
        Assert.Equal([done], Ids(await rig.List(new { state = "done" })));
        Assert.Equal([expired, closed], Ids(await rig.List(new { state = "closed" })));
        Assert.Equal([stopped], Ids(await rig.List(new { state = "stopped" })));
        Assert.Equal(7, Ids(await rig.List(new { state = "all" })).Count);
        Assert.Equal(7, (await rig.List(new { state = "done" }))["counts"]!["all"]!.GetValue<int>()); // counts before the state filter
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => rig.List(new { state = "sideways" }))).Code);
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => rig.List(new { limit = "lots" }))).Code);
    }

    [Fact]
    public async Task TheItem_IsTheDispatch_OrThePayloadsItem_WithItsTitleAndArchivedFlag()
    {
        await using var rig = await Rig.CreateAsync("Alpha:Ada");
        var spec = rig.Board.AddItem("Deliverables page", "## Why\nEvery team's deliverables in one table.", "Alpha");
        var dispatched = rig.Tell("Alpha", "Manager", $"Backlog item {spec.Label} \"Deliverables page\" is dispatched to your team. It is a SPEC: cut it into cards.");
        rig.Board.AddDispatch(spec.Id, "Alpha", dispatched, "you");

        var old = rig.Board.AddItem("Old idea", "stale", null);
        rig.Board.SaveItem(old with { ArchivedAt = rig.Board.Now() });
        var fromPayload = rig.Tell("Alpha", "Manager", "Backlog item B002 is dispatched to your team. It is a SPEC...", item: old.Label);
        var told = rig.Tell("Alpha", "Ada", "Just a quick question");
        var gone = rig.Tell("Alpha", "Ada", "Names an item that is no more", item: "B009");

        var page = await rig.List();
        var d = Row(page, dispatched);
        Assert.Equal("Deliverables page", S(d["title"])); // the item's title, not the dispatch sentence
        Assert.Equal((spec.Label, "Deliverables page", false), (S(d["item"]!["label"]), S(d["item"]!["title"]), d["item"]!["archived"]!.GetValue<bool>()));
        Assert.Null(d["item"]!["spec"]); // the spec is deliverables.get's
        var p = Row(page, fromPayload);
        Assert.Equal((old.Label, "Old idea", true), (S(p["item"]!["label"]), S(p["item"]!["title"]), p["item"]!["archived"]!.GetValue<bool>()));
        Assert.Equal("Old idea", S(p["title"]));
        Assert.Null(Row(page, told)["item"]);
        Assert.Equal("Just a quick question", S(Row(page, told)["title"]));
        Assert.Null(Row(page, gone)["item"]);

        // The item's label and title are searchable.
        Assert.Equal([dispatched], Ids(await rig.List(new { q = spec.Label })));
        Assert.Equal([fromPayload], Ids(await rig.List(new { q = "old idea" })));
    }

    [Fact]
    public async Task EveryOpenDeliverable_IsListed_PastTheLimit()
    {
        await using var rig = await Rig.CreateAsync("Alpha:Ada", "Beta:Bob");
        var oldOpen = rig.Tell("Beta", "Bob", "an old open one nobody works");
        var ended = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var w = rig.Tell("Alpha", "Ada", $"job {i}");
            rig.Complete(w, "Alpha/Ada");
            ended.Add(w);
        }

        var page = await rig.List(new { limit = 2 });
        Assert.Equal([ended[4], ended[3], oldOpen], Ids(page));
        Assert.True(page["truncated"]!.GetValue<bool>());
        Assert.Equal(6, page["counts"]!["all"]!.GetValue<int>());
        Assert.False((await rig.List(new { limit = 10 }))["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Get_CarriesTheRow_ItsCards_ItsMessagesNewestFirst_AndTheSpec()
    {
        await using var rig = await Rig.CreateAsync("Alpha:Ada:Ben");
        var item = rig.Board.AddItem("Commit view", "## Spec\nShow a commit.", "Alpha");
        var w = rig.Tell("Alpha", "Manager", "Backlog item B001 is dispatched to your team.");
        rig.Board.AddDispatch(item.Id, "Alpha", w, "you");
        rig.Say(w, MessageTypes.InstructionPrefix + "Alpha/Ada", "Alpha/Manager", new { text = "Card 2: build the core" });
        rig.Say(w, MessageTypes.Progress, "Alpha/Ada", new { status = "writing the contract" });
        rig.Say(w, MessageTypes.Handback, "Alpha/Ada", new { delivered = "core done at 1112f8d" });
        rig.Say(w, MessageTypes.InstructionPrefix + "Alpha/Ben", "Alpha/Manager", new { text = "Card 3: test the core" });
        var other = rig.Tell("Alpha", "Ada", "unrelated");

        var got = await rig.Call("deliverables.get", new { deliverable = w }); // the board has read the bus: its cards are the projector's
        var row = got["deliverable"]!.AsObject();
        Assert.Equal((w, "Commit view", "Alpha", "B001"), (row["id"]!.GetValue<long>(), S(row["title"]), S(row["team"]), S(row["item"]!["label"])));
        // Exactly the row the list gives.
        Assert.Equal(Row(await rig.List(), w).ToJsonString(), row.ToJsonString());

        var cards = got["cards"]!.AsArray();
        Assert.Equal(rig.Board.CardsOfDeliverable(w).Select(c => c.Id).Order(), cards.Select(c => c!["id"]!.GetValue<long>()).Order());
        Assert.DoesNotContain(rig.Board.CardsOfDeliverable(other).Single().Id, cards.Select(c => c!["id"]!.GetValue<long>()));
        var lanes = Lanes.All.Select(l => l.Id).ToList();
        var order = cards.Select(c => lanes.IndexOf(S(c!["lane"])!)).ToList();
        Assert.Equal(order.Order(), order); // by lane: To Do, In Progress, Needs You, Done
        var ada = cards.Single(c => S(c!["member"]) == "Ada")!;
        Assert.Equal(("done", "handback"), (S(ada["lane"]), S(ada["status"])));
        var ben = cards.Single(c => S(c!["member"]) == "Ben")!;
        Assert.Equal(("todo", "queued", "Card 3: test the core"), (S(ben["lane"]), S(ben["status"]), S(ben["title"])));
        Assert.NotNull(ada["time"]!["overallMs"]);

        var messages = got["messages"]!.AsArray();
        Assert.Equal(["instruction", "handback", "progress", "instruction", "instruction"], messages.Select(m => S(m!["kind"])));
        Assert.Equal(("Ada", "Ada", "core done at 1112f8d"), (S(messages[1]!["from"]), S(messages[1]!["member"]), S(messages[1]!["text"])));
        Assert.Equal(("Manager", "Ben"), (S(messages[0]!["from"]), S(messages[0]!["member"])));
        Assert.Equal(("person", "Manager"), (S(messages[4]!["from"]), S(messages[4]!["member"])));

        Assert.Equal("## Spec\nShow a commit.", S(got["spec"]));
        Assert.True(got["backlog"]!.GetValue<bool>());
        Assert.DoesNotContain("token", got.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        // A deliverable without an item has no spec; one that does not exist is refused.
        Assert.Null((await rig.Call("deliverables.get", new { deliverable = other }))["spec"]);
        Assert.Equal("no_such_deliverable", (await Assert.ThrowsAsync<RpcException>(() => rig.Call("deliverables.get", new { deliverable = 99_999 }))).Code);
    }

    [Fact]
    public async Task Get_SaysAClosedDeliverablesCloseInWords_AndKeepsAtMostThirtyMessages()
    {
        await using var rig = await Rig.CreateAsync("Alpha:Ada");
        var w = rig.Tell("Alpha", "Ada", "chatty");
        for (var i = 0; i < 40; i++) rig.Say(w, MessageTypes.Progress, "Alpha/Ada", new { status = $"step {i}" });
        rig.Close(w);
        var got = await rig.Call("deliverables.get", new { deliverable = w });
        var messages = got["messages"]!.AsArray();
        Assert.Equal(UiRpc.DeliverableMessages, messages.Count);
        Assert.Equal(("closed", "person", "not needed"), (S(messages[0]!["kind"]), S(messages[0]!["from"]), S(messages[0]!["text"])));
        Assert.Equal("step 39", S(messages[1]!["text"]));
        Assert.Equal("closed", S(got["deliverable"]!["status"]));
    }

    [Theory]
    [InlineData("deliverables.list")]
    [InlineData("deliverables.get")]
    public void AVisitor_MayReadBoth(string method)
    {
        Assert.True(RemoteAccess.OnAllowList(method));
        Assert.True(RemoteAccess.Reads(method, null));
        Assert.Null(RemoteAccess.Refusal(method, null, allowHostTerminals: false, RemoteAccess.Read));
        Assert.True(Yawble.Core.Provenance.AuditLog.IsReadOnly(method));
    }

    [Fact]
    public void TheLinksShaping_SetsTheBacklogFlag_ForEveryAccess_AndKeepsTheSpecWhileTheViewerMayReadTheBacklog()
    {
        // Every Cloud Link viewer, a Visitor too, may read the backlog today (backlog.item is a read): the flag is true and
        // the spec stays. The flag is the core's answer, so the shell never decides this itself.
        var got = new JsonObject { ["spec"] = "secret plans", ["backlog"] = true };
        RemoteAccess.ShapeResult("deliverables.get", got, showCode: true, RemoteAccess.Read);
        Assert.True(got["backlog"]!.GetValue<bool>()); // a Visitor reads the backlog
        Assert.Equal("secret plans", S(got["spec"]));
        var list = new JsonObject { ["backlog"] = true };
        RemoteAccess.ShapeResult("deliverables.list", list, showCode: false, RemoteAccess.Full);
        Assert.True(list["backlog"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AThousandDeliverablesAcrossTeams_AreReadInAFixedNumberOfQueries()
    {
        await using var rig = await Rig.CreateAsync("Alpha:Ada", "Beta:Bob", "Gamma:Gus");
        var item = rig.Board.AddItem("Big item", "spec", null);
        string[] teams = ["Alpha", "Beta", "Gamma"], members = ["Ada", "Bob", "Gus"];

        void Seed(int n)
        {
            var made = new List<(int I, long W)>();
            rig.Store.InTransaction(() =>
            {
                for (var i = 0; i < n; i++)
                {
                    var t = i % 3;
                    var w = rig.Tell(teams[t], members[t], $"job {i}");
                    rig.Say(w, MessageTypes.Progress, $"{teams[t]}/{members[t]}", new { status = "working" });
                    if (i % 4 == 0) rig.Complete(w, $"{teams[t]}/{members[t]}");
                    made.Add((i, w));
                }
            });
            // The board's own connection writes after the bus's transaction (SQLite has one writer).
            foreach (var (i, w) in made)
            {
                if (i % 10 == 0) rig.Board.AddDispatch(item.Id, teams[i % 3], w, "you");
                if (i % 7 == 0) rig.Board.AddCard(teams[i % 3], members[i % 3], item.Label, $"card {i}", "", "queued", Lanes.Todo, w);
            }
        }

        long Queries() => rig.Store.Queries + rig.Board.Queries;

        Seed(12);
        var before = Queries();
        var small = await rig.Engine.DeliverablesPageAsync(null, null, null, BusEngine.MaxPageLimit, Ct);
        var smallQueries = Queries() - before;
        Assert.Equal(12, small.Rows.Count);

        Seed(1000 - 12);
        before = Queries();
        var watch = Stopwatch.StartNew();
        var big = await rig.Engine.DeliverablesPageAsync(null, null, null, BusEngine.MaxPageLimit, Ct);
        watch.Stop();
        var bigQueries = Queries() - before;
        Assert.Equal(1000, big.Rows.Count);
        Assert.All(big.Rows, r => Assert.NotNull(r.Summary.Time));
        Assert.Equal(2 + 99, big.Rows.Count(r => r.Item is not null)); // every tenth of each batch (12, then 988) was dispatched
        // No per-deliverable query: the same statements for 1,000 deliverables as for 12, and only a handful of them.
        Assert.Equal(smallQueries, bigQueries);
        Assert.InRange(bigQueries, 1, 20);
        TestContext.Current.SendDiagnosticMessage($"deliverables.list over 1,000 deliverables: {bigQueries} queries, {watch.ElapsedMilliseconds} ms");
    }

    // ---------------------------------------------------------------- on a started engine: live states, limits, filters

    private static readonly HashSet<string> Categories =
        ["agent-working", "waiting-on-you", "stalled", "waiting-for-slot", "waiting-on-teammate", "blocked", "scheduled", "not-started", "idle", "was-done"];

    /// <summary>A started engine with the given run slots; <c>workflow</c> scripts the runner before the engine exists.</summary>
    private sealed class Live : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusStore Store { get; private set; } = null!;
        public BoardStore Board { get; private set; } = null!;
        public BusEngine Engine { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;
        public ScriptedRunner Runner { get; } = new();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, long> Teams { get; } = [];

        public static async Task<Live> CreateAsync(int slots, Action<Live> workflow, params string[] teams)
        {
            var rig = new Live();
            workflow(rig);
            rig.Store = rig.Db.OpenStore();
            rig.Engine = new BusEngine(rig.Store, rig.Runner, new BusOptions { MaxConcurrentAgents = () => slots, IdleOffer = false, SafetyPoll = TimeSpan.FromMilliseconds(100) });
            rig.Board = new BoardStore(rig.Db.ConnectionString);
            rig.Engine.Board = new BoardService(rig.Board, rig.Engine);
            foreach (var spec in teams)
            {
                var parts = spec.Split(':');
                rig.Teams[parts[0]] = await rig.Engine.CreateTeamAsync(parts[0], ct: Ct);
                foreach (var member in parts.Skip(1)) await rig.Engine.AddMemberAsync(parts[0], member, ct: Ct);
            }
            var terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(rig.Engine, new PolicyStore(rig.Db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Engine), terminals, 16_384);
            rig.Rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
            return rig;
        }

        public async Task<JsonObject> Call(string method, object? args = null) =>
            (await Rpc.DispatchAsync(method, args is null ? null : JsonSerializer.SerializeToNode(args)!.AsObject(), Ct))!.AsObject();

        public async Task<JsonObject> List(object? args = null) => await Call("deliverables.list", args ?? new { });

        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

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

    private static List<(string, string)> Members(JsonObject row) =>
        [.. row["members"]!.AsArray().Select(m => (S(m!["name"])!, S(m["state"])!))];

    private static void Fixture(string name, JsonNode node)
    {
        var dir = Environment.GetEnvironmentVariable("DELIVERABLES_FIXTURE_OUT");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>
    /// The live statuses, members and working flag (contract: status, waitingFor, working, members[] order running, lease,
    /// queued; owner in members when it works it). Web's Manager holds the lease and tells Ben in the same deliverable, whose
    /// run then waits for the lease; Cy (own deliverable) waits for the lease too; Dee (Cloud) asks the person and keeps
    /// running; Eve finds no free run slot. Then a dispatched item with its spec, an archived item from the payload, a
    /// completed, a closed and an expired deliverable. Writes the shell fixtures.
    /// </summary>
    [Fact]
    public async Task LiveStatuses_Members_AndWorking_ThroughTheRealDispatcher_AndTheShellFixtures()
    {
        string? benLease = null, cyLease = null;
        await using var rig = await Live.CreateAsync(slots: 4, r =>
        {
            r.Runner.On("Web/Manager", async (run, ct) =>
            {
                if (!run.Prompt.Contains("lead it", StringComparison.Ordinal)) return RunResult.Ok;
                Assert.StartsWith("ok: you hold the heavy-work lease", (await Tool(run, "lease", new { action = "acquire", what = "full test suite" })).Text);
                Assert.False((await Tool(run, "tell", new { member = "Ben", instruction = "Card 1: build the page" })).Error);
                await Tool(run, "progress", new { status = "Card 1 sent to Ben; running the suite" });
                await r.Release.Task.WaitAsync(ct);
                return RunResult.Ok;
            })
            .On("Web/Ben", async (run, ct) =>
            {
                benLease = (await Tool(run, "lease", new { action = "acquire", what = "build" })).Text;
                await r.Release.Task.WaitAsync(ct);
                return RunResult.Ok;
            })
            .On("Web/Cy", async (run, ct) =>
            {
                cyLease = (await Tool(run, "lease", new { action = "acquire", what = "image build" })).Text;
                await r.Release.Task.WaitAsync(ct);
                return RunResult.Ok;
            })
            .On("Cloud/Dee", async (run, ct) =>
            {
                await Tool(run, "needs_decision", new { question = "Dark or light by default?" });
                await r.Release.Task.WaitAsync(ct);
                return RunResult.Ok;
            });
        }, "Web:Ben:Cy", "Cloud:Dee:Eve");

        // Ended and backlog deliverables first (written straight to the log: nothing runs them).
        var spec = rig.Board.AddItem("Deliverables page", "**Why:** every team's work in one place.\n\n## Table\n- Status\n- Elapsed", "Web");
        var dispatched = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Web/Manager",
            JsonSerializer.Serialize(new { text = $"Backlog item {spec.Label} \"Deliverables page\" is dispatched to your team. It is a SPEC: cut it into cards." }),
            MessageTypes.PersonSource, null)).Seq;
        rig.Board.AddDispatch(spec.Id, "Web", dispatched, "you");
        rig.Store.Append(new NewMessage(MessageTypes.DeliverableCompleted, JsonSerializer.Serialize(new { delivered = "page shipped at 1a2b3c4" }), "Web/Manager", dispatched));
        var old = rig.Board.AddItem("Old export", "the old spec", null);
        rig.Board.SaveItem(old with { ArchivedAt = rig.Board.Now() });
        var archived = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Cloud/Manager",
            JsonSerializer.Serialize(new { text = $"Backlog item {old.Label} is dispatched to your team. It is a SPEC...", item = old.Label }), MessageTypes.PersonSource, null)).Seq;
        rig.Store.Append(new NewMessage(MessageTypes.DeliverableClosed, JsonSerializer.Serialize(new { reason = "Not now" }), MessageTypes.PersonSource, archived));
        var expired = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Cloud/Eve", JsonSerializer.Serialize(new { text = "Look at the flaky test" }), MessageTypes.PersonSource, null)).Seq;
        rig.Store.Append(new NewMessage(MessageTypes.DeliverableExpired, JsonSerializer.Serialize(new { reason = "expired", detail = "no activity for 3 days" }), MessageTypes.HostSource, expired));

        await rig.Engine.StartAsync(Ct);
        var lead = (await rig.Engine.PersonTellAsync("Web", "Manager", "lead it", ct: Ct)).Seq;
        await Wait.UntilAsync(() => benLease is not null, "Ben asks for the lease");
        var cy = (await rig.Engine.PersonTellAsync("Web", "Cy", "build the image", ct: Ct)).Seq;
        await Wait.UntilAsync(() => cyLease is not null, "Cy asks for the lease");
        var dee = (await rig.Engine.PersonTellAsync("Cloud", "Dee", "pick the theme", ct: Ct)).Seq;
        await Wait.UntilAsync(async () => S(Row(await rig.List(), dee)["status"]) == "needs-you", "Dee's question");
        var eve = (await rig.Engine.PersonTellAsync("Cloud", "Eve", "write the docs", ct: Ct)).Seq; // no free slot: 4 runs on
        await Wait.UntilAsync(async () => S(Row(await rig.List(), eve)["status"]) == "waiting", "Eve queued");
        Assert.StartsWith("queued, position 1", benLease);
        Assert.StartsWith("queued, position 2", cyLease);

        var all = await rig.List(new { state = "all" });

        // running: the Manager's run is on it; Ben's run in it waits for the lease. Members: running, then lease.
        var l = Row(all, lead);
        Assert.Equal(("running", null, true), (S(l["status"]), S(l["waitingFor"]), l["working"]!.GetValue<bool>()));
        Assert.Equal([("Manager", "running"), ("Ben", "lease")], Members(l));
        Assert.Equal("Manager", S(l["owner"]));
        Assert.Contains(l["busy"]!.AsArray(), b => S(b)!.Contains("Manager is running", StringComparison.Ordinal));
        // waiting / lease: every active run in it waits in the lease queue (still working: animate it).
        var c = Row(all, cy);
        Assert.Equal(("waiting", "lease", true), (S(c["status"]), S(c["waitingFor"]), c["working"]!.GetValue<bool>()));
        Assert.Equal([("Cy", "lease")], Members(c));
        // needs-you with a run still on it: the word is needs-you, working stays true.
        var d = Row(all, dee);
        Assert.Equal(("needs-you", true), (S(d["status"]), d["working"]!.GetValue<bool>()));
        Assert.NotNull(d["question"]);
        Assert.Equal([("Dee", "running")], Members(d));
        // waiting / slot: a delivery not started (no run slot): not working, Eve queued.
        var e = Row(all, eve);
        Assert.Equal(("waiting", "slot", false), (S(e["status"]), S(e["waitingFor"]), e["working"]!.GetValue<bool>()));
        Assert.Equal([("Eve", "queued")], Members(e));
        // Ended ones: nobody works them.
        Assert.Equal(("completed", "Deliverables page"), (S(Row(all, dispatched)["status"]), S(Row(all, dispatched)["title"])));
        Assert.Equal(("closed", "Old export", true), (S(Row(all, archived)["status"]), S(Row(all, archived)["title"]), Row(all, archived)["item"]!["archived"]!.GetValue<bool>()));
        Assert.Equal("expired", S(Row(all, expired)["status"]));
        Assert.All(new[] { dispatched, archived, expired }, w => Assert.Empty(Row(all, w)["members"]!.AsArray()));

        // The chips' counts and the exact-status filters agree with the rows.
        var counts = all["counts"]!.AsObject();
        Assert.Equal((7, 4, 1, 1, 2, 1, 2, 1, 1), (counts["all"]!.GetValue<int>(), counts["open"]!.GetValue<int>(), counts["needsYou"]!.GetValue<int>(),
            counts["done"]!.GetValue<int>(), counts["closed"]!.GetValue<int>(), counts["running"]!.GetValue<int>(), counts["waiting"]!.GetValue<int>(),
            counts["completed"]!.GetValue<int>(), counts["expired"]!.GetValue<int>()));
        Assert.Equal([lead], Ids(await rig.List(new { state = "RUNNING" })));                    // case-insensitive
        Assert.Equal(new[] { eve, cy }.Order(), Ids(await rig.List(new { state = "waiting" })).Order());
        Assert.Equal(new[] { lead, cy, dee, eve }.Order(), Ids(await rig.List(new { state = "Open" })).Order());
        // The members working a deliverable are searchable: Ben only works the Manager's deliverable.
        var benPage = await rig.List(new { q = "ben", state = "all" });
        // Should the filter find a second deliverable, print its root so the failure below says which one it is.
        if (Ids(benPage).Count != 1) foreach (var x in benPage["deliverables"]!.AsArray())
            Console.WriteLine($"DELIVERABLES q=ben extra:{x!.ToJsonString()} ROOT {rig.Store.Deliverable(x["id"]!.GetValue<long>())[0].Type} {rig.Store.Deliverable(x["id"]!.GetValue<long>())[0].Payload}");
        Assert.Equal([lead], Ids(await rig.List(new { q = "ben" })));
        // Its number finds it. The lead's seq races the start-up messages (7, 8 or 10), so a bare "7" also finds #17 (Eve's) as a
        // substring: any other row it finds must be one whose #id holds the number, never a text match. "#<id>" finds it alone.
        var leadId = lead.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var byNumber = Ids(await rig.List(new { q = leadId, state = "open" }));
        Assert.Contains(lead, byNumber);
        Assert.All(byNumber.Where(id => id != lead), id => Assert.Contains(leadId, "#" + id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.Equal([lead], Ids(await rig.List(new { q = "#" + leadId, state = "open" })));

        // deliverables.get: the running deliverable's cards (Manager's and Ben's) and its messages in words, newest first.
        var got = await rig.Call("deliverables.get", new { deliverable = lead });
        Assert.Equal(Row(await rig.List(new { state = "all" }), lead)["members"]!.ToJsonString(), got["deliverable"]!["members"]!.ToJsonString());
        var kinds = got["messages"]!.AsArray().Select(m => S(m!["kind"])).ToList();
        Assert.Equal("progress", kinds[0]);
        Assert.Contains("instruction", kinds);
        Assert.Contains(got["cards"]!.AsArray(), card => S(card!["member"]) == "Ben");
        Assert.Null(got["spec"]);
        var gotDispatched = await rig.Call("deliverables.get", new { deliverable = dispatched });
        Assert.StartsWith("**Why:**", S(gotDispatched["spec"]));
        Assert.Equal("completed", S(gotDispatched["messages"]![0]!["kind"]));

        // The shell's fixtures: the real core's answers.
        Fixture("list-all", all);
        Fixture("list-open", await rig.List(new { state = "open" }));
        Fixture("get-running", got);
        Fixture("get-dispatched", gotDispatched);
        Fixture("get-needs-you", await rig.Call("deliverables.get", new { deliverable = dee }));
        Fixture("get-closed-archived", await rig.Call("deliverables.get", new { deliverable = archived }));
        Fixture("ids", new JsonObject { ["lead"] = lead, ["cy"] = cy, ["dee"] = dee, ["eve"] = eve, ["dispatched"] = dispatched, ["archived"] = archived, ["expired"] = expired,
            ["web"] = rig.Teams["Web"], ["cloud"] = rig.Teams["Cloud"], ["spec"] = spec.Label, ["old"] = old.Label });

        rig.Release.SetResult();
        await Wait.UntilAsync(async () => !Row(await rig.List(), lead)["working"]!.GetValue<bool>(), "runs ended");
    }

    /// <summary>The time object on every row: breakdown in the contract's categories, adding up to overallMs exactly.</summary>
    [Fact]
    public async Task EveryRowsTime_BreakdownAddsUpToOverall_InTheContractsCategories()
    {
        await using var rig = await Live.CreateAsync(slots: 2, _ => { }, "Web:Ada");
        for (var i = 0; i < 3; i++)
        {
            var w = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Web/Ada", JsonSerializer.Serialize(new { text = $"job {i}" }), MessageTypes.PersonSource, null)).Seq;
            rig.Store.Append(new NewMessage(MessageTypes.Progress, JsonSerializer.Serialize(new { status = "on it" }), "Web/Ada", w));
            if (i == 0) rig.Store.Append(new NewMessage(MessageTypes.DeliverableCompleted, JsonSerializer.Serialize(new { delivered = "done" }), "Web/Ada", w));
        }
        await Task.Delay(30, Ct);
        var page = await rig.List(new { state = "all" });
        foreach (var row in page["deliverables"]!.AsArray())
        {
            var t = row!["time"]!.AsObject();
            var overall = t["overallMs"]!.GetValue<long>();
            var parts = t["breakdown"]!.AsArray();
            Assert.Equal(overall, parts.Sum(p => p!["ms"]!.GetValue<long>()));
            Assert.All(parts, p => Assert.Contains(S(p!["category"])!, Categories));
            Assert.All(parts, p => Assert.True(p!["ms"]!.GetValue<long>() > 0, "only categories with time"));
            Assert.Equal(S(row["state"]) == "open", t["open"]!.GetValue<bool>());
        }
        Assert.DoesNotContain("cost", page.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usage", page.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>item: the NEWEST dispatch of the deliverable wins, and a dispatch wins over the root payload's item.</summary>
    [Fact]
    public async Task TheItem_IsTheNewestDispatch_AndADispatchBeatsThePayload()
    {
        await using var rig = await Live.CreateAsync(slots: 2, _ => { }, "Web:Ada");
        var first = rig.Board.AddItem("First idea", "one", "Web");
        var second = rig.Board.AddItem("Second idea", "two", "Web");
        var w = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Web/Manager",
            JsonSerializer.Serialize(new { text = "Backlog item ... is dispatched", item = first.Label }), MessageTypes.PersonSource, null)).Seq;
        rig.Board.AddDispatch(first.Id, "Web", w, "you");
        rig.Board.AddDispatch(second.Id, "Web", w, "you");
        var row = Row(await rig.List(), w);
        Assert.Equal((second.Label, "Second idea"), (S(row["item"]!["label"]), S(row["title"])));
        Assert.Equal("two", S((await rig.Call("deliverables.get", new { deliverable = w }))["spec"]));
    }

    /// <summary>The clean title: the first non-empty line of the root, at most 100 characters ending in an ellipsis; an item's long title too.</summary>
    [Fact]
    public async Task TheCleanTitle_IsTheFirstNonEmptyLine_AtMost100Characters()
    {
        await using var rig = await Live.CreateAsync(slots: 2, _ => { }, "Web:Ada");
        var longLine = string.Concat(Enumerable.Repeat("abcdefghij", 15));
        var w = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Web/Ada", JsonSerializer.Serialize(new { text = "\n\n   \n" + longLine + "\nsecond" }), MessageTypes.PersonSource, null)).Seq;
        var item = rig.Board.AddItem(longLine, "spec", "Web");
        var d = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Web/Manager", JsonSerializer.Serialize(new { text = "Backlog item is dispatched" }), MessageTypes.PersonSource, null)).Seq;
        rig.Board.AddDispatch(item.Id, "Web", d, "you");
        var page = await rig.List();
        foreach (var id in new[] { w, d })
        {
            var title = S(Row(page, id)["title"])!;
            Assert.Equal(100, title.Length);
            Assert.EndsWith("…", title);
            Assert.StartsWith("abcdefghij", title);
        }
    }

    /// <summary>The text filter matches the owner (the team's Manager for a non-addressed one) and item.title; a bare id too.</summary>
    [Fact]
    public async Task TheTextFilter_MatchesOwner_ItemTitle_AndTheBareId()
    {
        await using var rig = await Live.CreateAsync(slots: 2, _ => { }, "Web:Ada", "Cloud:Bob");
        var item = rig.Board.AddItem("Refund report", "spec", "Web");
        var w = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Web/Ada", JsonSerializer.Serialize(new { text = "please do the thing" }), MessageTypes.PersonSource, null)).Seq;
        rig.Board.AddDispatch(item.Id, "Web", w, "you");
        var other = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Cloud/Bob", JsonSerializer.Serialize(new { text = "unrelated" }), MessageTypes.PersonSource, null)).Seq;
        Assert.Equal([w], Ids(await rig.List(new { q = "refund REPORT" })));
        Assert.Equal([w], Ids(await rig.List(new { q = "ada" })));       // owner
        Assert.Equal([other], Ids(await rig.List(new { q = "bob cloud" })));
        Assert.Contains(w, Ids(await rig.List(new { q = w.ToString(System.Globalization.CultureInfo.InvariantCulture) })));
        Assert.Equal(2, Ids(await rig.List(new { q = "   " })).Count); // blank: no filter
    }

    /// <summary>The limit: 1-1000, default 200; outside that clamped (contract: "1-1000"), a non-number refused; open rows never cut.</summary>
    [Fact]
    public async Task TheLimit_DefaultsTo200_IsClamped_AndNeverCutsAnOpenRow()
    {
        await using var rig = await Live.CreateAsync(slots: 2, _ => { }, "Web:Ada");
        var open = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Web/Ada", JsonSerializer.Serialize(new { text = "the oldest, open" }), MessageTypes.PersonSource, null)).Seq;
        rig.Store.InTransaction(() =>
        {
            for (var i = 0; i < 205; i++)
            {
                var w = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Web/Ada", JsonSerializer.Serialize(new { text = $"job {i}" }), MessageTypes.PersonSource, null)).Seq;
                rig.Store.Append(new NewMessage(MessageTypes.DeliverableCompleted, JsonSerializer.Serialize(new { delivered = "ok" }), "Web/Ada", w));
            }
        });
        var page = await rig.List();
        Assert.Equal(201, Ids(page).Count);              // the newest 200 + the old open one
        Assert.Contains(open, Ids(page));
        Assert.True(page["truncated"]!.GetValue<bool>());
        Assert.Equal(206, page["counts"]!["all"]!.GetValue<int>());
        Assert.Equal(2, Ids(await rig.List(new { limit = 0 })).Count);    // clamped to 1 (+ the open one)
        Assert.Equal(206, Ids(await rig.List(new { limit = 5000 })).Count);
        Assert.False((await rig.List(new { limit = 5000 }))["truncated"]!.GetValue<bool>());
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => rig.List(new { limit = 1.5 }))).Code);
    }

    /// <summary>deliverables.get's errors and its message kinds in words (blocked, decision, stopped, expired by the host).</summary>
    [Fact]
    public async Task Get_Errors_AndMessageKindsInWords()
    {
        await using var rig = await Live.CreateAsync(slots: 2, _ => { }, "Web:Ada");
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => rig.Call("deliverables.get", new { }))).Code);
        // A thread addressed to no team: in no list, and get says no_such_deliverable.
        var orphan = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Nowhere/Zed", JsonSerializer.Serialize(new { text = "lost" }), MessageTypes.PersonSource, null)).Seq;
        Assert.DoesNotContain(orphan, Ids(await rig.List(new { state = "all" })));
        Assert.Equal("no_such_deliverable", (await Assert.ThrowsAsync<RpcException>(() => rig.Call("deliverables.get", new { deliverable = orphan }))).Code);

        var w = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Web/Ada", JsonSerializer.Serialize(new { text = "go" }), MessageTypes.PersonSource, null)).Seq;
        Message Say(string type, string source, object payload) => rig.Store.Append(new NewMessage(type, JsonSerializer.Serialize(payload), source, rig.Store.Deliverable(w)[^1].Seq));
        Say(MessageTypes.Blocked, "Web/Ada", new { reason = "no network" });
        Say(MessageTypes.NeedsDecision, "Web/Ada", new { question = "which colour?" });
        Say(MessageTypes.Failed, "Web/Ada", new { reason = BusEngine.StoppedReason, failureClass = FailureClasses.Stopped });
        Say(MessageTypes.Progress, "Web/Ada", new { status = new string('x', 400) });
        Say(MessageTypes.DeliverableExpired, MessageTypes.HostSource, new { reason = "expired", detail = "no activity for 3 days" });
        var got = await rig.Call("deliverables.get", new { deliverable = w });
        var m = got["messages"]!.AsArray();
        Assert.Equal(["expired", "progress", "stopped", "decision", "blocked", "instruction"], m.Select(x => S(x!["kind"])));
        Assert.Equal(("host", null, "no activity for 3 days"), (S(m[0]!["from"]), S(m[0]!["member"]), S(m[0]!["text"])));
        Assert.True(S(m[1]!["text"])!.Length <= 241, "at most 240 characters plus the ellipsis");
        Assert.EndsWith("…", S(m[1]!["text"]));
        Assert.Equal(("Ada", "which colour?"), (S(m[3]!["from"]), S(m[3]!["text"])));
        Assert.Equal("expired", S(got["deliverable"]!["status"]));
        Assert.All(m, x => Assert.Equal(["seq", "at", "kind", "from", "member", "text"], x!.AsObject().Select(kv => kv.Key)));
    }

    /// <summary>
    /// A Visitor (read access) over the Cloud Link: both reads answer with the backlog flag; the page's four actions are
    /// refused; both calls are reads in the audit.
    /// </summary>
    [Fact]
    public async Task AVisitor_ReadsBoth_ButEveryActionIsRefused_AndBothAreAuditReads()
    {
        await using var rig = await Live.CreateAsync(slots: 2, _ => { }, "Web:Ada");
        var item = rig.Board.AddItem("Commit view", "the spec", "Web");
        var w = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Web/Ada", JsonSerializer.Serialize(new { text = "do it" }), MessageTypes.PersonSource, null)).Seq;
        rig.Board.AddDispatch(item.Id, "Web", w, "you");
        var dir = Directory.CreateTempSubdirectory("yawble-deliverables-visitor-").FullName;
        var link = new CloudLink(dir, (m, p, ct) => rig.Rpc.DispatchAsync(m, p, ct), new AppEvents());
        try
        {
            async Task<JsonObject> Remote(string method, JsonObject p, string access)
            {
                JsonObject? sent = null;
                await link.AnswerAsync(new JsonObject
                {
                    ["type"] = "call", ["session"] = "v-" + access, ["id"] = 1, ["method"] = method, ["params"] = p, ["user"] = "visitor@example.com", ["access"] = access,
                }, m => { sent = m; return Task.CompletedTask; }, Ct);
                return sent!;
            }
            var list = await Remote("deliverables.list", new JsonObject { ["state"] = "all" }, "read");
            Assert.Null(list["error"]);
            Assert.Equal([w], Ids(list["result"]!.AsObject()));
            Assert.True(list["result"]!["backlog"]!.GetValue<bool>());
            var got = await Remote("deliverables.get", new JsonObject { ["deliverable"] = w }, "read");
            Assert.Null(got["error"]);
            Assert.Equal(("the spec", true), (S(got["result"]!["spec"]), got["result"]!["backlog"]!.GetValue<bool>()));
            Fixture("visitor-list", list["result"]!.DeepClone());
            Fixture("visitor-get", got["result"]!.DeepClone());

            foreach (var action in new[] { "bus.nudgeDeliverable", "bus.deliverableExtend", "bus.stopDeliverable", "bus.closeDeliverable" })
            {
                Assert.NotNull(RemoteAccess.Refusal(action, null, allowHostTerminals: false, RemoteAccess.Read));
                var refused = await Remote(action, new JsonObject { ["teamId"] = rig.Teams["Web"], ["deliverable"] = w }, "read");
                Assert.NotNull(refused["error"]);
                Assert.False(Yawble.Core.Provenance.AuditLog.IsReadOnly(action), $"{action} is a change");
            }
            Assert.Equal("open", S(Row(await rig.List(), w)["state"])); // nothing was closed
        }
        finally
        {
            await link.DisposeAsync();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Assert.True(Yawble.Core.Provenance.AuditLog.IsReadOnly("deliverables.list"));
        Assert.True(Yawble.Core.Provenance.AuditLog.IsReadOnly("deliverables.get"));
    }

    /// <summary>1,000 deliverables across three teams, a third of them open: the list and a get read in the same number of statements as for 12.</summary>
    [Fact]
    public async Task AThousandDeliverables_AFilteredListAndAGet_UseAFixedNumberOfQueries()
    {
        await using var rig = await Live.CreateAsync(slots: 2, _ => { }, "Alpha:Ada", "Beta:Bob", "Gamma:Gus");
        string[] teams = ["Alpha", "Beta", "Gamma"], members = ["Ada", "Bob", "Gus"];
        var item = rig.Board.AddItem("Big item", "spec", null);
        long lastW = 0, firstW = 0;
        void Seed(int from, int n)
        {
            var made = new List<(int I, long W)>();
            rig.Store.InTransaction(() =>
            {
                for (var i = from; i < from + n; i++)
                {
                    var t = i % 3;
                    var w = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + teams[t] + "/" + members[t], JsonSerializer.Serialize(new { text = $"job {i}" }), MessageTypes.PersonSource, null)).Seq;
                    rig.Store.Append(new NewMessage(MessageTypes.Progress, JsonSerializer.Serialize(new { status = "working" }), teams[t] + "/" + members[t], w));
                    if (i % 3 != 0) rig.Store.Append(new NewMessage(MessageTypes.DeliverableCompleted, JsonSerializer.Serialize(new { delivered = "ok" }), teams[t] + "/" + members[t], w));
                    made.Add((i, w));
                    lastW = w;
                    if (i == 0) firstW = w;
                }
            });
            foreach (var (i, w) in made) if (i % 10 == 0) rig.Board.AddDispatch(item.Id, teams[i % 3], w, "you");
        }
        long Queries() => rig.Store.Queries + rig.Board.Queries;
        async Task<long> Measure(Func<Task> f) { var b = Queries(); await f(); return Queries() - b; }

        Seed(0, 12);
        await rig.Call("deliverables.list", new { state = "all", limit = 1000 }); // the board projects what was written
        var smallList = await Measure(() => rig.Engine.DeliverablesPageAsync(null, "job", "all", 1000, Ct));
        var smallGet = await Measure(() => rig.Engine.DeliverableRowAsync(firstW, Ct));
        Seed(12, 988);
        await rig.Call("deliverables.list", new { state = "all", limit = 1000 });
        var watch = Stopwatch.StartNew();
        var bigList = await Measure(() => rig.Engine.DeliverablesPageAsync(null, "job", "all", 1000, Ct));
        watch.Stop();
        var bigGet = await Measure(() => rig.Engine.DeliverableRowAsync(firstW, Ct));
        var bigList2 = await Measure(() => rig.Engine.DeliverablesPageAsync(null, "job", "all", 1000, Ct));
        var bigAllNoQ = await Measure(() => rig.Engine.DeliverablesPageAsync(null, null, "all", 1000, Ct));
        Assert.Equal(smallList, bigList);
        Assert.Equal(bigList, bigList2);
        Assert.Equal(bigList, bigAllNoQ);
        Assert.Equal(smallGet, bigGet);
        var rpcWatch = Stopwatch.StartNew();
        var page = await rig.Call("deliverables.list", new { state = "open", limit = 1 });
        rpcWatch.Stop();
        Assert.Equal(334, Ids(page).Count); // every open one past limit 1
        TestContext.Current.SendDiagnosticMessage($"1,000 deliverables: list {bigList} queries ({watch.ElapsedMilliseconds} ms engine, {rpcWatch.ElapsedMilliseconds} ms rpc open-only), get {bigGet} queries");
    }
}
