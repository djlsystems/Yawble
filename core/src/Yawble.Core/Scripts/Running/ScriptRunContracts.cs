using System.Text.Json.Nodes;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Scripts.Running;

/// <summary>
/// Who a script run's integration calls are for: the team, the member (null for a Test run), the run, and the bindings the
/// calls go through: each requires name to the integration that runs (itself for a real name with no override).
/// </summary>
public sealed record ScriptCaller(long TeamId, string Team, long? MemberId, string? Member, long RunId, IReadOnlyDictionary<string, string> Bindings, bool DryRun);

/// <summary>The script engine's integration actions: the catalog a script is checked against, and the calls for one run (SC-2 fills it).</summary>
public interface IScriptActionsProvider
{
    IScriptActionCatalog Catalog { get; }
    IScriptActions For(ScriptCaller caller);
}

/// <summary>Until SC-2: no integration action exists, so a script that requires one is a draft and an integration step fails.</summary>
public sealed class NoScriptActions : IScriptActionsProvider, IScriptActionCatalog, IScriptActions
{
    public static readonly NoScriptActions Instance = new();

    public IScriptActionCatalog Catalog => this;
    public IScriptActions For(ScriptCaller caller) => this;
    public bool HasAction(string kind, string action) => false;
    public bool IsChanging(string kind, string action) => true;

    public Task<JsonNode?> InvokeAsync(ScriptActionCall call, CancellationToken ct) =>
        throw new InvalidOperationException($"no {call.Kind} action '{call.Action}' is available to scripts yet");
}

/// <summary>The engine a run goes through: <see cref="SeamScriptEngine"/> (ScriptEngine) in the app; a fake in tests.</summary>
public interface IScriptRunEngine
{
    Task<ScriptRunResult> RunAsync(ScriptRunRequest request, IScriptRunJournal journal, CancellationToken ct);
}

public sealed class SeamScriptEngine : IScriptRunEngine
{
    public static readonly SeamScriptEngine Instance = new();

    public Task<ScriptRunResult> RunAsync(ScriptRunRequest request, IScriptRunJournal journal, CancellationToken ct) =>
        ScriptEngine.RunAsync(request, journal, ct);
}

/// <summary>The tell steps of a member's runs (the bus, in the app: <see cref="BusScriptTells"/>; tests fake it).</summary>
public interface IScriptTellers
{
    /// <summary>The teller for one run of <paramref name="script"/> (its key) by <paramref name="caller"/>.</summary>
    IScriptTeller For(ScriptCaller caller, string script);
}

/// <summary>
/// Tells when a run's wait of one <see cref="Kind"/> has ended (a tell's workflow: <see cref="ScriptWait.WorkflowKind"/>; a later
/// step that waits, such as askPerson, adds its own source). The runner asks each waiting run's source on its sweep.
/// </summary>
public interface IScriptWaitSource
{
    string Kind { get; }

    /// <summary>How the wait ended, or null while it goes on (the runner handles the wait's timeout itself).</summary>
    Task<ScriptWaitResult?> CheckAsync(ScriptWait wait, CancellationToken ct);
}

/// <summary>Opens the one decision card for a member whose runs failed three times in a row (the bus, in the app).</summary>
public interface IScriptDecisions
{
    /// <summary>The decision's message seq, or null when it could not be opened.</summary>
    Task<long?> OpenFailureDecisionAsync(long memberId, string summary, string details, CancellationToken ct);
}

/// <summary>The structured trigger payloads a script member gets as its run's input (design section 6).</summary>
public static class ScriptPayloads
{
    /// <summary>every / cron / once: TriggerFired { triggerId (a string, as the built-in schema declares), kind, firedAt }.</summary>
    public static JsonObject TriggerFired(long triggerId, string kind, DateTimeOffset firedAt) => new()
    {
        ["triggerId"] = triggerId.ToString(System.Globalization.CultureInfo.InvariantCulture), // TriggerFired declares it a string
        ["kind"] = kind,
        ["firedAt"] = firedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    };

    /// <summary>folder: FolderChange { root, added, removed, changed }.</summary>
    public static JsonObject FolderChange(string root, IEnumerable<string> added, IEnumerable<string> removed, IEnumerable<string> changed) => new()
    {
        ["root"] = root,
        ["added"] = new JsonArray(added.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        ["removed"] = new JsonArray(removed.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        ["changed"] = new JsonArray(changed.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
    };

    /// <summary>event: EventItem { event, item } (the watch's JSON item, unflattened).</summary>
    public static JsonObject EventItem(string name, JsonObject item) => new() { ["event"] = name, ["item"] = item.DeepClone() };

    /// <summary>A tell to the member: { text }.</summary>
    public static JsonObject Tell(string text) => new() { ["text"] = text };
}

/// <summary>What starting a member's run did: started (RunId), or skipped because a run was going (Reason).</summary>
public sealed record ScriptFireOutcome(bool Started, long? RunId, string? Reason);

/// <summary>A run that ended (or was skipped): its row, steps and log.</summary>
public sealed record ScriptRunOutcome(ScriptRunRow Run, IReadOnlyList<ScriptStepRow> Steps, IReadOnlyList<ScriptLogLine> Log)
{
    public bool Succeeded => Run.Status == ScriptRunStatus.Succeeded;
}

/// <summary>
/// scripts.testRun: a version of a script (null: the latest, a draft too) on a sample input, dry run by default. A Test run never
/// sends a tell; <paramref name="TellReplies"/> gives its waiting tells made-up replies by step id (text or JSON).
/// </summary>
public sealed record ScriptTestRun(long ScriptId, int? Version, JsonNode? Input, JsonObject? Settings, JsonObject? Bindings, long TeamId, bool DryRun = true,
    JsonObject? TellReplies = null);

/// <summary>A run that could not start: the script has no runnable version, the member is gone, ...</summary>
public sealed class ScriptRunRefused(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
