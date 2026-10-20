using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// `messages-006`: a generated `team` column (the source up to its first `/`) with an index on it,
/// so every team-scoped read walks the index instead of the whole log, and a team id holding `_`
/// or `%` still matches only itself.
/// </summary>
public sealed class MessageTeamIndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_team_filtered_latest_window_walks_the_team_index()
    {
        await using var bed = new ContainerTestBed();
        await RunAsync(bed, new ContainerId("alpha", "Dev"), "go");

        await using var connection = new SqliteConnection(SqliteOpen.ConnectionString(bed.DatabasePath));
        await connection.OpenAsync(Ct);
        await using var plan = connection.CreateCommand();
        plan.CommandText =
            """
            EXPLAIN QUERY PLAN
            SELECT seq FROM messages
            WHERE seq > 0 AND team IN ('alpha') AND (type IN ('agentContainer.completed') OR type LIKE 'agentContainer.instruction.%')
            ORDER BY seq DESC
            LIMIT 200
            """;

        var details = new List<string>();
        await using var reader = await plan.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) details.Add(reader.GetString(3));

        Assert.Contains(details, detail => detail.Contains("ix_messages_team_seq", StringComparison.Ordinal));
        Assert.DoesNotContain(details, detail => detail.StartsWith("SCAN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_latest_window_narrowed_to_a_team_holds_that_teams_rows_however_many_another_wrote_since()
    {
        await using var bed = new ContainerTestBed();
        var mine = await bed.Store.AppendAsync(new NewMessage(MessageTypes.Completed, """{"output":"done"}""", "alpha/Dev"), Ct);
        for (var i = 0; i < 30; i++)
        {
            await bed.Store.AppendAsync(new NewMessage(MessageTypes.Completed, """{"output":"noise"}""", "beta/Dev"), Ct);
        }

        var rows = await bed.Store.ReadLatestAsync(0, [MessageTypes.Completed], ["ALPHA"], 10, Ct);

        Assert.Equal([mine.Seq], rows.Select(r => r.Seq));
    }

    [Fact]
    public async Task A_members_latest_terminal_is_one_row_read_above_its_floor()
    {
        await using var bed = new ContainerTestBed();
        var terminals = new[] { MessageTypes.Completed, MessageTypes.Blocked, MessageTypes.Failed, MessageTypes.NeedsDecision };
        await bed.Store.AppendAsync(new NewMessage(MessageTypes.Completed, """{"output":"first"}""", "alpha/Dev"), Ct);
        await bed.Store.AppendAsync(new NewMessage(MessageTypes.Progress, """{"status":"working"}""", "alpha/Dev"), Ct);
        var blocked = await bed.Store.AppendAsync(new NewMessage(MessageTypes.Blocked, """{"reason":"stuck"}""", "alpha/Dev"), Ct);
        await bed.Store.AppendAsync(new NewMessage(MessageTypes.Completed, """{"output":"other"}""", "alpha/Ops"), Ct);
        await bed.Store.AppendAsync(new NewMessage(MessageTypes.Progress, """{"status":"later"}""", "alpha/Dev"), Ct);

        Assert.Equal(blocked.Seq, (await bed.Store.LatestPublishedAsync("ALPHA/dev", terminals, 0, Ct))?.Seq);
        Assert.Null(await bed.Store.LatestPublishedAsync("alpha/Dev", terminals, blocked.Seq, Ct));
    }

    private static async Task RunAsync(ContainerTestBed bed, ContainerId member, string text)
    {
        var delivery = await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(member), $$"""{"instruction":"{{text}}"}""", "console"), Ct);
        await bed.Store.AppendAsync(new NewMessage(MessageTypes.Started, "{}", member.ToString(), delivery.Seq), Ct);
        await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.Completed, """{"tokensIn":10,"tokensOut":5,"output":"done"}""", member.ToString(), delivery.Seq), Ct);
    }

    [Fact]
    public async Task A_team_scoped_read_walks_the_team_index()
    {
        await using var bed = new ContainerTestBed();
        await RunAsync(bed, new ContainerId("alpha", "Dev"), "go");

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = bed.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync(Ct);
        await using var plan = connection.CreateCommand();
        plan.CommandText =
            """
            EXPLAIN QUERY PLAN
            SELECT seq FROM messages
            WHERE team = 'alpha' AND source NOT LIKE 'alpha/%/%' ESCAPE '\' AND seq > 0
            ORDER BY seq
            """;

        var details = new List<string>();
        await using var reader = await plan.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) details.Add(reader.GetString(3));

        Assert.Contains(details, detail => detail.Contains("ix_messages_team_seq", StringComparison.Ordinal));
        Assert.DoesNotContain(details, detail => detail.StartsWith("SCAN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_team_id_with_an_underscore_matches_only_itself()
    {
        await using var bed = new ContainerTestBed();
        // `_` is the one LIKE wildcard a team id may hold (`%` is not a legal name): `a_b` must
        // never match `axb`.
        foreach (var team in new[] { "a_b", "axb", "aab" })
        {
            await RunAsync(bed, new ContainerId(team, "Dev"), $"work for {team}");
        }

        foreach (var team in new[] { "a_b", "axb" })
        {
            var usage = await bed.Store.SumUsageForTeamAsync(team, 0, Ct);
            Assert.Equal([$"{team}/Dev"], usage.Select(row => row.Source));

            var workflows = await bed.Store.WorkflowsForTeamAsync(team, 0, Ct);
            var expected = (await bed.Store.ReadRangeAsync(0, int.MaxValue, Ct))
                .Where(row => string.Equals(MessageTeam.Of(row), team, StringComparison.OrdinalIgnoreCase))
                .Select(row => row.CorrelationId)
                .Distinct()
                .ToList();
            Assert.Equal(expected.Count, workflows.TotalCount);
        }
    }

    [Fact]
    public async Task A_team_is_matched_whatever_its_case()
    {
        await using var bed = new ContainerTestBed();
        await RunAsync(bed, new ContainerId("Alpha", "Dev"), "go");

        var usage = await bed.Store.SumUsageForTeamAsync("alpha", 0, Ct);

        Assert.Equal(["Alpha/Dev"], usage.Select(row => row.Source));
    }
}
