using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// No way around the tester rule. Declaring the deliverable complete, a developer's move and someone else's hand-back
/// do not put an untested tester's card in Done; only the tester's own hand-back, or its run that handed back, after
/// the card's latest assignment counts as tested; and who is a tester.
/// Fakes only: the projector fed hand-made messages shaped like BusEngine's.
/// </summary>
public class BoardTesterRuleTests
{
    private long _seq = 100;

    private Message M(string type, object payload, string source, long deliverable, long? causation = null) =>
        new(++_seq, type, JsonSerializer.Serialize(payload), source, deliverable, causation, 0, DateTimeOffset.UtcNow);

    /// <summary>A message whose payload is the given JSON text, as BusEngine writes an agent.completed payload.</summary>
    private Message Raw(string type, string payloadJson, string source, long deliverable) =>
        new(++_seq, type, payloadJson, source, deliverable, null, 0, DateTimeOffset.UtcNow);

    private Message Claim(string member, long card, long deliverable) =>
        new(++_seq, "agent.instruction.payments/" + member, new JsonObject { ["text"] = "work card " + card, ["card"] = card }.ToJsonString(),
            "payments/Manager", deliverable, null, 0, DateTimeOffset.UtcNow);

    /// <summary>A tell that names no card: it goes to the member's own card.</summary>
    private Message Tell(string member, string text, string source, long deliverable, long? causation = null) =>
        new(++_seq, "agent.instruction.payments/" + member, new JsonObject { ["text"] = text }.ToJsonString(),
            source, deliverable, causation, 0, DateTimeOffset.UtcNow);

    private static (TempDb Db, BoardStore Store, BoardProjector P) Board(params string[] testers)
    {
        var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var set = testers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (db, store, new BoardProjector(store) { TestersOf = team => team == "payments" ? set : new HashSet<string>() });
    }

    // ---------------------------------------------------------------- deliverable_complete

    [Fact]
    public void DeclaringTheDeliverableComplete_LeavesOutTheUntestedTesterCard_WithAComment()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var verify = store.AddCard("payments", null, "B002", "Verify the fix", "spec", "queued", Lanes.Todo, 20);
        var fix = store.AddCard("payments", null, "B002", "Fix it", "spec", "queued", Lanes.Todo, 20);
        p.Apply(Claim("TesterWren", verify.Id, 20));
        p.Apply(Claim("DeveloperPriya", fix.Id, 20));
        p.Apply(M(MessageTypes.Handback, new { delivered = "fixed" }, "payments/DeveloperPriya", 20));

        p.Apply(M(MessageTypes.DeliverableCompleted, new { delivered = "all done" }, "payments/Manager", 20));

        Assert.Equal(Lanes.Done, store.Card(fix.Id)!.Lane);
        var left = store.Card(verify.Id)!;
        Assert.NotEqual(Lanes.Done, left.Lane);
        var comment = Assert.Single(store.Trail(verify.Id), t => t.Kind == "comment");
        Assert.Equal(BoardProjector.BoardSource, comment.Source);
        Assert.Contains("Left out of Done", comment.Text);
        Assert.Contains("TesterWren has not tested it", comment.Text);
        Assert.Contains($"tell (card: {verify.Id})", comment.Text);
        Assert.DoesNotContain(store.Trail(verify.Id), t => t.Kind == "deliverable done");
    }

    [Fact]
    public void ATestedTesterCard_AndATeamWithoutATester_StillGoToDone()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var verify = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 21);
        p.Apply(Claim("TesterWren", verify.Id, 21));
        p.Apply(M(MessageTypes.Progress, new { status = "testing" }, "payments/TesterWren", 21));
        p.Apply(M(MessageTypes.Handback, new { delivered = "12 passed" }, "payments/TesterWren", 21));
        p.Apply(M(MessageTypes.DeliverableCompleted, new { delivered = "done" }, "payments/Manager", 21));
        Assert.Equal(Lanes.Done, store.Card(verify.Id)!.Lane);
        Assert.DoesNotContain(store.Trail(verify.Id), t => t.Kind == "comment");

        var (db2, store2, p2) = Board(); // nobody is a tester
        using var __ = db2;
        var card = store2.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 22);
        p2.Apply(Claim("TesterWren", card.Id, 22));
        p2.Apply(M(MessageTypes.DeliverableCompleted, new { delivered = "done" }, "payments/Manager", 22));
        Assert.Equal(Lanes.Done, store2.Card(card.Id)!.Lane);
    }

    // ---------------------------------------------------------------- someone else's hand-back

    [Fact]
    public void ADevelopersHandBack_OnATesterCardReToldToIt_DoesNotPutItInDone()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify the login fix", "spec", "queued", Lanes.Todo, 30);
        p.Apply(Claim("TesterWren", card.Id, 30));
        p.Apply(M(MessageTypes.Handback, new { delivered = "FAILED: login 500s" }, "payments/TesterWren", 30));
        Assert.Equal(Lanes.Done, store.Card(card.Id)!.Lane); // the tester's own hand-back closes it

        p.Apply(Claim("DeveloperPriya", card.Id, 31)); // re-told to the developer to fix
        p.Apply(M(MessageTypes.Progress, new { status = "fixing" }, "payments/DeveloperPriya", 31));
        p.Apply(M(MessageTypes.Handback, new { delivered = "fixed" }, "payments/DeveloperPriya", 31));
        p.Apply(M(MessageTypes.Completed, new { reason = "ok" }, "payments/DeveloperPriya", 31));

        var after = store.Card(card.Id)!;
        Assert.Equal(Lanes.InProgress, after.Lane);
        Assert.Equal("completed", after.Status); // its run's end, as for any card; only the lane is held back
        var trail = store.Trail(card.Id);
        Assert.Contains(trail, t => t.Kind == "handed back" && t.Source == "DeveloperPriya");
        Assert.Contains(trail, t => t.Kind == "comment" && t.Source == BoardProjector.BoardSource && t.Text.Contains("TesterWren has not tested it"));

        p.Apply(Claim("TesterWren", card.Id, 32)); // back to the tester, who tests it
        p.Apply(M(MessageTypes.Handback, new { delivered = "12 passed" }, "payments/TesterWren", 32));
        Assert.Equal(Lanes.Done, store.Card(card.Id)!.Lane);
    }

    [Fact]
    public void ADevelopersOwnCard_StillGoesToDoneOnItsHandBack()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Fix", "spec", "queued", Lanes.Todo, 33);
        p.Apply(Claim("DeveloperPriya", card.Id, 33));
        p.Apply(M(MessageTypes.Handback, new { delivered = "fixed" }, "payments/DeveloperPriya", 33));
        Assert.Equal(Lanes.Done, store.Card(card.Id)!.Lane);
        Assert.DoesNotContain(store.Trail(card.Id), t => t.Kind == "comment");
    }

    // ---------------------------------------------------------------- what counts as having tested

    [Fact]
    public void ACrashedRun_DoesNotCount_ButAFinishedRunAfterTheAssignmentDoes()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 40);
        p.Apply(Claim("TesterWren", card.Id, 40));
        p.Apply(M(MessageTypes.Failed, new { reason = "crashed" }, "payments/TesterWren", 40));
        Assert.Equal("TesterWren", TesterRule.UntestedTester(store.Card(card.Id)!, store.Trail(card.Id), Set("TesterWren")));

        p.Apply(Claim("TesterWren", card.Id, 41)); // given again: its run finishes this time
        p.Apply(M(MessageTypes.Completed, new { reason = "ok", handedBack = true }, "payments/TesterWren", 41));
        Assert.Null(TesterRule.UntestedTester(store.Card(card.Id)!, store.Trail(card.Id), Set("TesterWren")));
        Assert.Equal(Lanes.Done, store.Card(card.Id)!.Lane);
    }

    /// <summary>A tester's run that ended without handing back (stopped, or cut off after its final answer:
    /// completed with exit -1) is not a test; deliverable_complete leaves the card out. One that handed back is.</summary>
    [Theory]
    [InlineData("""{"reason":"ok","handedBack":false}""")]
    [InlineData("""{"reason":"stopped","handedBack":false,"run":{"end":"stopped","exitCode":-1}}""")]
    [InlineData("""{"reason":"ok","run":{"exitCode":-1}}""")] // no handedBack at all: not a hand-back
    public void ATestersRunThatDidNotHandBack_DoesNotCount(string completed)
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 44);
        p.Apply(Claim("TesterWren", card.Id, 44));
        p.Apply(new Message(++_seq, MessageTypes.Completed, completed, "payments/TesterWren", 44, null, 0, DateTimeOffset.UtcNow));

        var ended = Assert.Single(store.Trail(card.Id), t => t.Kind == "run ended");
        Assert.StartsWith("run ended (no hand-back", ended.Text);
        Assert.Equal("TesterWren", TesterRule.UntestedTester(store.Card(card.Id)!, store.Trail(card.Id), Set("TesterWren")));
        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);

        p.Apply(M(MessageTypes.DeliverableCompleted, new { delivered = "done" }, "payments/Manager", 44));
        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);
        Assert.Contains(store.Trail(card.Id), t => t.Kind == "comment" && t.Text.StartsWith("Left out of Done") && t.Text.Contains("TesterWren has not tested it"));
    }

    [Fact]
    public void ATestersRunThatHandedBack_Counts_AndSaysSoOnTheTrail()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 45);
        p.Apply(Claim("TesterWren", card.Id, 45));
        p.Apply(M(MessageTypes.Completed, new { reason = "ok", handedBack = true, run = new { exitCode = -1 } }, "payments/TesterWren", 45));

        Assert.Contains(store.Trail(card.Id), t => t.Kind == "run ended" && t.Text == TesterRule.RanAndHandedBack);
        Assert.Null(TesterRule.UntestedTester(store.Card(card.Id)!, store.Trail(card.Id), Set("TesterWren")));
        Assert.Equal(Lanes.Done, store.Card(card.Id)!.Lane);
    }

    [Theory]
    [InlineData("progress", "x", false)]
    [InlineData("comment", "x", false)]
    [InlineData("asked", "x", false)]
    [InlineData("failed", "x", false)]
    [InlineData("moved", "x", false)]
    [InlineData("handed back", "x", true)]
    [InlineData("run ended", TesterRule.RanAndHandedBack, true)]
    [InlineData("run ended", "run ended (no hand-back)", false)]          // a run that did not hand back
    [InlineData("run ended", "run ended (no hand-back, exit -1)", false)] // nor one that was cut off
    [InlineData("run ended", "run ended", false)]                         // nor a plain entry that says nothing of a hand-back
    public void OnlyAHandBackOrAFinishedRunCounts(string kind, string text, bool counts)
    {
        var (db, store, _) = Board();
        using var __ = db;
        var card = store.AddCard("payments", "TesterWren", null, "Verify", "spec", "queued", Lanes.InProgress, null);
        store.AddTrail(card.Id, "Manager", "assigned", "Assigned to TesterWren: verify");
        store.AddTrail(card.Id, "TesterWren", kind, text);
        var untested = TesterRule.UntestedTester(card, store.Trail(card.Id), Set("TesterWren"));
        Assert.Equal(counts ? null : "TesterWren", untested);
    }

    [Fact]
    public void AHandBackBeforeTheLatestAssignment_DoesNotCount()
    {
        var (db, store, _) = Board();
        using var __ = db;
        var card = store.AddCard("payments", "DeveloperPriya", null, "Verify", "spec", "queued", Lanes.InProgress, null);
        store.AddTrail(card.Id, "Manager", "assigned", "Assigned to TesterWren: verify");
        store.AddTrail(card.Id, "TesterWren", "handed back", "failed: 500");
        store.AddTrail(card.Id, "Manager", "assigned", "Assigned to DeveloperPriya: fix it");
        Assert.Equal("TesterWren", TesterRule.UntestedTester(card, store.Trail(card.Id), Set("TesterWren")));

        // New work told to the tester on its own card resets it too; the person's answer and the app's notices do not.
        var own = store.AddCard("payments", "TesterWren", null, "Verify", "spec", "queued", Lanes.InProgress, null);
        store.AddTrail(own.Id, "Manager", "told", "verify");
        store.AddTrail(own.Id, "TesterWren", "handed back", "passed");
        store.AddTrail(own.Id, "you", "told", "thanks");
        store.AddTrail(own.Id, "schedule", "notice", "idle");
        Assert.Null(TesterRule.UntestedTester(own, store.Trail(own.Id), Set("TesterWren")));
        store.AddTrail(own.Id, "Manager", "told", "verify again on the new commit");
        Assert.Equal("TesterWren", TesterRule.UntestedTester(own, store.Trail(own.Id), Set("TesterWren")));
    }

    [Fact]
    public void ATesterCardReToldToADeveloper_ThenDeclaredComplete_StaysOutOfDone()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 40);
        p.Apply(Claim("TesterWren", card.Id, 40));
        p.Apply(M(MessageTypes.Handback, new { delivered = "FAILED" }, "payments/TesterWren", 40));
        p.Apply(Claim("DeveloperPriya", card.Id, 40));
        p.Apply(M(MessageTypes.Handback, new { delivered = "fixed" }, "payments/DeveloperPriya", 40));
        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);

        p.Apply(M(MessageTypes.DeliverableCompleted, new { delivered = "done" }, "payments/Manager", 40));

        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);
        Assert.Contains(store.Trail(card.Id), t => t.Kind == "comment" && t.Source == BoardProjector.BoardSource && t.Text.StartsWith("Left out of Done"));
        Assert.DoesNotContain(store.Trail(card.Id), t => t.Kind == "deliverable done");
    }

    [Fact]
    public void ThePersonsAnswerAndTheAppsNotice_AfterTheTestersHandBack_AreNotANewAssignment()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 41);
        p.Apply(Claim("TesterWren", card.Id, 41));
        var handback = M(MessageTypes.Handback, new { delivered = "12 passed" }, "payments/TesterWren", 41);
        p.Apply(handback);
        // The person's reply inside the deliverable (it has a causation) and the app's idle reminder go on the trail only.
        p.Apply(Tell("TesterWren", "thanks", MessageTypes.PersonSource, 41, causation: handback.Seq));
        p.Apply(Tell("Manager", "an open deliverable has nobody working it", MessageTypes.HostSource, 41));
        Assert.Contains(store.Trail(card.Id), t => t.Kind == "told" && t.Source == "you");

        p.Apply(M(MessageTypes.DeliverableCompleted, new { delivered = "done" }, "payments/Manager", 41));

        Assert.Equal(Lanes.Done, store.Card(card.Id)!.Lane);
        Assert.DoesNotContain(store.Trail(card.Id), t => t.Kind == "comment");
    }

    [Fact]
    public void NewWorkToldToTheTester_AfterItsHandBack_NeedsANewTest()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 42);
        p.Apply(Claim("TesterWren", card.Id, 42));
        p.Apply(M(MessageTypes.Handback, new { delivered = "12 passed" }, "payments/TesterWren", 42));
        p.Apply(Tell("TesterWren", "also check the logout page", "payments/Manager", 42)); // new work on its card

        p.Apply(M(MessageTypes.DeliverableCompleted, new { delivered = "done" }, "payments/Manager", 42));

        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);
        Assert.Contains(store.Trail(card.Id), t => t.Kind == "comment" && t.Text.StartsWith("Left out of Done"));
    }

    [Fact]
    public void TheTestersQuestionOrProgress_IsNotAFinishedTest()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 43);
        p.Apply(Claim("TesterWren", card.Id, 43));
        p.Apply(M(MessageTypes.Progress, new { status = "running the suite" }, "payments/TesterWren", 43));
        p.Apply(M(MessageTypes.NeedsDecision, new { question = "which base?" }, "payments/TesterWren", 43));

        Assert.Equal("TesterWren", TesterRule.UntestedTester(store.Card(card.Id)!, store.Trail(card.Id), new HashSet<string> { "TesterWren" }));
    }

    // ---------------------------------------------------------------- the "run ended" entry of an agent.completed message

    [Fact]
    public void TheManagersOwnRunEnding_OnACardItTookFromTheTester_IsNotATest()
    {
        // BusEngine sends handedBack: null for the Manager's runs.
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Verify", "spec", "queued", Lanes.Todo, 60);
        p.Apply(Claim("TesterWren", card.Id, 60));
        p.Apply(Claim("Manager", card.Id, 60));
        p.Apply(Raw(MessageTypes.Completed, """{"runId":7,"reason":"ok","handedBack":null,"run":{"exitCode":0}}""", "payments/Manager", 60));

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
        p.Apply(Raw(MessageTypes.Completed, """{"runId":8,"reason":"ok","handedBack":true}""", "payments/DeveloperPriya", 61));

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
        p.Apply(Raw(MessageTypes.Completed, """{"runId":9,"reason":"ok","handedBack":true}""", "payments/TesterWren", 62));
        Assert.Null(Untested(store, card.Id, "TesterWren"));

        p.Apply(Claim("DeveloperPriya", card.Id, 62)); // back to the developer: the earlier test is stale
        p.Apply(Raw(MessageTypes.Completed, """{"runId":10,"reason":"ok","handedBack":true}""", "payments/DeveloperPriya", 62));
        Assert.Equal("TesterWren", Untested(store, card.Id, "TesterWren"));
        Assert.NotEqual(Lanes.Done, store.Card(card.Id)!.Lane);

        p.Apply(Claim("TesterWren", card.Id, 62));
        p.Apply(Raw(MessageTypes.Completed, """{"runId":11,"reason":"ok","handedBack":true}""", "payments/TesterWren", 62));
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

    // ---------------------------------------------------------------- who is a tester

    [Theory]
    [InlineData("TesterWren", "", "member", true)]                         // the name starts with Tester
    [InlineData("testerkai", "", "member", true)]                          // any case
    [InlineData("Kai", "Tester: checks the work", "member", true)]         // job title
    [InlineData("Kai", "Testing lead", "member", true)]
    [InlineData("Kai", "Testers' guild", "member", true)]
    [InlineData("Kai", "Developer and tester", "member", true)]            // contains the word
    [InlineData("Kai", "QA engineer, mobile", "member", true)]
    [InlineData("Kai", "qa: release checks", "member", true)]
    [InlineData("Kai", "Test lead: owns the plan", "member", true)]
    [InlineData("TesterQa", "Developer", "member", true)]                  // the name alone is enough
    [InlineData("Kai", "tester, end to end", "member", true)]              // the job title in any case
    [InlineData("Kai", "  Tester: checks", "member", true)]                // leading spaces in the job
    [InlineData("Kai", "", "QA", true)]                                    // the role
    [InlineData("Kai", "Keeps the latest docs", "member", false)]          // "latest" is not "test"
    [InlineData("Kai", "Contest judge", "member", false)]                  // nor "contest"
    [InlineData("Kai", "Attestation and aqua checks", "member", false)]    // nor "attestation", "aqua"
    [InlineData("Kai", "Developer: writes the code and its tests, tested", "member", false)] // after the colon: not the title
    [InlineData("Kai", "Writes the code and its unit tests", "member", false)] // "tests" is not a tester word
    [InlineData("Kai", "Developer\nalso does testing", "member", false)]  // only the first line without a colon
    [InlineData("DeveloperTester", "", "member", false)]                   // the name must start with Tester
    [InlineData("TesterWren", "Tester", "manager", false)]                 // never the Manager
    public void WhoIsATester(string name, string job, string role, bool tester)
    {
        Assert.Equal(tester, TesterRule.IsTester(new MemberRow(1, 1, "payments", name, role, "claude", job, "", "")));
    }

    private static HashSet<string> Set(params string[] names) => names.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? Untested(BoardStore store, long card, params string[] testers) =>
        TesterRule.UntestedTester(store.Card(card)!, store.Trail(card), Set(testers));
}
