using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Link;
using Xunit;
using C = Yawble.Core.Board.TimeCategories;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// The tester's own checks of the timeline beside the developer's: seeded random deliverables whose cards overlap
/// (the priority merge is the highest open card per moment and everything adds up exactly, to the millisecond), a
/// Visitor may read timeline.get, and no token or cost field is on the wire. Fixed times only.
/// </summary>
public class TimelineVerifyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    // Every category a stored state can have (Scheduled needs an Until and is covered by the developer's tests).
    private static readonly string[] States =
        [C.AgentWorking, C.WaitingOnYou, C.WaitingForSlot, C.WaitingOnTeammate, C.Blocked, C.NotStarted, C.Idle];

    private static CardTimeline RandomCard(Random r, long id, DateTimeOffset now)
    {
        var created = T0.AddSeconds(r.Next(0, 3 * 3600)).AddMilliseconds(r.Next(0, 1000));
        var states = new List<TimelineState>();
        var at = created;
        var n = r.Next(1, 7);
        for (var i = 0; i < n && at < now; i++)
        {
            var done = r.Next(0, 5) == 0;
            states.Add(new TimelineState(at, done ? C.WasDone : States[r.Next(States.Length)], done, "Ada"));
            at = at.AddSeconds(r.Next(30, 5400)).AddMilliseconds(r.Next(0, 1000));
        }
        var stalls = new List<ActivitySpan>();
        if (r.Next(0, 2) == 0)
        {
            var s = created.AddSeconds(r.Next(0, 3600));
            stalls.Add(new ActivitySpan(s, s.AddSeconds(r.Next(600, 3000))));
        }
        var runs = stalls.Count == 0 ? new List<RunSpan>() : [new RunSpan("Ada", stalls[0].Start.AddMinutes(-5), stalls[0].Start.AddMinutes(-4), stalls[0].End)];
        return TimelineCalculator.Card(new CardTimelineInput(id, "Ada", $"card {id}", created, created, null, null, false,
            states, stalls, [], runs, []), now);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void ARandomDeliverableOfOverlappingCards_MergesByPriority_AndAddsUpExactly(int seed)
    {
        var r = new Random(seed);
        var now = T0.AddHours(9).AddMilliseconds(r.Next(0, 1000));
        var cards = Enumerable.Range(1, r.Next(2, 6)).Select(i => RandomCard(r, i, now)).ToList();
        var created = T0.AddMilliseconds(-r.Next(0, 60_000));
        var w = TimelineCalculator.Deliverable(new DeliverableTimelineInput(431, created, [], cards, []), now);

        // Each card: its breakdown adds up to its overall exactly.
        foreach (var c in cards) Assert.Equal(c.Time.OverallMs, c.Time.Breakdown.Sum(b => b.Ms));

        // The deliverable: open, created -> now, its breakdown adds up exactly.
        Assert.True(w.Time.Open);
        Assert.Equal((long)(now - created).TotalMilliseconds, w.Time.OverallMs);
        Assert.Equal(w.Time.OverallMs, w.Time.Breakdown.Sum(b => b.Ms));
        Assert.All(w.Time.Breakdown, b => Assert.True(b.Ms > 0, $"{b.Category} has {b.Ms} ms"));
        Assert.Equal(w.Time.Breakdown.Count, w.Time.Breakdown.Select(b => b.Category).Distinct().Count());

        // The summary row covers created -> now with no gap and no overlap, and its bands add up to the breakdown.
        Assert.Equal(created, w.Summary[0].Start);
        Assert.Equal(now, w.Summary[^1].End);
        for (var i = 1; i < w.Summary.Count; i++) Assert.Equal(w.Summary[i - 1].End, w.Summary[i].Start);
        foreach (var share in w.Time.Breakdown)
            Assert.Equal(share.Ms, w.Summary.Where(b => b.Category == share.Category).Sum(b => (long)(b.End - b.Start).TotalMilliseconds));

        // Each summary band is the highest category of the cards' open (not Done) stretches at that moment; none is Idle.
        foreach (var band in w.Summary)
        {
            var mid = band.Start + (band.End - band.Start) / 2;
            var here = cards.SelectMany(c => c.Live).Where(b => b.Start <= mid && b.End > mid).Select(b => b.Category).ToList();
            var expected = C.ByPriority.FirstOrDefault(here.Contains) ?? C.Idle;
            Assert.Equal(expected, band.Category);
        }
        Assert.Equal(cards.Select(c => (long?)c.Id), w.Rows.Select(row => row.Card));
    }

    [Fact]
    public void TheMergeOrderIsTheSpecs()
    {
        Assert.Equal([C.AgentWorking, C.WaitingOnYou, C.Stalled, C.WaitingForSlot, C.WaitingOnTeammate, C.Blocked, C.Scheduled,
            C.NotStarted, C.Idle, C.WasDone], C.ByPriority);
    }

    [Theory]
    [InlineData("{\"card\":375}")]
    [InlineData("{\"deliverable\":3573}")]
    public void AVisitorMayReadTimelineGet(string parameters)
    {
        var p = JsonNode.Parse(parameters)!.AsObject();
        Assert.True(RemoteAccess.Reads("timeline.get", p));
        Assert.Null(RemoteAccess.Refusal("timeline.get", p, false, RemoteAccess.Read));
        Assert.Null(RemoteAccess.Refusal("timeline.get", p, false, RemoteAccess.Full));
        // and a Visitor still cannot change the board
        Assert.NotNull(RemoteAccess.Refusal("board.move", JsonNode.Parse("{\"card\":375,\"lane\":\"done\"}")!.AsObject(), false, RemoteAccess.Read));
    }

    [Fact]
    public void NoTokenOrCostFieldIsOnTheWire()
    {
        var r = new Random(431);
        var now = T0.AddHours(9);
        var cards = Enumerable.Range(1, 3).Select(i => RandomCard(r, i, now)).ToList();
        var w = TimelineCalculator.Deliverable(new DeliverableTimelineInput(431, T0, [], cards, []), now);
        foreach (var json in new[] { BoardTimeline.Json(cards[0]).ToJsonString(), BoardTimeline.Json(w).ToJsonString() })
        {
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cost", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("usd", json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
