using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// TESTER PROBES FOR MCP 2, ROUND 4: a binary result written as the member through a real remote worker connection
/// (control's <see cref="RemoteWorker"/> and <see cref="WorkerAsks"/> at one end of a loopback WebSocket, a worker
/// reading frames and answering with <see cref="WorkerAgentCli"/> at the other), the 32 MiB write frame's limits,
/// and a worker built before it.
/// </summary>
public sealed class McpServerToolsProbe4Tests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => McpServerToolsBed.Ct;

    /// <summary>The largest result the item allows (20 MB) is written by the worker as the member, and nothing is left pending.</summary>
    [Fact]
    public async Task A_20_MB_result_is_written_through_a_remote_worker()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The member's write is Linux only.");
        using var standIn = new StandIn();
        var root = Directory.CreateTempSubdirectory("probe4-wc-").FullName;
        try
        {
            await using var bed = await Connected.StartAsync();
            var worker = bed.ServeAsync(async command => await standIn.Cli.AnswerAsync((AgentCliCommand)command.Message, Ct));

            var bytes = new byte[(int)McpRunTools.DefaultMaximumBinary];
            Random.Shared.NextBytes(bytes);
            var path = Path.Combine(root, ".harness", "mcp", "fake", "image-1.png");
            var asked = await bed.Asks.AskAsync<WroteAsAgent>(new WriteAsAgent(WorkerAsks.NewRequest(), root, path, bytes), Bound, Ct);

            Assert.Null(asked.Why);
            Assert.Equal(WroteAsAgent.Written, asked.Answer!.Result);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Ct));
            Assert.Equal(0, bed.Pending());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A frame past 32 MiB through the real <see cref="RemoteWorker"/>: a sentence, nothing pending, nothing sent, and
    /// the next request on the same connection is still answered.
    /// </summary>
    [Fact]
    public async Task A_write_past_the_frame_is_a_sentence_and_the_connection_carries_on()
    {
        await using var bed = await Connected.StartAsync();
        var seen = new List<string>();
        var worker = bed.ServeAsync(command =>
        {
            var write = (WriteAsAgent)command.Message;
            lock (seen) seen.Add(write.Request);
            return Task.FromResult<WorkerEvent?>(new WroteAsAgent(write.Request, WroteAsAgent.Written, null));
        });

        var huge = await bed.Asks.AskAsync<WroteAsAgent>(new WriteAsAgent("big", "/w", "/w/x.png", new byte[25 * 1024 * 1024]), Bound, Ct);
        Assert.Null(huge.Answer);
        Assert.Equal($"Worker w1 could not be sent it: {WorkerFrameCodec.WriteTooLargeText}", huge.Why);
        Assert.Equal(0, bed.Pending());

        var next = await bed.Asks.AskAsync<WroteAsAgent>(new WriteAsAgent("small", "/w", "/w/x.png", [1]), Bound, Ct);
        Assert.Equal(WroteAsAgent.Written, next.Answer!.Result);
        lock (seen) Assert.Equal(["small"], seen);
        Assert.Equal(0, bed.Pending());
    }

    /// <summary>
    /// A WORKER BUILT BEFORE THIS reads one message of at most 16 MiB and ends the connection past it (ControlConnection
    /// catches the InvalidDataException and lets the socket go). Control's ask answers with a sentence, quickly, not
    /// after the 2 minute bound. Records what that sentence says and that the connection was dropped.
    /// </summary>
    [Fact]
    public async Task An_older_worker_reading_16_MiB_gives_a_sentence_not_a_hang()
    {
        await using var bed = await Connected.StartAsync();
        var reading = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            long length = 0;
            while (true)
            {
                var result = await bed.WorkerEnd.Socket.ReceiveAsync(buffer, Ct);
                length += result.Count;
                if (length > WorkerFrameCodec.MaxBytes)
                {
                    bed.WorkerEnd.Drop();
                    return length;
                }

                if (result.EndOfMessage) length = 0;
            }
        }, Ct);

        var clock = Stopwatch.StartNew();
        var asked = await bed.Asks.AskAsync<WroteAsAgent>(
            new WriteAsAgent("old", "/w", "/w/x.png", new byte[15 * 1024 * 1024]), TimeSpan.FromMinutes(2), Ct);
        clock.Stop();
        TestContext.Current.SendDiagnosticMessage($"old worker: why='{asked.Why}' after {clock.Elapsed}");


        Assert.Null(asked.Answer);
        Assert.False(string.IsNullOrEmpty(asked.Why));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
        Assert.True(bed.Remote.Dropped);
    }

    /// <summary>
    /// A WORKER CANNOT USE THE 32 MiB ROOM. Control reads at most 16 MiB of anything but a write command - an event
    /// naming a write in its text included - and drops the connection past it, as before.
    /// </summary>
    [Fact]
    public async Task A_worker_sending_a_large_event_naming_a_write_is_dropped()
    {
        await using var bed = await Connected.StartAsync();
        var text = "\"writeAsAgent\"" + new string('x', 20 * 1024 * 1024);
        var forged = JsonSerializer.Serialize<WorkerFrame>(
            new EventFrame(new WorkerEnvelope(new WorkerId("w1"), 1, new RunOutput(new RunId(new("alpha", "Dev"), "n1"), text))), WorkerFrameCodec.Json);
        await bed.WorkerEnd.Socket.SendAsync(Encoding.UTF8.GetBytes(forged), WebSocketMessageType.Text, true, Ct);

        await bed.Running.WaitAsync(Bound, Ct);
        Assert.True(bed.Remote.Dropped);
        var closing = await bed.WorkerEnd.Socket.ReceiveAsync(new byte[1024], Ct);
        Assert.Equal(WebSocketMessageType.Close, closing.MessageType);
        Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, bed.WorkerEnd.Socket.CloseStatus);
        Assert.Equal(WorkerFrameCodec.TooLargeText, bed.WorkerEnd.Socket.CloseStatusDescription);
    }

    /// <summary>A worker sending control a write command of 20 MiB: read (the codec allows it) and ignored; the connection stays up.</summary>
    [Fact]
    public async Task A_worker_sending_control_a_write_command_is_ignored()
    {
        await using var bed = await Connected.StartAsync();
        var socket = new WorkerSocket(bed.WorkerEnd.Socket, new WorkerFrameCodec(null));
        await socket.SendAsync(new CommandFrame(1, new WriteAsAgent("x", "/w", "/w/x", new byte[20 * 1024 * 1024])), Ct);
        await socket.SendAsync(new PongFrame(1), Ct);
        await Task.Delay(500, Ct);
        Assert.False(bed.Remote.Dropped);
        Assert.False(bed.Running.IsCompleted);
    }

    private sealed class Connected : IAsyncDisposable
    {
        public required RemoteWorker Remote { get; init; }

        public required WorkerAsks Asks { get; init; }

        public required Link WorkerEnd { get; init; }

        public required Link ControlEnd { get; init; }

        public required Task Running { get; init; }

        public static async Task<Connected> StartAsync()
        {
            WorkerAsks? asks = null;
            var remote = new RemoteWorker(
                new WorkerInfo(new WorkerId("w1"), null, 4, 8_000_000_000, DateTimeOffset.UnixEpoch),
                "s1", (envelope, ct) => asks!.HandleAsync(envelope, ct), WorkerTimings.Default, _ => { }, _ => { },
                new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1)));
            asks = new WorkerAsks(() => remote);
            var (control, worker) = await Pair();
            var running = remote.RunAsync(new WorkerSocket(control.Socket, new WorkerFrameCodec(null)), Ct);
            return new Connected { Remote = remote, Asks = asks, WorkerEnd = worker, ControlEnd = control, Running = running };
        }

        /// <summary>The worker's end: each command applied, then answered with what <paramref name="answer"/> gives.</summary>
        public Task ServeAsync(Func<CommandFrame, Task<WorkerEvent?>> answer) => Task.Run(async () =>
        {
            var socket = new WorkerSocket(WorkerEnd.Socket, new WorkerFrameCodec(null));
            long seq = 0;
            while (await socket.ReceiveAsync(Ct) is { } frame)
            {
                if (frame is not CommandFrame command) continue;
                await socket.SendAsync(new AppliedFrame(command.Id, null), Ct);
                if (await answer(command) is { } said) await socket.SendAsync(new EventFrame(new WorkerEnvelope(new WorkerId("w1"), ++seq, said)), Ct);
            }
        }, Ct);

        public int Pending()
        {
            var field = typeof(RemoteWorker).GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var gate = typeof(RemoteWorker).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Remote)!;
            lock (gate) return ((ICollection)field.GetValue(Remote)!).Count;
        }

        public async ValueTask DisposeAsync()
        {
            Remote.Shutdown();
            WorkerEnd.Drop();
            ControlEnd.Drop();
            try
            {
                await Running.WaitAsync(Bound);
            }
            catch (Exception)
            {
                // Ending anyway.
            }
        }
    }

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

    private sealed class StandIn : IDisposable
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("setpriv-standin4-").FullName;

        public StandIn()
        {
            var script = Path.Combine(_folder, "setpriv");
            File.WriteAllText(script, "#!/bin/sh\nwhile [ \"$1\" != \"--\" ]; do shift; done\nshift\nexec \"$@\"\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Cli = new WorkerAgentCli(new WorkerId("w1"), new AgentLaunchUser(true, "agent", (int)getuid(), (int)getgid(), "stand-in", script));
        }

        public WorkerAgentCli Cli { get; }

        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }

    [DllImport("libc")]
    private static extern uint getuid();

    [DllImport("libc")]
    private static extern uint getgid();
}
