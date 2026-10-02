using System.Text;
using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;
using Harness.Pty;

namespace Harness.Tests;

/// <summary>
/// A PERSON'S TERMINAL ON A WORKER, RELAYED BY CONTROL, over a real connection: the worker spawns what
/// the factory composes; keystrokes reach it in order and never wait behind a command; output made while
/// the connection was down arrives when it is back, cut with a line past a mebibyte; what was typed
/// while it was down never arrives; and a stopped terminal leaves no MCP config or prompt file behind.
/// </summary>
public sealed partial class ConciergeRelayTests
{
    private const string SessionKey = "harness-key-Rl7wq";

    private static TerminalLaunch Launch(TerminalMcp? mcp = null) =>
        new(
            ["claude", "--mcp-config", "{mcpConfig}", "--strict"],
            Path.GetTempPath(),
            new Dictionary<string, string>
            {
                ["HARNESS_KEY"] = SessionKey,
                ["HARNESS_URL"] = "http://127.0.0.1:5999",
                ["FORCE_COLOR"] = string.Empty,
            },
            ["HARNESS_TEAM", "HARNESS_SHARED", "HARNESS_MEMBER", "HARNESS_CAUSATION"],
            "You are the Concierge.",
            ["--system-prompt-file", "{systemPromptFile}"],
            mcp ?? new TerminalMcp("http://127.0.0.1:5999", "concierge-u1"));

    [Fact]
    public async Task The_terminal_a_worker_spawns_is_the_one_the_factory_composes()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();
        var launch = Launch();

        await using var session = await bed.Engine.SpawnAsync(bed.Engine.Stage(launch) with { Cols = 100, Rows = 30 }, Ct);
        var spawned = bed.Pty.Spawned.Single().Spec;

        // Composed in one process from the same launch, as ConciergeLaunchFactory.ForAsync does.
        var (composed, mcp) = ConciergeTerminal.Materialize(launch, AgentLaunchUser.Same("tester", "the test runs everything as itself"), 100, 30);
        try
        {
            Assert.Equal(composed.CommandLine, spawned.CommandLine);
            Assert.Equal(composed.StartingFolder, spawned.StartingFolder);
            Assert.Equal((composed.Cols, composed.Rows), (spawned.Cols, spawned.Rows));
            Assert.Equal(composed.ClearEnvironment, spawned.ClearEnvironment);
            Assert.Equal(composed.Argv!.Select(Normal), spawned.Argv!.Select(Normal));
            Assert.Equal(
                composed.Env!.OrderBy(e => e.Key).Select(e => (e.Key, Normal(e.Value))),
                spawned.Env!.OrderBy(e => e.Key).Select(e => (e.Key, Normal(e.Value))));
            Assert.Equal(SessionKey, spawned.Env!["HARNESS_KEY"]);
            Assert.Contains("HARNESS_MCP_CONFIG", spawned.Env.Keys);

            // The prompt file the worker wrote holds the prompt control resolved.
            Assert.Equal("You are the Concierge.", await File.ReadAllTextAsync(Assert.Single(spawned.TempFiles!), Ct));
            Assert.Single(composed.TempFiles!);
        }
        finally
        {
            foreach (var file in composed.TempFiles ?? []) File.Delete(file);
            mcp?.Delete();
        }
    }

    [Fact]
    public async Task A_terminals_measurement_reaches_control_named_by_its_session()
    {
        // A fixture /proc and a fixture pid: nothing here reads a live process.
        var proc = Directory.CreateTempSubdirectory("harness-relay-proc-").FullName;
        try
        {
            FixtureProc.Write(proc, pid: 4321, group: 4321, residentPages: 2500);
            FixtureProc.Write(proc, pid: 4322, group: 4321, residentPages: 500);

            await using var bed = new WorkerStreamBed(processes: new ProcessGroupReader(proc, 4096));
            bed.Pty.ProcessId = 4321;
            var sampler = new CapacitySampler(
                () => [(bed.Remote, new HeadroomGate(() => 80, () => 0))], bed.Wip, new NoHeavyLease(), () => 80, () => 0);
            bed.Events = sampler.HandleAsync;
            await bed.StartAsync();

            await using var session = (WorkerPtySession)await bed.Engine.SpawnAsync(bed.Engine.Stage(Launch()), Ct);
            await WorkerStreamBed.Until(() => bed.Pty.Spawned.Count == 1);

            // The worker records the terminal just after spawning it: sampled until it is there.
            for (var i = 0; i < 100 && sampler.Terminal(session.Id) is null; i++) await sampler.SampleAsync(Ct);

            var measured = sampler.Terminal(session.Id);
            Assert.NotNull(measured);
            Assert.Equal(4321, measured.Group);
            Assert.Equal(2, measured.Processes);
            Assert.Equal(3000 * 4096L, measured.ResidentBytes);
            Assert.Equal(session.Id, Assert.Single(sampler.TerminalsOn(new WorkerId("w1"))).Session);
        }
        finally
        {
            Directory.Delete(proc, recursive: true);
        }
    }

    [Fact]
    public async Task Keystrokes_reach_the_terminal_in_order_and_out_of_the_command_queue()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();
        await using var session = await bed.Engine.SpawnAsync(bed.Engine.Stage(Launch()), Ct);
        var pty = bed.Pty.Spawned.Single();

        // A command the worker never finishes applying holds everything queued behind it.
        var never = new TaskCompletionSource();
        bed.Apply = (message, ct) => message is SampleCapacity ? never.Task : bed.Host.ApplyAsync(message, ct);
        var sample = bed.Remote.SendAsync(new SampleCapacity(), Ct);

        foreach (var key in new[] { "h", "e", "l", "l", "o", "\r" }) session.Write(Encoding.UTF8.GetBytes(key));

        await WorkerStreamBed.Until(() => pty.Typed == "hello\r");
        Assert.False(sample.IsCompleted);
        never.SetResult();
    }

    [Fact]
    public async Task Keystrokes_typed_while_the_worker_is_dropped_never_reach_the_terminal()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();
        await using var session = await bed.Engine.SpawnAsync(bed.Engine.Stage(Launch()), Ct);
        var pty = bed.Pty.Spawned.Single();

        await bed.DropAsync();
        session.Write("before"u8);
        await Task.Delay(100, Ct);

        await bed.ReconnectAsync();
        session.Write("after"u8);

        await WorkerStreamBed.Until(() => pty.Typed.Contains("after", StringComparison.Ordinal));
        Assert.Equal("after", pty.Typed);
    }

    [Fact]
    public async Task Output_while_the_worker_was_dropped_is_sent_after_it_returns()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();
        await using var session = await bed.Engine.SpawnAsync(bed.Engine.Stage(Launch()), Ct);
        var output = Collect(session);
        var pty = bed.Pty.Spawned.Single();

        pty.Print("one ");
        await WorkerStreamBed.Until(() => output.Text == "one ");

        await bed.DropAsync();
        pty.Print("two ");
        pty.Print("three");
        await Task.Delay(300, Ct);
        Assert.Equal("one ", output.Text);

        await bed.ReconnectAsync();
        await WorkerStreamBed.Until(() => output.Text == "one two three");
    }

    [Fact]
    public async Task More_than_a_mebibyte_while_dropped_is_cut_with_a_gap_line()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();
        await using var session = await bed.Engine.SpawnAsync(bed.Engine.Stage(Launch()), Ct);
        var output = Collect(session);
        var pty = bed.Pty.Spawned.Single();

        await bed.DropAsync();
        var piece = new byte[64 * 1024];
        Array.Fill(piece, (byte)'x');
        for (var i = 0; i < 24; i++) pty.Print(piece);
        pty.Print("END");

        await bed.ReconnectAsync();
        await WorkerStreamBed.Until(() => output.Text.EndsWith("END", StringComparison.Ordinal));

        Assert.Contains("\r\n" + WorkerTerminals.GapText + "\r\n", output.Text, StringComparison.Ordinal);
        var kept = output.Text.Count(c => c == 'x');
        Assert.InRange(kept, 1, WorkerTerminals.HeldBytes + piece.Length);
        Assert.True(kept < 24 * piece.Length);
    }

    [Fact]
    public async Task The_mcp_folder_and_the_prompt_file_are_deleted_when_the_session_ends()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();
        var session = await bed.Engine.SpawnAsync(bed.Engine.Stage(Launch()), Ct);
        var exited = new TaskCompletionSource<int>();
        session.Exited += code => exited.TrySetResult(code);
        var pty = bed.Pty.Spawned.Single();
        var mcpFolder = Path.GetDirectoryName(pty.Spec.Env!["HARNESS_MCP_CONFIG"])!;
        var promptFile = Assert.Single(pty.Spec.TempFiles!);
        Assert.True(Directory.Exists(mcpFolder));
        Assert.True(File.Exists(promptFile));

        await session.DisposeAsync();

        Assert.Equal(-1, await exited.Task.WaitAsync(WorkerStreamBed.Bound, Ct));
        Assert.True(pty.Disposed);
        await WorkerStreamBed.Until(() => !Directory.Exists(mcpFolder) && !File.Exists(promptFile));
        Assert.Empty(bed.Host.Terminals);
    }

    [Fact]
    public async Task A_cli_that_exits_ends_the_session_after_its_last_output_with_its_code()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();
        var session = await bed.Engine.SpawnAsync(bed.Engine.Stage(Launch()), Ct);
        var output = Collect(session);
        var exited = new TaskCompletionSource<(int Code, string Seen)>();
        session.Exited += code => exited.TrySetResult((code, output.Text));
        var pty = bed.Pty.Spawned.Single();

        pty.Print("bye");
        pty.Exit(3);

        Assert.Equal((3, "bye"), await exited.Task.WaitAsync(WorkerStreamBed.Bound, Ct));
        await WorkerStreamBed.Until(() => bed.Host.Terminals.Count == 0);
    }

    [Fact]
    public async Task A_resize_reaches_the_terminal_clamped()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();
        await using var session = await bed.Engine.SpawnAsync(bed.Engine.Stage(Launch()) with { Cols = 100, Rows = 30 }, Ct);
        var pty = bed.Pty.Spawned.Single();
        Assert.Equal((100, 30), pty.Size);

        session.Resize(90, 20);
        await WorkerStreamBed.Until(() => pty.Size == (90, 20));

        session.Resize(5000, 20);
        await WorkerStreamBed.Until(() => pty.Size == (PtyDimensionLimits.Max, 20));
    }

    [Fact]
    public async Task A_lost_worker_ends_the_session_with_a_sentence()
    {
        var streams = new WorkerStreams();
        var worker = new FakeWorker("w9");
        var pool = new WorkerPool([(worker.Id, new Harness.Host.Capacity.HeadroomGate(() => 80, () => 0, TimeProvider.System))]);
        pool.Connect(worker);
        var engine = new WorkerPtyEngine(pool, streams);

        var session = await engine.SpawnAsync(engine.Stage(Launch()), Ct);
        var output = Collect(session);
        var exited = new TaskCompletionSource<int>();
        session.Exited += code => exited.TrySetResult(code);

        worker.Lose("Worker w9 did not come back within 30 s.");

        Assert.Equal(-1, await exited.Task.WaitAsync(WorkerStreamBed.Bound, Ct));
        Assert.Equal("\r\n" + WorkerPtyEngine.LostText(worker.Id) + "\r\n", output.Text);
        Assert.Equal(
            "The worker running this Concierge (w9) stopped, so this session ended. Open the Concierge again to start a new one.",
            WorkerPtyEngine.LostText(worker.Id));
    }

    [Fact]
    public async Task With_no_worker_a_terminal_is_refused_and_the_pool_says_when_one_connects()
    {
        var pool = new WorkerPool(_ => new Harness.Host.Capacity.HeadroomGate(() => 80, () => 0, TimeProvider.System));
        var engine = new WorkerPtyEngine(pool, new WorkerStreams());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SpawnAsync(engine.Stage(Launch()), Ct));
        Assert.Equal("No worker is connected.", refused.Message);

        var connected = pool.ConnectedAsync(Ct);
        Assert.False(connected.IsCompleted);

        var worker = new FakeWorker("w1");
        pool.Join(worker, new WorkerInfo(worker.Id, "v", null, null, DateTimeOffset.UnixEpoch));
        await connected.WaitAsync(WorkerStreamBed.Bound, Ct);
        Assert.True(pool.ConnectedAsync(Ct).IsCompleted);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A path the worker made for one launch, with its random part taken out.</summary>
    private static string Normal(string text) => RandomPart().Replace(text, "#");

    [GeneratedRegex("[0-9a-f]{32}")]
    private static partial Regex RandomPart();

    private static Seen Collect(IPtySession session)
    {
        var seen = new Seen();
        session.Output += seen.Add;
        return seen;
    }

    private sealed class Seen
    {
        private readonly Lock _gate = new();
        private readonly List<byte> _bytes = [];

        public string Text
        {
            get
            {
                lock (_gate) return Encoding.UTF8.GetString([.. _bytes]);
            }
        }

        public void Add(byte[] chunk)
        {
            lock (_gate) _bytes.AddRange(chunk);
        }
    }
}
