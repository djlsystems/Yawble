using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Harness.Contracts;
using Harness.Pty;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// A PERSON'S TERMINAL, STARTED ON A WORKER AND RELAYED BY CONTROL. Control keeps everything about the
/// session - the browser's socket, the replay, who is attached, the credential and the lease - and this
/// engine is the one thing that changed under it: the CLI's process is a worker's. The connected worker
/// with the most measured headroom starts it; its output arrives as a stream, keystrokes go back
/// outside the command queue, and a resize, a stop and the end are ordered messages.
/// </summary>
/// <remarks>
/// <para>
/// THE LAUNCH IS STAGED. <see cref="ConciergeSessionStore"/> takes a <see cref="PtySpec"/> from its
/// launch and spawns it here; the spec a worker needs to make the terminal is the
/// <see cref="TerminalLaunch"/> control resolved, which <see cref="Stage"/> records against the spec's
/// environment. The store's own <c>with</c> for the size keeps that environment, so the spec it hands
/// back still finds its launch. A spec nobody staged is sent as it is.
/// </para>
/// <para>
/// NO WORKER: <see cref="IRunWorkerRouter.Any"/>'s "No worker is connected." is thrown, which the
/// socket route has already waited out (<see cref="WaitingText"/>).
/// </para>
/// </remarks>
public sealed class WorkerPtyEngine(WorkerPool pool, WorkerStreams streams, TimeProvider? clock = null, ILogger? log = null) : IPtyEngine
{
    /// <summary>What a person opening the Concierge sees while no worker is connected.</summary>
    public const string WaitingText = "Waiting for a worker: the Concierge starts on a worker, and none is connected.";

    /// <summary>How long ending a session waits for its worker to say the terminal ended.</summary>
    public static readonly TimeSpan StopBound = TimeSpan.FromSeconds(10);

    /// <summary>What a session says when the worker running it is lost.</summary>
    public static string LostText(WorkerId worker) =>
        $"The worker running this Concierge ({worker}) stopped, so this session ended. Open the Concierge again to start a new one.";

    private readonly ConditionalWeakTable<IReadOnlyDictionary<string, string>, TerminalLaunch> _staged = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The spec the session store spawns <paramref name="launch"/> by.</summary>
    public PtySpec Stage(TerminalLaunch launch)
    {
        _staged.AddOrUpdate(launch.Environment, launch);
        return new PtySpec(
            launch.Argv.Count > 0 ? launch.Argv[0] : string.Empty,
            launch.StartingFolder,
            Env: launch.Environment,
            Argv: launch.Argv,
            ClearEnvironment: launch.ClearEnvironment);
    }

    public async Task<IPtySession> SpawnAsync(PtySpec spec, CancellationToken ct)
    {
        var launch = spec.Env is { } environment && _staged.TryGetValue(environment, out var staged)
            ? staged
            : new TerminalLaunch(
                spec.Argv ?? [.. SplitCommandLine(spec.CommandLine)],
                spec.StartingFolder,
                spec.Env ?? new Dictionary<string, string>(),
                spec.ClearEnvironment ?? [],
                null,
                null,
                null);

        var worker = pool.Worker(null);
        var id = "terminal:" + Guid.NewGuid().ToString("N");
        var stream = streams.Open(id, worker, whole: false);

        try
        {
            await worker.SendAsync(new StartTerminal(id, launch, spec.Cols, spec.Rows), ct);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return new WorkerPtySession(id, worker, stream, _clock, log);
    }

    private static string[] SplitCommandLine(string commandLine) =>
        commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// A terminal on a worker, as the session store sees any terminal. Output is raised from the stream's
/// chunks in order; the end from the worker's <see cref="TerminalEnded"/>, after the last output, or
/// from its worker being lost, after a line saying so. Keystrokes and resizes are sent in the order they
/// were made, each on its own lane.
/// </summary>
public sealed class WorkerPtySession : IPtySession
{
    private readonly string _id;
    private readonly IRunWorker _worker;
    private readonly WorkerStream _stream;
    private readonly TimeProvider _clock;
    private readonly ILogger? _log;
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<(int Cols, int Rows)> _resizes = Channel.CreateUnbounded<(int, int)>(new UnboundedChannelOptions { SingleReader = true });

    // Output read before the store subscribes is kept and replayed to the first subscriber, under the
    // one lock every dispatch holds: the order PortaPtySession keeps.
    private readonly Lock _outputLock = new();
    private readonly List<byte[]> _buffered = [];
    private bool _subscribed;
    private Action<byte[]>? _output;

    private readonly Lock _exitLock = new();
    private bool _exited;
    private int _exitCode;
    private Action<int>? _exitedHandlers;
    private int _disposed;

    internal WorkerPtySession(string id, IRunWorker worker, WorkerStream stream, TimeProvider clock, ILogger? log)
    {
        _id = id;
        _worker = worker;
        _stream = stream;
        _clock = clock;
        _log = log;
        _ = Task.Run(ReadAsync);
        _ = Task.Run(TypeAsync);
        _ = Task.Run(ResizeAsync);
    }

    /// <summary>The worker this terminal runs on.</summary>
    public WorkerId Worker => _worker.Id;

    public event Action<byte[]> Output
    {
        add
        {
            using (_outputLock.EnterScope())
            {
                _output += value;
                if (_subscribed) return;

                _subscribed = true;
                foreach (var chunk in _buffered) value(chunk);
                _buffered.Clear();
            }
        }
        remove
        {
            using (_outputLock.EnterScope()) _output -= value;
        }
    }

    public event Action<int> Exited
    {
        add
        {
            int? now = null;
            using (_exitLock.EnterScope())
            {
                if (_exited) now = _exitCode;
                else _exitedHandlers += value;
            }

            if (now is { } code) value(code);
        }
        remove
        {
            using (_exitLock.EnterScope()) _exitedHandlers -= value;
        }
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (Volatile.Read(ref _disposed) == 0) _input.Writer.TryWrite(bytes.ToArray());
    }

    public void Resize(int cols, int rows)
    {
        if (Volatile.Read(ref _disposed) == 0) _resizes.Writer.TryWrite((cols, rows));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _input.Writer.TryComplete();
        _resizes.Writer.TryComplete();

        if (!_stream.Ended.IsCompleted)
        {
            // Bounded: with the worker dropped or silent the session still ends, and the lease with it.
            try
            {
                await _worker.SendAsync(new StopTerminal(_id)).WaitAsync(WorkerPtyEngine.StopBound, _clock);
                await _stream.Ended.WaitAsync(WorkerPtyEngine.StopBound, _clock);
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
            {
                _log?.LogWarning("Terminal {Session} on worker {Worker} did not say it ended: {Message}", _id, _worker.Id, exception.Message);
            }
        }

        _stream.Dispose();
    }

    private async Task ReadAsync()
    {
        var code = -1;
        try
        {
            await foreach (var chunk in _stream.Reader.ReadAllAsync())
            {
                if (chunk.End)
                {
                    if (_stream.Lost is not null) Line(WorkerPtyEngine.LostText(_worker.Id));
                    else if (chunk.Gap is { } why) Line(why);
                    else if (await _stream.Ended is TerminalEnded ended) code = ended.ExitCode;
                    break;
                }

                if (chunk.Gap is { } gap) Line(gap);
                if (chunk.Bytes is { Length: > 0 } bytes) Raise(bytes);
            }
        }
        catch (Exception exception)
        {
            _log?.LogWarning("Terminal {Session}'s output failed: {Message}", _id, exception.Message);
        }

        RaiseExited(code);
    }

    private async Task TypeAsync()
    {
        await foreach (var bytes in _input.Reader.ReadAllAsync())
        {
            try
            {
                if (_worker is IRunWorkerInput input) await input.InputAsync(new StreamInput(_id, bytes));
            }
            catch (Exception exception)
            {
                _log?.LogWarning("Keystrokes for terminal {Session} were not sent: {Message}", _id, exception.Message);
            }
        }
    }

    private async Task ResizeAsync()
    {
        await foreach (var (cols, rows) in _resizes.Reader.ReadAllAsync())
        {
            try
            {
                await _worker.SendAsync(new ResizeTerminal(_id, cols, rows));
            }
            catch (InvalidOperationException)
            {
                // Not connected: the next resize says the size again.
            }
        }
    }

    private void Line(string sentence) => Raise(Encoding.UTF8.GetBytes("\r\n" + sentence + "\r\n"));

    private void Raise(byte[] chunk)
    {
        using (_outputLock.EnterScope())
        {
            if (!_subscribed)
            {
                _buffered.Add(chunk);
                return;
            }

            try
            {
                _output?.Invoke(chunk);
            }
            catch (Exception exception)
            {
                _log?.LogWarning("A viewer of terminal {Session} failed: {Message}", _id, exception.Message);
            }
        }
    }

    private void RaiseExited(int code)
    {
        Action<int>? handlers;
        using (_exitLock.EnterScope())
        {
            if (_exited) return;
            _exited = true;
            _exitCode = code;
            handlers = _exitedHandlers;
            _exitedHandlers = null;
        }

        handlers?.Invoke(code);
    }
}
