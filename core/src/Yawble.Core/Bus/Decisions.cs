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

    public static readonly IReadOnlyList<string> All = [AllowLocalhostMac];

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
public sealed record DecisionAsk(string Question, string? Summary = null, IReadOnlyList<DecisionOption>? Options = null, string? Details = null)
{
    public const int MinOptions = 2, MaxOptions = 4, MaxLabel = 40;

    /// <summary>The line the card and the lists show: the summary when there is one, else the question.</summary>
    public string Headline => Summary ?? Question;

    /// <summary>
    /// The tool's arguments, checked: question or summary (at least one), options 2-4 (each a label of at most 40 characters
    /// and a reply; at most one with an action, and that action allowlisted), details. Refused (nothing stored) otherwise.
    /// <paramref name="options"/> may be the array itself or a string holding it as JSON (some CLIs send arrays as text).
    /// </summary>
    public static DecisionAsk Parse(string? question, string? summary, JsonNode? options, string? details)
    {
        question = Trimmed(question);
        summary = Trimmed(summary);
        details = Trimmed(details);
        if (question is null && summary is null) throw new BusRefusal("bad_decision", "give the decision's summary (one plain sentence), or a question");
        var list = ParseOptions(options);
        return new DecisionAsk(question ?? summary!, summary, list, details);
    }

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
        return new DecisionAsk(S(o, "question") ?? S(o, "summary") ?? payload, S(o, "summary"), options, S(o, "details"));
    }
}
