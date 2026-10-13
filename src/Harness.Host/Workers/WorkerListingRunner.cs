using Harness.Contracts;
using Harness.Pty;

namespace Harness.Host;

/// <summary>
/// A TOOL LISTING RUN ON A WORKER: each listing command is one <see cref="RunAgentCommands"/> to the
/// connected worker with the most measured headroom, run there as a member's CLI is started - as the
/// agent, from a scratch folder of its own, with the worker's environment, the launch's overlay and
/// the run's credential scope, and every provider key that is not the CLI's own taken out. No worker, or
/// none that answers, is a listing that did not answer, with the reason: never clean.
/// </summary>
/// <param name="pathIsTheWorkers">Whether this process's PATH is the worker's - a Host that runs its
/// runs itself - so whether a CLI is installed is read here. Otherwise the worker says, when it runs it.</param>
public sealed class WorkerListingRunner(WorkerAsks asks, bool pathIsTheWorkers) : IListingRunner
{
    /// <summary>How long one listing command may take on the worker.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    /// <summary>What a listing reads when no worker is connected to run it.</summary>
    public const string NoWorkerText = "No worker is connected to list this CLI's tools.";

    public bool Installed(string command) => !pathIsTheWorkers || PathSearch.Find(command) is not null;

    public async Task<ListingRun> RunAsync(
        string command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        CancellationToken ct, CredentialScope? scope = null)
    {
        // What the run is handed explicitly - the overlay - keeps its provider keys; every other
        // provider key is taken out, as at a member's spawn.
        var overlay = new Dictionary<string, string>(environment, StringComparer.Ordinal);

        var asked = await asks.AskAsync<AgentCommandsRan>(
            new RunAgentCommands(
                WorkerAsks.NewRequest(),
                [new AgentCliRun(
                    command, arguments, overlay, AgentEnvironment.ProviderKeysToRemove(command, overlay),
                    (int)Timeout.TotalSeconds, Scratch: true)],
                scope),
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
