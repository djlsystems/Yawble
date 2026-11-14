using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;

namespace Yawble.Core.Tasks;

/// <summary>
/// The two surfaces of host tasks. The person's: <c>tasks.*</c> RPCs, which only the person's
/// window or an authenticated Cloud Link viewer may call - an agent, the Concierge, a trigger or the host itself is refused
/// (the Concierge only drafts; the dialog is the control). The agent's: exactly two tools, <c>task_list</c> and
/// <c>task_run(name, reason)</c>, offered to a team that has at least one task.
/// </summary>
public sealed class HostTaskRpc(HostTaskService tasks, Func<long, IReadOnlyList<string>> repoNames)
{
    public static bool Handles(string method) => method.StartsWith("tasks.", StringComparison.Ordinal);

    public HostTaskService Service => tasks;

    // ------------------------------------------------------------------ the person's RPCs

    /// <summary>Null when the current actor may write host tasks; otherwise why not.</summary>
    public static string? WriteRefusal(Actor? actor) =>
        actor is { Via: Actor.App or Actor.Cloud } ? null
            : $"host tasks can only be defined, approved and run by the person (through the app's window), not by {(actor?.Via is { } via ? "an actor of kind '" + via + "'" : "an unidentified caller")}";

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        if (WriteRefusal(Actor.Current) is { } refusal) throw new RpcException("forbidden", refusal);
        try
        {
            return method switch
            {
                "tasks.list" => await ListAsync(Long(p, "teamId"), ct),
                "tasks.preview" => await PreviewAsync(Long(p, "teamId"), Def(p), ct),
                "tasks.save" => await SaveAsync(Long(p, "teamId"), Def(p), ct),
                "tasks.delete" => Then(() => tasks.Delete(Long(p, "teamId"), Str(p, "name"))),
                "tasks.revoke" => Then(() => tasks.RevokeApproval(Long(p, "teamId"), Str(p, "name"))),
                "tasks.runNow" => Result(await tasks.RunNowAsync(Long(p, "teamId"), Str(p, "name"), ct)),
                "tasks.cancel" => new JsonObject { ["cancelled"] = tasks.Cancel(Long(p, "runId")) },
                "tasks.asks" => Asks(Opt(p, "teamId")),
                "tasks.approve" => Result(await tasks.ApproveAsync(Long(p, "id"), p?["always"]?.GetValue<bool>() == true, ct)),
                "tasks.decline" => await DeclineAsync(Long(p, "id"), p?["note"]?.GetValue<string>(), ct),
                "tasks.runs" => Runs(Opt(p, "teamId"), (int)(Opt(p, "limit") ?? 50)),
                _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
            };
        }
        catch (ArgumentException e)
        {
            throw new RpcException("bad_params", e.Message);
        }
    }

    private static JsonNode Then(Action action)
    {
        action();
        return new JsonObject { ["ok"] = true };
    }

    private async Task<JsonNode?> DeclineAsync(long id, string? note, CancellationToken ct)
    {
        await tasks.DeclineAsync(id, note, ct);
        return new JsonObject { ["ok"] = true };
    }

    public static JsonObject Result(TaskRequestResult r) => new()
    {
        ["kind"] = r.Kind.ToString().ToLowerInvariant(), ["text"] = r.Text, ["askId"] = r.AskId, ["runId"] = r.RunId, ["precondition"] = r.Precondition,
    };

    /// <summary>
    /// Null when the current actor may save <paramref name="def"/> over <paramref name="existing"/>; otherwise why not. A Cloud Link viewer may
    /// not define or change a task's environment from Connections (Connections are seen and changed on the computer itself): adding,
    /// removing or re-pointing a binding is refused; a save that keeps the bindings exactly as they are is not.
    /// </summary>
    public static string? BindingRefusal(Actor? actor, HostTaskDef def, HostTaskDef? existing)
    {
        if (actor?.Via != Actor.Cloud) return null;
        static IEnumerable<(string, string, string)> Keys(IEnumerable<EnvBinding> list) =>
            list.Select(b => (b.Name, b.ConnectionId, b.Field)).OrderBy(k => k.Name, StringComparer.Ordinal);
        return Keys(def.EnvFromConnections).SequenceEqual(Keys(existing?.EnvFromConnections ?? []))
            ? null
            : "a host task's environment from a Connection can only be defined or changed on the computer itself";
    }

    private async Task<JsonNode> SaveAsync(long teamId, HostTaskDef def, CancellationToken ct)
    {
        if (BindingRefusal(Actor.Current, def, tasks.Store.Task(teamId, def.Name)?.Def) is { } refused) throw new RpcException("forbidden", refused);
        var saved = await tasks.SaveAsync(teamId, def, ct);
        return new JsonObject { ["ok"] = true, ["id"] = saved.Id };
    }

    private async Task<JsonNode> PreviewAsync(long teamId, HostTaskDef def, CancellationToken ct)
    {
        // A Cloud Link preview with new bindings is refused like a save: its errors would name the computer's Connections.
        if (BindingRefusal(Actor.Current, def, tasks.Store.Task(teamId, def.Name)?.Def) is { } refused) throw new RpcException("forbidden", refused);
        var rules = Validation(def);
        var pv = rules is null ? await tasks.PreviewAsync(teamId, def, ct) : null;
        return new JsonObject
        {
            ["problem"] = rules ?? pv!.Refusal?.Message,
            ["programResolved"] = pv?.ProgramResolved, ["commit"] = pv?.Commit,
            ["pinned"] = new JsonArray((pv?.Pins?.Files ?? []).Select(f => (JsonNode?)f.Path).ToArray()),
            ["warnings"] = new JsonArray((pv?.Pins?.Warnings ?? []).Select(w => (JsonNode?)w).ToArray()),
            ["commandLineOnly"] = pv?.Pins?.CommandLineOnly ?? false,
        };
    }

    private string? Validation(HostTaskDef def)
    {
        try { HostTaskRules.Validate(def); HostTaskRules.ValidateBindings(def, tasks.Connections); return null; }
        catch (ArgumentException e) { return e.Message; }
    }

    private async Task<JsonNode> ListAsync(long teamId, CancellationToken ct)
    {
        var store = tasks.Store;
        var list = new JsonArray();
        foreach (var t in store.Tasks(teamId))
        {
            var approval = store.Approval(t.Id);
            var preview = await tasks.PreviewAsync(teamId, t.Def, ct);
            var last = store.Runs(t.Id, 1).FirstOrDefault();
            list.Add(new JsonObject
            {
                ["id"] = t.Id, ["def"] = DefJson(t.Def), ["commandLine"] = CommandLine(t.Def),
                ["envFromConnectionsShown"] = Arr(t.Def.EnvFromConnections.Select(b => b.Shown)),
                ["approval"] = await tasks.ApprovalStateAsync(t, ct),
                ["approvedAt"] = approval?.ApprovedAt, ["approvedCommit"] = approval?.CommitSha, ["approvedBy"] = approval?.ApprovedBy,
                ["programResolved"] = preview.ProgramResolved, ["commit"] = preview.Commit,
                ["pinned"] = new JsonArray((preview.Pins?.Files ?? []).Select(f => (JsonNode?)f.Path).ToArray()),
                ["warnings"] = new JsonArray((preview.Pins?.Warnings ?? []).Select(w => (JsonNode?)w).ToArray()),
                ["problem"] = preview.Refusal?.Message,
                ["runningRunId"] = store.RunningRun(t.Id)?.Id, ["pendingAskId"] = store.PendingAsk(t.Id)?.Id,
                ["lastRun"] = last is null ? null : RunJson(last),
                ["updatedAt"] = t.UpdatedAt, ["updatedBy"] = t.UpdatedBy,
            });
        }
        return new JsonObject { ["tasks"] = list, ["repos"] = new JsonArray(repoNames(teamId).Select(n => (JsonNode?)n).ToArray()) };
    }

    public static string CommandLine(HostTaskDef d) => string.Join(' ', new[] { d.Program }.Concat(d.Args).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    private static JsonObject DefJson(HostTaskDef d) => new()
    {
        ["name"] = d.Name, ["repo"] = d.Repo, ["program"] = d.Program, ["args"] = Arr(d.Args), ["workdir"] = d.Workdir, ["timeoutSeconds"] = d.TimeoutSeconds,
        ["envNames"] = Arr(d.EnvNames), ["watch"] = Arr(d.Watch), ["pinMode"] = d.PinMode, ["commandLineOnly"] = d.CommandLineOnly,
        ["envFromConnections"] = new JsonArray(d.EnvFromConnections.Select(b => (JsonNode?)new JsonObject
        {
            ["name"] = b.Name, ["connectionId"] = b.ConnectionId, ["field"] = b.Field, ["connectionName"] = b.ConnectionName,
        }).ToArray()),
    };

    private static JsonArray Arr(IEnumerable<string> s) => new(s.Select(x => (JsonNode?)x).ToArray());

    public static JsonObject RunJson(TaskRunRow r) => new()
    {
        ["id"] = r.Id, ["task"] = r.TaskName, ["teamId"] = r.TeamId, ["state"] = r.State, ["startedAt"] = r.StartedAt, ["endedAt"] = r.EndedAt, ["commit"] = r.CommitSha,
        ["headAfter"] = r.HeadAfter, ["definitionHash"] = Short(r.DefinitionHash), ["filesHash"] = Short(r.FilesHash), ["program"] = r.ProgramResolved, ["exitCode"] = r.ExitCode,
        ["timedOut"] = r.TimedOut, ["killedBy"] = r.KilledBy, ["outputBytes"] = r.OutputBytes, ["truncated"] = r.Truncated, ["logPath"] = r.LogPath,
        ["dirtyAfter"] = r.DirtyAfter, ["askId"] = r.AskId, ["requestedBy"] = r.RequestedBy, ["missingEnv"] = r.MissingEnv,
    };

    private static string? Short(string? hash) => hash is { Length: > 12 } ? hash[..12] : hash;

    public JsonNode Runs(long? teamId, int limit)
    {
        var rows = tasks.Store.RecentRuns(teamId, Math.Clamp(limit, 1, 200));
        return new JsonArray(rows.Select(r => (JsonNode?)RunJson(r)).ToArray());
    }

    /// <summary>Pending asks with what the person is shown (4.3): the command, the base commit, the pinned files, and what changed.</summary>
    public JsonArray Asks(long? teamId) => new(tasks.Store.PendingAsks(teamId).Select(AskJson).ToArray());

    public JsonNode? AskJson(TaskAskRow a) => AskJsonCore(a);

    private JsonObject AskJsonCore(TaskAskRow a)
    {
        JsonNode? detail = null;
        if (a.DetailJson is not null) { try { detail = JsonNode.Parse(a.DetailJson); } catch (JsonException) { } }
        return new JsonObject
        {
            ["id"] = a.Id, ["at"] = a.At, ["integration"] = "task:" + a.TaskName, ["task"] = true, ["taskName"] = a.TaskName, ["teamId"] = a.TeamId, ["member"] = a.Member,
            ["command"] = $"task {a.TaskName}", ["reason"] = a.Reason, ["workflow"] = a.Workflow, ["commit"] = a.CommitSha, ["detail"] = detail,
        };
    }

    private static HostTaskDef Def(JsonObject? p)
    {
        var d = p?["def"] as JsonObject ?? throw new ArgumentException("def is required");
        static string[] List(JsonNode? n) => n is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToArray() : [];
        return new HostTaskDef(d["name"]?.GetValue<string>()?.Trim() ?? "", d["repo"]?.GetValue<string>()?.Trim() ?? "main", d["program"]?.GetValue<string>()?.Trim() ?? "",
            List(d["args"]), d["workdir"]?.GetValue<string>()?.Trim() is { Length: > 0 } w ? w : ".", (int)(Opt(d, "timeoutSeconds") ?? 600),
            List(d["envNames"]).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray(), List(d["watch"]).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray(),
            d["pinMode"]?.GetValue<string>() ?? PinModes.Files, d["commandLineOnly"]?.GetValue<bool>() ?? false)
        {
            EnvFromConnections = Bindings(d["envFromConnections"]),
        };
    }

    /// <summary>envFromConnections: [{name, connectionId, field, connectionName}]; connectionId may come as a number or a string. A value is never accepted.</summary>
    private static EnvBinding[] Bindings(JsonNode? n)
    {
        if (n is null) return [];
        if (n is not JsonArray a) throw new ArgumentException("envFromConnections is a list of {name, connectionId, field}");
        static string Text(JsonNode? v) => v is JsonValue jv
            ? jv.TryGetValue<string>(out var s) ? s.Trim() : jv.TryGetValue<long>(out var l) ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""
            : "";
        return a.Select(x => x is JsonObject o
                ? o.ContainsKey("value") ? throw new ArgumentException("a binding names a Connection's field, never a value")
                : new EnvBinding(Text(o["name"]), Text(o["connectionId"]), Text(o["field"]), Text(o["connectionName"]))
                : throw new ArgumentException("envFromConnections is a list of {name, connectionId, field}"))
            .Where(b => b.Name.Length > 0 || b.ConnectionId.Length > 0 || b.Field.Length > 0).ToArray();
    }

    private static long? Opt(JsonObject? p, string name) =>
        p?[name] is JsonValue v && (v.TryGetValue<long>(out var l) || (v.TryGetValue<int>(out var i) && (l = i) == i) || (v.TryGetValue<string>(out var s) && long.TryParse(s, out l))) ? l : null;

    private static long Long(JsonObject? p, string name) => Opt(p, name) ?? throw new ArgumentException($"{name} is required");

    private static string Str(JsonObject? p, string name) =>
        p?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : throw new ArgumentException($"{name} is required");

    // ------------------------------------------------------------------ the agent's tools

    public const string RunTool = "task_run", ListTool = "task_list";

    public static bool IsTool(string name) => name is RunTool or ListTool;

    /// <summary>The two tools, when the team has at least one task. <c>task_run</c> has exactly <c>name</c> and <c>reason</c>.</summary>
    public IReadOnlyList<JsonObject> ToolsFor(long teamId)
    {
        if (tasks.Store.Tasks(teamId).Count == 0) return [];
        return
        [
            Tool(ListTool, "The person's host tasks for your team (programs they defined, such as the e2e tests): name, command line, approval, state and the last run. Nothing runs."),
            Tool(RunTool, "Ask for one of the person's host tasks to run on their computer (outside your sandbox, on their merged base branch). You name the task and give a reason; " +
                          "you cannot give arguments or environment. It may ask the person first. You get a one-line answer at once and are woken with the result (exit code, duration, output tail, log path): end your turn instead of waiting.",
                ("name", "string", "The task's name (see task_list).", true),
                ("reason", "string", "Why you need it, in one line (shown to the person, as your words).", true)),
        ];
    }

    private static JsonObject Tool(string name, string description, params (string Name, string Type, string Description, bool Required)[] parameters)
    {
        var properties = new JsonObject();
        foreach (var p in parameters) properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
        return new JsonObject
        {
            ["name"] = name, ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = properties,
                ["required"] = new JsonArray(parameters.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
                ["additionalProperties"] = false,
            },
        };
    }

    public async Task<string> CallToolAsync(RunContext run, string tool, JsonObject args, CancellationToken ct)
    {
        var teamId = run.Member.TeamId;
        if (tool == ListTool) return TaskList(teamId, await ListStatesAsync(teamId, ct));
        if (args.Select(kv => kv.Key).FirstOrDefault(k => k is not ("name" or "reason")) is { } extra)
            throw new ArgumentException($"task_run takes only 'name' and 'reason', not '{extra}': the person defined the program, its arguments and its environment");
        var name = args["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : null;
        var reason = args["reason"] is JsonValue rv && rv.TryGetValue<string>(out var r) ? r : null;
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("'name' is required (see task_list)");
        var result = await tasks.RequestAsync(teamId, name, reason ?? "", run.Member.Qualified, run.Correlation, run.Causation, ct);
        return result.Text;
    }

    private async Task<IReadOnlyList<JsonObject>> ListStatesAsync(long teamId, CancellationToken ct)
    {
        var store = tasks.Store;
        var rows = new List<JsonObject>();
        foreach (var t in store.Tasks(teamId))
        {
            var last = store.Runs(t.Id, 1).FirstOrDefault();
            rows.Add(new JsonObject
            {
                ["name"] = t.Def.Name, ["repo"] = t.Def.Repo, ["command"] = CommandLine(t.Def), ["workdir"] = t.Def.Workdir, ["timeout_s"] = t.Def.TimeoutSeconds,
                ["env_names"] = Arr(t.Def.EnvNames), ["env_from_connections"] = Arr(t.Def.EnvFromConnections.Select(b => b.Name)), ["approval"] = await tasks.ApprovalStateAsync(t, ct),
                ["state"] = store.RunningRun(t.Id) is not null ? "running" : store.PendingAsk(t.Id) is not null ? "waiting-for-person" : "idle",
                ["last_run"] = last is null ? null : new JsonObject { ["run"] = last.Id, ["exit"] = last.ExitCode, ["ended"] = last.EndedAt, ["commit"] = last.CommitSha, ["log"] = last.LogPath },
            });
        }
        return rows;
    }

    private static string TaskList(long teamId, IReadOnlyList<JsonObject> rows) =>
        rows.Count == 0 ? "No host tasks are defined for your team." : new JsonArray(rows.Select(r => (JsonNode?)r).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
