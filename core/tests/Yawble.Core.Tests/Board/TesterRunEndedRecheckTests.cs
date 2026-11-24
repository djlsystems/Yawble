using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// A re-check of BoardTesterRuleTests' item 4 ("not a crashed run") after its fix: a tester's "run ended" counts only when
/// that run handed back. Gap tests beside BoardTesterRuleTests / BoardTesterDoneGapTests. Fakes only: the projector fed
/// hand-made messages shaped like BusEngine's agent.completed payload.
/// </summary>
public class TesterRunEndedRecheckTests
{
    private long _seq = 900;

    private Message M(string type, string payloadJson, string source, long workflow) =>
        new(++_seq, type, payloadJson, source, workflow, null, 0, DateTimeOffset.UtcNow);

    private Message Claim(string member, long card, long workflow) =>
        new(++_seq, "agent.instruction.payments/" + member, new JsonObject { ["text"] = "work card " + card, ["card"] = card }.ToJsonString(),
            "payments/Manager", workflow, null, 0, DateTimeOffset.UtcNow);

    private static (TempDb Db, BoardStore Store, BoardProjector P) Board(params string[] testers)
    {
        var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var set = testers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (db, store, new BoardProjector(store) { TestersOf = team => team == "payments" ? set : new HashSet<string>() });
    }

    private static string? Untested(BoardStore store, long card, params string[] testers) =>
        TesterRule.UntestedTester(store.Card(card)!, store.Trail(card), testers.ToHashSet(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void TheManagersOwnRunEnding_OnACardItTookFromTheTester_IsNotATest()
    {
        // BusEngine sends handedBack: null for the Manager's runs.
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 60);
        p.Apply(Claim("TesterWren", card.Id, 60));
        p.Apply(Claim("Manager", card.Id, 60));
        p.Apply(M(MessageTypes.Completed, """{"runId":7,"reason":"ok","handedBack":null,"run":{"exitCode":0}}""", "payments/Manager", 60));

        Assert.Contains(store.Trail(card.Id), t => t.Kind == "run ended" && t.Source == "Manager" && t.Text == "run ended (no hand-back)");
        Assert.Equal("TesterWren", Untested(store, card.Id, "TesterWren"));
        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);
    }

    [Fact]
    public void ANonTestersHandedBackRun_OnTheTestersCard_DoesNotCountForTheTester()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 61);
        p.Apply(Claim("TesterWren", card.Id, 61));
        p.Apply(Claim("DeveloperPriya", card.Id, 61));
        p.Apply(M(MessageTypes.Completed, """{"runId":8,"reason":"ok","handedBack":true}""", "payments/DeveloperPriya", 61));

        Assert.Contains(store.Trail(card.Id), t => t.Kind == "run ended" && t.Source == "DeveloperPriya" && t.Text == TesterRule.RanAndHandedBack);
        Assert.Equal("TesterWren", Untested(store, card.Id, "TesterWren"));
        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);
        Assert.Contains(store.Trail(card.Id), t => t.Kind == "comment" && t.Source == BoardProjector.BoardSource && t.Text.Contains("TesterWren has not tested it"));
    }

    [Fact]
    public void TheTestersHandedBackRun_BeforeTheLatestAssignment_DoesNotCount_ButOneAfterItDoes()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 62);
        p.Apply(Claim("TesterWren", card.Id, 62));
        p.Apply(M(MessageTypes.Completed, """{"runId":9,"reason":"ok","handedBack":true}""", "payments/TesterWren", 62));
        Assert.Null(Untested(store, card.Id, "TesterWren"));

        p.Apply(Claim("DeveloperPriya", card.Id, 62)); // back to the developer: the earlier test is stale
        p.Apply(M(MessageTypes.Completed, """{"runId":10,"reason":"ok","handedBack":true}""", "payments/DeveloperPriya", 62));
        Assert.Equal("TesterWren", Untested(store, card.Id, "TesterWren"));
        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);

        p.Apply(Claim("TesterWren", card.Id, 62));
        p.Apply(M(MessageTypes.Completed, """{"runId":11,"reason":"ok","handedBack":true}""", "payments/TesterWren", 62));
        Assert.Null(Untested(store, card.Id, "TesterWren"));
        Assert.Equal(Lanes.Done, store.Card(card.Id)!.Lane);
    }

    [Fact]
    public void AnotherTestersHandedBackRun_DoesNotCountForTheTesterTheCardWasGivenTo()
    {
        var (db, store, p) = Board("TesterWren", "TesterOla");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 63);
        p.Apply(Claim("TesterWren", card.Id, 63));
        store.AddTrail(card.Id, "TesterOla", "run ended", TesterRule.RanAndHandedBack);

        Assert.Equal("TesterWren", Untested(store, card.Id, "TesterWren", "TesterOla"));
    }

    [Fact]
    public void TheTestersRunCutOffWithoutAHandBack_ViaTheProjector_LeavesTheCardOutOfDone()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 64);
        p.Apply(Claim("TesterWren", card.Id, 64));
        p.Apply(M(MessageTypes.Completed, """{"runId":12,"reason":"ok","handedBack":false,"run":{"exitCode":-1}}""", "payments/TesterWren", 64));

        Assert.Contains(store.Trail(card.Id), t => t.Kind == "run ended" && t.Text == "run ended (no hand-back, exit -1)");
        p.Apply(M(MessageTypes.WorkflowCompleted, """{"delivered":"done"}""", "payments/Manager", 64));
        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);
        Assert.Contains(store.Trail(card.Id), t => t.Kind == "comment" && t.Text.StartsWith("Left out of Done"));
    }

    [Theory]
    [InlineData(null, "run ended (no hand-back)")]
    [InlineData("""{}""", "run ended (no hand-back)")]
    [InlineData("""{"handedBack":"true"}""", "run ended (no hand-back)")]  // a string is not a hand-back
    [InlineData("""{"handedBack":1}""", "run ended (no hand-back)")]       // nor a number
    [InlineData("""{"handedBack":false,"run":{"exitCode":0}}""", "run ended (no hand-back)")]
    [InlineData("""{"handedBack":false,"run":{"exitCode":3}}""", "run ended (no hand-back, exit 3)")]
    [InlineData("""{"handedBack":false,"run":null}""", "run ended (no hand-back)")]
    [InlineData("""{"handedBack":true,"run":{"exitCode":-1}}""", TesterRule.RanAndHandedBack)]
    public void RunEndedText_OnlyABooleanTrueIsAHandBack(string? payload, string expected)
    {
        var obj = payload is null ? null : JsonNode.Parse(payload)!.AsObject();
        Assert.Equal(expected, TesterRule.RunEndedText(obj));
    }

    [Fact]
    public void TheTestersNameInADifferentCase_StillCounts()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 65);
        p.Apply(Claim("TesterWren", card.Id, 65));
        store.AddTrail(card.Id, "testerwren", "run ended", TesterRule.RanAndHandedBack);
        Assert.Null(Untested(store, card.Id, "TesterWren"));
    }
}
