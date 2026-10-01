using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Tests;

/// <summary>
/// A Manager whose own run a restart cut off is told, once, to carry on: in the same start pass
/// that fails the run, as the platform, under the failed row. Not when it already holds a delivery
/// in that workflow, and never twice for one failed row.
/// </summary>
public sealed class RestartWakeTests
{
    private static readonly ContainerId Manager = new("Alpha", "Manager");
    private static readonly ContainerId Dev = new("Alpha", "DeveloperTobias");
    private static readonly string[] ManagerTypes = [MessageTypes.Completed, MessageTypes.Failed, MessageTypes.Handback];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_Manager_cut_off_by_a_restart_with_nothing_queued_is_told_once_and_runs()
    {
        await using var bed = new ContainerTestBed(pending: true);
        await bed.AddAsync(Manager, ManagerTypes);

        var root = await bed.Store.AppendAsync(ToManager("plan the work"), Ct);
        await bed.Pending!.AddAsync(Manager, root.Seq, Ct);
        await bed.Pending.StartAsync(Manager, root.Seq, Ct);

        var report = await bed.Host.ResumePendingAsync(Ct);

        Assert.Equal(1, report.Interrupted);
        Assert.Equal(1, report.ManagersWoken);

        var failed = Assert.Single(await bed.OfTypeAsync(MessageTypes.Failed));
        var wake = Assert.Single(await WakesAsync(bed));
        Assert.Equal(ContainerHost.RestartWakeSource, wake.Source);
        Assert.Equal(failed.Seq, wake.CausationSeq);
        Assert.Equal(root.CorrelationId, wake.CorrelationId);
        Assert.Contains("cut off by a host restart", wake.Payload);
        Assert.Equal(failed.Seq, JsonDocument.Parse(wake.Payload).RootElement.GetProperty(ContainerHost.RestartWakeOfField).GetInt64());

        Assert.True(await bed.PumpUntilAsync(() => bed.Agent.RunsFor(Manager) == 1));
        await bed.SettleAsync();
        Assert.Equal(1, bed.Agent.RunsFor(Manager));
        Assert.Single(await WakesAsync(bed));
    }

    [Fact]
    public async Task A_Manager_with_a_queued_delivery_in_that_workflow_is_not_told()
    {
        await using var bed = new ContainerTestBed(pending: true);
        await bed.AddAsync(Manager, ManagerTypes);

        var root = await bed.Store.AppendAsync(ToManager("plan the work"), Ct);
        var next = await bed.Store.AppendAsync(ToManager("and then this", root.Seq), Ct);
        await bed.Pending!.AddAsync(Manager, root.Seq, Ct);
        await bed.Pending.StartAsync(Manager, root.Seq, Ct);
        await bed.Pending.AddAsync(Manager, next.Seq, Ct);

        var report = await bed.Host.ResumePendingAsync(Ct);

        Assert.Equal(1, report.Interrupted);
        Assert.Equal(1, report.Reoffered);
        Assert.Equal(0, report.ManagersWoken);
        Assert.Empty(await WakesAsync(bed));
    }

    [Fact]
    public async Task A_second_restart_does_not_repeat_the_instruction_for_the_same_failed_row()
    {
        await using var bed = new ContainerTestBed(pending: true);
        await bed.AddAsync(Manager, ManagerTypes);

        var root = await bed.Store.AppendAsync(ToManager("plan the work"), Ct);
        await bed.Pending!.AddAsync(Manager, root.Seq, Ct);
        await bed.Pending.StartAsync(Manager, root.Seq, Ct);

        Assert.Equal(1, (await bed.Host.ResumePendingAsync(Ct)).ManagersWoken);

        // The Host goes down again before the wake was delivered, and once more after.
        Assert.Equal(0, (await bed.Host.ResumePendingAsync(Ct)).ManagersWoken);
        Assert.Equal(0, (await bed.Host.ResumePendingAsync(Ct)).ManagersWoken);

        Assert.Single(await WakesAsync(bed));
    }

    [Fact]
    public async Task A_members_run_cut_off_by_a_restart_does_not_get_a_restart_wake()
    {
        await using var bed = new ContainerTestBed(pending: true);
        await bed.AddAsync(Manager, ManagerTypes);
        await bed.AddAsync(Dev);

        var root = await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = "build it" }), Manager.ToString(), null),
            Ct);
        await bed.Pending!.AddAsync(Dev, root.Seq, Ct);
        await bed.Pending.StartAsync(Dev, root.Seq, Ct);

        var report = await bed.Host.ResumePendingAsync(Ct);

        Assert.Equal(1, report.Interrupted);
        Assert.Equal(0, report.ManagersWoken);
        Assert.Empty(await WakesAsync(bed));
    }

    private static NewMessage ToManager(string text, long? causation = null) =>
        new(MessageTypes.InstructionFor(Manager), JsonSerializer.Serialize(new { instruction = text }), "console", causation);

    private static async Task<IReadOnlyList<Message>> WakesAsync(ContainerTestBed bed) =>
        (await bed.Store.ReadAfterAsync(0, [MessageTypes.InstructionFor(Manager)], int.MaxValue))
            .Where(m => m.Type == MessageTypes.InstructionFor(Manager) && m.Source == ContainerHost.RestartWakeSource)
            .ToList();
}
