using System.Text.Json.Nodes;

namespace Yawble.Core.Integrations;

/// <summary>
/// What an MCP tool does, from what the server says about it. First hit wins: 1 the universal credential floor on the words of the name
/// (so no annotation can vouch for a credential tool), 2 destructiveHint, 3 readOnlyHint, 4 the verb of the FIRST word of the name
/// (list_issues, create_issue) from the CLI verb tables, 5 unknown. A hint counts only when it is the JSON boolean true.
/// </summary>
public static class McpClassifier
{
    public static Classification Classify(JsonObject tool)
    {
        var name = StringOf(tool["name"]) ?? "";
        var words = name.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (CredentialFloor.Check(words) is { } word)
            return new Classification(CommandClass.Credential, ClassSource.Floor, $"word \"{word}\" reveals or changes a sign-in: never allowed (universal credential floor)");
        var annotations = tool["annotations"] as JsonObject;
        if (Hint(annotations, "destructiveHint"))
            return new Classification(CommandClass.Destructive, ClassSource.Annotation, "the server says this tool is destructive (destructiveHint)");
        if (Hint(annotations, "readOnlyHint"))
            return new Classification(CommandClass.ReadOnly, ClassSource.Annotation, "the server says this tool only reads (readOnlyHint)");
        var verb = words.Length == 0 ? "" : words[0].ToLowerInvariant();
        if (CommandClassifier.DestructiveVerbs.Contains(verb)) return new Classification(CommandClass.Destructive, ClassSource.Verb, $"verb \"{verb}\" is destructive");
        if (CommandClassifier.ReadOnlyVerbs.Contains(verb)) return new Classification(CommandClass.ReadOnly, ClassSource.Verb, $"verb \"{verb}\" matches read-only");
        if (CommandClassifier.ChangeVerbs.Contains(verb)) return new Classification(CommandClass.Change, ClassSource.Verb, $"verb \"{verb}\" changes something");
        return new Classification(CommandClass.Unknown, ClassSource.None, "the name does not say what it does");
    }

    private static bool Hint(JsonObject? annotations, string key) =>
        annotations?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>The string a node holds, or null when it is missing or not a JSON string (a server's JSON is not trusted to have the right types).</summary>
    internal static string? StringOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
