using System.Text.Json;
using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// A MANAGER IS WOKEN ONCE PER FINISHED RUN.
///
/// A Manager subscribes to both `handback` and `completed`. A worker that hands back publishes the
/// first from inside its run and the platform publishes the second when the run ends. Woken twice
/// for one delivery, the second wake would find its own declaration refused because the first
/// wake's completion row is still pending. The `completed` row says whether the run had handed
/// back, and the pump does not wake a subscriber on it when that subscriber was already woken by
/// the hand-back.
/// </summary>
public sealed class HandbackWakesOnceTests
{
    private static readonly string[] ManagerTypes = [MessageTypes.Completed, MessageTypes.Handback];

    [Fact]
    public async Task A_worker_that_hands_back_wakes_its_manager_once()
    {
        await using var bed = new ContainerTestBed();

        var manager = new ContainerId("Alpha", "Manager");
        var workerId = new ContainerId("Alpha", "DeveloperRowan");
        await bed.AddAsync(manager, ManagerTypes);
        var worker = await bed.AddAsync(workerId);

        // The worker hands back and then keeps running until the test lets it finish, which is
        // the real shape: a manager's wake on the hand-back starts and ends while the worker is
        // still busy, and the worker's `completed` lands after that.
        var workerMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        bed.Agent.Behaviour = async invocation =>
        {
            if (invocation.Container == workerId)
            {
                await bed.Store.AppendAsync(new NewMessage(
                    MessageTypes.Handback, """{"delivered":"hello world"}""",
                    workerId.ToString(), worker.CurrentCausation), TestContext.Current.CancellationToken);
                worker.MarkHandedBack("hello world");
                await workerMayFinish.Task;
            }

            return new AgentResult(0, "done");
        };

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(workerId), """{"instruction":"build it"}""", "console"), TestContext.Current.CancellationToken);

        // Woken by the hand-back, while the worker is still running.
        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(manager) == 1));
        Assert.Equal(ContainerState.Running, worker.State);

        workerMayFinish.SetResult();

        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.OfTypeAsync(MessageTypes.Completed)).Any(m => m.Source == workerId.ToString())));
        await bed.SettleAsync();

        var completed = Assert.Single(await bed.OfTypeAsync(MessageTypes.Completed),
            m => m.Source == workerId.ToString());
        using var payload = JsonDocument.Parse(completed.Payload);
        Assert.True(payload.RootElement.GetProperty(PayloadFields.HandedBack).GetBoolean());

        Assert.Equal(1, bed.Agent.RunsFor(manager));
    }

    [Fact]
    public async Task A_worker_that_does_not_hand_back_still_wakes_its_manager_on_completion()
    {
        await using var bed = new ContainerTestBed();

        var manager = new ContainerId("Alpha", "Manager");
        var workerId = new ContainerId("Alpha", "DeveloperRowan");
        await bed.AddAsync(manager, ManagerTypes);
        await bed.AddAsync(workerId);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(workerId), """{"instruction":"build it"}""", "console"), TestContext.Current.CancellationToken);

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(manager) > 0));
        await bed.SettleAsync();

        var completed = Assert.Single(await bed.OfTypeAsync(MessageTypes.Completed),
            m => m.Source == workerId.ToString());
        using var payload = JsonDocument.Parse(completed.Payload);
        Assert.False(payload.RootElement.GetProperty(PayloadFields.HandedBack).GetBoolean());

        Assert.Equal(1, bed.Agent.RunsFor(manager));
    }

    [Fact]
    public async Task A_subscriber_holding_only_completed_still_wakes_on_a_handed_back_run()
    {
        await using var bed = new ContainerTestBed();

        var observer = new ContainerId("Alpha", "Observer");
        var workerId = new ContainerId("Alpha", "DeveloperRowan");
        await bed.AddAsync(observer, [MessageTypes.Completed]);
        var worker = await bed.AddAsync(workerId);

        bed.Agent.Behaviour = async invocation =>
        {
            if (invocation.Container == workerId)
            {
                await bed.Store.AppendAsync(new NewMessage(
                    MessageTypes.Handback, """{"delivered":"hello world"}""",
                    workerId.ToString(), worker.CurrentCausation), TestContext.Current.CancellationToken);
                worker.MarkHandedBack("hello world");
            }

            return new AgentResult(0, "done");
        };

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(workerId), """{"instruction":"build it"}""", "console"), TestContext.Current.CancellationToken);

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(observer) > 0));
        await bed.SettleAsync();

        Assert.Equal(1, bed.Agent.RunsFor(observer));
    }

    /// <summary>
    /// TWO TEAMS' MANAGERS NEVER WAKE EACH OTHER. Completion types are global, so without the
    /// own-team check in the pump Alpha's `completed` wakes Beta's Manager, whose own `completed`
    /// wakes Alpha's, without end.
    /// </summary>
    [Fact]
    public async Task Two_teams_managers_never_wake_each_other()
    {
        await using var bed = new ContainerTestBed();

        var alphaManager = new ContainerId("Alpha", "Manager");
        var betaManager = new ContainerId("Beta", "Manager");
        var alphaWorker = new ContainerId("Alpha", "DeveloperRowan");
        await bed.AddAsync(alphaManager, ManagerTypes);
        await bed.AddAsync(betaManager, ManagerTypes);
        await bed.AddAsync(alphaWorker);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(alphaWorker), """{"instruction":"build it"}""", "console"), TestContext.Current.CancellationToken);

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(alphaManager) > 0));
        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.OfTypeAsync(MessageTypes.Completed)).Any(m => m.Source == alphaManager.ToString())));
        await bed.SettleAsync();

        Assert.Equal(1, bed.Agent.RunsFor(alphaManager));
        Assert.Equal(0, bed.Agent.RunsFor(betaManager));
    }

    /// <summary>A container never reacts to its own row: a Manager subscribed to `completed` is
    /// not woken by the `completed` its own run published.</summary>
    [Fact]
    public async Task A_container_is_not_woken_by_its_own_row()
    {
        await using var bed = new ContainerTestBed();

        var manager = new ContainerId("Alpha", "Manager");
        await bed.AddAsync(manager, ManagerTypes);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(manager), """{"instruction":"plan it"}""", "console"), TestContext.Current.CancellationToken);

        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.OfTypeAsync(MessageTypes.Completed)).Any(m => m.Source == manager.ToString())));
        await bed.SettleAsync();

        Assert.Equal(1, bed.Agent.RunsFor(manager));
    }
}
