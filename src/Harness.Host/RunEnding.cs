using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE RUN-ENDING CHAIN, each step guarded on its own: the lease release, the terminal publish, the
/// default-branch report, the idle offer and the platform's declaration. Every way a run ends
/// reaches this, and a step that throws must not skip the ones after it - in control a worker that
/// drops as its run ends made the lease release throw, and the team branch was never pushed. A step
/// that throws is logged and the next one runs; a cancellation stops the chain, since the Host is
/// going down.
/// </summary>
public static class RunEnding
{
    /// <summary>
    /// What the offer and the declaration both read of the run's workflow, read once per run end. The
    /// two decide on the same thread of rows; before this each read the whole correlation for itself.
    /// </summary>
    public sealed class Reads
    {
        private long _correlation;
        private Task<IReadOnlyList<Message>>? _thread;

        public Task<IReadOnlyList<Message>> ThreadAsync(IMessageLog log, long correlation, CancellationToken ct)
        {
            if (_thread is null || _correlation != correlation)
            {
                _correlation = correlation;
                _thread = log.ReadCorrelationAsync(correlation, ct);
            }

            return _thread;
        }
    }

    public static async Task<bool> RunAsync(
        ILogger log, ContainerId member, long? causation, bool succeeded, CancellationToken ct,
        Func<Task> releaseLeases, Func<Task> publish, Func<Task> defaultBranch, Func<Reads, Task> idleOffer, Func<Reads, Task<bool>> declare)
    {
        var reads = new Reads();

        await StepAsync(log, member, "lease release", releaseLeases);
        await StepAsync(log, member, "terminal publish", publish);
        await StepAsync(log, member, "default-branch report", defaultBranch);
        await StepAsync(log, member, "idle offer", () => idleOffer(reads));

        try
        {
            return await declare(reads);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(log, member, "declaration", exception);
            return false;
        }
    }

    private static async Task StepAsync(ILogger log, ContainerId member, string step, Func<Task> run)
    {
        try
        {
            await run();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Warn(log, member, step, exception);
        }
    }

    private static void Warn(ILogger log, ContainerId member, string step, Exception exception) =>
        log.LogWarning("Run ending for {Team}/{Member}: {Step} failed: {Error}", member.Team, member.Name, step, exception.Message);
}
