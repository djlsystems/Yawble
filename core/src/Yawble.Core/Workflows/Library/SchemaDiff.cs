using System.Text.Json.Nodes;

namespace Yawble.Core.Workflows.Library;

/// <summary>
/// What a new version of a schema takes away from the old one: the dotted field paths (e.g. "from.address") that are gone
/// or whose type changed (type, format, a schema reference, or an enum that lost a value). Lists are looked through: the
/// fields of a list's items keep the list field's path. Added fields break nothing.
/// </summary>
public static class SchemaDiff
{
    public static IReadOnlyList<string> RemovedOrRetyped(JsonObject oldSchema, JsonObject newSchema)
    {
        var list = new List<string>();
        Walk(oldSchema, newSchema, "", list);
        return list;
    }

    /// <summary>Whether a change at <paramref name="changed"/> breaks a reference to <paramref name="referenced"/> (the field or one inside it).</summary>
    public static bool Covers(string changed, string referenced) =>
        string.Equals(changed, referenced, StringComparison.Ordinal)
        || referenced.StartsWith(changed + ".", StringComparison.Ordinal)
        || referenced.StartsWith(changed + "[", StringComparison.Ordinal);

    private static void Walk(JsonObject oldNode, JsonObject newNode, string path, List<string> changed)
    {
        oldNode = Items(oldNode);
        newNode = Items(newNode);
        if (oldNode["properties"] is not JsonObject oldProps) return;
        var newProps = newNode["properties"] as JsonObject;
        foreach (var (name, value) in oldProps)
        {
            var p = path.Length == 0 ? name : path + "." + name;
            if (value is not JsonObject oldField) continue;
            if (newProps?[name] is not JsonObject newField)
            {
                changed.Add(p);
                continue;
            }
            if (Retyped(oldField, newField))
            {
                changed.Add(p);
                continue;
            }
            Walk(oldField, newField, p, changed);
        }
    }

    /// <summary>A list's item schema (nested lists too); anything else as is.</summary>
    private static JsonObject Items(JsonObject node)
    {
        while (Str(node, "type") == "array" && node["items"] is JsonObject items) node = items;
        return node;
    }

    private static bool Retyped(JsonObject a, JsonObject b)
    {
        if (Shape(a) != Shape(b)) return true;
        var (ia, ib) = (Items(a), Items(b));
        if (ia["enum"] is JsonArray ea)
        {
            if (ib["enum"] is not JsonArray eb) return false; // an enum widened to any string still accepts every old value
            var kept = eb.Select(x => x?.ToJsonString()).ToHashSet();
            return ea.Any(x => !kept.Contains(x?.ToJsonString()));
        }
        return ib["enum"] is JsonArray; // a free value narrowed to an enum
    }

    /// <summary>The field's type as a string: list depth, then the item's type, format and schema reference.</summary>
    private static string Shape(JsonObject node)
    {
        var depth = 0;
        while (Str(node, "type") == "array" && node["items"] is JsonObject items)
        {
            depth++;
            node = items;
        }
        return $"{depth}|{Str(node, "type")}|{Str(node, "format")}|{Str(node, "$ref")}";
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
