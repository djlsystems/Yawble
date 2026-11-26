using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// No way around the tester rule. Declaring the deliverable complete (item 1), a developer's move (item 2)
/// and someone else's hand-back (item 3) do not put an untested tester's card in Done; only the tester's own hand-back
/// or finished run after the card's latest assignment counts as tested (item 4); and who is a tester (item 5).
/// Fakes only: the projector fed hand-made messages, and the scripted runner's bus.
/// </summary>
public class BoardTesterRuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private long _seq = 100;

    private Message M(string type, object payload, string source, long deliverable) =>
        new(++_seq, type, JsonSerializer.Serialize(payload), source, deliverable, null, 0, DateTimeOffset.UtcNow);

    private Message Claim(string member, long card, long deliverable) =>
        new(++_seq, "agent.instruction.payments/" + member, new JsonObject { ["text"] = "work card " + card, ["card"] = card }.ToJsonString(),
            "payments/Manager", deliverable, null, 0, DateTimeOffset.UtcNow);

    private static (TempDb Db, BoardStore Store, BoardProjector P) Board(params string[] testers)
    {
        var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var set = testers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (db, store, new BoardProjector(store) { TestersOf = team => team == "payments" ? set : new HashSet<string>() });
    }

    // ---------------------------------------------------------------- item 1: deliverable_complete

    [Fact]
    public void Item1_DeclaringTheDeliverableComplete_LeavesOutTheUntestedTesterCard_WithAComment()
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
    public void Item1_ATestedTesterCard_AndATeamWithoutATester_StillGoToDone()
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

    // ---------------------------------------------------------------- item 3: someone else's hand-back

    [Fact]
    public void Item3_ADevelopersHandBack_OnATesterCardReToldToIt_DoesNotPutItInDone()
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
    public void Item3_ADevelopersOwnCard_StillGoesToDoneOnItsHandBack()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var card = store.AddCard("payments", null, null, "Fix", "spec", "queued", Lanes.Todo, 33);
        p.Apply(Claim("DeveloperPriya", card.Id, 33));
        p.Apply(M(MessageTypes.Handback, new { delivered = "fixed" }, "payments/DeveloperPriya", 33));
        Assert.Equal(Lanes.Done, store.Card(card.Id)!.Lane);
        Assert.DoesNotContain(store.Trail(card.Id), t => t.Kind == "comment");
    }

    // ---------------------------------------------------------------- item 4: what counts as having tested

    [Fact]
    public void Item4_ACrashedRun_DoesNotCount_ButAFinishedRunAfterTheAssignmentDoes()
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
    public void Item4_ATestersRunThatDidNotHandBack_DoesNotCount(string completed)
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
    public void Item4_ATestersRunThatHandedBack_Counts_AndSaysSoOnTheTrail()
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
    [InlineData("run ended", "run ended", false)]                         // nor the plain entry older builds wrote
    public void Item4_OnlyAHandBackOrAFinishedRunCounts(string kind, string text, bool counts)
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
    public void Item4_AHandBackBeforeTheLatestAssignment_DoesNotCount()
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

    // ---------------------------------------------------------------- item 5: who is a tester

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
    [InlineData("Kai", "", "QA", true)]                                    // the role
    [InlineData("Kai", "Keeps the latest docs", "member", false)]          // "latest" is not "test"
    [InlineData("Kai", "Contest judge", "member", false)]                  // nor "contest"
    [InlineData("Kai", "Attestation and aqua checks", "member", false)]    // nor "attestation", "aqua"
    [InlineData("Kai", "Developer: writes the code and its tests, tested", "member", false)] // after the colon: not the title
    [InlineData("Kai", "Writes the code and its unit tests", "member", false)] // "tests" is not a tester word
    [InlineData("Kai", "Developer\nalso does testing", "member", false)]  // only the first line without a colon
    [InlineData("DeveloperTester", "", "member", false)]                   // the name must start with Tester
    [InlineData("TesterWren", "Tester", "manager", false)]                 // never the Manager
    public void Item5_WhoIsATester(string name, string job, string role, bool tester)
    {
        Assert.Equal(tester, TesterRule.IsTester(new MemberRow(1, 1, "payments", name, role, "claude", job, "", "")));
    }

    private static HashSet<string> Set(params string[] names) => names.ToHashSet(StringComparer.OrdinalIgnoreCase);
}
