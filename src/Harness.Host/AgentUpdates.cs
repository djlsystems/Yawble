using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Harness.Pty;

namespace Harness.Host;

/// <summary>
/// How a preset's CLI is kept from updating ITSELF, and how the platform updates it instead.
///
/// THE INSTALL IS SHARED. Every member launches its CLI from one install on the volume, and a CLI
/// that updates itself replaces that install in place while other members are being launched from
/// it; a launch landing in the gap finds no program. So every launch the Host makes of a preset's
/// command - a member, the Concierge's terminal, the sign-in probe - carries what turns the CLI's
/// own updater off, and updates happen only where the platform chooses: at start, before the Host,
/// and when a person asks (<see cref="AgentCliUpdater"/>).
///
/// WHAT EACH BUILT-IN DECLARES WAS FOUND IN THE CLI INSTALLED IN THE IMAGE and checked with a real
/// launch, never read from documentation alone; the seed says which version.
/// </summary>
public sealed record AgentUpdates(
    [property: Description(
        "Variables that turn the CLI's own automatic update off, set on every launch the Host makes "
        + "of this preset's command - a member, the Concierge's terminal, the sign-in probe - after "
        + "every other source, so no preset or team env can turn it back on. A key beginning "
        + "HARNESS_ is ignored.")]
    IReadOnlyDictionary<string, string>? Env = null,
    [property: Description(
        "Arguments that do the same, for a CLI with no such variable. They go LAST on a member's "
        + "and the Concierge's launch, so a launch's own order (a subcommand first, a prompt "
        + "flag where it must be) is untouched, and before the sign-in probe's own arguments. A "
        + "preset whose prompt must stay the last argument declares a variable instead.")]
    IReadOnlyList<string>? Arguments = null,
    [property: Description(
        "The command the PLATFORM runs to update this CLI, when a person asks - as the user agents "
        + "run as, while no run of it is in flight and new launches of it wait. Null when the "
        + "platform has no way to update it; the container's start still installs it.")]
    IReadOnlyList<string>? Update = null)
{
    /// <summary>
    /// The update-off every built-in preset launching <paramref name="command"/> declares, merged:
    /// what the sign-in probe, which is keyed by command rather than preset, sets.
    /// </summary>
    public static AgentUpdates? ForCommand(string command, IEnumerable<AgentDefinition>? definitions = null)
    {
        var declared = (definitions ?? AgentCatalogFile.BuiltIns())
            .Where(d => d.Updates is not null
                && string.Equals(d.Launch?.FileName, command, StringComparison.Ordinal))
            .Select(d => d.Updates!)
            .ToList();

        if (declared.Count == 0) return null;

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in declared.SelectMany(u => u.Env ?? new Dictionary<string, string>())) env[key] = value;

        return new AgentUpdates(
            env,
            declared.Select(u => u.Arguments).FirstOrDefault(a => a is { Count: > 0 }),
            declared.Select(u => u.Update).FirstOrDefault(a => a is { Count: > 0 }));
    }

    /// <summary><see cref="Env"/> without any HARNESS_ key, or null when there is none.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, string>? Environment =>
        Env?.Where(e => !e.Key.StartsWith("HARNESS_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal) is { Count: > 0 } env
            ? env
            : null;

    /// <summary>
    /// <paramref name="command"/> with this preset's update-off folded in: the arguments last, the
    /// environment carried for the spawn site to set last.
    /// </summary>
    public static AgentCommand Apply(AgentCommand command, AgentUpdates? updates) =>
        updates is null
            ? command
            : command with
            {
                Arguments = [.. command.Arguments, .. updates.Arguments ?? []],
                UpdateEnvironment = updates.Environment,
            };
}

/// <summary>
/// A run that holds, or waits for, a share of a CLI's install: its team and member, the way
/// `GET /api/wip` names who holds a slot.
/// </summary>
public sealed record AgentRunHolder(string Team, string Member);

/// <summary>The words <see cref="AgentUpdateState.Phase"/> takes.</summary>
public static class AgentUpdatePhases
{
    /// <summary>No update of this command has been asked for since the Host started.</summary>
    public const string None = "none";

    /// <summary>Asked for, and waiting for the runs in flight; new launches are held. Cancellable.</summary>
    public const string Waiting = "waiting";

    /// <summary>The update command is running. Not cancellable.</summary>
    public const string Updating = "updating";

    /// <summary>The update ran; <see cref="AgentUpdateState.Result"/> says what was measured.</summary>
    public const string Done = "done";

    /// <summary>A person cancelled it while it waited; nothing was run.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>It threw before it could measure anything; <see cref="AgentUpdateState.Error"/> says what.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// WHAT THE GATE SAYS ABOUT ONE COMMAND'S UPDATE, read from the gate and never estimated: the update
/// asked for (or the outcome of the last one), the runs in flight it waits for and the launches it holds.
/// </summary>
/// <param name="Running">Runs of the command in flight now.</param>
/// <param name="InFlight">The team and member of each of them that the gate knows.</param>
/// <param name="Held">The team and member of each launch waiting for this update.</param>
public sealed record AgentUpdateState(
    string Command,
    string Phase,
    string? Agent,
    string? RequestedBy,
    DateTimeOffset? RequestedAt,
    int Running,
    IReadOnlyList<AgentRunHolder> InFlight,
    IReadOnlyList<AgentRunHolder> Held,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    AgentUpdateResult? Result = null,
    string? Error = null,
    string? CancelledBy = null);

/// <summary>
/// WHO MAY USE A CLI'S INSTALL RIGHT NOW: runs share it, an update has it alone.
///
/// An update the platform runs waits until no run of that command is in flight, and from the moment
/// it is asked for, new launches of that command WAIT - not fail - until it is done, the way a full
/// pool waits. Keyed by command (`claude`), because that is what an install is: `claude` and
/// `claude-headless` launch the same program.
///
/// A PERSON'S UPDATE IS ASKED FOR, NOT AWAITED (<see cref="Request"/>): the gate keeps it, and its
/// outcome afterwards, so the Agents screen reads it back (<see cref="StateOf"/>) however often it is
/// closed and opened. While it waits it can be cancelled, which lets the launches it held go; once
/// its command runs it cannot.
/// </summary>
public sealed class AgentUpdateGate
{
    private sealed class State
    {
        public readonly Dictionary<long, AgentRunHolder?> Running = [];
        public readonly Dictionary<long, AgentRunHolder?> Held = [];
        public TaskCompletionSource? Updating;
        public TaskCompletionSource? Drained;
        public Asked? Current;
        public AgentUpdateState? Last;
        public readonly SemaphoreSlim OneUpdate = new(1, 1);
    }

    /// <summary>A person's update, from asked to finished. Changed only under its state's lock.</summary>
    private sealed class Asked(string agent, string? person)
    {
        public string Agent { get; } = agent;
        public string? Person { get; } = person;
        public DateTimeOffset At { get; } = DateTimeOffset.UtcNow;
        public string Phase = AgentUpdatePhases.Waiting;
        public DateTimeOffset? StartedAt;
        public readonly CancellationTokenSource Cancel = new();
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);
    private long _next;

    private State For(string command) => _states.GetOrAdd(command, _ => new State());

    /// <summary>Runs of <paramref name="command"/> in flight now.</summary>
    public int Running(string command)
    {
        if (!_states.TryGetValue(command, out var s)) return 0;
        lock (s) return s.Running.Count;
    }

    /// <summary>Whether an update of <paramref name="command"/> is asked for or running.</summary>
    public bool Updating(string command) => _states.TryGetValue(command, out var s) && s.Updating is not null;

    /// <summary>
    /// A run's share of <paramref name="command"/>'s install, held until disposed. While an update
    /// is pending or running this WAITS for it, named in the update's state by
    /// <paramref name="holder"/>; <paramref name="onHeld"/> is called once, before waiting, so the run
    /// can say why it has not started.
    /// </summary>
    public async Task<IDisposable> EnterRunAsync(
        string command, Func<Task>? onHeld, CancellationToken ct, AgentRunHolder? holder = null)
    {
        var state = For(command);
        var id = Interlocked.Increment(ref _next);
        var told = false;

        try
        {
            while (true)
            {
                Task? wait;

                lock (state)
                {
                    wait = state.Updating?.Task;

                    if (wait is null)
                    {
                        state.Held.Remove(id);
                        state.Running[id] = holder;
                        return new Lease(this, command, id);
                    }

                    state.Held[id] = holder;
                }

                if (!told && onHeld is not null)
                {
                    told = true;
                    await onHeld();
                }

                await wait.WaitAsync(ct);
            }
        }
        catch
        {
            lock (state) state.Held.Remove(id);
            throw;
        }
    }

    /// <summary>Waits while an update of <paramref name="command"/> is pending or running, taking no
    /// share: a Concierge's terminal, which is held at launch but is too long-lived to hold an
    /// update back.</summary>
    public async Task WaitUntilNotUpdatingAsync(string command, CancellationToken ct)
    {
        var state = For(command);

        while (true)
        {
            Task? wait;
            lock (state) wait = state.Updating?.Task;
            if (wait is null) return;
            await wait.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Runs <paramref name="update"/> with <paramref name="command"/>'s install alone: new runs wait
    /// from now, in-flight runs are waited for, and everything waiting is let go when it ends,
    /// however it ends.
    /// </summary>
    public Task<T> UpdateAsync<T>(string command, Func<CancellationToken, Task<T>> update, CancellationToken ct) =>
        UpdateAsync(command, update, null, ct);

    private async Task<T> UpdateAsync<T>(
        string command, Func<CancellationToken, Task<T>> update, Asked? asked, CancellationToken ct)
    {
        var state = For(command);
        // Synchronous when no other update holds it, so a launch arriving after Request returns is held.
        await state.OneUpdate.WaitAsync(ct);

        TaskCompletionSource updating;
        Task drained;

        lock (state)
        {
            updating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Updating = updating;

            if (state.Running.Count == 0)
            {
                drained = Task.CompletedTask;
            }
            else
            {
                state.Drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drained = state.Drained.Task;
            }
        }

        try
        {
            await drained.WaitAsync(ct);

            // THE LINE BETWEEN CANCELLABLE AND NOT, drawn under the lock Cancel takes: from here the
            // command runs, and a cancel that got in first has already let everything go.
            lock (state)
            {
                if (asked is not null)
                {
                    if (asked.Phase == AgentUpdatePhases.Cancelled) throw new OperationCanceledException(ct);
                    Start(asked);
                }
            }

            return await update(ct);
        }
        finally
        {
            lock (state)
            {
                if (state.Updating == updating) state.Updating = null;
                state.Drained = null;
            }

            updating.TrySetResult();
            state.OneUpdate.Release();
        }
    }

    /// <summary>
    /// A person asks for an update of <paramref name="command"/>, and this answers at once: the
    /// update waits in the gate for the runs in flight, holds new launches, runs, and is kept with
    /// its outcome for <see cref="StateOf"/>. One at a time per command: asked again while one is
    /// waiting or running, this answers that one's state and <c>Started</c> is false.
    /// <paramref name="finished"/> is called once with the final state when it ran or failed.
    /// </summary>
    public (AgentUpdateState State, bool Started) Request(
        string command,
        string agent,
        string? person,
        Func<CancellationToken, Task<AgentUpdateResult>> update,
        Func<AgentUpdateState, Task>? finished = null)
    {
        var state = For(command);
        Asked asked;

        lock (state)
        {
            if (state.Current is not null) return (Snapshot(command, state), false);
            asked = new Asked(agent, person);
            state.Current = asked;
        }

        var running = UpdateAsync(command, update, asked, asked.Cancel.Token);
        _ = FinishAsync(command, state, asked, running, finished);

        lock (state) return (Snapshot(command, state), true);
    }

    private async Task FinishAsync(
        string command, State state, Asked asked, Task<AgentUpdateResult> running,
        Func<AgentUpdateState, Task>? finished)
    {
        AgentUpdateResult? result = null;
        string? error = null;

        try
        {
            result = await running;
        }
        catch (OperationCanceledException) when (asked.Cancel.IsCancellationRequested)
        {
            // Cancel recorded it, and let the held launches go, before this saw it.
            return;
        }
        catch (Exception exception)
        {
            error = $"The update did not finish: {exception.Message}";
        }

        AgentUpdateState final;

        lock (state)
        {
            asked.Phase = result is null ? AgentUpdatePhases.Failed : AgentUpdatePhases.Done;
            state.Last = StateFor(command, state, asked) with
            {
                FinishedAt = result?.At ?? DateTimeOffset.UtcNow,
                Result = result,
                Error = error,
            };
            if (state.Current == asked) state.Current = null;
            final = Snapshot(command, state);
        }

        if (finished is not null) await finished(final);
    }

    /// <summary>
    /// Cancels <paramref name="command"/>'s update while it is still waiting, and lets the launches
    /// it held go. Answers the cancelled state; null with <c>Running</c> true when its command is
    /// already running (it cannot be cancelled), and null with <c>Running</c> false when none waits.
    /// </summary>
    public (AgentUpdateState? State, bool Running) Cancel(string command, string? person)
    {
        if (!_states.TryGetValue(command, out var state)) return (null, false);

        TaskCompletionSource? release;
        AgentUpdateState cancelled;
        Asked asked;

        lock (state)
        {
            if (state.Current is not { } current) return (null, false);
            if (current.Phase != AgentUpdatePhases.Waiting) return (null, true);

            asked = current;
            asked.Phase = AgentUpdatePhases.Cancelled;
            state.Current = null;

            release = state.Updating;
            state.Updating = null;
            state.Drained = null;

            state.Last = StateFor(command, state, asked) with
            {
                FinishedAt = DateTimeOffset.UtcNow,
                CancelledBy = person,
            };
            cancelled = Snapshot(command, state);
        }

        release?.TrySetResult();
        asked.Cancel.Cancel();

        return (cancelled, false);
    }

    /// <summary>What the gate says about <paramref name="command"/>'s update now.</summary>
    public AgentUpdateState StateOf(string command)
    {
        var state = For(command);
        lock (state) return Snapshot(command, state);
    }

    /// <summary>Every command with an update asked for, an outcome kept, or a launch held.</summary>
    public IReadOnlyList<AgentUpdateState> States() =>
        [.. _states.Keys.Order(StringComparer.Ordinal)
            .Select(StateOf)
            .Where(s => s.Phase != AgentUpdatePhases.None || s.Held.Count > 0)];

    private static AgentUpdateState Snapshot(string command, State state)
    {
        if (state.Current is { } current) return StateFor(command, state, current);

        var running = Holders(state.Running);
        var held = Holders(state.Held);

        return state.Last is { } last
            ? last with { Running = state.Running.Count, InFlight = running, Held = held }
            : new AgentUpdateState(
                command, AgentUpdatePhases.None, null, null, null, state.Running.Count, running, held);
    }

    private static AgentUpdateState StateFor(string command, State state, Asked asked) =>
        new(command, asked.Phase, asked.Agent, asked.Person, asked.At, state.Running.Count,
            Holders(state.Running), Holders(state.Held), asked.StartedAt);

    private static IReadOnlyList<AgentRunHolder> Holders(Dictionary<long, AgentRunHolder?> shares) =>
        [.. shares.OrderBy(s => s.Key).Select(s => s.Value).OfType<AgentRunHolder>()];

    private static void Start(Asked asked)
    {
        if (asked.Phase != AgentUpdatePhases.Waiting) return;
        asked.Phase = AgentUpdatePhases.Updating;
        asked.StartedAt = DateTimeOffset.UtcNow;
    }

    private void Leave(string command, long id)
    {
        var state = For(command);

        lock (state)
        {
            state.Running.Remove(id);
            if (state.Running.Count > 0 || state.Drained is null) return;

            // The last run out starts the update in the same step, so its state never reads
            // "waiting for 0 runs".
            if (state.Current is { } asked) Start(asked);
            state.Drained.TrySetResult();
        }
    }

    private sealed class Lease(AgentUpdateGate gate, string command, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Leave(command, id);
        }
    }
}

/// <summary>What one platform update of a CLI came to.</summary>
/// <param name="CliVersion">The CLI's version and when it last changed, read back from the version
/// history after this update's line was written - what the Agents screen's row shows next. Null when
/// the Host keeps no history or the preset declares no update.</param>
public sealed record AgentUpdateResult(
    string Agent,
    string Command,
    bool Updated,
    int? ExitCode,
    string? VersionBefore,
    string? VersionAfter,
    DateTimeOffset At,
    string Detail,
    CliVersionNow? CliVersion = null);

/// <summary>
/// THE PLATFORM'S UPDATE OF A PRESET'S CLI, when a person asks: the preset's declared
/// <see cref="AgentUpdates.Update"/> command, run as the user agents run as through
/// <see cref="AgentUpdateGate"/>, with the versions before and after recorded in
/// `cli-versions.jsonl` beside the start's own lines.
/// </summary>
public sealed class AgentCliUpdater(
    AgentCatalog catalog, AgentUpdateGate gate, AgentLaunchUser? runAs = null, string? dataRoot = null)
{
    /// <summary>How long one update command may take before it is stopped.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <param name="person">The email of the person who asked, recorded on the history's line.</param>
    public async Task<AgentUpdateResult?> UpdateAsync(string agent, CancellationToken ct, string? person = null)
    {
        if (Prepare(agent) is not { } prepared) return null;
        if (prepared.Refused is { } refused) return refused;

        return await gate.UpdateAsync(prepared.Command, token => RunUpdateAsync(prepared, person, token), ct);
    }

    /// <summary>
    /// A person's update, ASKED FOR AND NOT AWAITED: it waits in <see cref="AgentUpdateGate"/> for the
    /// runs in flight and is read back with <see cref="AgentUpdateGate.StateOf"/>. Null for an unknown
    /// preset. A preset that declares no update command answers its outcome at once, with nothing run.
    /// </summary>
    public (AgentUpdateState State, bool Started)? Request(
        string agent, string? person, Func<AgentUpdateState, Task>? finished = null)
    {
        if (Prepare(agent) is not { } prepared) return null;

        if (prepared.Refused is { } refused)
        {
            var now = gate.StateOf(prepared.Command);
            return (now with
            {
                Phase = AgentUpdatePhases.Done, Agent = refused.Agent, RequestedBy = person, RequestedAt = refused.At,
                StartedAt = null, FinishedAt = refused.At, Result = refused, Error = null, CancelledBy = null,
            }, false);
        }

        return gate.Request(
            prepared.Command, prepared.Agent, person, token => RunUpdateAsync(prepared, person, token), finished);
    }

    /// <summary>The command a preset launches, for reading its update's state; null for an unknown one.</summary>
    public string? CommandOf(string agent) => catalog.Definition(agent)?.Launch?.FileName;

    private sealed record Prepared(
        string Agent, string Command, AgentUpdates? Updates, IReadOnlyList<string> Update, AgentUpdateResult? Refused);

    private Prepared? Prepare(string agent)
    {
        if (catalog.Definition(agent) is not { Launch: { } launch } definition) return null;

        var command = launch.FileName;
        var updates = definition.Updates ?? AgentUpdates.ForCommand(command, catalog.Definitions);

        if (updates?.Update is not { Count: > 0 } update)
        {
            return new Prepared(definition.Name, command, updates, [], new AgentUpdateResult(
                definition.Name, command, false, null, null, null, DateTimeOffset.UtcNow,
                $"'{definition.Name}' declares no update command, so the platform cannot update it. The "
                + "container's start installs it."));
        }

        return new Prepared(definition.Name, command, updates, update, null);
    }

    private async Task<AgentUpdateResult> RunUpdateAsync(Prepared prepared, string? person, CancellationToken token)
    {
        var (command, updates, update) = (prepared.Command, prepared.Updates, prepared.Update);

        var before = await VersionAsync(command, updates, token);
        // WITHOUT the update-off: that is for launches, and an explicit update must not read it.
        var (exit, output) = await RunAsync(update, null, token);
        var after = await VersionAsync(command, updates, token);
        var at = DateTimeOffset.UtcNow;

        CliVersionNow? now = null;

        if (dataRoot is not null)
        {
            var history = CliVersionHistory.In(dataRoot);
            await history.AppendAsync(
                new Dictionary<string, string?> { [command] = after }, "update", token, person);
            now = CliVersionHistory.Now(command, await history.ReadAsync(CliVersionHistory.MaxTake, token));
        }

        return new AgentUpdateResult(
            prepared.Agent, command, exit == 0, exit, before, after, at,
            exit == 0
                ? before == after ? $"`{string.Join(' ', update)}` ran; {command} is already the newest ({after})."
                    : $"`{string.Join(' ', update)}` updated {command} from {before} to {after}."
                : $"`{string.Join(' ', update)}` exited {exit}: {Tail(output)}",
            now);
    }

    /// <summary>The first line `<paramref name="command"/> --version` prints, with its update-off.</summary>
    public async Task<string?> VersionAsync(string command, AgentUpdates? updates, CancellationToken ct)
    {
        if (PathSearch.Find(command) is null) return null;
        var (exit, output) = await RunAsync([command, .. updates?.Arguments ?? [], "--version"], updates, ct);
        return exit == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() : null;
    }

    private async Task<(int ExitCode, string Output)> RunAsync(
        IReadOnlyList<string> argv, AgentUpdates? updates, CancellationToken ct)
    {
        var resolved = PathSearch.Find(argv[0]);
        if (resolved is null) return (-1, $"`{argv[0]}` is not an executable file on PATH.");

        var prefix = runAs is { Switches: true } ? runAs.Prefix : [];
        var start = new ProcessStartInfo
        {
            FileName = prefix.Count > 0 ? prefix[0] : resolved,
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        foreach (var part in prefix.Skip(1)) start.ArgumentList.Add(part);
        if (prefix.Count > 0) start.ArgumentList.Add(resolved);
        foreach (var arg in argv.Skip(1)) start.ArgumentList.Add(arg);
        foreach (var (key, value) in updates?.Environment ?? new Dictionary<string, string>()) start.Environment[key] = value;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The update did not start.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);

        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        return (process.ExitCode, (await stdout) + (await stderr));
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : "..." + trimmed[^400..];
    }
}
