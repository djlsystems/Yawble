using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;
using Harness.Pty;

namespace Harness.Tests;

/// <summary>
/// Control and one worker joined by a real loopback WebSocket, as two processes would be, in one test:
/// control's pool, streams, reads and terminal engine, and the worker's own connection and host with a
/// fake terminal engine. Control's timers are on a manual clock; dropping the link disposes the
/// worker end's socket, as a killed process's kernel would.
/// </summary>
internal sealed class WorkerStreamBed : IAsyncDisposable
{
    public const string Key = "the-worker-key-Vb3";

    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly WorkerTimings Timings = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<Link> _links = new();
    private Task<int>? _running;

    /// <param name="processes">How the worker reads process groups; a fixture tree in a test, never the live one.</param>
    public WorkerStreamBed(
        Func<IDictionary<string, string?>>? environment = null, AgentLaunchUser? runAs = null, ProcessGroupReader? processes = null)
    {
        Pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0, Clock), clock: Clock);
        Wip = new WipLedger(5, Pool);
        Directory = new RunDirectory(clock: Clock);
        Engine = new WorkerPtyEngine(Pool, Streams);
        Reads = new WorkerReads(Pool, Streams);
        Connections = new WorkerConnections(
            Pool, Wip,
            async (envelope, ct) =>
            {
                await Directory.HandleAsync(envelope, ct);
                await Streams.HandleAsync(envelope, ct);
                if (Events is { } events) await events(envelope, ct);
            },
            Directory.OpenOn, Key, takesWorkers: true, BuildVersion.Current.Version, timings: Timings, clock: Clock,
            streams: Streams.Deliver);

        Connection = new ControlConnection(
            new WorkerId("w1"), BuildVersion.Current.Version, Key,
            async ct =>
            {
                var (control, worker) = await Pair();
                _links.Enqueue(worker);
                _ = Connections.AcceptAsync(control.Socket, _stop.Token);
                return worker.Socket;
            },
            clock: Clock);

        var heartbeat = new RunHeartbeat();
        Host = new WorkerHost(
            new WorkerId("w1"), Connection, new RunLauncher(heartbeat, reports: false, lookup: LaunchLookup.Once), heartbeat,
            processes: processes, groups: new RunProcessGroups(),
            streaming: new WorkerStreaming(Connection, Pty, runAs ?? AgentLaunchUser.Same("tester", "the test runs everything as itself"), environment));

        Apply = Host.ApplyAsync;
        Connection.Apply = (message, ct) => Apply(message, ct);
        Connection.OpenRuns = Host.OpenRuns;
        Connection.Ready = Host.ReadyAsync;
        Connection.Input = input => Input(input);
    }

    public ManualTime Clock { get; } = new(DateTimeOffset.UnixEpoch.AddDays(1));

    public WorkerPool Pool { get; }

    public WipLedger Wip { get; }

    public RunDirectory Directory { get; }

    public WorkerStreams Streams { get; } = new();

    public WorkerPtyEngine Engine { get; }

    public WorkerReads Reads { get; }

    public WorkerConnections Connections { get; }

    public ControlConnection Connection { get; }

    public WorkerHost Host { get; }

    public FakePtyEngine Pty { get; } = new();

    /// <summary>What else control does with each event a worker sends; a test may measure with it.</summary>
    public Func<WorkerEnvelope, CancellationToken, Task>? Events { get; set; }

    /// <summary>How the worker applies control's commands; a test may hold one back.</summary>
    public Func<ControlMessage, CancellationToken, Task> Apply { get; set; }

    /// <summary>How the worker takes keystrokes; a test may record them.</summary>
    public Action<StreamInput> Input
    {
        get => _input ?? Host.Input;
        set => _input = value;
    }

    private Action<StreamInput>? _input;

    /// <summary>Control's handle on the worker, once it has connected.</summary>
    public RemoteWorker Remote => Connections.Workers().Single();

    /// <summary>Starts the worker and waits until control has it.</summary>
    public async Task StartAsync()
    {
        _running = Connection.RunAsync(_stop.Token);
        await Until(() => Connection.Connected && Pool.Workers.Count == 1);
    }

    /// <summary>Drops the connection, and waits until control has seen it dropped.</summary>
    public async Task DropAsync()
    {
        _links.Last().Drop();
        await Until(() => !Connection.Connected && Connections.Workers() is [{ Dropped: true }]);
    }

    /// <summary>Lets the worker reconnect, and waits until control has it again.</summary>
    public Task ReconnectAsync() =>
        Until(() =>
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            return Connection.Connected && Pool.Workers.Count == 1;
        });

    public static async Task Until(Func<bool> condition, TimeSpan? bound = null)
    {
        var limit = bound ?? Bound;
        var deadline = DateTime.UtcNow + limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Not true within {limit.TotalSeconds:0} s.");
            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopStreamsAsync();
        Connections.Stop();
        _stop.Cancel();
        if (_running is not null) await _running.WaitAsync(Bound);
    }

    /// <summary>Two ends of one loopback TCP connection, as WebSockets.</summary>
    private static async Task<(Link Control, Link Worker)> Pair()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await listener.AcceptTcpClientAsync();
        await connecting;

        return (
            new Link(server, WebSocket.CreateFromStream(server.GetStream(), isServer: true, null, Timeout.InfiniteTimeSpan)),
            new Link(client, WebSocket.CreateFromStream(client.GetStream(), isServer: false, null, Timeout.InfiniteTimeSpan)));
    }

    private sealed record Link(TcpClient Tcp, WebSocket Socket)
    {
        public void Drop() => Tcp.Dispose();
    }
}

/// <summary>A terminal engine that spawns nothing: each terminal records what it was spawned with and typed, and prints what the test says.</summary>
internal sealed class FakePtyEngine : IPtyEngine
{
    public ConcurrentQueue<FakePty> Spawned { get; } = new();

    /// <summary>The process id each terminal it spawns says its child has; none when null.</summary>
    public int? ProcessId { get; set; }

    public Task<IPtySession> SpawnAsync(PtySpec spec, CancellationToken ct)
    {
        var pty = new FakePty(spec) { ProcessId = ProcessId };
        Spawned.Enqueue(pty);
        return Task.FromResult<IPtySession>(pty);
    }
}

internal sealed class FakePty(PtySpec spec) : IPtySession, IPtyProcess
{
    /// <summary>The child's process id this terminal reports: a fixture's, never a live process.</summary>
    public int? ProcessId { get; set; }

    private readonly Lock _gate = new();
    private readonly List<byte> _typed = [];

    public PtySpec Spec => spec;

    public string Typed
    {
        get
        {
            lock (_gate) return System.Text.Encoding.UTF8.GetString([.. _typed]);
        }
    }

    public (int Cols, int Rows) Size { get; private set; } = (spec.Cols, spec.Rows);

    public bool Disposed { get; private set; }

    public event Action<byte[]>? Output;

    public event Action<int>? Exited;

    public void Print(string text) => Output?.Invoke(System.Text.Encoding.UTF8.GetBytes(text));

    public void Print(byte[] bytes) => Output?.Invoke(bytes);

    public void Exit(int code) => Exited?.Invoke(code);

    public void Write(ReadOnlySpan<byte> bytes)
    {
        lock (_gate) _typed.AddRange(bytes.ToArray());
    }

    public void Resize(int cols, int rows) => Size = (cols, rows);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
