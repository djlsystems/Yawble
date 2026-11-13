namespace Yawble.Core.Launch;

/// <summary>A terminal size in character cells.</summary>
public readonly record struct PtySize(ushort Rows, ushort Cols)
{
    public static PtySize Default { get; } = new(40, 120);
}

/// <summary>How a sandbox process ended, as the sandbox reports it.</summary>
public readonly record struct SandboxExit(int ExitCode, bool TimedOut);

/// <summary>
/// Starts one sandbox from an exact compiled request. The request is the policy compiler's output, submitted
/// unchanged.
/// </summary>
public interface ISandboxBackend
{
    /// <summary>Short name for the run record ("yawble-ffi", "wxc-exec", "fake").</summary>
    string Name { get; }

    /// <summary>A headless run: stdout and stderr piped, stdin closed at once (the CLI must not wait on it).</summary>
    ISandboxProcess Spawn(string requestJson);

    /// <summary>An interactive run on a sandbox-owned PTY (SpawnWithPty): one output stream, one input stream.</summary>
    ISandboxProcess SpawnWithPty(string requestJson, PtySize size);
}

/// <summary>
/// A live sandbox. Every member except the streams is called from the supervisor under one lock, as the sandbox's
/// handle contract requires (microsoft/mxc src/ffi/mxc_ffi/src/streaming.rs "Concurrency contract" at v1.0.1). The streams
/// are separate objects and may be read and written on their own threads.
/// </summary>
public interface ISandboxProcess : IDisposable
{
    /// <summary>The OS process id the sandbox reports (on Linux: the bwrap process, the PID namespace's parent).</summary>
    int Id { get; }

    /// <summary>stdout, or the PTY's output for an interactive run. Null when there is none.</summary>
    Stream? Output { get; }

    /// <summary>stderr for a headless run; null for a PTY.</summary>
    Stream? Error { get; }

    /// <summary>The PTY's input for an interactive run; null for a headless run.</summary>
    Stream? Input { get; }

    /// <summary>Non-blocking: null while running.</summary>
    SandboxExit? TryWait();

    /// <summary>Kills the whole sandbox tree (the person's Stop, the idle watchdog).</summary>
    void Kill();

    /// <summary>Kills the whole sandbox tree because the wall-clock deadline passed; the sandbox then reports timed out.</summary>
    void KillForTimeout();

    void Resize(PtySize size);
}

/// <summary>Raised when the sandbox refuses or cannot start a sandbox. Status is the MXC_STATUS_* value.</summary>
public sealed class SandboxStartException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
