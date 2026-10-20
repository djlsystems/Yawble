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
