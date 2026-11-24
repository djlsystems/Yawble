using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Bus;

/// <summary>
/// B123: the actions an option of a needs_decision may carry. Fixed in the core: an agent can only name one, never apply
/// it; the core applies it when the person answers with that option (UiRpc bus.answer, optionIndex).
/// </summary>
public static class DecisionActions
{
    /// <summary>Turns on the team's "Allow localhost (macOS)" (teams.localhost, B119), as Team settings > Network does.</summary>
    public const string AllowLocalhostMac = "team.allowLocalhostMac";

    /// <summary>
    /// B142 (card 620): the person says yes to closing the workflow over a tester's FAIL on the decision's card (accepted,
    /// or won't-do). Only on a decision with <c>card</c> and <c>kind</c>; the app changes no setting, the bus records the
    /// yes on the answer (<c>closeOverFail</c>) and workflow_complete then goes through, listing the card in closedOverFail.
    /// </summary>
    public const string CloseOverFail = "workflow.closeOverFail";

    public static readonly IReadOnlyList<string> All = [AllowLocalhostMac, CloseOverFail];

    public static bool IsKnown(string? action) => action is not null && All.Contains(action, StringComparer.Ordinal);
}

/// <summary>One answer the person can pick: the button's label, the reply sent as the answer, and the allowlisted action (if any).</summary>
public sealed record DecisionOption(string Label, string Reply, string? Action = null);

/// <summary>
/// B123: what a needs_decision asks, as stored in its message payload:
/// <c>{ question, summary?, options?: [{ label, reply, action? }], details? }</c>. A call with only question is stored as
/// before (<c>{ question }</c>). A call with a summary and no question stores the summary as the question too, so every
/// reader of "question" keeps working.
/// </summary>
public sealed record DecisionAsk(string Question, string? Summary = null, IReadOnlyList<DecisionOption>? Options = null, string? Details = null,
    CloseOverFail? Close = null)
{
    public const int MinOptions = 2, MaxOptions = 4, MaxLabel = 40;

    /// <summary>The line the card and the lists show: the summary when there is one, else the question.</summary>
    public string Headline => Summary ?? Question;

    /// <summary>
    /// The tool's arguments, checked: question or summary (at least one), options 2-4 (each a label of at most 40 characters
    /// and a reply; at most one with an action, and that action allowlisted), details. Refused (nothing stored) otherwise.
    /// <paramref name="options"/> may be the array itself or a string holding it as JSON (some CLIs send arrays as text).
    /// </summary>
    /// <remarks>
    /// B142 (card 620): with <paramref name="card"/>, the decision asks the person to close the workflow over that card's
    /// tester FAIL: <paramref name="kind"/> is required (accept-fail or wont-do), and a won't-do needs a
    /// <paramref name="reason"/>. Exactly one option must then carry the action workflow.closeOverFail (the yes); with no
    /// options the core gives two (the yes, and "fix it first"). The action is refused on a decision without a card.
    /// </remarks>
    public static DecisionAsk Parse(string? question, string? summary, JsonNode? options, string? details,
        long? card = null, string? kind = null, string? reason = null)
    {
        question = Trimmed(question);
        summary = Trimmed(summary);
        details = Trimmed(details);
        reason = Trimmed(reason);
        if (question is null && summary is null) throw new BusRefusal("bad_decision", "give the decision's summary (one plain sentence), or a question");
        var list = ParseOptions(options);
        CloseOverFail? close = null;
        if (card is { } id)
        {
            if (Trimmed(kind) is null) throw new BusRefusal("bad_decision", "with card, give kind: accept-fail (accept the tester's FAIL and close) or wont-do (the card will not be done)");
            close = new CloseOverFail(id, CloseOverFail.ParseKind(kind), reason);
            if (close.Kind == CloseOverFail.WontDo && reason is null) throw new BusRefusal("bad_decision", $"a won't-do needs a reason: say why card {id} will not be done");
            list ??= DefaultCloseOptions(close);
            if (list.Count(o => o.Action == DecisionActions.CloseOverFail) != 1)
                throw new BusRefusal("bad_decision", $"with card, exactly one option (the yes) must carry action {DecisionActions.CloseOverFail}");
        }
        else if (kind is not null && Trimmed(kind) is not null) throw new BusRefusal("bad_decision", "kind goes with card: name the card the tester failed");
        else if (list?.Any(o => o.Action == DecisionActions.CloseOverFail) == true)
            throw new BusRefusal("bad_decision", $"action {DecisionActions.CloseOverFail} needs card and kind: name the card the tester failed");
        return new DecisionAsk(question ?? summary!, summary, list, details, close);
    }

    private static IReadOnlyList<DecisionOption> DefaultCloseOptions(CloseOverFail c) => c.Kind == CloseOverFail.WontDo
        ? [new("Yes, won't do it", $"Yes: card {c.Card} is won't-do. Close the workflow.", DecisionActions.CloseOverFail),
           new("No, do the card", $"No: card {c.Card} is to be done. Fix it and have the tester verify it again.")]
        : [new("Accept and close", $"Yes: accept the FAIL on card {c.Card} and close the workflow.", DecisionActions.CloseOverFail),
           new("No, fix it first", $"No: fix card {c.Card} and have the tester verify it again.")];

    private static IReadOnlyList<DecisionOption>? ParseOptions(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue text && text.TryGetValue<string>(out var s))
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            try { node = JsonNode.Parse(s); }
            catch (JsonException) { throw new BusRefusal("bad_decision", "options must be an array of { label, reply, action? }"); }
        }
        if (node is not JsonArray array) throw new BusRefusal("bad_decision", "options must be an array of { label, reply, action? }");
        if (array.Count is < MinOptions or > MaxOptions)
            throw new BusRefusal("bad_decision", $"give {MinOptions} to {MaxOptions} options (you gave {array.Count})");
        var list = new List<DecisionOption>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject o) throw new BusRefusal("bad_decision", $"option {i + 1} must be {{ label, reply, action? }}");
            foreach (var key in o.Select(kv => kv.Key))
                if (key is not ("label" or "reply" or "action")) throw new BusRefusal("bad_decision", $"option {i + 1} has an unknown field '{key}' (label, reply, action)");
            var label = Trimmed(Str(o, "label", i)) ?? throw new BusRefusal("bad_decision", $"option {i + 1} needs a label");
            if (label.Length > MaxLabel) throw new BusRefusal("bad_decision", $"option {i + 1}'s label is {label.Length} characters; at most {MaxLabel}");
            var reply = Trimmed(Str(o, "reply", i)) ?? throw new BusRefusal("bad_decision", $"option {i + 1} needs a reply (the answer it sends)");
            var action = Trimmed(Str(o, "action", i));
            if (action is not null && !DecisionActions.IsKnown(action))
                throw new BusRefusal("bad_decision", $"option {i + 1}'s action '{action}' is not one the app knows ({string.Join(", ", DecisionActions.All)})");
            list.Add(new DecisionOption(label, reply, action));
        }
        if (list.Count(o => o.Action is not null) > 1) throw new BusRefusal("bad_decision", "at most one option may carry an action");
        if (list.Select(o => o.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count)
            throw new BusRefusal("bad_decision", "each option needs its own label");
        return list;
    }

    private static string? Str(JsonObject o, string key, int i) => o[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => throw new BusRefusal("bad_decision", $"option {i + 1}'s {key} must be text"),
    };

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The payload stored: only the fields given (a question-only ask is <c>{"question": ...}</c>, as before B123).</summary>
    public JsonObject ToPayload()
    {
        var o = new JsonObject { ["question"] = Question };
        if (Summary is not null) o["summary"] = Summary;
        if (Options is not null)
            o["options"] = new JsonArray(Options.Select(x =>
            {
                var item = new JsonObject { ["label"] = x.Label, ["reply"] = x.Reply };
                if (x.Action is not null) item["action"] = x.Action;
                return (JsonNode?)item;
            }).ToArray());
        if (Details is not null) o["details"] = Details;
        if (Close is not null) o["closeOverFail"] = Close.ToJson();
        return o;
    }

    /// <summary>A stored needs_decision read back; a payload that is not an object gives its text as the question. Never throws.</summary>
    public static DecisionAsk Read(string payload)
    {
        JsonObject? o = null;
        try { o = JsonNode.Parse(payload) as JsonObject; } catch (JsonException) { }
        if (o is null) return new DecisionAsk(payload);
        static string? S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
        List<DecisionOption>? options = null;
        if (o["options"] is JsonArray a)
        {
            options = [];
            foreach (var n in a)
                if (n is JsonObject x && S(x, "label") is { } label && S(x, "reply") is { } reply) options.Add(new DecisionOption(label, reply, S(x, "action")));
        }
        return new DecisionAsk(S(o, "question") ?? S(o, "summary") ?? payload, S(o, "summary"), options, S(o, "details"), CloseOverFail.Read(o["closeOverFail"]));
    }
}
