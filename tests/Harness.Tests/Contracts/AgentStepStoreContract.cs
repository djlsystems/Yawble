using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IAgentStepStore"/>, as each provider must keep it. From the existing SQLite tests, same
/// names and values: <c>FreshVolumeTests</c>' step round trip and
/// <c>AgentStepReplayTests.A_log_purge_removes_the_steps_older_than_the_oldest_log_row_kept_and_keeps_the_rest</c>
/// (the log's own delete pruning these rows, which no SQL Server test reached before). The rest are the
/// interface's remarks (the scope by each team's creation, paging, the counted steps, the removals), the
/// case and trailing-space map of a team and a member, and long text.
/// </summary>
public abstract class AgentStepStoreContract : HostStoreContract
{
    protected async Task<IAgentStepStore> CreateAsync() => (await DatabaseAsync()).AgentSteps();

    private static readonly DateTimeOffset At = new(2026, 10, 16, 20, 0, 1, TimeSpan.Zero);

    private static Task Write(IAgentStepStore store, string team, string member, DateTimeOffset at, string label) =>
        store.WriteAsync(team, member, 1, 1, [new NewAgentStep(at, "tool", label, null)], [], Ct);

    private static async Task<List<string>> LabelsAsync(IAgentStepStore store, Dictionary<string, DateTimeOffset> teams) =>
        [.. (await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 500, teams, Ct)).Select(s => s.Label)];

    [Fact]
    public async Task The_steps_round_trip_with_their_run_their_workflow_and_their_count()
    {
        var database = await DatabaseAsync();
        var log = database.Log();
        var delivery = await log.AppendAsync(new NewMessage("test.instruction", "{}", "test"), Ct);
        var terminal = await log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", "Alpha/Dev", delivery.Seq), Ct);

        var store = database.AgentSteps();
        var tool = new NewAgentStep(At, LiveLineKinds.Tool, "MCP", "tell") { Count = 3 };
        await store.WriteAsync("Alpha", "Dev", delivery.Seq, 9, [tool, new NewAgentStep(At, LiveLineKinds.Text, "", null)], [], Ct);
        await store.WriteAsync("Alpha", "Ops", 12345, null, [new NewAgentStep(At, LiveLineKinds.Result, "", null)], [], Ct);

        var teams = new Dictionary<string, DateTimeOffset> { ["Alpha"] = DateTimeOffset.MinValue };
        var rows = await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 10, teams, Ct);
        Assert.Equal(3, rows.Count);
        Assert.Equal(("Alpha", "Dev", terminal.Seq, 9L, At, "tool", "MCP", "tell", 3),
            (rows[0].Team, rows[0].Member, rows[0].Run, rows[0].Workflow!.Value, rows[0].At, rows[0].Kind, rows[0].Label, rows[0].McpTool, rows[0].Count));
        Assert.Equal(("text", "", null, 1), (rows[1].Kind, rows[1].Label, rows[1].McpTool, rows[1].Count));

        // A run with no terminal row yet is its delivery.
        Assert.Equal((12345L, (long?)null), (rows[2].Run, rows[2].Workflow));

        Assert.Equal(1, await store.DeleteForMembersAsync("Alpha", ["Ops"], Ct));
        Assert.Equal(2, await store.DeleteForTeamAsync("Alpha", Ct));
    }

    /// <summary>The log's one delete prunes these rows in its own transaction: every one older than the
    /// oldest log row kept. The report counts them.</summary>
    [Fact]
    public async Task A_log_purge_removes_the_steps_older_than_the_oldest_log_row_kept_and_keeps_the_rest()
    {
        var database = await DatabaseAsync();
        var log = database.Log();
        var store = database.AgentSteps();

        var first = await log.AppendAsync(new NewMessage("test.run-steps", "{}", "test"), Ct);
        await Task.Delay(20, Ct);
        var second = await log.AppendAsync(new NewMessage("test.run-steps", "{}", "test"), Ct);
        Assert.True(second.OccurredAt > first.OccurredAt);

        await Write(store, "Alpha", "Dev", first.OccurredAt.AddMinutes(-1), "before");
        await Write(store, "Alpha", "Dev", first.OccurredAt.AddTicks(1), "between");
        await Write(store, "Alpha", "Dev", second.OccurredAt, "at");
        await Write(store, "Beta", "Ops", second.OccurredAt.AddMinutes(1), "after");

        var report = await log.DeleteAsync([first.Seq], Ct);

        Assert.Equal(1, report.Purged);
        Assert.Equal(2, report.RunSteps);
        Assert.Equal(["at", "after"], await LabelsAsync(store, Everyone("Alpha", "Beta")));
    }

    /// <summary>A write sets the count of the steps that already have a row (folded over the cap) and
    /// skips one that has none yet.</summary>
    [Fact]
    public async Task A_counted_step_takes_its_new_count_and_one_with_no_row_is_left()
    {
        var store = await CreateAsync();
        var step = new NewAgentStep(At, "tool", "Grep", null);
        await store.WriteAsync("Alpha", "Dev", 1, null, [step], [], Ct);
        Assert.True(step.Id > 0);

        step.Count = 7;
        var unwritten = new NewAgentStep(At, "tool", "Read", null) { Count = 4 };
        await store.WriteAsync("Alpha", "Dev", 1, null, [], [step, unwritten], Ct);

        var row = Assert.Single(await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 10, Everyone("Alpha"), Ct));
        Assert.Equal((step.Id, 7), (row.Id, row.Count));
        Assert.Equal(0, unwritten.Id);

        // Nothing to write writes nothing.
        await store.WriteAsync("Alpha", "Dev", 1, null, [], [], Ct);
    }

    /// <summary>Only the teams named, each from its creation; after the cursor; within the window; at
    /// most <c>take</c>, oldest first. An empty map, or a take of nothing, reads none.</summary>
    [Fact]
    public async Task A_read_is_scoped_by_each_teams_creation_and_paged_oldest_first()
    {
        var store = await CreateAsync();
        for (var i = 0; i < 5; i++) await Write(store, "Alpha", "Dev", At.AddMinutes(i), $"a{i}");
        await Write(store, "Beta", "Dev", At.AddMinutes(2), "b");
        await Write(store, "Gamma", "Dev", At, "g");

        var since = new Dictionary<string, DateTimeOffset> { ["Alpha"] = At.AddMinutes(1), ["Beta"] = DateTimeOffset.MinValue };
        var all = await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 100, since, Ct);
        Assert.Equal(["a1", "a2", "a3", "a4", "b"], all.Select(s => s.Label));

        var page = await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, all[1].Id, 2, since, Ct);
        Assert.Equal(["a3", "a4"], page.Select(s => s.Label));

        var window = await store.ReadAsync(At.AddMinutes(2), At.AddMinutes(3), 0, 100, since, Ct);
        Assert.Equal(["a2", "a3", "b"], window.Select(s => s.Label));

        Assert.Empty(await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 100, new Dictionary<string, DateTimeOffset>(), Ct));
        Assert.Empty(await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 0, since, Ct));
    }

    [Fact]
    public async Task Deleting_members_removes_only_their_steps_and_counts_them()
    {
        var store = await CreateAsync();
        await Write(store, "Alpha", "Dev", At, "dev");
        await Write(store, "Alpha", "Ops", At, "ops");
        await Write(store, "Alpha", "Lead", At, "lead");
        await Write(store, "Beta", "Dev", At, "beta");

        Assert.Equal(0, await store.DeleteForMembersAsync("Alpha", [], Ct));
        Assert.Equal(2, await store.DeleteForMembersAsync("Alpha", ["Dev", "Ops", "Nobody"], Ct));

        Assert.Equal(["lead", "beta"], await LabelsAsync(store, Everyone("Alpha", "Beta")));
    }

    /// <summary>THE CASE MAP: a team and a member fold case, as SQLite's NOCASE columns do.</summary>
    [Fact]
    public async Task A_team_or_member_differing_only_in_case_is_the_same()
    {
        var store = await CreateAsync();
        await Write(store, "Alpha", "Dev", At, "one");

        Assert.Equal(["one"], await LabelsAsync(store, new Dictionary<string, DateTimeOffset> { ["ALPHA"] = DateTimeOffset.MinValue }));
        Assert.Equal(1, await store.DeleteForMembersAsync("alpha", ["DEV"], Ct));

        await Write(store, "Alpha", "Dev", At, "two");
        Assert.Equal(1, await store.DeleteForTeamAsync("aLPHA", Ct));
    }

    /// <summary>THE TRAILING-SPACE MAP: a team or a member with a trailing space is another, as on SQLite.</summary>
    [Fact]
    public async Task A_team_or_member_with_a_trailing_space_is_another()
    {
        var store = await CreateAsync();
        await Write(store, "Alpha", "Dev", At, "one");

        Assert.Empty(await LabelsAsync(store, new Dictionary<string, DateTimeOffset> { ["Alpha "] = DateTimeOffset.MinValue }));
        Assert.Equal(0, await store.DeleteForMembersAsync("Alpha", ["Dev "], Ct));
        Assert.Equal(0, await store.DeleteForMembersAsync("Alpha ", ["Dev"], Ct));
        Assert.Equal(0, await store.DeleteForTeamAsync("Alpha ", Ct));
        Assert.Equal(["one"], await LabelsAsync(store, Everyone("Alpha")));
    }

    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var store = await CreateAsync();
        var label = LongUnicode("label");
        var tool = LongUnicode("tool");
        await store.WriteAsync("Alpha", "Dev", 1, null, [new NewAgentStep(At, "tool", label, tool)], [], Ct);

        var row = Assert.Single(await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 10, Everyone("Alpha"), Ct));
        Assert.Equal((label, tool), (row.Label, row.McpTool));
    }
}

public sealed class SqliteAgentStepStoreTests : AgentStepStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerAgentStepStoreTests : AgentStepStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
