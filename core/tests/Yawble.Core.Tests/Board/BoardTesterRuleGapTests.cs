using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// Gap tests beside BoardTesterRuleTests (its items 1-4). Fakes only: the projector fed hand-made messages.
/// <list type="bullet">
/// <item>Items 1 + 3 together: a tester's card re-told to a developer, the developer hands back, then the workflow is
///   declared complete: the card stays out of Done.</item>
/// <item>Item 4: the person's answer and the app's notice after the tester's hand-back are not a new assignment, so the
///   test still counts; new work told to the tester by the Manager is one, so it does not.</item>
/// <item>Item 4: the tester's own question or block is not a finished test.</item>
/// </list>
/// </summary>
public class BoardTesterRuleGapTests
{
    private long _seq = 500;

    private Message M(string type, object payload, string source, long workflow, long? causation = null) =>
        new(++_seq, type, JsonSerializer.Serialize(payload), source, workflow, causation, 0, DateTimeOffset.UtcNow);

    private Message Claim(string member, long card, long workflow) =>
        new(++_seq, "agent.instruction.payments/" + member, new JsonObject { ["text"] = "work card " + card, ["card"] = card }.ToJsonString(),
            "payments/Manager", workflow, null, 0, DateTimeOffset.UtcNow);

    private Message Tell(string member, string text, string source, long workflow, long? causation = null) =>
        new(++_seq, "agent.instruction.payments/" + member, new JsonObject { ["text"] = text }.ToJsonString(),
            source, workflow, causation, 0, DateTimeOffset.UtcNow);

    private static (TempDb Db, BoardStore Store, BoardProjector P) Board(params string[] testers)
    {
        var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var set = testers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (db, store, new BoardProjector(store) { TestersOf = team => team == "payments" ? set : new HashSet<string>() });
    }

    [Fact]
    public void Items1And3_ReToldToADeveloper_ThenDeclaredComplete_TheCardStaysOutOfDone()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 40);
        p.Apply(Claim("TesterWren", card.Id, 40));
        p.Apply(M(MessageTypes.Handback, new { delivered = "FAILED" }, "payments/TesterWren", 40));
        p.Apply(Claim("DeveloperPriya", card.Id, 40));
        p.Apply(M(MessageTypes.Handback, new { delivered = "fixed" }, "payments/DeveloperPriya", 40));
        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);

        p.Apply(M(MessageTypes.WorkflowCompleted, new { delivered = "done" }, "payments/Manager", 40));

        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);
        Assert.Contains(store.Trail(card.Id), t => t.Kind == "comment" && t.Source == BoardProjector.BoardSource && t.Text.StartsWith("Left out of Done"));
        Assert.DoesNotContain(store.Trail(card.Id), t => t.Kind == "workflow done");
    }

    [Fact]
    public void Item4_ThePersonsAnswerAndTheAppsNotice_AfterTheTestersHandBack_AreNotANewAssignment()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 41);
        p.Apply(Claim("TesterWren", card.Id, 41));
        var handback = M(MessageTypes.Handback, new { delivered = "12 passed" }, "payments/TesterWren", 41);
        p.Apply(handback);
        // The person's reply inside the workflow (it has a causation) and the app's idle reminder go on the trail only.
        p.Apply(Tell("TesterWren", "thanks", MessageTypes.PersonSource, 41, causation: handback.Seq));
        p.Apply(Tell("Manager", "an open workflow has nobody working it", MessageTypes.HostSource, 41));
        Assert.Contains(store.Trail(card.Id), t => t.Kind == "told" && t.Source == "you");

        p.Apply(M(MessageTypes.WorkflowCompleted, new { delivered = "done" }, "payments/Manager", 41));

        Assert.Equal(Lanes.Done, store.Card(card.Id)!.Lane);
        Assert.DoesNotContain(store.Trail(card.Id), t => t.Kind == "comment");
    }

    [Fact]
    public void Item4_NewWorkToldToTheTester_AfterItsHandBack_NeedsANewTest()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 42);
        p.Apply(Claim("TesterWren", card.Id, 42));
        p.Apply(M(MessageTypes.Handback, new { delivered = "12 passed" }, "payments/TesterWren", 42));
        p.Apply(Tell("TesterWren", "also check the logout page", "payments/Manager", 42)); // new work on its card

        p.Apply(M(MessageTypes.WorkflowCompleted, new { delivered = "done" }, "payments/Manager", 42));

        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);
        Assert.Contains(store.Trail(card.Id), t => t.Kind == "comment" && t.Text.StartsWith("Left out of Done"));
    }

    [Fact]
    public void Item4_TheTestersQuestionOrProgress_IsNotAFinishedTest()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 43);
        p.Apply(Claim("TesterWren", card.Id, 43));
        p.Apply(M(MessageTypes.Progress, new { status = "running the suite" }, "payments/TesterWren", 43));
        p.Apply(M(MessageTypes.NeedsDecision, new { question = "which base?" }, "payments/TesterWren", 43));

        Assert.Equal("TesterWren", TesterRule.UntestedTester(store.Card(card.Id)!, store.Trail(card.Id), new HashSet<string> { "TesterWren" }));
    }
}
