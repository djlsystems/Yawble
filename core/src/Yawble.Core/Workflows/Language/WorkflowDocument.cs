using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Workflows.Language;

/// <summary>A workflow document that is not valid JSON or not shaped like <c>yawble-workflow/1</c>.</summary>
public sealed class WorkflowDocumentException(string message) : FormatException(message);

/// <summary>What triggers a workflow accepts: <c>every</c>, <c>cron</c>, <c>once</c>, <c>folder</c>, <c>event</c>, <c>tell</c>.</summary>
public sealed record WorkflowTriggerSpec(IReadOnlyList<string> Accepts)
{
    public static readonly IReadOnlyList<string> Kinds = ["every", "cron", "once", "folder", "event", "tell"];
}

/// <summary>A setting the workflow declares and each workflow member sets (recipients, a folder name).</summary>
public sealed record WorkflowSetting(string Name, string Type, string? Label = null, JsonNode? Default = null, string? Description = null);

/// <summary>An integration the workflow needs, by a name its steps use; each member binds it to one of the team's.</summary>
public sealed record WorkflowRequirement(string Name, string Kind, IReadOnlyList<string> Actions);

/// <summary>A variable: <c>run</c> (in memory for one run) or <c>saved</c> (kept for the member between runs).</summary>
public sealed record WorkflowVariable(string Name, string Type, string Scope = "run", JsonNode? Initial = null)
{
    public bool IsSaved => Scope == "saved";
}

/// <summary>An output the workflow declares; an <c>end</c> step sets it.</summary>
public sealed record WorkflowOutput(string Name, string Type, string? Description = null);

/// <summary>
/// One step, kept as its JSON object so every property survives a save. <see cref="Id"/> and <see cref="Type"/> are its
/// identity; the step's other properties (<c>next</c>, <c>when</c>, <c>args</c> ...) are read where they are used.
/// </summary>
public sealed class WorkflowStep
{
    public string Id { get; }
    public string Type { get; }
    public JsonObject Json { get; }

    internal WorkflowStep(JsonObject json)
    {
        Json = WorkflowDocument.Materialize(json);
        Id = (string?)json["id"] ?? "";
        Type = (string?)json["type"] ?? "";
    }

    public bool Has(string property) => Json.ContainsKey(property);

    public JsonNode? this[string property] => Json[property];

    /// <summary>A text property, or null when absent, null, or not text.</summary>
    public string? Text(string property) =>
        Json[property] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>The steps this one names as successors, with the property that names each (null: the body or run ends there).</summary>
    public IEnumerable<(string Property, string? Target)> Successors()
    {
        switch (Type)
        {
            case "if":
                yield return ("then", Text("then"));
                yield return ("else", Text("else"));
                break;
            case "switch":
                if (Json["cases"] is JsonObject cases)
                    foreach (var (value, target) in cases) yield return ($"cases.{value}", target is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : null);
                yield return ("default", Text("default"));
                break;
            case "forEach" or WorkflowLoop.StepType:
                yield return ("body", Text("body"));
                yield return ("then", Text("then"));
                break;
            case "end":
                break;
            default:
                yield return ("next", Text("next"));
                break;
        }
    }
}

/// <summary>A workflow: a <c>yawble-workflow/1</c> JSON document (settings, requirements, variables and a graph of steps).</summary>
public sealed class WorkflowDocument
{
    public const string CurrentFormat = "yawble-workflow/1";

    public required string Format { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public WorkflowTriggerSpec Trigger { get; init; } = new([]);

    /// <summary>The type of <c>input</c> when the workflow declares one; otherwise it follows from the triggers it accepts.</summary>
    public string? Input { get; init; }

    public IReadOnlyList<WorkflowSetting> Settings { get; init; } = [];
    public IReadOnlyList<WorkflowRequirement> Requires { get; init; } = [];
    public IReadOnlyList<WorkflowVariable> Variables { get; init; } = [];
    public IReadOnlyList<WorkflowOutput> Outputs { get; init; } = [];
    public required string Start { get; init; }
    public IReadOnlyList<WorkflowStep> Steps { get; init; } = [];

    /// <summary>
    /// The top-level <c>layout</c> section (node positions for the designer), kept as given and never checked or run.
    /// Treat it as read-only; <see cref="ToJson"/> writes a copy.
    /// </summary>
    public JsonObject? Layout { get; init; }

    /// <summary>
    /// Top-level members the format does not name (and a <c>layout</c> that is not an object), in document order, kept as
    /// given so a save does not drop them; never checked or run. Treat them as read-only; <see cref="ToJson"/> writes copies.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, JsonNode?>> Extensions { get; init; } = [];

    /// <summary>The top-level members this format reads; any other is kept in <see cref="Extensions"/>.</summary>
    public static readonly IReadOnlyList<string> KnownMembers =
        ["format", "id", "name", "description", "trigger", "input", "settings", "requires", "variables", "outputs", "start", "steps", "layout"];

    /// <summary>The step with this id (the first, when ids repeat; the checker reports that).</summary>
    public WorkflowStep? Step(string? id) => id is null ? null : Steps.FirstOrDefault(s => s.Id == id);

    public static WorkflowDocument Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException e) { throw new WorkflowDocumentException($"the workflow is not valid JSON: {e.Message}"); }
        if (root is not JsonObject o) throw new WorkflowDocumentException("a workflow is a JSON object");
        return FromJson(o);
    }

    public static WorkflowDocument FromJson(JsonObject o)
    {
        var format = Str(o, "format", "the workflow");
        if (format != CurrentFormat) throw new WorkflowDocumentException($"format '{format}' is not '{CurrentFormat}'");

        var trigger = o["trigger"] switch
        {
            null => new WorkflowTriggerSpec([]),
            JsonObject t => new WorkflowTriggerSpec(Strings(t["accepts"], "trigger.accepts")),
            _ => throw new WorkflowDocumentException("trigger is an object { \"accepts\": [...] }"),
        };

        return new WorkflowDocument
        {
            Format = format,
            Id = Str(o, "id", "the workflow"),
            Name = OptStr(o, "name", "the workflow") ?? Str(o, "id", "the workflow"),
            Description = OptStr(o, "description", "the workflow"),
            Trigger = trigger,
            Input = OptStr(o, "input", "the workflow"),
            Settings = Objects(o, "settings").Select((s, i) => new WorkflowSetting(
                Str(s, "name", $"settings[{i}]"), Str(s, "type", $"settings[{i}]"), OptStr(s, "label", $"settings[{i}]"),
                Materialize(s["default"]?.DeepClone()), OptStr(s, "description", $"settings[{i}]"))).ToList(),
            Requires = Objects(o, "requires").Select((r, i) => new WorkflowRequirement(
                Str(r, "name", $"requires[{i}]"), Str(r, "kind", $"requires[{i}]"), Strings(r["actions"], $"requires[{i}].actions"))).ToList(),
            Variables = Objects(o, "variables").Select((v, i) => new WorkflowVariable(
                Str(v, "name", $"variables[{i}]"), Str(v, "type", $"variables[{i}]"), OptStr(v, "scope", $"variables[{i}]") ?? "run",
                Materialize(v["initial"]?.DeepClone()))).ToList(),
            Outputs = Objects(o, "outputs").Select((v, i) => new WorkflowOutput(
                Str(v, "name", $"outputs[{i}]"), Str(v, "type", $"outputs[{i}]"), OptStr(v, "description", $"outputs[{i}]"))).ToList(),
            Start = Str(o, "start", "the workflow"),
            Steps = Objects(o, "steps").Select((s, i) =>
            {
                Str(s, "id", $"steps[{i}]");
                Str(s, "type", $"steps[{i}]");
                return new WorkflowStep((JsonObject)s.DeepClone());
            }).ToList(),
            Layout = o["layout"] is JsonObject layout ? Materialize((JsonObject)layout.DeepClone()) : null,
            Extensions = o.Where(m => !KnownMembers.Contains(m.Key) || (m.Key == "layout" && m.Value is not JsonObject))
                .Select(m => new KeyValuePair<string, JsonNode?>(m.Key, Materialize(m.Value?.DeepClone()))).ToList(),
        };
    }

    /// <summary>
    /// Reads every object and array under <paramref name="node"/> once, so a node parsed lazily from JSON is fully built
    /// before the document is shared: concurrent runs of one document then only read it.
    /// </summary>
    [return: NotNullIfNotNull(nameof(node))]
    internal static T? Materialize<T>(T? node) where T : JsonNode
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, child) in o) Materialize(child);
                break;
            case JsonArray a:
                foreach (var child in a) Materialize(child);
                break;
        }
        return node;
    }

    /// <summary>The document as JSON, properties in the order the format lists them.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["format"] = Format, ["id"] = Id, ["name"] = Name };
        if (Description is not null) o["description"] = Description;
        o["trigger"] = new JsonObject { ["accepts"] = new JsonArray(Trigger.Accepts.Select(a => (JsonNode?)a).ToArray()) };
        if (Input is not null) o["input"] = Input;
        o["settings"] = new JsonArray(Settings.Select(s =>
        {
            var j = new JsonObject { ["name"] = s.Name, ["type"] = s.Type };
            if (s.Label is not null) j["label"] = s.Label;
            if (s.Description is not null) j["description"] = s.Description;
            if (s.Default is not null) j["default"] = s.Default.DeepClone();
            return (JsonNode?)j;
        }).ToArray());
        o["requires"] = new JsonArray(Requires.Select(r => (JsonNode?)new JsonObject
        {
            ["name"] = r.Name, ["kind"] = r.Kind, ["actions"] = new JsonArray(r.Actions.Select(a => (JsonNode?)a).ToArray()),
        }).ToArray());
        o["variables"] = new JsonArray(Variables.Select(v =>
        {
            var j = new JsonObject { ["name"] = v.Name, ["type"] = v.Type, ["scope"] = v.Scope };
            if (v.Initial is not null) j["initial"] = v.Initial.DeepClone();
            return (JsonNode?)j;
        }).ToArray());
        if (Outputs.Count > 0)
            o["outputs"] = new JsonArray(Outputs.Select(v =>
            {
                var j = new JsonObject { ["name"] = v.Name, ["type"] = v.Type };
                if (v.Description is not null) j["description"] = v.Description;
                return (JsonNode?)j;
            }).ToArray());
        o["start"] = Start;
        o["steps"] = new JsonArray(Steps.Select(s => s.Json.DeepClone()).ToArray());
        if (Layout is not null) o["layout"] = Layout.DeepClone();
        foreach (var (name, value) in Extensions)
            if (!o.ContainsKey(name)) o[name] = value?.DeepClone();
        return o;
    }

    public string ToJsonString(bool indented = true) => ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    private static string Str(JsonObject o, string name, string where) =>
        OptStr(o, name, where) ?? throw new WorkflowDocumentException($"{where} has no '{name}'");

    private static string? OptStr(JsonObject o, string name, string where) => o[name] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => throw new WorkflowDocumentException($"{where}: '{name}' is not text"),
    };

    private static List<string> Strings(JsonNode? node, string where) => node switch
    {
        null => [],
        JsonArray a => a.Select((x, i) => x is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>() : throw new WorkflowDocumentException($"{where}[{i}] is not text")).ToList(),
        _ => throw new WorkflowDocumentException($"{where} is not a list of text"),
    };

    private static IEnumerable<JsonObject> Objects(JsonObject o, string name) => o[name] switch
    {
        null => [],
        JsonArray a => a.Select((x, i) => x as JsonObject ?? throw new WorkflowDocumentException($"{name}[{i}] is not an object")).ToList(),
        _ => throw new WorkflowDocumentException($"'{name}' is not a list"),
    };
}
