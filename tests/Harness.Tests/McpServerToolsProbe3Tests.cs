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

/// <summary>
/// THE D1 LINES WITH A REAL SWITCH. Skipped unless this process can really start a child as the agent user
/// (<see cref="AgentLaunchUser.Resolve"/> switches): run it as the Host's user holding CAP_SETUID and CAP_SETGID, the way
/// the container entrypoint starts the Host, with HARNESS_TEST_HOST_ONLY_FILE and HARNESS_TEST_HOST_ONLY_DIR naming
/// a file (600) and a folder (700) of that user's own.
/// </summary>
public sealed class McpServerToolsRealSwitchTests
{
    private static CancellationToken Ct => McpServerToolsBed.Ct;

    [Fact]
    public async Task With_users_switched_links_to_Host_only_places_change_nothing_and_a_200_write_swap_race_never_does()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        var user = AgentLaunchUser.Resolve();
        Assert.SkipUnless(user.Switches, "This process cannot switch users: " + user.Reason);
        var file = Environment.GetEnvironmentVariable("HARNESS_TEST_HOST_ONLY_FILE");
        var dir = Environment.GetEnvironmentVariable("HARNESS_TEST_HOST_ONLY_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(file) || string.IsNullOrEmpty(dir), "HARNESS_TEST_HOST_ONLY_FILE and HARNESS_TEST_HOST_ONLY_DIR are not set");
        string Print(string p) => Stat("%i %s %Y %Z %U %a", p);
        var before = (Print(file!), Print(dir!));

        // A working copy the agent may write, as a member's is.
        var root = Path.Combine("/tmp", $"probe3-real-{Guid.NewGuid():N}");
        var open = (UnixFileMode)0b111_111_111;
        Directory.CreateDirectory(root, open);
        File.SetUnixFileMode(root, open);
        var cli = new WorkerAgentCli(WorkerId.Local, user);
        async Task<WroteAsAgent> Write(string path) => Assert.IsType<WroteAsAgent>(
            await cli.AnswerAsync(new WriteAsAgent(WorkerAsks.NewRequest(), root, path, [1, 2, 3]), Ct));
        try
        {
            // A link at the target file.
            Directory.CreateDirectory(Path.Combine(root, "art"), open);
            File.SetUnixFileMode(Path.Combine(root, "art"), open);
            File.CreateSymbolicLink(Path.Combine(root, "art", "pic.png"), file!);
            Assert.Equal(WroteAsAgent.Link, (await Write(Path.Combine(root, "art", "pic.png"))).Result);

            // A link as the default folder, to the Host-only file and to the Host-only folder.
            var folder = Path.Combine(root, McpRunTools.Folder, "fake");
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!, open);
            File.SetUnixFileMode(Path.Combine(root, McpRunTools.Folder), open);
            File.SetUnixFileMode(Path.GetDirectoryName(folder)!, open);
            File.CreateSymbolicLink(folder, file!);
            Assert.NotEqual(WroteAsAgent.Written, (await Write(Path.Combine(folder, "a-1.png"))).Result);
            File.Delete(folder);
            File.CreateSymbolicLink(folder, dir!);
            Assert.Equal(WroteAsAgent.Denied, (await Write(Path.Combine(folder, "a-1.png"))).Result);

            // The swap race, 200 writes.
            var real = Path.Combine(root, "d");
            var link = Path.Combine(root, "lnk");
            Directory.CreateDirectory(real, open);
            File.SetUnixFileMode(real, open);
            Directory.CreateSymbolicLink(link, dir!);
            using var stop = new CancellationTokenSource();
            long swaps = 0;
            var flipper = Task.Factory.StartNew(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (renameat2(-100, real, -100, link, 2) == 0) Interlocked.Increment(ref swaps);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var outcomes = new Dictionary<string, int>();
            for (var i = 0; i < 200; i++)
            {
                var r = (await Write(Path.Combine(real, $"pic-{i}.png"))).Result;
                outcomes[r] = outcomes.GetValueOrDefault(r) + 1;
            }

            await stop.CancelAsync();
            await flipper;
            var said = $"{swaps} swaps; " + string.Join(", ", outcomes.Select(o => $"{o.Key} {o.Value}"));
            TestContext.Current.SendDiagnosticMessage(said);
            Assert.True(before == (Print(file!), Print(dir!)), "a Host-only place changed: " + said);

            // Every file written is the agent's.
            var written = Directory.GetFiles(root, "*.png", SearchOption.AllDirectories).Where(f => new FileInfo(f).LinkTarget is null).ToList();
            Assert.NotEmpty(written);
            Assert.All(written, f => Assert.Equal(user.Uid.ToString(), Stat("%u", f)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Stat(string format, string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("stat") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-c", format, "--", path }) start.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(start)!;
        var said = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return said;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int renameat2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags);
}
