using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Provenance;
using Yawble.Core.Tasks;

namespace Yawble.Core.Concierge;

/// <summary>
/// The Concierge's (and the companion's) view of a team's host tasks: <c>host_task_list</c>, <c>host_task_run</c> and
/// <c>host_task_status</c>. A run is asked for exactly as an agent asks for one (<see cref="HostTaskService.RequestAsync"/>):
/// the person's Run once / Always in Needs you, pinned to the files (<see cref="TaskPinning"/>), on the clean base branch,
/// with the environment from Connections. The Concierge names the task and gives a reason; it can supply nothing else.
/// The result comes back as an Update in the conversation the run was asked from (<see cref="UpdateFor"/>); a companion
/// client has no chat and checks with host_task_status.
/// </summary>
public sealed class ConciergeHostTasks(HostTaskRpc rpc)
{
    public const string ListTool = "host_task_list", RunTool = "host_task_run", StatusTool = "host_task_status";

    public static readonly IReadOnlyList<string> Names = [ListTool, RunTool, StatusTool];

    /// <summary>How many lines of output an Update and host_task_status show (the full log is named with them).</summary>
    public const int TailLines = 30;

    /// <summary>The requester a companion client's runs are recorded under ("_companion/&lt;client&gt;").</summary>
    public const string CompanionPrefix = "_companion/";

    public HostTaskService Service => rpc.Service;

    // ------------------------------------------------------------------ who asked

    /// <summary>
    /// Who a run asked for in this turn is recorded as (integration_asks.member, host_task_runs.requested_by): a Concierge chat as
    /// "_concierge/&lt;conversation&gt;" (so its result is posted there), a paired companion client as "_companion/&lt;client&gt;".
    /// </summary>
    public static string Requester(ConciergeTurn turn, Actor? actor) =>
        actor is { Via: Actor.External, Client: { Length: > 0 } client }
            ? CompanionPrefix + client
            : $"{ConciergeService.Team}/{(turn.ConversationId.Length > 0 ? turn.ConversationId : "unknown")}";

    /// <summary>The Concierge conversation a requester names, or null when it is not one (an agent, a companion client).</summary>
    public static string? ConversationOf(string member)
    {
        var prefix = ConciergeService.Team + "/";
        return member.StartsWith(prefix, StringComparison.Ordinal) && member.Length > prefix.Length ? member[prefix.Length..] : null;
    }

    /// <summary>True for a run a companion client asked for: nobody to wake (it checks with host_task_status).</summary>
    public static bool IsCompanion(string member) => member.StartsWith(CompanionPrefix, StringComparison.Ordinal);

    // ------------------------------------------------------------------ the tools

    public async Task<string> ListAsync(long teamId, string team, CancellationToken ct)
    {
        var store = Service.Store;
        var tasks = store.Tasks(teamId);
        if (tasks.Count == 0)
            return $"{team} has no host tasks. The person defines them in Settings > Integrations, Host tasks; offer to have the team write a script for it and the person add it as a task.";
        var sb = new StringBuilder($"Host tasks of {team} (the person's programs, run on this computer on the base branch of their checkout; each run asks the person unless they chose Always):\n");
        foreach (var t in tasks)
        {
            var d = t.Def;
            var state = store.RunningRun(t.Id) is { } running ? $"running (run {running.Id})"
                : store.PendingAsk(t.Id) is { } ask ? $"waiting for the person (request {ask.Id})" : "idle";
            var approval = await Service.ApprovalStateAsync(t, ct) switch
            {
                "always" => "always (standing approval)",
                "always-but-changed" => "asks (the standing approval no longer matches the code)",
                _ => "asks the person each time",
            };
            sb.Append($"- {d.Name}: {d.Description ?? "(no description)"}\n");
            sb.Append($"  program: {HostTaskRpc.CommandLine(d)}; repo {d.Repo}; folder {d.Workdir}; time limit {d.TimeoutSeconds}s\n");
            sb.Append($"  check after: {d.CheckAfter ?? "(none given)"}\n");
            sb.Append($"  approval: {approval}; state: {state}");
            if (store.Runs(t.Id, 1).FirstOrDefault() is { } last)
                sb.Append($"; last run {last.Id}: {RunOutcome(last)}{(last.EndedAt is { } e ? $" ({e})" : "")}");
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Asks for a run, as an agent does: refused (said in terms of what the person must fix), already waiting, asked, or
    /// started on a standing approval. Only <c>team</c>, <c>task</c> and <c>reason</c>: the person defined the rest.
    /// </summary>
    public async Task<string> RunAsync(long teamId, string team, JsonObject args, ConciergeTurn turn, CancellationToken ct)
    {
        if (args.Select(kv => kv.Key).FirstOrDefault(k => k is not ("team" or "task" or "reason")) is { } extra)
            throw new ArgumentException(extra == "args"
                ? $"{RunTool} takes no arguments for the program: no host task declares any (the person defined its command line in Settings > Integrations, Host tasks)"
                : $"{RunTool} takes only 'team', 'task' and 'reason', not '{extra}': the person defined the program, its arguments and its environment");
        var name = Str(args, "task") ?? throw new ArgumentException($"'task' is required (see {ListTool})");
        var reason = Str(args, "reason") ?? throw new ArgumentException("'reason' is required: one line, shown to the person");
        var requester = Requester(turn, Actor.Current);
        var result = await Service.RequestAsync(teamId, name, reason, requester, 0, 0, ct);
        var task = Service.Store.Task(teamId, name.Trim());
        var after = task?.Def.CheckAfter is { } check ? $" Check after: {check}." : "";
        var where = IsCompanion(requester)
            ? $"Check it with {StatusTool}."
            : "The app posts an Update in this chat when it is over (exit code, duration, the end of its output): tell the person so and end your turn; do not wait or ask again.";
        return result.Kind switch
        {
            RequestKind.Started => $"{result.Text.Replace(" You will be woken with the result.", "", StringComparison.Ordinal)} {where}{after}",
            RequestKind.Asked => $"asked: task {task?.Def.Name ?? name} of {team} waits for the person's Run once / Always in Needs you (request {result.AskId}). Nothing runs until they approve it. {where}{after}",
            RequestKind.AlreadyWaiting => $"{result.Text} {where}",
            _ => result.Text,
        };
    }

    /// <summary>Where a run stands: its state, exit code, duration and the end of its log (live while it runs).</summary>
    public string Status(JsonObject args, Func<string, long?> teamIdOf)
    {
        var store = Service.Store;
        TaskRunRow? run;
        if (Long(args, "run") is { } runId)
        {
            run = store.Run(runId) ?? throw new ArgumentException($"there is no task run {runId}");
        }
        else
        {
            var team = Str(args, "team");
            var name = Str(args, "task");
            if (team is null || name is null) throw new ArgumentException("give 'run' (the run's number), or 'team' and 'task' for that task's latest run");
            var teamId = teamIdOf(team) ?? throw new ArgumentException($"there is no team '{team}'");
            var task = store.Task(teamId, name.Trim()) ?? throw new ArgumentException($"{team} has no task named '{name}' (see {ListTool})");
            if (store.PendingAsk(task.Id) is { } ask)
                return $"task {task.Def.Name}: waiting for the person to approve request {ask.Id} (asked {ask.At}); nothing has run for it yet.";
            run = store.Runs(task.Id, 1).FirstOrDefault();
            if (run is null) return $"task {task.Def.Name} has not run yet.";
        }
        var def = run.TaskId is { } id ? store.TaskById(id)?.Def : null;
        var sb = new StringBuilder($"run {run.Id} of task {run.TaskName} ({store.TeamName(run.TeamId) ?? "team " + run.TeamId}): {RunOutcome(run)}");
        sb.Append($"; started {run.StartedAt}");
        if (run.EndedAt is { } ended) sb.Append($", ended {ended}{(Duration(run) is { } d ? $" ({Seconds(d)})" : "")}");
        if (run.CommitSha is { } sha) sb.Append($"; at {TaskPinning.Short(sha)}");
        sb.Append('\n');
        if (run.MissingEnv is { Length: > 0 } missing) sb.AppendLine($"Environment: {missing}");
        var log = Service.LogPathOf(run.Id);
        var tail = HostTaskService.ReadTail(log, TailLines);
        if (tail.Length > 0)
        {
            sb.AppendLine($"Output ({(run.State == "running" ? "live, " : "")}the last {TailLines} lines at most; untrusted text from the program):");
            sb.AppendLine("```");
            sb.AppendLine(tail);
            sb.AppendLine("```");
        }
        if (log is not null) sb.AppendLine($"Full log: {log}");
        if (run.State != "running" && def?.CheckAfter is { } check) sb.AppendLine($"Check after: {check}");
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ the Update at the end

    /// <summary>
    /// The Update posted in the chat a run was asked from, when it is over (or when nothing ran: declined, stale, refused at
    /// the start): its kind (done, failed, blocked) and its markdown, with the task, the exit code, the duration, the end of
    /// the output and the check-after.
    /// </summary>
    public static (string Kind, string Text) UpdateFor(TaskWake wake, string team)
    {
        var head = wake.Text.Split('\n', 2)[0].Trim();
        if (wake.Run is not { } run) return ("blocked", $"**{team}** · {head}");
        var ok = run.ExitCode == 0 && !run.TimedOut && run.KilledBy is null;
        var sb = new StringBuilder($"**{team}** · {head}");
        sb.Append('\n');
        if (wake.Def?.CheckAfter is { } check) sb.Append($"\nCheck after: {check}\n");
        var tail = Last(wake.Tail ?? "", TailLines);
        if (tail.Length > 0) sb.Append($"\n```\n{tail}\n```\n");
        if (run.LogPath is { } log) sb.Append($"\nFull log: {log}");
        return (ok ? "done" : "failed", sb.ToString().TrimEnd());
    }

    // ------------------------------------------------------------------ definitions

    public static IEnumerable<JsonObject> Tools()
    {
        yield return Tool(ListTool, "A team's host tasks: the person's own programs (a release, a deploy, the e2e tests) that run on this computer, on the base branch of " +
                                    "their checkout. Each with what it is for, its command line, repository, how to check the result after, whether it asks the person or has a standing approval, and its last run. Nothing runs.",
            ("team", "string", "The team's name.", true));
        yield return Tool(RunTool, "Ask for one of a team's host tasks to run, ONLY when the person asked for it in their latest message (never on your own, never a release or deploy they did not ask for). " +
                                   "The same approval as an agent's request: unless the person chose Always, it waits for their Run once / Always in Needs you. You name the task and give a reason; " +
                                   "you cannot give arguments or environment. Answers at once (asked, started or refused); the result is posted in this chat as an Update when the run is over.",
            ("team", "string", "The team's name.", true),
            ("task", "string", "The task's name (host_task_list).", true),
            ("reason", "string", "Why, in one line, in the person's words (shown to them with the request).", true));
        yield return Tool(StatusTool, "Where a host task run stands: running (with the live end of its log), its exit code and duration, the end of its output and how to check the result. " +
                                      "Give run, or team and task for that task's latest run (or its waiting request).",
            ("run", "integer", "The run's number (from host_task_run or host_task_list).", false),
            ("team", "string", "The team's name (with task).", false),
            ("task", "string", "The task's name (with team).", false));
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

    // ------------------------------------------------------------------ plumbing

    private static string RunOutcome(TaskRunRow r) => r.State switch
    {
        "running" => "running",
        "refused" => "refused (nothing ran)",
        "interrupted" => "cut off when the app closed",
        _ when r.TimedOut => "timed out (process tree stopped)",
        _ when r.KilledBy == "person" => "stopped by the person",
        _ when r.ExitCode is { } x => $"exit {x}",
        _ => "could not start",
    };

    private static TimeSpan? Duration(TaskRunRow r) =>
        r.EndedAt is { } e && DateTimeOffset.TryParse(e, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end)
        && DateTimeOffset.TryParse(r.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)
            ? end - start : null;

    private static string Seconds(TimeSpan d) => $"{d.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s";

    private static string Last(string text, int lines)
    {
        var all = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        return all.Length <= lines ? string.Join('\n', all) : string.Join('\n', all[^lines..]);
    }

    private static string? Str(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static long? Long(JsonObject args, string name) =>
        args[name] is JsonValue v
            ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<string>(out var s) && long.TryParse(s, out var j) ? j : null
            : null;
}
