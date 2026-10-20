using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// The activity read's runs: every run of the team that overlaps the period, and for each member
/// named or seen in it the one run either side, nothing older or later, nothing of another team and
/// nothing of a member neither named nor seen.
/// </summary>
public sealed class TeamRunsReadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-team-runs-{Guid.NewGuid():N}");
    private readonly string _database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TeamRunsReadTests()
    {
        Directory.CreateDirectory(_directory);
        _database = Path.Combine(_directory, "messages.db");
        new SchemaMigrator(_database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_runs_overlapping_the_period_and_each_named_or_seen_members_run_either_side_and_nothing_else()
    {
        await RunAsync(1, "alpha", "Dev", T(45), T(50));    // at the floor: not read
        await RunAsync(2, "alpha", "Dev", T(10), T(12));    // the run before
        await RunAsync(3, "alpha", "Dev", T(45), T(50));    // inside
        await RunAsync(4, "alpha", "Dev", T(70), T(75));    // later than the run after: not read
        await RunAsync(5, "alpha", "Dev", T(90), T(95));    // later still: not read
        await RunAsync(6, "alpha", "Ops", T(10), T(20));    // Ops is named and has no run inside: its run before is 7
        await RunAsync(7, "alpha", "Ops", T(20), T(30));
        await RunAsync(8, "alpha", "Gone", T(10), T(20));   // neither named nor seen: not read
        await RunAsync(9, "alpha", "Dev", T(65), T(66));    // began after the period: not inside, but Dev's run after
        await RunAsync(10, "beta", "Dev", T(45), T(50));    // another team
        await RunAsync(11, "alpha", "Dev", T(5), T(6));     // older than the run before: not read

        var rows = await new SqliteUsageLedger(_database).ReadTeamRunsAsync("alpha", floor: 1, T(40), T(60), ["Dev", "ops"], Ct);

        Assert.Equal([2, 3, 7, 9], rows.Select(r => r.RunSeq));
    }

    private static DateTimeOffset T(int minutes) => new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private async Task RunAsync(long seq, string team, string member, DateTimeOffset queued, DateTimeOffset ended)
    {
        await using var connection = new SqliteConnection(SqliteOpen.ConnectionString(_database));
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, run_outcome,
                queued_at, started_at, ended_at, measured, tokens_in, tokens_out, billable)
            VALUES ($seq, $seq, $team, $team, $member, 'completed', $queued, $queued, $ended, 1, 1, 1, 1)
            """;
        command.Parameters.AddWithValue("$seq", seq);
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$member", member);
        command.Parameters.AddWithValue("$queued", Instant.Stamp(queued));
        command.Parameters.AddWithValue("$ended", Instant.Stamp(ended));
        await command.ExecuteNonQueryAsync(Ct);
    }
}
