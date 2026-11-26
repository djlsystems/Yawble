using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;
using Yawble.Core.Workflows.Library;
using Yawble.Core.Workflows.Running;

namespace Yawble.Core.Tests.Workflows;

/// <summary>
/// Fakes of the workflow engine seam for the library and running tests: a catalog, a checker and an engine. Every
/// place that builds or reads a seam type (checkpoint, result) is here, so the merge with the real engine changes only this file.
/// </summary>
internal static class Seam
{
    public static WorkflowRunCheckpoint Checkpoint(string? next) => new() { Next = next };

    /// <summary>The step a checkpoint resumes at.</summary>
    public static string? NextOf(WorkflowRunCheckpoint cp) => cp.Next;

    public static WorkflowRunResult Succeeded(JsonObject? saved, JsonObject? outputs = null) => new(Yawble.Core.Workflows.Engine.WorkflowRunStatus.Succeeded, null, null, outputs, saved);

    public static WorkflowRunResult Failed(string? step, string reason) => new(Yawble.Core.Workflows.Engine.WorkflowRunStatus.Failed, step, reason, null, null);
}

internal sealed class FakeActionCatalog(params (string Kind, string Action)[] actions) : IWorkflowActionCatalog, IWorkflowActionsProvider, IWorkflowActions
{
    public List<WorkflowCaller> Callers { get; } = [];
    public bool HasAction(string kind, string action) => actions.Contains((kind, action));
    public bool IsChanging(string kind, string action) => action != "list";
    public IWorkflowActionCatalog Catalog => this;

    public IWorkflowActions For(WorkflowCaller caller)
    {
        lock (Callers) Callers.Add(caller);
        return this;
    }

    public Task<JsonNode?> InvokeAsync(WorkflowActionCall call, CancellationToken ct) => Task.FromResult<JsonNode?>(new JsonObject { ["status"] = 200 });
}

/// <summary>
/// The checker fake: a document is clean when every requires action is in the catalog; its schema references are what
/// the test document lists under "_refs" ([{ schema, fieldPath, stepId }]).
/// </summary>
internal static class FakeChecker
{
    public static readonly WorkflowCheck Check = (document, schemas, catalog) =>
    {
        var json = JsonNode.Parse(DocumentJson(document)) as JsonObject ?? [];
        var issues = new List<WorkflowCheckIssue>();
        foreach (var r in json["requires"] as JsonArray ?? [])
            foreach (var a in r?["actions"] as JsonArray ?? [])
                if (!catalog.HasAction(r!["kind"]!.GetValue<string>(), a!.GetValue<string>()))
                    issues.Add(new WorkflowCheckIssue(null, "unknown_action", $"no action {a}"));
        var refs = (json["_refs"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => new WorkflowSchemaReference(o["schema"]!.GetValue<string>(), o["fieldPath"]!.GetValue<string>(), o["stepId"]?.GetValue<string>()))
            .ToList();
        foreach (var x in refs)
            if (schemas.Find(x.Schema) is null) issues.Add(new WorkflowCheckIssue(x.StepId, "unknown_schema", $"no schema {x.Schema}"));
        return new WorkflowCheckResult(issues, refs);
    };

    private static string DocumentJson(WorkflowDocument d) => d.ToJsonString(indented: false);
}

/// <summary>
/// The engine fake: walks the document's steps in order (or from the checkpoint it is resumed at), journaling each one,
/// and succeeds with the saved variables plus runs = runs + 1. <see cref="FailAt"/> fails at a step; <see cref="Gate"/>
/// holds the run before a step until released.
/// </summary>
internal sealed class FakeEngine : IWorkflowRunEngine
{
    public List<WorkflowRunRequest> Requests { get; } = [];
    public string? FailAt { get; set; }
    public string? HoldAt { get; set; }
    public TaskCompletionSource Gate { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Holding { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _held;

    /// <summary>How many runs are waiting at <see cref="HoldAt"/> now.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>Whether runs also keep lastSeen = &lt;folder setting&gt;:&lt;runs&gt; (the per-member state test).</summary>
    public bool KeepsLastSeen { get; set; }

    public async Task<WorkflowRunResult> RunAsync(WorkflowRunRequest request, IWorkflowRunJournal journal, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        var mine = request.Settings?["folder"]?.GetValue<string>() ?? "";
        var json = JsonNode.Parse(FakeCheckerJson(request.Document)) as JsonObject ?? [];
        var steps = (json["steps"] as JsonArray ?? []).OfType<JsonObject>().Select(s => s["id"]!.GetValue<string>()).ToList();
        var start = request.ResumeFrom is { } cp && Seam.NextOf(cp) is { } next ? steps.IndexOf(next) : 0;
        for (var i = Math.Max(0, start); i < steps.Count; i++)
        {
            var id = steps[i];
            if (HoldAt == id)
            {
                Interlocked.Increment(ref _held);
                Holding.TrySetResult();
                try { await Gate.Task.WaitAsync(ct); }
                finally { Interlocked.Decrement(ref _held); }
            }
            var at = DateTimeOffset.UtcNow;
            await journal.StepStartedAsync(id, "set", new JsonObject { ["step"] = id });
            if (FailAt == id) return Seam.Failed(id, $"step {id} failed");
            await journal.StepCompletedAsync(new WorkflowStepRecord(id, "set", new JsonObject { ["step"] = id }, new JsonObject { ["done"] = id }, "succeeded", at, DateTimeOffset.UtcNow),
                Seam.Checkpoint(i + 1 < steps.Count ? steps[i + 1] : null));
            await journal.LogAsync($"did {id}");
        }
        var saved = (JsonObject?)request.SavedVariables?.DeepClone() ?? [];
        saved["runs"] = (saved["runs"]?.GetValue<int>() ?? 0) + 1;
        // What a poller keeps: its own lastSeen, and a line in its own data folder's run.log.
        if (KeepsLastSeen) saved["lastSeen"] = $"{mine}:{saved["runs"]}";
        Directory.CreateDirectory(request.DataFolder);
        await File.AppendAllTextAsync(Path.Combine(request.DataFolder, "run.log"), $"{mine} {DocDescription(request.Document)}\n", ct);
        return Seam.Succeeded(saved, new JsonObject { ["ok"] = true });
    }

    /// <summary>The document's description (the tests mark versions with it).</summary>
    public static string DocDescription(WorkflowDocument d) =>
        (JsonNode.Parse(FakeCheckerJson(d)) as JsonObject)?["description"]?.GetValue<string>() ?? "";

    private static string FakeCheckerJson(WorkflowDocument d) => d.ToJsonString(indented: false);
}

internal sealed class FakeDecisions : IWorkflowDecisions
{
    public List<(long MemberId, string Summary)> Opened { get; } = [];

    public Task<long?> OpenFailureDecisionAsync(long memberId, string summary, string details, CancellationToken ct)
    {
        lock (Opened) Opened.Add((memberId, summary));
        return Task.FromResult<long?>(1000 + Opened.Count);
    }
}

internal static class Docs
{
    /// <summary>A small clean workflow: three steps, a saved variable, one setting with a default.</summary>
    public static string Clean(string id = "poller", string name = "Poller", JsonArray? refs = null, JsonArray? requires = null,
        string description = "Polls and logs.")
    {
        var o = new JsonObject
        {
            ["format"] = "yawble-workflow/1",
            ["id"] = id,
            ["name"] = name,
            ["description"] = description,
            ["trigger"] = new JsonObject { ["accepts"] = new JsonArray("every", "cron", "once") },
            ["settings"] = new JsonArray(new JsonObject { ["name"] = "folder", ["type"] = "string", ["label"] = "Folder", ["default"] = "INBOX" }),
            ["requires"] = requires ?? [],
            ["variables"] = new JsonArray(new JsonObject { ["name"] = "runs", ["type"] = "int", ["scope"] = "saved" }),
            ["start"] = "a",
            ["steps"] = new JsonArray(
                new JsonObject { ["id"] = "a", ["type"] = "set", ["var"] = "x", ["value"] = 1, ["next"] = "b" },
                new JsonObject { ["id"] = "b", ["type"] = "log", ["text"] = "hello", ["next"] = "c" },
                new JsonObject { ["id"] = "c", ["type"] = "end" }),
        };
        if (refs is not null) o["_refs"] = refs;
        return o.ToJsonString();
    }

    /// <summary>A draft: it requires an action no catalog has.</summary>
    public static string Draft(string id = "poller") =>
        Clean(id, requires: new JsonArray(new JsonObject { ["name"] = "mail", ["kind"] = "email", ["actions"] = new JsonArray("teleport") }));
}
