using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>The board: cards follow the bus by Yawble's rules; the person's moves, edits and comments wake the Manager.</summary>
public class BoardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private long _seq = 100;

    private Message M(string type, object payload, string source, long workflow) =>
        new(++_seq, type, JsonSerializer.Serialize(payload), source, workflow, null, 0, DateTimeOffset.UtcNow);

    private Message Tell(string member, string text, long workflow, string from = "payments/Manager", object? extra = null)
    {
        var payload = new JsonObject { ["text"] = text };
        if (extra is not null) foreach (var (k, v) in JsonSerializer.SerializeToNode(extra)!.AsObject()) payload[k] = v?.DeepClone();
        return new(++_seq, "agent.instruction.payments/" + member, payload.ToJsonString(), from, workflow, null, 0, DateTimeOffset.UtcNow);
    }

    private static (TempDb Db, BoardStore Store, BoardProjector P) Board()
    {
        var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        return (db, store, new BoardProjector(store));
    }

    [Fact]
    public void ACardFollowsItsMembersWork_AndALoudStatusIsKeptUntilNewWork()
    {
        var (db, store, p) = Board();
        using var _ = db;
        p.Apply(Tell("DeveloperPriya", "# Add a README\nwith setup steps", 7));
        var card = Assert.Single(store.Cards("payments"));
        Assert.Equal(("Add a README", "DeveloperPriya", "queued", Lanes.Todo), (card.Title, card.Member, card.Status, card.Lane));

        p.Apply(M(MessageTypes.Progress, new { status = "writing" }, "payments/DeveloperPriya", 7));
        Assert.Equal(("running", Lanes.InProgress), (store.Card(card.Id)!.Status, store.Card(card.Id)!.Lane));

        p.Apply(M(MessageTypes.NeedsDecision, new { question = "MIT or Apache?" }, "payments/DeveloperPriya", 7));
        p.Apply(M(MessageTypes.Completed, new { reason = "ok" }, "payments/DeveloperPriya", 7)); // quieter: does not erase it
        Assert.Equal(("needs-decision", Lanes.NeedsYou), (store.Card(card.Id)!.Status, store.Card(card.Id)!.Lane));

        p.Apply(Tell("DeveloperPriya", "MIT", 7, from: "person")); // the answer is new work on the same card
        Assert.Equal(("queued", Lanes.Todo), (store.Card(card.Id)!.Status, store.Card(card.Id)!.Lane));
        p.Apply(M(MessageTypes.Handback, new { delivered = "README on members/developerpriya" }, "payments/DeveloperPriya", 7));
        Assert.Equal(("handback", Lanes.Done), (store.Card(card.Id)!.Status, store.Card(card.Id)!.Lane));
        Assert.Single(store.Cards("payments"));
        Assert.Contains(store.Trail(card.Id), t => t.Kind == "asked" && t.Text == "MIT or Apache?");
    }

    [Fact]
    public void APlannedCardIsClaimedByATellThatNamesIt()
    {
        var (db, store, p) = Board();
        using var _ = db;
        var planned = store.AddCard("payments", null, "B007", "Licence file", "Add LICENSE", "queued", Lanes.Todo, 5);
        p.Apply(Tell("TesterWren", "Check the licence", 5, extra: new { card = planned.Id }));
        var card = Assert.Single(store.Cards("payments"));
        Assert.Equal((planned.Id, "TesterWren", "B007"), (card.Id, card.Member, card.Item));
        Assert.Contains(store.Trail(card.Id), t => t.Kind == "assigned");
    }

    [Fact]
    public void APlacedCardKeepsItsLane_ExceptForAGiveUp()
    {
        var (db, store, p) = Board();
        using var _ = db;
        p.Apply(Tell("DeveloperPriya", "Refactor", 9));
        var card = store.Cards("payments")[0];
        store.Save(card with { Lane = Lanes.Done, Placed = true });
        p.Apply(M(MessageTypes.Progress, new { status = "still going" }, "payments/DeveloperPriya", 9));
        Assert.Equal(Lanes.Done, store.Card(card.Id)!.Lane);
        p.Apply(M(MessageTypes.Blocked, new { reason = "no access" }, "payments/DeveloperPriya", 9));
        Assert.Equal(("blocked", Lanes.NeedsYou), (store.Card(card.Id)!.Status, store.Card(card.Id)!.Lane));
    }

    [Fact]
    public void TheWorkflowDeclaredCompleteFinishesItsCards_AndItsBacklogItem()
    {
        var (db, store, p) = Board();
        using var _ = db;
        var item = store.AddItem("README", "spec", null);
        store.SaveItem(item with { State = "ready", DispatchedTeam = "payments", DispatchedWorkflow = 11 });
        p.Apply(Tell("Manager", "Backlog item B001 dispatched", 11, from: "person", extra: new { item = "B001" })); // makes no card
        Assert.Empty(store.Cards("payments"));
        p.Apply(Tell("DeveloperPriya", "Write it", 11));
        p.Apply(Tell("TesterWren", "Check it", 11));
        p.Apply(M(MessageTypes.Failed, new { reason = "timed out" }, "payments/TesterWren", 11));
        p.Apply(M(MessageTypes.WorkflowCompleted, new { delivered = "README" }, "payments/Manager", 11));
        var cards = store.Cards("payments").ToDictionary(c => c.Member!);
        Assert.Equal("B001", cards["DeveloperPriya"].Item);
        Assert.Equal(Lanes.Done, cards["DeveloperPriya"].Lane);
        Assert.Equal(Lanes.NeedsYou, cards["TesterWren"].Lane); // a failed card still waits on the person
        Assert.Equal("declared", store.Item(item.Id)!.State);
    }

    [Fact]
    public void AnythingTheManagerSaysMeansItHasSeenThePersonsEdits()
    {
        var (db, store, p) = Board();
        using var _ = db;
        p.Apply(Tell("DeveloperPriya", "Docs", 3));
        var card = store.Cards("payments")[0];
        store.Save(card with { AwaitingManager = true });
        p.Apply(Tell("Manager", "The person moved card", 3, from: "person", extra: new { about = card.Id })); // the wake: no card
        Assert.True(store.Card(card.Id)!.AwaitingManager);
        p.Apply(M(MessageTypes.Progress, new { status = "re-planning" }, "payments/Manager", 3));
        Assert.False(store.Card(card.Id)!.AwaitingManager);
        Assert.Single(store.Cards("payments"));
    }

    [Theory]
    [InlineData("B007", 7L)]
    [InlineData("b12", 12L)]
    [InlineData("3", 3L)]
    [InlineData("card", null)]
    public void ItemLabels(string label, long? id) => Assert.Equal(id, BoardStore.ItemId(label));

    // ---------------------------------------------------------------- the service, on a real bus

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusEngine Bus { get; private set; } = null!;
        public BoardStore Store { get; private set; } = null!;
        public BoardService Service { get; private set; } = null!;

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            rig.Bus = new BusEngine(rig.Db.OpenStore(), new UnavailableRunner());
            await rig.Bus.StartAsync(Ct);
            await rig.Bus.CreateTeamAsync("payments", "Manager", "claude", null, Ct);
            rig.Store = new BoardStore(rig.Db.ConnectionString);
            rig.Service = new BoardService(rig.Store, rig.Bus);
            return rig;
        }

        public Task<JsonNode?> Call(string method, object p) => Service.DispatchAsync(method, JsonSerializer.SerializeToNode(p)!.AsObject(), Ct);

        public async ValueTask DisposeAsync()
        {
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    [Fact]
    public async Task TheBacklog_PendingThenReady_DispatchedToTheManagerAsASpec()
    {
        await using var rig = await Rig.StartAsync();
        var added = (await rig.Call("backlog.add", new { title = "Add a README", body = "Setup steps and a licence" }))!;
        var id = added["id"]!.GetValue<long>();
        Assert.Equal("B001", added["label"]!.GetValue<string>());
        var teamId = (await rig.Bus.TeamsAsync(Ct)).Single().Id;
        var refused = await Assert.ThrowsAsync<RpcException>(() => rig.Call("backlog.dispatch", new { id, teamId }));
        Assert.Equal("not_ready", refused.Code);

        await rig.Call("backlog.edit", new { id, state = "ready" });
        var sent = (await rig.Call("backlog.dispatch", new { id, teamId }))!;
        Assert.Equal("payments", sent["dispatchedTeam"]!.GetValue<string>());
        var told = (await rig.Bus.MessagesFromAsync(0, 50, Ct)).Single(m => m.Type == "agent.instruction.payments/Manager");
        Assert.Contains("B001", told.Payload);
        Assert.Contains("\"item\":\"B001\"", told.Payload);

        await rig.Call("backlog.add", new { title = "Second" });
        var moved = (await rig.Call("backlog.move", new { id, direction = "down" }))!.AsArray();
        Assert.Equal("Second", moved[0]!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task AMemberReadsItsCardInFull_DescriptionAndTrail()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        BoardService? service = null;
        string? shown = null, other = null;
        var runner = new ScriptedRunner().On("payments/Ada", async (run, ct) =>
        {
            var card = store.Cards("payments").Single();
            shown = await service!.BoardToolAsync(run, new JsonObject { ["action"] = "show", ["card"] = card.Id }, ct);
            try { await service.BoardToolAsync(run, new JsonObject { ["action"] = "show", ["card"] = 9999 }, ct); }
            catch (BusRefusal e) { other = e.Message; }
            return RunResult.Ok;
        });
        await using var bus = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        service = new BoardService(store, bus);
        await bus.CreateTeamAsync("payments", "Manager", "claude", null, Ct);
        await bus.AddMemberAsync("payments", "Ada", ct: Ct);
        var longTitle = "remote-ui stage pulls the registry image pinned by remote-ui.lock.json; deploy.ps1 checks it exists first";
        var planned = store.AddCard("payments", null, null, longTitle, "1. FROM registry.example/web-ui:${REMOTE_UI_COMMIT}\n2. deploy.ps1 reads the lock file", "queued", Lanes.Todo, null);
        store.AddTrail(planned.Id, "Manager", "planned", "from the person's decision");
        await bus.StartAsync(Ct);
        await bus.PersonTellAsync("payments", "Ada", "Do card " + planned.Id, ct: Ct);
        await Wait.UntilAsync(() => other is not null, "Ada read the card");

        Assert.StartsWith($"card {planned.Id}: ", shown);
        Assert.Contains("FROM registry.example/web-ui:${REMOTE_UI_COMMIT}", shown); // the description, in full
        Assert.Contains("deploy.ps1 reads the lock file", shown);
        Assert.Contains("from the person's decision", shown); // and its trail
        Assert.Contains("no card 9999", other);
    }

    [Fact]
    public async Task EveryDispatchIsKept_InTheItemsHistory_WithItsWorkflow()
    {
        await using var rig = await Rig.StartAsync();
        var id = (await rig.Call("backlog.add", new { title = "Health endpoint", body = "GET /health returns 200" }))!["id"]!.GetValue<long>();
        await rig.Call("backlog.edit", new { id, state = "ready" });
        var teamId = (await rig.Bus.TeamsAsync(Ct)).Single().Id;
        var first = (await rig.Call("backlog.dispatch", new { id, teamId }))!["dispatchedWorkflow"]!.GetValue<long>();
        var second = (await rig.Call("backlog.dispatch", new { id, teamId, again = true }))!;
        Assert.Equal(2, second["dispatchCount"]!.GetValue<int>());

        var detail = (await rig.Call("backlog.item", new { id }))!;
        var dispatches = detail["dispatches"]!.AsArray();
        Assert.Equal(2, dispatches.Count);
        Assert.Equal(second["dispatchedWorkflow"]!.GetValue<long>(), dispatches[0]!["workflow"]!.GetValue<long>()); // newest first
        Assert.Equal(first, dispatches[1]!["workflow"]!.GetValue<long>());
        Assert.Equal("payments", dispatches[0]!["team"]!.GetValue<string>());
        Assert.False(dispatches[0]!["teamGone"]!.GetValue<bool>());
        // The rig's runner fails at once, but the Manager's run is still the workflow's and is counted.
        var runs = dispatches[0]!["runs"]!.GetValue<int>();
        Assert.True(runs <= 1, $"runs: {runs}");
        if (runs == 1) Assert.Equal(["Manager"], dispatches[0]!["members"]!.AsArray().Select(m => m!.GetValue<string>()));
    }

    [Fact]
    public async Task DispatchingToANewTeam_MakesTheTeam_AndTellsItsManager()
    {
        await using var rig = await Rig.StartAsync();
        var id = (await rig.Call("backlog.add", new { title = "Spin out billing" }))!["id"]!.GetValue<long>();
        await Assert.ThrowsAsync<RpcException>(() => rig.Call("backlog.dispatchNew", new { id, name = "billing" })); // not ready
        await rig.Call("backlog.edit", new { id, state = "ready" });
        var refused = await Assert.ThrowsAsync<RpcException>(() => rig.Call("backlog.dispatchNew", new { id, name = "Payments" }));
        Assert.Equal("exists", refused.Code);

        var sent = (await rig.Call("backlog.dispatchNew", new { id, name = "billing" }))!;
        Assert.Equal("billing", sent["dispatchedTeam"]!.GetValue<string>());
        Assert.Contains(await rig.Bus.TeamsAsync(Ct), t => t.Name == "billing");
        Assert.Contains(await rig.Bus.MessagesFromAsync(0, 50, Ct), m => m.Type == "agent.instruction.billing/Manager");
    }

    [Fact]
    public async Task AnItemIsPlacedBetweenTwoOthers_AndTitlesKeep200Characters()
    {
        await using var rig = await Rig.StartAsync();
        async Task<long> Add(string t) => (await rig.Call("backlog.add", new { title = t }))!["id"]!.GetValue<long>();
        var a = await Add("A");
        var b = await Add("B");
        var c = await Add("C");
        var placed = (await rig.Call("backlog.position", new { id = c, after = a, before = b }))!.AsArray();
        Assert.Equal(["A", "C", "B"], placed.Select(x => x!["title"]!.GetValue<string>()));
        var top = (await rig.Call("backlog.position", new { id = b, before = a }))!.AsArray();
        Assert.Equal("B", top[0]!["title"]!.GetValue<string>());

        var longTitle = new string('x', 200);
        Assert.Equal(200, (await rig.Call("backlog.add", new { title = longTitle }))!["title"]!.GetValue<string>().Length);

        await rig.Call("backlog.archive", new { id = a });
        await Assert.ThrowsAsync<RpcException>(() => rig.Call("backlog.position", new { id = a, before = b }));
        Assert.True((await rig.Call("backlog.delete", new { id = a }))!["deleted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task MovingACardWakesTheManager_AndMarksItAwaiting()
    {
        await using var rig = await Rig.StartAsync();
        var card = rig.Store.AddCard("payments", "Manager", null, "Ship it", "", "queued", Lanes.Todo, null);
        var after = (await rig.Call("board.move", new { id = card.Id, lane = "done", note = "it is shipped" }))!;
        Assert.Equal("done", after["lane"]!.GetValue<string>());
        Assert.True(after["awaitingManager"]!.GetValue<bool>());
        Assert.Contains(after["trail"]!.AsArray(), t => t!["kind"]!.GetValue<string>() == "moved");
        var wake = (await rig.Bus.MessagesFromAsync(0, 50, Ct)).Single(m => m.Type == "agent.instruction.payments/Manager");
        Assert.Contains("moved card", wake.Payload);
        Assert.Contains($"\"about\":{card.Id}", wake.Payload);

        var nothing = await Assert.ThrowsAsync<RpcException>(() => rig.Call("board.edit", new { id = card.Id, title = "Ship it" }));
        Assert.Equal("bad_params", nothing.Code); // an edit that changes nothing would wake the Manager for nothing

        var list = (await rig.Call("board.list", new { }))!;
        Assert.Equal(4, list["lanes"]!.AsArray().Count);
        Assert.Single(list["cards"]!.AsArray());
    }
}
