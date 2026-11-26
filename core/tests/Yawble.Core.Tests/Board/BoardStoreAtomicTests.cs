using Microsoft.Data.Sqlite;
using Yawble.Core.Board;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// The board store's deletes that take several statements happen whole or not at all: a team's cards with their trails
/// and timelines, and a backlog item with the record of its dispatches. A trigger stands in for the failure.
/// </summary>
public class BoardStoreAtomicTests
{
    private static void Exec(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void DeletingATeam_TakesItsCardsTrailsAndTimelines_AndLeavesOtherTeamsAlone()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var mine = store.AddCard("payments", "Ada", null, "Build it", "", "queued", Lanes.Todo, 42).Id;
        store.AddTrail(mine, "Manager", "planned", "Build it");
        var other = store.AddCard("docs", "Bo", null, "Write it", "", "queued", Lanes.Todo, 43).Id;
        store.AddTrail(other, "Manager", "planned", "Write it");

        store.DeleteTeam("Payments");

        Assert.Null(store.Card(mine));
        Assert.Empty(store.Trail(mine));
        Assert.NotNull(store.Card(other));
        Assert.Single(store.Trail(other));
    }

    [Fact]
    public void ATeamDeleteThatFails_LeavesItsCardsAndTheirTrails()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var id = store.AddCard("payments", "Ada", null, "Build it", "", "queued", Lanes.Todo, 42).Id;
        store.AddTrail(id, "Manager", "planned", "Build it");
        Exec(db, "CREATE TRIGGER cards_stay BEFORE DELETE ON board_cards BEGIN SELECT RAISE(ABORT, 'cards stay'); END;");

        Assert.Throws<SqliteException>(() => store.DeleteTeam("payments"));

        Assert.NotNull(store.Card(id));
        Assert.Single(store.Trail(id));
    }

    [Fact]
    public void DeletingAnArchivedItem_TakesItsDispatches_AndALiveItemStaysWithThem()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var live = store.AddItem("Live", "", "payments");
        store.AddDispatch(live.Id, "payments", 7, "pat");
        var archived = store.AddItem("Old", "", "payments");
        store.AddDispatch(archived.Id, "payments", 8, "pat");
        store.SaveItem(archived with { ArchivedAt = store.Now() });

        Assert.False(store.DeleteItem(live.Id));
        Assert.True(store.DeleteItem(archived.Id));

        Assert.NotNull(store.Item(live.Id));
        Assert.Equal(1, store.DispatchCount(live.Id));
        Assert.Null(store.Item(archived.Id));
        Assert.Equal(0, store.DispatchCount(archived.Id));
    }

    [Fact]
    public void AnItemDeleteThatFails_LeavesTheItemAndItsDispatches()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var item = store.AddItem("Old", "", "payments");
        store.AddDispatch(item.Id, "payments", 8, "pat");
        store.SaveItem(item with { ArchivedAt = store.Now() });
        Exec(db, "CREATE TRIGGER items_stay BEFORE DELETE ON backlog_items BEGIN SELECT RAISE(ABORT, 'items stay'); END;");

        Assert.Throws<SqliteException>(() => store.DeleteItem(item.Id));

        Assert.NotNull(store.Item(item.Id));
        Assert.Equal(1, store.DispatchCount(item.Id));
    }

    /// <summary>Inside the projector's batch the delete is part of the page: it goes when the page does.</summary>
    [Fact]
    public void ATeamDeleteInsideABatch_IsPartOfTheBatch()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var id = store.AddCard("payments", "Ada", null, "Build it", "", "queued", Lanes.Todo, 42).Id;
        store.AddTrail(id, "Manager", "planned", "Build it");

        Assert.Throws<InvalidOperationException>(() => store.InBatch(() =>
        {
            store.DeleteTeam("payments");
            Assert.Null(store.Card(id));
            throw new InvalidOperationException("the page fails");
        }));

        Assert.NotNull(store.Card(id));
        Assert.Single(store.Trail(id));
    }
}
