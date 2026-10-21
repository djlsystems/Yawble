using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The rollup's timing reads are kept per team until that team's rows move: a push from one team
/// re-reads that team alone, a purge re-reads every team, and a kept answer is the fresh answer
/// but for the clock stamp.
/// </summary>
public sealed class RollupCacheTests
{
    private static readonly ContainerId Alpha = new("alpha", "Dev");
    private static readonly ContainerId Beta = new("beta", "Dev");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Message> RunAsync(ContainerTestBed bed, ContainerId member, string text, bool finish = true)
    {
        var delivery = await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(member), $$"""{"instruction":"{{text}}"}""", "console"), Ct);
        await bed.Store.AppendAsync(new NewMessage(MessageTypes.Started, "{}", member.ToString(), delivery.Seq), Ct);
        if (finish)
        {
            await bed.Store.AppendAsync(
                new NewMessage(MessageTypes.Completed, """{"tokensIn":10,"tokensOut":5,"output":"done"}""", member.ToString(), delivery.Seq), Ct);
        }

        return delivery;
    }

    private static int Reads(IMessageLog log) =>
        CountingProxy.Of(log).CallsTo(nameof(IMessageLog.ElapsedForTeamAsync))
        + CountingProxy.Of(log).CallsTo(nameof(IMessageLog.WorkflowsForTeamAsync));

    [Fact]
    public async Task A_second_read_with_nothing_new_reads_only_the_teams_highest_seq_and_refreshes_the_clock()
    {
        await using var bed = new ContainerTestBed();
        await RunAsync(bed, Alpha, "one");
        var log = CountingProxy.Over<IMessageLog>(bed.Store);
        var cache = new RollupCache(log);

        var first = await cache.ReadAsync("alpha", 0, Ct);
        await Task.Delay(10, Ct);
        var second = await cache.ReadAsync("alpha", 0, Ct);

        Assert.Equal(2, Reads(log));
        Assert.Equal(2, CountingProxy.Of(log).CallsTo(nameof(IMessageLog.TeamHighestSeqAsync)));
        Assert.True(second.Elapsed.ServerNow > first.Elapsed.ServerNow);
        Assert.Equal(first.Elapsed with { ServerNow = default }, second.Elapsed with { ServerNow = default });
        Assert.Equal(first.Open.TotalCount, second.Open.TotalCount);
        Assert.All(second.Open.Workflows, w => Assert.Equal(second.Open.ServerNow, w.ServerNow));
    }

    [Fact]
    public async Task A_row_of_one_team_re_reads_that_team_only()
    {
        await using var bed = new ContainerTestBed();
        await RunAsync(bed, Alpha, "one");
        await RunAsync(bed, Beta, "one");
        var log = CountingProxy.Over<IMessageLog>(bed.Store);
        var cache = new RollupCache(log);
        await cache.ReadAsync("alpha", 0, Ct);
        await cache.ReadAsync("beta", 0, Ct);
        Assert.Equal(4, Reads(log));

        await RunAsync(bed, Beta, "two");
        var alpha = await cache.ReadAsync("alpha", 0, Ct);
        var beta = await cache.ReadAsync("beta", 0, Ct);

        Assert.Equal(6, Reads(log));
        Assert.Equal(1, alpha.Open.TotalCount);
        Assert.Equal(2, beta.Open.TotalCount);
    }

    [Fact]
    public async Task A_run_in_flight_is_answered_the_same_from_the_cache_but_for_the_clock()
    {
        await using var bed = new ContainerTestBed();
        await RunAsync(bed, Alpha, "going", finish: false);
        var cache = new RollupCache(bed.Store);

        var first = await cache.ReadAsync("alpha", 0, Ct);
        var second = await cache.ReadAsync("alpha", 0, Ct);
        var fresh = await bed.Store.ElapsedForTeamAsync("alpha", 0, Ct);

        Assert.True(first.Elapsed.RunsUnfinished > 0);
        // Serialised: the record's list members compare by reference.
        Assert.Equal(
            JsonSerializer.Serialize(fresh with { ServerNow = default }),
            JsonSerializer.Serialize(second.Elapsed with { ServerNow = default }));
    }

    [Fact]
    public async Task A_persons_close_of_a_workflow_re_reads_the_team_although_no_member_wrote_a_row()
    {
        await using var bed = new ContainerTestBed();
        var root = await RunAsync(bed, Alpha, "one", finish: false);
        var log = CountingProxy.Over<IMessageLog>(bed.Store);
        var cache = new RollupCache(log);
        var open = await cache.ReadAsync("alpha", 0, Ct);
        Assert.Equal(1, open.Open.OpenCount);

        // A PERSON'S CLOSE: its source is the person, so the generated team column is null for it and
        // the team's highest member-sourced seq does not move. The row still ends the workflow.
        await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.WorkflowClosed, """{"team":"alpha","reason":"done"}""", "person-1", root.Seq), Ct);

        var after = await cache.ReadAsync("alpha", 0, Ct);
        Assert.Equal(4, Reads(log));
        Assert.Equal(0, after.Open.OpenCount);
        Assert.Equal(1, after.Open.TotalCount);
    }

    [Fact]
    public async Task A_floor_that_rose_and_an_invalidate_each_re_read()
    {
        await using var bed = new ContainerTestBed();
        var early = await RunAsync(bed, Alpha, "one");
        await RunAsync(bed, Alpha, "two");
        var log = CountingProxy.Over<IMessageLog>(bed.Store);
        var cache = new RollupCache(log);
        await cache.ReadAsync("alpha", 0, Ct);

        var raised = await cache.ReadAsync("alpha", early.Seq + 2, Ct);
        Assert.Equal(4, Reads(log));
        Assert.Equal(1, raised.Open.TotalCount);

        await cache.ReadAsync("alpha", early.Seq + 2, Ct);
        Assert.Equal(4, Reads(log));

        cache.Invalidate();
        await cache.ReadAsync("alpha", early.Seq + 2, Ct);
        Assert.Equal(6, Reads(log));
    }
}
