using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Gateway;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// B142 (card 621): a workflow the person let close over a tester's FAIL (WorkflowCompleted payload closedOverFail).
/// The board puts each listed card in Done with the board's line, the tester rule notwithstanding; the digest and
/// workflow_show say the same line; the Concierge's workflow watch says "closed over a FAIL (accepted)". Without the field
/// nothing changes. Fakes only: hand-made messages.
/// </summary>
public class ClosedOverFailTests
{
    private long _seq = 100;

    private Message M(string type, object payload, string source, long workflow) =>
        new(++_seq, type, JsonSerializer.Serialize(payload), source, workflow, null, 0, DateTimeOffset.UtcNow);

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

    private static object Completed(params object[] closedOverFail) => new { delivered = "shipped", closedOverFail };

    private static object Entry(long card, string kind, string failLine, string? reason) =>
        new { card, tester = "payments/TesterWren", failLine, kind, reason };

    [Fact]
    public void Accepted_TheUntestedTesterCard_GoesToDone_WithTheBoardsLine()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var verify = store.AddCard("payments", null, "B142", "Verify the fix", "spec", "queued", Lanes.Todo, 40);
        p.Apply(Claim("TesterWren", verify.Id, 40));
        p.Apply(M(MessageTypes.Blocked, new { reason = "FAIL: login still 500s" }, "payments/TesterWren", 40));
        // The tester's card was re-told (so untested since) and its status is loud: both would leave it out of Done.
        p.Apply(Claim("TesterWren", verify.Id, 40));

        p.Apply(M(MessageTypes.WorkflowCompleted, Completed(Entry(verify.Id, "accepted", "FAIL: login still 500s", null)), "payments/Manager", 40));

        var card = store.Card(verify.Id)!;
        Assert.Equal(Lanes.Done, card.Lane);
        Assert.Equal("done", card.Status);
        var line = Assert.Single(store.Trail(verify.Id), t => t.Kind == "closed over a FAIL");
        Assert.Equal(BoardProjector.BoardSource, line.Source);
        Assert.Equal("Closed over a FAIL, accepted by the person: FAIL: login still 500s", line.Text);
        Assert.DoesNotContain(store.Trail(verify.Id), t => t.Text.StartsWith("Left out of Done", StringComparison.Ordinal));
    }

    [Fact]
    public void WontDo_TheCardGoesToDone_WithTheReason()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var verify = store.AddCard("payments", null, "B142", "Verify export", "spec", "queued", Lanes.Todo, 41);
        p.Apply(Claim("TesterWren", verify.Id, 41));

        p.Apply(M(MessageTypes.WorkflowCompleted,
            Completed(Entry(verify.Id, "wont-do", "FAIL: CSV export missing", "CSV export is dropped from this release")), "payments/Manager", 41));

        Assert.Equal(Lanes.Done, store.Card(verify.Id)!.Lane);
        var line = Assert.Single(store.Trail(verify.Id), t => t.Kind == "won't do");
        Assert.Equal(BoardProjector.BoardSource, line.Source);
        Assert.Equal("Won't do, accepted by the person: CSV export is dropped from this release", line.Text);
    }

    [Fact]
    public void WithoutTheField_TheTesterRuleStillLeavesTheCardOut()
    {
        var (db, store, p) = Board("TesterWren");
        using var _ = db;
        var verify = store.AddCard("payments", null, "B142", "Verify", "spec", "queued", Lanes.Todo, 42);
        var fix = store.AddCard("payments", null, "B142", "Fix", "spec", "queued", Lanes.Todo, 42);
        p.Apply(Claim("TesterWren", verify.Id, 42));
        p.Apply(Claim("DeveloperPriya", fix.Id, 42));
        p.Apply(M(MessageTypes.Handback, new { delivered = "fixed" }, "payments/DeveloperPriya", 42));

        p.Apply(M(MessageTypes.WorkflowCompleted, new { delivered = "all done", closedOverFail = (object?)null }, "payments/Manager", 42));

        Assert.Equal(Lanes.Done, store.Card(fix.Id)!.Lane);
        Assert.Single(store.Trail(fix.Id), t => t.Kind == "workflow done");
        Assert.NotEqual(Lanes.Done, store.Card(verify.Id)!.Lane);
        Assert.Contains(store.Trail(verify.Id), t => t.Text.StartsWith("Left out of Done", StringComparison.Ordinal));
        Assert.DoesNotContain(store.Trail(verify.Id), t => t.Kind is "closed over a FAIL" or "won't do");
    }

    [Fact]
    public void Digest_AndWorkflowShow_SayTheSameLine()
    {
        var t0 = new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
        var thread = new List<Message>
        {
            new(1, "agent.instruction.payments/Manager", JsonSerializer.Serialize(new { text = "ship it" }), MessageTypes.PersonSource, 1, null, 0, t0),
            new(2, MessageTypes.WorkflowCompleted, JsonSerializer.Serialize(Completed(
                Entry(7, "accepted", "FAIL: login still 500s", null),
                Entry(8, "wont-do", "FAIL: CSV export missing", "CSV export is dropped"))), "payments/Manager", 1, 1, 1, t0.AddMinutes(30)),
        };
        var text = WorkflowDigest.Build(new DigestInput(1, "payments", "completed", thread, [], [], [], new HashSet<string>(), t0.AddHours(1))).Text;
        Assert.Contains("- card 7 (tester TesterWren): Closed over a FAIL, accepted by the person: «FAIL: login still 500s»", text);
        Assert.Contains("- card 8 (tester TesterWren): Won't do, accepted by the person: «CSV export is dropped»", text);

        var show = McpBusServer.WorkflowText(thread);
        Assert.Contains("shipped | card 7: Closed over a FAIL, accepted by the person: FAIL: login still 500s | card 8: Won't do, accepted by the person: CSV export is dropped", show);

        // Without the field: neither says it.
        thread[1] = thread[1] with { Payload = JsonSerializer.Serialize(new { delivered = "shipped" }) };
        Assert.DoesNotContain("closed over a FAIL", WorkflowDigest.Build(new DigestInput(1, "payments", "completed", thread, [], [], [], new HashSet<string>(), t0.AddHours(1))).Text);
        Assert.DoesNotContain("Closed over a FAIL", McpBusServer.WorkflowText(thread));
    }

    [Fact]
    public void ConciergeWatch_SaysClosedOverAFailAccepted()
    {
        var with = M(MessageTypes.WorkflowCompleted, Completed(Entry(7, "accepted", "FAIL: login still 500s", null)), "payments/Manager", 50);
        Assert.Equal(("done", "**payments** finished, closed over a FAIL (accepted): shipped"), ConciergeWatcher.Describe(with, "payments"));

        var without = M(MessageTypes.WorkflowCompleted, new { delivered = "shipped" }, "payments/Manager", 51);
        Assert.Equal(("done", "**payments** finished: shipped"), ConciergeWatcher.Describe(without, "payments"));
    }
}
