using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Tests.Bus;
using Xunit;
using static Yawble.Core.Tests.Board.TimelineCalculatorTests;
using C = Yawble.Core.Board.TimeCategories;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// Card and deliverable time through the store: the projector records each card's states and markers (0047), an old card's timeline is rebuilt
/// from its trail, and deliverable 3573's trail is the reference case. Fake clocks only.
/// </summary>
public class TimelineStoreTests
{
    private long _seq = 1000;

    private Message M(string type, object payload, string source, long deliverable, DateTimeOffset at, long? causation = null) =>
        new(++_seq, type, JsonSerializer.Serialize(payload), source, deliverable, causation, 0, at);

    private Message Tell(string member, string text, long deliverable, DateTimeOffset at, string from = "web/Manager", object? extra = null, long? causation = null)
    {
        var payload = new JsonObject { ["text"] = text };
        if (extra is not null) foreach (var (k, v) in JsonSerializer.SerializeToNode(extra)!.AsObject()) payload[k] = v?.DeepClone();
        return new(++_seq, "agent.instruction.web/" + member, payload.ToJsonString(), from, deliverable, causation, 0, at);
    }

    private static (TempDb Db, BoardStore Store, BoardProjector P, ManualClock Clock) Rig(DateTimeOffset start)
    {
        var db = new TempDb();
        var clock = new ManualClock(start);
        var store = new BoardStore(db.ConnectionString, clock);
        return (db, store, new BoardProjector(store), clock);
    }

    private static long Min(double minutes) => (long)TimeSpan.FromMinutes(minutes).TotalMilliseconds;

    private static long Share(TimeSummary t, string category) => t.Breakdown.FirstOrDefault(b => b.Category == category)?.Ms ?? 0;

    private static CardTimeline TimeOf(BoardStore store, long card, DateTimeOffset now) =>
        new BoardTimeline(store).OfCard(store.Card(card)!, [], now);

    /// <summary>Makes a card look as if it was made before 0047: no timeline lines, to be rebuilt from its trail.</summary>
    private static void MakeOld(TempDb db, long card)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM board_timeline WHERE card_id = $c; UPDATE board_cards SET timeline_rebuild = 1, timeline_rebuilt = 0, started_at = NULL WHERE id = $c;";
        cmd.Parameters.AddWithValue("$c", card);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ThePlannedAndStartedTimes_ComeFromThePlanAndTheTellThatClaimsIt()
    {
        var (db, store, p, clock) = Rig(At("16:01"));
        using var _ = db;
        var planned = store.AddCard("web", null, "B008", "Timeline", "", "queued", Lanes.Todo, 3573);
        store.AddTrail(planned.Id, "Manager", "planned", "Timeline");
        p.Apply(Tell("TesterKofi", "Test the timeline", 3573, At("16:54"), extra: new { card = planned.Id }));
        p.Apply(M(MessageTypes.Progress, new { status = "testing" }, "web/TesterKofi", 3573, At("17:00")));
        p.Apply(M(MessageTypes.Handback, new { delivered = "tested" }, "web/TesterKofi", 3573, At("17:30")));

        var card = store.Card(planned.Id)!;
        Assert.Equal(At("16:54"), BoardStore.Time(card.StartedAt!));
        var t = TimeOf(store, card.Id, At("18:00"));
        Assert.Equal(At("16:01"), BoardStore.Time(t.Time.CreatedAt));
        Assert.Equal(At("16:54"), BoardStore.Time(t.Time.StartedAt!));
        Assert.Equal(Min(53), Share(t.Time, C.NotStarted));
        Assert.Equal(Min(6), Share(t.Time, C.WaitingOnTeammate)); // told, but no run in the run queue: not a slot wait
        Assert.Equal(0, Share(t.Time, C.WaitingForSlot));
        Assert.Equal(Min(30), Share(t.Time, C.AgentWorking));
        Assert.Equal(Min(89), t.Time.OverallMs);
        Assert.Equal(("done", false, false), (t.Time.EndedHow, t.Time.Open, t.Time.Rebuilt));
        Assert.Contains(t.Markers, m => m.Kind == TimeMarkers.Handback && m.At == At("17:30"));
    }

    [Fact]
    public void AQuestionAndItsAnswerAreMarkers_AndTheWaitIsWaitingOnYou()
    {
        var (db, store, p, _) = Rig(At("10:00"));
        using var __ = db;
        p.Apply(Tell("DeveloperAda", "Add a licence", 5, At("10:00")));
        var id = store.Cards("web")[0].Id;
        var asked = M(MessageTypes.NeedsDecision, new { question = "MIT?" }, "web/DeveloperAda", 5, At("10:10"));
        p.Apply(asked);
        p.Apply(Tell("DeveloperAda", "MIT", 5, At("10:40"), from: "person", causation: asked.Seq));
        p.Apply(M(MessageTypes.Handback, new { delivered = "LICENSE" }, "web/DeveloperAda", 5, At("10:50")));
        var t = TimeOf(store, id, At("11:00"));
        Assert.Equal(Min(30), Share(t.Time, C.WaitingOnYou));
        Assert.Contains(t.Markers, m => m.Kind == TimeMarkers.DecisionAsked && m.At == At("10:10"));
        Assert.Contains(t.Markers, m => m.Kind == TimeMarkers.DecisionAnswered && m.At == At("10:40"));
    }

    [Fact]
    public void ARunTheWatchdogEnded_IsStalledFromItsLastActivity_AndAMarker()
    {
        var (db, store, p, _) = Rig(At("16:54"));
        using var __ = db;
        p.Apply(Tell("TesterKofi", "Test it", 3573, At("16:54")));
        p.Apply(M(MessageTypes.Progress, new { status = "running the suite" }, "web/TesterKofi", 3573, At("17:14")));
        p.Apply(M(MessageTypes.Failed, new { reason = "idle: no progress for 1800 s", failureClass = "idle" }, "web/TesterKofi", 3573, At("17:52")));
        var id = store.Cards("web")[0].Id;
        var t = TimeOf(store, id, At("18:00"));
        var stalled = Assert.Single(t.Bands, b => b.Category == C.Stalled);
        Assert.Equal((At("17:14"), At("17:52")), (stalled.Start, stalled.End));
        Assert.Contains(t.Markers, m => m.Kind == TimeMarkers.WatchdogEnd && m.At == At("17:52"));
        Assert.Equal(Min(8), Share(t.Time, C.Blocked)); // failed: Blocked from the end to now
        Assert.True(t.Time.Open);
    }

    /// <summary>
    /// A run the person stopped is not a failure on the timeline either: no watchdog-end marker, no Blocked
    /// time (the card keeps its status), while the stalls the run did have are kept. Rebuilt from the trail, the same.
    /// </summary>
    [Fact]
    public void ARunThePersonStopped_IsNotBlockedOrAWatchdogEnd_LiveOrRebuilt()
    {
        var (db, store, p, _) = Rig(At("16:54"));
        using var __ = db;
        p.Apply(Tell("TesterKofi", "Test it", 3573, At("16:54")));
        p.Apply(M(MessageTypes.Progress, new { status = "running the suite" }, "web/TesterKofi", 3573, At("17:14")));
        p.Apply(M(MessageTypes.Failed, new
        {
            reason = BusEngine.StoppedReason, failureClass = FailureClasses.Stopped,
            activity = new { stalls = new[] { new { start = TimelineCalculator.Iso(At("17:20")), end = TimelineCalculator.Iso(At("17:30")) } } },
        }, "web/TesterKofi", 3573, At("17:52")));
        var card = store.Cards("web")[0];
        Assert.Equal(("stopped", Lanes.InProgress), (card.Status, card.Lane));

        // After the stop the card is Idle (8 min to 18:00): not Blocked, not Agent working.
        var t = TimeOf(store, card.Id, At("18:00"));
        Assert.DoesNotContain(t.Markers, m => m.Kind == TimeMarkers.WatchdogEnd);
        Assert.Equal(0, Share(t.Time, C.Blocked));
        Assert.Equal(Min(8), Share(t.Time, C.Idle));
        Assert.DoesNotContain(t.Bands, b => b.Category == C.AgentWorking && b.End > At("17:52"));
        var stalled = Assert.Single(t.Bands, b => b.Category == C.Stalled);
        Assert.Equal((At("17:20"), At("17:30")), (stalled.Start, stalled.End));

        MakeOld(db, card.Id);
        var rebuilt = TimeOf(store, card.Id, At("18:00"));
        Assert.DoesNotContain(rebuilt.Markers, m => m.Kind == TimeMarkers.WatchdogEnd);
        Assert.Equal(0, Share(rebuilt.Time, C.Blocked));
        Assert.Equal(Min(8), Share(rebuilt.Time, C.Idle));
    }

    /// <summary>New work after a stop re-queues the card, live and rebuilt. With no run in the run queue that wait is Waiting on a teammate, not Waiting for a slot.</summary>
    [Fact]
    public void NewWorkAfterAStop_WaitsAgain_LiveOrRebuilt()
    {
        var (db, store, p, _) = Rig(At("10:00"));
        using var __ = db;
        p.Apply(Tell("DeveloperAda", "Build it", 9, At("10:00")));
        p.Apply(M(MessageTypes.Progress, new { status = "building" }, "web/DeveloperAda", 9, At("10:05")));
        p.Apply(M(MessageTypes.Failed, new { reason = BusEngine.StoppedReason, failureClass = FailureClasses.Stopped }, "web/DeveloperAda", 9, At("10:20")));
        p.Apply(Tell("DeveloperAda", "Carry on", 9, At("10:50")));
        var card = store.Cards("web")[0];
        Assert.Equal(("queued", Lanes.Todo), (card.Status, card.Lane));

        foreach (var t in new[] { TimeOf(store, card.Id, At("11:00")), Rebuilt() })
        {
            Assert.Equal(Min(30), Share(t.Time, C.Idle));            // 10:20 - 10:50, stopped
            Assert.Equal(Min(15), Share(t.Time, C.WaitingOnTeammate)); // 10:00 - 10:05 and 10:50 - 11:00
            Assert.Equal(0, Share(t.Time, C.WaitingForSlot));
            Assert.Equal(0, Share(t.Time, C.Blocked));
        }

        CardTimeline Rebuilt()
        {
            MakeOld(db, card.Id);
            return TimeOf(store, card.Id, At("11:00"));
        }
    }

    [Fact]
    public void ARunsOwnStallsAndLeaseWaits_FromItsEnd_AreUsedAsTheyCame()
    {
        var (db, store, p, _) = Rig(At("10:00"));
        using var __ = db;
        p.Apply(Tell("DeveloperAda", "Build it", 8, At("10:00")));
        p.Apply(M(MessageTypes.Progress, new { status = "building" }, "web/DeveloperAda", 8, At("10:01")));
        p.Apply(M(MessageTypes.Failed, new
        {
            reason = "idle: no progress for 1800 s", failureClass = "idle",
            activity = new
            {
                stalls = new[] { new { start = TimelineCalculator.Iso(At("10:50")), end = TimelineCalculator.Iso(At("11:20")) } },
                holds = new[] { new { start = TimelineCalculator.Iso(At("10:10")), end = TimelineCalculator.Iso(At("10:30")) } },
            },
        }, "web/DeveloperAda", 8, At("11:20")));
        var t = TimeOf(store, store.Cards("web")[0].Id, At("11:20"));
        Assert.Equal(Min(30), Share(t.Time, C.Stalled)); // the run's own stall, not "from the last trail line" (10:01)
        Assert.Equal(Min(20), Share(t.Time, C.WaitingForSlot)); // the lease wait 10:10-10:30
        Assert.Equal(Min(1), Share(t.Time, C.WaitingOnTeammate)); // told 10:00-10:01 with no run in the run queue
        Assert.Equal(Min(9 + 20), Share(t.Time, C.AgentWorking));
    }

    [Fact]
    public void TheManagersCardlessRun_GoesOnTheDeliverablesTimeline()
    {
        var (db, store, p, _) = Rig(At("10:00"));
        using var __ = db;
        p.Apply(M(MessageTypes.Completed, new
        {
            activity = new { stalls = new[] { new { start = TimelineCalculator.Iso(At("10:05")), end = TimelineCalculator.Iso(At("10:25")) } } },
        }, "web/Manager", 12, At("10:30")));
        var line = Assert.Single(store.LooseTimeline(12));
        Assert.Equal(("stall", "Manager", (long?)null), (line.Kind, line.Member, line.CardId));
    }

    [Fact]
    public void AnExpiredDeliverablesCardsEndAtItsLastActivity()
    {
        var (db, store, p, _) = Rig(At("09:00", 14));
        using var __ = db;
        p.Apply(Tell("DeveloperAda", "Refactor", 21, At("09:00", 14)));
        p.Apply(M(MessageTypes.Progress, new { status = "refactoring" }, "web/DeveloperAda", 21, At("09:30", 14)));
        p.Apply(M(MessageTypes.Completed, new { }, "web/DeveloperAda", 21, At("10:00", 14)));
        p.Apply(Tell("DeveloperAda", "One more thing", 21, At("10:30", 14))); // told again; nobody runs it
        var expired = M(MessageTypes.DeliverableExpired, new
        {
            team = "web", reason = "expired", detail = "no activity for 3 days",
            endedAt = TimelineCalculator.Iso(At("10:30", 14)), expiredAt = TimelineCalculator.Iso(At("10:30", 17)),
        }, MessageTypes.HostSource, 21, At("10:30", 17));
        p.Apply(expired);

        var card = store.Cards("web")[0];
        var t = TimeOf(store, card.Id, At("12:00", 17));
        Assert.False(t.Time.Open);
        Assert.Equal(("expired", At("10:30", 14)), (t.Time.EndedHow, BoardStore.Time(t.Time.EndedAt!)));
        Assert.Equal(Min(90), t.Time.OverallMs);
        Assert.Contains(t.Markers, m => m.Kind == TimeMarkers.Expired && m.At == At("10:30", 17));

        var root = Tell("Manager", "Refactor the module", 21, At("09:00", 14), from: "person");
        var ends = BoardTimeline.Ends([root, expired]);
        Assert.Equal(new DeliverableEnd(At("10:30", 14), "expired", null, At("10:30", 17)), Assert.Single(ends));
    }

    // ---------------------------------------------------------------- the history rebuild

    [Fact]
    public void AnOldCardsTimeline_IsRebuiltFromItsTrail_IncludingAStallFromAFailedIdleLine()
    {
        var (db, store, _, _) = Rig(At("16:01"));
        using var __ = db;
        var card = store.AddCard("web", null, "B007", "Verify the code map", "", "queued", Lanes.Todo, 3573);
        store.AddTrail(card.Id, "Manager", "planned", "Verify the code map", TimelineCalculator.Iso(At("16:01")));
        store.AddTrail(card.Id, "Manager", "assigned", "Assigned to TesterKofi: verify it", TimelineCalculator.Iso(At("16:54")));
        store.AddTrail(card.Id, "TesterKofi", "progress", "fetching the branch", TimelineCalculator.Iso(At("16:58")));
        store.AddTrail(card.Id, "TesterKofi", "progress", "running e2e", TimelineCalculator.Iso(At("17:14")));
        store.AddTrail(card.Id, "TesterKofi", "failed", "idle: no progress for 1800 s", TimelineCalculator.Iso(At("17:52")));
        using (var c = new SqliteConnection(db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE board_cards SET member = 'TesterKofi', status = 'failed', lane = 'blocked' WHERE id = $c;";
            cmd.Parameters.AddWithValue("$c", card.Id);
            cmd.ExecuteNonQuery();
        }
        MakeOld(db, card.Id);

        var t = TimeOf(store, card.Id, At("18:00"));
        Assert.True(t.Time.Rebuilt);
        Assert.True(store.Card(card.Id)!.Rebuilt);
        Assert.All(store.Timeline(card.Id), l => Assert.True(l.Rebuilt));
        Assert.Equal(At("16:54"), BoardStore.Time(t.Time.StartedAt!));
        Assert.Equal([C.NotStarted, C.WaitingOnTeammate, C.AgentWorking, C.Stalled, C.Blocked], t.Bands.Select(b => b.Category)); // no run queued
        Assert.Equal(Min(38), Share(t.Time, C.Stalled));
        Assert.Contains(t.Markers, m => m.Kind == TimeMarkers.WatchdogEnd && m.At == At("17:52"));

        // Once: reading it again does not add the lines again.
        var lines = store.Timeline(card.Id).Count;
        TimeOf(store, card.Id, At("18:00"));
        Assert.Equal(lines, store.Timeline(card.Id).Count);
    }

    [Fact]
    public void TheRebuildReplaysMovesEditsAReopenAndTheTesterRule()
    {
        var card = new CardRow(1, "web", "TesterKofi", null, "t", "", "done", Lanes.Done, true, false, 4,
            TimelineCalculator.Iso(At("10:00")), TimelineCalculator.Iso(At("13:00")));
        IReadOnlyList<TrailRow> trail =
        [
            new(1, TimelineCalculator.Iso(At("10:00")), "Manager", "told", "check it"),
            new(2, TimelineCalculator.Iso(At("10:10")), "TesterKofi", "progress", "checking"),
            new(3, TimelineCalculator.Iso(At("10:30")), "DeveloperAda", "handed back", "fixed"),
            new(4, TimelineCalculator.Iso(At("10:30")), "board", "comment", "Not Done yet: TesterKofi has not tested it since it was last assigned."),
            new(5, TimelineCalculator.Iso(At("11:00")), "you", "moved", "Moved to Done. Note: enough"),
            new(6, TimelineCalculator.Iso(At("12:00")), "you", "edited", "Edited: status queued"),
            new(7, TimelineCalculator.Iso(At("12:30")), "TesterKofi", "handed back", "tested"),
        ];
        var r = TimelineRebuild.FromTrail(card, trail);
        var states = r.Lines.Where(l => l.Kind == "state").Select(l => (l.At[11..16], l.Category, l.Done)).ToList();
        // 10:30: the developer's hand-back would put it in Done, and the tester rule's comment at the same moment keeps it out.
        Assert.Equal([("10:00", C.WaitingForSlot, false), ("10:10", C.AgentWorking, false), ("10:30", C.WasDone, true), ("10:30", C.WaitingOnTeammate, false),
            ("11:00", C.WasDone, true), ("12:00", C.WaitingForSlot, false), ("12:30", C.WasDone, true)], states);
        Assert.Equal(TimelineCalculator.Iso(At("10:00")), r.StartedAt);
        Assert.Equal(2, r.Lines.Count(l => l.Marker == TimeMarkers.Handback));
    }

    // ---------------------------------------------------------------- the reference case: deliverable 3573

    /// <summary>
    /// A deliverable whose cards have a trail and no timeline lines, so the timeline is rebuilt from the trail: a
    /// developer's card, then the tester's card, which the Manager plans at 16:01 and gives the tester at 16:54. The
    /// tester's run shows no progress from 17:14 until the idle watchdog ends it at 17:52 UTC; the Manager tells it again
    /// and it hands back at 18:13; the deliverable is declared complete at 18:14. About 3 h overall, 38 min of it Stalled
    /// (the tester's card), not Agent working.
    /// </summary>
    [Fact]
    public void Deliverable3573_IsAbout3hOverall_With38MinutesStalledForTheTestersCard()
    {
        var (db, store, _, _) = Rig(At("15:12:40"));
        using var __ = db;
        string I(string hhmm) => TimelineCalculator.Iso(At(hhmm));
        var dev = store.AddCard("browser", null, "B007", "Code map: files stay 2 minutes", "", "queued", Lanes.Todo, 3573, I("15:20"));
        foreach (var (at, source, kind, text) in new[]
        {
            ("15:20", "Manager", "planned", "Code map: files stay 2 minutes"),
            ("15:22", "Manager", "assigned", "Assigned to DeveloperTomas: build it"),
            ("15:30", "DeveloperTomas", "progress", "reading the code map"),
            ("16:10", "DeveloperTomas", "progress", "tests pass"),
            ("16:41", "DeveloperTomas", "handed back", "fe233e3"),
            ("18:14:10", "Manager", "deliverable done", "the work is declared complete"),
        }) store.AddTrail(dev.Id, source, kind, text, I(at));
        var testerCard = store.AddCard("browser", null, "B007", "Verify the code map", "", "queued", Lanes.Todo, 3573, I("16:01"));
        foreach (var (at, source, kind, text) in new[]
        {
            ("16:01", "Manager", "planned", "Verify the code map"),
            ("16:54", "Manager", "assigned", "Assigned to TesterKofi: verify the code map at 1280 and 375"),
            ("16:58", "TesterKofi", "progress", "fetching developertomas"),
            ("17:14", "TesterKofi", "progress", "running the e2e suite"),
            ("17:52", "TesterKofi", "failed", "idle: no progress for 1800 s"),
            ("17:57", "Manager", "told", "Your run was ended by the idle watchdog. Run the suite again with progress calls."),
            ("18:00", "TesterKofi", "progress", "running the e2e suite again"),
            ("18:13", "TesterKofi", "handed back", "verified: pass"),
            ("18:14:10", "Manager", "deliverable done", "the work is declared complete"),
        }) store.AddTrail(testerCard.Id, source, kind, text, I(at));
        using (var c = new SqliteConnection(db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE board_cards SET member = 'DeveloperTomas', status = 'done', lane = 'done', ended_at = $end, ended_how = 'completed' WHERE id = $dev;
                UPDATE board_cards SET member = 'TesterKofi', status = 'done', lane = 'done', ended_at = $end, ended_how = 'completed' WHERE id = $tester;
                """;
            cmd.Parameters.AddWithValue("$end", I("18:14:10"));
            cmd.Parameters.AddWithValue("$dev", dev.Id);
            cmd.Parameters.AddWithValue("$tester", testerCard.Id);
            cmd.ExecuteNonQuery();
        }
        MakeOld(db, dev.Id);
        MakeOld(db, testerCard.Id);

        var thread = new[]
        {
            Tell("Manager", "Backlog item B007 is dispatched to your team.", 3573, At("15:12:40"), from: "person", extra: new { item = "B007" }),
            Tell("DeveloperTomas", "build it", 3573, At("15:22")),
            Tell("TesterKofi", "verify the code map", 3573, At("16:54")),
            M(MessageTypes.Failed, new { reason = "idle: no progress for 1800 s", failureClass = "idle" }, "browser/TesterKofi", 3573, At("17:52")),
            Tell("TesterKofi", "Run the suite again", 3573, At("17:57")),
            M(MessageTypes.DeliverableCompleted, new { delivered = "B007 verified" }, "browser/Manager", 3573, At("18:14:10")),
        };
        var w = new BoardTimeline(store).OfDeliverable(3573, thread, [], At("19:00"))!;

        Assert.False(w.Time.Open);
        Assert.Equal("completed", w.Time.EndedHow);
        Assert.True(w.Time.Rebuilt);
        Assert.InRange(TimeSpan.FromMilliseconds(w.Time.OverallMs), TimeSpan.FromHours(2.9), TimeSpan.FromHours(3.1));
        Assert.Equal((long)TimeSpan.FromSeconds(3 * 3600 + 90).TotalMilliseconds, w.Time.OverallMs); // 15:12:40 -> 18:14:10
        Assert.Equal(w.Time.OverallMs, w.Time.Breakdown.Sum(b => b.Ms));
        Assert.Equal(Min(38), Share(w.Time, C.Stalled));

        var testerRow = w.Rows.Single(r => r.Card == testerCard.Id);
        Assert.Equal("TesterKofi", testerRow.Member);
        var stall = Assert.Single(testerRow.Bands, b => b.Category == C.Stalled);
        Assert.Equal((At("17:14"), At("17:52")), (stall.Start, stall.End));
        var summaryStall = Assert.Single(w.Summary, b => b.Category == C.Stalled);
        Assert.Equal((At("17:14"), At("17:52")), (summaryStall.Start, summaryStall.End));
        Assert.Contains(w.Markers, m => m.Kind == TimeMarkers.WatchdogEnd && m.Card == testerCard.Id && m.At == At("17:52"));

        // The tester's card on its own: planned 16:01, started 16:54, done 18:13; 53 min before it started.
        var t = TimeOf(store, testerCard.Id, At("19:00"));
        Assert.Equal((At("16:01"), At("16:54"), At("18:13")),
            (BoardStore.Time(t.Time.CreatedAt), BoardStore.Time(t.Time.StartedAt!), BoardStore.Time(t.Time.EndedAt!)));
        Assert.Equal(Min(53), Share(t.Time, C.NotStarted));
        Assert.Equal(Min(132), t.Time.OverallMs);
        Assert.Equal(Min(38), Share(t.Time, C.Stalled));
        Assert.Equal(Min(29), Share(t.Time, C.AgentWorking));
    }

    // ---------------------------------------------------------------- the wire contract

    [Fact]
    public void TheWireNamesAreTheContracts()
    {
        var now = At("12:00");
        var card = TimelineCalculator.Card(new CardTimelineInput(4, "Ada", "t", At("10:00"), At("10:00"), null, null, false,
            [new TimelineState(At("10:00"), C.AgentWorking, false, "Ada"), new TimelineState(At("11:00"), C.WasDone, true, "Ada")], [], [], [], []), now);
        var json = BoardTimeline.Json(card);
        var time = json["time"]!.AsObject();
        Assert.Equal(["createdAt", "startedAt", "endedAt", "endedHow", "open", "overallMs", "rebuilt", "breakdown"], time.Select(p => p.Key));
        Assert.Equal("done", time["endedHow"]!.GetValue<string>());
        Assert.Equal(Min(60), time["overallMs"]!.GetValue<long>());
        var share = time["breakdown"]![0]!.AsObject();
        Assert.Equal(("agent-working", Min(60)), (share["category"]!.GetValue<string>(), share["ms"]!.GetValue<long>()));
        var row = json["rows"]![0]!.AsObject();
        Assert.Equal(["card", "member", "title", "bands"], row.Select(p => p.Key));
        Assert.Equal(["category", "start", "end"], row["bands"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["kind", "at", "card"], json["markers"]![0]!.AsObject().Select(p => p.Key));

        var w = BoardTimeline.Json(TimelineCalculator.Deliverable(new DeliverableTimelineInput(9, At("09:00"), [], [card], []), now));
        Assert.Equal(["time", "rows", "summary", "markers"], w.Select(p => p.Key));
    }
}
