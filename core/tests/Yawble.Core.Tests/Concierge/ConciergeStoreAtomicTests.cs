using Microsoft.Data.Sqlite;
using Yawble.Core.Concierge;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Concierge;

/// <summary>
/// The Concierge store's writes that take several statements happen whole or not at all: deleting a conversation with
/// its messages and watches, and adding a message with its conversation's "last updated" time. A trigger stands in
/// for the failure.
/// </summary>
public class ConciergeStoreAtomicTests
{
    private static void Exec(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Count(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void DeletingAConversation_TakesItsMessagesAndWatches_AndLeavesOthersAlone()
    {
        using var db = new TempDb();
        var store = new ConciergeStore(db.ConnectionString);
        store.Create("c-1", "One", "claude", null);
        store.AddMessage("c-1", "user", "hello");
        store.AddWatch("c-1", "payments", 7, "the login page", true, 0, DateTimeOffset.UtcNow.AddDays(1));
        store.Create("c-2", "Two", "claude", null);
        store.AddMessage("c-2", "user", "hi");

        Assert.True(store.Delete("c-1"));
        Assert.False(store.Delete("c-1"));

        Assert.Null(store.Get("c-1"));
        Assert.Empty(store.Messages("c-1"));
        Assert.Equal(0, Count(db, "SELECT count(*) FROM concierge_watches;"));
        Assert.NotNull(store.Get("c-2"));
        Assert.Single(store.Messages("c-2"));
    }

    [Fact]
    public void AConversationDeleteThatFails_LeavesItsMessagesAndWatches()
    {
        using var db = new TempDb();
        var store = new ConciergeStore(db.ConnectionString);
        store.Create("c-1", "One", "claude", null);
        store.AddMessage("c-1", "user", "hello");
        store.AddWatch("c-1", "payments", 7, "the login page", true, 0, DateTimeOffset.UtcNow.AddDays(1));
        Exec(db, "CREATE TRIGGER conversations_stay BEFORE DELETE ON concierge_conversations BEGIN SELECT RAISE(ABORT, 'conversations stay'); END;");

        Assert.Throws<SqliteException>(() => store.Delete("c-1"));

        Assert.NotNull(store.Get("c-1"));
        Assert.Single(store.Messages("c-1"));
        Assert.Equal(1, Count(db, "SELECT count(*) FROM concierge_watches;"));
    }

    [Fact]
    public void AddingAMessage_StoresItAndTouchesItsConversation()
    {
        using var db = new TempDb();
        var store = new ConciergeStore(db.ConnectionString);
        store.Create("c-1", "One", "claude", null);
        Exec(db, "UPDATE concierge_conversations SET updated_at = 'long ago';");

        var id = store.AddMessage("c-1", "user", "hello");

        var message = Assert.Single(store.Messages("c-1"));
        Assert.Equal(id, message.Id);
        Assert.Equal("hello", message.Text);
        Assert.Equal(message.At, store.Get("c-1")!.UpdatedAt);
    }

    [Fact]
    public void AMessageThatCannotBeStored_LeavesItsConversationUntouched()
    {
        using var db = new TempDb();
        var store = new ConciergeStore(db.ConnectionString);
        store.Create("c-1", "One", "claude", null);
        Exec(db, "UPDATE concierge_conversations SET updated_at = 'long ago';");
        Exec(db, "CREATE TRIGGER no_messages BEFORE INSERT ON concierge_messages BEGIN SELECT RAISE(ABORT, 'no messages'); END;");

        Assert.Throws<SqliteException>(() => store.AddMessage("c-1", "user", "hello"));

        Assert.Equal("long ago", store.Get("c-1")!.UpdatedAt);
        Assert.Empty(store.Messages("c-1"));
    }
}
