using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Scripts.Language;

/// <summary>
/// A named schema: an object (or a string enum) stored as JSON Schema in the subset the core checks: <c>type</c>,
/// <c>properties</c>, <c>required</c>, <c>items</c>, <c>enum</c>, <c>format</c> (<c>date</c>, <c>date-time</c>), plus
/// <c>description</c> (what a model reads) and <c>$ref</c> naming another schema.
/// </summary>
public sealed record ScriptSchema(string Name, JsonObject Schema, bool BuiltIn = false)
{
    /// <summary>The schema's fields (its top-level properties) and their types.</summary>
    public IReadOnlyList<(string Field, ScriptType Type, bool Required)> Fields(Func<string, ScriptSchema?> findSchema)
    {
        var required = (Schema["required"] as JsonArray)?.Select(r => r?.ToString()).ToHashSet() ?? [];
        if (Schema["properties"] is not JsonObject props) return [];
        return props.Select(p => (p.Key, ScriptType.FromJsonSchema(p.Value, findSchema), required.Contains(p.Key))).ToList();
    }

    /// <summary>Checks a value against this schema; the problems, each with its JSON path, or none.</summary>
    public IReadOnlyList<string> Validate(JsonNode? value, Func<string, ScriptSchema?>? findSchema = null) =>
        JsonSchemaValidator.Validate(Schema, value, findSchema);

    /// <summary>Parses a schema from its JSON Schema text.</summary>
    public static ScriptSchema Parse(string name, string json) =>
        new(name, JsonNode.Parse(json) as JsonObject ?? throw new FormatException("a schema is a JSON object"));
}

/// <summary>The schemas a script can use by name; the library implements it. Built-in schemas need not be included.</summary>
public interface IScriptSchemaLookup
{
    ScriptSchema? Find(string name);
}

/// <summary>A lookup over a fixed set of schemas (tests, a test run on a draft).</summary>
public sealed class ScriptSchemaSet(IEnumerable<ScriptSchema> schemas) : IScriptSchemaLookup
{
    private readonly Dictionary<string, ScriptSchema> _byName = schemas.ToDictionary(s => s.Name, StringComparer.Ordinal);
    public static readonly ScriptSchemaSet Empty = new([]);
    public ScriptSchema? Find(string name) => _byName.GetValueOrDefault(name);
}

/// <summary>A validator for the JSON Schema subset scripts use. No other keyword is checked; extra properties are allowed.</summary>
public static class JsonSchemaValidator
{
    public static IReadOnlyList<string> Validate(JsonObject schema, JsonNode? value, Func<string, ScriptSchema?>? findSchema = null)
    {
        var errors = new List<string>();
        Check(schema, value, "$", errors, findSchema ?? (_ => null), 0);
        return errors;
    }

    /// <summary>The schema's type, ignoring "null" in a type list (<c>["string", "null"]</c> is a nullable string).</summary>
    internal static string? PrimaryType(JsonObject schema) => schema["type"] switch
    {
        JsonValue v => (string?)v,
        JsonArray a => a.Select(t => (string?)t).FirstOrDefault(t => t != "null") ?? "null",
        _ => schema["properties"] is not null ? "object" : schema["items"] is not null ? "array" : null,
    };

    private static void Check(JsonObject schema, JsonNode? value, string path, List<string> errors, Func<string, ScriptSchema?> find, int depth)
    {
        if (depth > 64) { errors.Add($"{path}: schema nests too deeply"); return; }
        if ((string?)schema["$ref"] is { } reference)
        {
            var target = BuiltInSchemas.Find(reference) ?? find(reference);
            if (target is null) errors.Add($"{path}: no schema named '{reference}'");
            else Check(target.Schema, value, path, errors, find, depth + 1);
            return;
        }

        var types = schema["type"] switch
        {
            JsonValue v => [(string?)v ?? ""],
            JsonArray a => a.Select(t => (string?)t ?? "").ToArray(),
            _ => Array.Empty<string>(),
        };
        if (types.Length > 0 && !types.Any(t => Is(t, value)))
        {
            errors.Add($"{path}: expected {string.Join(" or ", types)}, got {Describe(value)}");
            return;
        }

        if (schema["enum"] is JsonArray allowed && !allowed.Any(a => JsonNode.DeepEquals(a, value)))
            errors.Add($"{path}: '{value?.ToJsonString()}' is not one of {allowed.ToJsonString()}");

        if (value is JsonValue sv && sv.GetValueKind() == JsonValueKind.String && (string?)schema["format"] is { } format)
        {
            var s = sv.GetValue<string>();
            if (format == "date-time" && !ScriptValues.TryParseInstant(s, out _)) errors.Add($"{path}: '{s}' is not a date-time");
            if (format == "date" && !ScriptValues.TryParseDate(s, out _)) errors.Add($"{path}: '{s}' is not a date");
        }

        if (value is JsonObject obj)
        {
            if (schema["required"] is JsonArray required)
                foreach (var r in required.Select(r => (string?)r))
                    if (r is not null && !obj.ContainsKey(r)) errors.Add($"{path}: '{r}' is required");
            if (schema["properties"] is JsonObject props)
                foreach (var (name, sub) in props)
                    if (sub is JsonObject subSchema && obj.TryGetPropertyValue(name, out var fieldValue))
                        Check(subSchema, fieldValue, $"{path}.{name}", errors, find, depth + 1);
        }

        if (value is JsonArray arr && schema["items"] is JsonObject items)
            for (var i = 0; i < arr.Count; i++) Check(items, arr[i], $"{path}[{i}]", errors, find, depth + 1);
    }

    private static bool Is(string type, JsonNode? value) => type switch
    {
        "null" => value is null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value is JsonValue n && n.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue i && i.GetValueKind() == JsonValueKind.Number && IsIntegral(i),
        _ => true,
    };

    private static bool IsIntegral(JsonValue v) =>
        v.TryGetValue<long>(out _) || (v.TryGetValue<double>(out var d) && Math.Floor(d) == d && !double.IsInfinity(d))
        || (decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && decimal.Truncate(m) == m);

    private static string Describe(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "value",
        },
        _ => "value",
    };
}

/// <summary>The read-only schemas every script can use: trigger payloads, email and API responses.</summary>
public static class BuiltInSchemas
{
    private static ScriptSchema Make(string name, string json) => ScriptSchema.Parse(name, json) with { BuiltIn = true };

    public static readonly ScriptSchema TriggerFired = Make("TriggerFired", """
        { "type": "object", "description": "A time trigger (every, cron or once) fired.",
          "properties": {
            "triggerId": { "type": "string", "description": "The trigger's id." },
            "kind": { "type": "string", "enum": ["every", "cron", "once"], "description": "The trigger's kind." },
            "firedAt": { "type": "string", "format": "date-time", "description": "When it fired." } },
          "required": ["triggerId", "kind", "firedAt"] }
        """);

    public static readonly ScriptSchema FolderChange = Make("FolderChange", """
        { "type": "object", "description": "Files changed in a watched folder.",
          "properties": {
            "root": { "type": "string", "description": "The watched folder." },
            "added": { "type": "array", "items": { "type": "string" }, "description": "Files added, relative to root." },
            "removed": { "type": "array", "items": { "type": "string" }, "description": "Files removed, relative to root." },
            "changed": { "type": "array", "items": { "type": "string" }, "description": "Files changed, relative to root." } },
          "required": ["root", "added", "removed", "changed"] }
        """);

    public static readonly ScriptSchema EventItem = Make("EventItem", """
        { "type": "object", "description": "An item an event watch delivered.",
          "properties": {
            "event": { "type": "string", "description": "The event's name." },
            "item": { "description": "The watch's JSON item, as it came." } },
          "required": ["event"] }
        """);

    public static readonly ScriptSchema EmailAddress = Make("EmailAddress", """
        { "type": "object", "description": "A mailbox: a display name and an address.",
          "properties": {
            "name": { "type": ["string", "null"], "description": "The display name, if any." },
            "address": { "type": "string", "description": "The email address." } },
          "required": ["address"] }
        """);

    public static readonly ScriptSchema EmailMessage = Make("EmailMessage", """
        { "type": "object", "description": "An email message, without its attachments.",
          "properties": {
            "id": { "type": "string", "description": "Opaque id of the message, per folder: stable while it stays in that folder or label. Also list's polling cursor: save the last one a list gave and pass it back as after." },
            "threadId": { "type": ["string", "null"], "description": "The conversation it belongs to." },
            "messageId": { "type": ["string", "null"], "description": "The Message-ID header." },
            "folder": { "type": "string", "description": "The folder it was read from." },
            "from": { "$ref": "EmailAddress", "description": "The sender." },
            "to": { "type": "array", "items": { "$ref": "EmailAddress" }, "description": "The To recipients." },
            "cc": { "type": "array", "items": { "$ref": "EmailAddress" }, "description": "The Cc recipients." },
            "subject": { "type": "string", "description": "The subject line." },
            "receivedAt": { "type": "string", "format": "date-time", "description": "When the server received it (INTERNALDATE): UTC ISO-8601, whole seconds, e.g. 2026-11-20T09:10:00Z." },
            "snippet": { "type": "string", "description": "The first 500 characters of the plain text." },
            "body": { "type": ["string", "null"], "description": "The plain text body, when it was fetched." },
            "hasAttachments": { "type": "boolean", "description": "Whether it has attachments." },
            "unread": { "type": "boolean", "description": "Whether it is unread." } },
          "required": ["id", "from", "subject", "receivedAt"] }
        """);

    public static readonly ScriptSchema EmailList = Make("EmailList", """
        { "type": "object", "description": "What an email list returns: the messages, and whether its after no longer applied.",
          "properties": {
            "messages": { "type": "array", "items": { "$ref": "EmailMessage" }, "description": "The messages, in the list's order: by UID with after, else by (receivedAt, UID); the last is the newest." },
            "cursorReset": { "type": "boolean", "description": "True when after no longer applied (the folder was renumbered): the messages are the since list, or none without since. Always present." } },
          "required": ["messages", "cursorReset"] }
        """);

    public static readonly ScriptSchema ApiResponse = Make("ApiResponse", """
        { "type": "object", "description": "An API call's response.",
          "properties": {
            "status": { "type": "integer", "description": "The HTTP status code." },
            "body": { "description": "The body: parsed JSON when it is JSON, else the text." } },
          "required": ["status"] }
        """);

    /// <summary>
    /// What a <c>tell</c> step gives back. Its <c>data</c> is the reply's JSON: the checker types it by the step's <c>reply</c>
    /// schema (<see cref="ScriptTell.ResultType"/>); a variable declared <c>TellResult</c> holds any data.
    /// </summary>
    public static readonly ScriptSchema TellResult = Make("TellResult", """
        { "type": "object", "description": "What a tell step gives back: how the work it gave went, its workflow and the agent's reply.",
          "properties": {
            "outcome": { "type": "string", "enum": ["sent", "done", "failed", "blocked", "timedOut", "dryRun"],
                         "description": "sent (it did not wait), done, failed, blocked, timedOut (the wait passed its timeout) or dryRun (a dry run or Test run sends nothing)." },
            "workflow": { "type": "integer", "description": "The workflow the tell started (0 in a dry run)." },
            "reply": { "type": "string", "description": "The closing summary the agent wrote (empty when it did not wait or nothing came)." },
            "data": { "description": "The reply's JSON, of the step's reply schema; null without a reply schema or when the work did not finish." } },
          "required": ["outcome", "workflow", "reply"] }
        """);

    public static readonly IReadOnlyList<ScriptSchema> All = [TriggerFired, FolderChange, EventItem, EmailAddress, EmailMessage, EmailList, ApiResponse, TellResult];

    public static ScriptSchema? Find(string name) => All.FirstOrDefault(s => s.Name == name);

    public static bool IsBuiltIn(string name) => Find(name) is not null;

    /// <summary>The built-in schemas' names, in the order of <see cref="All"/>.</summary>
    public static readonly IReadOnlyList<string> Names = All.Select(s => s.Name).ToArray();

    /// <summary>A lookup over only the built-in schemas.</summary>
    public static readonly IScriptSchemaLookup Lookup = new ScriptSchemaSet(All);
}
