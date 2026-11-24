using System.Text.Json.Nodes;

namespace Yawble.Core.Board;

/// <summary>One card a completed workflow closed over a tester's FAIL, as the person accepted it (B142).</summary>
/// <param name="Kind"><see cref="ClosedOverFail.Accepted"/> or <see cref="ClosedOverFail.WontDo"/>.</param>
public sealed record ClosedOverFailEntry(long Card, string? Tester, string? FailLine, string Kind, string? Reason)
{
    /// <summary>The card's trail line, the digest's and the history's: what the person accepted.</summary>
    public string Line => ClosedOverFail.LineOf(Kind, FailLine, Reason);

    /// <summary>The trail entry's kind (shown as is on the card's trail).</summary>
    public string TrailKind => Kind == ClosedOverFail.WontDo ? ClosedOverFail.WontDoTrailKind : ClosedOverFail.AcceptedTrailKind;
}

/// <summary>
/// B142: the WorkflowCompleted payload's <c>closedOverFail</c> (card 620 writes it): an array of
/// <c>{ card, tester, failLine, kind: "accepted" | "wont-do", reason }</c>, absent or null when nothing was closed over a FAIL.
/// </summary>
public static class ClosedOverFail
{
    public const string Field = "closedOverFail";
    public const string Accepted = "accepted", WontDo = "wont-do";
    public const string AcceptedTrailKind = "closed over a FAIL", WontDoTrailKind = "won't do";
    public const string AcceptedPrefix = "Closed over a FAIL, accepted by the person: ";
    public const string WontDoPrefix = "Won't do, accepted by the person: ";

    /// <summary>What the Concierge's workflow watch says of such a workflow.</summary>
    public const string ConciergeWords = "closed over a FAIL (accepted)";

    public static string LineOf(string kind, string? failLine, string? reason) => kind == WontDo
        ? WontDoPrefix + (reason ?? failLine ?? "no reason given")
        : AcceptedPrefix + (failLine ?? "FAIL");

    /// <summary>The entries of a WorkflowCompleted payload; empty when the field is absent, null or malformed.</summary>
    public static IReadOnlyList<ClosedOverFailEntry> Read(JsonObject? payload)
    {
        if (payload?[Field] is not JsonArray a) return [];
        var list = new List<ClosedOverFailEntry>();
        foreach (var o in a.OfType<JsonObject>())
        {
            if (!TryLong(o["card"], out var card)) continue;
            var kind = Str(o, "kind") == WontDo ? WontDo : Accepted;
            list.Add(new ClosedOverFailEntry(card, Str(o, "tester"), Str(o, "failLine"), kind, Str(o, "reason")));
        }
        return list;
    }

    public static IReadOnlyList<ClosedOverFailEntry> Read(string payload)
    {
        try { return Read(JsonNode.Parse(payload) as JsonObject); }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private static bool TryLong(JsonNode? n, out long v)
    {
        v = 0;
        return n is JsonValue j && (j.TryGetValue(out v) || (j.TryGetValue<string>(out var s) && long.TryParse(s, out v)));
    }

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
