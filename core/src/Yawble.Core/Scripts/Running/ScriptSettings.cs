using System.Text.Json.Nodes;
using Yawble.Core.Scripts.Library;

namespace Yawble.Core.Scripts.Running;

/// <summary>
/// A script member's settings and bindings against what its script declares: settings are the document's "settings"
/// (name, type, default). A "requires" name is an integration's real name (the member uses it; a binding of it to another
/// integration of the same kind is an override) or an alias, which a binding maps to one of the team's integrations.
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

    /// <summary>
    /// What is wrong with a member's settings and bindings for this script (empty: nothing), where the script's steps name the person's
    /// integrations by their real names. A requires name that is one of <paramref name="integrations"/> needs no binding: the member uses
    /// it, and it must be turned on for <paramref name="teamId"/>. A binding of a real name to another integration is an override: the other
    /// must be an integration of the same kind, turned on for the team. A requires name that is not an integration (an alias) needs a
    /// binding, as before.
    /// </summary>
    public static IReadOnlyList<string> Problems(JsonObject document, JsonObject settings, JsonObject bindings, long teamId, string team,
        IScriptIntegrationDirectory integrations)
    {
        var requires = (document["requires"] as JsonArray ?? []).OfType<JsonObject>().Where(o => Name(o) is not null).ToList();
        var real = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var r in requires)
        {
            var name = Name(r)!;
            if (integrations.Find(name) is not { } named) continue;
            real.Add(name);
            if (bindings[name] is JsonValue v && v.TryGetValue<string>(out var to) && !string.IsNullOrWhiteSpace(to)
                && !string.Equals(to.Trim(), named.Name, StringComparison.OrdinalIgnoreCase))
            {
                var other = integrations.Find(to);
                if (other is null) problems.Add($"'{name}' cannot be replaced by '{to.Trim()}': there is no integration of that name");
                else if (other.Kind != named.Kind)
                    problems.Add($"'{name}' cannot be replaced by {other.Name}: {name} is {KindText(named.Kind)} and {other.Name} is {KindText(other.Kind)}");
                else if (!other.IsOnFor(teamId))
                    problems.Add($"'{name}' cannot be replaced by {other.Name}: {other.Name} is not turned on for {team} (Settings > Integrations)");
            }
            else if (!named.IsOnFor(teamId))
                problems.Add($"the script uses {named.Name}, which is not turned on for {team}: turn it on in Settings > Integrations, or replace it with one that is");
        }
        var withoutReal = new JsonObject();
        foreach (var (k, v) in bindings) if (!real.Contains(k)) withoutReal[k] = v?.DeepClone();
        var rest = Problems(document, settings, withoutReal, real);
        return [.. rest, .. problems];
    }

    private static string KindText(string kind) => kind == "email" ? "an Email integration" : "an API";

    /// <summary>What is wrong with a member's settings and bindings for this script (empty: nothing).</summary>
    public static IReadOnlyList<string> Problems(JsonObject document, JsonObject settings, JsonObject bindings) =>
        Problems(document, settings, bindings, new HashSet<string>(StringComparer.Ordinal));

    private static IReadOnlyList<string> Problems(JsonObject document, JsonObject settings, JsonObject bindings, IReadOnlySet<string> unbound)
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
            if (!bindings.ContainsKey(name) && !unbound.Contains(name)) problems.Add($"'{name}' needs a binding to one of the team's integrations");
        return problems;
    }

    /// <summary>
    /// The bindings a run's calls go through: the member's, plus each requires name that is one of the person's integrations and has no
    /// binding, bound to itself (the member uses the integration the steps name).
    /// </summary>
    public static Dictionary<string, string> Effective(JsonObject document, JsonObject bindings, IScriptIntegrationDirectory integrations)
    {
        var map = bindings.Where(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>().Trim(), StringComparer.Ordinal);
        foreach (var r in (document["requires"] as JsonArray ?? []).OfType<JsonObject>())
            if (Name(r) is { } name && !map.ContainsKey(name) && integrations.Find(name) is { } named) map[name] = named.Name;
        return map;
    }

    /// <summary>
    /// Each requires name as the member's runs use it: <c>name</c> as the steps name it, its kind, the integration that runs
    /// (<c>uses</c>) and whether a binding replaces a real name with another integration (<c>overridden</c>).
    /// </summary>
    public static IReadOnlyList<(string Name, string Kind, string Uses, bool Overridden)> MemberIntegrations(JsonObject document, JsonObject bindings,
        IScriptIntegrationDirectory integrations)
    {
        var effective = Effective(document, bindings, integrations);
        var list = new List<(string, string, string, bool)>();
        foreach (var r in (document["requires"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Name(r) is not { } name) continue;
            var kind = r["kind"] is JsonValue kv && kv.TryGetValue<string>(out var k) ? k : "";
            var named = integrations.Find(name);
            var uses = effective.GetValueOrDefault(name) ?? "";
            list.Add((name, named?.Kind ?? kind, uses, named is not null && uses.Length > 0 && !string.Equals(uses, named.Name, StringComparison.OrdinalIgnoreCase)));
        }
        return list;
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
