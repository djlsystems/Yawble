using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Workflows.Engine;

/// <summary>
/// One call of an integration action by an <c>integration</c> step: <see cref="Use"/> is the name the step uses (an
/// integration's real name, or an alias; the run's bindings say which integration it is), <see cref="Kind"/> its kind, <see cref="Action"/> the action,
/// <see cref="Args"/> the evaluated arguments. <see cref="DryRun"/> is true in a dry run when the engine had no catalog to
/// tell whether the action changes something: the implementation must then not perform a changing action.
/// </summary>
public sealed record WorkflowActionCall(string Use, string Kind, string Action, JsonObject Args, bool DryRun);

/// <summary>Performs integration actions for a workflow run (the integrations implement it; tests fake it).</summary>
public interface IWorkflowActions
{
    /// <summary>The action's result (e.g. a list of EmailMessage, an ApiResponse), or null. A failure throws; the step fails with its message.</summary>
    Task<JsonNode?> InvokeAsync(WorkflowActionCall call, CancellationToken ct);
}

/// <summary>
/// A <c>tell</c> step's work for an agent member: <see cref="Member"/> (the Manager by default), <see cref="Team"/> (null: the
/// workflow member's own team), the evaluated <see cref="Text"/>, whether the run waits, and the reply schema the agent is asked
/// to end with (its name and JSON Schema), if any.
/// </summary>
public sealed record WorkflowTellCall(string StepId, string Member, string? Team, string Text, bool Wait, string? ReplySchemaName, JsonObject? ReplySchema);

/// <summary>A tell that went: the deliverable it started, and the member and team it went to.</summary>
public sealed record WorkflowTellSent(long Deliverable, string Member, string Team);

/// <summary>
/// Gives a tell step's work to the team's agents (the bus, in the app; tests fake it). A refusal (no such agent member, a workflow
/// member, another team when that is off) throws <see cref="WorkflowActionException"/>: the step fails with its message.
/// </summary>
public interface IWorkflowTeller
{
    Task<WorkflowTellSent> TellAsync(WorkflowTellCall call, CancellationToken ct);
}

/// <summary>
/// What a run waits on, saved in its checkpoint: <see cref="Kind"/> (<c>deliverable</c> for a tell; a later step such as askPerson
/// adds its own), the step that waits, the deliverable (or other key), since when and until when (null: no timeout), and the step's own
/// details (<see cref="Data"/>, e.g. a tell's reply schema).
/// </summary>
public sealed class WorkflowWait
{
    public const string DeliverableKind = "deliverable";

    public required string Kind { get; init; }
    public required string StepId { get; init; }
    public long? Deliverable { get; init; }
    public string? Key { get; init; }
    public DateTimeOffset Since { get; init; }
    public DateTimeOffset? Until { get; init; }
    public JsonObject? Data { get; init; }
    public JsonNode? Input { get; init; }
}

/// <summary>
/// How a wait ended: <see cref="Outcome"/> (for a tell: done, failed, blocked or timedOut), the text that came with it (a tell's
/// closing summary, a blocked reason).
/// </summary>
public sealed record WorkflowWaitResult(string Outcome, string? Text = null);

/// <summary>A folder the team granted, which a workflow's file steps may use besides the member's data folder.</summary>
public sealed record WorkflowFolderGrant(string Path, bool CanWrite);

/// <summary>What a run does: the workflow, its input, the member's settings and saved variables, the actions, the folders and limits.</summary>
public sealed record WorkflowRunRequest
{
    public required WorkflowDocument Document { get; init; }

    /// <summary>The trigger's payload (TriggerFired, FolderChange, EventItem), a tell's <c>{text}</c>, or a test run's sample.</summary>
    public JsonNode? Input { get; init; }

    /// <summary>The member's settings by name; a missing one takes the workflow's default.</summary>
    public JsonObject? Settings { get; init; }

    /// <summary>The member's saved variables from its last successful run; a missing one takes its initial value.</summary>
    public JsonObject? SavedVariables { get; init; }

    public required IWorkflowActions Actions { get; init; }

    /// <summary>The catalog, so a dry run knows which actions change something. Without it the call is made with DryRun set.</summary>
    public IWorkflowActionCatalog? Catalog { get; init; }

    /// <summary>The library's schemas, so results and values are checked against their declared types (built-in ones always are).</summary>
    public IWorkflowSchemaLookup? Schemas { get; init; }

    /// <summary>The member's own data folder: relative file paths are under it.</summary>
    public required string DataFolder { get; init; }

    public IReadOnlyList<WorkflowFolderGrant> AllowedFolders { get; init; } = [];

    /// <summary>
    /// The run's team's Documents folder (what agents see as YAWBLE_TEAM_DOCS): <c>docs:/...</c> paths are under it, writable.
    /// Null: a docs:/ path fails its step.
    /// </summary>
    public string? TeamDocumentsFolder { get; init; }

    public bool DryRun { get; init; }

    /// <summary>Where a dry run's file writes go; defaults to <c>dry-run</c> under the data folder.</summary>
    public string? DryRunFolder { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public int StepBudget { get; init; } = 10_000;

    /// <summary>The most items one forEach may loop over, and the most passes one while may make (whatever its max).</summary>
    public int LoopCap { get; init; } = 1_000;

    /// <summary>The limits each code step runs under (time, memory, statements, call depth).</summary>
    public WorkflowCodeLimits CodeLimits { get; init; } = WorkflowCodeLimits.Default;

    /// <summary>The last checkpoint of a run that was cut off; the run goes on from there.</summary>
    public WorkflowRunCheckpoint? ResumeFrom { get; init; }

    /// <summary>The clock for the timeout and date functions (tests fake it); null is the system clock.</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>Who a tell step gives work to; null: a tell step fails (no team to tell). A dry run never calls it.</summary>
    public IWorkflowTeller? Teller { get; init; }

    /// <summary>Tells are not sent even when the run is not a dry run (a Test run): they log and give outcome dryRun.</summary>
    public bool DryTells { get; init; }

    /// <summary>The run's team, as the dry-run log names it ("would tell Manager of Support: ...").</summary>
    public string? Team { get; init; }

    /// <summary>The most tells the run may make; the next one fails the run.</summary>
    public int MaxTells { get; init; } = Language.WorkflowTell.DefaultMaxTells;

    /// <summary>
    /// A Test run's made-up replies for its waiting tells, by step id: text, or JSON (the reply's data). A dry run's waiting tell
    /// with one gets it as its reply instead of nothing.
    /// </summary>
    public JsonObject? TellReplies { get; init; }

    /// <summary>
    /// How the wait the checkpoint (<see cref="ResumeFrom"/>) is parked on ended. Null with a waiting checkpoint: the run goes on
    /// only when the wait's timeout has passed (outcome timedOut); otherwise it stays waiting.
    /// </summary>
    public WorkflowWaitResult? WaitResult { get; init; }
}

public enum WorkflowRunStatus
{
    Succeeded,
    Failed,

    /// <summary>The run is parked on <see cref="WorkflowRunResult.Wait"/> (a tell that waits); its checkpoint is saved at that step.</summary>
    Waiting,
}

/// <summary>
/// How a run ended. <see cref="SavedVariables"/> is set only when it succeeded (a failed run must not move them);
/// <see cref="Outputs"/> holds what an <c>end</c> step set.
/// </summary>
public sealed record WorkflowRunResult(
    WorkflowRunStatus Status, string? FailedStepId, string? Reason, JsonObject? Outputs, JsonObject? SavedVariables)
{
    /// <summary>The run's log lines (log steps, dry-run "would ..." lines, the failure).</summary>
    public IReadOnlyList<string> Log { get; init; } = [];

    /// <summary>Steps run, counting those before a resume (the step budget counts the same).</summary>
    public int StepsRun { get; init; }

    /// <summary>With <see cref="WorkflowRunStatus.Waiting"/>: what the run waits on (also in its checkpoint).</summary>
    public WorkflowWait? Wait { get; init; }
}

/// <summary>One step as it ran: its evaluated input, its output, <c>succeeded</c> or <c>failed</c> (then <see cref="Error"/>).</summary>
public sealed record WorkflowStepRecord(
    string StepId, string Type, JsonNode? Input, JsonNode? Output, string Status, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    string? Error = null);

/// <summary>Where a run writes as it goes (the workflow_runs tables); every completion is written before the next step starts.</summary>
public interface IWorkflowRunJournal
{
    Task StepStartedAsync(string stepId, string type, JsonNode? input);

    /// <summary>
    /// A step finished. For a succeeded step <paramref name="cp"/> is the state after it; for a failed one it is the state
    /// before it (resuming from it runs the step again).
    /// </summary>
    Task StepCompletedAsync(WorkflowStepRecord rec, WorkflowRunCheckpoint cp);

    Task LogAsync(string line);

    /// <summary>
    /// A step parks the run (status <c>waiting</c>): <paramref name="cp"/> holds the wait and is the state the run resumes from.
    /// The step's row stays open until it completes. By default the checkpoint goes through <see cref="StepCompletedAsync"/>.
    /// </summary>
    Task StepWaitingAsync(WorkflowStepRecord rec, WorkflowRunCheckpoint cp) => StepCompletedAsync(rec, cp);
}

/// <summary>A journal that keeps nothing (a check-only caller, tests that do not look).</summary>
public sealed class NullWorkflowRunJournal : IWorkflowRunJournal
{
    public static readonly NullWorkflowRunJournal Instance = new();
    public Task StepStartedAsync(string stepId, string type, JsonNode? input) => Task.CompletedTask;
    public Task StepCompletedAsync(WorkflowStepRecord rec, WorkflowRunCheckpoint cp) => Task.CompletedTask;
    public Task LogAsync(string line) => Task.CompletedTask;
}

/// <summary>
/// A loop being run: its step. For a forEach, the item name, the items and the index of the current one. For a while
/// (<see cref="IsWhile"/>), no items: <see cref="Index"/> is the passes it has finished, and the end of a pass goes back to the
/// while step, which checks its condition again.
/// </summary>
public sealed class WorkflowLoopFrame
{
    public required string StepId { get; init; }
    public string? As { get; init; }
    public JsonArray Items { get; init; } = [];
    public int Index { get; set; }
    public bool IsWhile { get; init; }

    public WorkflowLoopFrame Clone() => new() { StepId = StepId, As = As, Items = (JsonArray)Items.DeepClone(), Index = Index, IsWhile = IsWhile };
}

/// <summary>
/// The whole state of a run between two steps, as JSON: the step to run next (null with no loops left: the run is
/// done), the loops it is inside, every variable, the settings and input it started with, and each step's result.
/// </summary>
public sealed class WorkflowRunCheckpoint
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string? Next { get; init; }
    public List<WorkflowLoopFrame> Frames { get; init; } = [];
    public JsonObject Vars { get; init; } = [];
    public JsonObject Settings { get; init; } = [];
    public JsonNode? Input { get; init; }
    public JsonObject Steps { get; init; } = [];
    public JsonObject? Outputs { get; init; }
    public int StepsRun { get; init; }

    /// <summary>An <c>end</c> step ran: nothing more to do.</summary>
    public bool Ended { get; init; }

    /// <summary>The tells the run has made (the member's maxTells caps them).</summary>
    public int Tells { get; init; }

    /// <summary>
    /// How long the run has been going, in milliseconds, without the time it waited: the run timeout counts this, so the clock
    /// stops while a run waits (and while the app is closed).
    /// </summary>
    public long ActiveMs { get; init; }

    /// <summary>What the run is parked on, at step <see cref="WorkflowWait.StepId"/> (<see cref="Next"/> is that step); null when it is not waiting.</summary>
    public WorkflowWait? Wait { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDone => Ended || (Next is null && Frames.Count == 0);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static WorkflowRunCheckpoint FromJson(string json) =>
        JsonSerializer.Deserialize<WorkflowRunCheckpoint>(json, Json) ?? throw new FormatException("not a workflow run checkpoint");
}
