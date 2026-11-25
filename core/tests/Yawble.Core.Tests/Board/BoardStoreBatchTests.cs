using Microsoft.Data.Sqlite;
using Yawble.Core.Board;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// The board store's batch: the projector writes a page of bus messages in one transaction (one commit, one disk flush),
/// with the cursor in the same commit. Other threads keep their own connections and never see a page half written.
/// </summary>
public class BoardStoreBatchTests
{
    [Fact]
    public void APage_IsOneCommit_WithItsCursor_AndOtherThreadsSeeItOnlyAfterTheCommit()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        long id = 0;
        CardRow? seenFromAnotherThread = null;
        long cursorFromAnotherThread = 0;
        store.InBatch(() =>
        {
            id = store.AddCard("payments", "Ada", null, "Build it", "", "queued", Lanes.Todo, 42).Id;
            store.AddTrail(id, "Manager", "planned", "Build it");
            store.SetCursor(7);
            // Inside the batch the writer reads its own writes.
            Assert.Equal("Build it", store.Card(id)!.Title);
            Assert.Equal(7, store.Cursor());
            // Another thread on the same store does not join the batch: it has its own connection and sees the last commit.
            // A thread of its own (a waited-on Task can run inline on this very thread and so join the batch).
            var other = new Thread(() => (seenFromAnotherThread, cursorFromAnotherThread) = (store.Card(id), store.Cursor()));
            other.Start();
            other.Join();
        });
        Assert.Null(seenFromAnotherThread);
        Assert.Equal(-1, cursorFromAnotherThread);
        Assert.Equal("Build it", store.Card(id)!.Title);
        Assert.Single(store.Trail(id));
        Assert.Equal(7, store.Cursor());
    }

    [Fact]
    public void APageThatThrows_LeavesNothingBehind_AndTheCursorWhereItWas()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        store.SetCursor(3);
        var before = store.Cards("payments").Count;
        Assert.Throws<InvalidOperationException>(() => store.InBatch(() =>
        {
            store.AddCard("payments", "Ada", null, "Half a page", "", "queued", Lanes.Todo, 42);
            store.SetCursor(9);
            throw new InvalidOperationException("a message the projector could not apply");
        }));
        Assert.Equal(before, store.Cards("payments").Count);
        Assert.Equal(3, store.Cursor());
        // The store works normally afterwards, outside a batch.
        Assert.Equal("After", store.AddCard("payments", "Ada", null, "After", "", "queued", Lanes.Todo, 43).Title);
    }

    [Fact]
    public void AnOldCardsTimelineRebuild_RunsInsideTheBatch_AndCommitsWithIt()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var card = store.AddCard("web", "TesterKofi", null, "Timeline", "", "queued", Lanes.Todo, 3573);
        store.AddTrail(card.Id, "Manager", "planned", "Timeline");
        MakeOld(db, card.Id);

        // Save rebuilds the old card's timeline first (its own all-or-nothing step), here inside the page's transaction.
        store.InBatch(() => store.Save(store.Card(card.Id)! with { Status = "running", Lane = Lanes.InProgress }));

        Assert.Equal(Lanes.InProgress, store.Card(card.Id)!.Lane);
        Assert.NotEmpty(store.Timeline(card.Id));
        Assert.Equal(0L, Flag(db, card.Id));
    }

    [Fact]
    public void ABatchInsideABatch_JoinsIt()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        Assert.Throws<InvalidOperationException>(() => store.InBatch(() =>
        {
            store.InBatch(() => store.SetCursor(5));
            throw new InvalidOperationException("the outer page fails");
        }));
        Assert.Equal(-1, store.Cursor());
    }

    /// <summary>Makes a card look as if it was made before its timeline existed: no lines, to be rebuilt from its trail.</summary>
    private static void MakeOld(TempDb db, long card)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM board_timeline WHERE card_id = $c; UPDATE board_cards SET timeline_rebuild = 1, timeline_rebuilt = 0, started_at = NULL WHERE id = $c;";
        cmd.Parameters.AddWithValue("$c", card);
        cmd.ExecuteNonQuery();
    }

    private static long Flag(TempDb db, long card)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT timeline_rebuild FROM board_cards WHERE id = $c;";
        cmd.Parameters.AddWithValue("$c", card);
        return (long)cmd.ExecuteScalar()!;
    }
}
