using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// <see cref="IUsageLedger"/> on SQL Server: SQLite's <c>SqliteUsageLedger</c> query for query, over
/// the tables of <see cref="SqlServerOutcomeSchema"/>. Reads only: the one writer is
/// <see cref="SqlServerLedgerRows"/>, inside the append. Rows, the export page and the team activity
/// read's edges are decided by <see cref="LedgerFacts"/>, shared with it.
/// </summary>
public sealed class SqlServerUsageLedger(string connectionString) : IUsageLedger
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    private const string RunColumns = LedgerFacts.RunColumns;

    private const string WorkflowColumns = LedgerFacts.WorkflowColumns;

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);

    public async Task<IReadOnlyList<UsageLedgerRow>> ReadRecentRunsAsync(
        ContainerId member, int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        return await RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     SELECT TOP (@max) {RunColumns} FROM usage_ledger
                     WHERE team_id = @team AND member = @member
                     ORDER BY run_seq DESC
                     """;
                command.Parameters.AddWithValue("@team", member.Team);
                command.Parameters.AddWithValue("@member", member.Name);
                command.Parameters.AddWithValue("@max", max);

                return await LedgerFacts.ReadRunsAsync(command, token);
            },
            ct);
    }

    public Task<WorkflowLedgerRow?> ReadWorkflowAsync(long correlation, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     SELECT TOP 1 {WorkflowColumns} FROM workflow_ledger
                     WHERE correlation = @correlation
                     ORDER BY close_seq DESC
                     """;
                command.Parameters.AddWithValue("@correlation", correlation);

                var rows = await LedgerFacts.ReadWorkflowsAsync(command, token);
                return rows.Count == 0 ? null : rows[0];
            },
            ct);

    public Task<LedgerPage> ReadPageAsync(
        DateTimeOffset since, long? before, int take, CancellationToken ct = default)
    {
        take = Math.Max(1, take);
        var stamp = Instant.Stamp(since);
        var cursor = before ?? long.MaxValue;

        return RunAsync(
            async (connection, token) =>
            {
                await using var runsCommand = connection.CreateCommand();
                runsCommand.CommandText =
                    $"""
                     SELECT TOP (@take) {RunColumns} FROM usage_ledger
                     WHERE ended_at >= @since AND run_seq < @before
                     ORDER BY run_seq DESC
                     """;
                Bind(runsCommand, stamp, cursor, take);
                var runs = await LedgerFacts.ReadRunsAsync(runsCommand, token);

                await using var workflowsCommand = connection.CreateCommand();
                workflowsCommand.CommandText =
                    $"""
                     SELECT TOP (@take) {WorkflowColumns} FROM workflow_ledger
                     WHERE closed_at >= @since AND close_seq < @before
                     ORDER BY close_seq DESC
                     """;
                Bind(workflowsCommand, stamp, cursor, take);
                var workflows = await LedgerFacts.ReadWorkflowsAsync(workflowsCommand, token);

                return LedgerFacts.Page(runs, workflows, take);
            },
            ct);

        static void Bind(SqlCommand command, string stamp, long cursor, int take)
        {
            command.Parameters.AddWithValue("@since", stamp);
            command.Parameters.AddWithValue("@before", cursor);
            command.Parameters.AddWithValue("@take", take);
        }
    }

    /// <summary>
    /// SqliteUsageLedger.SumForTeamAsync. SQLite answers the grouped name from whichever row it read
    /// last; here a grouped column must be aggregated, so the lowest spelling of each half is named.
    /// The counts are cast, because a SUM of 1s is an INT here.
    /// </summary>
    public Task<IReadOnlyList<ContainerUsageRow>> SumForTeamAsync(string team, long floorSeq, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<ContainerUsageRow>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT
                        MIN(team_id) + N'/' + MIN(member),
                        CAST(COALESCE(SUM(CASE WHEN tokens_in IS NOT NULL AND tokens_out IS NOT NULL THEN tokens_in END), 0) AS BIGINT),
                        CAST(COALESCE(SUM(CASE WHEN tokens_in IS NOT NULL AND tokens_out IS NOT NULL THEN tokens_out END), 0) AS BIGINT),
                        CAST(SUM(CASE WHEN tokens_in IS NOT NULL AND tokens_out IS NOT NULL THEN 1 ELSE 0 END) AS BIGINT),
                        CAST(SUM(CASE WHEN tokens_in IS NULL OR tokens_out IS NULL THEN 1 ELSE 0 END) AS BIGINT),
                        SUM(tokens_combined),
                        CAST(COALESCE(SUM(CASE WHEN tokens_in IS NOT NULL AND tokens_out IS NOT NULL THEN tokens_cached_in END), 0) AS BIGINT),
                        CAST(COALESCE(SUM(CASE WHEN tokens_in IS NOT NULL AND tokens_out IS NOT NULL THEN tokens_cache_creation END), 0) AS BIGINT),
                        CAST(COALESCE(SUM(billable), 0) AS BIGINT)
                    FROM usage_ledger
                    WHERE team_id = @team AND run_seq > @floor
                    GROUP BY member
                    """;
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@floor", floorSeq);

                var rows = new List<ContainerUsageRow>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(new ContainerUsageRow(
                        reader.GetString(0),
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        checked((int)reader.GetInt64(3)),
                        checked((int)reader.GetInt64(4)),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8)));
                }

                return rows;
            },
            ct);

    /// <summary>SqliteUsageLedger.ReadTeamRunsAsync's one statement; which rows are kept is
    /// <see cref="LedgerFacts.TeamRuns"/>'s.</summary>
    public Task<IReadOnlyList<UsageLedgerRow>> ReadTeamRunsAsync(
        string team, long floor, DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string> members,
        CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     SELECT {RunColumns}, CAST(0 AS BIGINT) AS part FROM usage_ledger
                     WHERE team_id = @team AND run_seq > @floor AND ended_at >= @from
                     UNION ALL
                     SELECT {RunColumns}, CAST(1 AS BIGINT) FROM (
                         SELECT {RunColumns}, ROW_NUMBER() OVER (PARTITION BY member ORDER BY ended_at DESC, run_seq DESC) AS n
                         FROM usage_ledger WHERE team_id = @team AND run_seq > @floor AND ended_at < @from) before_period
                     WHERE n = 1
                     UNION ALL
                     SELECT {RunColumns}, CAST(2 AS BIGINT) FROM (
                         SELECT {RunColumns}, ROW_NUMBER() OVER (PARTITION BY member ORDER BY ended_at, run_seq) AS n
                         FROM usage_ledger WHERE team_id = @team AND run_seq > @floor AND ended_at >= @to) after_period
                     WHERE n = 1
                     """;
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@floor", floor);
                command.Parameters.AddWithValue("@from", Instant.Stamp(from));
                command.Parameters.AddWithValue("@to", Instant.Stamp(to));

                var read = new List<(UsageLedgerRow, long)>();
                await using (var reader = await command.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token)) read.Add((LedgerFacts.ReadRun(reader), reader.GetInt64(21)));
                }

                return LedgerFacts.TeamRuns(read, to, members);
            },
            ct);

    public Task<IReadOnlyList<UsageLedgerRow>> ReadTeamRunsEndedAsync(
        string team, long floor, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                var period = from is not null && to is not null ? "AND ended_at >= @from AND ended_at < @to" : "";
                command.CommandText =
                    $"""
                     SELECT {RunColumns} FROM usage_ledger
                     WHERE team_id = @team AND run_seq > @floor {period}
                     ORDER BY ended_at, run_seq
                     """;
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@floor", floor);

                if (from is { } start && to is { } end)
                {
                    command.Parameters.AddWithValue("@from", Instant.Stamp(start));
                    command.Parameters.AddWithValue("@to", Instant.Stamp(end));
                }

                return await LedgerFacts.ReadRunsAsync(command, token);
            },
            ct);
}
