using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A REQUEST TO A DROPPED WORKER IS REFUSED, NOT KEPT. A request is answered by an event its caller
/// waits for within a bound; one kept while the worker is dropped would make the caller wait out that
/// bound for nothing, and be applied after it gave up. So the send throws at once, and the worker
/// that comes back is never sent it - while what control says to its runs is still said again.
/// Over a real loopback socket pair, on a manual clock.
/// </summary>
public sealed class RemoteWorkerRequestTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly ContainerId Member = new("alpha", "Developer");

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Requests() => [.. RequestsByName.Keys];

    private static readonly Dictionary<string, ControlMessage> RequestsByName = new(StringComparer.Ordinal)
    {
        ["CheckLaunch"] = new CheckLaunch(
            "r1", new RunLaunch("cli", ["--version"], null, null, null, true, null, null, 60, []), ["--version"], [],
            new Dictionary<string, string>(), null, 60, null),
        ["ProbeSignIn"] = new ProbeSignIn("r2", [new SignInProbeSpec("claude", null, null, ["auth", "status"], null, null)]),
        ["RunAgentCommands"] = new RunAgentCommands("r3", [new AgentCliRun("claude", ["--version"], new Dictionary<string, string>(), [], 30)]),
        ["RemoveAsAgent"] = new RemoveAsAgent("r4", "/data/teams/t", ["/data/teams/t/workspaces/a"]),
        ["StartTerminal"] = new StartTerminal(
            "s1", new TerminalLaunch(["cli"], "/data/home", new Dictionary<string, string>(), [], null, null, null), 80, 24),
        ["ResizeTerminal"] = new ResizeTerminal("s1", 120, 40),
        ["FollowTranscript"] = new FollowTranscript("f1", new RunId(Member, "n1"), "/data/runs/1/transcript.jsonl", "claude"),
        ["ReadAgentFile"] = new ReadAgentFile("r5", "/data/runs/1/transcript.jsonl", [], Redact: true),
    };

    public static TheoryData<string> Ends() => [.. EndsByName.Keys];

    /// <summary>Ends of a terminal or a tail: nothing waits for them, and the worker that comes back must still hear them.</summary>
    private static readonly Dictionary<string, ControlMessage> EndsByName = new(StringComparer.Ordinal)
    {
        ["StopTerminal"] = new StopTerminal("s1"),
        ["StopStream"] = new StopStream("f1"),
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task A_request_sent_while_the_worker_is_dropped_is_refused_not_replayed(string name)
    {
        var request = RequestsByName[name];
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var remote = new RemoteWorker(
            new WorkerInfo(new WorkerId("w1"), null, 4, 8_000_000_000, DateTimeOffset.UnixEpoch),
            "s1", (_, _) => Task.CompletedTask, WorkerTimings.Default, _ => { }, _ => { }, clock);

        // Connected, then dropped, as a killed process's socket would be.
        var (control, worker) = await Pair();
        var first = remote.RunAsync(new WorkerSocket(control.Socket, new WorkerFrameCodec(null)), Ct);
        worker.Drop();
        await first.WaitAsync(Bound, Ct);
        Assert.True(remote.Dropped);

        // What control says to a run is kept; the request is refused at once.
        await remote.SendAsync(new HoldIdleClock(Member, true), Ct);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => remote.SendAsync(request, Ct).WaitAsync(Bound, Ct));
        Assert.Equal("Worker w1 is not connected.", refused.Message);

        // Back: the kept message is said again, and the request never.
        (control, worker) = await Pair();
        var socket = new WorkerSocket(worker.Socket, new WorkerFrameCodec(null));
        var second = remote.RunAsync(new WorkerSocket(control.Socket, new WorkerFrameCodec(null)), Ct);

        var received = await SaidAgainAsync(remote, socket, typeof(HoldIdleClock));

        Assert.DoesNotContain(received, m => m.GetType() == request.GetType());
        Assert.Equal([typeof(HoldIdleClock)], received.Select(m => m.GetType()));

        remote.Shutdown();
        await second.WaitAsync(Bound, Ct);
    }

    [Theory]
    [MemberData(nameof(Ends))]
    public async Task An_end_sent_while_the_worker_is_dropped_is_said_again_when_it_is_back(string name)
    {
        var end = EndsByName[name];
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var remote = new RemoteWorker(
            new WorkerInfo(new WorkerId("w1"), null, 4, 8_000_000_000, DateTimeOffset.UnixEpoch),
            "s1", (_, _) => Task.CompletedTask, WorkerTimings.Default, _ => { }, _ => { }, clock);

        var (control, worker) = await Pair();
        var first = remote.RunAsync(new WorkerSocket(control.Socket, new WorkerFrameCodec(null)), Ct);
        worker.Drop();
        await first.WaitAsync(Bound, Ct);
        Assert.True(remote.Dropped);

        // Accepted while dropped, not refused.
        await remote.SendAsync(end, Ct).WaitAsync(Bound, Ct);

        (control, worker) = await Pair();
        var socket = new WorkerSocket(worker.Socket, new WorkerFrameCodec(null));
        var second = remote.RunAsync(new WorkerSocket(control.Socket, new WorkerFrameCodec(null)), Ct);

        List<ControlMessage> received = [];
        while (received.Count == 0 || received[^1].GetType() != end.GetType())
        {
            var command = Assert.IsType<CommandFrame>(await socket.ReceiveAsync(Ct).WaitAsync(Bound, Ct));
            received.Add(command.Message);
            await socket.SendAsync(new AppliedFrame(command.Id, null), Ct);
        }

        Assert.Equal(end, received[^1]);

        remote.Shutdown();
        await second.WaitAsync(Bound, Ct);
    }

    public static TheoryData<string> InFlight() => [.. RequestsByName.Keys, .. KeptByName.Keys];

    /// <summary>What control says to a worker's runs, or ends there: kept while the worker is dropped and said again.</summary>
    private static readonly Dictionary<string, ControlMessage> KeptByName = new(StringComparer.Ordinal)
    {
        ["ChangeRunMemoryAllowance"] = new ChangeRunMemoryAllowance(["alpha/Developer"]),
        ["HoldIdleClock"] = new HoldIdleClock(Member, true),
        ["TouchIdleClock"] = new TouchIdleClock(Member),
        ["CancelRun"] = new CancelRun(new RunId(Member, "n1")),
        ["StopTerminal"] = new StopTerminal("s1"),
        ["StopStream"] = new StopStream("f1"),
    };

    /// <summary>
    /// A MESSAGE ALREADY SENT WHEN THE CONNECTION DROPS, before the worker said it applied it: what
    /// control says to the runs is kept and said again when the worker is back, and its send returns;
    /// a request fails its caller at once and is never said again.
    /// </summary>
    [Theory]
    [MemberData(nameof(InFlight))]
    public async Task A_message_in_flight_when_the_connection_drops_is_kept_or_fails_by_its_kind(string name)
    {
        var kept = KeptByName.ContainsKey(name);
        var message = kept ? KeptByName[name] : RequestsByName[name];
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var remote = new RemoteWorker(
            new WorkerInfo(new WorkerId("w1"), null, 4, 8_000_000_000, DateTimeOffset.UnixEpoch),
            "s1", (_, _) => Task.CompletedTask, WorkerTimings.Default, _ => { }, _ => { }, clock);

        // Connected: the worker reads the message and drops before it says it applied it.
        var (control, worker) = await Pair();
        var first = remote.RunAsync(new WorkerSocket(control.Socket, new WorkerFrameCodec(null)), Ct);
        var socket = new WorkerSocket(worker.Socket, new WorkerFrameCodec(null));
        var sending = remote.SendAsync(message, Ct);
        var read = Assert.IsType<CommandFrame>(await socket.ReceiveAsync(Ct).WaitAsync(Bound, Ct));
        Assert.Equal(message.GetType(), read.Message.GetType());
        worker.Drop();
        await first.WaitAsync(Bound, Ct);
        Assert.True(remote.Dropped);

        if (kept)
        {
            await sending.WaitAsync(Bound, Ct);
        }
        else
        {
            var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => sending.WaitAsync(Bound, Ct));
            Assert.Equal("The connection to worker w1 dropped.", failed.Message);
        }

        // Back: a kept message is said again, once; a request never.
        (control, worker) = await Pair();
        socket = new WorkerSocket(worker.Socket, new WorkerFrameCodec(null));
        var second = remote.RunAsync(new WorkerSocket(control.Socket, new WorkerFrameCodec(null)), Ct);

        var received = await SaidAgainAsync(remote, socket, kept ? message.GetType() : null);

        Assert.Equal(kept ? [message.GetType()] : [], received.Select(m => m.GetType()));

        remote.Shutdown();
        await second.WaitAsync(Bound, Ct);
    }

    /// <summary>
    /// What the worker that came back is told again: everything it receives, answered as applied, up to
    /// <paramref name="last"/> when that is said again, and up to a marker control sends after it - so a
    /// message said again behind <paramref name="last"/> is seen too. The marker is not returned.
    /// </summary>
    private async Task<List<ControlMessage>> SaidAgainAsync(RemoteWorker remote, WorkerSocket socket, Type? last)
    {
        var marker = new TouchIdleClock(new ContainerId("marker", "Developer"));
        List<ControlMessage> received = [];
        var marking = last is null ? remote.SendAsync(marker, Ct) : null;
        while (true)
        {
            var command = Assert.IsType<CommandFrame>(await socket.ReceiveAsync(Ct).WaitAsync(Bound, Ct));
            await socket.SendAsync(new AppliedFrame(command.Id, null), Ct);
            if (command.Message == marker) break;

            received.Add(command.Message);
            if (marking is null && command.Message.GetType() == last) marking = remote.SendAsync(marker, Ct);
        }

        await marking!.WaitAsync(Bound, Ct);
        return received;
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
}
