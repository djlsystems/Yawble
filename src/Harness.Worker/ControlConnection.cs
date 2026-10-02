using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>Control refused this worker before a connection opened, in a sentence the worker logs before it exits.</summary>
public sealed class ControlRefusedException(string sentence) : Exception(sentence);

/// <summary>
/// A WORKER'S ONE CONNECTION OUT TO CONTROL, and where its events go. It connects with the worker key,
/// says hello, applies control's commands in order and answers each, and publishes its events into an
/// outbox control acknowledges: an event is kept until control says it handled it, so a connection
/// that drops and comes back sends again, in order, everything control has not handled.
/// </summary>
/// <remarks>
/// <para>
/// RECONNECTS by itself, after 1 s, 2 s, then every 5 s, with the same session. A REFUSAL ENDS IT:
/// control's sentence (another build, a name in use, a wrong key) is logged and <see cref="RunAsync"/>
/// returns <see cref="RefusedExitCode"/>, so a worker that can never be taken does not knock forever.
/// </para>
/// <para>
/// GIVES UP, when given a time: with no welcomed connection for that long - counted from the start, or
/// from the drop of the last connection that was welcomed - <see cref="RunAsync"/> returns
/// <see cref="GaveUpExitCode"/>, so the engine's restart policy starts the worker again. On Docker a
/// worker shares control's network namespace, and one that outlived control's restart is in a dead one.
/// </para>
/// <para>
/// THE OUTBOX IS BOUNDED (<see cref="OutboxLimit"/> events or <see cref="OutboxBytes"/>). Past it this
/// worker can no longer prove what control saw, so it starts a new session and stops every run it
/// has: control takes the old session's runs as lost with their worker.
/// </para>
/// </remarks>
public sealed class ControlConnection : IRunEvents, IRunStreamSink
{
    /// <summary>What a worker process exits with when control refused it.</summary>
    public const int RefusedExitCode = 3;

    /// <summary>What a worker process exits with when control did not answer for its give-up time.</summary>
    public const int GaveUpExitCode = 4;

    public const int OutboxLimit = 10_000;

    public const long OutboxBytes = 64L * 1024 * 1024;

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly WorkerId _id;
    private readonly string _version;
    private readonly string _workerKey;
    private readonly Func<CancellationToken, Task<WebSocket>> _connect;
    private readonly Func<WorkerHelloCapacity> _capacity;
    private readonly TimeProvider _clock;
    private readonly ILogger? _log;
    private readonly int _outboxLimit;
    private readonly long _outboxBytes;
    private readonly TimeSpan? _giveUp;
    private readonly string _control;

    private readonly Lock _gate = new();
    private readonly LinkedList<(WorkerEnvelope Envelope, long Bytes)> _outbox = new();
    private readonly SemaphoreSlim _flush = new(1, 1);
    private long _bytes;
    private long _sent;
    private long _lastSeq;
    private WorkerSocket? _socket;
    private bool _ready;
    private int _connects;

    /// <param name="connect">Opens a WebSocket to control, presenting the worker key; throws <see cref="ControlRefusedException"/> when control refused the key.</param>
    public ControlConnection(
        WorkerId id,
        string version,
        string workerKey,
        Func<CancellationToken, Task<WebSocket>> connect,
        Func<WorkerHelloCapacity>? capacity = null,
        TimeProvider? clock = null,
        ILogger? log = null,
        int outboxLimit = OutboxLimit,
        long outboxBytes = OutboxBytes,
        TimeSpan? giveUp = null,
        string? control = null)
    {
        _id = id;
        _version = version;
        _workerKey = workerKey;
        _connect = connect;
        _capacity = capacity ?? (() => new WorkerHelloCapacity(null, null));
        _clock = clock ?? TimeProvider.System;
        _log = log;
        _outboxLimit = outboxLimit;
        _outboxBytes = outboxBytes;
        _giveUp = giveUp is { } after && after > TimeSpan.Zero ? after : null;
        _control = control ?? "control";
    }

    /// <summary>This worker process's session: new at each start, and when it gives up what it could not prove.</summary>
    public string Session { get; private set; } = Guid.NewGuid().ToString("N");

    /// <summary>Applies one of control's messages: the worker's own <c>ApplyAsync</c>.</summary>
    public Func<ControlMessage, CancellationToken, Task> Apply { get; set; } = (_, _) => Task.CompletedTask;

    /// <summary>The runs this worker has open now, for its hello.</summary>
    public Func<IReadOnlyList<RunId>> OpenRuns { get; set; } = () => [];

    /// <summary>Says the worker takes runs, once, after the first welcome: the worker's own <c>ReadyAsync</c>.</summary>
    public Func<CancellationToken, Task> Ready { get; set; } = _ => Task.CompletedTask;

    /// <summary>Takes a person's keystrokes for a terminal: the worker's own <c>Input</c>. Called as they arrive, outside the command queue.</summary>
    public Action<StreamInput> Input { get; set; } = _ => { };

    /// <summary>What control's welcome, or a later settings frame, said to run by.</summary>
    public Action<RunWorkerSettings> Settings { get; set; } = _ => { };

    /// <summary>
    /// Told true when control welcomed this worker and at every keep-alive it answers, false when the
    /// connection drops: what the worker's health file follows.
    /// </summary>
    public Action<bool> Alive { get; set; } = _ => { };

    /// <summary>Whether this worker takes no new run; said in every hello.</summary>
    public bool Draining
    {
        get
        {
            lock (_gate) return _draining;
        }
    }

    private bool _draining;

    /// <summary>Starts or ends draining, and tells control now when connected (else the next hello does).</summary>
    public async Task SetDrainingAsync(bool draining)
    {
        WorkerSocket? socket;
        lock (_gate)
        {
            if (_draining == draining) return;
            _draining = draining;
            socket = _socket;
        }

        _log?.LogInformation(draining
            ? "This worker is draining: it takes no new run, and the runs it has go on."
            : "This worker is no longer draining: it takes runs again.");
        if (socket is not null) await AnswerAsync(socket, new WorkerDraining(draining));
    }

    /// <summary>How many times a connection was opened, for a test that counts them.</summary>
    public int Connects => Volatile.Read(ref _connects);

    /// <summary>Whether a welcomed connection is open now.</summary>
    public bool Connected
    {
        get
        {
            lock (_gate) return _socket is not null;
        }
    }

    /// <summary>Events not yet acknowledged by control.</summary>
    public int Unacknowledged
    {
        get
        {
            lock (_gate) return _outbox.Count;
        }
    }

    /// <summary>
    /// Connects, and connects again whenever the connection drops, until <paramref name="ct"/> stops
    /// it (0) or control refuses this worker (<see cref="RefusedExitCode"/>).
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        var contact = _clock.GetUtcNow();

        while (!ct.IsCancellationRequested)
        {
            WebSocket webSocket;
            try
            {
                Interlocked.Increment(ref _connects);
                webSocket = await _connect(ct);
            }
            catch (ControlRefusedException refused)
            {
                _log?.LogError("Control refused this worker: {Sentence}", refused.Message);
                return RefusedExitCode;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                if (GaveUp(contact)) return GaveUpExitCode;
                var wait = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
                _log?.LogWarning("Could not reach control ({Message}); trying again in {Seconds} s.", exception.Message, wait.TotalSeconds);
                if (!await WaitAsync(wait, ct)) break;
                continue;
            }

            var (refusal, welcomed) = await SessionAsync(webSocket, ct);
            if (refusal is not null)
            {
                _log?.LogError("Control refused this worker: {Sentence}", refusal);
                return RefusedExitCode;
            }

            if (welcomed)
            {
                attempt = 0;
                contact = _clock.GetUtcNow();
            }
            else if (GaveUp(contact))
            {
                return GaveUpExitCode;
            }

            var delay = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
            _log?.LogWarning("The connection to control dropped; connecting again in {Seconds} s.", delay.TotalSeconds);
            if (!await WaitAsync(delay, ct)) break;
        }

        return 0;
    }

    /// <summary>Whether the give-up time has passed since <paramref name="contact"/>, said once when it has.</summary>
    private bool GaveUp(DateTimeOffset contact)
    {
        if (_giveUp is not { } after || _clock.GetUtcNow() - contact < after) return false;

        _log?.LogError("No answer from control at {Control} for {Seconds} s; this worker exits so the engine starts it again.", _control, (int)after.TotalSeconds);
        return true;
    }

    public async Task PublishAsync(WorkerEnvelope envelope, CancellationToken ct = default)
    {
        bool overflowed;
        lock (_gate)
        {
            var bytes = Size(envelope);
            _outbox.AddLast((envelope, bytes));
            _bytes += bytes;
            _lastSeq = envelope.Seq;
            overflowed = _outbox.Count > _outboxLimit || _bytes > _outboxBytes;
        }

        if (overflowed)
        {
            await GiveUpSessionAsync();
            return;
        }

        await FlushAsync();
    }

    /// <summary>
    /// Sends one stream chunk now, straight to the socket and never through the outbox: a chunk is not
    /// kept, so a terminal's output can never fill the outbox and cost this worker its runs. False when
    /// no welcomed connection is open, or the send failed; the caller decides what to keep.
    /// </summary>
    public async ValueTask<bool> StreamAsync(StreamChunk chunk, CancellationToken ct = default)
    {
        WorkerSocket? socket;
        lock (_gate) socket = _socket;
        if (socket is null) return false;

        try
        {
            await socket.SendAsync(new StreamFrame(chunk), ct);
            return true;
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Dropped: the reconnect settles the rest.
            socket.Abort();
            return false;
        }
    }

    /// <summary>One connection: hello, welcome or refusal, then commands in and events out until it ends.</summary>
    private async Task<(string? Refusal, bool Welcomed)> SessionAsync(WebSocket webSocket, CancellationToken ct)
    {
        using var socket = new WorkerSocket(webSocket, new WorkerFrameCodec(null));
        var nonce = WorkerFrameCodec.NewNonce();

        WorkerWelcome welcome;
        try
        {
            long last;
            bool draining;
            lock (_gate) (last, draining) = (_lastSeq, _draining);
            await socket.SendAsync(new WorkerHello(_id, _version, Session, nonce, _capacity(), OpenRuns(), last, draining), ct);

            switch (await socket.ReceiveAsync(ct))
            {
                case WorkerRefused refused:
                    return (refused.Sentence, false);
                case WorkerWelcome welcomed:
                    welcome = welcomed;
                    break;
                default:
                    return (null, false);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or InvalidDataException or OperationCanceledException)
        {
            _log?.LogWarning("The connection to control ended before its welcome: {Message}", exception.Message);
            return (null, false);
        }

        socket.Codec = new WorkerFrameCodec(WorkerFrameCodec.Key(_workerKey, welcome.Nonce, nonce));
        Settings(welcome.Settings);

        lock (_gate)
        {
            Trim(welcome.HandledSeq);
            _sent = welcome.HandledSeq;
            _socket = socket;
        }

        Alive(true);

        // What control has not handled goes first, in order; then this worker says it takes runs, once.
        await FlushAsync();
        if (!_ready)
        {
            _ready = true;
            await Ready(ct);
        }

        var commands = Channel.CreateUnbounded<CommandFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var applying = Task.Run(() => ApplyAsync(commands.Reader, socket), CancellationToken.None);
        string? refusal = null;

        try
        {
            while (await socket.ReceiveAsync(ct) is { } frame)
            {
                switch (frame)
                {
                    case CommandFrame command:
                        commands.Writer.TryWrite(command);
                        break;

                    case StreamInputFrame input:
                        try
                        {
                            Input(input.Input);
                        }
                        catch (Exception exception)
                        {
                            _log?.LogWarning("Keystrokes for {Stream} were not taken: {Message}", input.Input.Stream, exception.Message);
                        }

                        break;

                    case PingFrame ping:
                        _ = AnswerAsync(socket, new PongFrame(ping.N));
                        Alive(true);
                        break;

                    case WorkerSettingsFrame changed:
                        Settings(changed.Settings);
                        break;

                    case HandledFrame handled:
                        lock (_gate) Trim(handled.Seq);
                        break;

                    case WorkerRefused refused:
                        refusal = refused.Sentence;
                        break;
                }

                if (refusal is not null) break;
            }
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            _log?.LogWarning("The connection to control ended: {Message}", exception.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_socket, socket)) _socket = null;
            }

            Alive(false);
            commands.Writer.TryComplete();
            await applying;
        }

        return (refusal, true);
    }

    /// <summary>Applies control's commands one at a time, in order, and answers each.</summary>
    private async Task ApplyAsync(ChannelReader<CommandFrame> commands, WorkerSocket socket)
    {
        await foreach (var command in commands.ReadAllAsync())
        {
            string? error = null;
            try
            {
                await Apply(command.Message, CancellationToken.None);
            }
            catch (Exception exception)
            {
                // Its type crosses as text only: control never catches a worker's exception type.
                error = $"{exception.GetType().FullName}: {exception.Message}";
            }

            await AnswerAsync(socket, new AppliedFrame(command.Id, error));
        }
    }

    private static async Task AnswerAsync(WorkerSocket socket, WorkerFrame frame)
    {
        try
        {
            await socket.SendAsync(frame);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Dropped: the reconnect settles what control saw.
        }
    }

    /// <summary>Sends every event control has not been sent on this connection, in order.</summary>
    private async Task FlushAsync()
    {
        await _flush.WaitAsync();
        try
        {
            while (true)
            {
                WorkerSocket? socket;
                WorkerEnvelope? next;
                lock (_gate)
                {
                    socket = _socket;
                    next = _outbox.Select(e => e.Envelope).FirstOrDefault(e => e.Seq > _sent);
                }

                if (socket is null || next is null) return;

                try
                {
                    await socket.SendAsync(new EventFrame(next));
                }
                catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // Dropped: the event stays in the outbox and goes again after the reconnect.
                    socket.Abort();
                    return;
                }

                lock (_gate) _sent = Math.Max(_sent, next.Seq);
            }
        }
        finally
        {
            _flush.Release();
        }
    }

    /// <summary>
    /// The outbox overflowed: this worker can no longer prove what control saw. It starts a new
    /// session, forgets what it held, stops every run it has, and connects again.
    /// </summary>
    private async Task GiveUpSessionAsync()
    {
        WorkerSocket? socket;
        lock (_gate)
        {
            Session = Guid.NewGuid().ToString("N");
            _outbox.Clear();
            _bytes = 0;
            _sent = _lastSeq;
            socket = _socket;
            _socket = null;
        }

        _log?.LogError("More than {Limit} events went unacknowledged; this worker starts a new session and stops every run it had.", _outboxLimit);
        foreach (var run in OpenRuns())
        {
            try
            {
                await Apply(new CancelRun(run), CancellationToken.None);
            }
            catch (Exception exception)
            {
                _log?.LogWarning("Stopping run {Run} failed: {Message}", run.Key, exception.Message);
            }
        }

        socket?.Abort();
    }

    private void Trim(long handled)
    {
        while (_outbox.First is { } first && first.Value.Envelope.Seq <= handled)
        {
            _bytes -= first.Value.Bytes;
            _outbox.RemoveFirst();
        }
    }

    private static long Size(WorkerEnvelope envelope) =>
        256 + envelope.Event switch
        {
            RunOutput output => output.Text.Length * 2L,
            RunDiagnostic row => ((row.Message?.Length ?? 0) + (row.Detail?.Length ?? 0)) * 2L,
            RunProgress progress => progress.Sentence.Length * 2L,
            _ => 0,
        };

    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _clock, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the WebSocket to control's worker connection at <paramref name="control"/> with
    /// <paramref name="workerKey"/>. A refusal of the key is a <see cref="ControlRefusedException"/>
    /// with control's sentence.
    /// </summary>
    public static Func<CancellationToken, Task<WebSocket>> Connector(Uri control, string workerKey) => async ct =>
    {
        var client = new ClientWebSocket();
        client.Options.SetRequestHeader("X-Api-Key", workerKey);
        client.Options.CollectHttpResponseDetails = true;
        client.Options.KeepAliveInterval = TimeSpan.Zero;

        try
        {
            await client.ConnectAsync(new Uri(control, "/api/workers/connect"), ct);
            return client;
        }
        catch (WebSocketException) when (client.HttpStatusCode is HttpStatusCode.Unauthorized)
        {
            client.Dispose();
            throw new ControlRefusedException(WorkerSentences.WrongKey);
        }
        catch (WebSocketException) when (client.HttpStatusCode is HttpStatusCode.Forbidden)
        {
            client.Dispose();
            throw new ControlRefusedException(WorkerSentences.NotAWorker);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    };
}
