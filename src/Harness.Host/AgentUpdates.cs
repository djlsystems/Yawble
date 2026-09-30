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
/// WHO MAY USE A CLI'S INSTALL RIGHT NOW: runs share it, an update has it alone.
///
/// An update the platform runs waits until no run of that command is in flight, and from the moment
/// it is asked for, new launches of that command WAIT - not fail - until it is done, the way a full
/// pool waits. Keyed by command (`claude`), because that is what an install is: `claude` and
/// `claude-headless` launch the same program.
/// </summary>
public sealed class AgentUpdateGate
{
    private sealed class State
    {
        public int Running;
        public TaskCompletionSource? Updating;
        public TaskCompletionSource? Drained;
        public readonly SemaphoreSlim OneUpdate = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    private State For(string command) => _states.GetOrAdd(command, _ => new State());

    /// <summary>Runs of <paramref name="command"/> in flight now.</summary>
    public int Running(string command) => _states.TryGetValue(command, out var s) ? Volatile.Read(ref s.Running) : 0;

    /// <summary>Whether an update of <paramref name="command"/> is asked for or running.</summary>
    public bool Updating(string command) => _states.TryGetValue(command, out var s) && s.Updating is not null;

    /// <summary>
    /// A run's share of <paramref name="command"/>'s install, held until disposed. While an update
    /// is pending or running this WAITS for it; <paramref name="onHeld"/> is called once, before
    /// waiting, so the run can say why it has not started.
    /// </summary>
    public async Task<IDisposable> EnterRunAsync(string command, Func<Task>? onHeld, CancellationToken ct)
    {
        var state = For(command);
        var told = false;

        while (true)
        {
            Task? wait;

            lock (state)
            {
                wait = state.Updating?.Task;

                if (wait is null)
                {
                    state.Running++;
                    return new Lease(this, command);
                }
            }

            if (!told && onHeld is not null)
            {
                told = true;
                await onHeld();
            }

            await wait.WaitAsync(ct);
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
    public async Task<T> UpdateAsync<T>(string command, Func<CancellationToken, Task<T>> update, CancellationToken ct)
    {
        var state = For(command);
        await state.OneUpdate.WaitAsync(ct);

        TaskCompletionSource updating;
        Task drained;

        lock (state)
        {
            updating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Updating = updating;

            if (state.Running == 0)
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
            return await update(ct);
        }
        finally
        {
            lock (state)
            {
                state.Updating = null;
                state.Drained = null;
            }

            updating.TrySetResult();
            state.OneUpdate.Release();
        }
    }

    private void Leave(string command)
    {
        var state = For(command);

        lock (state)
        {
            state.Running--;
            if (state.Running == 0) state.Drained?.TrySetResult();
        }
    }

    private sealed class Lease(AgentUpdateGate gate, string command) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Leave(command);
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
        if (catalog.Definition(agent) is not { Launch: { } launch } definition) return null;

        var command = launch.FileName;
        var updates = definition.Updates ?? AgentUpdates.ForCommand(command, catalog.Definitions);

        if (updates?.Update is not { Count: > 0 } update)
        {
            return new AgentUpdateResult(definition.Name, command, false, null, null, null, DateTimeOffset.UtcNow,
                $"'{definition.Name}' declares no update command, so the platform cannot update it. The "
                + "container's start installs it.");
        }

        return await gate.UpdateAsync(command, async token =>
        {
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
                definition.Name, command, exit == 0, exit, before, after, at,
                exit == 0
                    ? before == after ? $"`{string.Join(' ', update)}` ran; {command} is already the newest ({after})."
                        : $"`{string.Join(' ', update)}` updated {command} from {before} to {after}."
                    : $"`{string.Join(' ', update)}` exited {exit}: {Tail(output)}",
                now);
        }, ct);
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
