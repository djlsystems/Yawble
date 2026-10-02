using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// EACH WORKER IS MEASURED AS IT JOINS, in control: one sign-in probe (<see cref="ProbeSignIn"/>, no
/// new message) asking about every command the catalog launches - those only issued-credential
/// presets launch included, which the sign-in probe itself never asks about - and each command's
/// <c>installed</c> recorded against that worker (<see cref="WorkerInstalls"/>). Only that: an issued
/// preset's sign-in is never taken from it. Without this a second worker would never be measured,
/// since the sign-in probe asks one worker, and two that disagree could not be seen. A worker that
/// does not answer records nothing, and whatever it said before stands.
/// </summary>
public static class MeasureInstallsWhenAWorkerJoins
{
    /// <summary>What the Host logs when it wires the measurement, which it does in control alone.</summary>
    public const string WiredText = "Each worker that joins is asked which agent CLIs it has.";

    /// <summary>
    /// Wires the measurement to every join <paramref name="onJoined"/> reports, when
    /// <paramref name="control"/>; returns whether it did. <paramref name="worker"/> finds the joined
    /// worker's connection, and <paramref name="commands"/> is asked at each join, so a catalog saved
    /// since is measured.
    /// </summary>
    public static bool Wire(
        bool control, Action<Action<WorkerId>> onJoined, Func<WorkerId, IRunWorker?> worker,
        Func<IReadOnlyList<string>> commands, WorkerAsks asks, WorkerInstalls installs, ILogger? log = null)
    {
        if (!control) return false;

        log?.LogInformation(WiredText);

        onJoined(joined => _ = Task.Run(async () =>
        {
            try
            {
                if (worker(joined) is { } connection) await MeasureAsync(connection, commands(), asks, installs, log);
            }
            catch (Exception exception)
            {
                log?.LogWarning("Worker {Worker} joined; asking which agent CLIs it has failed: {Message}", joined, exception.Message);
            }
        }));

        return true;
    }

    /// <summary>Every distinct command the catalog launches, in catalog order.</summary>
    public static IReadOnlyList<string> Commands(AgentCatalog catalog) =>
        [.. catalog.Definitions
            .Where(d => d.Launch is not null && !string.IsNullOrWhiteSpace(d.Launch.FileName))
            .Select(d => d.Launch.FileName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Asks <paramref name="worker"/> about <paramref name="commands"/> and records each answer's <c>installed</c>.</summary>
    public static async Task MeasureAsync(
        IRunWorker worker, IReadOnlyList<string> commands, WorkerAsks asks, WorkerInstalls installs,
        ILogger? log = null, CancellationToken ct = default)
    {
        if (commands.Count == 0) return;

        var specs = AgentAuthProbe.LoadSpecs();
        var asked = await asks.AskAsync<SignInProbed>(
            worker, new ProbeSignIn(WorkerAsks.NewRequest(), [.. commands.Select(command => AgentAuthProbe.Spec(command, specs))]),
            AgentAuthProbe.Bound(commands.Count), ct);

        if (asked.Answer is not { } probed)
        {
            log?.LogWarning("Worker {Worker} was not measured for its agent CLIs: {Why}", worker.Id, asked.Why);
            return;
        }

        installs.Record(worker.Id, probed.Results.Select(result => (result.Command, result.Installed)));
    }
}
