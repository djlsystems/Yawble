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
///   ("handed back" / "run ended" under its name) after the card's latest assignment counts, and a finished run only
///   when that run handed back (card 170: a run cut off after its final answer still ends "completed", exit -1). A
///   failed (crashed) run, a run that ended without a hand-back, a run before the card was last assigned or told new work, its progress, questions and board comments do not.</item>
/// </list>
/// </summary>
public static class TesterRule
{
    private static readonly HashSet<string> TesterWords = new(["test", "tester", "testers", "testing", "qa"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The trail text of a finished run that handed back: the only "run ended" that counts as tested.</summary>
    public const string RanAndHandedBack = "run ended (handed back)";

    /// <summary>
    /// The trail text of a finished run (agent.completed), from its payload's <c>handedBack</c> and <c>run.exitCode</c>.
    /// A run killed after its final answer is still recorded as completed (exit -1), so only <c>handedBack: true</c>
    /// says the run finished its work; a missing <c>handedBack</c> (the Manager's runs, old messages) is not a hand-back.
    /// </summary>
    public static string RunEndedText(System.Text.Json.Nodes.JsonObject? payload)
    {
        var handedBack = payload?["handedBack"] is System.Text.Json.Nodes.JsonValue h && h.TryGetValue<bool>(out var b) && b;
        if (handedBack) return RanAndHandedBack;
        var exit = payload?["run"]?["exitCode"] is System.Text.Json.Nodes.JsonValue x && x.TryGetValue<int>(out var code) && code != 0
            ? $", exit {code}" : "";
        return $"run ended (no hand-back{exit})";
    }

    /// <summary>Whether a trail entry means the tester's work on the card is finished: its hand-back, or its run's end
    /// when that run handed back. A "run ended" with any other text (no hand-back, cut off, or from before this rule)
    /// does not count.</summary>
    private static bool Ran(TrailRow t) =>
        t.Kind == "handed back" || (t.Kind == "run ended" && t.Text == RanAndHandedBack);

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
            .Any(t => Ran(t) && string.Equals(t.Source, tester, StringComparison.OrdinalIgnoreCase)));
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
