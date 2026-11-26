using System.Globalization;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;

namespace Yawble.Core.Goals;

/// <summary>A goal: the person's name for what some deliverables are for, and a description.</summary>
public sealed record Goal(long Id, string Name, string Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A deliverable's overall time: from its first message to its end (null while it is open).</summary>
public sealed record DeliverableSpan(long Id, DateTimeOffset Start, DateTimeOffset? End)
{
    public bool Open => End is null;
}

/// <summary>A set of deliverables' time together: its first start, its last end (null while one is open) and the calendar time covered.</summary>
public sealed record GoalTime(DateTimeOffset? FirstAt, DateTimeOffset? EndAt, long ElapsedMs)
{
    public JsonObject ToJson() => new()
    {
        ["firstAt"] = FirstAt?.ToString("O", CultureInfo.InvariantCulture),
        ["endAt"] = EndAt?.ToString("O", CultureInfo.InvariantCulture),
        ["elapsedMs"] = ElapsedMs,
    };

    /// <summary>
    /// The calendar time the spans cover together: each one from its start to its end (or <paramref name="now"/> while open),
    /// overlapping time counted once. Not a sum: two deliverables worked side by side for an hour make one hour.
    /// </summary>
    public static GoalTime Of(IEnumerable<DeliverableSpan> spans, DateTimeOffset now)
    {
        var list = spans.ToList();
        if (list.Count == 0) return new GoalTime(null, null, 0);
        var ranges = list.Select(s => (Start: s.Start, End: s.End ?? (now > s.Start ? now : s.Start)))
            .Select(r => r.End < r.Start ? (r.Start, End: r.Start) : r)
            .OrderBy(r => r.Start).ToList();
        long ms = 0;
        var (curStart, curEnd) = ranges[0];
        foreach (var (start, end) in ranges.Skip(1))
        {
            if (start <= curEnd)
            {
                if (end > curEnd) curEnd = end;
                continue;
            }
            ms += (long)(curEnd - curStart).TotalMilliseconds;
            (curStart, curEnd) = (start, end);
        }
        ms += (long)(curEnd - curStart).TotalMilliseconds;
        var open = list.Any(s => s.Open);
        return new GoalTime(ranges[0].Start, open ? null : list.Max(s => s.End), ms);
    }
}

/// <summary>The rules for a goal's name and description, and their refusals (bad_params, no_such_goal).</summary>
public static class GoalRules
{
    public const int NameMax = 80, DescriptionMax = 2000;

    /// <summary>The name as kept: trimmed, 1 to <see cref="NameMax"/> characters, on one line.</summary>
    public static string Name(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new BusRefusal("bad_params", "a goal needs a name");
        if (n.Length > NameMax) throw new BusRefusal("bad_params", $"a goal's name is at most {NameMax} characters");
        if (n.Any(c => c is '\r' or '\n')) throw new BusRefusal("bad_params", "a goal's name is one line");
        return n;
    }

    /// <summary>
    /// Whether two names are the same goal: ignoring case for every letter (é and É too), as the shell's check does
    /// (toLowerCase). The table's NOCASE only folds ASCII, so this is the rule the core checks with.
    /// </summary>
    public static bool SameName(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.ToLowerInvariant(), b.ToLowerInvariant(), StringComparison.Ordinal);

    /// <summary>The description as kept: trimmed, at most <see cref="DescriptionMax"/> characters (empty is fine).</summary>
    public static string Description(string? description)
    {
        var d = (description ?? "").Trim();
        if (d.Length > DescriptionMax) throw new BusRefusal("bad_params", $"a goal's description is at most {DescriptionMax} characters");
        return d;
    }
}
