using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// ONE UPDATE GATE, IN CONTROL. In the control role every run on every worker holds a share of its
/// CLI's install in control's gate from before it is sent until it ends, however it ends - dropped
/// workers included, until they are lost - so a person's update waits for runs on every worker, holds
/// new launches everywhere (they are not sent, and say so), and then runs ONCE, on the worker with the
/// most headroom when it starts. No worker then, or the worker lost while it runs: failed, and the held
/// launches go. A launch check takes a share the same way and reads not checked while an update waits.
/// In one process the Host's own worker takes the share, and a run is counted once.
/// </summary>
public sealed class ControlUpdateGateTests : IDisposable
{
    private const string Cli = "fakecli";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly string _root = Directory.CreateTempSubdirectory("harness-control-gate-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<string> Endings() => ["completed", "failed", "stopped", "worker-lost"];

    [Theory]
    [MemberData(nameof(Endings))]
    public async Task In_control_a_run_holds_a_share_of_its_command_until_it_ends_however_it_ends(string ending)
    {
        var bed = new Bed(_root);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = bed.Runner.RunAsync(Bed.Invocation("alpha"), stop.Token);
        var start = await bed.StartedOn(bed.W1);
        Assert.Equal(1, bed.Gate.Running(Cli));

        switch (ending)
        {
            case "completed":
                await bed.EndAsync(bed.W1, start, 0);
                Assert.Equal(0, (await running.WaitAsync(Bound, Ct)).ExitCode);
                break;
            case "failed":
                await bed.EndAsync(bed.W1, start, 3);
                Assert.Equal(3, (await running.WaitAsync(Bound, Ct)).ExitCode);
                break;
            case "stopped":
                await stop.CancelAsync();
                await Until(() => bed.W1.Sent.OfType<CancelRun>().Any());
                await bed.EndAsync(bed.W1, start, -1, new RunFault(true, typeof(OperationCanceledException).FullName!, "stopped"));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Bound, Ct));
                break;
            case "worker-lost":
                bed.W1.Lose("Worker w1 did not come back within its grace.");
                Assert.Equal(FailureClasses.WorkerLost, (await running.WaitAsync(Bound, Ct)).FailureClass);
                break;
        }

        Assert.Equal(0, bed.Gate.Running(Cli));
    }

    [Fact]
    public async Task A_held_run_is_not_sent_and_says_the_held_sentence()
    {
        var bed = new Bed(_root);
        var release = new TaskCompletionSource<AgentUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updating = bed.Gate.UpdateAsync(Cli, _ => release.Task, Ct);
        await Until(() => bed.Gate.Updating(Cli));

        var held = bed.Runner.RunAsync(Bed.Invocation("alpha"), Ct);
        await Until(() => !bed.Reports.Progress.IsEmpty);

        Assert.Equal(ProcessAgentRunner.HeldText(Cli), Assert.Single(bed.Reports.Progress));
        Assert.Equal([new AgentRunHolder("alpha", "Developer")], bed.Gate.StateOf(Cli).Held);
        Assert.Empty(bed.W1.Sent.OfType<StartRun>());

        release.SetResult(new AgentUpdateResult("fake", Cli, true, 0, "1", "2", DateTimeOffset.UtcNow, "done"));
        await updating;
        var start = await bed.StartedOn(bed.W1);
        await bed.EndAsync(bed.W1, start, 0);
        Assert.Equal(0, (await held.WaitAsync(Bound, Ct)).ExitCode);
    }

    [Fact]
    public async Task The_update_runs_on_one_worker_after_every_share_is_released()
    {
        var bed = new Bed(_root);
        var one = bed.Runner.RunAsync(Bed.Invocation("alpha"), Ct);
        var two = bed.Runner.RunAsync(Bed.Invocation("beta"), Ct);
        var first = await bed.StartedOn(bed.W1);
        var second = await bed.StartedOn(bed.W2);

        var (asked, started) = bed.Updater.Request("fake", "person@example.test")!.Value;
        Assert.True(started);
        Assert.Equal(AgentUpdatePhases.Waiting, asked.Phase);
        Assert.Equal([new AgentRunHolder("alpha", "Developer"), new AgentRunHolder("beta", "Developer")], asked.InFlight.OrderBy(h => h.Team));

        await bed.EndAsync(bed.W1, first, 0);
        await one.WaitAsync(Bound, Ct);
        Assert.Equal(AgentUpdatePhases.Waiting, bed.Gate.StateOf(Cli).Phase);
        Assert.Empty(bed.Updates());

        await bed.EndAsync(bed.W2, second, 0);
        await two.WaitAsync(Bound, Ct);
        await Until(() => bed.Gate.StateOf(Cli).Phase == AgentUpdatePhases.Done);

        var update = Assert.Single(bed.Updates());
        Assert.Equal(["fakecli --version", "fakecli update", "fakecli --version"], update.Commands.Select(c => string.Join(' ', [c.FileName, .. c.Arguments])));
        Assert.Equal((false, false, true), (update.Commands[0].WithUpdatesOn, update.Commands[2].WithUpdatesOn, update.Commands[1].WithUpdatesOn));
        var result = bed.Gate.StateOf(Cli).Result!;
        Assert.Equal(("1.0", "2.0", true), (result.VersionBefore, result.VersionAfter, result.Updated));
        Assert.Equal("update", Assert.Single(await CliVersionHistory.In(_root).ReadAsync(5, Ct)).By);
    }

    [Fact]
    public async Task The_update_runs_on_the_worker_with_most_headroom_when_it_starts_not_when_asked()
    {
        var bed = new Bed(_root) { Best = "w1" };
        var run = bed.Runner.RunAsync(Bed.Invocation("alpha"), Ct);
        var start = await bed.StartedOn(bed.W1);

        bed.Updater.Request("fake", "person@example.test");
        bed.Best = "w2";
        await bed.EndAsync(bed.W1, start, 0);
        await run.WaitAsync(Bound, Ct);
        await Until(() => bed.Gate.StateOf(Cli).Phase == AgentUpdatePhases.Done);

        Assert.Empty(bed.W1.Sent.OfType<RunAgentCommands>());
        Assert.Single(bed.W2.Sent.OfType<RunAgentCommands>());
    }

    [Fact]
    public async Task No_worker_when_it_is_time_fails_the_update_and_lets_launches_go()
    {
        var bed = new Bed(_root);
        var run = bed.Runner.RunAsync(Bed.Invocation("alpha"), Ct);
        var start = await bed.StartedOn(bed.W1);
        bed.Updater.Request("fake", "person@example.test");
        var held = bed.Runner.RunAsync(Bed.Invocation("beta"), Ct);
        await Until(() => bed.Gate.StateOf(Cli).Held.Count == 1);

        bed.Best = null;
        await bed.EndAsync(bed.W1, start, 0);
        await run.WaitAsync(Bound, Ct);
        await Until(() => bed.Gate.StateOf(Cli).Phase == AgentUpdatePhases.Failed);

        Assert.Equal($"The update did not finish: {AgentCliUpdater.NoWorkerText}", bed.Gate.StateOf(Cli).Error);
        Assert.Empty(bed.Updates());

        // The held launch goes.
        var second = await bed.StartedOn(bed.W2);
        await bed.EndAsync(bed.W2, second, 0);
        Assert.Equal(0, (await held.WaitAsync(Bound, Ct)).ExitCode);
    }

    [Fact]
    public async Task A_worker_lost_while_updating_fails_it_and_lets_launches_go()
    {
        var bed = new Bed(_root) { AnswerUpdates = false };
        bed.Updater.Request("fake", "person@example.test");
        await Until(() => bed.W1.Sent.OfType<RunAgentCommands>().Any());
        var held = bed.Runner.RunAsync(Bed.Invocation("beta"), Ct);
        await Until(() => bed.Gate.StateOf(Cli).Held.Count == 1);

        bed.W1.Lose("Worker w1 did not come back within its grace.");
        await Until(() => bed.Gate.StateOf(Cli).Phase == AgentUpdatePhases.Failed);

        Assert.Equal($"The update did not finish: {AgentCliUpdater.LostText(bed.W1.Id)}", bed.Gate.StateOf(Cli).Error);
        var start = await bed.StartedOn(bed.W2);
        await bed.EndAsync(bed.W2, start, 0);
        Assert.Equal(0, (await held.WaitAsync(Bound, Ct)).ExitCode);
    }

    [Fact]
    public async Task A_launch_check_in_control_reads_not_checked_while_an_update_waits()
    {
        var bed = new Bed(_root);
        var run = bed.Runner.RunAsync(Bed.Invocation("alpha"), Ct);
        var start = await bed.StartedOn(bed.W1);
        bed.Updater.Request("fake", "person@example.test");
        await Until(() => bed.Gate.Updating(Cli));

        var check = await bed.Runner.CheckLaunchAsync("fake", Ct);

        Assert.Equal(AgentLaunchReport.Unchecked(RunLauncher.UpdatingText(Cli)), check);
        Assert.Empty(bed.W1.Sent.OfType<CheckLaunch>());
        Assert.Empty(bed.W2.Sent.OfType<CheckLaunch>());

        await bed.EndAsync(bed.W1, start, 0);
        await run.WaitAsync(Bound, Ct);
    }

    [Fact]
    public async Task A_run_on_a_dropped_worker_keeps_its_share_until_it_is_lost_or_ends()
    {
        // A worker over a real connection, on a manual clock: dropped, then lost when its grace passes.
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var timings = new WorkerTimings(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), TimeSpan.FromSeconds(10));
        var bed = new Bed(_root);
        RemoteWorker? remote = null;
        remote = new RemoteWorker(
            new WorkerInfo(new WorkerId("w9"), null, 4, null, clock.GetUtcNow()), "s1",
            (envelope, ct) => bed.Directory.HandleAsync(envelope, ct), timings, _ => { }, _ => { }, clock);
        bed.Place["alpha"] = remote;

        var (control, worker) = await Pair();
        var connected = remote.RunAsync(new WorkerSocket(control.Socket, new WorkerFrameCodec(null)), Ct);
        var socket = new WorkerSocket(worker.Socket, new WorkerFrameCodec(null));

        var run = bed.Runner.RunAsync(Bed.Invocation("alpha"), Ct);
        var command = Assert.IsType<CommandFrame>(await socket.ReceiveAsync(Ct).WaitAsync(Bound, Ct));
        Assert.IsType<StartRun>(command.Message);
        await socket.SendAsync(new AppliedFrame(command.Id, null), Ct);
        Assert.Equal(1, bed.Gate.Running(Cli));

        bed.Updater.Request("fake", "person@example.test");

        // Dropped: inside the grace the update waits, has not run, and the run still holds its share.
        worker.Drop();
        await connected.WaitAsync(Bound, Ct);
        Assert.True(remote.Dropped);
        clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(100, Ct);
        Assert.Equal((AgentUpdatePhases.Waiting, 1), (bed.Gate.StateOf(Cli).Phase, bed.Gate.Running(Cli)));
        Assert.Empty(bed.Updates());
        Assert.False(run.IsCompleted);

        // The grace passes: the run is lost, its share released, and only then the update runs.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(FailureClasses.WorkerLost, (await run.WaitAsync(Bound, Ct)).FailureClass);
        await Until(() => bed.Gate.StateOf(Cli).Phase == AgentUpdatePhases.Done);
        Assert.Single(bed.Updates());

        // The same with a run whose end arrives while the worker is still only dropped: that end, not
        // the drop, lets the next update run.
        var again = bed.Runner.RunAsync(Bed.Invocation("beta"), Ct);
        var start = await bed.StartedOn(bed.W2);
        bed.W2.Dropped = true;
        bed.Updater.Request("fake", "person@example.test");
        clock.Advance(TimeSpan.FromSeconds(20));
        await Task.Delay(100, Ct);
        Assert.Equal(AgentUpdatePhases.Waiting, bed.Gate.StateOf(Cli).Phase);
        Assert.Single(bed.Updates());

        await bed.EndAsync(bed.W2, start, 0);
        await again.WaitAsync(Bound, Ct);
        await Until(() => bed.Updates().Count == 2);
    }

    [Fact]
    public async Task In_all_a_run_is_counted_once()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        var release = Path.Combine(_root, "release");
        var started = Path.Combine(_root, "started");
        var cli = Path.Combine(_root, "stub-cli");
        await TestExecutable.WriteAsync(cli, $"#!/bin/sh\ncat >/dev/null\ntouch '{started}'\nwhile [ ! -f '{release}' ]; do sleep 0.05; done\n");
        var catalog = new AgentCatalog([new AgentDefinition("stub", AgentMode.Headless, new AgentLaunch(cli, [], LanguageModel: false))]);
        var gate = new AgentUpdateGate();

        // The Host's own worker takes the share from the shared gate; the runner, built as in all, takes none.
        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat(), updates: gate);
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var running = runner.RunAsync(
            new AgentInvocation(new ContainerId("alpha", "worker"), "s", "p", work, new Dictionary<string, string>(), Agent: "stub"), Ct);
        await Until(() => File.Exists(started));

        Assert.Equal(1, gate.Running(cli));
        Assert.Equal([new AgentRunHolder("alpha", "worker")], gate.StateOf(cli).InFlight);

        await File.WriteAllTextAsync(release, "", Ct);
        await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(0, gate.Running(cli));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Not true within {Bound.TotalSeconds:0} s.");
            await Task.Delay(10, Ct);
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

    /// <summary>
    /// Control as the control role composes it, over two scripted workers: team alpha's runs are placed
    /// on w1 and beta's on w2; a request goes to <see cref="Best"/> (none when null).
    /// </summary>
    private sealed class Bed
    {
        public Bed(string root)
        {
            W1 = Scripted("w1");
            W2 = Scripted("w2");
            Place["alpha"] = W1;
            Place["beta"] = W2;
            Asks = new WorkerAsks(() => Best switch
            {
                "w1" => W1,
                "w2" => W2,
                _ => throw new InvalidOperationException("No worker is connected."),
            });
            W1.Asks = W2.Asks = Asks;

            var catalog = new AgentCatalog(
            [
                new AgentDefinition(
                    "fake", AgentMode.Headless, new AgentLaunch(Cli, []), LaunchCheck: ["--version"],
                    Updates: new AgentUpdates(Update: [Cli, "update"])),
            ]);
            Runner = new ProcessAgentRunner(
                catalog, W1, Directory, null, () => null, null,
                placedOn: member => Place[member.Team], gateHere: Gate, reports: () => Reports);
            Updater = new AgentCliUpdater(catalog, Gate, dataRoot: root, asks: Asks);
        }

        public ScriptedCliWorker W1 { get; }

        public ScriptedCliWorker W2 { get; }

        public string? Best { get; set; } = "w1";

        public bool AnswerUpdates { get; set; } = true;

        public ConcurrentDictionary<string, IRunWorker> Place { get; } = new();

        public AgentUpdateGate Gate { get; } = new();

        public RunDirectory Directory { get; } = new();

        public WorkerAsks Asks { get; }

        public RecordedReports Reports { get; } = new();

        public ProcessAgentRunner Runner { get; }

        public AgentCliUpdater Updater { get; }

        public static AgentInvocation Invocation(string team) =>
            new(new ContainerId(team, "Developer"), "s", "p", "/work", new Dictionary<string, string>(), Agent: "fake");

        /// <summary>The run its worker was sent, once it was.</summary>
        public async Task<StartRun> StartedOn(ScriptedCliWorker worker)
        {
            await Until(() => worker.Sent.OfType<StartRun>().Any(s => !_ended.ContainsKey(s.Run)));
            return worker.Sent.OfType<StartRun>().First(s => !_ended.ContainsKey(s.Run));
        }

        public Task EndAsync(ScriptedCliWorker worker, StartRun start, int exit, RunFault? fault = null)
        {
            _ended[start.Run] = true;
            return worker.SayAsync(Directory.HandleAsync, new RunEnded(start.Run, exit, 42, null, null, null, null, null, fault));
        }

        /// <summary>Every update any worker was sent.</summary>
        public IReadOnlyList<RunAgentCommands> Updates() =>
            [.. W1.Sent.Concat(W2.Sent).OfType<RunAgentCommands>()];

        private readonly ConcurrentDictionary<RunId, bool> _ended = new();

        private ScriptedCliWorker Scripted(string id) =>
            new(id, message => message is RunAgentCommands run && AnswerUpdates
                ? new AgentCommandsRan(run.Request,
                [
                    new AgentCliRunResult(true, 0, false, "1.0\n", "", false, null),
                    new AgentCliRunResult(true, 0, false, "updated", "", false, null),
                    new AgentCliRunResult(true, 0, false, "2.0\n", "", false, null),
                ])
                : null);
    }

    internal sealed class RecordedReports : IMemberReports
    {
        public ConcurrentQueue<string> Progress { get; } = new();

        public Task<MemberReportOutcome> ProgressAsync(ContainerId member, string status, CancellationToken ct = default)
        {
            Progress.Enqueue(status);
            return Task.FromResult(MemberReportOutcome.Ok);
        }

        private static Task<MemberReportOutcome> Unexpected() => throw new InvalidOperationException("only progress");

        public Task<MemberReportOutcome> BlockedAsync(ContainerId member, string reason, int? item = null, CancellationToken ct = default) => Unexpected();
        public Task<MemberReportOutcome> DeferAsync(ContainerId member, int item, string reason, CancellationToken ct = default) => Unexpected();
        public Task<MemberReportOutcome> NeedsDecisionAsync(ContainerId member, string question, CancellationToken ct = default) => Unexpected();
        public Task<MemberReportOutcome> HandbackAsync(ContainerId member, string delivered, CancellationToken ct = default) => Unexpected();
        public Task<MemberReportOutcome> PublishAsync(ContainerId member, string type, string payload, CancellationToken ct = default) => Unexpected();
    }
}
