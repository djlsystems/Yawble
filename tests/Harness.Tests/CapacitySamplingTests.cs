using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;

namespace Harness.Tests;

/// <summary>
/// A capacity sample asks each worker and waits for its answer, never past the sample's interval:
/// a worker that does not answer is not measured that round, and nothing in control blocks on it.
/// </summary>
public sealed class CapacitySamplingTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_worker_that_never_answers_does_not_hold_the_sample_past_its_interval()
    {
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var gate = new HeadroomGate(() => 80, () => 0, clock);
        var wip = new WipLedger(3, new WorkerPool([(WorkerId.Local, gate)]));
        var worker = new Answering((long)9e9, (long)10e9, clock);
        var sampler = new CapacitySampler(worker, wip, gate, new NoLease(), () => 80, () => 0, clock: clock, interval: Interval);
        worker.Control = sampler.HandleAsync;

        // First it answers: the gate holds runs on what it measured.
        var measured = await sampler.SampleAsync(Ct);
        Assert.Equal((long)10e9, measured.Memory.LimitBytes);
        Assert.Equal("waiting for memory: 9.0 of 10.0 GB in use", gate.Reason());

        // Then it never answers: the sample waits for it no longer than its interval.
        worker.Silent = true;
        var pending = sampler.SampleAsync(Ct);
        Assert.False(pending.IsCompleted);

        clock.Advance(Interval - TimeSpan.FromSeconds(1));
        await Task.Delay(50, Ct);
        Assert.False(pending.IsCompleted);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, Ct);
        }

        var unanswered = await pending.WaitAsync(TimeSpan.FromSeconds(1), Ct);

        // Not measured, never 0, and the gate keeps the figures it had.
        Assert.Null(unanswered.Memory.LimitBytes);
        Assert.Null(unanswered.Memory.InUseBytes);
        Assert.Contains("memory.limit", unanswered.NotMeasured);
        Assert.Equal("waiting for memory: 9.0 of 10.0 GB in use", gate.Reason());
        Assert.Equal("waiting for memory: 9.0 of 10.0 GB in use", unanswered.Admission.Holding);
    }

    [Fact]
    public async Task A_late_answer_is_not_charged_to_the_next_sample()
    {
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var gate = new HeadroomGate(() => 80, () => 0, clock);
        var wip = new WipLedger(3, new WorkerPool([(WorkerId.Local, gate)]));
        var worker = new Answering((long)1e9, (long)10e9, clock) { Silent = true };
        var sampler = new CapacitySampler(worker, wip, gate, new NoLease(), () => 80, () => 0, clock: clock, interval: Interval);
        worker.Control = sampler.HandleAsync;

        var pending = sampler.SampleAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, Ct);
        }

        await pending.WaitAsync(TimeSpan.FromSeconds(1), Ct);

        // The answer to the round that gave up arrives now, between rounds, and goes nowhere.
        await worker.AnswerAsync(new RunMeasured(new RunId(new ContainerId("alpha", "late"), "n"), 7, 1, 100, 1, clock.GetUtcNow()));

        worker.Silent = false;
        var next = await sampler.SampleAsync(Ct);
        Assert.Empty(next.TopByMemory);
        Assert.Equal((long)10e9, next.Memory.LimitBytes);
    }

    [Fact]
    public async Task Each_worker_s_figures_and_runs_are_in_the_sample()
    {
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0, clock), clock: clock);
        var wip = new WipLedger(5, pool);
        var w1 = new Answering((long)2e9, (long)10e9, clock, "w1");
        var w2 = new Answering((long)9e9, (long)10e9, clock, "w2");
        pool.Join(w1, new WorkerInfo(w1.Id, "v", 4, (long)10e9, clock.GetUtcNow()));
        pool.Join(w2, new WorkerInfo(w2.Id, "v", 8, (long)10e9, clock.GetUtcNow()));

        var sampler = new CapacitySampler(
            () => [.. pool.Entries().Select(e => (e.Worker!, e.Gate))], wip, new NoLease(), () => 80, () => 0, clock: clock, interval: Interval)
        {
            Describe = () => WorkersView.Of(pool, wip, "v"),
        };
        w1.Control = w2.Control = sampler.HandleAsync;

        var sample = await sampler.SampleAsync(Ct);

        // Each worker's own figures and reason; the top level is their sum.
        var workers = sample.Workers!;
        Assert.Equal(["w1", "w2"], workers.Select(w => w.Id));
        Assert.Equal((long)2e9, workers[0].Capacity.MemoryInUseBytes);
        Assert.Null(workers[0].Holding);
        Assert.Equal("waiting for memory: 9.0 of 10.0 GB in use", workers[1].Holding);
        Assert.Equal((long)20e9, sample.Memory.LimitBytes);
        Assert.Equal((long)11e9, sample.Memory.InUseBytes);
        Assert.Null(sample.Admission.Holding);
    }

    [Fact]
    public async Task A_top_run_names_its_worker()
    {
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0, clock), clock: clock);
        var wip = new WipLedger(5, pool);
        var w1 = new Answering((long)1e9, (long)10e9, clock, "w1")
        {
            Runs = [new RunMeasured(new RunId(new ContainerId("alpha", "Developer"), "n"), 77, 2, 500_000_000, 10, DateTimeOffset.UnixEpoch)],
        };
        pool.Join(w1, new WorkerInfo(w1.Id, "v", 4, (long)10e9, clock.GetUtcNow()));
        var sampler = new CapacitySampler(
            () => [.. pool.Entries().Select(e => (e.Worker!, e.Gate))], wip, new NoLease(), () => 80, () => 0, clock: clock, interval: Interval);
        w1.Control = sampler.HandleAsync;

        var sample = await sampler.SampleAsync(Ct);

        var top = Assert.Single(sample.TopByMemory);
        Assert.Equal(("Developer", "w1"), (top.Member, top.Worker));
    }

    [Fact]
    public async Task A_manager_over_a_workers_bound_says_so_in_the_samples_running_runs()
    {
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0, clock), bound: _ => 1, clock: clock);
        var wip = new WipLedger(5, pool);
        pool.Placed = wip.PlacedCount;
        var w1 = new Answering((long)2e9, (long)10e9, clock, "w1");
        var w2 = new Answering((long)3e9, (long)10e9, clock, "w2");
        pool.Join(w1, new WorkerInfo(w1.Id, "v", 4, (long)10e9, clock.GetUtcNow()));
        pool.Join(w2, new WorkerInfo(w2.Id, "v", 4, (long)10e9, clock.GetUtcNow()));

        // Neither is measured yet: A takes w1, B w2, and then both are at their bound of 1.
        var manager = new ContainerId("Alpha", WipLedger.ManagerName);
        using var a = wip.TryEnter(new ContainerId("Alpha", "A"));
        using var b = wip.TryEnter(new ContainerId("Alpha", "B"));
        using var slot = wip.TryEnter(manager);
        Assert.Equal(new WorkerId("w1"), wip.PlacedOn(manager));

        var sampler = new CapacitySampler(
            () => [.. pool.Entries().Select(e => (e.Worker!, e.Gate))], wip, new NoLease(), () => 80, () => 0, clock: clock, interval: Interval)
        {
            Describe = () => WorkersView.Of(pool, wip, "v"),
        };
        w1.Control = w2.Control = sampler.HandleAsync;

        var sample = await sampler.SampleAsync(Ct);

        var overBound = $"over w1's bound of 1: {WipLedger.OverBoundReason}";
        Assert.Equal(
            new HashSet<(string, string?)> { ("A", null), ("B", null), (WipLedger.ManagerName, overBound) },
            sample.Runs.Running.Select(r => (r.Member, r.Reason)).ToHashSet());
        var workers = sample.Workers!;
        Assert.Equal(
            new HashSet<(string, string?)> { ("A", null), (WipLedger.ManagerName, overBound) },
            workers.Single(w => w.Id == "w1").Runs.Select(r => (r.Member, r.Reason)).ToHashSet());
        Assert.Equal(("B", (string?)null), workers.Single(w => w.Id == "w2").Runs.Select(r => (r.Member, r.Reason)).Single());
    }

    /// <summary>A worker that answers every sample with the same figures, until it is told to say nothing.</summary>
    private sealed class Answering(long inUse, long limit, TimeProvider clock, string id = "local") : IRunWorker
    {
        public IReadOnlyList<RunMeasured> Runs { get; init; } = [];

        private long _seq;

        public Func<WorkerEnvelope, CancellationToken, Task> Control { get; set; } = (_, _) => Task.CompletedTask;

        public bool Silent { get; set; }

        public WorkerId Id { get; } = new(id);

        public Task Closed { get; } = new TaskCompletionSource().Task;

        public Task AnswerAsync(WorkerEvent @event) => Control(new WorkerEnvelope(Id, ++_seq, @event), CancellationToken.None);

        public async Task SendAsync(ControlMessage message, CancellationToken ct = default)
        {
            if (message is not SampleCapacity) return;

            // A silent worker's send never completes, as a connection that went quiet would not.
            if (Silent)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return;
            }

            var figures = new CapacityFigures(
                "v2", 4, false, 0, 0, 0, limit, false, inUse, inUse, 0, 0, null, null, null, null, true, []);
            foreach (var run in Runs) await AnswerAsync(run);
            await AnswerAsync(new WorkerCapacitySampled(clock.GetUtcNow(), figures, []));
        }
    }

    private sealed class NoLease : IHeavyLeaseView
    {
        public HeavyLeaseSnapshot? Read() => null;
    }
}
