using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Scripts.Engine;

/// <summary>
/// One call of an integration action by an <c>integration</c> step: <see cref="Use"/> is the script's requirement name
/// (the member binds it to one of the team's integrations), <see cref="Kind"/> its kind, <see cref="Action"/> the action,
/// <see cref="Args"/> the evaluated arguments. <see cref="DryRun"/> is true in a dry run when the engine had no catalog to
/// tell whether the action changes something: the implementation must then not perform a changing action.
/// </summary>
public sealed record ScriptActionCall(string Use, string Kind, string Action, JsonObject Args, bool DryRun);

/// <summary>Performs integration actions for a script run (the integrations implement it; tests fake it).</summary>
public interface IScriptActions
{
    /// <summary>The action's result (e.g. a list of EmailMessage, an ApiResponse), or null. A failure throws; the step fails with its message.</summary>
    Task<JsonNode?> InvokeAsync(ScriptActionCall call, CancellationToken ct);
}

/// <summary>A folder the team granted, which a script's file steps may use besides the member's data folder.</summary>
public sealed record ScriptFolderGrant(string Path, bool CanWrite);

/// <summary>What a run does: the script, its input, the member's settings and saved variables, the actions, the folders and limits.</summary>
public sealed record ScriptRunRequest
{
    public required ScriptDocument Document { get; init; }

    /// <summary>The trigger's payload (TriggerFired, FolderChange, EventItem), a tell's <c>{text}</c>, or a test run's sample.</summary>
    public JsonNode? Input { get; init; }

    /// <summary>The member's settings by name; a missing one takes the script's default.</summary>
    public JsonObject? Settings { get; init; }

    /// <summary>The member's saved variables from its last successful run; a missing one takes its initial value.</summary>
    public JsonObject? SavedVariables { get; init; }

    public required IScriptActions Actions { get; init; }

    /// <summary>The catalog, so a dry run knows which actions change something. Without it the call is made with DryRun set.</summary>
    public IScriptActionCatalog? Catalog { get; init; }

    /// <summary>The library's schemas, so results and values are checked against their declared types (built-in ones always are).</summary>
    public IScriptSchemaLookup? Schemas { get; init; }

    /// <summary>The member's own data folder: relative file paths are under it.</summary>
    public required string DataFolder { get; init; }

    public IReadOnlyList<ScriptFolderGrant> AllowedFolders { get; init; } = [];

    public bool DryRun { get; init; }

    /// <summary>Where a dry run's file writes go; defaults to <c>dry-run</c> under the data folder.</summary>
    public string? DryRunFolder { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public int StepBudget { get; init; } = 10_000;

    /// <summary>The most items one forEach may loop over.</summary>
    public int LoopCap { get; init; } = 1_000;

    /// <summary>The last checkpoint of a run that was cut off; the run goes on from there.</summary>
    public ScriptRunCheckpoint? ResumeFrom { get; init; }

    /// <summary>The clock for the timeout and date functions (tests fake it); null is the system clock.</summary>
    public TimeProvider? Clock { get; init; }
}

public enum ScriptRunStatus
{
    Succeeded,
    Failed,
}

/// <summary>
/// How a run ended. <see cref="SavedVariables"/> is set only when it succeeded (a failed run must not move them);
/// <see cref="Outputs"/> holds what an <c>end</c> step set.
/// </summary>
public sealed record ScriptRunResult(
    ScriptRunStatus Status, string? FailedStepId, string? Reason, JsonObject? Outputs, JsonObject? SavedVariables)
{
    /// <summary>The run's log lines (log steps, dry-run "would ..." lines, the failure).</summary>
    public IReadOnlyList<string> Log { get; init; } = [];

    /// <summary>Steps run, counting those before a resume (the step budget counts the same).</summary>
    public int StepsRun { get; init; }
}

/// <summary>One step as it ran: its evaluated input, its output, <c>succeeded</c> or <c>failed</c> (then <see cref="Error"/>).</summary>
public sealed record ScriptStepRecord(
    string StepId, string Type, JsonNode? Input, JsonNode? Output, string Status, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    string? Error = null);

/// <summary>Where a run writes as it goes (the script_runs tables); every completion is written before the next step starts.</summary>
public interface IScriptRunJournal
{
    Task StepStartedAsync(string stepId, string type, JsonNode? input);

    /// <summary>
    /// A step finished. For a succeeded step <paramref name="cp"/> is the state after it; for a failed one it is the state
    /// before it (resuming from it runs the step again).
    /// </summary>
    Task StepCompletedAsync(ScriptStepRecord rec, ScriptRunCheckpoint cp);

    Task LogAsync(string line);
}

/// <summary>A journal that keeps nothing (a check-only caller, tests that do not look).</summary>
public sealed class NullScriptRunJournal : IScriptRunJournal
{
    public static readonly NullScriptRunJournal Instance = new();
    public Task StepStartedAsync(string stepId, string type, JsonNode? input) => Task.CompletedTask;
    public Task StepCompletedAsync(ScriptStepRecord rec, ScriptRunCheckpoint cp) => Task.CompletedTask;
    public Task LogAsync(string line) => Task.CompletedTask;
}

/// <summary>A forEach being run: its step, the item name, the items and the index of the current one.</summary>
public sealed class ScriptLoopFrame
{
    public required string StepId { get; init; }
    public required string As { get; init; }
    public required JsonArray Items { get; init; }
    public int Index { get; set; }

    public ScriptLoopFrame Clone() => new() { StepId = StepId, As = As, Items = (JsonArray)Items.DeepClone(), Index = Index };
}

/// <summary>
/// The whole state of a run between two steps, as JSON: the step to run next (null with no loops left: the run is
/// done), the loops it is inside, every variable, the settings and input it started with, and each step's result.
/// </summary>
public sealed class ScriptRunCheckpoint
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string? Next { get; init; }
    public List<ScriptLoopFrame> Frames { get; init; } = [];
    public JsonObject Vars { get; init; } = [];
    public JsonObject Settings { get; init; } = [];
    public JsonNode? Input { get; init; }
    public JsonObject Steps { get; init; } = [];
    public JsonObject? Outputs { get; init; }
    public int StepsRun { get; init; }

    /// <summary>An <c>end</c> step ran: nothing more to do.</summary>
    public bool Ended { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDone => Ended || (Next is null && Frames.Count == 0);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ScriptRunCheckpoint FromJson(string json) =>
        JsonSerializer.Deserialize<ScriptRunCheckpoint>(json, Json) ?? throw new FormatException("not a script run checkpoint");
}
