using System.Globalization;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Xunit;
using C = Yawble.Core.Board.TimeCategories;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// The arithmetic of a card's and a deliverable's time. Fixed times only: "now" is always given.
/// </summary>
public class TimelineCalculatorTests
{
    internal static DateTimeOffset At(string hhmm, int day = 17) =>
        DateTimeOffset.Parse($"2026-11-{day:00}T{hhmm}{(hhmm.Length == 5 ? ":00" : "")}Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

    private static TimelineState S(string at, string category, bool done = false, string? member = "Ada", string? until = null) =>
        new(At(at), category, done, member, until is null ? null : At(until));

    private static CardTimelineInput Card(string created, IReadOnlyList<TimelineState> states, string? started = null,
        IReadOnlyList<RunSpan>? runs = null, IReadOnlyList<ActivitySpan>? stalls = null, IReadOnlyList<ActivitySpan>? holds = null,
        long id = 1, string? member = "Ada", string? endedHow = null, string? endedAt = null) =>
        new(id, member, $"card {id}", At(created), started is null ? null : At(started), endedHow, endedAt is null ? null : At(endedAt), false,
            states, stalls ?? [], holds ?? [], runs ?? [], []);

    private static ActivitySpan Span(string from, string to) => new(At(from), At(to));

    private static long Min(double minutes) => (long)TimeSpan.FromMinutes(minutes).TotalMilliseconds;

    private static long Share(TimeSummary t, string category) => t.Breakdown.FirstOrDefault(b => b.Category == category)?.Ms ?? 0;

    private static void AddsUp(TimeSummary t) => Assert.Equal(t.OverallMs, t.Breakdown.Sum(b => b.Ms));

    // ---------------------------------------------------------------- the category of each state

    [Theory]
    [InlineData("Ada", "queued", Lanes.Todo, C.WaitingForSlot)]
    [InlineData("Ada", "running", Lanes.InProgress, C.AgentWorking)]
    [InlineData("Ada", "needs-decision", Lanes.NeedsYou, C.WaitingOnYou)]
    [InlineData("Ada", "blocked", Lanes.NeedsYou, C.Blocked)]
    [InlineData("Ada", "failed", Lanes.NeedsYou, C.Blocked)]
    [InlineData("Ada", "handback", Lanes.InProgress, C.WaitingOnTeammate)]
    [InlineData("Ada", "completed", Lanes.InProgress, C.WaitingOnTeammate)]
    [InlineData("Ada", "done", Lanes.InProgress, C.Idle)]
    [InlineData("Ada", "handback", Lanes.Done, C.WasDone)]
    [InlineData(null, "queued", Lanes.Todo, C.NotStarted)]
    public void EachStateHasOneCategory(string? member, string status, string lane, string expected) =>
        Assert.Equal(expected, C.OfState(member, status, lane));

    [Fact]
    public void ThePriorityOrderIsTheSpecs_AndEveryCategoryIsKebabCase()
    {
        Assert.Equal(["agent-working", "waiting-on-you", "stalled", "waiting-for-slot", "waiting-on-teammate", "blocked", "scheduled",
            "not-started", "idle", "was-done"], C.ByPriority);
        Assert.Equal(TimeSpan.FromMinutes(10), C.StallThreshold);
    }

    [Fact]
    public void ARunOverridesTheStoredState_QueuedIsWaitingForASlotUntilItStarts()
    {
        // Told at 10:00, the run queued until 10:05, ran until 10:40 (no progress call yet), then handed back.
        var t = TimelineCalculator.Card(Card("10:00",
            [S("10:00", C.WaitingForSlot), S("10:40", C.WaitingOnTeammate), S("10:50", C.WasDone, done: true)], started: "10:00",
            runs: [new RunSpan("Ada", At("10:00"), At("10:05"), At("10:40"))]), At("12:00"));
        Assert.Equal(Min(5), Share(t.Time, C.WaitingForSlot));
        Assert.Equal(Min(35), Share(t.Time, C.AgentWorking));
        Assert.Equal(Min(10), Share(t.Time, C.WaitingOnTeammate));
        Assert.Equal(Min(50), t.Time.OverallMs);
        AddsUp(t.Time);
    }

    [Fact]
    public void AScheduledCardWaitsUntilItsWake_ThenIsDoneAgain()
    {
        var t = TimelineCalculator.Card(Card("10:00",
            [S("10:00", C.AgentWorking), S("10:30", C.WasDone, done: true), S("10:30", C.Scheduled, done: true, until: "11:00")]), At("12:00"));
        Assert.False(t.Time.Open);
        Assert.Equal(At("11:00"), DateTimeOffset.Parse(t.Time.EndedAt!, CultureInfo.InvariantCulture));
        Assert.Equal(Min(30), Share(t.Time, C.Scheduled));
    }

    // ---------------------------------------------------------------- Stalled

    [Fact]
    public void Stalled_StartsAtTheLastActivity_AndEndsAtTheNextActivity()
    {
        var t = TimelineCalculator.Card(Card("10:00", [S("10:00", C.AgentWorking), S("11:00", C.WaitingOnTeammate)],
            runs: [new RunSpan("Ada", At("10:00"), At("10:00"), At("11:00"))], stalls: [Span("10:10", "10:40")]), At("11:30"));
        var stalled = Assert.Single(t.Bands, b => b.Category == C.Stalled);
        Assert.Equal((At("10:10"), At("10:40")), (stalled.Start, stalled.End));
        Assert.Equal([C.AgentWorking, C.Stalled, C.AgentWorking, C.WaitingOnTeammate], t.Bands.Select(b => b.Category));
        Assert.Equal(Min(30), Share(t.Time, C.AgentWorking));
        AddsUp(t.Time);
    }

    [Fact]
    public void Stalled_EndsWhenTheRunEnds()
    {
        // The idle watchdog ended the run at 11:00: it was Stalled from its last activity (10:30) to then; then Blocked (failed).
        var t = TimelineCalculator.Card(Card("10:00", [S("10:00", C.AgentWorking), S("11:00", C.Blocked)],
            runs: [new RunSpan("Ada", At("10:00"), At("10:00"), At("11:00"))], stalls: [Span("10:30", "11:00")]), At("11:20"));
        Assert.Equal([C.AgentWorking, C.Stalled, C.Blocked], t.Bands.Select(b => b.Category));
        Assert.Equal(At("11:00"), t.Bands[1].End);
        Assert.Equal(Min(20), Share(t.Time, C.Blocked));
    }

    [Fact]
    public void AWaitForTheHeavyWorkLeaseIsWaitingForASlot_NotStalled()
    {
        var t = TimelineCalculator.Card(Card("10:00", [S("10:00", C.AgentWorking)],
            runs: [new RunSpan("Ada", At("10:00"), At("10:00"), null)], holds: [Span("10:20", "10:50")]), At("11:00"));
        Assert.Equal(Min(30), Share(t.Time, C.WaitingForSlot));
        Assert.Equal(0, Share(t.Time, C.Stalled));
        Assert.True(t.Time.Open);
        Assert.Equal(Min(60), t.Time.OverallMs);
    }

    [Fact]
    public void TheRunsIdleClockKeepsItsStalls_FromTheLastActivity_AndItsLeaseWaits()
    {
        var clock = new ManualClock(At("10:00"));
        var activity = new Yawble.Core.Launch.RunActivity(clock);
        clock.Advance(TimeSpan.FromMinutes(5));
        activity.Touch(); // 10:05: a tool call
        clock.Advance(TimeSpan.FromMinutes(9));
        activity.Touch(); // 10:14: 9 min of quiet is under the threshold
        clock.Advance(TimeSpan.FromMinutes(25));
        activity.Touch(); // 10:39: 25 min of quiet: Stalled 10:14 -> 10:39
        activity.Hold(); // waits for the lease from 10:39
        clock.Advance(TimeSpan.FromMinutes(40));
        activity.Resume(); // 11:19: a wait is never a stall
        clock.Advance(TimeSpan.FromMinutes(12));
        var spans = activity.Spans(clock.GetUtcNow()); // 11:31: quiet since 11:19, still going
        Assert.Equal([Span("10:14", "10:39"), Span("11:19", "11:31")], spans.Stalls);
        Assert.Equal([Span("10:39", "11:19")], spans.Holds);
    }

    // ---------------------------------------------------------------- Not started, planned and started

    [Fact]
    public void NotStarted_FromPlannedToAssigned_AndThePlannedAndStartedTimes()
    {
        var t = TimelineCalculator.Card(Card("16:01",
            [S("16:01", C.NotStarted, member: null), S("16:54", C.WaitingForSlot, member: "TesterKofi"), S("17:10", C.WasDone, done: true, member: "TesterKofi")],
            started: "16:54", member: "TesterKofi"), At("18:00"));
        Assert.Equal(Min(53), Share(t.Time, C.NotStarted));
        Assert.Equal(TimelineCalculator.Iso(At("16:01")), t.Time.CreatedAt);
        Assert.Equal(TimelineCalculator.Iso(At("16:54")), t.Time.StartedAt);
        Assert.Contains(t.Markers, m => m.Kind == TimeMarkers.Planned && m.At == At("16:01"));
        Assert.Contains(t.Markers, m => m.Kind == TimeMarkers.Started && m.At == At("16:54"));
        Assert.Equal("done", t.Time.EndedHow);
    }

    // ---------------------------------------------------------------- overall with a reopen

    [Fact]
    public void Overall_RunsToTheLastDone_AndTheTimeDoneBeforeAReopenCountsAsWasDone()
    {
        var t = TimelineCalculator.Card(Card("10:00",
            [S("10:00", C.AgentWorking), S("11:00", C.WasDone, done: true), S("13:00", C.WaitingForSlot), S("13:10", C.AgentWorking),
             S("14:00", C.WasDone, done: true)]), At("15:00"));
        Assert.False(t.Time.Open);
        Assert.Equal(Min(240), t.Time.OverallMs);
        Assert.Equal(Min(120), Share(t.Time, C.WasDone));
        Assert.Equal(TimelineCalculator.Iso(At("14:00")), t.Time.EndedAt);
        Assert.Single(t.Markers, m => m.Kind == TimeMarkers.Reopen && m.At == At("13:00"));
        AddsUp(t.Time);

        // Reopened and still open: the end moves to now.
        var reopened = TimelineCalculator.Card(Card("10:00",
            [S("10:00", C.AgentWorking), S("11:00", C.WasDone, done: true), S("13:00", C.WaitingForSlot)]), At("13:30"));
        Assert.True(reopened.Time.Open);
        Assert.Null(reopened.Time.EndedAt);
        Assert.Equal(Min(210), reopened.Time.OverallMs);
    }

    // ---------------------------------------------------------------- the deliverable merge

    [Fact]
    public void ADeliverableTakesTheHighestCategoryOfItsOpenCards_AndItsCategoriesAddUpToItsOverallExactly()
    {
        var now = At("14:00");
        var a = TimelineCalculator.Card(Card("10:00", [S("10:00", C.AgentWorking), S("11:00", C.WaitingOnYou), S("12:00", C.WasDone, done: true)], id: 1), now);
        var b = TimelineCalculator.Card(Card("10:30",
            [S("10:30", C.NotStarted, member: null), S("11:30", C.AgentWorking, member: "Bo"), S("11:45", C.Blocked, member: "Bo"),
             S("12:30", C.WasDone, done: true, member: "Bo")],
            runs: [new RunSpan("Bo", At("11:30"), At("11:30"), At("11:45"))], stalls: [Span("11:30", "11:45")], id: 2, member: "Bo"), now);
        var w = TimelineCalculator.Deliverable(new DeliverableTimelineInput(7, At("09:50"),
            [new DeliverableEnd(At("12:40"), "completed", At("13:00")), new DeliverableEnd(At("13:30:00"), "completed")], [a, b], []), now);

        Assert.Equal([(C.Idle, "09:50", "10:00"), (C.AgentWorking, "10:00", "11:00"), (C.WaitingOnYou, "11:00", "12:00"), (C.Blocked, "12:00", "12:30"),
            (C.Idle, "12:30", "12:40"), (C.WasDone, "12:40", "13:00"), (C.Idle, "13:00", "13:30")],
            w.Summary.Select(s => (s.Category, s.Start.ToString("HH:mm", CultureInfo.InvariantCulture), s.End.ToString("HH:mm", CultureInfo.InvariantCulture))));
        Assert.Equal(Min(220), w.Time.OverallMs);
        AddsUp(w.Time);
        Assert.Equal("completed", w.Time.EndedHow);
        Assert.Contains(w.Markers, m => m.Kind == TimeMarkers.Reopen && m.At == At("13:00") && m.Card is null);
        Assert.Equal([1L, 2L], w.Rows.Select(r => r.Card!.Value));
        // B was Stalled 11:30-11:45 while A waited on the person: Waiting on you is higher.
        Assert.Equal(0, Share(w.Time, C.Stalled));
    }

    [Fact]
    public void ACardlessRun_TheManagersOwn_CountsTowardTheDeliverable()
    {
        var now = At("11:00");
        var w = TimelineCalculator.Deliverable(new DeliverableTimelineInput(9, At("10:00"), [], [],
            [new LooseInput("Manager", [new RunSpan("Manager", At("10:00"), At("10:02"), At("10:40"))], [Span("10:20", "10:40")], [],
                [Span("10:40", "10:50")], [], [])]), now);
        Assert.True(w.Time.Open);
        Assert.Equal(Min(2), Share(w.Time, C.WaitingForSlot));
        Assert.Equal(Min(18), Share(w.Time, C.AgentWorking));
        Assert.Equal(Min(20), Share(w.Time, C.Stalled));
        Assert.Equal(Min(10), Share(w.Time, C.WaitingOnYou));
        Assert.Equal(Min(10), Share(w.Time, C.Idle));
        AddsUp(w.Time);
        var row = Assert.Single(w.Rows);
        Assert.Equal(("Manager", (long?)null), (row.Member, row.Card));
    }

    [Fact]
    public void AnExpiredDeliverableEndsAtItsLastActivity()
    {
        var w = TimelineCalculator.Deliverable(new DeliverableTimelineInput(3, At("10:00", 14), [new DeliverableEnd(At("12:00", 14), "expired", null, At("12:00", 17))], [], []), At("13:00", 17));
        Assert.False(w.Time.Open);
        Assert.Equal("expired", w.Time.EndedHow);
        Assert.Equal(Min(120), w.Time.OverallMs);
        Assert.Contains(w.Markers, m => m.Kind == TimeMarkers.Expired && m.At == At("12:00", 17));
    }

    internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
        public void Set(DateTimeOffset to) => _now = to;
    }
}
