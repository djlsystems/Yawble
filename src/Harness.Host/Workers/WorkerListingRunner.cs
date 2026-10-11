using Harness.Contracts;
using Harness.Pty;

namespace Harness.Host;

/// <summary>
/// A TOOL LISTING RUN ON A WORKER: each listing command is one <see cref="RunAgentCommands"/> to the
/// connected worker with the most measured headroom, run there as a member's CLI is started - as the
/// agent, from a scratch folder of its own, with the worker's environment, the launch's overlay and
/// the run's credential, and every provider key that is not the CLI's own taken out. No worker, or
/// none that answers, is a listing that did not answer, with the reason: never clean.
/// </summary>
/// <remarks>
/// A LISTING IN A HOME OF ITS OWN (an issued member preset's) is made BY THE WORKER, inside the
/// command, in <paramref name="homes"/> on the shared volume, and removed by it after.
/// <see cref="MakeHomeAsync"/> hands the pre-flight a path in that folder to set as HOME, which this
/// runner recognises and replaces with the worker's own; there is nothing to remove here.
/// </remarks>
/// <param name="homes">Where a listing's own home is made: the member temporary folders' root.</param>
/// <param name="pathIsTheWorkers">Whether this process's PATH is the worker's - a Host that runs its
/// runs itself - so whether a CLI is installed is read here. Otherwise the worker says, when it runs it.</param>
public sealed class WorkerListingRunner(WorkerAsks asks, string homes, bool pathIsTheWorkers) : IListingRunner
{
    /// <summary>How long one listing command may take on the worker.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    /// <summary>What a listing reads when no worker is connected to run it.</summary>
    public const string NoWorkerText = "No worker is connected to list this CLI's tools.";

    private string OwnHome => Path.Combine(homes, RunHome.Prefix + "made-by-the-worker");

    public bool Installed(string command) => !pathIsTheWorkers || PathSearch.Find(command) is not null;

    public Task<string?> MakeHomeAsync(CancellationToken ct) => Task.FromResult<string?>(OwnHome);

    public Task RemoveHomeAsync(string home) => Task.CompletedTask;

    public async Task<ListingRun> RunAsync(
        string command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        CancellationToken ct, RunCredential? credential = null)
    {
        var ownHome = environment.TryGetValue("HOME", out var home) && string.Equals(home, OwnHome, StringComparison.Ordinal);
        var overlay = ownHome
            ? environment.Where(e => e.Key is not ("HOME" or "XDG_CACHE_HOME")).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal)
            : new Dictionary<string, string>(environment, StringComparer.Ordinal);

        // What the run is handed explicitly - the overlay and the credential's own variables - keeps
        // its provider keys; every other provider key is taken out, as at a member's spawn.
        var handedIn = new Dictionary<string, string>(environment, StringComparer.Ordinal);
        foreach (var (name, value) in credential?.Environment ?? new Dictionary<string, string>()) handedIn[name] = value;

        var asked = await asks.AskAsync<AgentCommandsRan>(
            new RunAgentCommands(
                WorkerAsks.NewRequest(),
                [new AgentCliRun(
                    command, arguments, overlay, AgentEnvironment.ProviderKeysToRemove(command, handedIn),
                    (int)Timeout.TotalSeconds, Scratch: true, HomesIn: ownHome ? homes : null)],
                credential),
            Timeout + TimeSpan.FromSeconds(15),
            ct);

        if (asked.Answer?.Results.SingleOrDefault() is not { } result)
        {
            return new ListingRun(null, "", asked.Worker is { } worker ? $"Worker {worker} did not answer." : NoWorkerText);
        }

        if (!result.Installed) return new ListingRun(null, "", result.Error ?? $"'{command}' is not on PATH.");

        return result.Error is { } error ? new ListingRun(null, "", error) : new ListingRun(result.ExitCode, result.Stdout);
    }
}
