using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Integrations;
using Yawble.Core.Provenance;
using Yawble.Core.Teams;

namespace Yawble.Core.Tasks;

/// <summary>A team repository as a task sees it: where the person's checkout is and which branch is the base.</summary>
public sealed record TaskRepoInfo(string Path, string BaseBranch);

/// <summary>What the definition resolves to right now (the Tasks tab's live panel, and what Save checks).</summary>
public sealed record TaskPreview(string? ProgramResolved, string? Commit, PinSet? Pins, TaskRefusal? Refusal);

/// <summary>Everything computed fresh from the checkout for one request, ask or run.</summary>
public sealed record TaskEvaluation(HostTask Task, TaskRepoInfo Repo, RepoGit Git, string Commit, string ProgramResolved, string? ProgramRelative, string Workdir,
    string DefinitionJson, string DefinitionHash, PinSet Pins, string FilesHash, string WorkdirPath)
{
    public bool Covers(ApprovalRow? a) =>
        a is not null && a.DefinitionHash == DefinitionHash
        && (Task.Def.PinMode == PinModes.Commit ? a.CommitSha == Commit : a.FilesHash == FilesHash);
}

/// <summary>
/// Host tasks, the core: the person's named programs, run on this computer on the base branch of the
/// person's own checkout, only when it is clean, and only under an approval tied to what will execute. The agent side
/// (<see cref="RequestAsync"/>) names a task and gives a reason; it can supply nothing else. Results reach the agent as a
/// wake-up through <c>wake</c>. This class has no UI and no tool surface: <see cref="HostTaskRpc"/> wires those.
/// </summary>
public sealed class HostTaskService
{
    public const int DeclineCooldownMinutes = 10, MaxAsksPerTeamPerHour = 5, KeepLogsPerTask = 20;

    private readonly HostTaskStore _store;
    private readonly HostGit _git;
    private readonly Func<long, string, TaskRepoInfo?> _repoOf;
    private readonly Func<long, string> _docsRootOf;
    private readonly Func<TaskWake, Task> _wake;
    private readonly TimeProvider _clock;
    private readonly Func<string, string?> _readEnv;
    private readonly Action<string>? _log;
    private readonly Connections.ConnectionService? _connections;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _busy = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, (CancellationTokenSource Cts, Task Done)> _active = new();

    /// <param name="repoOf">The team's repository by name ("main" is the primary), or null when the team has none by that name.</param>
    /// <param name="docsRootOf">The team's documents folder, where full logs go (under task-runs/).</param>
    /// <param name="wake">Delivers a message to the requesting agent (a PersonTell on its workflow).</param>
    /// <param name="readEnv">The person's environment (for the named pass-through variables); tests substitute it.</param>
    /// <param name="connections">Where the variables a task takes from a Connection are checked (at save) and read (at each start).</param>
    public HostTaskService(string connectionString, HostGit git, Func<long, string, TaskRepoInfo?> repoOf, Func<long, string> docsRootOf, Func<TaskWake, Task> wake,
        TimeProvider? clock = null, Func<string, string?>? readEnv = null, Action<string>? log = null, Connections.ConnectionService? connections = null)
    {
        _connections = connections;
        _clock = clock ?? TimeProvider.System;
        _store = new HostTaskStore(connectionString, _clock);
        _git = git;
        _repoOf = repoOf;
        _docsRootOf = docsRootOf;
        _wake = wake;
        _readEnv = readEnv ?? Environment.GetEnvironmentVariable;
        _log = log;
    }

    public HostTaskStore Store => _store;

    /// <summary>The Connections bindings are checked against and read from (null: none on this host).</summary>
    public Connections.ConnectionService? Connections => _connections;

    /// <summary>Overridable for tests: the log cap and the grace after a kill.</summary>
    public long LogCapBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>On app start: a run left 'running' means the app died; the job object killed its process.</summary>
    public int Recover() => _store.MarkInterrupted();

    private static Actor Who => Actor.Current ?? Actor.Window;

    // ------------------------------------------------------------------ the person's definitions

    /// <summary>Resolves a definition against the checkout without requiring it to be clean: what Save checks and the live panel shows.</summary>
    public async Task<TaskPreview> PreviewAsync(long teamId, HostTaskDef def, CancellationToken ct = default)
    {
        var (eval, refusal) = await EvaluateAsync(new HostTask(0, teamId, def, "", null), strict: false, ct);
        return new TaskPreview(eval?.ProgramResolved, eval?.Commit, eval?.Pins, refusal);
    }

    /// <summary>Saves a definition (new, or replacing the same name's, which deletes its approval). Refuses with the reason.</summary>
    public async Task<HostTask> SaveAsync(long teamId, HostTaskDef def, CancellationToken ct = default)
    {
        HostTaskRules.Validate(def);
        def = HostTaskRules.ValidateBindings(def, _connections);
        var (eval, refusal) = await EvaluateAsync(new HostTask(0, teamId, def, "", null), strict: false, ct);
        if (refusal is not null) throw new ArgumentException(refusal.Message);
        if (def.PinMode == PinModes.Files && eval!.Pins.CommandLineOnly && !def.CommandLineOnly)
            throw new ArgumentException("nothing but the command line can be pinned for this task (no script, no package files, and the watch list is empty); "
                + "add watch paths, use pin mode 'commit', or confirm that only the command line is pinned");
        return _store.Save(teamId, def, Who.User);
    }

    public void Delete(long teamId, string name)
    {
        var task = _store.Task(teamId, name) ?? throw new ArgumentException($"no task named '{name}'");
        _store.Delete(task.Id);
    }

    public void RevokeApproval(long teamId, string name) =>
        _store.RevokeApproval((_store.Task(teamId, name) ?? throw new ArgumentException($"no task named '{name}'")).Id);

    /// <summary>asks | always | always-but-changed (the last when the standing approval no longer matches the base commit).</summary>
    public async Task<string> ApprovalStateAsync(HostTask task, CancellationToken ct = default)
    {
        var approval = _store.Approval(task.Id);
        if (approval is null) return "asks";
        var (eval, _) = await EvaluateAsync(task, strict: false, ct);
        return eval is null || eval.Covers(approval) ? "always" : "always-but-changed";
    }

    // ------------------------------------------------------------------ the agent's request

    /// <summary>
    /// <c>task_run(name, reason)</c>: refused (a precondition, said in terms of what the person must fix), already waiting, asked, or
    /// started on a standing approval. Returns at once; the outcome of a run arrives as a wake-up.
    /// </summary>
    public async Task<TaskRequestResult> RequestAsync(long teamId, string name, string reason, string member, long workflow, long causationSeq, CancellationToken ct = default)
    {
        var task = _store.Task(teamId, name?.Trim() ?? "");
        if (task is null)
        {
            var names = _store.Tasks(teamId).Select(t => t.Def.Name).ToList();
            return new TaskRequestResult(RequestKind.Refused, $"refused: there is no task named '{name}'. Tasks: {(names.Count == 0 ? "none" : string.Join(", ", names))}.", Precondition: 1);
        }
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("give a reason (one line) for running the task");

        if (_store.LastDecline(task.Id) is { } d
            && DateTimeOffset.TryParse(d.At, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            && _clock.GetUtcNow() - at < TimeSpan.FromMinutes(DeclineCooldownMinutes))
        {
            _store.Call(task, member, workflow, "deny", "declined recently", null, Actor.Current);
            return Refused(task, 0, $"the person declined this task a moment ago{(string.IsNullOrWhiteSpace(d.Note) ? "" : $": {d.Note.Trim()}")}. "
                + $"Do not ask again for {DeclineCooldownMinutes} minutes; carry on without it or say what you need in your report.");
        }

        var (eval, refusal) = await EvaluateAsync(task, strict: true, ct);
        if (refusal is not null)
        {
            _store.Call(task, member, workflow, "deny", $"precondition {refusal.Precondition}", null, Actor.Current);
            return Refused(task, refusal.Precondition, refusal.Message);
        }

        if (BusyText(task, eval!.Repo.Path) is { } busy)
        {
            _store.Call(task, member, workflow, "deny", "precondition 8", null, Actor.Current);
            return Refused(task, 8, busy);
        }

        // A request that already waits is answered first: repeating it must not add a refused run each time.
        if (_store.PendingAsk(task.Id) is { } waiting)
            return new TaskRequestResult(RequestKind.AlreadyWaiting, $"asked: task {task.Def.Name} already waits for the person (request {waiting.Id}). Do not ask again.", waiting.Id);

        if (BindingRefusal(eval, null, member, workflow, Actor.Current) is { } unbound) return Refused(task, 9, unbound);

        var approval = _store.Approval(task.Id);
        if (eval.Covers(approval))
        {
            var reservation = TryReserve(task, eval.Repo.Path, out var busyNow);
            if (reservation is null) return Refused(task, 8, busyNow!);
            var runId = await StartRunAsync(reservation, eval, null, member, workflow, causationSeq, "allow", Actor.Current, ct);
            return new TaskRequestResult(RequestKind.Started,
                $"started: run {runId} of task {task.Def.Name} at {TaskPinning.Short(eval.Commit)} (standing approval). You will be woken with the result.", RunId: runId);
        }

        if (_store.AsksSince(teamId, _clock.GetUtcNow().AddHours(-1)) >= MaxAsksPerTeamPerHour)
        {
            _store.Call(task, member, workflow, "deny", "too many requests", null, Actor.Current);
            return Refused(task, 0, "too many task requests from this team in the last hour; say what you need in your report instead.");
        }
        var askId = await CreateAskAsync(task, eval, approval, member, reason, workflow, causationSeq, ct);
        _store.Call(task, member, workflow, "asked", null, null, Actor.Current);
        return new TaskRequestResult(RequestKind.Asked,
            $"asked: task {task.Def.Name} waits for the person (request {askId}). You will be woken with the result in this workflow. Do other work or end your turn; do not ask again.", askId);
    }

    private static TaskRequestResult Refused(HostTask task, int precondition, string message) =>
        new(RequestKind.Refused, $"refused: task {task.Def.Name}: {message}", Precondition: precondition);

    private async Task<long> CreateAskAsync(HostTask task, TaskEvaluation eval, ApprovalRow? approval, string member, string reason, long workflow, long causation, CancellationToken ct)
    {
        var detail = new JsonObject
        {
            ["commit"] = eval.Commit, ["program"] = eval.ProgramResolved, ["workdir"] = eval.WorkdirPath, ["args"] = new JsonArray(task.Def.Args.Select(a => (JsonNode?)a).ToArray()),
            ["timeout_s"] = task.Def.TimeoutSeconds, ["env_names"] = new JsonArray(task.Def.EnvNames.Select(a => (JsonNode?)a).ToArray()), ["pin_mode"] = task.Def.PinMode,
            ["env_from_connections"] = new JsonArray(task.Def.EnvFromConnections.Select(b => (JsonNode?)b.Shown).ToArray()),
            ["pinned"] = new JsonArray(eval.Pins.Files.Select(f => (JsonNode?)f.Path).ToArray()),
            ["warnings"] = new JsonArray(eval.Pins.Warnings.Select(w => (JsonNode?)w).ToArray()),
            ["standingApproval"] = approval is not null,
        };
        if (approval is not null)
            detail["changed"] = await TaskPinning.DescribeChangeAsync(approval, eval.DefinitionJson, eval.Pins, eval.Commit, eval.DefinitionHash, eval.FilesHash, task.Def.PinMode, eval.Git, ct);
        return _store.InsertAsk(task, member, reason, workflow, causation, eval.Commit, eval.DefinitionHash, eval.FilesHash, detail.ToJsonString());
    }

    // ------------------------------------------------------------------ the person's decision

    /// <summary>
    /// Approves a waiting ask: "Run once", or with <paramref name="always"/> "Always for this team" (pinned at the hashes shown).
    /// Everything is recomputed first: if the base commit, either hash or the checkout differs from what the ask showed, nothing
    /// runs, the ask is closed as stale and a fresh one (with the new diff) replaces it.
    /// </summary>
    public async Task<TaskRequestResult> ApproveAsync(long askId, bool always, CancellationToken ct = default)
    {
        var ask = _store.Ask(askId);
        if (ask is null || ask.State != "pending") throw new ArgumentException($"request {askId} is not waiting");
        var who = Who;
        var task = _store.TaskById(ask.TaskId);
        if (task is null)
        {
            _store.CloseAsk(askId, "declined", "stale: the task was deleted", who);
            await WakeAsync(ask, $"The person approved request {askId}, but task {ask.TaskName} no longer exists; nothing ran.");
            return new TaskRequestResult(RequestKind.Refused, "refused: the task no longer exists", Precondition: 1);
        }

        var (eval, refusal) = await EvaluateAsync(task, strict: true, ct);
        if (refusal is not null)
        {
            _store.CloseAsk(askId, "declined", $"stale: {refusal.Message}", who);
            await WakeAsync(ask, $"The person approved request {askId} for task {task.Def.Name}, but it could not run and nothing ran: {refusal.Message}");
            return Refused(task, refusal.Precondition, refusal.Message);
        }

        if (eval!.Commit != ask.CommitSha || eval.DefinitionHash != ask.DefinitionHash || eval.FilesHash != ask.FilesHash)
        {
            _store.CloseAsk(askId, "declined", "stale: the code or the definition changed while the request waited", who);
            var fresh = await CreateAskAsync(task, eval, _store.Approval(task.Id), ask.Member, ask.Reason, ask.Workflow, ask.CausationSeq, ct);
            await WakeAsync(ask, $"Request {askId} for task {task.Def.Name} went stale (the base branch or the task changed before the person decided), so nothing ran. "
                + $"A fresh request {fresh}, showing what changed, waits for the person.");
            return new TaskRequestResult(RequestKind.Asked,
                $"stale: the base branch or the task changed since the request was shown; nothing ran. A fresh request {fresh} waits.", fresh);
        }

        if (BindingRefusal(eval, askId, ask.Member, ask.Workflow, who) is { } unbound)
        {
            _store.CloseAsk(askId, "declined", $"stale: {unbound}", who);
            await WakeAsync(ask, $"The person approved request {askId} for task {task.Def.Name}, but {unbound}");
            return Refused(task, 9, unbound);
        }

        var reservation = TryReserve(task, eval.Repo.Path, out var busy);
        if (reservation is null) return Refused(task, 8, busy!); // the ask stays pending: try again when the other run is over
        if (!_store.CloseAsk(askId, "approved", null, who))
        {
            reservation.Dispose();
            throw new ArgumentException($"request {askId} is not waiting");
        }
        if (always) _store.SetApproval(task.Id, eval.DefinitionHash, eval.FilesHash, eval.DefinitionJson, TaskPinning.FilesJson(eval.Pins.Files), eval.Commit, who);
        var runId = await StartRunAsync(reservation, eval, askId, ask.Member, ask.Workflow, ask.CausationSeq, "approved", who, ct);
        return new TaskRequestResult(RequestKind.Started, $"started: run {runId} of task {task.Def.Name}{(always ? " (Always for this team stored)" : "")}", askId, runId);
    }

    public async Task DeclineAsync(long askId, string? note, CancellationToken ct = default)
    {
        var ask = _store.Ask(askId);
        if (ask is null || ask.State != "pending") throw new ArgumentException($"request {askId} is not waiting");
        var who = Who;
        _store.CloseAsk(askId, "declined", string.IsNullOrWhiteSpace(note) ? null : note.Trim(), who);
        if (_store.TaskById(ask.TaskId) is { } task) _store.Call(task, ask.Member, ask.Workflow, "declined", null, null, who);
        await WakeAsync(ask, $"The person declined your request to run task {ask.TaskName} (request {askId})"
            + (string.IsNullOrWhiteSpace(note) ? "." : $": {note.Trim()}") + " Do not try another way; carry on without it or say what you need.");
    }

    /// <summary>The person runs their own task from the Tasks tab: same preconditions and records, no ask, no agent to wake.</summary>
    public async Task<TaskRequestResult> RunNowAsync(long teamId, string name, CancellationToken ct = default)
    {
        var task = _store.Task(teamId, name) ?? throw new ArgumentException($"no task named '{name}'");
        var (eval, refusal) = await EvaluateAsync(task, strict: true, ct);
        if (refusal is not null) return Refused(task, refusal.Precondition, refusal.Message);
        if (BindingRefusal(eval!, null, null, 0, Who) is { } unbound) return Refused(task, 9, unbound);
        var reservation = TryReserve(task, eval!.Repo.Path, out var busy);
        if (reservation is null) return Refused(task, 8, busy!);
        var runId = await StartRunAsync(reservation, eval, null, null, 0, 0, "allow", Who, ct);
        return new TaskRequestResult(RequestKind.Started, $"started: run {runId} of task {task.Def.Name}", RunId: runId);
    }

    /// <summary>The person stops a running task: its whole process tree is killed and the agent is told.</summary>
    public bool Cancel(long runId)
    {
        if (!_active.TryGetValue(runId, out var a)) return false;
        a.Cts.Cancel();
        return true;
    }

    /// <summary>Completes when the run is over and its record and wake-up are written (for tests and for a caller that must wait).</summary>
    public Task WaitForRunAsync(long runId) => _active.TryGetValue(runId, out var a) ? a.Done : Task.CompletedTask;

    // ------------------------------------------------------------------ evaluation: the preconditions, then the hashes

    private async Task<(TaskEvaluation? Eval, TaskRefusal? Refusal)> EvaluateAsync(HostTask task, bool strict, CancellationToken ct)
    {
        var def = task.Def;
        var repo = _repoOf(task.TeamId, def.Repo);
        if (repo is null || !Directory.Exists(repo.Path))
            return (null, new TaskRefusal(2, $"the repository '{def.Repo}' is not available for this team on this computer. The person must fix the task or the team's repositories."));
        var git = new RepoGit(_git, repo.Path);
        var inside = await git.TryAsync(ct, "rev-parse", "--is-inside-work-tree");
        if (inside.ExitCode != 0 || inside.Stdout != "true")
            return (null, new TaskRefusal(2, $"{repo.Path} is not a git work tree. The person must fix the repository."));

        string? commit;
        if (strict)
        {
            var head = await git.TryAsync(ct, "symbolic-ref", "-q", "HEAD");
            var onBase = head.ExitCode == 0 && head.Stdout == "refs/heads/" + repo.BaseBranch;
            if (!onBase)
                return (null, new TaskRefusal(3, $"the checkout {repo.Path} is on {(head.ExitCode == 0 ? head.Stdout.Replace("refs/heads/", "") : "a detached HEAD")}, "
                    + $"not the base branch {repo.BaseBranch}. Tasks run only on the base branch: the person must switch it."));
            commit = await git.RevAsync("HEAD", ct);
            var status = await git.TryAsync(ct, "status", "--porcelain", "--untracked-files=normal", "--ignore-submodules=none");
            if (status.ExitCode != 0)
                return (null, new TaskRefusal(4, $"git could not check {repo.Path}: {status.Stderr}"));
            if (status.Stdout.Length > 0)
            {
                var lines = status.Stdout.Split('\n');
                return (null, new TaskRefusal(4, $"the checkout {repo.Path} has uncommitted or untracked files ({string.Join("; ", lines.Take(5).Select(l => l.Trim()))}"
                    + (lines.Length > 5 ? $"; and {lines.Length - 5} more" : "") + "). Tasks run only on a clean checkout: the person must commit, stash or remove them."));
            }
        }
        else
        {
            commit = await git.RevAsync("refs/heads/" + repo.BaseBranch, ct);
        }
        if (commit is null) return (null, new TaskRefusal(3, $"the repository {repo.Path} has no {repo.BaseBranch} branch (or no commit yet)."));

        var tree = await TrackedTree.ReadAsync(git, commit, ct);

        var workdir = def.Workdir.Replace('\\', '/').Trim('/');
        if (workdir == ".") workdir = "";
        var workdirPath = Path.GetFullPath(Path.Combine(repo.Path, workdir));
        if (!HostTaskRules.IsInsideRelative(def.Workdir) || !IsInside(repo.Path, workdirPath) || !Directory.Exists(workdirPath) || PathHasLink(repo.Path, workdirPath)
            || (workdir.Length > 0 && !tree.HasDirectory(workdir)))
            return (null, new TaskRefusal(5, $"the working folder '{def.Workdir}' is not a tracked folder inside {repo.Path} at {TaskPinning.Short(commit)} "
                + "(it is missing, outside the repository, or reached through a link). The person must fix the task."));

        string resolved;
        string? relative = null;
        var program = def.Program.Trim();
        if (program.Contains('/') || program.Contains('\\') || Path.IsPathRooted(program))
        {
            var full = Path.GetFullPath(Path.Combine(repo.Path, program));
            if (!IsInside(repo.Path, full) || !File.Exists(full) || PathHasLink(repo.Path, full))
                return (null, new TaskRefusal(6, $"the program '{program}' is not a regular file inside {repo.Path} (or it is reached through a link). The person must fix the task."));
            relative = Path.GetRelativePath(repo.Path, full).Replace('\\', '/');
            if (!tree.Has(relative))
                return (null, new TaskRefusal(6, $"the program '{relative}' is not tracked at {TaskPinning.Short(commit)}: it cannot be pinned, so it cannot run."));
            resolved = full;
        }
        else
        {
            var found = CliRunner.Find(program);
            if (found is null) return (null, new TaskRefusal(6, $"the program '{program}' was not found on the PATH of this computer. The person must install it or fix the task."));
            resolved = Path.GetFullPath(found);
        }

        var (defJson, defHash) = TaskPinning.Definition(task.TeamId, def, resolved);
        var pins = TaskPinning.Compute(def, workdir, relative, tree);
        return (new TaskEvaluation(task, repo, git, commit, resolved, relative, workdir, defJson, defHash, pins, TaskPinning.FilesHash(pins.Files), workdirPath), null);
    }

    private static bool IsInside(string root, string full)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var f = Path.GetFullPath(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return f.StartsWith(r, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>True when any folder or file from <paramref name="root"/> (exclusive) down to <paramref name="full"/> is a link or junction.</summary>
    private static bool PathHasLink(string root, string full)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var p = Path.GetFullPath(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
             p.Length > rootFull.Length && p.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p) ?? "")
        {
            FileSystemInfo info = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
            if (info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ environment from Connections

    /// <summary>
    /// Null when every binding can be read now. Otherwise nothing starts: a refused run record notes which bindings (by variable and
    /// Connection name, never a value, in missing_env as MissingEnv does) and the returned text says what the person must fix.
    /// </summary>
    private string? BindingRefusal(TaskEvaluation eval, long? askId, string? member, long workflow, Actor? who)
    {
        if (eval.Task.Def.EnvFromConnections.Count == 0) return null;
        var (_, problems) = TaskProcess.ReadBindings(eval.Task.Def.EnvFromConnections, _connections);
        return problems.Count == 0 ? null : RecordBindingRefusal(eval, problems, askId, member, workflow, who).Text;
    }

    private (long Run, string Text) RecordBindingRefusal(TaskEvaluation eval, List<string> problems, long? askId, string? member, long workflow, Actor? who)
    {
        var task = eval.Task;
        var run = _store.InsertRun(task, eval.Repo.Path, askId, member, eval.Commit, eval.DefinitionHash, eval.FilesHash, eval.ProgramResolved, string.Join("; ", problems), who);
        _store.FinishRun(run, "refused", null, false, null, 0, false, null, null, null, null);
        _store.Call(task, member, workflow, "deny", "precondition 9", _store.Run(run), who);
        return (run, $"nothing ran: a variable it takes from a Connection cannot be read ({string.Join("; ", problems)}). "
            + "The person must fix the Connection in Settings > Integrations > Connections, or the task.");
    }

    // ------------------------------------------------------------------ one run at a time

    private sealed class Reservation(HostTaskService owner, string[] keys) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) foreach (var k in keys) owner._busy.Remove(k);
        }
    }

    private static string TaskKey(HostTask t) => "task:" + t.Id;
    private static string RepoKey(string path) => "repo:" + Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private string? BusyText(HostTask task, string repoPath)
    {
        lock (_gate)
        {
            if (_busy.TryGetValue(TaskKey(task), out var run)) return $"task {task.Def.Name} is running (run {run}). Wait for its result.";
            if (_busy.TryGetValue(RepoKey(repoPath), out var other)) return $"another task run (run {other}) is using the same checkout. Wait for its result.";
        }
        return null;
    }

    private Reservation? TryReserve(HostTask task, string repoPath, out string? busy)
    {
        lock (_gate)
        {
            busy = BusyText(task, repoPath);
            if (busy is not null) return null;
            // The run id is not known yet: the reservation is by key; the id is filled in when the run row exists.
            _busy[TaskKey(task)] = 0;
            _busy[RepoKey(repoPath)] = 0;
            return new Reservation(this, [TaskKey(task), RepoKey(repoPath)]);
        }
    }

    // ------------------------------------------------------------------ running

    private async Task<long> StartRunAsync(Reservation reservation, TaskEvaluation eval, long? askId, string? member, long workflow, long causation, string decision,
        Actor? who, CancellationToken ct)
    {
        var task = eval.Task;
        long runId;
        try
        {
            // The recheck an instant before spawning: still on the base branch at the commit the decision was made for, still clean.
            var head = await eval.Git.RevAsync("HEAD", ct);
            var dirty = (await eval.Git.TryAsync(ct, "status", "--porcelain", "--untracked-files=normal", "--ignore-submodules=none")).Stdout.Length > 0;
            if (head != eval.Commit || dirty)
            {
                var why = head != eval.Commit ? "the checkout moved to another commit" : "the checkout is no longer clean";
                var refused = _store.InsertRun(task, eval.Repo.Path, askId, member, eval.Commit, eval.DefinitionHash, eval.FilesHash, eval.ProgramResolved, null, who);
                _store.FinishRun(refused, "refused", null, false, null, 0, false, null, null, head, dirty);
                _store.Call(task, member, workflow, "deny", "checkout changed before start", _store.Run(refused), who);
                reservation.Dispose();
                if (member is not null) await WakeAsync(new TaskWake(task.TeamId, member, workflow, causation, $"task {task.Def.Name}: nothing ran, {why} between the decision and the start. Ask again."));
                return refused;
            }

            // The bound secrets are read now, at the start: a value replaced in the Connection since the approval is the one used.
            var (bound, problems) = TaskProcess.ReadBindings(task.Def.EnvFromConnections, _connections);
            if (problems.Count > 0)
            {
                var (refusedRun, text) = RecordBindingRefusal(eval, problems, askId, member, workflow, who);
                reservation.Dispose();
                if (member is not null) await WakeAsync(new TaskWake(task.TeamId, member, workflow, causation, $"task {task.Def.Name}: {text}"));
                return refusedRun;
            }
            var (env, missing, secrets) = TaskProcess.BuildEnvironment(task.Def.EnvNames, _readEnv);
            foreach (var (name, value) in bound)
            {
                env[name] = value;
                secrets.AddRange(TaskProcess.MaskedParts(value));
            }
            runId = _store.InsertRun(task, eval.Repo.Path, askId, member, eval.Commit, eval.DefinitionHash, eval.FilesHash, eval.ProgramResolved,
                missing.Count == 0 ? null : string.Join(",", missing), who);
            lock (_gate)
            {
                _busy[TaskKey(task)] = runId;
                _busy[RepoKey(eval.Repo.Path)] = runId;
            }
            var logPath = Path.Combine(_docsRootOf(task.TeamId), "task-runs", $"{task.Def.Name}-{runId}-{_clock.GetUtcNow():yyyyMMdd-HHmmss}.log");
            var options = new TaskProcessOptions
            {
                Program = eval.ProgramResolved, Args = task.Def.Args, WorkingDirectory = eval.WorkdirPath, Environment = env,
                Timeout = TimeSpan.FromSeconds(task.Def.TimeoutSeconds), LogPath = logPath, SecretValues = secrets, LogCapBytes = LogCapBytes,
            };
            var cts = new CancellationTokenSource();
            var done = Task.Run(() => ExecuteAsync(reservation, eval, runId, options, missing, member, workflow, causation, decision, who, cts));
            _active[runId] = (cts, done);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
        return runId;
    }

    private async Task ExecuteAsync(Reservation reservation, TaskEvaluation eval, long runId, TaskProcessOptions options, List<string> missing, string? member, long workflow,
        long causation, string decision, Actor? who, CancellationTokenSource cts)
    {
        var task = eval.Task;
        TaskProcessResult? result = null;
        string? error = null;
        try
        {
            try
            {
                result = await TaskProcess.RunAsync(options, cts.Token);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                error = e.Message;
            }
            var headAfter = await SafeAsync(() => eval.Git.RevAsync("HEAD", CancellationToken.None));
            var dirtyAfter = await SafeAsync(async () => (await eval.Git.TryAsync(CancellationToken.None, "status", "--porcelain", "--untracked-files=normal", "--ignore-submodules=none")).Stdout.Length > 0);
            _store.FinishRun(runId, "done", result?.ExitCode, result?.TimedOut ?? false, result?.KilledBy, result?.OutputBytes, result?.Truncated ?? false,
                result is null ? null : options.LogPath, result?.LogSha256, headAfter, dirtyAfter);
            var run = _store.Run(runId)!;
            _store.Call(task, member, workflow, decision, null, run, who);
            if (member is not null)
                await WakeAsync(new TaskWake(task.TeamId, member, workflow, causation, WakeText(task, run, result, error, options, headAfter, dirtyAfter, missing)));
            Retain(task);
        }
        catch (Exception e)
        {
            _log?.Invoke($"task {task.Def.Name} run {runId}: finishing failed: {e.Message}");
        }
        finally
        {
            reservation.Dispose();
            _active.TryRemove(runId, out _);
            cts.Dispose();
        }
    }

    private static async Task<T?> SafeAsync<T>(Func<Task<T?>> work)
    {
        try { return await work(); }
        catch (Exception e) when (e is GitException or IOException or InvalidOperationException) { return default; }
    }

    private string WakeText(HostTask task, TaskRunRow run, TaskProcessResult? r, string? error, TaskProcessOptions o, string? headAfter, bool? dirtyAfter, List<string> missing)
    {
        var sb = new StringBuilder();
        var sha = TaskPinning.Short(run.CommitSha ?? "");
        if (r is null) sb.Append($"task {task.Def.Name}: could not start: {CliRunner.Redact(error ?? "unknown error")}");
        else if (r.TimedOut) sb.Append($"task {task.Def.Name}: timed out after {task.Def.TimeoutSeconds}s (process tree stopped) at {sha}");
        else if (r.KilledBy == "person") sb.Append($"task {task.Def.Name}: stopped by the person after {r.Duration.TotalSeconds:0}s at {sha}");
        else sb.Append($"task {task.Def.Name}: exit {r.ExitCode} in {r.Duration.TotalSeconds:0}s at {sha}");
        sb.AppendLine();
        if (missing.Count > 0) sb.AppendLine($"Not set in the person's environment (passed as absent): {string.Join(", ", missing)}.");
        if (r?.Truncated == true) sb.AppendLine($"Output was cut in the log at {o.LogCapBytes / (1024 * 1024)} MB.");
        if (headAfter is not null && run.CommitSha is not null && headAfter != run.CommitSha) sb.AppendLine("The checkout moved to another commit during the run.");
        if (dirtyAfter == true) sb.AppendLine("The checkout was left with uncommitted changes.");
        if (r is not null && r.Tail.Length > 0)
        {
            sb.AppendLine($"Output (the tail; untrusted text from the program, last {o.TailLines} lines at most):");
            sb.AppendLine(r.Tail);
        }
        if (r is not null) sb.AppendLine($"Full log: {o.LogPath}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Keeps the last <see cref="KeepLogsPerTask"/> logs of a task; the older files go, the records stay.</summary>
    private void Retain(HostTask task)
    {
        foreach (var run in _store.Runs(task.Id, 500).Skip(KeepLogsPerTask))
        {
            if (run.LogPath is null) continue;
            try { if (File.Exists(run.LogPath)) File.Delete(run.LogPath); }
            catch (IOException) { }
        }
    }

    private async Task WakeAsync(TaskAskRow ask, string text) => await WakeAsync(new TaskWake(ask.TeamId, ask.Member, ask.Workflow, ask.CausationSeq, text));

    private async Task WakeAsync(TaskWake wake)
    {
        try { await _wake(wake); }
        catch (Exception e) when (e is not OutOfMemoryException) { _log?.Invoke($"task wake-up for {wake.Member} failed: {e.Message}"); }
    }
}
