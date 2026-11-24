using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// B104 (card 476): workflows.list and workflows.get, the Workflows page's reads across every team (contract
/// B104/contract.md). Fakes only: a temp database, a fake clock, a scripted runner that is never started (each
/// workflow's messages are written straight to the log), the real board store and the real UiRpc over RpcDispatcher.
/// </summary>
public class WorkflowsPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public WorkflowExpiryTests.ManualClock Clock { get; } = new(Start);
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

        /// <summary>A workflow started by the person telling a member something; payload extras (item) merged in.</summary>
        public long Tell(string team, string member, string text, string? item = null)
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            var payload = new JsonObject { ["text"] = text };
            if (item is not null) payload["item"] = item;
            return Store.Append(new NewMessage(MessageTypes.InstructionPrefix + team + "/" + member, payload.ToJsonString(), MessageTypes.PersonSource, null)).Seq;
        }

        public Message Say(long workflow, string type, string source, object payload)
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            var last = Store.Workflow(workflow)[^1].Seq;
            return Store.Append(new NewMessage(type, JsonSerializer.Serialize(payload), source, last));
        }

        public void Complete(long w, string by) => Say(w, MessageTypes.WorkflowCompleted, by, new { delivered = "done and dusted" });

        public void Close(long w) => Say(w, MessageTypes.WorkflowClosed, MessageTypes.PersonSource, new { reason = "not needed" });

        public void Expire(long w) =>
            Say(w, MessageTypes.WorkflowExpired, MessageTypes.HostSource, new { reason = "expired", detail = "no activity for 3 days", endedAt = Start.ToString("O"), expiredAt = Clock.Now.ToString("O") });

        public async Task<JsonObject> Call(string method, object? args = null) =>
            (await Rpc.DispatchAsync(method, args is null ? null : JsonSerializer.SerializeToNode(args)!.AsObject(), Ct))!.AsObject();

        public async Task<JsonObject> List(object? args = null) => await Call("workflows.list", args ?? new { });

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static List<long> Ids(JsonObject page) => [.. page["workflows"]!.AsArray().Select(w => w!["id"]!.GetValue<long>())];

    private static JsonObject Row(JsonObject page, long id) => page["workflows"]!.AsArray().Single(w => w!["id"]!.GetValue<long>() == id)!.AsObject();

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
        Assert.NotNull(r["time"]);      // B086's time object from the board
        Assert.Equal("completed", S(Row(all, done)["status"]));
        Assert.Equal("closed", S(Row(all, closed)["status"]));
        Assert.Equal("expired", S(Row(all, expired)["status"]));
        Assert.Equal(("needs-you", "Beta"), (S(Row(all, asking)["status"]), S(Row(all, asking)["team"])));
        Assert.NotNull(Row(all, asking)["question"]);
        Assert.Equal("stopped", S(Row(all, stopped)["status"]));
        // B126: an undelivered delivery whose member is not in the run queue is pending, not "queued for a run slot".
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
        var spec = rig.Board.AddItem("Workflows page", "## Why\nEvery team's workflows in one table.", "Alpha");
        var dispatched = rig.Tell("Alpha", "Manager", $"Backlog item {spec.Label} \"Workflows page\" is dispatched to your team. It is a SPEC: cut it into cards.");
        rig.Board.AddDispatch(spec.Id, "Alpha", dispatched, "you");

        var old = rig.Board.AddItem("Old idea", "stale", null);
        rig.Board.SaveItem(old with { ArchivedAt = rig.Board.Now() });
        var fromPayload = rig.Tell("Alpha", "Manager", "Backlog item B002 is dispatched to your team. It is a SPEC...", item: old.Label);
        var told = rig.Tell("Alpha", "Ada", "Just a quick question");
        var gone = rig.Tell("Alpha", "Ada", "Names an item that is no more", item: "B999");

        var page = await rig.List();
        var d = Row(page, dispatched);
        Assert.Equal("Workflows page", S(d["title"])); // the item's title, not the dispatch sentence
        Assert.Equal((spec.Label, "Workflows page", false), (S(d["item"]!["label"]), S(d["item"]!["title"]), d["item"]!["archived"]!.GetValue<bool>()));
        Assert.Null(d["item"]!["spec"]); // the spec is workflows.get's
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
    public async Task EveryOpenWorkflow_IsListed_PastTheLimit()
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

        var got = await rig.Call("workflows.get", new { workflow = w }); // the board has read the bus: its cards are the projector's
        var row = got["workflow"]!.AsObject();
        Assert.Equal((w, "Commit view", "Alpha", "B001"), (row["id"]!.GetValue<long>(), S(row["title"]), S(row["team"]), S(row["item"]!["label"])));
        // Exactly the row the list gives.
        Assert.Equal(Row(await rig.List(), w).ToJsonString(), row.ToJsonString());

        var cards = got["cards"]!.AsArray();
        Assert.Equal(rig.Board.CardsOfWorkflow(w).Select(c => c.Id).Order(), cards.Select(c => c!["id"]!.GetValue<long>()).Order());
        Assert.DoesNotContain(rig.Board.CardsOfWorkflow(other).Single().Id, cards.Select(c => c!["id"]!.GetValue<long>()));
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

        // A workflow without an item has no spec; one that does not exist is refused.
        Assert.Null((await rig.Call("workflows.get", new { workflow = other }))["spec"]);
        Assert.Equal("no_such_workflow", (await Assert.ThrowsAsync<RpcException>(() => rig.Call("workflows.get", new { workflow = 99_999 }))).Code);
    }

    [Fact]
    public async Task Get_SaysAClosedWorkflowsCloseInWords_AndKeepsAtMostThirtyMessages()
    {
        await using var rig = await Rig.CreateAsync("Alpha:Ada");
        var w = rig.Tell("Alpha", "Ada", "chatty");
        for (var i = 0; i < 40; i++) rig.Say(w, MessageTypes.Progress, "Alpha/Ada", new { status = $"step {i}" });
        rig.Close(w);
        var got = await rig.Call("workflows.get", new { workflow = w });
        var messages = got["messages"]!.AsArray();
        Assert.Equal(UiRpc.WorkflowMessages, messages.Count);
        Assert.Equal(("closed", "person", "not needed"), (S(messages[0]!["kind"]), S(messages[0]!["from"]), S(messages[0]!["text"])));
        Assert.Equal("step 39", S(messages[1]!["text"]));
        Assert.Equal("closed", S(got["workflow"]!["status"]));
    }

    [Theory]
    [InlineData("workflows.list")]
    [InlineData("workflows.get")]
    public void AVisitor_MayReadBoth(string method)
    {
        Assert.True(RemoteAccess.OnAllowList(method));
        Assert.True(RemoteAccess.Reads(method, null));
        Assert.Null(RemoteAccess.Refusal(method, null, allowHostTerminals: false, RemoteAccess.Read));
        Assert.True(Yawble.Core.Provenance.AuditLog.IsReadOnly(method));
    }

    [Fact]
    public async Task OverTheCloudLink_AVisitorGetsBoth_WithTheBacklogFlag()
    {
        await using var rig = await Rig.CreateAsync("Alpha:Ada");
        var item = rig.Board.AddItem("Commit view", "the spec", "Alpha");
        var w = rig.Tell("Alpha", "Ada", "do it");
        rig.Board.AddDispatch(item.Id, "Alpha", w, "you");
        var dir = Directory.CreateTempSubdirectory("yawble-workflows-link-").FullName;
        var link = new CloudLink(dir, (m, p, ct) => rig.Rpc.DispatchAsync(m, p, ct), new AppEvents());
        try
        {
            async Task<JsonObject> Remote(string method, JsonObject p)
            {
                JsonObject? sent = null;
                await link.AnswerAsync(new JsonObject
                {
                    ["type"] = "call", ["session"] = "v1", ["id"] = 1, ["method"] = method, ["params"] = p, ["user"] = "visitor@example.com", ["access"] = "read",
                }, m => { sent = m; return Task.CompletedTask; }, Ct);
                Assert.Null(sent!["error"]);
                return sent["result"]!.AsObject();
            }
            var list = await Remote("workflows.list", []);
            Assert.Equal([w], Ids(list));
            Assert.True(list["backlog"]!.GetValue<bool>());
            var got = await Remote("workflows.get", new JsonObject { ["workflow"] = w });
            Assert.Equal(("the spec", true), (S(got["spec"]), got["backlog"]!.GetValue<bool>()));
        }
        finally
        {
            await link.DisposeAsync();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void TheLinksShaping_SetsTheBacklogFlag_ForEveryAccess_AndKeepsTheSpecWhileTheViewerMayReadTheBacklog()
    {
        // Every Cloud Link viewer, a Visitor too, may read the backlog today (backlog.item is a read): the flag is true and
        // the spec stays. The flag is the core's answer, so the shell never decides this itself.
        var got = new JsonObject { ["spec"] = "secret plans", ["backlog"] = true };
        RemoteAccess.ShapeResult("workflows.get", got, showCode: true, RemoteAccess.Read);
        Assert.True(got["backlog"]!.GetValue<bool>()); // a Visitor reads the backlog
        Assert.Equal("secret plans", S(got["spec"]));
        var list = new JsonObject { ["backlog"] = true };
        RemoteAccess.ShapeResult("workflows.list", list, showCode: false, RemoteAccess.Full);
        Assert.True(list["backlog"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AThousandWorkflowsAcrossTeams_AreReadInAFixedNumberOfQueries()
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
        var small = await rig.Engine.WorkflowsPageAsync(null, null, null, BusEngine.MaxPageLimit, Ct);
        var smallQueries = Queries() - before;
        Assert.Equal(12, small.Rows.Count);

        Seed(1000 - 12);
        before = Queries();
        var watch = Stopwatch.StartNew();
        var big = await rig.Engine.WorkflowsPageAsync(null, null, null, BusEngine.MaxPageLimit, Ct);
        watch.Stop();
        var bigQueries = Queries() - before;
        Assert.Equal(1000, big.Rows.Count);
        Assert.All(big.Rows, r => Assert.NotNull(r.Summary.Time));
        Assert.Equal(2 + 99, big.Rows.Count(r => r.Item is not null)); // every tenth of each batch (12, then 988) was dispatched
        // No per-workflow query: the same statements for 1,000 workflows as for 12, and only a handful of them.
        Assert.Equal(smallQueries, bigQueries);
        Assert.InRange(bigQueries, 1, 20);
        TestContext.Current.SendDiagnosticMessage($"workflows.list over 1,000 workflows: {bigQueries} queries, {watch.ElapsedMilliseconds} ms");
    }
}
