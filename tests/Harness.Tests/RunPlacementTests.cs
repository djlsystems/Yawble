using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;

namespace Harness.Tests;

/// <summary>
/// Admission places each run on a worker, and the memory headroom it reads is that worker's last
/// capacity sample. The run limit, the Manager's slot, the queue and the words are unchanged.
/// </summary>
public sealed class RunPlacementTests
{
    private static readonly ContainerId Developer = new("Alpha", "Developer");
    private static readonly ContainerId Manager = new("Alpha", WipLedger.ManagerName);

    [Fact]
    public void A_run_is_placed_on_the_one_worker()
    {
        var (wip, _, _) = Admission(inUseGb: 1);

        using var slot = wip.TryEnter(Developer);

        Assert.NotNull(slot);
        Assert.Equal(WorkerId.Local, wip.PlacedOn(Developer));
    }

    [Fact]
    public void A_run_waits_on_its_workers_headroom_with_todays_words()
    {
        var (wip, sampler, worker) = Admission(inUseGb: 9);

        Assert.Null(wip.TryEnter(Developer));

        // The reason is the gate's own sentence, from the figures that worker measured.
        var hold = Assert.Single(wip.View().Waiting);
        Assert.Equal("waiting for memory: 9.0 of 10.0 GB in use", hold.Reason);
        Assert.Equal(1, worker.Samples);
        Assert.Equal(hold.Reason, sampler.Latest!.Admission.Holding);
        Assert.Null(wip.PlacedOn(Developer));
    }

    [Fact]
    public void A_manager_is_placed_without_the_headroom_check()
    {
        var (wip, _, _) = Admission(inUseGb: 9);

        using var slot = wip.TryEnter(Manager);

        Assert.NotNull(slot);
        Assert.Equal(WorkerId.Local, wip.PlacedOn(Manager));
    }

    [Fact]
    public void Placement_is_released_with_the_slot()
    {
        var (wip, _, _) = Admission(inUseGb: 1);

        var slot = wip.TryEnter(Developer);
        Assert.Equal(WorkerId.Local, wip.PlacedOn(Developer));

        slot!.Dispose();

        Assert.Null(wip.PlacedOn(Developer));
        Assert.Empty(wip.View().Running);
    }

    /// <summary>A ledger over one worker whose sample says <paramref name="inUseGb"/> of 10 GB is in use, sampled once.</summary>
    private static (WipLedger Wip, CapacitySampler Sampler, Measuring Worker) Admission(double inUseGb)
    {
        var gate = new HeadroomGate(() => 80, () => 0);
        var workers = new WorkerPool([(WorkerId.Local, gate)]);
        var wip = new WipLedger(3, workers);
        var worker = new Measuring((long)(inUseGb * 1e9), (long)10e9);
        var sampler = new CapacitySampler(worker, wip, gate, new NoLease(), () => 80, () => 0);
        worker.Control = sampler.HandleAsync;
        workers.Connect(worker);

        sampler.Sample();
        return (wip, sampler, worker);
    }

    /// <summary>A worker that answers every sample with the same figures.</summary>
    private sealed class Measuring(long inUse, long limit) : IRunWorker
    {
        private long _seq;

        public Func<WorkerEnvelope, CancellationToken, Task> Control { get; set; } = (_, _) => Task.CompletedTask;

        public int Samples { get; private set; }

        public WorkerId Id => WorkerId.Local;

        public Task Closed { get; } = new TaskCompletionSource().Task;

        public async Task SendAsync(ControlMessage message, CancellationToken ct = default)
        {
            if (message is not SampleCapacity) return;

            Samples++;
            var figures = new CapacityFigures(
                "v2", 4, false, 0, 0, 0, limit, false, inUse, inUse, 0, 0, null, null, null, null, true, []);
            await Control(new WorkerEnvelope(Id, ++_seq, new WorkerCapacitySampled(DateTimeOffset.UtcNow, figures, [])), ct);
        }
    }

    private sealed class NoLease : IHeavyLeaseView
    {
        public HeavyLeaseSnapshot? Read() => null;
    }
}
