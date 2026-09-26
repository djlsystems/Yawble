using Harness.Containers;
using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// THE WIP CLAIM LOOP. `Wip:MaxRunning` is instance-wide: work that cannot start waits rather than
/// fails, a released slot goes to whoever has waited longest, a Manager is never held behind its
/// own workers, and a paused team takes no slot at all.
/// </summary>
public sealed class WipClaimTests
{
    private static readonly ContainerId AlphaWorker = new("Alpha", "Developer");
    private static readonly ContainerId AlphaTester = new("Alpha", "Tester");
    private static readonly ContainerId AlphaManager = new("Alpha", "Manager");
    private static readonly ContainerId BetaWorker = new("Beta", "Developer");
    private static readonly ContainerId BetaManager = new("Beta", "Manager");
    private static readonly ContainerId GammaWorker = new("Gamma", "Developer");

    [Fact]
    public async Task Work_over_the_cap_waits_and_starts_when_the_slot_is_released()
    {
        var wip = new WipLedger(1);
        await using var bed = new ContainerTestBed(wip);
        var release = HoldRunsOf(bed, AlphaWorker);

        await bed.AddAsync(AlphaWorker);
        await bed.AddAsync(AlphaTester);

        await InstructAsync(bed, AlphaWorker);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaWorker) == 1));

        await InstructAsync(bed, AlphaTester);
        Assert.True(await bed.PumpUntilAsync(() => IsWaiting(wip, AlphaTester)));
        await bed.SettleAsync();

        Assert.Equal(0, bed.Agent.RunsFor(AlphaTester));
        Assert.Equal(ContainerState.Running, bed.Host.Find(AlphaWorker)!.State);

        release.SetResult();

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaTester) == 1));
    }

    [Fact]
    public void A_released_slot_goes_to_the_longest_waiter()
    {
        // At the ledger rather than through two polling consumers: which of two containers polls
        // first after a release is a timing race, and a test that passes on the lucky ordering
        // proves nothing. The claim loop asks exactly this question.
        var wip = new WipLedger(1);

        var held = wip.TryEnter(AlphaWorker);
        Assert.NotNull(held);
        Assert.Null(wip.TryEnter(AlphaTester));
        Assert.Null(wip.TryEnter(BetaWorker));

        held!.Dispose();

        // The later waiter asks first and is still refused: the slot is the earlier waiter's.
        Assert.Null(wip.TryEnter(BetaWorker));
        Assert.NotNull(wip.TryEnter(AlphaTester));
    }

    [Fact]
    public async Task A_manager_is_not_held_behind_its_workers()
    {
        var wip = new WipLedger(1);
        await using var bed = new ContainerTestBed(wip);
        var release = HoldRunsOf(bed, AlphaWorker);

        await bed.AddAsync(AlphaWorker);
        await bed.AddAsync(AlphaManager);

        await InstructAsync(bed, AlphaWorker);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaWorker) == 1));

        await InstructAsync(bed, AlphaManager);

        Assert.True(
            await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaManager) == 1),
            "The Manager waited for a worker's slot.");
        Assert.Equal(ContainerState.Running, bed.Host.Find(AlphaWorker)!.State);

        release.SetResult();
    }

    [Fact]
    public async Task A_paused_team_is_passed_over_for_a_released_slot()
    {
        var wip = new WipLedger(1);
        await using var bed = new ContainerTestBed(wip);
        var release = HoldRunsOf(bed, AlphaWorker);

        await bed.AddAsync(AlphaWorker);
        await bed.AddAsync(BetaWorker);
        await bed.AddAsync(AlphaTester);

        await InstructAsync(bed, AlphaWorker);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaWorker) == 1));

        // Beta's work was accepted before the pause and is waiting in the claim loop.
        await InstructAsync(bed, BetaWorker);
        Assert.True(await bed.PumpUntilAsync(() => IsWaiting(wip, BetaWorker)));

        await bed.Host.SetPausedAsync(BetaWorker.Team, paused: true);

        await InstructAsync(bed, AlphaTester);
        Assert.True(await bed.PumpUntilAsync(() => IsWaiting(wip, AlphaTester)));

        release.SetResult();

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaTester) == 1));
        await bed.SettleAsync();

        Assert.Equal(0, bed.Agent.RunsFor(BetaWorker));
        Assert.DoesNotContain(wip.View().Running, hold => hold.Team == BetaWorker.Team);
    }

    [Fact]
    public async Task Released_slots_go_to_waiters_in_the_order_they_queued()
    {
        var wip = new WipLedger(1);
        await using var bed = new ContainerTestBed(wip);
        var release = HoldRunsOf(bed, AlphaWorker, BetaWorker, GammaWorker);

        await bed.AddAsync(AlphaWorker);
        await bed.AddAsync(BetaWorker);
        await bed.AddAsync(GammaWorker);

        await InstructAsync(bed, AlphaWorker);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaWorker) == 1));

        await InstructAsync(bed, BetaWorker);
        Assert.True(await bed.PumpUntilAsync(() => IsWaiting(wip, BetaWorker)));
        await InstructAsync(bed, GammaWorker);
        Assert.True(await bed.PumpUntilAsync(() => IsWaiting(wip, GammaWorker)));

        Assert.Equal(["Beta", "Gamma"], wip.View().Waiting.Select(hold => hold.Team));

        release[AlphaWorker].SetResult();

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(BetaWorker) == 1));
        await bed.SettleAsync();
        Assert.Equal(0, bed.Agent.RunsFor(GammaWorker));
        Assert.True(IsWaiting(wip, GammaWorker));

        release[BetaWorker].SetResult();

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(GammaWorker) == 1));
        release[GammaWorker].SetResult();
    }

    [Fact]
    public async Task A_held_claim_shows_on_the_snapshot_until_the_run_starts()
    {
        var wip = new WipLedger(1);
        await using var bed = new ContainerTestBed(wip);
        var release = HoldRunsOf(bed, AlphaWorker);

        await bed.AddAsync(AlphaWorker);
        await bed.AddAsync(AlphaTester);

        await InstructAsync(bed, AlphaWorker);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaWorker) == 1));
        Assert.False(bed.Host.Find(AlphaWorker)!.Snapshot().Held);

        await InstructAsync(bed, AlphaTester);
        Assert.True(await bed.PumpUntilAsync(() => bed.Host.Find(AlphaTester)!.Snapshot().Held));

        release.SetResult();

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaTester) == 1));
        Assert.False(bed.Host.Find(AlphaTester)!.Snapshot().Held);
    }

    [Fact]
    public async Task A_pool_full_of_members_still_starts_a_manager_wake()
    {
        var wip = new WipLedger(2);
        await using var bed = new ContainerTestBed(wip);
        var release = HoldRunsOf(bed, AlphaWorker, AlphaTester, BetaManager);

        await bed.AddAsync(AlphaWorker);
        await bed.AddAsync(AlphaTester);
        await bed.AddAsync(GammaWorker);
        await bed.AddAsync(BetaManager);

        await InstructAsync(bed, AlphaWorker);
        await InstructAsync(bed, AlphaTester);
        Assert.True(await bed.PumpUntilAsync(
            () => bed.Agent.RunsFor(AlphaWorker) == 1 && bed.Agent.RunsFor(AlphaTester) == 1));

        // A third member waits: the pool is full.
        await InstructAsync(bed, GammaWorker);
        Assert.True(await bed.PumpUntilAsync(() => IsWaiting(wip, GammaWorker)));

        // Another team's Manager is not held behind them, nor behind the member queued first.
        await InstructAsync(bed, BetaManager);
        Assert.True(
            await bed.PumpUntilAsync(() => bed.Agent.RunsFor(BetaManager) == 1),
            "A Manager wake waited behind a pool full of members.");
        Assert.Equal(0, bed.Agent.RunsFor(GammaWorker));
        Assert.Equal(3, wip.View().Running.Count);

        foreach (var held in release.Values) held.SetResult();
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(GammaWorker) == 1));
    }

    [Fact]
    public async Task Raising_the_limit_at_runtime_starts_waiting_work_without_a_release()
    {
        var wip = new WipLedger(1);
        await using var bed = new ContainerTestBed(wip);
        var release = HoldRunsOf(bed, AlphaWorker, AlphaTester);

        await bed.AddAsync(AlphaWorker);
        await bed.AddAsync(AlphaTester);

        await InstructAsync(bed, AlphaWorker);
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaWorker) == 1));
        await InstructAsync(bed, AlphaTester);
        Assert.True(await bed.PumpUntilAsync(() => IsWaiting(wip, AlphaTester)));

        wip.SetMax(2);

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(AlphaTester) == 1));
        Assert.Equal(ContainerState.Running, bed.Host.Find(AlphaWorker)!.State);

        foreach (var held in release.Values) held.SetResult();
    }

    [Fact]
    public async Task Lowering_the_limit_at_runtime_evicts_nothing_and_holds_the_next_start()
    {
        var wip = new WipLedger(2);
        await using var bed = new ContainerTestBed(wip);
        var release = HoldRunsOf(bed, AlphaWorker, AlphaTester, BetaWorker);

        await bed.AddAsync(AlphaWorker);
        await bed.AddAsync(AlphaTester);
        await bed.AddAsync(BetaWorker);

        await InstructAsync(bed, AlphaWorker);
        await InstructAsync(bed, AlphaTester);
        Assert.True(await bed.PumpUntilAsync(
            () => bed.Agent.RunsFor(AlphaWorker) == 1 && bed.Agent.RunsFor(AlphaTester) == 1));

        wip.SetMax(1);

        // Nothing running was stopped.
        Assert.Equal(ContainerState.Running, bed.Host.Find(AlphaWorker)!.State);
        Assert.Equal(ContainerState.Running, bed.Host.Find(AlphaTester)!.State);
        Assert.Equal(1, wip.View().Max);

        await InstructAsync(bed, BetaWorker);
        Assert.True(await bed.PumpUntilAsync(() => IsWaiting(wip, BetaWorker)));

        // One release leaves one running: that is the new limit, so Beta still waits.
        release[AlphaWorker].SetResult();
        Assert.True(await bed.PumpUntilAsync(() => bed.Host.Find(AlphaWorker)!.State == ContainerState.Idle));
        await bed.SettleAsync();
        Assert.Equal(0, bed.Agent.RunsFor(BetaWorker));

        release[AlphaTester].SetResult();
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(BetaWorker) == 1));
        release[BetaWorker].SetResult();
    }

    [Fact]
    public void Set_max_up_and_down_at_the_ledger()
    {
        var wip = new WipLedger(2);
        var a = wip.TryEnter(AlphaWorker);
        var b = wip.TryEnter(AlphaTester);
        Assert.NotNull(a);
        Assert.NotNull(b);

        wip.SetMax(1);
        Assert.Equal(2, wip.View().Running.Count);
        Assert.Null(wip.TryEnter(BetaWorker));

        a!.Dispose();
        Assert.Null(wip.TryEnter(BetaWorker));

        wip.SetMax(3);
        Assert.NotNull(wip.TryEnter(BetaWorker));
        Assert.Empty(wip.View().Waiting);
    }

    [Fact]
    public void A_manager_uses_one_reserved_slot_and_no_more()
    {
        var wip = new WipLedger(1);
        Assert.NotNull(wip.TryEnter(AlphaWorker));
        Assert.NotNull(wip.TryEnter(AlphaManager));
        Assert.Null(wip.TryEnter(BetaManager));
        Assert.Null(wip.TryEnter(BetaWorker));
    }

    [Fact]
    public async Task Changed_completes_on_release_and_on_set_max()
    {
        var wip = new WipLedger(1);
        var held = wip.TryEnter(AlphaWorker)!;

        var onRelease = wip.Changed;
        Assert.False(onRelease.IsCompleted);
        held.Dispose();
        await onRelease.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var onSetMax = wip.Changed;
        wip.SetMax(4);
        await onSetMax.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    /// <summary>Every run of each of <paramref name="held"/> blocks until its source is set.</summary>
    private static Dictionary<ContainerId, TaskCompletionSource> HoldRunsOf(
        ContainerTestBed bed, params ContainerId[] held)
    {
        var release = held.ToDictionary(
            id => id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        bed.Agent.Behaviour = async invocation =>
        {
            if (release.TryGetValue(invocation.Container, out var gate)) await gate.Task;
            return new AgentResult(0, "done");
        };

        return release;
    }

    /// <summary>Every run of <paramref name="held"/> blocks until the returned source is set, so
    /// it holds its slot for as long as the test needs.</summary>
    private static TaskCompletionSource HoldRunsOf(ContainerTestBed bed, ContainerId held)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        bed.Agent.Behaviour = async invocation =>
        {
            if (invocation.Container == held) await release.Task;
            return new AgentResult(0, "done");
        };

        return release;
    }

    private static Task InstructAsync(ContainerTestBed bed, ContainerId id) =>
        bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(id), """{"instruction":"build it"}""", "console"),
            TestContext.Current.CancellationToken);

    private static bool IsWaiting(WipLedger wip, ContainerId id) =>
        wip.View().Waiting.Any(hold => hold.Team == id.Team && hold.Member == id.Name);
}
