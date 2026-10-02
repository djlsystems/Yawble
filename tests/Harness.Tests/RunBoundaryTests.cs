using System.Collections.Concurrent;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;
using Harness.Pty;

namespace Harness.Tests;

/// <summary>
/// A member run crosses the run protocol: control starts it, the worker says what it does, and
/// control turns that back into the result its callers have always had. Each record is caused on
/// purpose and checked in the order cause and effect require.
/// </summary>
public sealed class RunBoundaryTests : IDisposable
{
    private static readonly ContainerId Member = new("alpha", "worker");

    private readonly string _root = Directory.CreateTempSubdirectory("harness-boundary-").FullName;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        MemberTempCleanup.Remove(_root);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_run_starts_reports_and_ends_across_the_transport()
    {
        using var bed = new Bed(_root);
        var ready = Path.Combine(_root, "ready");
        var go = Path.Combine(_root, "go");

        // ONE JSON DOCUMENT ON STDOUT, as the usage format reads it, then a wait the test ends.
        var script = await Script(
            "printf '%s' '{\"result\":\"the answer\",\"usage\":{\"input_tokens\":7,\"output_tokens\":11}}'\n"
            + $"touch '{ready}'\n"
            + $"i=0; while [ ! -e '{go}' ] && [ $i -lt 400 ]; do sleep 0.05; i=$((i+1)); done\n");

        // 1. Progress: the CLI's update holds the run, which says so before it starts.
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = bed.Updates.UpdateAsync("sh", _ => release.Task, Ct);

        var start = bed.Start(Launch("sh", [script], usageFormat: "claude-json"));
        var running = bed.Directory.RunAsync(bed.Worker, start, Ct);

        await bed.Recorded<RunProgress>().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        release.SetResult(true);
        await update;

        // 2 and 3. The child is up: measure it by hand while it is alive.
        await Until(() => File.Exists(ready), TimeSpan.FromSeconds(10));
        await bed.Worker.SendAsync(new SampleCapacity(), Ct);
        var measured = await bed.Recorded<RunMeasured>(m => m.Run == start.Run).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // 4. The end.
        await File.WriteAllTextAsync(go, "", Ct);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var order = bed.Order(start.Run);
        Assert.Equal(
            [
                nameof(StartRun), nameof(RunProgress), nameof(RunStarted), nameof(RunLiveViewChanged), nameof(SampleCapacity),
                nameof(RunMeasured), nameof(WorkerCapacitySampled), nameof(RunOutput), nameof(RunUsage), nameof(RunEnded),
            ],
            order);

        Assert.Contains(start.Run, bed.Sampled.Single().OpenRuns);
        Assert.Equal(start.Run, measured.Run);
        Assert.True(measured.Processes >= 1, $"{measured.Processes} processes");
        Assert.True(measured.ResidentBytes > 0, $"{measured.ResidentBytes} bytes");

        Assert.Equal([RunLauncher.HeldText("sh")], bed.Progress);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.LaunchError);
        Assert.Equal("the answer", result.Output);
        Assert.Equal(new InvocationUsage(7, 11, "claude"), result.Usage);
    }

    [Fact]
    public async Task The_same_run_through_the_runner_gives_the_same_result()
    {
        // The protocol's answer, beside the runner's own for the same child.
        using var bed = new Bed(_root);
        var script = await Script(
            "printf '%s' '{\"result\":\"the answer\",\"usage\":{\"input_tokens\":7,\"output_tokens\":11}}'\n");

        var direct = await bed.Directory.RunAsync(bed.Worker, bed.Start(Launch("sh", [script], usageFormat: "claude-json")), Ct);

        var catalog = new AgentCatalog(
        [
            new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch("sh", [script], UsageFormat: "claude-json", LanguageModel: false)),
        ]);
        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat());
        var viaRunner = await runner.RunAsync(
            new AgentInvocation(Member, "You are a probe.", "hello", Workspace(), new Dictionary<string, string>(), Agent: "probe"),
            Ct);

        Assert.Equal(viaRunner with { ProcessId = null, AgentTranscript = null }, direct with { ProcessId = null, AgentTranscript = null });
    }

    [Fact]
    public async Task A_stop_crosses_as_cancel_and_ends_as_today()
    {
        using var bed = new Bed(_root);
        var script = await Script("sleep 30\n");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var start = bed.Start(Launch("sh", [script]));
        var running = bed.Directory.RunAsync(bed.Worker, start, stop.Token);
        await bed.Recorded<RunStarted>().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await stop.CancelAsync();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Contains(nameof(CancelRun), bed.Order(start.Run));
        Assert.Equal(nameof(RunEnded), bed.Order(start.Run)[^1]);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(FailureClasses.Interrupted, result.FailureClass);
        Assert.Equal(
            "This run was stopped before it finished, because the Host was shutting down. Whatever it had done is not recorded.",
            result.LaunchError);
    }

    // ---------------------------------------------------------------------------------------------

    private string Workspace() => Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;

    private async Task<string> Script(string body)
    {
        var path = Path.Combine(_root, $"run-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(path, body, Ct);
        return path;
    }

    private static RunLaunch Launch(string fileName, IReadOnlyList<string> arguments, string? usageFormat = null) =>
        new(fileName, arguments, null, null, usageFormat, false, null, null, null, []);

    private static async Task Until(Func<bool> condition, TimeSpan bound)
    {
        var deadline = DateTime.UtcNow + bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Not true within {bound.TotalSeconds:0} s.");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Control and one in-process worker, with every message and event stamped by one counter in the
    /// order it was delivered.
    /// </summary>
    private sealed class Bed : IDisposable, IRunWorker
    {
        private readonly string _root;
        private readonly InProcessWorker _worker;
        private readonly ConcurrentQueue<(long Order, object Record)> _log = new();
        private readonly List<(Func<object, bool> Matches, TaskCompletionSource<object> Found)> _waiters = [];
        private long _counter;

        public Bed(string root, bool reports = true)
        {
            _root = root;
            Reports = new ProgressLines();
            Directory = new RunDirectory(Reports);
            var launcher = new RunLauncher(Heartbeat, reports: reports, lookup: LaunchLookup.Once, updates: Updates);
            _worker = InProcessWorker.Connect(
                WorkerId.Local,
                events => new WorkerHost(
                    WorkerId.Local, events, launcher, Heartbeat, processes: new ProcessGroupReader("/proc"),
                    cgroup: new CgroupReader("/sys/fs/cgroup")),
                async (envelope, ct) =>
                {
                    Record(envelope.Event);
                    await Directory.HandleAsync(envelope, ct);
                });
        }

        public RunHeartbeat Heartbeat { get; } = new();

        public AgentUpdateGate Updates { get; } = new();

        public ProgressLines Reports { get; }

        public RunDirectory Directory { get; }

        /// <summary>Control's handle on the worker, recording what is sent.</summary>
        public IRunWorker Worker => this;

        public IReadOnlyList<string> Progress => [.. Reports.Lines.Select(l => l.Status)];

        public IReadOnlyList<WorkerCapacitySampled> Sampled => [.. _log.Select(l => l.Record).OfType<WorkerCapacitySampled>()];

        public StartRun Start(RunLaunch launch) =>
            new(RunId.For(Member), "probe", "You are a probe.", "hello", "", System.IO.Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName,
                new Dictionary<string, string>(), null, launch, null, null, null);

        /// <summary>The first record of this type, once delivered.</summary>
        public Task<T> Recorded<T>() => Recorded<T>(_ => true);

        /// <summary>The first record of this type that <paramref name="matches"/>, once delivered.</summary>
        public async Task<T> Recorded<T>(Func<T, bool> matches)
        {
            var found = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiters)
            {
                if (_log.Select(l => l.Record).OfType<T>().FirstOrDefault(matches) is { } already) return already;
                _waiters.Add((record => record is T typed && matches(typed), found));
            }

            return (T)await found.Task;
        }

        /// <summary>The names of what crossed about <paramref name="run"/>, in delivery order; a sample counts for every run.</summary>
        public IReadOnlyList<string> Order(RunId run) =>
            [.. _log.OrderBy(l => l.Order)
                .Where(l => l.Record switch
                {
                    RunCommand command => command.Run == run,
                    RunEvent @event => @event.Run == run,
                    SampleCapacity or WorkerCapacitySampled => true,
                    _ => false,
                })
                .Select(l => l.Record.GetType().Name)];

        WorkerId IRunWorker.Id => _worker.Worker.Id;

        Task IRunWorker.Closed => _worker.Worker.Closed;

        async Task IRunWorker.SendAsync(ControlMessage message, CancellationToken ct)
        {
            Record(message);
            await _worker.Worker.SendAsync(message, ct);
        }

        private void Record(object record)
        {
            lock (_waiters)
            {
                _log.Enqueue((Interlocked.Increment(ref _counter), record));
                foreach (var waiter in _waiters.Where(w => w.Matches(record)).ToList())
                {
                    waiter.Found.TrySetResult(record);
                    _waiters.Remove(waiter);
                }
            }
        }

        public void Dispose() => _worker.Dispose();
    }

    private sealed class ProgressLines : IMemberReports
    {
        public ConcurrentQueue<(ContainerId Member, string Status)> Lines { get; } = new();

        public Task<MemberReportOutcome> ProgressAsync(ContainerId member, string status, CancellationToken ct = default)
        {
            Lines.Enqueue((member, status));
            return Task.FromResult(MemberReportOutcome.Ok);
        }

        public Task<MemberReportOutcome> BlockedAsync(ContainerId member, string reason, int? item = null, CancellationToken ct = default) => Ok();
        public Task<MemberReportOutcome> DeferAsync(ContainerId member, int item, string reason, CancellationToken ct = default) => Ok();
        public Task<MemberReportOutcome> NeedsDecisionAsync(ContainerId member, string question, CancellationToken ct = default) => Ok();
        public Task<MemberReportOutcome> HandbackAsync(ContainerId member, string delivered, CancellationToken ct = default) => Ok();
        public Task<MemberReportOutcome> PublishAsync(ContainerId member, string type, string payload, CancellationToken ct = default) => Ok();

        private static Task<MemberReportOutcome> Ok() => Task.FromResult(MemberReportOutcome.Ok);
    }
}
