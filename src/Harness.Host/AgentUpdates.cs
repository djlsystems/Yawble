using System.ComponentModel;
using Harness.Contracts;

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
/// THE PLATFORM'S UPDATE OF A PRESET'S CLI, when a person asks: the preset's declared
/// <see cref="AgentUpdates.Update"/> command through <see cref="AgentUpdateGate"/>, with the versions
/// before and after recorded in `cli-versions.jsonl` beside the start's own lines.
/// </summary>
/// <remarks>
/// RUN ONCE, ON ONE WORKER: when the gate has drained the runs in flight, the connected worker with
/// the most measured headroom is picked - then, not when it was asked for - and runs, in one request,
/// the version, the update (without the update-off: that is for launches) and the version again, as
/// the agent. No worker then: it fails and nothing ran. The worker lost while it runs: it fails, and
/// whether the install changed is not known. Either way the launches it held are let go.
/// </remarks>
public sealed class AgentCliUpdater(
    AgentCatalog catalog, AgentUpdateGate gate, AgentLaunchUser? runAs = null, string? dataRoot = null, WorkerAsks? asks = null)
{
    /// <summary>How long one update command may take before it is stopped.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>How long a version may take.</summary>
    private static readonly TimeSpan VersionTimeout = CliVersionsWhenWorkersChange.VersionTimeout;

    /// <summary>What an update reads when no worker is connected to run it.</summary>
    public const string NoWorkerText = "No worker is connected to run the update; nothing was run.";

    /// <summary>What an update reads when its worker stopped while it ran.</summary>
    public static string LostText(WorkerId worker) =>
        $"Worker {worker} stopped while it ran the update; whether the install changed is not known. "
        + "Its version is measured again at the next update or container start.";

    private readonly Lazy<WorkerAsks> _asks = new(() => asks ?? WorkerAsks.InProcess(runAs));

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

        // The version with its update-off; the update WITHOUT it: that is for launches, and an
        // explicit update must not read it.
        var version = CliVersionsWhenWorkersChange.VersionRun(command, updates);
        var run = new AgentCliRun(
            update[0], [.. update.Skip(1)], new Dictionary<string, string>(), [], (int)Timeout.TotalSeconds, WithUpdatesOn: true);

        // Picked now, as it starts, and named to the gate: what reads "updating on <worker>" from here.
        if (_asks.Value.Pick() is not { } worker) throw new InvalidOperationException(NoWorkerText);
        gate.RunsOn(command, worker.Id.Value);

        var asked = await _asks.Value.AskAsync<AgentCommandsRan>(
            worker, new RunAgentCommands(WorkerAsks.NewRequest(), [version, run, version]), Timeout + VersionTimeout * 2 + TimeSpan.FromSeconds(30), token);

        if (asked.Answer is not { Results: [var before, var ran, var after] })
        {
            throw new InvalidOperationException(asked.Worker is { } lost ? LostText(lost) : NoWorkerText);
        }

        var at = DateTimeOffset.UtcNow;
        var (exit, output) = ran.Installed
            ? (ran.ExitCode ?? -1, ran.Error is { } error ? error : ran.Stdout + ran.Stderr)
            : (-1, $"`{update[0]}` is not an executable file on PATH.");

        CliVersionNow? now = null;

        if (dataRoot is not null)
        {
            var history = CliVersionHistory.In(dataRoot);
            await history.AppendAsync(
                new Dictionary<string, string?> { [command] = VersionOf(after) }, "update", token, person);
            now = CliVersionHistory.Now(command, await history.ReadAsync(CliVersionHistory.MaxTake, token));
        }

        var (was, @is) = (VersionOf(before), VersionOf(after));
        return new AgentUpdateResult(
            prepared.Agent, command, exit == 0, exit, was, @is, at,
            exit == 0
                ? was == @is ? $"`{string.Join(' ', update)}` ran; {command} is already the newest ({@is})."
                    : $"`{string.Join(' ', update)}` updated {command} from {was} to {@is}."
                : $"`{string.Join(' ', update)}` exited {exit}: {Tail(output)}",
            now);
    }

    private static string? VersionOf(AgentCliRunResult version) => CliVersionsWhenWorkersChange.VersionOf(version);

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : "..." + trimmed[^400..];
    }
}
