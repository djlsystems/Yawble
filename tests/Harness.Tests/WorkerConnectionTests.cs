using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// A worker's connection to control, end to end over a real loopback socket pair, with every timer on
/// a manual clock: the hello and its refusals, the keep-alive, the drop, the grace, the reconnect that
/// sends again what control had not handled, and the stale run a reconnecting worker is told to stop.
/// A drop disposes the worker end's socket, as a killed process's kernel would.
/// </summary>
public sealed class WorkerConnectionTests
{
    private const string Key = "the-worker-key-Vb3";

    private static readonly string Version = BuildVersion.Current.Version;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly WorkerTimings Timings = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_worker_of_another_version_is_refused_naming_both_versions()
    {
        var bed = new Bed();
        var changed = bed.Wip.Changed;
        var (control, worker) = await Pair();
        var accepting = bed.Connections.AcceptAsync(control.Socket, Ct);

        var socket = new WorkerSocket(worker.Socket, new WorkerFrameCodec(null));
        await socket.SendAsync(Hello("w1", version: "0.0.0-test"), Ct);

        var refused = Assert.IsType<WorkerRefused>(await socket.ReceiveAsync(Ct));
        Assert.Equal(
            $"This worker is version 0.0.0-test and control is version {Version}; a worker must run control's version.",
            refused.Sentence);
        Assert.Null(await socket.ReceiveAsync(Ct));
        await accepting.WaitAsync(Bound, Ct);

        Assert.Empty(bed.Pool.Entries());
        Assert.Empty(bed.Connections.Workers());
        Assert.False(changed.IsCompleted);
        Assert.Contains(bed.Rows.Written, r => r.Kind == DiagnosticKinds.WorkerRefused && r.Message!.Contains("0.0.0-test"));
    }

    [Fact]
    public async Task A_second_worker_with_a_live_id_is_refused()
    {
        var bed = new Bed();
        var first = await bed.RawWorkerAsync(Hello("w1", session: "s1"));

        var (control, worker) = await Pair();
        var accepting = bed.Connections.AcceptAsync(control.Socket, Ct);
        var socket = new WorkerSocket(worker.Socket, new WorkerFrameCodec(null));
        await socket.SendAsync(Hello("w1", session: "s2"), Ct);

        var refused = Assert.IsType<WorkerRefused>(await socket.ReceiveAsync(Ct));
        Assert.StartsWith("A worker named w1 is already connected, since ", refused.Sentence);
        Assert.EndsWith("; give each worker its own HARNESS_WORKER_ID.", refused.Sentence);
        await accepting.WaitAsync(Bound, Ct);

        // The first is untouched.
        Assert.Equal([new WorkerId("w1")], bed.Pool.Workers);
        Assert.False(first.Remote.Dropped);
    }

    [Fact]
    public async Task No_hello_within_its_time_closes_the_connection()
    {
        var bed = new Bed();
        var (control, worker) = await Pair();
        var accepting = bed.Connections.AcceptAsync(control.Socket, Ct);

        bed.Clock.Advance(Timings.Hello - TimeSpan.FromSeconds(1));
        await Task.Delay(50, Ct);
        Assert.False(accepting.IsCompleted);

        await Until(() => { bed.Clock.Advance(TimeSpan.FromSeconds(1)); return accepting.IsCompleted; });
        await accepting;

        Assert.Empty(bed.Pool.Entries());
        Assert.Contains(bed.Rows.Written, r => r.Kind == DiagnosticKinds.WorkerRefused && r.Message == WorkerSentences.NoHello);
    }

    [Fact]
    public async Task A_refused_worker_reports_the_sentence_and_does_not_reconnect()
    {
        // Refused at the hello: control's own sentence.
        {
            var bed = new Bed();
            var log = new Lines();
            var connection = new ControlConnection(
                new WorkerId("w1"), "0.0.0-test", Key,
                async ct =>
                {
                    var (control, worker) = await Pair();
                    _ = bed.Connections.AcceptAsync(control.Socket, ct);
                    return worker.Socket;
                },
                clock: bed.Clock, log: log);

            var code = await connection.RunAsync(Ct).WaitAsync(Bound, Ct);

            Assert.Equal(ControlConnection.RefusedExitCode, code);
            Assert.Contains(log.Written, l => l.Contains($"This worker is version 0.0.0-test and control is version {Version}"));
            bed.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(1, connection.Connects);
        }

        // Refused at the door, for the key (401) or for not being a worker (403).
        foreach (var sentence in new[] { WorkerSentences.WrongKey, WorkerSentences.NotAWorker })
        {
            var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
            var log = new Lines();
            var connection = new ControlConnection(
                new WorkerId("w1"), Version, Key, _ => throw new ControlRefusedException(sentence), clock: clock, log: log);

            Assert.Equal(ControlConnection.RefusedExitCode, await connection.RunAsync(Ct).WaitAsync(Bound, Ct));
            Assert.Contains(log.Written, l => l.Contains(sentence));
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(1, connection.Connects);
        }
    }

    [Fact]
    public async Task Events_published_while_dropped_are_replayed_in_order_on_reconnect()
    {
        var bed = new Bed();
        var (connection, links, running) = bed.Worker("w1");
        await Until(() => connection.Connected && bed.Pool.Workers.Count == 1);

        for (var seq = 1; seq <= 3; seq++) await connection.PublishAsync(Envelope("w1", seq), Ct);
        await Until(() => bed.Handled.Count == 3 && connection.Unacknowledged == 0);

        // Dropped, as a killed process's socket would be: control sees it go.
        links.Last().Drop();
        await Until(() => !connection.Connected && bed.Pool.Workers.Count == 0);

        for (var seq = 4; seq <= 6; seq++) await connection.PublishAsync(Envelope("w1", seq), Ct);
        Assert.Equal(3, connection.Unacknowledged);

        // Back within the grace, on the same session: what control had not handled arrives, in order, once.
        await Until(() => { bed.Clock.Advance(TimeSpan.FromSeconds(1)); return connection.Connected && bed.Pool.Workers.Count == 1; });
        await Until(() => bed.Handled.Count == 6);
        await Until(() => connection.Unacknowledged == 0);

        Assert.Equal([1L, 2, 3, 4, 5, 6], bed.Handled.Select(e => e.Seq));
        Assert.Single(bed.Connections.Workers());
        Assert.False(bed.Connections.Workers()[0].Closed.IsCompleted);
        bed.Stop();
        await running.WaitAsync(Bound, Ct);
    }

    [Fact]
    public async Task A_reconnecting_worker_is_told_to_stop_a_run_control_already_ended()
    {
        var bed = new Bed();
        var stale = new RunId(new ContainerId("alpha", "Developer"), "ended-here");
        var applied = new ConcurrentQueue<ControlMessage>();
        var (connection, _, running) = bed.Worker("w1", open: () => [stale], apply: applied.Enqueue);

        await Until(() => applied.OfType<CancelRun>().Any(c => c.Run == stale));
        Assert.Contains(bed.Rows.Written, r => r.Kind == DiagnosticKinds.WorkerStaleRunStopped
            && r.Message == "Stopped run alpha/Developer that worker w1 still held after control had ended it.");

        bed.Stop();
        await running.WaitAsync(Bound, Ct);
    }

    [Fact]
    public async Task A_worker_that_misses_three_pings_is_dropped()
    {
        var bed = new Bed();
        var raw = await bed.RawWorkerAsync(Hello("w1"));

        // It never answers. Each tick sends a ping; at the fourth, three have gone unanswered.
        bed.Clock.Advance(Timings.KeepAlive);
        bed.Clock.Advance(Timings.KeepAlive);
        bed.Clock.Advance(Timings.KeepAlive);
        Assert.False(raw.Remote.Dropped);
        Assert.Equal([new WorkerId("w1")], bed.Pool.Workers);

        bed.Clock.Advance(Timings.KeepAlive);
        Assert.True(raw.Remote.Dropped);
        Assert.Empty(bed.Pool.Workers);
        Assert.False(raw.Remote.Closed.IsCompleted);
    }

    [Fact]
    public async Task A_worker_that_answers_pings_stays_connected_however_long_idle()
    {
        var bed = new Bed();
        var (connection, _, running) = bed.Worker("w1");
        await Until(() => connection.Connected && bed.Connections.Workers().Count == 1);
        var remote = bed.Connections.Workers()[0];

        for (var tick = 0; tick < 30; tick++)
        {
            bed.Clock.Advance(Timings.KeepAlive);
            await Until(() => remote.MissedPings == 0);
        }

        Assert.False(remote.Dropped);
        Assert.Equal([new WorkerId("w1")], bed.Pool.Workers);
        bed.Stop();
        await running.WaitAsync(Bound, Ct);
    }

    [Fact]
    public async Task A_worker_gone_past_its_grace_is_lost_and_leaves_the_pool()
    {
        var bed = new Bed();
        var raw = await bed.RawWorkerAsync(Hello("w1"));
        var changed = bed.Wip.Changed;

        raw.Link.Drop();
        await Until(() => raw.Remote.Dropped);
        var droppedAt = raw.Remote.DroppedAt!.Value;

        bed.Clock.Advance(Timings.Grace - TimeSpan.FromSeconds(1));
        Assert.False(raw.Remote.Closed.IsCompleted);

        bed.Clock.Advance(TimeSpan.FromSeconds(1));
        await raw.Remote.Closed.WaitAsync(Bound, Ct);
        Assert.Equal(RunDirectory.WorkerLostText(new WorkerId("w1"), droppedAt, Timings.Grace), raw.Remote.Lost);
        Assert.Empty(bed.Pool.Entries());
        Assert.Empty(bed.Connections.Workers());
        Assert.True(changed.IsCompleted);
    }

    [Fact]
    public async Task A_stopping_control_fires_no_timer_of_its_workers_again()
    {
        var bed = new Bed();
        var raw = await bed.RawWorkerAsync(Hello("w1"));
        raw.Link.Drop();
        await Until(() => raw.Remote.Dropped);

        // The Host stops while the worker is in its grace: nothing fires after, into a Host that is gone.
        var changed = bed.Wip.Changed;
        bed.Connections.Stop();
        bed.Clock.Advance(Timings.Grace + Timings.KeepAlive);

        Assert.False(raw.Remote.Closed.IsCompleted);
        Assert.False(changed.IsCompleted);
    }

    [Fact]
    public async Task A_worker_back_as_a_new_session_loses_its_runs_at_once()
    {
        var bed = new Bed();
        var old = await bed.RawWorkerAsync(Hello("w1", session: "s1"));
        var start = new StartRun(
            RunId.For(new ContainerId("alpha", "Developer")), "probe", "", "", "", "/", new Dictionary<string, string>(), null, null, null, null, null);
        var running = bed.Directory.RunAsync(old.Remote, start, Ct);
        await old.AnswerStartAsync();

        old.Link.Drop();
        await Until(() => old.Remote.Dropped);

        // A new process under the same name: no waiting out the grace.
        var renewed = await bed.RawWorkerAsync(Hello("w1", session: "s2"));
        var result = await running.WaitAsync(Bound, Ct);

        Assert.Equal(FailureClasses.WorkerLost, result.FailureClass);
        Assert.Equal(RunDirectory.WorkerRestartedText(new WorkerId("w1")), result.LaunchError);
        Assert.True(old.Remote.Closed.IsCompleted);
        Assert.Same(renewed.Remote, Assert.Single(bed.Connections.Workers()));
        Assert.Equal([new WorkerId("w1")], bed.Pool.Workers);
    }

    private static WorkerHello Hello(string id, string? version = null, string session = "s1", IReadOnlyList<RunId>? open = null) =>
        new(new WorkerId(id), version ?? Version, session, WorkerFrameCodec.NewNonce(), new WorkerHelloCapacity(4, 8_000_000_000), open ?? [], 0);

    private static WorkerEnvelope Envelope(string worker, long seq) =>
        new(new WorkerId(worker), seq, new RunProgress(new RunId(new ContainerId("alpha", "Developer"), "n"), $"event {seq}"));

    private async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Not true within {Bound.TotalSeconds:0} s.");
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>Two ends of one loopback TCP connection, as WebSockets; dropping one disposes its socket.</summary>
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

    private sealed class Bed
    {
        private readonly CancellationTokenSource _stop = new();

        public Bed()
        {
            Pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0, Clock), clock: Clock);
            Wip = new WipLedger(5, Pool);
            Directory = new RunDirectory(diagnostics: Rows, clock: Clock);
            Connections = new WorkerConnections(
                Pool, Wip,
                async (envelope, ct) =>
                {
                    Handled.Enqueue(envelope);
                    await Directory.HandleAsync(envelope, ct);
                },
                Directory.OpenOn, Key, takesWorkers: true, Version, timings: Timings, clock: Clock, diagnostics: Rows);
        }

        public ManualTime Clock { get; } = new(DateTimeOffset.UnixEpoch.AddDays(1));

        public WorkerPool Pool { get; }

        public WipLedger Wip { get; }

        public RunDirectory Directory { get; }

        public WorkerConnections Connections { get; }

        public Rows Rows { get; } = new();

        public ConcurrentQueue<WorkerEnvelope> Handled { get; } = new();

        public void Stop() => _stop.Cancel();

        /// <summary>A worker's own connection to this control, each connect a fresh socket pair.</summary>
        public (ControlConnection Connection, ConcurrentQueue<Link> Links, Task<int> Running) Worker(
            string id, Func<IReadOnlyList<RunId>>? open = null, Action<ControlMessage>? apply = null)
        {
            var links = new ConcurrentQueue<Link>();
            var connection = new ControlConnection(
                new WorkerId(id), Version, Key,
                async ct =>
                {
                    var (control, worker) = await Pair();
                    links.Enqueue(worker);
                    _ = Connections.AcceptAsync(control.Socket, _stop.Token);
                    return worker.Socket;
                },
                clock: Clock)
            {
                OpenRuns = open ?? (() => []),
                Apply = (message, _) =>
                {
                    apply?.Invoke(message);
                    return Task.CompletedTask;
                },
            };

            return (connection, links, connection.RunAsync(_stop.Token));
        }

        /// <summary>A worker that says its hello and reads its welcome by hand, and then does only what the test does.</summary>
        public async Task<Raw> RawWorkerAsync(WorkerHello hello)
        {
            var (control, worker) = await Pair();
            _ = Connections.AcceptAsync(control.Socket, _stop.Token);
            var socket = new WorkerSocket(worker.Socket, new WorkerFrameCodec(null));
            await socket.SendAsync(hello);
            Assert.IsType<WorkerWelcome>(await socket.ReceiveAsync());

            var deadline = DateTime.UtcNow + Bound;
            RemoteWorker? remote = null;
            while ((remote = Connections.Workers().FirstOrDefault(w => w.Session == hello.Session && !w.Dropped)) is null)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The worker was not welcomed.");
                await Task.Delay(10);
            }

            return new Raw(worker, socket, remote);
        }
    }

    private sealed record Raw(Link Link, WorkerSocket Socket, RemoteWorker Remote)
    {
        /// <summary>Applies the next command it is sent, a start, and says the run started.</summary>
        public async Task AnswerStartAsync()
        {
            var command = Assert.IsType<CommandFrame>(await Socket.ReceiveAsync());
            var start = Assert.IsType<StartRun>(command.Message);
            await Socket.SendAsync(new AppliedFrame(command.Id, null));
            await Socket.SendAsync(new EventFrame(new WorkerEnvelope(Remote.Id, 1, new RunStarted(start.Run, 4242, DateTimeOffset.UnixEpoch))));
        }
    }

    private sealed class Rows : IDiagnosticsLog
    {
        public ConcurrentQueue<(string Kind, string? Message)> Written { get; } = new();

        public Task WriteAsync(
            DiagnosticSeverity severity, string kind, string? source = null, string? route = null, int? status = null,
            string? exceptionType = null, string? message = null, string? detail = null, CancellationToken ct = default)
        {
            Written.Enqueue((kind, message));
            return Task.CompletedTask;
        }

        public Task<DiagnosticsPage> ReadAsync(DiagnosticsFilter? filter = null, long? before = null, int take = 50, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool?> HasAnyAsync(CancellationToken ct = default) => Task.FromResult<bool?>(!Written.IsEmpty);

        public Task TrimAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Lines : ILogger
    {
        public ConcurrentQueue<string> Written { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Written.Enqueue(formatter(state, exception));
    }
}
