using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Bus;

/// <summary>
/// What the person said yes to, so the workflow may be declared complete over a tester's FAIL on a card.
/// <see cref="Kind"/> is <see cref="Accepted"/> (the FAIL is accepted as is) or <see cref="WontDo"/> (the card will not be
/// done). Stored on the needs_decision as <c>closeOverFail: { card, kind, reason }</c> and, when the person picks the yes
/// option, on the answer as <c>closeOverFail: { card, kind, reason, decision }</c>.
/// </summary>
public sealed record CloseOverFail(long Card, string Kind, string? Reason)
{
    public const string Accepted = "accepted", WontDo = "wont-do";

    /// <summary>needs_decision's <c>kind</c> argument: accept-fail (or accepted) and wont-do.</summary>
    public static string ParseKind(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "accept-fail" or "accepted" or "accept" => Accepted,
        "wont-do" or "won't-do" or "wontdo" => WontDo,
        _ => throw new BusRefusal("bad_decision", "kind must be accept-fail (accept the FAIL and close) or wont-do (the card will not be done)"),
    };

    public JsonObject ToJson() => new() { ["card"] = Card, ["kind"] = Kind, ["reason"] = Reason };

    public static CloseOverFail? Read(JsonNode? node) =>
        node is JsonObject o && o["card"] is JsonValue c && c.TryGetValue<long>(out var card)
        && o["kind"] is JsonValue k && k.TryGetValue<string>(out var kind) && kind is Accepted or WontDo
            ? new CloseOverFail(card, kind, o["reason"] is JsonValue r && r.TryGetValue<string>(out var reason) ? reason : null)
            : null;
}

/// <summary>A card closed over a FAIL, as the WorkflowCompleted payload's <c>closedOverFail</c> lists it.</summary>
public sealed record FailClosure(long Card, string Tester, string FailLine, string Kind, string? Reason);

/// <summary>A card whose latest tester verdict in the workflow is FAIL, and the person's yes to close over it (if any).</summary>
public sealed record OutstandingFail(long Card, string Tester, string FailLine, long FailSeq, CloseOverFail? Yes);

/// <summary>
/// The testers' verdicts in a workflow, read from their hand-backs.
/// <list type="bullet">
/// <item>A hand-back's verdict is how it starts: PASS or FAIL (any case, after trimming and any leading Markdown such as
///   <c>**</c> or <c>#</c>; PASSED / FAILED count too). Anything else has no verdict.</item>
/// <item>Its card is the one from the tester's latest instruction in the workflow (before the hand-back) that carries
///   <c>card</c> (tell's card). A hand-back with no such instruction belongs to no card.</item>
/// <item>Testers are the members <see cref="Board.TesterRule.IsTester(MemberRow)"/> says are (as BoardProjector.TestersOf).</item>
/// <item>A card's latest verdict counts: a later PASS (from any tester) clears an earlier FAIL.</item>
/// <item>The person's yes (the answer that picked the decision's <see cref="DecisionActions.CloseOverFail"/> option) counts for a FAIL
///   it came after; a FAIL after the yes needs a yes of its own.</item>
/// </list>
/// </summary>
public static class TesterVerdicts
{
    public const string Pass = "PASS", Fail = "FAIL";

    /// <summary>Longest FAIL line quoted (the rest is cut with "...").</summary>
    public const int MaxFailLine = 300;

    /// <summary>PASS, FAIL or null, from how the hand-back starts.</summary>
    public static string? Of(string? delivered)
    {
        var t = StripLead(delivered);
        foreach (var (word, verdict) in new[] { ("PASSED", Pass), ("FAILED", Fail), ("PASS", Pass), ("FAIL", Fail) })
            if (t.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (t.Length == word.Length || !char.IsLetterOrDigit(t[word.Length])))
                return verdict;
        return null;
    }

    /// <summary>The FAIL line quoted: the hand-back's first line, trimmed, at most <see cref="MaxFailLine"/> characters.</summary>
    public static string FirstLine(string delivered)
    {
        var t = delivered.Trim();
        var nl = t.IndexOfAny(['\r', '\n']);
        if (nl >= 0) t = t[..nl].TrimEnd();
        return t.Length <= MaxFailLine ? t : t[..(MaxFailLine - 3)] + "...";
    }

    private static string StripLead(string? s) => (s ?? "").TrimStart().TrimStart('*', '_', '#', '`', '>', ' ', '\t');

    /// <summary>The cards whose latest tester verdict in <paramref name="thread"/> is FAIL, in card order.</summary>
    /// <param name="testers">The team's testers, by member name (any case).</param>
    public static IReadOnlyList<OutstandingFail> Outstanding(IReadOnlyList<Message> thread, string team, IReadOnlySet<string> testers)
    {
        if (testers.Count == 0) return [];
        var ordered = thread.OrderBy(m => m.Seq).ToList();
        var cardOf = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); // tester -> card of its latest instruction with card
        var latest = new Dictionary<long, (string Verdict, string Tester, string Line, long Seq)>();
        var yeses = new List<(long Seq, CloseOverFail Yes)>();
        foreach (var m in ordered)
        {
            if (m.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
            {
                if (m.Source == MessageTypes.PersonSource && Field(m.Payload, "closeOverFail") is { } yes && CloseOverFail.Read(yes) is { } y)
                    yeses.Add((m.Seq, y));
                var to = m.Type[MessageTypes.InstructionPrefix.Length..];
                if (TesterOf(to, team, testers) is { } tester && Field(m.Payload, "card") is JsonValue c && c.TryGetValue<long>(out var card))
                    cardOf[tester] = card;
            }
            else if (m.Type == MessageTypes.Handback && TesterOf(m.Source, team, testers) is { } tester
                     && cardOf.TryGetValue(tester, out var card) && Field(m.Payload, "delivered") is JsonValue d
                     && d.TryGetValue<string>(out var delivered) && Of(delivered) is { } verdict)
            {
                latest[card] = (verdict, m.Source, FirstLine(delivered), m.Seq);
            }
        }
        return latest.Where(kv => kv.Value.Verdict == Fail).OrderBy(kv => kv.Key)
            .Select(kv => new OutstandingFail(kv.Key, kv.Value.Tester, kv.Value.Line, kv.Value.Seq,
                yeses.LastOrDefault(y => y.Seq > kv.Value.Seq && y.Yes.Card == kv.Key).Yes))
            .ToList();
    }

    /// <summary>The tester's name when <paramref name="qualified"/> is "team/name" of one of <paramref name="testers"/>.</summary>
    private static string? TesterOf(string qualified, string team, IReadOnlySet<string> testers)
    {
        var slash = qualified.IndexOf('/');
        if (slash <= 0 || !string.Equals(qualified[..slash], team, StringComparison.OrdinalIgnoreCase)) return null;
        var name = qualified[(slash + 1)..];
        return testers.Contains(name) ? name : null;
    }

    private static JsonNode? Field(string payload, string key)
    {
        try { return (JsonNode.Parse(payload) as JsonObject)?[key]; }
        catch (JsonException) { return null; }
    }

    /// <summary>The refusal of workflow_complete while <paramref name="open"/> (FAILs without the person's yes) is outstanding.</summary>
    public static BusRefusal Refusal(IReadOnlyList<OutstandingFail> open)
    {
        var first = open[0];
        var tester = first.Tester[(first.Tester.IndexOf('/') + 1)..];
        var what = open.Count == 1
            ? $"card {first.Card}'s latest test is a FAIL: {tester} handed back \"{first.FailLine}\""
            : "these cards' latest tests are FAILs: " + string.Join("; ", open.Select(f =>
                $"card {f.Card} ({f.Tester[(f.Tester.IndexOf('/') + 1)..]}: \"{f.FailLine}\")"));
        return new BusRefusal("tester_fail",
            $"workflow completion refused: {what}. A workflow is not declared complete over a tester's FAIL. Do one of these:\n" +
            $"- fix it and have the tester verify card {first.Card} again (a later PASS clears the FAIL);\n" +
            $"- ask the person with needs_decision, card {first.Card} and kind accept-fail, saying why, e.g. \"{tester} failed card {first.Card} on X; " +
            "I think the criterion is too strict because Y: accept and close?\". When the person picks the yes, declare again;\n" +
            $"- mark card {first.Card} won't-do: needs_decision with card {first.Card}, kind wont-do and the reason. That also needs the person's yes.");
    }
}
