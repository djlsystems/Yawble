using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Storage;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// "Clear done" (0037): the person hides Done cards from the board view; nothing is deleted, and everything but the
/// board view (workflows, backlog counts, the agents' board tool, the Concierge) still sees them.
/// </summary>
public class BoardClearDoneTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusEngine Bus { get; private set; } = null!;
        public BoardStore Store { get; private set; } = null!;
        public BoardService Service { get; private set; } = null!;
        public int Changes;

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            rig.Bus = new BusEngine(rig.Db.OpenStore(), new UnavailableRunner());
            await rig.Bus.StartAsync(Ct);
            await rig.Bus.CreateTeamAsync("payments", "Manager", "claude", null, Ct);
            await rig.Bus.CreateTeamAsync("billing", "Manager", "claude", null, Ct);
            rig.Store = new BoardStore(rig.Db.ConnectionString);
            rig.Service = new BoardService(rig.Store, rig.Bus) { Changed = () => Interlocked.Increment(ref rig.Changes) };
            return rig;
        }

        public async Task<JsonObject> Call(string method, object p) =>
            (await Service.DispatchAsync(method, JsonSerializer.SerializeToNode(p)!.AsObject(), Ct))!.AsObject();

        public async Task<List<long>> Ids(object p) =>
            (await Call("board.list", p))["cards"]!.AsArray().Select(c => c!["id"]!.GetValue<long>()).Order().ToList();

        public async ValueTask DisposeAsync()
        {
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static CardRow Card(BoardStore s, string team, string lane, long? workflow = null) =>
        s.AddCard(team, null, null, $"{team} {lane}", "", lane == Lanes.Done ? "done" : "queued", lane, workflow);

    [Fact]
    public async Task ClearDone_HidesExactlyTheGivenDoneCards_AndListClearedReturnsThem()
    {
        await using var rig = await Rig.StartAsync();
        var payDone = Card(rig.Store, "payments", Lanes.Done);
        var payDone2 = Card(rig.Store, "payments", Lanes.Done); // arrived since the view loaded, or filtered out: not sent
        var payTodo = Card(rig.Store, "payments", Lanes.Todo);
        var billDone = Card(rig.Store, "billing", Lanes.Done);

        // A card not in Done, and one that does not exist, are ignored.
        var result = await rig.Call("board.clearDone", new { ids = new[] { payDone.Id, payTodo.Id, 9999L } });
        Assert.Equal(1, result["cleared"]!.GetValue<int>());
        Assert.Equal(1, rig.Changes); // board.changed

        Assert.Equal([payDone2.Id, payTodo.Id, billDone.Id], await rig.Ids(new { }));
        Assert.Equal([payDone.Id], await rig.Ids(new { cleared = true }));
        Assert.Equal([payDone.Id], await rig.Ids(new { team = "payments", cleared = true }));
        Assert.Empty(await rig.Ids(new { team = "billing", cleared = true }));
        Assert.NotNull((await rig.Call("board.list", new { cleared = true }))["cards"]![0]!["clearedAt"]);
        Assert.Equal([payDone.Id, payDone2.Id, payTodo.Id, billDone.Id], await rig.Ids(new { includeCleared = true }));

        // Already cleared, or nothing given: zero, and no change raised.
        Assert.Equal(0, (await rig.Call("board.clearDone", new { ids = new[] { payDone.Id } }))["cleared"]!.GetValue<int>());
        Assert.Equal(0, (await rig.Call("board.clearDone", new { ids = Array.Empty<long>() }))["cleared"]!.GetValue<int>());
        Assert.Equal(1, rig.Changes);
        Assert.Equal(2, (await rig.Call("board.clearDone", new { ids = new[] { payDone2.Id, billDone.Id, billDone.Id } }))["cleared"]!.GetValue<int>());
        Assert.Equal([payTodo.Id], await rig.Ids(new { }));
        Assert.Equal(4, rig.Store.Cards().Count); // nothing deleted
    }

    [Fact]
    public async Task ClearDone_NeedsTheIds()
    {
        await using var rig = await Rig.StartAsync();
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => rig.Call("board.clearDone", new { team = "payments" }))).Code);
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => rig.Call("board.clearDone", new { ids = new[] { "x" } }))).Code);
    }

    [Fact]
    public async Task ClearedCount_FollowsTheTeamFilter()
    {
        await using var rig = await Rig.StartAsync();
        var ids = new[] { Card(rig.Store, "payments", Lanes.Done).Id, Card(rig.Store, "payments", Lanes.Done).Id, Card(rig.Store, "billing", Lanes.Done).Id };
        Assert.Equal(0, (await rig.Call("board.list", new { }))["clearedCount"]!.GetValue<int>());
        await rig.Call("board.clearDone", new { ids });

        Assert.Equal(3, (await rig.Call("board.list", new { }))["clearedCount"]!.GetValue<int>());
        Assert.Equal(2, (await rig.Call("board.list", new { team = "payments" }))["clearedCount"]!.GetValue<int>());
        Assert.Equal(1, (await rig.Call("board.list", new { team = "Billing" }))["clearedCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Restore_BringsOneBack()
    {
        await using var rig = await Rig.StartAsync();
        var a = Card(rig.Store, "payments", Lanes.Done);
        var b = Card(rig.Store, "payments", Lanes.Done);
        await rig.Call("board.clearDone", new { ids = new[] { a.Id, b.Id } });
        var before = rig.Changes;

        Assert.True((await rig.Call("board.restore", new { id = a.Id }))["restored"]!.GetValue<bool>());
        Assert.Equal(before + 1, rig.Changes);
        Assert.Equal([a.Id], await rig.Ids(new { }));
        Assert.Equal([b.Id], await rig.Ids(new { cleared = true }));
        Assert.Equal(Lanes.Done, rig.Store.Card(a.Id)!.Lane);
        Assert.False((await rig.Call("board.restore", new { id = a.Id }))["restored"]!.GetValue<bool>()); // not cleared
        Assert.Equal(before + 1, rig.Changes);
        Assert.Equal("no_such_card", (await Assert.ThrowsAsync<RpcException>(() => rig.Call("board.restore", new { id = 9999 }))).Code);
    }

    [Fact]
    public async Task MovingAClearedCardOutOfDone_UnclearsIt_AndSaysTheBoardChanged()
    {
        await using var rig = await Rig.StartAsync();
        var card = Card(rig.Store, "payments", Lanes.Done);
        var other = Card(rig.Store, "payments", Lanes.Done);
        var plain = Card(rig.Store, "payments", Lanes.Done);
        await rig.Call("board.clearDone", new { ids = new[] { card.Id, other.Id } });
        var before = rig.Changes;

        await rig.Call("board.move", new { id = card.Id, lane = "todo" });
        Assert.Null(rig.Store.Card(card.Id)!.ClearedAt);
        Assert.Equal([card.Id, plain.Id], await rig.Ids(new { }));
        Assert.Equal(before + 1, rig.Changes);

        // Any save in Done keeps it cleared (no change raised); the store's save (every caller) unclears on leaving Done.
        rig.Store.Save(rig.Store.Card(other.Id)! with { Title = "renamed" });
        Assert.NotNull(rig.Store.Card(other.Id)!.ClearedAt);
        Assert.Equal(before + 1, rig.Changes);
        rig.Store.Save(rig.Store.Card(other.Id)! with { Lane = Lanes.InProgress });
        Assert.Null(rig.Store.Card(other.Id)!.ClearedAt);
        Assert.Equal(before + 2, rig.Changes);
        // A card that was never cleared leaving Done raises nothing extra.
        rig.Store.Save(rig.Store.Card(plain.Id)! with { Lane = Lanes.Todo });
        Assert.Equal(before + 2, rig.Changes);
    }

    [Fact]
    public async Task CardsOfWorkflow_TheBacklog_AndTheConciergeView_StillIncludeClearedCards()
    {
        await using var rig = await Rig.StartAsync();
        var card = Card(rig.Store, "payments", Lanes.Done, workflow: 42);
        await rig.Call("board.clearDone", new { ids = new[] { card.Id } });

        Assert.Equal(card.Id, Assert.Single(rig.Store.CardsOfWorkflow(42)).Id);
        Assert.Equal(card.Id, Assert.Single(rig.Store.Cards("payments")).Id);
        Assert.Equal([card.Id], await rig.Ids(new { includeCleared = true }));
    }

    [Fact]
    public async Task TheAgentsBoardTool_StillSeesClearedCards_AndItsMoveUnclears()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        BoardService? service = null;
        string? shown = null, moved = null;
        long cardId = 0;
        var runner = new ScriptedRunner().On("payments/Ada", async (run, ct) =>
        {
            shown = await service!.BoardToolAsync(run, new JsonObject { ["action"] = "show" }, ct);
            moved = await service.BoardToolAsync(run, new JsonObject { ["action"] = "move", ["card"] = cardId, ["lane"] = "in-progress" }, ct);
            return RunResult.Ok;
        });
        await using var bus = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        service = new BoardService(store, bus);
        await bus.CreateTeamAsync("payments", "Manager", "claude", null, Ct);
        await bus.AddMemberAsync("payments", "Ada", ct: Ct);
        cardId = store.AddCard("payments", "Ada", null, "Shipped thing", "", "done", Lanes.Done, null).Id;
        Assert.Equal(1, store.ClearDone([cardId]));
        await bus.StartAsync(Ct);
        await bus.PersonTellAsync("payments", "Ada", "Look at the board", ct: Ct);
        await Wait.UntilAsync(() => moved is not null, "Ada used the board tool");

        Assert.Contains($"card {cardId}: Shipped thing", shown);
        Assert.Null(store.Card(cardId)!.ClearedAt);
        Assert.Equal(Lanes.InProgress, store.Card(cardId)!.Lane);
    }

    [Fact]
    public void TheMigration_AppliesOnAnExistingDatabase()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var card = store.AddCard("payments", null, null, "Old card", "", "done", Lanes.Done, null);
        // Wind the database back to before 0037, as an existing install is.
        using (var c = new SqliteConnection(db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE board_cards DROP COLUMN cleared_at; DELETE FROM schema_migrations WHERE name = '0037_board_cards_cleared';";
            cmd.ExecuteNonQuery();
        }

        Assert.Contains("0037_board_cards_cleared", SchemaMigrator.Migrate(db.ConnectionString));
        var after = store.Card(card.Id)!;
        Assert.Null(after.ClearedAt);
        Assert.Equal("Old card", after.Title);
        Assert.Equal(1, store.ClearDone([card.Id]));
        Assert.NotNull(store.Card(card.Id)!.ClearedAt);
    }
}
