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

    /// <summary>A worker that answers every sample with the same figures, until it is told to say nothing.</summary>
    private sealed class Answering(long inUse, long limit, TimeProvider clock) : IRunWorker
    {
        private long _seq;

        public Func<WorkerEnvelope, CancellationToken, Task> Control { get; set; } = (_, _) => Task.CompletedTask;

        public bool Silent { get; set; }

        public WorkerId Id => WorkerId.Local;

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
            await AnswerAsync(new WorkerCapacitySampled(clock.GetUtcNow(), figures, []));
        }
    }

    private sealed class NoLease : IHeavyLeaseView
    {
        public HeavyLeaseSnapshot? Read() => null;
    }
}
