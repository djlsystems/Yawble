using System.Collections.Concurrent;
using System.Diagnostics;

namespace Harness.Host;

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
        public AgentUpdateHold? Hold;
        public long Turned;
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
    private long _turn;

    /// <summary>
    /// Raised once each time a hold of a command ends - the update done, failed or cancelled while it
    /// waited - after the launches it held are let go, with <see cref="Holding"/> already null.
    /// </summary>
    public event Action<string>? UpdateEnded;

    /// <summary>
    /// A count raised whenever any command's hold begins or ends. Read it before asking a worker about
    /// a command and pass it to <see cref="HeldSince"/> with the answer: a count, not a time, so no
    /// two clocks are compared.
    /// </summary>
    public long Turn => Interlocked.Read(ref _turn);

    /// <summary>
    /// Whether <paramref name="command"/> is held now, or its hold began or ended since
    /// <paramref name="turn"/> was read: an answer about it asked then says nothing about the install.
    /// </summary>
    public bool HeldSince(string command, long turn)
    {
        if (!_states.TryGetValue(command, out var s)) return false;
        lock (s) return s.Hold is not null || s.Turned > turn;
    }

    /// <summary>
    /// The update holding <paramref name="command"/> now - waiting for its runs, or running, and on which
    /// worker once one is picked - or null. Exactly the window <see cref="Updating"/> reports.
    /// </summary>
    public AgentUpdateHold? Holding(string command)
    {
        if (!_states.TryGetValue(command, out var s)) return null;
        lock (s) return s.Hold is { } hold ? hold with { Runs = s.Running.Count } : null;
    }

    /// <summary>The updater names the worker it picked for <paramref name="command"/>'s update, as it starts it.</summary>
    public void RunsOn(string command, string worker)
    {
        if (!_states.TryGetValue(command, out var s)) return;
        lock (s)
        {
            if (s.Hold is { } hold) s.Hold = hold with { Phase = AgentUpdatePhases.Updating, Worker = worker };
        }
    }

    private void Turned(State state) => state.Turned = Interlocked.Increment(ref _turn);

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
            state.Hold = new AgentUpdateHold(command, AgentUpdatePhases.Waiting, null, DateTimeOffset.UtcNow);
            Turned(state);

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

                if (state.Updating == updating && state.Hold is { Phase: AgentUpdatePhases.Waiting } hold)
                {
                    state.Hold = hold with { Phase = AgentUpdatePhases.Updating };
                }
            }

            return await update(ct);
        }
        finally
        {
            var ended = false;
            lock (state)
            {
                if (state.Updating == updating)
                {
                    // Not when a cancel ended this hold already: that one said so.
                    state.Updating = null;
                    state.Hold = null;
                    Turned(state);
                    ended = true;
                }

                state.Drained = null;
            }

            updating.TrySetResult();
            state.OneUpdate.Release();
            if (ended) Ended(command);
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
            state.Hold = null;
            Turned(state);

            state.Last = StateFor(command, state, asked) with
            {
                FinishedAt = DateTimeOffset.UtcNow,
                CancelledBy = person,
            };
            cancelled = Snapshot(command, state);
        }

        release?.TrySetResult();
        asked.Cancel.Cancel();
        if (release is not null) Ended(command);

        return (cancelled, false);
    }

    private void Ended(string command)
    {
        try
        {
            UpdateEnded?.Invoke(command);
        }
        catch (Exception)
        {
            // A listener's failure is its own; the hold has ended either way.
        }
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
            if (state.Hold is { Phase: AgentUpdatePhases.Waiting } hold) state.Hold = hold with { Phase = AgentUpdatePhases.Updating };
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

/// <summary>
/// AN UPDATE HOLDING A COMMAND NOW, as the gate holds it: <see cref="Phase"/> is
/// <see cref="AgentUpdatePhases.Waiting"/> (for <see cref="Runs"/> runs in flight) or
/// <see cref="AgentUpdatePhases.Updating"/>; <see cref="Worker"/> is the worker it runs on, null until
/// one is picked, which is when it starts. Nothing in it is estimated.
/// </summary>
public sealed record AgentUpdateHold(string Command, string Phase, string? Worker, DateTimeOffset Since, int Runs = 0);

/// <summary>
/// WHAT A COMMAND HELD BY AN UPDATE READS, everywhere it is read: the install state, the sign-in, the
/// launch check, the tool listing and the doctor. Its own state, never "not installed": the install is
/// being replaced, and it is measured again when the update ends.
/// </summary>
public static class UpdatingOn
{
    /// <summary>The sentence for <paramref name="hold"/>; <paramref name="onThisMachine"/> in a Host that runs its runs itself.</summary>
    public static string Text(AgentUpdateHold hold, bool onThisMachine = false)
    {
        const string again = "it is measured again when the update ends.";
        if (hold.Phase == AgentUpdatePhases.Waiting)
        {
            var runs = hold.Runs == 1 ? "1 run" : $"{hold.Runs} runs";
            return $"Updating {hold.Command}: waiting for {runs} of it to finish, then it runs on "
                   + (onThisMachine ? "this machine" : "one worker") + $"; {again}";
        }

        return onThisMachine ? $"Updating {hold.Command} on this machine; {again}"
            : hold.Worker is { } worker ? $"Updating {hold.Command} on {worker}; {again}"
            : $"Updating {hold.Command}; {again}";
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

/// <summary>A CLI's installed version and when it last changed, as the history says.</summary>
/// <param name="UpdatedAt">The first line that recorded this version after a different one; null
/// when every kept line has this version (no update recorded since <paramref name="Since"/>), and
/// null when the version is not known.</param>
/// <param name="Since">The oldest line kept.</param>
/// <param name="UpdatedBy">Who brought this version, from the line at <paramref name="UpdatedAt"/>:
/// `start` for a container start's line, `person` for a person's update through the platform,
/// `measured` for a line control wrote when the workers changed and a version differed; null
/// when <paramref name="UpdatedAt"/> is, so always null for a version that is not known.</param>
/// <param name="Person">That person's email when the line records it.</param>
public sealed record CliVersionNow(
    string Cli, string? Version, DateTimeOffset? UpdatedAt, DateTimeOffset? Since,
    string? UpdatedBy = null, string? Person = null);
