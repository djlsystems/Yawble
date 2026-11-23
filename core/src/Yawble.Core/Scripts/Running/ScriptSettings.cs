using System.Text.Json.Nodes;

namespace Yawble.Core.Scripts.Running;

/// <summary>
/// A script member's settings and bindings against what its script declares (design section 7): settings are the
/// document's "settings" (name, type, default), bindings map each "requires" name to one of the team's integrations.
/// </summary>
public static class ScriptSettings
{
    /// <summary>The settings a run gets: the member's values, and each declared default the member did not set.</summary>
    public static JsonObject WithDefaults(JsonObject document, JsonObject given)
    {
        var settings = (JsonObject)given.DeepClone();
        foreach (var s in document["settings"] as JsonArray ?? [])
        {
            if (s is not JsonObject o || Name(o) is not { } name || settings.ContainsKey(name)) continue;
            if (o["default"] is { } d) settings[name] = d.DeepClone();
        }
        return settings;
    }

    /// <summary>What is wrong with a member's settings and bindings for this script (empty: nothing).</summary>
    public static IReadOnlyList<string> Problems(JsonObject document, JsonObject settings, JsonObject bindings)
    {
        var problems = new List<string>();
        var declared = (document["settings"] as JsonArray ?? []).OfType<JsonObject>().Where(o => Name(o) is not null).ToDictionary(o => Name(o)!, StringComparer.Ordinal);
        foreach (var (name, value) in settings)
        {
            if (!declared.TryGetValue(name, out var decl)) { problems.Add($"the script has no setting '{name}'"); continue; }
            var type = decl["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : "string";
            if (!Fits(type, value)) problems.Add($"setting '{name}' must be a {type}");
        }
        foreach (var (name, decl) in declared)
            if (!settings.ContainsKey(name) && decl["default"] is null) problems.Add($"setting '{name}' needs a value");

        var requires = (document["requires"] as JsonArray ?? []).OfType<JsonObject>().Select(Name).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var (name, value) in bindings)
        {
            if (!requires.Contains(name)) problems.Add($"the script requires no '{name}' to bind");
            else if (value is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s))
                problems.Add($"binding '{name}' must name one of the team's integrations");
        }
        foreach (var name in requires)
            if (!bindings.ContainsKey(name)) problems.Add($"'{name}' needs a binding to one of the team's integrations");
        return problems;
    }

    private static string? Name(JsonObject o) => o["name"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>A value against a declared type, as far as JSON tells (dates and durations are strings; objects any object).</summary>
    private static bool Fits(string type, JsonNode? value)
    {
        type = type.Trim();
        if (value is null) return false;
        if (type.StartsWith("list<", StringComparison.Ordinal) && type.EndsWith('>'))
            return value is JsonArray a && a.All(x => Fits(type[5..^1], x));
        return type switch
        {
            "int" => value is JsonValue v && v.TryGetValue<long>(out _),
            "float" => value is JsonValue v && v.TryGetValue<double>(out _),
            "bool" => value is JsonValue v && v.TryGetValue<bool>(out _),
            "string" or "date" or "datetime" or "duration" => value is JsonValue v && v.TryGetValue<string>(out _),
            _ when type.StartsWith("enum", StringComparison.Ordinal) => value is JsonValue v && v.TryGetValue<string>(out _),
            _ => value is JsonObject,
        };
    }
}
