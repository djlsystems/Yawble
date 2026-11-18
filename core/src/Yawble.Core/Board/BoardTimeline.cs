using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;

namespace Yawble.Core.Board;

/// <summary>
/// B086's times from what is stored: a card's timeline lines (0047), the workflow's runs and its messages, and the runs
/// working now (their stalls so far). <see cref="TimelineCalculator"/> does the arithmetic; this reads its inputs and
/// writes the wire contract (<c>time</c> on BoardCard and workflow items; <c>timeline.get</c>).
/// </summary>
public sealed class BoardTimeline(BoardStore store)
{
    /// <summary>A card's timeline as of <paramref name="now"/>. <paramref name="runs"/>: the runs of its workflow, when already read.</summary>
    public CardTimeline OfCard(CardRow card, IReadOnlyList<LiveRunActivity> live, DateTimeOffset now, IReadOnlyList<RunSpan>? runs = null)
    {
        var lines = store.Timeline(card.Id); // fills in an old card's timeline from its trail first
        var fresh = store.Card(card.Id) ?? card;
        var states = new List<TimelineState>();
        var stalls = new List<ActivitySpan>();
        var holds = new List<ActivitySpan>();
        var markers = new List<TimeMarker>();
        foreach (var l in lines)
        {
            switch (l.Kind)
            {
                case "state" when l.Category is not null:
                    states.Add(new TimelineState(T(l.At), l.Category, l.Done, l.Member, l.Until is { } u ? T(u) : null));
                    break;
                case "stall" when l.Until is not null:
                    stalls.Add(new ActivitySpan(T(l.At), T(l.Until)));
                    break;
                case "hold" when l.Until is not null:
                    holds.Add(new ActivitySpan(T(l.At), T(l.Until)));
                    break;
                case "marker" when l.Marker is not null:
                    markers.Add(new TimeMarker(l.Marker, T(l.At)));
                    break;
            }
        }
        if (fresh.Workflow is { } w && fresh.Member is { } member)
            foreach (var a in live.Where(a => a.Workflow == w && Same(a.Member, member) && Same(a.Team, fresh.Team)))
            {
                stalls.AddRange(a.Spans.Stalls);
                holds.AddRange(a.Spans.Holds);
            }
        var workflowRuns = runs ?? (fresh.Workflow is { } wf ? store.RunsOf(wf) : []);
        var input = new CardTimelineInput(fresh.Id, fresh.Member, fresh.Title, T(fresh.CreatedAt), fresh.StartedAt is { } s ? T(s) : null,
            fresh.EndedHow, fresh.EndedAt is { } e ? T(e) : null, fresh.Rebuilt, states, stalls, holds,
            fresh.Member is null ? [] : workflowRuns, markers);
        return TimelineCalculator.Card(input, now);
    }

    /// <summary>A workflow's timeline from its messages (oldest first); null when it has none.</summary>
    public WorkflowTimeline? OfWorkflow(long workflow, IReadOnlyList<Message> thread, IReadOnlyList<LiveRunActivity> live, DateTimeOffset now)
    {
        if (thread.Count == 0) return null;
        var runs = store.RunsOf(workflow);
        var cards = store.CardsOfWorkflow(workflow).OrderBy(c => c.Id).Select(c => OfCard(c, live, now, runs)).ToList();
        var ends = Ends(thread);
        var withCards = cards.Where(c => c.Member is not null).Select(c => c.Member!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var looseLines = store.LooseTimeline(workflow);
        var names = runs.Select(r => r.Member)
            .Concat(thread.Where(m => m.Type == MessageTypes.NeedsDecision).Select(m => MemberOf(m.Source)).OfType<string>())
            .Concat(looseLines.Select(l => l.Member).OfType<string>())
            .Where(n => !withCards.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var loose = names.Select(n => Loose(n, workflow, thread, runs, looseLines, live, now)).ToList();
        return TimelineCalculator.Workflow(new WorkflowTimelineInput(workflow, thread[0].OccurredAt, ends, cards, loose), now);
    }

    /// <summary>
    /// The workflow's ends in order: completed, closed or expired (an expired one ends at its last activity), each until
    /// the instruction that reopened it. An end while it is ended already changes nothing.
    /// </summary>
    public static IReadOnlyList<WorkflowEnd> Ends(IReadOnlyList<Message> thread)
    {
        var ends = new List<WorkflowEnd>();
        foreach (var m in thread)
        {
            var ended = ends.Count > 0 && ends[^1].ReopenAt is null;
            if (MessageTypes.WorkflowEnds.Contains(m.Type))
            {
                if (ended) continue;
                var how = m.Type switch { MessageTypes.WorkflowClosed => "closed", MessageTypes.WorkflowExpired => "expired", _ => "completed" };
                var p = Payload(m);
                var at = how == "expired" && Str(p, "endedAt") is { } last ? T(last) : m.OccurredAt;
                ends.Add(new WorkflowEnd(at, how, null, how == "expired" ? (Str(p, "expiredAt") is { } x ? T(x) : m.OccurredAt) : null));
            }
            else if (ended && m.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
                ends[^1] = ends[^1] with { ReopenAt = m.OccurredAt };
        }
        return ends;
    }

    private static LooseInput Loose(string member, long workflow, IReadOnlyList<Message> thread, IReadOnlyList<RunSpan> runs,
        IReadOnlyList<TimelineLine> lines, IReadOnlyList<LiveRunActivity> live, DateTimeOffset now)
    {
        var mine = lines.Where(l => Same(l.Member, member)).ToList();
        var stalls = mine.Where(l => l.Kind == "stall" && l.Until is not null).Select(l => new ActivitySpan(T(l.At), T(l.Until!))).ToList();
        var holds = mine.Where(l => l.Kind == "hold" && l.Until is not null).Select(l => new ActivitySpan(T(l.At), T(l.Until!))).ToList();
        foreach (var a in live.Where(a => a.Workflow == workflow && Same(a.Member, member)))
        {
            stalls.AddRange(a.Spans.Stalls);
            holds.AddRange(a.Spans.Holds);
        }
        var markers = mine.Where(l => l.Kind == "marker" && l.Marker is not null).Select(l => new TimeMarker(l.Marker!, T(l.At))).ToList();
        var asks = new List<ActivitySpan>();
        foreach (var q in thread.Where(m => m.Type == MessageTypes.NeedsDecision && Same(MemberOf(m.Source), member)))
        {
            // Answered: a reply to it, or anything the person told the asker later (as BusStore.OpenDecisions reads it).
            var answer = thread.FirstOrDefault(a => a.Source == MessageTypes.PersonSource
                && (a.CausationSeq == q.Seq || (a.Seq > q.Seq && a.Type == MessageTypes.InstructionPrefix + q.Source)));
            asks.Add(new ActivitySpan(q.OccurredAt, answer?.OccurredAt ?? now));
            if (answer is not null) markers.Add(new TimeMarker(TimeMarkers.DecisionAnswered, answer.OccurredAt));
        }
        var wakes = new List<ActivitySpan>();
        foreach (var c in thread.Where(m => m.Type == MessageTypes.Completed && Same(MemberOf(m.Source), member)))
            if (Payload(c)?["wake"]?["at"] is JsonValue v && v.TryGetValue<string>(out var text)
                && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at) && at > c.OccurredAt)
                wakes.Add(new ActivitySpan(c.OccurredAt, at));
        return new LooseInput(member, [.. runs.Where(r => Same(r.Member, member))], stalls, holds, asks, wakes, markers);
    }

    // ---------------------------------------------------------------- the wire

    /// <summary>timeline.get for a card: its time, its one row, its markers.</summary>
    public static JsonObject Json(CardTimeline c) => new()
    {
        ["time"] = c.Time.ToJson(),
        ["rows"] = new JsonArray(Row(c.Id, c.Member, c.Title, c.Bands)),
        ["markers"] = Markers(c.Markers),
    };

    /// <summary>timeline.get for a workflow: its time, a row per card (and per card-less member), the summary row, its markers.</summary>
    public static JsonObject Json(WorkflowTimeline w) => new()
    {
        ["time"] = w.Time.ToJson(),
        ["rows"] = new JsonArray([.. w.Rows.Select(r => Row(r.Card, r.Member, r.Title, r.Bands))]),
        ["summary"] = Bands(w.Summary),
        ["markers"] = Markers(w.Markers),
    };

    private static JsonNode Row(long? card, string? member, string? title, IReadOnlyList<TimeBand> bands)
    {
        var o = new JsonObject();
        if (card is not null) o["card"] = card;
        if (member is not null) o["member"] = member;
        if (title is not null) o["title"] = title;
        o["bands"] = Bands(bands);
        return o;
    }

    private static JsonArray Bands(IReadOnlyList<TimeBand> bands) =>
        new([.. bands.Select(b => (JsonNode)new JsonObject
        {
            ["category"] = b.Category, ["start"] = TimelineCalculator.Iso(b.Start), ["end"] = TimelineCalculator.Iso(b.End),
        })]);

    private static JsonArray Markers(IReadOnlyList<TimeMarker> markers) =>
        new([.. markers.Select(m =>
        {
            var o = new JsonObject { ["kind"] = m.Kind, ["at"] = TimelineCalculator.Iso(m.At) };
            if (m.Card is not null) o["card"] = m.Card;
            return (JsonNode)o;
        })]);

    /// <summary>"1h 12m 05s": the overall time as the Concierge says it.</summary>
    public static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        var h = (long)t.TotalHours;
        return h > 0 ? $"{h}h {t.Minutes:00}m {t.Seconds:00}s" : t.Minutes > 0 ? $"{t.Minutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
    }

    /// <summary>"3h 01m 00s (agent-working 1h 40m 00s, stalled 38m 00s, ...)" for the Concierge's tools.</summary>
    public static string Describe(TimeSummary t) =>
        $"{Duration(t.OverallMs)}{(t.Open ? " so far, open" : $", {t.EndedHow}")}" +
        (t.Breakdown.Count == 0 ? "" : $" ({string.Join(", ", t.Breakdown.Select(b => $"{b.Category} {Duration(b.Ms)}"))})") +
        (t.Rebuilt ? " [rebuilt from history]" : "");

    // ---------------------------------------------------------------- plumbing

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? MemberOf(string source) => source.IndexOf('/') is > 0 and var i ? source[(i + 1)..] : null;

    private static DateTimeOffset T(string iso) => BoardStore.Time(iso);

    private static JsonObject? Payload(Message m)
    {
        try { return JsonNode.Parse(m.Payload) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonObject? p, string name) =>
        p?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
