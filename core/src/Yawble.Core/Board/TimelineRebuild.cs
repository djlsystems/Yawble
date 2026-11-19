using System.Text.RegularExpressions;

namespace Yawble.Core.Board;

/// <summary>
/// Fills in the timeline of a card made before 0047 from its board_trail lines (B086), replaying them the way
/// <see cref="BoardProjector"/> moved the card: planned, assigned, told, progress, blocked, asked, failed, run ended, handed
/// back, moved, edited, and the workflow's end (done, closed, expired). A run the idle watchdog ended ("failed" with
/// "idle: no progress for N s") was Stalled from the line before it (its last sign of activity) to its end, and is a
/// watchdog-end marker.
/// </summary>
public static partial class TimelineRebuild
{
    /// <summary>A line to write (the store marks it rebuilt).</summary>
    public sealed record Line(string Kind, string At, string? Until, string? Category, bool Done, string? Marker, string? Member);

    public sealed record Result(IReadOnlyList<Line> Lines, string? StartedAt);

    [GeneratedRegex(@"^\s*idle: no progress for (\d+)\s*s", RegexOptions.IgnoreCase)]
    private static partial Regex IdleReason();

    [GeneratedRegex(@"^Assigned to ([^:]+):")]
    private static partial Regex AssignedTo();

    [GeneratedRegex(@"(?:^Edited: |, )status ([a-z-]+)")]
    private static partial Regex EditedStatus();

    /// <summary>The idle watchdog's reason ("idle: no progress for 1800 s"): its N seconds; null for any other reason.</summary>
    public static int? IdleSeconds(string? reason) =>
        reason is not null && IdleReason().Match(reason) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;

    /// <summary>
    /// Where the Stalled band of a run the watchdog ended starts: at its last sign of activity (the line before the end)
    /// when there is one, else N seconds before the end.
    /// </summary>
    public static string StallStart(string? lastActivity, string endedAt, int idleSeconds)
    {
        if (lastActivity is not null && BoardStore.Time(lastActivity) < BoardStore.Time(endedAt)) return lastActivity;
        return TimelineCalculator.Iso(BoardStore.Time(endedAt) - TimeSpan.FromSeconds(idleSeconds));
    }

    public static Result FromTrail(CardRow card, IReadOnlyList<TrailRow> trail)
    {
        var lines = new List<Line>();
        var bornPlanned = trail.Count > 0 ? trail[0].Kind is "planned" : card.Member is null;
        string? member = bornPlanned ? null : card.Member;
        string status = "queued", lane = Lanes.Todo;
        var placed = false;
        string? started = null;
        Line? last = null;

        void State(string at)
        {
            var category = TimeCategories.OfState(member, status, lane);
            var done = lane == Lanes.Done;
            if (member is not null && status == "queued") started ??= at;
            if (last is not null && last.Category == category && last.Done == done && string.Equals(last.Member, member, StringComparison.OrdinalIgnoreCase)) return;
            last = new Line("state", at, null, category, done, null, member);
            lines.Add(last);
        }

        var laneBeforeReport = lane;
        void Report(string next, bool giveUp)
        {
            laneBeforeReport = lane;
            if (Lanes.Loudness(next) >= Lanes.Loudness(status)) status = next;
            lane = placed && !giveUp ? lane : Lanes.Of(status);
        }

        State(card.CreatedAt);
        string? previous = null;
        foreach (var t in trail)
        {
            var stateAt = t.At;
            switch (t.Kind)
            {
                case "planned":
                    member = null;
                    break;
                case "assigned":
                    member = AssignedTo().Match(t.Text) is { Success: true } m ? m.Groups[1].Value.Trim() : card.Member;
                    status = "queued";
                    lane = placed ? lane : Lanes.Todo;
                    break;
                case "told":
                    if (t.Source == "you" && status == "needs-decision") lines.Add(Mark(TimeMarkers.DecisionAnswered, t.At, member));
                    member ??= card.Member;
                    status = "queued";
                    lane = placed && lane != Lanes.Done ? lane : Lanes.Todo;
                    break;
                case "progress":
                    Report("running", giveUp: false);
                    break;
                case "blocked":
                    Report("blocked", giveUp: true);
                    break;
                case "asked":
                    Report("needs-decision", giveUp: true);
                    lines.Add(Mark(TimeMarkers.DecisionAsked, t.At, member));
                    break;
                case "failed":
                    Report("failed", giveUp: true);
                    if (IdleSeconds(t.Text) is { } n)
                    {
                        lines.Add(new Line("stall", StallStart(previous, t.At, n), t.At, null, false, null, member));
                        lines.Add(Mark(TimeMarkers.WatchdogEnd, t.At, member));
                    }
                    break;
                case "stopped":
                    // B097: a run the person stopped is not a failure: the card says "stopped" (Idle time) in its lane,
                    // unless it is in Done or louder (as BoardProjector).
                    if (!Lanes.StopKeeps(status, lane)) status = Lanes.Stopped;
                    break;
                case "run ended":
                    Report("completed", giveUp: false);
                    break;
                case "handed back":
                    Report("handback", giveUp: false);
                    lines.Add(Mark(TimeMarkers.Handback, t.At, member));
                    break;
                case "comment" when t.Source == BoardProjector.BoardSource && t.Text.StartsWith("Not Done yet", StringComparison.Ordinal):
                    // B027: someone else's hand-back did not put a tester's card in Done.
                    lane = laneBeforeReport == Lanes.Todo ? Lanes.InProgress : laneBeforeReport;
                    break;
                case "moved":
                    var to = Lanes.All.FirstOrDefault(l => t.Text.StartsWith("Moved to " + l.Label, StringComparison.Ordinal));
                    if (to.Id is not null)
                    {
                        lane = to.Id;
                        placed = true;
                    }
                    break;
                case "edited":
                    if (EditedStatus().Match(t.Text) is { Success: true } e && Lanes.Statuses.Contains(e.Groups[1].Value))
                    {
                        status = e.Groups[1].Value;
                        lane = Lanes.Of(status);
                        placed = false;
                    }
                    break;
                case "done" or "workflow done" or "workflow closed":
                    status = "done";
                    lane = Lanes.Done;
                    break;
                case "workflow expired":
                    status = "done";
                    lane = Lanes.Done;
                    // B085: an expired workflow's cards end at its last activity, not at the expiry.
                    if (card.EndedHow == "expired" && card.EndedAt is { } endedAt && BoardStore.Time(endedAt) <= BoardStore.Time(t.At)) stateAt = endedAt;
                    lines.Add(Mark(TimeMarkers.Expired, t.At, member));
                    break;
            }
            State(stateAt);
            previous = t.At;
        }
        return new Result(lines, started);
    }

    private static Line Mark(string marker, string at, string? member) => new("marker", at, null, null, false, marker, member);
}
