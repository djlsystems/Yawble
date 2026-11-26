using System.ComponentModel;
using System.Diagnostics;

namespace Yawble.Core.Teams;

/// <summary>
/// Waiting for a program the core started on this computer (the person's gh, glab, az or git, reaching a service over
/// the network): it gets a time limit, and when the limit runs out or the caller gives up, the program and every
/// process it started are stopped. A program that is only left behind would go on running, and holding its files and
/// its network call, after nobody waits for it.
/// </summary>
public static class HostProcess
{
    /// <summary>The limit for a provider CLI call (opening, reading or merging a pull request).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Waits for <paramref name="process"/> to exit, for at most <paramref name="timeout"/>.</summary>
    /// <returns>True when it exited by itself; false when the time ran out and it was stopped.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled: the program is stopped first.</exception>
    public static async Task<bool> WaitOrStopAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>Stops the program and the processes it started. One that has exited already, or cannot be reached, is left.</summary>
    public static void Stop(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException) { }
    }
}
