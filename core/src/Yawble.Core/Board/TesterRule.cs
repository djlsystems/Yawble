using Yawble.Core.Bus;

namespace Yawble.Core.Board;

/// <summary>
/// B019 / B027: a tester's card reaches Done only once that tester has tested it, or with a reason on the card.
/// <list type="bullet">
/// <item>Who is a tester (<see cref="IsTester"/>): a member other than the Manager whose name starts with "Tester"
///   ("Tester Wren" is TesterWren), or whose role or job title has one of the words test, tester, testers, testing or QA,
///   any case. The job title is the job text before its first colon ("Tester: builds and tests" is "Tester"), or its
///   first line when it has no colon. Whole words only, so "latest", "contest" or "attestation" are not hits; and
///   "tests" / "tested" are not tester words, so "Writes the code and its unit tests" is not a tester.</item>
/// <item>Whose card it is: the card's member now, or any member it was assigned to ("Assigned to X:" on the trail).</item>
/// <item>Has the tester tested it (<see cref="UntestedTester"/>): only the tester's own hand-back or finished run
///   ("handed back" / "run ended" under its name) after the card's latest assignment counts. A failed (crashed) run, a
///   run before the card was last assigned or told new work, its progress, questions and board comments do not.</item>
/// </list>
/// </summary>
public static class TesterRule
{
    private static readonly HashSet<string> TesterWords = new(["test", "tester", "testers", "testing", "qa"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The trail kinds that mean the tester's work on the card is finished.</summary>
    private static readonly HashSet<string> RanKinds = new(["handed back", "run ended"], StringComparer.Ordinal);

    public static bool IsTester(MemberRow m) =>
        !string.Equals(m.Role, "manager", StringComparison.OrdinalIgnoreCase) && IsTester(m.Name, m.Role, m.Instructions);

    public static bool IsTester(string? name, string? role, string? job) =>
        (name ?? "").TrimStart().StartsWith("Tester", StringComparison.OrdinalIgnoreCase)
        || HasTesterWord(role) || HasTesterWord(JobTitle(job));

    /// <summary>The job's title: the text before its first colon, or its first line when it has none.</summary>
    public static string JobTitle(string? job)
    {
        var t = (job ?? "").Trim();
        var colon = t.IndexOf(':');
        if (colon >= 0) return t[..colon];
        var nl = t.IndexOf('\n');
        return nl >= 0 ? t[..nl] : t;
    }

    private static bool HasTesterWord(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        var start = -1;
        for (var i = 0; i <= s.Length; i++)
        {
            var letter = i < s.Length && char.IsLetter(s[i]);
            if (letter && start < 0) start = i;
            else if (!letter && start >= 0)
            {
                if (TesterWords.Contains(s[start..i])) return true;
                start = -1;
            }
        }
        return false;
    }

    /// <summary>Each team's testers, by team name (any case).</summary>
    public static Dictionary<string, HashSet<string>> ByTeam(IEnumerable<MemberRow> members)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in members.Where(IsTester))
        {
            if (!map.TryGetValue(m.Team, out var set)) map[m.Team] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(m.Name);
        }
        return map;
    }

    /// <summary>The tester the card is for that has not tested it since its latest assignment; null when none.</summary>
    public static string? UntestedTester(CardRow card, IReadOnlyList<TrailRow> trail, IReadOnlySet<string> testers)
    {
        if (testers.Count == 0) return null;
        var forWhom = trail.Where(t => t.Kind == "assigned" && t.Text.StartsWith("Assigned to ", StringComparison.Ordinal))
            .Select(t => t.Text["Assigned to ".Length..].Split(':')[0].Trim())
            .Prepend(card.Member ?? "")
            .Where(testers.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (forWhom.Count == 0) return null;
        var latest = LatestAssignment(trail);
        return forWhom.FirstOrDefault(tester => !trail.Skip(latest + 1)
            .Any(t => RanKinds.Contains(t.Kind) && string.Equals(t.Source, tester, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The index of the card's latest assignment: a claim ("assigned") or new work told to its member by a teammate
    /// ("told" from anyone but the person or the schedule - those are answers and notices, not new work). -1 when none.
    /// </summary>
    private static int LatestAssignment(IReadOnlyList<TrailRow> trail)
    {
        for (var i = trail.Count - 1; i >= 0; i--)
        {
            var t = trail[i];
            if (t.Kind == "assigned") return i;
            if (t.Kind == "told" && t.Source is not ("you" or "schedule")) return i;
        }
        return -1;
    }
}
