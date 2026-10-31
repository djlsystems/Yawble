using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// TESTER PROBES FOR MCP 2, ROUND 3: the as-member write's command line with awkward names, and a binary result
/// sent to a worker over its connection.
/// </summary>
public sealed class McpServerToolsProbe3Tests
{
    private static CancellationToken Ct => McpServerToolsBed.Ct;

    [Theory]
    [InlineData("-rf/x.png")]
    [InlineData("--help.png")]
    [InlineData("a b/of=c.png")]
    [InlineData("if=x/conv=notrunc.png")]
    [InlineData("new\nline.png")]
    public async Task An_awkward_name_is_written_where_it_says_and_nowhere_else(string relative)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var member = new StandIn();
        var root = Directory.CreateTempSubdirectory("probe3-").FullName;
        try
        {
            var path = Path.Combine(root, relative);
            Assert.Equal((WroteAsAgent.Written, null), await member.WriteAsync(root, path, [7, 8, 9]));
            Assert.Equal([7, 8, 9], await File.ReadAllBytesAsync(path, Ct));
            var all = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Assert.Equal([path], all);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_dangling_link_at_the_name_is_never_written_through_as_the_member()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var member = new StandIn();
        var root = Directory.CreateTempSubdirectory("probe3-").FullName;
        var outside = Directory.CreateTempSubdirectory("probe3-out-").FullName;
        try
        {
            var target = Path.Combine(outside, "made.png");
            File.CreateSymbolicLink(Path.Combine(root, "d.png"), target);
            Assert.Equal(WroteAsAgent.Link, (await member.WriteAsync(root, Path.Combine(root, "d.png"), [1])).Result);
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>
    /// A remote worker reads one frame of at most 16 MiB; the binary cap is 20 MB and the bytes cross as base64 JSON.
    /// A 12 MB result is sent over a real WebSocket as control sends it, and the worker's end reads it.
    /// </summary>
    [Fact]
    public async Task A_12_MB_binary_result_crosses_to_a_remote_worker()
    {
        var bytes = new byte[12 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        var key = WorkerFrameCodec.Key("k", "a", "b");
        var frame = new CommandFrame(1, new WriteAsAgent("w1", "/data/teams/t/w", "/data/teams/t/w/x.png", bytes));
        var length = System.Text.Encoding.UTF8.GetByteCount(new WorkerFrameCodec(key).Write(frame));
        TestContext.Current.SendDiagnosticMessage($"frame {length} bytes, limit {WorkerFrameCodec.MaxBytes}");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Ct);
        using var server = await listener.AcceptTcpClientAsync(Ct);
        listener.Stop();
        using var control = new WorkerSocket(WebSocket.CreateFromStream(server.GetStream(), true, null, TimeSpan.Zero), new WorkerFrameCodec(key));
        using var worker = new WorkerSocket(WebSocket.CreateFromStream(client.GetStream(), false, null, TimeSpan.Zero), new WorkerFrameCodec(key));

        var sending = control.SendAsync(frame, Ct);
        var received = await worker.ReceiveAsync(Ct);
        await sending;
        var write = Assert.IsType<WriteAsAgent>(Assert.IsType<CommandFrame>(received).Message);
        Assert.Equal(bytes.Length, write.Data.Length);
    }

    /// <summary>
    /// What control's ask does with a binary result too large for a frame, with a worker that writes frames as
    /// <see cref="WorkerSocket.SendAsync"/> does: an answer or a sentence, never an exception out of the call.
    /// </summary>
    [Fact]
    public async Task A_result_too_large_for_a_frame_is_answered_with_a_sentence()
    {
        var asks = new WorkerAsks(() => new FramingWorker());
        var write = new WriteAsAgent(WorkerAsks.NewRequest(), "/w", "/w/x.png", new byte[13 * 1024 * 1024]);
        var asked = await asks.AskAsync<WroteAsAgent>(write, TimeSpan.FromSeconds(5), Ct);
        Assert.Null(asked.Answer);
        Assert.False(string.IsNullOrEmpty(asked.Why));
    }

    private sealed class FramingWorker : IRunWorker
    {
        public WorkerId Id => WorkerId.Local;

        public Task Closed { get; } = new TaskCompletionSource().Task;

        public Task SendAsync(ControlMessage message, CancellationToken ct = default)
        {
            _ = new WorkerFrameCodec(WorkerFrameCodec.Key("k", "a", "b")).Write(new CommandFrame(1, message));
            return Task.CompletedTask;
        }
    }

    private sealed class StandIn : IDisposable
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("setpriv-standin3-").FullName;

        public StandIn()
        {
            var script = Path.Combine(_folder, "setpriv");
            File.WriteAllText(script, "#!/bin/sh\nwhile [ \"$1\" != \"--\" ]; do shift; done\nshift\nexec \"$@\"\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            User = new AgentLaunchUser(true, "agent", (int)getuid(), (int)getgid(), "stand-in", script);
        }

        public AgentLaunchUser User { get; }

        public async Task<(string Result, string? Detail)> WriteAsync(string root, string path, byte[] bytes)
        {
            var answer = Assert.IsType<WroteAsAgent>(
                await new WorkerAgentCli(WorkerId.Local, User).AnswerAsync(new WriteAsAgent("w1", root, path, bytes), Ct));
            return (answer.Result, answer.Detail);
        }

        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }

    [DllImport("libc")]
    private static extern uint getuid();

    [DllImport("libc")]
    private static extern uint getgid();
}
