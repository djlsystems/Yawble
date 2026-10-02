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

        List<ControlMessage> received = [];
        while (true)
        {
            var command = Assert.IsType<CommandFrame>(await socket.ReceiveAsync(Ct).WaitAsync(Bound, Ct));
            received.Add(command.Message);
            await socket.SendAsync(new AppliedFrame(command.Id, null), Ct);
            if (command.Message is HoldIdleClock) break;
        }

        Assert.DoesNotContain(received, m => m.GetType() == request.GetType());
        Assert.Equal([typeof(HoldIdleClock)], received.Select(m => m.GetType()));

        remote.Shutdown();
        await second.WaitAsync(Bound, Ct);
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
