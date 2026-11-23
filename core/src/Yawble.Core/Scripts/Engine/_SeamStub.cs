// TEMPORARY (card 584): a minimal stub of the card 583 seam so Scripts/Library and Scripts/Running compile before
// DeveloperSamuel's Scripts/Language and Scripts/Engine are merged. DROP THIS FILE AT MERGE: the real types replace it.
// Names follow card 583's body exactly; where the card does not give a member's shape, the assumption is marked "assumed".
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Scripts.Language
{
    /// <summary>A parsed yawble-script/1 document.</summary>
    public sealed class ScriptDocument
    {
        private ScriptDocument(JsonObject json) => Json = json;

        /// <summary>assumed: the document as JSON.</summary>
        public JsonObject Json { get; }

        /// <summary>card 583: ScriptDocument.Parse(json). Throws on a document that is not a yawble-script/1 object (assumed: FormatException).</summary>
        public static ScriptDocument Parse(string json)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(json); }
            catch (JsonException e) { throw new FormatException("the document is not JSON: " + e.Message, e); }
            if (node is not JsonObject o) throw new FormatException("the document must be a JSON object");
            if (o["format"]?.GetValue<string>() != "yawble-script/1") throw new FormatException("format must be yawble-script/1");
            return new ScriptDocument(o);
        }
    }

    /// <summary>A named object type (JSON Schema subset).</summary>
    public sealed class ScriptSchema
    {
        private ScriptSchema(string name, JsonObject json) { Name = name; Json = json; }

        public string Name { get; }

        /// <summary>assumed: the JSON Schema document.</summary>
        public JsonObject Json { get; }

        /// <summary>assumed: builds a schema from its name and JSON Schema text.</summary>
        public static ScriptSchema Parse(string name, string json) =>
            new(name, JsonNode.Parse(json) as JsonObject ?? throw new FormatException("a schema must be a JSON object"));
    }

    public interface IScriptSchemaLookup
    {
        ScriptSchema? Find(string name);
    }

    /// <summary>assumed: the built-in read-only schemas (TriggerFired, FolderChange, EventItem, EmailMessage, EmailAddress, ApiResponse).</summary>
    public static class BuiltInSchemas
    {
        public static IReadOnlyList<string> Names { get; } = ["TriggerFired", "FolderChange", "EventItem", "EmailMessage", "EmailAddress", "ApiResponse"];

        public static IScriptSchemaLookup Lookup { get; } = new NamesOnly();

        private sealed class NamesOnly : IScriptSchemaLookup
        {
            public ScriptSchema? Find(string name) =>
                Names.Contains(name, StringComparer.Ordinal) ? ScriptSchema.Parse(name, """{"type":"object","properties":{}}""") : null;
        }
    }

    public sealed record ScriptCheckIssue(string? StepId, string Code, string Message);

    public sealed record ScriptSchemaReference(string Schema, string FieldPath, string? StepId);

    public sealed record ScriptCheckResult(bool Ok, IReadOnlyList<ScriptCheckIssue> Issues, IReadOnlyList<ScriptSchemaReference> SchemaReferences);

    public static class ScriptChecker
    {
        /// <summary>Stub: only checks that every requires action is in the catalog.</summary>
        public static ScriptCheckResult Check(ScriptDocument document, IScriptSchemaLookup schemas, Engine.IScriptActionCatalog catalog)
        {
            var issues = new List<ScriptCheckIssue>();
            foreach (var r in document.Json["requires"] as JsonArray ?? [])
            {
                var kind = r?["kind"]?.GetValue<string>() ?? "";
                foreach (var a in r?["actions"] as JsonArray ?? [])
                {
                    var action = a?.GetValue<string>() ?? "";
                    if (!catalog.HasAction(kind, action)) issues.Add(new ScriptCheckIssue(null, "unknown_action", $"no action {kind} {action}"));
                }
            }
            return new ScriptCheckResult(issues.Count == 0, issues, []);
        }
    }
}

namespace Yawble.Core.Scripts.Engine
{
    using Yawble.Core.Scripts.Language;

    public interface IScriptActionCatalog
    {
        bool HasAction(string kind, string action);
        bool IsChanging(string kind, string action);
    }

    public sealed record ScriptActionCall(string Use, string Kind, string Action, JsonObject Args, bool DryRun);

    public interface IScriptActions
    {
        Task<JsonNode?> InvokeAsync(ScriptActionCall call, CancellationToken ct);
    }

    /// <summary>assumed: JSON-serializable, opaque to the caller.</summary>
    public sealed record ScriptRunCheckpoint(string? NextStepId, JsonObject? State);

    public sealed record ScriptStepRecord(string StepId, string Type, JsonNode? Input, JsonNode? Output, string Status, DateTimeOffset StartedAt, DateTimeOffset EndedAt);

    public interface IScriptRunJournal
    {
        /// <summary>assumed parameters.</summary>
        Task StepStartedAsync(string stepId, string type, JsonNode? input);
        Task StepCompletedAsync(ScriptStepRecord rec, ScriptRunCheckpoint cp);
        Task LogAsync(string line);
    }

    /// <summary>assumed: init properties.</summary>
    public sealed class ScriptRunRequest
    {
        public required ScriptDocument Document { get; init; }
        public JsonNode? Input { get; init; }
        public JsonObject Settings { get; init; } = [];
        public JsonObject SavedVariables { get; init; } = [];
        public required IScriptActions Actions { get; init; }
        public required string DataFolder { get; init; }
        public IReadOnlyList<string> AllowedFolders { get; init; } = [];
        public bool DryRun { get; init; }
        public string? DryRunFolder { get; init; }
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
        public int StepBudget { get; init; } = 10_000;
        public int LoopCap { get; init; } = 10_000;
        public ScriptRunCheckpoint? ResumeFrom { get; init; }
    }

    /// <summary>assumed: Status is "succeeded" or "failed".</summary>
    public sealed record ScriptRunResult(string Status, string? FailedStepId, string? Reason, JsonObject? Outputs, JsonObject? SavedVariables);

    public static class ScriptEngine
    {
        public static Task<ScriptRunResult> RunAsync(ScriptRunRequest request, IScriptRunJournal journal, CancellationToken ct) =>
            Task.FromResult(new ScriptRunResult("failed", null, "the script engine is not in this build yet", null, null));
    }
}
