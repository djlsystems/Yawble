using System.Net.WebSockets;
using System.Threading.Channels;
using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>How long a worker may be gone and come back, how often it is asked whether it is there, and how long its hello may take.</summary>
public sealed record WorkerTimings(TimeSpan Grace, TimeSpan KeepAlive, TimeSpan Hello)
{
    public static WorkerTimings Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

    /// <summary>Unanswered pings after which a connection is taken as dropped.</summary>
    public const int MissedPings = 3;
}

/// <summary>
/// CONTROL'S HANDLE ON ONE WORKER IN A PROCESS OF ITS OWN, over its connection. A send completes when
/// the worker has applied the message, as in one process; the worker's events are handled one at a
/// time, in sequence order, each once, and acknowledged so the worker can let them go.
/// </summary>
/// <remarks>
/// <para>
/// CONNECTED, DROPPED, GONE. When the connection closes, errors, or misses
/// <see cref="WorkerTimings.MissedPings"/> pings in a row, the worker is DROPPED: it takes no new run,
/// and what control says to its runs meanwhile - the idle clocks, the heavy lease's holders, a stop -
/// is kept and said again when it is back. Back on the same session within
/// <see cref="WorkerTimings.Grace"/>, it carries on, and the events it published meanwhile arrive in
/// order. Not back in time, or back as a new process (<see cref="LoseAsRestarted"/>), it is GONE:
/// <see cref="Closed"/> completes with <see cref="Lost"/> saying why, and every run still open on it
/// fails <c>worker-lost</c>.
/// </para>
/// <para>
/// THE EVENTS ARE HANDLED APART FROM THE RECEIVING, so a handler that sends this worker a command is
/// answered: the frame that applies it is read while the handler waits.
/// </para>
/// </remarks>
public sealed class RemoteWorker : IRunWorker, IRunWorkerConnection
{
    private readonly Func<WorkerEnvelope, CancellationToken, Task> _control;
    private readonly WorkerTimings _timings;
    private readonly TimeProvider _clock;
    private readonly ILogger? _log;
    private readonly Action<RemoteWorker> _onDropped;
    private readonly Action<RemoteWorker> _onGone;

    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<long, (TaskCompletionSource Applied, ControlMessage Message)> _pending = [];
    private WorkerSocket? _socket;
    private int _generation;
    private long _nextId;
    private long _handled;
    private int _missed;
    private long _ping;
    private ITimer? _keepAlive;
    private ITimer? _grace;
    private string? _lost;
    private bool _gone;

    // What was said while the worker was dropped, said again when it is back: the latest of each.
    private ControlMessage? _settings;
    private ChangeRunMemoryAllowance? _heavy;
    private readonly Dictionary<ContainerId, bool> _holds = [];
    private readonly HashSet<ContainerId> _touches = [];
    private readonly List<CancelRun> _cancels = [];

    public RemoteWorker(
        WorkerInfo info,
        string session,
        Func<WorkerEnvelope, CancellationToken, Task> control,
        WorkerTimings timings,
        Action<RemoteWorker> dropped,
        Action<RemoteWorker> gone,
        TimeProvider? clock = null,
        ILogger? log = null)
    {
        Info = info;
        Session = session;
        _control = control;
        _timings = timings;
        _onDropped = dropped;
        _onGone = gone;
        _clock = clock ?? TimeProvider.System;
        _log = log;
    }

    public WorkerId Id => Info.Id;

    /// <summary>What the worker said of itself when it first connected.</summary>
    public WorkerInfo Info { get; }

    /// <summary>The worker process's session: a new process is a new session.</summary>
    public string Session { get; }

    /// <summary>The last event of this session control has handled.</summary>
    public long HandledSeq => Interlocked.Read(ref _handled);

    /// <summary>When the connection dropped, while it is dropped.</summary>
    public DateTimeOffset? DroppedAt { get; private set; }

    public Task Closed => _closed.Task;

    /// <summary>Pings sent since the worker last answered one.</summary>
    public int MissedPings => Volatile.Read(ref _missed);

    public bool Dropped
    {
        get
        {
            lock (_gate) return !_gone && _socket is null;
        }
    }

    public bool Gone
    {
        get
        {
            lock (_gate) return _gone;
        }
    }

    public string? Lost
    {
        get
        {
            lock (_gate) return _lost;
        }
    }

    public async Task SendAsync(ControlMessage message, CancellationToken ct = default)
    {
        TaskCompletionSource applied;
        WorkerSocket socket;
        long id;

        lock (_gate)
        {
            if (_gone) throw new InvalidOperationException($"The connection to worker {Id} is closed.");

            if (_socket is null)
            {
                Keep(message);
                return;
            }

            socket = _socket;
            id = ++_nextId;
            applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = (applied, message);
        }

        try
        {
            await socket.SendAsync(new CommandFrame(id, message), CancellationToken.None);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The connection is going: dropping it settles this send with the rest.
            socket.Abort();
        }

        await applied.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Runs the connection on <paramref name="socket"/> until it ends: what was kept while the worker
    /// was dropped is said again, the keep-alive starts, and the worker's frames are read.
    /// </summary>
    public async Task RunAsync(WorkerSocket socket, CancellationToken ct)
    {
        int generation;
        List<ControlMessage> again;
        lock (_gate)
        {
            if (_gone) return;

            _socket = socket;
            generation = ++_generation;
            DroppedAt = null;
            _missed = 0;
            _grace?.Dispose();
            _grace = null;
            _keepAlive = _clock.CreateTimer(_ => Safely(() => KeepAlive(generation)), null, _timings.KeepAlive, Timeout.InfiniteTimeSpan);
            again = KeptLocked();
        }

        foreach (var message in again) _ = SayAgainAsync(message);

        var events = Channel.CreateUnbounded<WorkerEnvelope>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var handling = Task.Run(() => HandleAsync(events.Reader, socket), CancellationToken.None);

        try
        {
            while (await socket.ReceiveAsync(ct) is { } frame)
            {
                switch (frame)
                {
                    case AppliedFrame applied:
                        Settle(applied);
                        break;

                    case EventFrame @event:
                        events.Writer.TryWrite(@event.Envelope);
                        break;

                    case PongFrame:
                        Interlocked.Exchange(ref _missed, 0);
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            _log?.LogWarning("Worker {Worker}'s connection ended: {Message}", Id, exception.Message);
            if (exception is InvalidDataException) await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, exception.Message);
        }
        finally
        {
            // Every event read before the drop is handled before the worker is taken as dropped.
            events.Writer.TryComplete();
            await handling;
            Drop(generation);
        }
    }

    /// <summary>
    /// Control is stopping: no timer of this worker's fires again, and nothing more is said about it.
    /// Its runs end as the Host's own stopping ends them.
    /// </summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            _grace?.Dispose();
            _keepAlive?.Dispose();
            _grace = null;
            _keepAlive = null;
            _stopped = true;
            _socket?.Abort();
        }
    }

    private bool _stopped;

    /// <summary>A timer's work, which must never throw on the timer's thread.</summary>
    private void Safely(Action work)
    {
        try
        {
            lock (_gate)
            {
                if (_stopped) return;
            }

            work();
        }
        catch (Exception exception)
        {
            _log?.LogWarning(exception, "Worker {Worker}'s timer failed.", Id);
        }
    }

    /// <summary>The worker came back as a new process: it is gone now, with every run it had.</summary>
    public void LoseAsRestarted()
    {
        lock (_gate)
        {
            if (_gone) return;
            _socket?.Abort();
        }

        Expire(null, RunDirectory.WorkerRestartedText(Id));
    }

    private async Task HandleAsync(ChannelReader<WorkerEnvelope> events, WorkerSocket socket)
    {
        await foreach (var envelope in events.ReadAllAsync())
        {
            // An event sent again after a reconnect, that control already handled, is handled once.
            if (envelope.Seq <= Interlocked.Read(ref _handled)) continue;

            try
            {
                await _control(envelope, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _log?.LogWarning(exception, "Handling worker {Worker}'s event {Seq} failed.", Id, envelope.Seq);
            }

            Interlocked.Exchange(ref _handled, envelope.Seq);

            try
            {
                await socket.SendAsync(new HandledFrame(envelope.Seq));
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Dropped: the worker learns what was handled from the welcome when it is back.
            }
        }
    }

    private void Settle(AppliedFrame applied)
    {
        TaskCompletionSource? done;
        lock (_gate)
        {
            done = _pending.Remove(applied.Id, out var pending) ? pending.Applied : null;
        }

        if (applied.Error is { } error) done?.TrySetException(new InvalidOperationException(error));
        else done?.TrySetResult();
    }

    private void KeepAlive(int generation)
    {
        WorkerSocket socket;
        bool drop;
        long n = 0;
        lock (_gate)
        {
            if (_gone || generation != _generation || _socket is null) return;
            socket = _socket;
            drop = Volatile.Read(ref _missed) >= WorkerTimings.MissedPings;

            if (!drop)
            {
                Interlocked.Increment(ref _missed);
                n = ++_ping;
                _keepAlive?.Change(_timings.KeepAlive, Timeout.InfiniteTimeSpan);
            }
        }

        if (drop)
        {
            // Taken as dropped now, not when the socket notices.
            _log?.LogWarning("Worker {Worker} answered none of {Missed} pings; its connection is taken as dropped.", Id, WorkerTimings.MissedPings);
            Drop(generation);
            socket.Abort();
            return;
        }

        _ = PingAsync(socket, n);
    }

    private static async Task PingAsync(WorkerSocket socket, long n)
    {
        try
        {
            await socket.SendAsync(new PingFrame(n));
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // A ping that cannot be sent is a miss like any other.
        }
    }

    private void Drop(int generation)
    {
        List<TaskCompletionSource> failed = [];
        lock (_gate)
        {
            if (_gone || _stopped || generation != _generation || _socket is null) return;

            _socket = null;
            DroppedAt = _clock.GetUtcNow();
            _keepAlive?.Dispose();
            _keepAlive = null;

            // A start or a sample cannot wait for the worker; anything else is kept and said again.
            foreach (var (applied, message) in _pending.Values)
            {
                if (message is StartRun or SampleCapacity)
                {
                    failed.Add(applied);
                }
                else
                {
                    Keep(message);
                    applied.TrySetResult();
                }
            }

            _pending.Clear();
            _grace = _clock.CreateTimer(_ => Safely(() => Expire(generation, null)), null, _timings.Grace, Timeout.InfiniteTimeSpan);
        }

        foreach (var applied in failed) applied.TrySetException(new InvalidOperationException($"The connection to worker {Id} dropped."));
        _onDropped(this);
    }

    /// <summary>Gone: past the grace (<paramref name="generation"/> still the dropped one), or at once as a new process.</summary>
    private void Expire(int? generation, string? restarted)
    {
        List<TaskCompletionSource> failed;
        lock (_gate)
        {
            if (_gone) return;
            if (generation is not null && (generation != _generation || _socket is not null)) return;

            _gone = true;
            _lost = restarted ?? RunDirectory.WorkerLostText(Id, DroppedAt ?? _clock.GetUtcNow(), _timings.Grace);
            _grace?.Dispose();
            _keepAlive?.Dispose();
            _socket = null;
            failed = [.. _pending.Values.Select(p => p.Applied)];
            _pending.Clear();
        }

        foreach (var applied in failed) applied.TrySetException(new InvalidOperationException($"The connection to worker {Id} is closed."));
        _closed.TrySetResult();
        _onGone(this);
    }

    private async Task SayAgainAsync(ControlMessage message)
    {
        try
        {
            await SendAsync(message);
        }
        catch (InvalidOperationException)
        {
            // Gone again: nothing more to say.
        }
    }

    /// <summary>Keeps what control says while the worker is dropped, the latest of each kind.</summary>
    private void Keep(ControlMessage message)
    {
        switch (message)
        {
            case StartRun or SampleCapacity:
                throw new InvalidOperationException($"Worker {Id} is not connected.");

            case ChangeRunMemoryAllowance heavy:
                _heavy = heavy;
                break;

            case HoldIdleClock hold:
                _holds[hold.Member] = hold.Held;
                break;

            case TouchIdleClock touch:
                _touches.Add(touch.Member);
                break;

            case CancelRun cancel:
                _cancels.Add(cancel);
                break;

            default:
                _settings = message;
                break;
        }
    }

    private List<ControlMessage> KeptLocked()
    {
        List<ControlMessage> kept = [];
        if (_settings is not null) kept.Add(_settings);
        if (_heavy is not null) kept.Add(_heavy);
        kept.AddRange(_holds.Select(h => new HoldIdleClock(h.Key, h.Value)));
        kept.AddRange(_touches.Select(m => new TouchIdleClock(m)));
        kept.AddRange(_cancels);

        _settings = null;
        _heavy = null;
        _holds.Clear();
        _touches.Clear();
        _cancels.Clear();
        return kept;
    }
}
