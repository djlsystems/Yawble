using System.Globalization;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;

namespace Yawble.Core.Board;

/// <summary>
/// B086's time categories. Every moment of a card or a workflow is in exactly one, so they add up to its overall time.
/// For a workflow, a moment takes the highest category among its open cards (and its card-less runs), in
/// <see cref="ByPriority"/> order.
/// </summary>
public static class TimeCategories
{
    public const string AgentWorking = "agent-working", Stalled = "stalled", WaitingOnYou = "waiting-on-you",
        WaitingForSlot = "waiting-for-slot", WaitingOnTeammate = "waiting-on-teammate", Blocked = "blocked",
        Scheduled = "scheduled", NotStarted = "not-started", Idle = "idle", WasDone = "was-done";

    /// <summary>Highest first: Agent working > Waiting on you > Stalled > Waiting for a slot > Waiting on a teammate > Blocked > Scheduled > Not started > Idle > Was done.</summary>
    public static readonly IReadOnlyList<string> ByPriority =
        [AgentWorking, WaitingOnYou, Stalled, WaitingForSlot, WaitingOnTeammate, Blocked, Scheduled, NotStarted, Idle, WasDone];

    /// <summary>How long an agent run may show no sign of activity before that time is Stalled (the idle watchdog's signal).</summary>
    public static TimeSpan StallThreshold => RunActivitySpans.StallAfter;

    public static int Rank(string category)
    {
        for (var i = 0; i < ByPriority.Count; i++) if (ByPriority[i] == category) return i;
        return ByPriority.Count;
    }

    /// <summary>
    /// A card's category from its stored state: Done is Was done (it counts only if the card is reopened); no member is
    /// Not started; then by status. Whether a run is really working it (Agent working, Stalled, Waiting for a slot) comes
    /// from its runs at read time (<see cref="TimelineCalculator"/>).
    /// </summary>
    public static string OfState(string? member, string status, string lane) =>
        lane == Lanes.Done ? WasDone
        : member is null ? NotStarted
        : status switch
        {
            "queued" => WaitingForSlot,
            "running" => AgentWorking,
            "needs-decision" => WaitingOnYou,
            "blocked" or "failed" => Blocked,
            "handback" or "completed" => WaitingOnTeammate,
            // B097 (card 437): the person stopped the run; nothing works the card and nothing blocks it until new work.
            Lanes.Stopped => Idle,
            _ => Idle,
        };
}

/// <summary>The moments a timeline marks on its axis.</summary>
public static class TimeMarkers
{
    public const string Planned = "planned", Started = "started", Reopen = "reopen", Handback = "handback",
        DecisionAsked = "decision-asked", DecisionAnswered = "decision-answered", WatchdogEnd = "watchdog-end", Expired = "expired";
}

/// <summary>One category's share of an overall time.</summary>
public sealed record TimeShare(string Category, long Ms);

/// <summary>
/// The wire contract's <c>time</c> on a BoardCard and on a workflow item (B086). <see cref="EndedHow"/>: done (a card),
/// completed, closed, stopped or expired; null while open. The breakdown's ms add up to <see cref="OverallMs"/> exactly.
/// </summary>
public sealed record TimeSummary(
    string CreatedAt, string? StartedAt, string? EndedAt, string? EndedHow, bool Open, long OverallMs, bool Rebuilt,
    IReadOnlyList<TimeShare> Breakdown)
{
    public JsonObject ToJson() => new()
    {
        ["createdAt"] = CreatedAt, ["startedAt"] = StartedAt, ["endedAt"] = EndedAt, ["endedHow"] = EndedHow,
        ["open"] = Open, ["overallMs"] = OverallMs, ["rebuilt"] = Rebuilt,
        ["breakdown"] = new JsonArray([.. Breakdown.Select(b => (JsonNode)new JsonObject { ["category"] = b.Category, ["ms"] = b.Ms })]),
    };
}

/// <summary>A stretch of one category.</summary>
public sealed record TimeBand(string Category, DateTimeOffset Start, DateTimeOffset End);

/// <summary>A moment on the axis; <see cref="Card"/> says whose, on a workflow's timeline.</summary>
public sealed record TimeMarker(string Kind, DateTimeOffset At, long? Card = null);

/// <summary>
/// A card's stored state from <see cref="At"/> on. A Scheduled state lasts until <see cref="Until"/> (the wake it asked
/// for); after that the card is Done again when <see cref="Done"/>, else Idle.
/// </summary>
public sealed record TimelineState(DateTimeOffset At, string Category, bool Done, string? Member = null, DateTimeOffset? Until = null);

/// <summary>One agent run: queued, then started, then ended (null while it runs).</summary>
public sealed record RunSpan(string Member, DateTimeOffset Queued, DateTimeOffset Started, DateTimeOffset? Ended);

/// <summary>What a card's timeline is computed from.</summary>
public sealed record CardTimelineInput(
    long Id, string? Member, string Title, DateTimeOffset Created, DateTimeOffset? Started, string? EndedHow, DateTimeOffset? EndedAt, bool Rebuilt,
    IReadOnlyList<TimelineState> States, IReadOnlyList<ActivitySpan> Stalls, IReadOnlyList<ActivitySpan> Holds, IReadOnlyList<RunSpan> Runs,
    IReadOnlyList<TimeMarker> Markers);

/// <summary>
/// A card's timeline: its time, its bands from created to its end (or now), and its markers. <see cref="Live"/>: the
/// stretches it was not in Done, created to now, which a workflow merges.
/// </summary>
public sealed record CardTimeline(long Id, string? Member, string Title, TimeSummary Time, IReadOnlyList<TimeBand> Bands,
    IReadOnlyList<TimeMarker> Markers, IReadOnlyList<TimeBand> Live);

/// <summary>A member's work in a workflow that is on none of its cards (the Manager's own runs, its questions).</summary>
public sealed record LooseInput(string Member, IReadOnlyList<RunSpan> Runs, IReadOnlyList<ActivitySpan> Stalls, IReadOnlyList<ActivitySpan> Holds,
    IReadOnlyList<ActivitySpan> Asks, IReadOnlyList<ActivitySpan> Wakes, IReadOnlyList<TimeMarker> Markers);

/// <summary>One end of a workflow: when (an expired one's last activity), how, and when new work reopened it (null: it still is ended).</summary>
public sealed record WorkflowEnd(DateTimeOffset EndAt, string How, DateTimeOffset? ReopenAt = null, DateTimeOffset? ExpiredAt = null);

public sealed record WorkflowTimelineInput(long Id, DateTimeOffset Created, IReadOnlyList<WorkflowEnd> Ends, IReadOnlyList<CardTimeline> Cards, IReadOnlyList<LooseInput> Loose);

/// <summary>A row of a workflow's timeline: a card (with its member and title), or a member's card-less work.</summary>
public sealed record TimelineRow(long? Card, string? Member, string? Title, IReadOnlyList<TimeBand> Bands);

public sealed record WorkflowTimeline(long Id, TimeSummary Time, IReadOnlyList<TimelineRow> Rows, IReadOnlyList<TimeBand> Summary, IReadOnlyList<TimeMarker> Markers);

/// <summary>
/// Computes B086's times. Pure: every input, "now" included, is given, so tests use fixed times only.
/// <list type="bullet">
/// <item>A card: created (planned) to the last time it entered Done, or now while it is not in Done. Its stored states
///   give the category; while a run of its member is active the time is Agent working, except the run's stalls (Stalled)
///   and its waits for the heavy-work lease (Waiting for a slot); queued for a run slot is Waiting for a slot. Time in Done
///   before a reopen is Was done.</item>
/// <item>A workflow: created (its first message) to its last end (completed, closed, expired: an expired one's last
///   activity), or now while open. Each moment takes the highest category of its cards not in Done and of its card-less
///   runs; nothing at all is Idle; time between an end and the work that reopened it is Was done.</item>
/// </list>
/// </summary>
public static class TimelineCalculator
{
    private readonly record struct Seg(string Category, bool Done, long Start, long End);

    public static CardTimeline Card(CardTimelineInput c, DateTimeOffset now)
    {
        long created = Ms(c.Created), last = Math.Max(created, Ms(now));
        var states = c.States.OrderBy(s => s.At).ToList(); // stable: same-time states keep their order
        var cuts = new SortedSet<long> { created, last };
        void Cut(DateTimeOffset? t)
        {
            if (t is { } x && Ms(x) > created && Ms(x) < last) cuts.Add(Ms(x));
        }
        foreach (var s in states) { Cut(s.At); Cut(s.Until); }
        foreach (var r in c.Runs) { Cut(r.Queued); Cut(r.Started); Cut(r.Ended); }
        foreach (var s in c.Stalls.Concat(c.Holds)) { Cut(s.Start); Cut(s.End); }

        var points = cuts.ToList();
        var segs = new List<Seg>();
        var si = -1;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            long a = points[i], b = points[i + 1];
            while (si + 1 < states.Count && Ms(states[si + 1].At) <= a) si++;
            var st = si >= 0 ? states[si] : null;
            var (category, done) = Base(st, a, c.Member);
            if (!done && category is not (TimeCategories.WaitingOnYou or TimeCategories.NotStarted))
            {
                var member = st?.Member ?? c.Member;
                var runs = c.Runs.Where(r => member is null || string.Equals(r.Member, member, StringComparison.OrdinalIgnoreCase)).ToList();
                if (In(c.Holds, a)) category = TimeCategories.WaitingForSlot;
                else if (In(c.Stalls, a)) category = TimeCategories.Stalled;
                else if (runs.Any(r => Ms(r.Started) <= a && a < (r.Ended is { } e ? Ms(e) : last))) category = TimeCategories.AgentWorking;
                else if (runs.Any(r => Ms(r.Queued) <= a && a < Ms(r.Started))) category = TimeCategories.WaitingForSlot;
            }
            Add(segs, new Seg(category, done, a, b));
        }

        bool open;
        long end;
        if (segs.Count == 0)
        {
            open = !(states.LastOrDefault()?.Done ?? false);
            end = created;
        }
        else
        {
            open = !segs[^1].Done;
            end = last;
            if (!open)
            {
                var k = segs.Count - 1;
                while (k > 0 && segs[k - 1].Done) k--;
                end = segs[k].Start; // the last time it entered Done
                // A reopen that lasted no time (told again at the very moment an expired workflow's last activity ended it)
                // still moves the end to that moment: the time before it in Done was done before a reopen.
                for (var i = 1; i < states.Count; i++)
                {
                    var at = Ms(states[i].At);
                    if (at > end && at <= last && IsDone(states[i]) && !IsDone(states[i - 1])) end = at;
                }
            }
        }

        var bands = Clip(segs, created, end);
        var markers = new List<TimeMarker> { new(TimeMarkers.Planned, c.Created, c.Id) };
        if (c.Started is { } started) markers.Add(new(TimeMarkers.Started, started, c.Id));
        for (var i = 1; i < segs.Count; i++)
            if (segs[i - 1].Done && !segs[i].Done) markers.Add(new(TimeMarkers.Reopen, At(segs[i].Start), c.Id));
        markers.AddRange(c.Markers.Select(m => m with { Card = c.Id }));

        var how = open ? null
            : c.EndedHow is { } h && c.EndedAt is { } endedAt && Math.Abs(Ms(endedAt) - end) <= 1000 ? h : "done";
        var time = new TimeSummary(Iso(c.Created), c.Started is { } s0 ? Iso(s0) : null, open ? null : Iso(At(end)), how, open,
            end - created, c.Rebuilt, Breakdown(bands));
        var live = segs.Where(s => !s.Done).Select(s => new TimeBand(s.Category, At(s.Start), At(s.End))).ToList();
        return new CardTimeline(c.Id, c.Member, c.Title, time, bands, [.. markers.OrderBy(m => m.At)], live);
    }

    /// <summary>A card-less member's bands: its runs (Agent working, Stalled, Waiting for a slot), its questions and its wakes.</summary>
    public static IReadOnlyList<TimeBand> Loose(LooseInput l, DateTimeOffset now)
    {
        var last = Ms(now);
        var cuts = new SortedSet<long>();
        void Cut(DateTimeOffset? t)
        {
            if (t is { } x && Ms(x) <= last) cuts.Add(Ms(x));
        }
        foreach (var r in l.Runs) { Cut(r.Queued); Cut(r.Started); Cut(r.Ended ?? now); }
        foreach (var s in l.Stalls.Concat(l.Holds).Concat(l.Asks).Concat(l.Wakes)) { Cut(s.Start); Cut(s.End); }
        var points = cuts.ToList();
        var segs = new List<Seg>();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            long a = points[i];
            var running = l.Runs.Any(r => Ms(r.Started) <= a && a < (r.Ended is { } e ? Ms(e) : last));
            string? category =
                running && In(l.Holds, a) ? TimeCategories.WaitingForSlot
                : running && In(l.Stalls, a) ? TimeCategories.Stalled
                : running ? TimeCategories.AgentWorking
                : In(l.Asks, a) ? TimeCategories.WaitingOnYou
                : l.Runs.Any(r => Ms(r.Queued) <= a && a < Ms(r.Started)) ? TimeCategories.WaitingForSlot
                : In(l.Wakes, a) ? TimeCategories.Scheduled
                : null;
            if (category is not null) Add(segs, new Seg(category, false, a, points[i + 1]));
        }
        return [.. segs.Select(s => new TimeBand(s.Category, At(s.Start), At(s.End)))];
    }

    public static WorkflowTimeline Workflow(WorkflowTimelineInput w, DateTimeOffset now)
    {
        long created = Ms(w.Created), last = Math.Max(created, Ms(now));
        var final = w.Ends.LastOrDefault();
        var open = final is null || final.ReopenAt is not null;
        var end = open ? last : Math.Clamp(Ms(final!.EndAt), created, last);

        var items = new List<Seg>();
        foreach (var card in w.Cards) items.AddRange(card.Live.Select(b => new Seg(b.Category, false, Ms(b.Start), Ms(b.End))));
        var loose = w.Loose.Select(l => (l, Bands: Loose(l, now))).ToList();
        foreach (var (_, bands) in loose) items.AddRange(bands.Select(b => new Seg(b.Category, false, Ms(b.Start), Ms(b.End))));
        var done = w.Ends.Where(e => e.ReopenAt is not null).Select(e => (Start: Ms(e.EndAt), End: Ms(e.ReopenAt!.Value))).ToList();
        var summary = Merge(items, done, created, end);

        var rows = new List<TimelineRow>();
        foreach (var card in w.Cards)
            rows.Add(new TimelineRow(card.Id, card.Member, card.Title, ClipBands(card.Bands, created, end)));
        foreach (var (l, bands) in loose)
            rows.Add(new TimelineRow(null, l.Member, null, ClipBands(bands, created, end)));

        var markers = w.Cards.SelectMany(c => c.Markers).Concat(w.Loose.SelectMany(l => l.Markers)).ToList();
        foreach (var e in w.Ends)
        {
            if (e.How == "expired") markers.Add(new(TimeMarkers.Expired, e.ExpiredAt ?? e.EndAt));
            if (e.ReopenAt is { } r) markers.Add(new(TimeMarkers.Reopen, r));
        }
        var startedAt = w.Cards.Select(c => c.Time.StartedAt).Where(s => s is not null).Min(StringComparer.Ordinal);
        var time = new TimeSummary(Iso(w.Created), startedAt, open ? null : Iso(At(end)), open ? null : final!.How, open,
            end - created, w.Cards.Any(c => c.Time.Rebuilt), Breakdown(summary));
        return new WorkflowTimeline(w.Id, time, rows, summary, [.. markers.OrderBy(m => m.At)]);
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsDone(TimelineState s) => s.Done && s.Category != TimeCategories.Scheduled;

    private static (string Category, bool Done) Base(TimelineState? st, long at, string? member)
    {
        if (st is null) return (member is null ? TimeCategories.NotStarted : TimeCategories.WaitingForSlot, false);
        if (st.Category == TimeCategories.Scheduled)
        {
            if (st.Until is { } u && at >= Ms(u)) return st.Done ? (TimeCategories.WasDone, true) : (TimeCategories.Idle, false);
            return (TimeCategories.Scheduled, false);
        }
        return st.Done ? (TimeCategories.WasDone, true) : (st.Category, false);
    }

    /// <summary>Each moment from <paramref name="from"/> to <paramref name="to"/>: Was done inside <paramref name="done"/>, else the highest item there, else Idle.</summary>
    private static IReadOnlyList<TimeBand> Merge(IReadOnlyList<Seg> items, IReadOnlyList<(long Start, long End)> done, long from, long to)
    {
        var cuts = new SortedSet<long> { from, to };
        foreach (var s in items) { if (s.Start > from && s.Start < to) cuts.Add(s.Start); if (s.End > from && s.End < to) cuts.Add(s.End); }
        foreach (var d in done) { if (d.Start > from && d.Start < to) cuts.Add(d.Start); if (d.End > from && d.End < to) cuts.Add(d.End); }
        var points = cuts.ToList();
        var segs = new List<Seg>();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            string category;
            if (done.Any(d => d.Start <= a && a < d.End)) category = TimeCategories.WasDone;
            else
            {
                category = TimeCategories.Idle;
                var best = TimeCategories.Rank(category);
                foreach (var s in items)
                    if (s.Start <= a && a < s.End && TimeCategories.Rank(s.Category) < best) (category, best) = (s.Category, TimeCategories.Rank(s.Category));
            }
            Add(segs, new Seg(category, false, a, points[i + 1]));
        }
        return [.. segs.Select(s => new TimeBand(s.Category, At(s.Start), At(s.End)))];
    }

    private static void Add(List<Seg> segs, Seg s)
    {
        if (s.End <= s.Start) return;
        if (segs.Count > 0 && segs[^1].Category == s.Category && segs[^1].Done == s.Done && segs[^1].End == s.Start)
            segs[^1] = segs[^1] with { End = s.End };
        else segs.Add(s);
    }

    private static List<TimeBand> Clip(IEnumerable<Seg> segs, long from, long to) =>
        [.. segs.Where(s => s.End > from && s.Start < to).Select(s => new TimeBand(s.Category, At(Math.Max(s.Start, from)), At(Math.Min(s.End, to))))];

    private static List<TimeBand> ClipBands(IEnumerable<TimeBand> bands, long from, long to) =>
        Clip(bands.Select(b => new Seg(b.Category, false, Ms(b.Start), Ms(b.End))), from, to);

    private static IReadOnlyList<TimeShare> Breakdown(IEnumerable<TimeBand> bands)
    {
        var sums = new Dictionary<string, long>();
        foreach (var b in bands) sums[b.Category] = sums.GetValueOrDefault(b.Category) + (Ms(b.End) - Ms(b.Start));
        return [.. TimeCategories.ByPriority.Where(c => sums.GetValueOrDefault(c) > 0).Select(c => new TimeShare(c, sums[c]))];
    }

    private static bool In(IReadOnlyList<ActivitySpan> spans, long at)
    {
        foreach (var s in spans) if (Ms(s.Start) <= at && at < Ms(s.End)) return true;
        return false;
    }

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    private static DateTimeOffset At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    public static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
