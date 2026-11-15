namespace Yawble.Core.Logs;

/// <summary>
/// The core's way out: the first request to stop says why, and that reason goes to the core's diagnostic log as
/// "core stopping: &lt;reason&gt;" at once (a SIGTERM may end the process before Main gets to write anything after it).
/// Later requests, such as the ProcessExit that follows every exit, change nothing.
/// </summary>
public sealed class CoreStop(Action<string> log)
{
    private readonly CancellationTokenSource _cts = new();
    private string? _reason;

    public const string Prefix = "core stopping: ";

    public CancellationToken Token => _cts.Token;

    /// <summary>Why the core is stopping (null while it is not).</summary>
    public string? Reason => Volatile.Read(ref _reason);

    public static string Line(string reason) => Prefix + reason;

    /// <summary>Stops the core; only the first call logs its reason.</summary>
    public void Request(string reason)
    {
        if (Interlocked.CompareExchange(ref _reason, reason, null) is null)
        {
            try { log(Line(reason)); } catch { /* the log is best effort: stopping is not */ }
        }
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }
}
