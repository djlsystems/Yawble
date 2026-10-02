using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE AGENT'S PASS OF A REMOVAL: what the Host could not remove, removed as the agent. Null when it
/// was handed over; otherwise why it was not, in a sentence the removal records with what remains.
/// </summary>
public interface IAgentPass
{
    /// <summary>
    /// Whether the pass is asked whenever something is left - a worker elsewhere decides whether it
    /// switches users - rather than only when this process's agent user switches.
    /// </summary>
    bool Always { get; }

    Task<string?> RemoveAsync(string boundary, IReadOnlyList<string> paths, CancellationToken ct);
}

/// <summary>
/// The agent's pass on a worker: one <see cref="RemoveAsAgent"/> to the connected worker with the most
/// measured headroom, which checks each path against the boundary again and runs
/// <c>rm -rf --one-file-system</c> as the agent. No worker, or none that answers: nothing was removed
/// as the agent, and the sentence says it is retried when one connects.
/// </summary>
public sealed class WorkerAgentPass(WorkerAsks asks, bool always) : IAgentPass
{
    /// <summary>How long one agent pass may take on a worker.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromMinutes(10);

    /// <summary>What a removal records when no worker is connected for its agent pass.</summary>
    public const string NoWorkerText = "No worker is connected to remove what the agent owns; it is retried when one connects.";

    /// <summary>What a removal records when the worker asked did not answer.</summary>
    public static string NoAnswerText(WorkerId worker) =>
        $"Worker {worker} did not answer the removal of what the agent owns; it is retried when one connects.";

    public bool Always => always;

    /// <summary>The pass a removal composed without a connected worker asks: one of its own process's.</summary>
    public static WorkerAgentPass InProcess(AgentLaunchUser? runAs) => new(WorkerAsks.InProcess(runAs), always: false);

    public async Task<string?> RemoveAsync(string boundary, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var asked = await asks.AskAsync<RemovedAsAgent>(new RemoveAsAgent(WorkerAsks.NewRequest(), boundary, paths), Bound, ct);

        return asked.Answer is { } removed
            ? removed.Error
            : asked.Worker is { } worker ? NoAnswerText(worker) : NoWorkerText;
    }
}

/// <summary>
/// IN CONTROL, EVERY WORKER THAT JOINS RETRIES THE UNFINISHED REMOVALS ONCE: control's own retry at
/// start runs before any worker can have connected, so without this what only the agent's pass can
/// remove would wait for a person. A worker coming back on its session is not a join.
/// </summary>
public static class RetryWhenAWorkerJoins
{
    /// <summary>Wires the retry to <paramref name="connections"/> when <paramref name="control"/>; returns whether it did.</summary>
    public static bool Wire(bool control, WorkerConnections connections, Func<CancellationToken, Task> retry, ILogger? log = null)
    {
        if (!control) return false;

        connections.Joined += worker => _ = Task.Run(async () =>
        {
            try
            {
                await retry(CancellationToken.None);
            }
            catch (Exception exception)
            {
                log?.LogWarning("Worker {Worker} joined; retrying the unfinished removals failed: {Message}", worker, exception.Message);
            }
        });

        return true;
    }
}
