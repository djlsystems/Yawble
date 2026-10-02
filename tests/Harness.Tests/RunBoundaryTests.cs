using System.Collections.Concurrent;
using System.Diagnostics;
using Harness.Containers;
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

    /// <summary>How long a lost run may take to be seen as one. A run past it failed, it did not hang.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

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
    // A run whose end never arrives is a lost run, never a hang.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_transport_that_drops_the_end_is_a_lost_run_not_a_hang()
    {
        var swallowed = 0;
        var bed = new Bed(_root, drop: e => e is RunEnded && Interlocked.Increment(ref swallowed) > 0);
        try
        {
            var start = bed.Start(Launch("sh", ["-c", "exit 0"]));
            var elapsed = Stopwatch.StartNew();
            var running = bed.Directory.RunAsync(bed.Worker, start, Ct);

            await Until(() => Volatile.Read(ref swallowed) == 1, Bound);
            await bed.Worker.SendAsync(new SampleCapacity(), Ct);
            var result = await running.WaitAsync(Bound, Ct);
            Say($"The dropped end was a lost run after {elapsed.Elapsed.TotalSeconds:0.000} s.");

            Assert.Equal(1, Volatile.Read(ref swallowed));
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal(FailureClasses.Interrupted, result.FailureClass);
            Assert.Equal(RunDirectory.LostRunText, result.LaunchError);
        }
        finally
        {
            // Closed only after the bounded wait: closing is a trigger of its own.
            bed.Dispose();
        }
    }

    [Fact]
    public async Task A_dropped_end_through_the_member_runtime_frees_its_slot_and_ends_its_card()
    {
        var swallowed = 0;
        var bed = new Bed(_root, drop: e => e is RunEnded && Interlocked.Increment(ref swallowed) > 0);
        try
        {
            var ended = new ConcurrentQueue<ContainerId>();
            var wip = new WipLedger(1);
            await using var members = new ContainerTestBed(
                wip, onRunEnding: (id, _, _, _) => { ended.Enqueue(id); return Task.FromResult(true); });

            var script = await Script("exit 0\n");
            var catalog = new AgentCatalog(
            [
                new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch("sh", [script], LanguageModel: false)),
            ]);
            var runner = new ProcessAgentRunner(catalog, bed.Worker, bed.Directory, bed.Launcher, () => null, null);
            var id = new ContainerId("alpha", "lost");
            await members.Host.AddAsync(
                new ContainerDefinition(
                    id, "probe", SystemPrompt: "You are a probe.", WorkingDirectory: Workspace(), Subscribes: [],
                    Environment: new Dictionary<string, string>()),
                members.AsMember(runner),
                Ct);

            var elapsed = Stopwatch.StartNew();
            await members.Store.AppendAsync(
                new NewMessage(MessageTypes.InstructionFor(id), """{"instruction":"build it"}""", "console"), Ct);

            await Until(() => Volatile.Read(ref swallowed) == 1, Bound, () => members.Host.PumpOnceAsync(Ct));
            await bed.Worker.SendAsync(new SampleCapacity(), Ct);
            await Until(() => !ended.IsEmpty && members.Host.Find(id)!.State != ContainerState.Running, Bound, () => members.Host.PumpOnceAsync(Ct));
            Say($"The member's dropped end was a lost run after {elapsed.Elapsed.TotalSeconds:0.000} s.");

            var failed = Assert.Single(await members.OfTypeAsync(MessageTypes.Failed));
            using var payload = System.Text.Json.JsonDocument.Parse(failed.Payload);
            Assert.Equal(RunDirectory.LostRunText, payload.RootElement.GetProperty("launchError").GetString());
            Assert.Equal(FailureClasses.Interrupted, payload.RootElement.GetProperty("failureClass").GetString());
            Assert.Equal([id], ended);
            Assert.Empty(wip.View().Running);
        }
        finally
        {
            bed.Dispose();
        }
    }

    [Fact]
    public async Task A_closed_transport_ends_an_open_run_as_interrupted()
    {
        var bed = new Bed(_root);
        var go = Path.Combine(_root, "go");
        try
        {
            var script = await Script($"i=0; while [ ! -e '{go}' ] && [ $i -lt 200 ]; do sleep 0.05; i=$((i+1)); done\n");
            var start = bed.Start(Launch("sh", [script]));
            var running = bed.Directory.RunAsync(bed.Worker, start, Ct);
            await bed.Recorded<RunStarted>().WaitAsync(Bound, Ct);

            var elapsed = Stopwatch.StartNew();
            bed.Dispose();
            var result = await running.WaitAsync(Bound, Ct);
            Say($"The closed connection's run was lost after {elapsed.Elapsed.TotalSeconds:0.000} s.");

            Assert.Equal(FailureClasses.Interrupted, result.FailureClass);
            Assert.Equal(RunDirectory.LostRunText, result.LaunchError);
        }
        finally
        {
            File.WriteAllText(go, "");
            bed.Dispose();
        }
    }

    [Fact]
    public async Task A_start_that_cannot_be_sent_ends_as_interrupted()
    {
        var directory = new RunDirectory();
        var start = new StartRun(RunId.For(Member), "probe", "s", "p", "", _root, new Dictionary<string, string>(), null, null, null, null, null);

        var result = await directory.RunAsync(new Refusing(), start, Ct).WaitAsync(Bound, Ct);

        Assert.Equal(FailureClasses.Interrupted, result.FailureClass);
        Assert.Equal(RunDirectory.LostRunText, result.LaunchError);
    }

    [Fact]
    public async Task A_connection_that_closes_after_the_start_and_before_the_process_ends_it_as_interrupted()
    {
        var bed = new Bed(_root);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Applied, and held before its process starts.
            var update = bed.Updates.UpdateAsync("sh", _ => release.Task, Ct);
            var start = bed.Start(Launch("sh", ["-c", "exit 0"]));
            var running = bed.Directory.RunAsync(bed.Worker, start, Ct);
            await bed.Recorded<RunProgress>().WaitAsync(Bound, Ct);

            bed.Dispose();
            var result = await running.WaitAsync(Bound, Ct);

            Assert.Equal(FailureClasses.Interrupted, result.FailureClass);
            Assert.Equal(RunDirectory.LostRunText, result.LaunchError);
            Assert.DoesNotContain(nameof(RunStarted), bed.Order(start.Run));
        }
        finally
        {
            release.TrySetResult(true);
            bed.Dispose();
        }
    }

    [Fact]
    public async Task An_unanswered_stop_ends_as_interrupted_after_the_backstop()
    {
        await RunBounded().WaitAsync(Bound, Ct);

        async Task RunBounded()
        {
            var time = new ManualTime(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
            var directory = new RunDirectory(clock: time);
            var deaf = new Deaf(directory);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var start = new StartRun(RunId.For(Member), "probe", "s", "p", "", _root, new Dictionary<string, string>(), null, null, null, null, null);

            var running = directory.RunAsync(deaf, start, stop.Token);
            await stop.CancelAsync();
            await Until(() => deaf.Cancels == 1, Bound);

            time.Advance(RunDirectory.StopBackstop - TimeSpan.FromMilliseconds(1));
            await Task.Delay(50, Ct);
            Assert.False(running.IsCompleted, "the run was taken as lost before the backstop");

            time.Advance(TimeSpan.FromMilliseconds(1));
            var result = await running.WaitAsync(Bound, Ct);

            Assert.Equal(FailureClasses.Interrupted, result.FailureClass);
            Assert.Equal(RunDirectory.LostRunText, result.LaunchError);
        }
    }

    [Fact]
    public async Task An_end_during_a_sample_is_not_a_lost_run()
    {
        using var bed = new Bed(_root);

        for (var batch = 0; batch < 4; batch++)
        {
            var elapsed = Stopwatch.StartNew();
            for (var i = 0; i < 50; i++)
            {
                using var both = new Barrier(2);
                var start = bed.Start(Launch("sh", ["-c", "exit 0"]));
                var run = Task.Run(() => { both.SignalAndWait(Ct); return bed.Directory.RunAsync(bed.Worker, start, Ct); }, Ct);
                var sample = Task.Run(async () => { both.SignalAndWait(Ct); await bed.Worker.SendAsync(new SampleCapacity(), Ct); }, Ct);

                var result = await run.WaitAsync(Bound, Ct);
                await sample.WaitAsync(Bound, Ct);

                Assert.NotEqual(FailureClasses.Interrupted, result.FailureClass);
                Assert.Equal(0, result.ExitCode);
            }

            Assert.True(elapsed.Elapsed < Bound, $"batch {batch} took {elapsed.Elapsed.TotalSeconds:0.0} s");
        }

        Assert.Equal(bed.Seqs.Order(), bed.Seqs);
        Assert.Equal(bed.Seqs.Distinct().Count(), bed.Seqs.Count);
    }

    // ---------------------------------------------------------------------------------------------
    // Leases and the idle clock.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_granted_lease_raises_the_allowance_before_the_call_answers()
    {
        using var bed = new Bed(_root);
        var reports = new ProgressLines();
        var leases = new LeaseActions(new InstanceLeases(() => 1), bed.Worker, reports);

        var answer = await leases.AcquireAsync(InstanceLeases.Heavy, LeaseOwner.For(Member), Ct);

        // Applied on the worker by the time the call answered: its allowances read this set.
        Assert.Equal(LeaseOutcome.Granted, answer.Outcome);
        Assert.Equal([Member.ToString()], bed.Host.HeavyHolders);
        Assert.Contains(bed.Sent, m => m is ChangeRunMemoryAllowance { HeavyHolders: [var key] } && key == Member.ToString());

        await leases.ReleaseAsync(InstanceLeases.Heavy, LeaseOwner.For(Member), Ct);

        Assert.Empty(bed.Host.HeavyHolders);
    }

    [Fact]
    public async Task A_queued_lease_holds_the_idle_clock_across_the_transport()
    {
        using var bed = new Bed(_root);
        var holding = new ContainerId("alpha", "holder");
        var held = new ConcurrentQueue<bool>();
        using var clock = bed.Heartbeat.WhileRunning(Member, () => { }, paused => held.Enqueue(paused));
        var leases = new LeaseActions(new InstanceLeases(() => 1), bed.Worker, new ProgressLines());

        await leases.AcquireAsync(InstanceLeases.Heavy, LeaseOwner.For(holding), Ct);
        var queued = await leases.AcquireAsync(InstanceLeases.Heavy, LeaseOwner.For(Member), Ct);
        await leases.ReleaseAsync(InstanceLeases.Heavy, LeaseOwner.For(holding), Ct);

        Assert.Equal(LeaseOutcome.Queued, queued.Outcome);
        Assert.Equal([true, false], held);
        Assert.Equal(
            [new HoldIdleClock(Member, true), new HoldIdleClock(Member, false)],
            bed.Sent.OfType<HoldIdleClock>().Where(h => h.Member == Member));
    }

    [Fact]
    public async Task A_progress_report_touches_the_idle_clock_across_the_transport()
    {
        using var bed = new Bed(_root);
        await using var members = new ContainerTestBed();
        await members.AddAsync(Member);
        var touched = 0;
        using var clock = bed.Heartbeat.WhileRunning(Member, () => Interlocked.Increment(ref touched));
        var reports = new MemberReports(members.Host, members.Store, bed.Worker);

        var outcome = await reports.ProgressAsync(Member, "still going", Ct);

        Assert.Equal(MemberReportOutcome.Ok, outcome);
        Assert.Equal(1, Volatile.Read(ref touched));
        Assert.Equal([new TouchIdleClock(Member)], bed.Sent.OfType<TouchIdleClock>());
    }

    [Fact]
    public void Stopping_the_host_does_not_close_its_worker_so_a_run_ends_as_today_not_lost()
    {
        // The service container disposes what it built when the Host stops. The worker is not
        // disposable, so a run still ending then ends in the Host's own words
        // (A_stop_crosses_as_cancel_and_ends_as_today), never as lost.
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(InProcessWorker)));
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(typeof(InProcessWorker)));
    }

    // ---------------------------------------------------------------------------------------------

    private static void Say(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private string Workspace() => Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;

    private async Task<string> Script(string body)
    {
        var path = Path.Combine(_root, $"run-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(path, body, Ct);
        return path;
    }

    private static RunLaunch Launch(string fileName, IReadOnlyList<string> arguments, string? usageFormat = null) =>
        new(fileName, arguments, null, null, usageFormat, false, null, null, null, []);

    private static async Task Until(Func<bool> condition, TimeSpan bound, Func<Task>? between = null)
    {
        var deadline = DateTime.UtcNow + bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Not true within {bound.TotalSeconds:0} s.");
            if (between is not null) await between();
            await Task.Delay(20);
        }
    }

    /// <summary>A worker whose connection refuses every send.</summary>
    private sealed class Refusing : IRunWorker
    {
        public WorkerId Id => WorkerId.Local;

        public Task Closed { get; } = new TaskCompletionSource().Task;

        public Task SendAsync(ControlMessage message, CancellationToken ct = default) =>
            Task.FromException(new InvalidOperationException("The connection refused it."));
    }

    /// <summary>A worker that starts what it is sent and then hears nothing: a stop never ends.</summary>
    private sealed class Deaf(RunDirectory control) : IRunWorker
    {
        private int _cancels;

        public int Cancels => Volatile.Read(ref _cancels);

        public WorkerId Id => WorkerId.Local;

        public Task Closed { get; } = new TaskCompletionSource().Task;

        public async Task SendAsync(ControlMessage message, CancellationToken ct = default)
        {
            switch (message)
            {
                case StartRun start:
                    await control.HandleAsync(new WorkerEnvelope(Id, 1, new RunStarted(start.Run, 4242, DateTimeOffset.UnixEpoch)), ct);
                    break;
                case CancelRun:
                    Interlocked.Increment(ref _cancels);
                    break;
            }
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

        public Bed(string root, bool reports = true, Func<WorkerEvent, bool>? drop = null)
        {
            _root = root;
            Reports = new ProgressLines();
            Directory = new RunDirectory(Reports);
            Launcher = new RunLauncher(Heartbeat, reports: reports, lookup: LaunchLookup.Once, updates: Updates);
            _worker = InProcessWorker.Connect(
                WorkerId.Local,
                events => new WorkerHost(
                    WorkerId.Local, events, Launcher, Heartbeat, processes: new ProcessGroupReader("/proc"),
                    cgroup: new CgroupReader("/sys/fs/cgroup")),
                async (envelope, ct) =>
                {
                    lock (Seqs) Seqs.Add(envelope.Seq);
                    if (drop?.Invoke(envelope.Event) == true) return;

                    Record(envelope.Event);
                    await Directory.HandleAsync(envelope, ct);
                });
        }

        /// <summary>The worker itself, for what it holds.</summary>
        public WorkerHost Host => _worker.Host;

        /// <summary>Every control message sent to the worker, in order.</summary>
        public IReadOnlyList<ControlMessage> Sent => [.. _log.OrderBy(l => l.Order).Select(l => l.Record).OfType<ControlMessage>()];

        /// <summary>Every envelope's sequence number, in the order it was delivered.</summary>
        public List<long> Seqs { get; } = [];

        public RunLauncher Launcher { get; }

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

        public void Dispose() => _worker.Close();
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

/// <summary>A clock a test moves by hand; its timers fire when it passes their time.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<Timer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<Timer> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(t => t.Due <= _now)];
            foreach (var timer in due) _timers.Remove(timer);
        }

        foreach (var timer in due) timer.Fire();
    }

    private sealed class Timer(ManualTime clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;

                Due = clock._now + dueTime;
                clock._timers.Add(this);
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
