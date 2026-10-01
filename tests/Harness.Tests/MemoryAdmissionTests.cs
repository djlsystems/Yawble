using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Capacity;

namespace Harness.Tests;

/// <summary>
/// ADMIT BY MEASURED HEADROOM. A run the limit has room for still WAITS - exactly as for a slot, with
/// the reason on its hold, never failed or refused - while memory in use or memory pressure is over
/// its threshold, and starts when the next measurement clears it. A Manager is never held by the gate -
/// it is the run that frees the memory - and the run limit, with its reserved slot, still applies to
/// it; figures that are not measured fall back to the run limit alone.
/// </summary>
public sealed class MemoryAdmissionTests : IDisposable
{
    private static readonly ContainerId AlphaWorker = new("Alpha", "Developer");
    private static readonly ContainerId AlphaTester = new("Alpha", "Tester");
    private static readonly ContainerId AlphaManager = new("Alpha", "Manager");
    private static readonly ContainerId BetaWorker = new("Beta", "Developer");

    private const string MemoryReason = "waiting for memory: 11.2 of 12.9 GB in use";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harness-admission-{Guid.NewGuid():N}");
    private readonly SteppedClock _clock = new(DateTimeOffset.Parse("2026-10-01T10:00:00Z"));
    private int _memoryPercent = 80;
    private int _pressurePercent = 10;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private HeadroomGate Gate() => new(() => _memoryPercent, () => _pressurePercent, _clock);

    /// <summary>Measures a fixture cgroup into the gate, as the sampler does.</summary>
    private void Measure(HeadroomGate gate, long anon, long shmem = 1_800_000_000, double pressure = 0.5)
    {
        var root = CapacityMeasurementTests.WriteV2(
            Path.Combine(_root, Guid.NewGuid().ToString("N")), anon: anon, shmem: shmem, memorySome10: pressure);
        gate.Update(new CgroupReader(root).Read(), _clock.GetUtcNow());
    }

    [Fact]
    public async Task A_run_waits_with_the_memory_reason_while_the_threshold_is_crossed_and_starts_when_it_clears()
    {
        var gate = Gate();
        var wip = new WipLedger(4, gate.Reason);
        await using var bed = new ContainerTestBed(wip);
        await bed.AddAsync(AlphaWorker);

        // 9.4 GB anonymous + 1.8 GB shmem of a 12.9 GB limit: 87%, over 80%.
        Measure(gate, anon: 9_400_000_000);

        await InstructAsync(bed, AlphaWorker);
        Assert.True(await bed.PumpUntilAsync(() => WaitReason(wip, AlphaWorker) == MemoryReason));
        await bed.SettleAsync();

        // Waiting, as for a slot: not run, not failed, shown held.
        Assert.Equal(0, bed.Agent.RunsFor(AlphaWorker));
        Assert.True(bed.Host.Find(AlphaWorker)!.Snapshot().Held);
        Assert.Empty(wip.View().Running);

        // The next measurement is under the threshold: the waiter asks again and starts.
        Measure(gate, anon: 5_000_000_000);
        wip.HeadroomChanged();

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaWorker) == 1));
        Assert.Empty(wip.View().Waiting);
    }

    [Fact]
    public void A_waiters_reason_follows_the_measurement_and_its_place_in_the_queue_is_kept()
    {
        var gate = Gate();
        var wip = new WipLedger(1, gate.Reason);
        Measure(gate, anon: 9_400_000_000);

        // Alpha has the limit's one slot to itself and waits for memory; Beta is behind it for the slot.
        Assert.Null(wip.TryEnter(AlphaWorker));
        Assert.Null(wip.TryEnter(BetaWorker));
        Assert.Equal(MemoryReason, WaitReason(wip, AlphaWorker));
        Assert.Equal(WipLedger.SlotReason, WaitReason(wip, BetaWorker));
        var since = wip.View().Waiting[0].Since;

        Measure(gate, anon: 9_800_000_000);
        wip.HeadroomChanged();

        Assert.Equal(["Alpha", "Beta"], wip.View().Waiting.Select(hold => hold.Team));
        Assert.Equal("waiting for memory: 11.6 of 12.9 GB in use", WaitReason(wip, AlphaWorker));
        Assert.Equal(since, wip.View().Waiting[0].Since);

        Measure(gate, anon: 1_000_000_000);
        wip.HeadroomChanged();

        // First come, first served once it clears: Beta asking first is still behind Alpha.
        Assert.Null(wip.TryEnter(BetaWorker));
        Assert.NotNull(wip.TryEnter(AlphaWorker));
    }

    [Fact]
    public void Memory_pressure_over_its_threshold_holds_a_run_with_its_own_reason()
    {
        var gate = Gate();
        var wip = new WipLedger(4, gate.Reason);
        Measure(gate, anon: 1_000_000_000, pressure: 14);

        Assert.Null(wip.TryEnter(AlphaWorker));
        Assert.Equal(
            "waiting for memory: work waited for memory 14% of the last 10 s",
            WaitReason(wip, AlphaWorker));
    }

    [Fact]
    public void The_thresholds_are_read_through_their_delegates_on_every_claim()
    {
        var gate = Gate();
        var wip = new WipLedger(4, gate.Reason);
        Measure(gate, anon: 9_400_000_000, pressure: 14);

        Assert.Null(wip.TryEnter(AlphaWorker));

        // A setting moved at runtime applies to the next claim; 0 turns a check off.
        _memoryPercent = 90;
        _pressurePercent = 0;

        Assert.NotNull(wip.TryEnter(AlphaWorker));
    }

    [Fact]
    public void The_managers_reserved_slot_still_applies_under_the_gate()
    {
        var gate = Gate();
        var wip = new WipLedger(1, gate.Reason);
        Measure(gate, anon: 1_000_000_000);

        Assert.NotNull(wip.TryEnter(AlphaWorker));
        Assert.Null(wip.TryEnter(AlphaTester));
        Assert.Equal(WipLedger.SlotReason, WaitReason(wip, AlphaTester));

        // The pool is full of members; the Manager still starts, in the one slot above the limit.
        Assert.NotNull(wip.TryEnter(AlphaManager));
        Assert.Equal(2, wip.View().Running.Count);
    }

    [Fact]
    public void A_manager_starts_over_the_memory_threshold_while_a_member_waits_with_the_memory_reason()
    {
        var gate = Gate();
        var wip = new WipLedger(4, gate.Reason);
        Measure(gate, anon: 9_400_000_000, pressure: 14);

        Assert.Null(wip.TryEnter(AlphaWorker));
        Assert.Equal(MemoryReason, WaitReason(wip, AlphaWorker));

        // The Manager is the run that stops or redirects the work holding the memory: the gate never holds it.
        Assert.NotNull(wip.TryEnter(AlphaManager));
        Assert.Equal(["Manager"], wip.View().Running.Select(hold => hold.Member));
        Assert.Equal(MemoryReason, WaitReason(wip, AlphaWorker));
    }

    [Fact]
    public void Over_the_memory_threshold_a_manager_still_gets_only_its_reserved_slot_when_the_limit_is_full()
    {
        var gate = Gate();
        var wip = new WipLedger(1, gate.Reason);

        Assert.NotNull(wip.TryEnter(AlphaWorker));
        Measure(gate, anon: 9_400_000_000);

        // The limit is full; the Manager takes the one slot above it, as with no gate.
        Assert.NotNull(wip.TryEnter(AlphaManager));

        // The reserved slot is taken: a second Manager waits for a slot, never for memory.
        var betaManager = new ContainerId("Beta", "Manager");
        Assert.Null(wip.TryEnter(betaManager));
        Assert.Equal(WipLedger.SlotReason, WaitReason(wip, betaManager));
        Assert.Equal(2, wip.View().Running.Count);
    }

    [Fact]
    public async Task A_pool_full_of_members_still_starts_a_manager_wake_when_memory_has_room()
    {
        var gate = Gate();
        var wip = new WipLedger(1, gate.Reason);
        await using var bed = new ContainerTestBed(wip);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bed.Agent.Behaviour = async invocation =>
        {
            if (invocation.Container == AlphaWorker) await release.Task;
            return new AgentResult(0, "done");
        };
        await bed.AddAsync(AlphaWorker);
        await bed.AddAsync(AlphaManager);
        Measure(gate, anon: 1_000_000_000);

        await InstructAsync(bed, AlphaWorker);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaWorker) == 1));

        await InstructAsync(bed, AlphaManager);
        Assert.True(
            await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaManager) == 1),
            "The Manager waited behind a worker for a slot.");

        release.SetResult();
    }

    [Fact]
    public void Not_measured_falls_back_to_the_run_limit_alone()
    {
        var gate = Gate();
        var wip = new WipLedger(2, gate.Reason);

        // No cgroup at all: nothing measured, so nothing held but by the limit.
        gate.Update(new CgroupReader(Path.Combine(_root, "absent")).Read(), _clock.GetUtcNow());

        Assert.NotNull(wip.TryEnter(AlphaWorker));
        Assert.NotNull(wip.TryEnter(AlphaTester));
        Assert.Null(wip.TryEnter(BetaWorker));
        Assert.Equal(WipLedger.SlotReason, WaitReason(wip, BetaWorker));
    }

    [Fact]
    public void A_container_with_no_memory_limit_or_no_pressure_file_falls_back_for_that_check()
    {
        var gate = Gate();
        var wip = new WipLedger(4, gate.Reason);
        var root = CapacityMeasurementTests.WriteV2(Path.Combine(_root, "nolimit"), memoryMax: "max", anon: 50_000_000_000, memorySome10: 50);
        File.Delete(Path.Combine(root, "memory.pressure"));

        gate.Update(new CgroupReader(root).Read(), _clock.GetUtcNow());

        Assert.NotNull(wip.TryEnter(AlphaWorker));
    }

    [Fact]
    public void A_measurement_gone_stale_is_not_measured_and_holds_nothing()
    {
        var gate = Gate();
        var wip = new WipLedger(4, gate.Reason);
        Measure(gate, anon: 9_400_000_000);
        Assert.Null(wip.TryEnter(AlphaWorker));

        _clock.Advance(HeadroomGate.StaleAfter + TimeSpan.FromSeconds(1));

        Assert.NotNull(wip.TryEnter(AlphaWorker));
    }

    [Fact]
    public async Task Headroom_changed_wakes_a_waiter_only_when_it_has_cleared()
    {
        var gate = Gate();
        var wip = new WipLedger(4, gate.Reason);
        Measure(gate, anon: 9_400_000_000);
        Assert.Null(wip.TryEnter(AlphaWorker));

        var signal = wip.Changed;
        Measure(gate, anon: 9_500_000_000);
        wip.HeadroomChanged();
        Assert.False(signal.IsCompleted);

        Measure(gate, anon: 1_000_000_000);
        wip.HeadroomChanged();
        await signal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private static Task InstructAsync(ContainerTestBed bed, ContainerId id) =>
        bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(id), """{"instruction":"build it"}""", "console"),
            TestContext.Current.CancellationToken);

    private static string? WaitReason(WipLedger wip, ContainerId id) =>
        wip.View().Waiting.FirstOrDefault(hold => hold.Team == id.Team && hold.Member == id.Name)?.Reason;
}
