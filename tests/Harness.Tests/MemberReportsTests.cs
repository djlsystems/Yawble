using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The one implementation of what a member's report does, called here directly - the way a plugin
/// member's stdout reaches it. The routes over it keep their own tests (HandbackWakesOnceTests,
/// PermitTests, the MCP tool tests); these pin that the effects are the service's.
/// </summary>
public sealed class MemberReportsTests
{
    private static readonly ContainerId Dev = new("alpha", "dev");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A member mid-run, held there until <paramref name="release"/>. With two messages, a first
    /// run is held while both queue behind it in one workflow, so the run under test is a batch of
    /// two - batching by timing alone is not deterministic in a test.
    /// </summary>
    private static async Task<(ContainerTestBed Bed, MemberReports Reports, RunHeartbeat Heartbeat, long Cause)> RunningAsync(
        TaskCompletionSource release, int messages = 1)
    {
        var bed = new ContainerTestBed();
        var opener = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var runs = 0;

        bed.Agent.Behaviour = async _ =>
        {
            var run = Interlocked.Increment(ref runs);

            if (messages > 1 && run == 1)
            {
                await opener.Task;
                return new AgentResult(0, "opened");
            }

            started.TrySetResult();
            await release.Task;
            return new AgentResult(0, "done");
        };

        await bed.AddAsync(Dev);

        var first = await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(Dev), """{"instruction":"one"}""", "console"), Ct);
        var cause = first.Seq;

        if (messages > 1)
        {
            Assert.True(await bed.PumpUntilAsync(() => runs == 1));

            for (var i = 0; i < messages; i++)
            {
                var queued = await bed.Store.AppendAsync(
                    new NewMessage(MessageTypes.InstructionFor(Dev), $$"""{"instruction":"part {{i}}"}""", "console", first.Seq), Ct);
                if (i == 0) cause = queued.Seq;
            }

            await bed.SettleAsync();
            opener.SetResult();
        }

        Assert.True(await bed.PumpUntilAsync(() => started.Task.IsCompleted));

        var heartbeat = new RunHeartbeat();
        return (bed, new MemberReports(bed.Host, bed.Store, heartbeat), heartbeat, cause);
    }

    private static async Task<Message> OnlyAsync(ContainerTestBed bed, string type) =>
        Assert.Single(await bed.OfTypeAsync(type));

    [Fact]
    public async Task Progress_writes_the_row_under_the_run_and_resets_the_idle_clock()
    {
        var release = new TaskCompletionSource();
        var (bed, reports, heartbeat, cause) = await RunningAsync(release);
        await using var _ = bed;

        var touched = 0;
        using var clock = heartbeat.WhileRunning(Dev, () => touched++);

        Assert.True((await reports.ProgressAsync(Dev, "  working  ", Ct)).Accepted);

        var row = await OnlyAsync(bed, MessageTypes.Progress);
        Assert.Equal("""{"status":"working"}""", row.Payload);
        Assert.Equal(cause, row.CausationSeq);
        Assert.Equal(Dev.ToString(), row.Source);
        Assert.Equal(1, touched);
        release.SetResult();
    }

    [Fact]
    public async Task Blocked_marks_the_member_and_needs_decision_and_handback_mark_theirs()
    {
        var release = new TaskCompletionSource();
        var (bed, reports, _, cause) = await RunningAsync(release);
        await using var _ = bed;

        await reports.BlockedAsync(Dev, "no key", ct: Ct);
        await reports.NeedsDecisionAsync(Dev, "which one?", Ct);
        await reports.HandbackAsync(Dev, "shipped", Ct);

        var snapshot = bed.Host.Find(Dev)!.Snapshot();
        Assert.Equal("no key", snapshot.Blocked);
        Assert.Equal("which one?", snapshot.NeedsDecision);
        Assert.Equal("shipped", snapshot.HandedBack);

        Assert.Equal("""{"reason":"no key"}""", (await OnlyAsync(bed, MessageTypes.Blocked)).Payload);
        Assert.Equal("""{"question":"which one?"}""", (await OnlyAsync(bed, MessageTypes.NeedsDecision)).Payload);
        var handback = await OnlyAsync(bed, MessageTypes.Handback);
        Assert.Equal("""{"delivered":"shipped"}""", handback.Payload);
        Assert.Equal(cause, handback.CausationSeq);
        release.SetResult();
    }

    [Fact]
    public async Task Blocking_one_item_closes_that_delivery_and_leaves_the_member_unmarked()
    {
        var release = new TaskCompletionSource();
        var (bed, reports, _, _) = await RunningAsync(release, messages: 2);
        await using var _ = bed;

        Assert.True((await reports.BlockedAsync(Dev, "not this one", item: 2, ct: Ct)).Accepted);
        var refused = await reports.BlockedAsync(Dev, "no such", item: 9, ct: Ct);

        Assert.False(refused.Accepted);
        Assert.Equal(400, refused.Status);
        Assert.Null(bed.Host.Find(Dev)!.Snapshot().Blocked);

        using var payload = JsonDocument.Parse((await OnlyAsync(bed, MessageTypes.Blocked)).Payload);
        Assert.Equal(2, payload.RootElement.GetProperty("item").GetInt32());
        release.SetResult();
    }

    [Fact]
    public async Task A_member_that_is_not_hosted_is_refused()
    {
        await using var bed = new ContainerTestBed();
        var reports = new MemberReports(bed.Host, bed.Store, new RunHeartbeat());

        var outcome = await reports.ProgressAsync(new ContainerId("alpha", "nobody"), "hi", Ct);

        Assert.False(outcome.Accepted);
        Assert.Equal(404, outcome.Status);
    }
}
