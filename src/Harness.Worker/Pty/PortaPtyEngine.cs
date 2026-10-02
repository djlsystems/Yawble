using Porta.Pty;

namespace Harness.Pty;

/// <summary>
/// <see cref="IPtyEngine"/> implementation backed by the Porta.Pty package.
/// Porta.Pty must not be referenced from any other file in the codebase; this
/// class is the sole seam so the PTY backend can be swapped by touching only it.
/// </summary>
public sealed class PortaPtyEngine : IPtyEngine
{
    public async Task<IPtySession> SpawnAsync(PtySpec spec, CancellationToken ct)
    {
        var options = new PtyOptions
        {
            Name = "Harness",
            Cwd = spec.StartingFolder,
            Cols = spec.Cols,
            Rows = spec.Rows,
        };

        if (spec.Argv is { Count: > 0 } argv)
        {
            // Already tokenized, so there is nothing to split and splitting would be actively
            // wrong: these elements came out of TemplateExpander, which put a whole system prompt
            // -- spaces, quotes and all -- into ONE element on purpose. execvp takes a real argv
            // array, so each element is passed as itself and no reassembly happens at all.
            options.App = argv[0];
            options.CommandLine = [.. argv.Skip(1)];
        }
        else
        {
            // execvp takes a real argv array and does its own PATH search for a bare name, so
            // splitting into tokens is the whole job.
            var (file, args) = CommandLineSplitter.Split(spec.CommandLine);
            options.App = file;
            options.CommandLine = args;
        }

        // A REAL TERMINAL'S MODES, NOT PORTA.PTY'S. Porta.Pty opens the terminal with output
        // processing off (-opost -onlcr), no echo and no line editing. A program that relies on the
        // defaults - a bare newline returning to the left edge - draws a staircase: the Copilot
        // Concierge does, while Claude, Codex and Grok send their own
        // carriage returns. `stty sane` puts the terminal where a real one starts, and the program
        // makes it raw itself if it wants. The child's own argv follows `exec` unchanged ("$@"), so
        // nothing is re-split or re-quoted. Pinned by PtyTerminalModeTests.
        if (!OperatingSystem.IsWindows())
        {
            options.CommandLine = ["-c", "stty sane 2>/dev/null; exec \"$@\"", "sh", options.App, .. options.CommandLine];
            options.App = "/bin/sh";
        }

        if (spec.Env is not null || spec.ClearEnvironment is not null)
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Porta.Pty merges with the host environment. Omitting a variable inherits it.
            if (spec.ClearEnvironment is not null)
                foreach (var name in spec.ClearEnvironment) environment[name] = string.Empty;
            if (spec.Env is not null)
                foreach (var (name, value) in spec.Env) environment[name] = value;
            options.Environment = environment;
        }

        var connection = await PtyProvider.SpawnAsync(options, ct).ConfigureAwait(false);
        return new PortaPtySession(connection);
    }
}

/// <summary>
/// <see cref="IPtySession"/> implementation wrapping a Porta.Pty <see cref="IPtyConnection"/>.
/// Pumps the reader stream on a dedicated background loop, raising <see cref="Output"/> for
/// each non-empty read, and raises <see cref="Exited"/> exactly once after the pump loop ends.
/// </summary>
internal sealed class PortaPtySession : IPtySession
{
    private readonly IPtyConnection _conn;
    private readonly Task _pumpTask;
    private readonly TaskCompletionSource<int> _exitCodeSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _exitLock = new();
    private bool _exited;
    private int _exitCode;
    private Action<int>? _exitedHandlers;
    private int _connClosed;
    private bool _disposed;

    // The pump loop (started from the constructor, below) can start reading and producing
    // output before SpawnAsync even returns to its caller, let alone before the caller has
    // had a chance to do `session.Output += ...`. Every byte read before the first subscriber
    // attaches is buffered here instead of handed to a no-op; the first subscription drains
    // the buffer (in order) before switching to live delivery. Unlike Exited (a one-shot
    // latch), Output can gain more subscribers later - only the FIRST subscriber triggers a
    // replay; later ones just join live delivery, same as a normal .NET event.
    //
    // Registration, buffer-drain, and dispatch (both the replay and every live chunk) all
    // happen under ONE lock, held across the actual handler invocation - not just the snapshot.
    // Releasing the lock before dispatching is NOT enough, however brief the critical section: a
    // payload big enough to make the pump loop's live path genuinely concurrent with a long replay
    // lands bytes out of order at a subscriber (and runs the replay foreach and a live invocation
    // of the same delegate on two threads at once). Holding the lock across dispatch is what
    // ChunkFanout.Append already does (invoked from TerminalSession's ChunkAppended under its
    // own _emitLock) - same convention, same requirement: every handler on this path MUST be
    // non-blocking, because a blocking handler here stalls this session's entire pump loop for
    // as long as it runs.
    private readonly Lock _outputLock = new();
    private readonly List<byte[]> _bufferedOutput = new();
    private bool _outputSubscribed;
    private Action<byte[]>? _outputHandlers;

    public event Action<byte[]> Output
    {
        add
        {
            using (_outputLock.EnterScope())
            {
                _outputHandlers += value;
                if (!_outputSubscribed)
                {
                    _outputSubscribed = true;

                    // Replay to the new handler directly (not via _outputHandlers) while still
                    // holding the lock: no chunk read by the pump loop from this point on can be
                    // dispatched (see RaiseOutput) until this replay finishes and the lock is
                    // released, so replay order and live order can never interleave.
                    foreach (var chunk in _bufferedOutput)
                    {
                        value(chunk);
                    }

                    _bufferedOutput.Clear();
                }
            }
        }
        remove
        {
            using (_outputLock.EnterScope())
                _outputHandlers -= value;
        }
    }

    /// <summary>
    /// Latches: raised exactly once, to whichever handlers are subscribed at the
    /// moment the pty exits. A handler that subscribes AFTER the exit has already
    /// latched is invoked immediately (once) with the stored exit code instead of
    /// being lost.
    /// </summary>
    public event Action<int> Exited
    {
        add
        {
            Action<int>? toInvokeImmediately = null;
            var code = 0;
            using (_exitLock.EnterScope())
            {
                if (_exited)
                {
                    toInvokeImmediately = value;
                    code = _exitCode;
                }
                else
                {
                    _exitedHandlers += value;
                }
            }

            toInvokeImmediately?.Invoke(code);
        }
        remove
        {
            using (_exitLock.EnterScope())
                _exitedHandlers -= value;
        }
    }

    public PortaPtySession(IPtyConnection conn)
    {
        _conn = conn;
        _conn.ProcessExited += OnProcessExited;
        _pumpTask = Task.Run(PumpLoopAsync);
    }

    private void OnProcessExited(object? sender, PtyExitedEventArgs e)
    {
        _exitCodeSource.TrySetResult(e.ExitCode);

        // The child process exiting does NOT make the reader stream hit EOF on its
        // own. Disposing the connection tears down the pseudo terminal and
        // force-closes the pipes, which is what lets the pending Read() return EOF
        // and the pump loop exit. Safe to call even if the consumer later disposes
        // the session too - Porta.Pty's Dispose() is idempotent.
        // NOT disposed inline. Disposing force-closes the pipes, and any bytes the child wrote
        // just before exiting that the pump has not read yet are destroyed with them. A child
        // that exits in microseconds (`echo`) can get here while the pump is still queued on the
        // thread pool. Give it a bounded window to drain, THEN tear down -- the blocking Read()
        // still never returns EOF on its own, so the dispose remains what ends the pump.
        _ = Task.Delay(DrainGrace).ContinueWith(_ => CloseConnection(), TaskScheduler.Default);
    }

    /// <summary>Idempotent teardown of the connection. Safe from the drain timer and DisposeAsync.</summary>
    private void CloseConnection()
    {
        if (Interlocked.Exchange(ref _connClosed, 1) == 1)
        {
            return;
        }

        try
        {
            _conn.Dispose();
        }
        catch
        {
            // Best-effort teardown; the pump loop's own read-error handling covers
            // any resulting stream failure.
        }
    }

    /// <summary>
    /// How long the pump is given to drain what the child wrote before the connection is torn
    /// down. Long enough for a queued pump to be scheduled and read; short enough not to be felt.
    /// </summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long the pump waits for an exit signal before falling back to the connection's own
    /// exit code. See the TimeoutException handler below for why waiting longer is pointless.
    /// </summary>
    private static readonly TimeSpan ExitSignalGrace = TimeSpan.FromMilliseconds(500);

    private int TryReadExitCode()
    {
        try
        {
            return _conn.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private async Task PumpLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                int n;
                try
                {
                    n = _conn.ReaderStream.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    // Reader stream torn down (process exited / connection disposed).
                    break;
                }

                if (n <= 0)
                {
                    break;
                }

                var chunk = new byte[n];
                Buffer.BlockCopy(buffer, 0, chunk, 0, n);
                RaiseOutput(chunk);
            }
        }
        finally
        {
            int exitCode;
            try
            {
                exitCode = await _exitCodeSource.Task.WaitAsync(ExitSignalGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Porta.Pty's ProcessExited is ONE-SHOT and is raised on a connection that is
                // already live when this class subscribes to it, so a child that exits fast
                // enough raises it into a void and it never arrives. That is not exceptional --
                // it is the common case for a short-lived child. Ask the connection for the code
                // instead of waiting out a signal that is not coming; a timed wait would turn every
                // such teardown into a stall with this thread parked.
                exitCode = TryReadExitCode();
            }

            RaiseExitedOnce(exitCode);
        }
    }

    private void RaiseOutput(byte[] chunk)
    {
        // Held across the invocation itself, not just the snapshot - see the comment on the
        // Output event. This is what makes it impossible for a live chunk to be dispatched while
        // a replay (in the add accessor, above) is still in progress, and impossible for two
        // chunks to be dispatched to the handlers concurrently from different threads.
        using (_outputLock.EnterScope())
        {
            if (!_outputSubscribed)
            {
                _bufferedOutput.Add(chunk);
                return;
            }

            try
            {
                _outputHandlers?.Invoke(chunk);
            }
            catch
            {
                // A throwing subscriber must not fault the pump loop / leave its
                // Task unobserved - the pump must keep reading regardless.
            }
        }
    }

    private void RaiseExitedOnce(int exitCode)
    {
        Action<int>? handlers;
        using (_exitLock.EnterScope())
        {
            if (_exited)
            {
                return;
            }

            _exited = true;
            _exitCode = exitCode;
            handlers = _exitedHandlers;
            _exitedHandlers = null;
        }

        handlers?.Invoke(exitCode);
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        // Best-effort: the connection may already be torn down (process exited /
        // session disposed) or may be torn down concurrently between this check and
        // the write below. Either way, a caller writing into a dead session should
        // get a silent no-op, not an ObjectDisposedException out of the core API.
        if (Volatile.Read(ref _connClosed) != 0)
        {
            return;
        }

        try
        {
            _conn.WriterStream.Write(bytes);

            // MUST flush. On Unix, Porta.Pty's WriterStream is a FileStream over the
            // pty controller fd with a small buffer, so an unflushed Write only lands
            // in a managed buffer - the symptom is a terminal that renders output
            // perfectly but silently ignores all input.
            _conn.WriterStream.Flush();
        }
        catch
        {
            // See comment above.
        }
    }

    public void Resize(int cols, int rows)
    {
        if (Volatile.Read(ref _connClosed) != 0)
        {
            return;
        }

        try
        {
            _conn.Resize(cols, rows);
        }
        catch
        {
            // See comment on Write.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _conn.Kill();
        }
        catch
        {
            // Already exited - nothing to kill.
        }

        // Dispose (tear down the pseudo terminal) BEFORE awaiting the pump task: the
        // pump loop's blocking Read() only returns once the connection is disposed
        // (see OnProcessExited) or errors out, so awaiting the pump task first would
        // deadlock whenever the process was still running at dispose time.
        CloseConnection();

        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch
        {
            // The pump loop swallows its own read/exit-wait errors; nothing further to surface.
        }
    }
}
