using System.Globalization;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;

namespace Yawble.Core.Goals;

/// <summary>
/// The goal tools of the Concierge and of paired companion apps, onto the goals.* RPC (the same calls as the Goals page).
/// goal_delete is the one that removes something: its first call shows what it does (the confirmation flow).
/// </summary>
public sealed class GoalTools(Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> ui)
{
    public const string GoalsList = "goals_list", GoalSave = "goal_save", GoalDelete = "goal_delete", DeliverableGoalSet = "deliverable_goal_set";

    public static readonly IReadOnlyList<string> Names = [GoalsList, GoalSave, GoalDelete, DeliverableGoalSet];

    /// <summary>The tools that change something.</summary>
    public static readonly IReadOnlyList<string> Acting = [GoalSave, GoalDelete, DeliverableGoalSet];

    public static bool Handles(string name) => Names.Contains(name);

    public static IEnumerable<JsonObject> Definitions()
    {
        yield return Tool(GoalsList,
            "The person's goals: each one's name, description and deliverables (numbers, how many are open) and the calendar time they cover " +
            "together, then the Unassigned deliverables (no goal). A deliverable has at most one goal.");
        yield return Tool(GoalSave,
            "Create a goal (no id) or rename one or change its description (with id). Names are unique ignoring case. You may propose goal " +
            "names from the high-level work the teams do; save and assign them when the person asks (or says yes to your proposal).",
            ("id", "integer", "The goal's id (goals_list), to change it; leave out to create one.", false),
            ("name", "string", "The goal's name (one line, up to 80 characters).", true),
            ("description", "string", "What the goal is about (optional, up to 2000 characters).", false));
        yield return Tool(GoalDelete,
            "Delete a goal. Its deliverables are kept and become Unassigned. Only when the person asks: the first call shows what it does and " +
            "a token; call again with confirm set to that token after the person says yes in a later message.",
            ("id", "integer", "The goal's id (goals_list).", true),
            ("confirm", "string", "The token from the first call, after the person's yes.", false));
        yield return Tool(DeliverableGoalSet,
            "Give a deliverable its goal, replacing the one it had (a deliverable has at most one), or none to make it Unassigned. When the " +
            "person asks, or to apply goals they agreed to.",
            ("deliverable", "integer", "The deliverable's number (team_deliverables, goals_list).", true),
            ("goal", "string", "The goal's exact name (ignoring case), else its id; none (or unassigned) for Unassigned when no goal has that name.", true));
    }

    /// <summary>The plan and key goal_delete needs a yes for; null for the tools that need none.</summary>
    public async Task<(string Plan, string Key)?> YesNeededAsync(string name, JsonObject args, CancellationToken ct)
    {
        if (name != GoalDelete) return null;
        var id = Id(args, "id") ?? throw new ArgumentException("'id' is required");
        var goal = (await GoalsAsync(ct)).FirstOrDefault(g => Num(g, "id") == id) ?? throw new ArgumentException($"refused (no_such_goal): there is no goal {id}");
        var total = Num(goal, "total");
        var plan = $"Delete goal #{id} \"{goal["name"]}\". " +
                   (total == 0 ? "It has no deliverables." : $"Its {total} deliverable{(total == 1 ? "" : "s")} stay and become Unassigned.");
        return (plan, $"goal:{id}:{goal["name"]}:{total}");
    }

    /// <summary>One tool call, as text for the agent; refusals throw (the caller words them).</summary>
    public async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        switch (name)
        {
            case GoalsList:
                return Describe(await ui("goals.list", null, ct) as JsonObject ?? []);
            case GoalSave:
            {
                var p = new JsonObject { ["name"] = Text(args, "name") ?? throw new ArgumentException("'name' is required") };
                if (Id(args, "id") is { } id) p["id"] = id;
                if (Text(args, "description") is { } d) p["description"] = d;
                var g = await ui("goals.save", p, ct) as JsonObject ?? [];
                return $"ok: goal #{Num(g, "id")} \"{g["name"]}\" saved.";
            }
            case GoalDelete:
            {
                var id = Id(args, "id") ?? throw new ArgumentException("'id' is required");
                var r = await ui("goals.delete", new JsonObject { ["id"] = id }, ct) as JsonObject ?? [];
                var n = Num(r, "unassigned");
                return $"ok: goal #{id} deleted; {n} deliverable{(n == 1 ? " is" : "s are")} Unassigned now.";
            }
            case DeliverableGoalSet:
            {
                var deliverable = Id(args, "deliverable") ?? throw new ArgumentException("'deliverable' is required");
                var said = Text(args, "goal") ?? throw new ArgumentException("'goal' is required (an id, a name, or none)");
                // An exact name first (ignoring case), so a goal named "2027" or "None" is that goal; only then a number is an id
                // and none / unassigned mean Unassigned.
                var goals = await GoalsAsync(ct);
                var match = goals.FirstOrDefault(g => GoalRules.SameName(g["name"]?.GetValue<string>(), said.Trim()));
                long? goal;
                if (match is not null) goal = Num(match, "id");
                else if (long.TryParse(said.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var asId) && goals.FirstOrDefault(g => Num(g, "id") == asId) is { } byId)
                    goal = Num(byId, "id");
                else if (string.Equals(said.Trim(), "none", StringComparison.OrdinalIgnoreCase) || string.Equals(said.Trim(), "unassigned", StringComparison.OrdinalIgnoreCase))
                    goal = null;
                else throw new ArgumentException($"refused (no_such_goal): there is no goal '{said}' (goals_list names them)");
                var r = await ui("goals.assign", new JsonObject { ["deliverable"] = deliverable, ["goal"] = goal }, ct) as JsonObject ?? [];
                return r["goal"] is JsonObject g ? $"ok: deliverable #{deliverable} has goal \"{g["name"]}\" now." : $"ok: deliverable #{deliverable} is Unassigned now.";
            }
            default:
                throw new ArgumentException($"unknown tool: {name}");
        }
    }

    /// <summary>goals.list in words: one line per goal, then the Unassigned ones.</summary>
    internal static string Describe(JsonObject list)
    {
        var lines = new List<string>();
        foreach (var g in (list["goals"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var ids = (g["deliverables"] as JsonArray ?? []).Select(d => "#" + d).ToList();
            var desc = g["description"]?.GetValue<string>() is { Length: > 0 } d ? $" - {d}" : "";
            lines.Add($"goal #{Num(g, "id")} \"{g["name"]}\"{desc}: {Counted(g)}{(ids.Count > 0 ? $" ({string.Join(", ", ids)})" : "")}");
        }
        if (lines.Count == 0) lines.Add("No goals yet.");
        if (list["unassigned"] is JsonObject u)
        {
            var ids = (u["deliverables"] as JsonArray ?? []).Select(d => "#" + d).Take(50).ToList();
            lines.Add($"Unassigned: {Counted(u)}{(ids.Count > 0 ? $" ({string.Join(", ", ids)}{(Num(u, "total") > ids.Count ? ", ..." : "")})" : "")}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string Counted(JsonObject g)
    {
        var total = Num(g, "total");
        var open = Num(g, "open");
        var ms = g["time"] is JsonObject t ? Num(t, "elapsedMs") : 0;
        return $"{total} deliverable{(total == 1 ? "" : "s")}, {open} open" + (total > 0 ? $", {Board.BoardTimeline.Duration(ms)} of calendar time" + (open > 0 ? " so far" : "") : "");
    }

    private async Task<List<JsonObject>> GoalsAsync(CancellationToken ct) =>
        [.. ((await ui("goals.list", null, ct) as JsonObject)?["goals"] as JsonArray ?? []).OfType<JsonObject>()];

    private static long Num(JsonObject o, string key) => o[key] switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        _ => 0,
    };

    private static string? Text(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static long? Id(JsonObject args, string key) => args[key] switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s.Trim().TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var p) => p,
        null => null,
        _ => throw new ArgumentException($"'{key}' must be a number"),
    };

    private static JsonObject Tool(string name, string description, params (string Name, string Type, string Description, bool Required)[] parameters)
    {
        var properties = new JsonObject();
        foreach (var p in parameters) properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(parameters.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
                ["additionalProperties"] = false,
            },
        };
    }
}
