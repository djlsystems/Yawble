using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// A task nobody awaits, observed: its fault is logged instead of lost. For the pushes and notices the
/// Host fires and forgets (a hub send, a settings push, a session's end), where an unobserved fault
/// would say nothing and, on a thread of its own, could end the process.
/// </summary>
public static class Observed
{
    public static void Forget(Task task, ILogger log, string what) =>
        _ = task.ContinueWith(
            t => log.LogWarning("{What} failed: {Error}", what, t.Exception?.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
