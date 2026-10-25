using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Messaging;

/// <summary>
/// <see cref="IUsageLedger"/> over <c>usage_ledger</c> and <c>workflow_ledger</c> in the log's own
/// database file. Reads only: the one writer is <see cref="LedgerRows"/>, inside the append.
/// </summary>
public sealed class SqliteUsageLedger(string databasePath) : IUsageLedger
{
    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

    private const string RunColumns = LedgerFacts.RunColumns;

    private const string WorkflowColumns = LedgerFacts.WorkflowColumns;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }

    public async Task<IReadOnlyList<UsageLedgerRow>> ReadRecentRunsAsync(
        ContainerId member, int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {RunColumns} FROM usage_ledger
             WHERE team_id = $team AND member = $member
             ORDER BY run_seq DESC
             LIMIT $max
             """;
        command.Parameters.AddWithValue("$team", member.Team);
        command.Parameters.AddWithValue("$member", member.Name);
        command.Parameters.AddWithValue("$max", max);

        return await ReadRunsAsync(command, ct);
    }

    public async Task<WorkflowLedgerRow?> ReadWorkflowAsync(long correlation, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {WorkflowColumns} FROM workflow_ledger
             WHERE correlation = $correlation
             ORDER BY close_seq DESC
             LIMIT 1
             """;
        command.Parameters.AddWithValue("$correlation", correlation);

        var rows = await ReadWorkflowsAsync(command, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<LedgerPage> ReadPageAsync(
        DateTimeOffset since, long? before, int take, CancellationToken ct = default)
    {
        take = Math.Max(1, take);

        await using var connection = Open();

        // THE NEWEST `take` OF BOTH, by the one seq space they share: each table is read to `take`
        // below the cursor, and the two are merged and cut. A page shorter than `take` is the end.
        var stamp = Instant.Stamp(since);
        var cursor = before ?? long.MaxValue;

        await using var runsCommand = connection.CreateCommand();
        runsCommand.CommandText =
            $"""
             SELECT {RunColumns} FROM usage_ledger
             WHERE ended_at >= $since AND run_seq < $before
             ORDER BY run_seq DESC
             LIMIT $take
             """;
        runsCommand.Parameters.AddWithValue("$since", stamp);
        runsCommand.Parameters.AddWithValue("$before", cursor);
        runsCommand.Parameters.AddWithValue("$take", take);
        var runs = await ReadRunsAsync(runsCommand, ct);

        await using var workflowsCommand = connection.CreateCommand();
        workflowsCommand.CommandText =
            $"""
             SELECT {WorkflowColumns} FROM workflow_ledger
             WHERE closed_at >= $since AND close_seq < $before
             ORDER BY close_seq DESC
             LIMIT $take
             """;
        workflowsCommand.Parameters.AddWithValue("$since", stamp);
        workflowsCommand.Parameters.AddWithValue("$before", cursor);
        workflowsCommand.Parameters.AddWithValue("$take", take);
        var workflows = await ReadWorkflowsAsync(workflowsCommand, ct);

        return LedgerFacts.Page(runs, workflows, take);
    }

    public async Task<IReadOnlyList<ContainerUsageRow>> SumForTeamAsync(string team, long floorSeq, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // THE SAME ANSWER THE LOG'S SUM GIVES, from the ledger's columns: with usage is a run that
        // carries both an in and an out figure; a combined total (codex) stays out of the in/out
        // sums and in the without count, summed on its own; billable is the ledger's own weighting.
        command.CommandText =
            """
            SELECT
                team_id || '/' || member,
                COALESCE(SUM(CASE WHEN tokens_in IS NOT NULL AND tokens_out IS NOT NULL THEN tokens_in END), 0),
                COALESCE(SUM(CASE WHEN tokens_in IS NOT NULL AND tokens_out IS NOT NULL THEN tokens_out END), 0),
                SUM(CASE WHEN tokens_in IS NOT NULL AND tokens_out IS NOT NULL THEN 1 ELSE 0 END),
                SUM(CASE WHEN tokens_in IS NULL OR tokens_out IS NULL THEN 1 ELSE 0 END),
                SUM(tokens_combined),
                COALESCE(SUM(CASE WHEN tokens_in IS NOT NULL AND tokens_out IS NOT NULL THEN tokens_cached_in END), 0),
                COALESCE(SUM(CASE WHEN tokens_in IS NOT NULL AND tokens_out IS NOT NULL THEN tokens_cache_creation END), 0),
                COALESCE(SUM(billable), 0)
            FROM usage_ledger
            WHERE team_id = $team COLLATE NOCASE AND run_seq > $floor
            GROUP BY member COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$floor", floorSeq);

        var rows = new List<ContainerUsageRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
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
    }

    public async Task<IReadOnlyList<UsageLedgerRow>> ReadTeamRunsAsync(
        string team, long floor, DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string> members,
        CancellationToken ct = default)
    {
        await using var connection = Open();

        // Every read here walks `ix_usage_ledger_team_ended` (`outcome-005`), so a year-long period
        // reads the rows that ended since its start and each edge row is one step from its ends.
        var start = Stamp(from);
        var end = Stamp(to);

        // ONE STATEMENT: every run that ended at or after the period's start (the inside candidates:
        // the ones that began before its end are kept below, because `queued_at` is not stored in
        // the "O" form `ended_at` is, so the two do not compare as text), then each member's last
        // run that ended before the start and first run that ended at or after the end. The edges
        // are kept only for a member named or seen inside, as the per-member reads this replaces
        // read them.
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {RunColumns}, 0 AS part FROM usage_ledger
             WHERE team_id = $team AND run_seq > $floor AND ended_at >= $from
             UNION ALL
             SELECT {RunColumns}, 1 FROM (
                 SELECT {RunColumns}, row_number() OVER (PARTITION BY member ORDER BY ended_at DESC, run_seq DESC) AS n
                 FROM usage_ledger WHERE team_id = $team AND run_seq > $floor AND ended_at < $from)
             WHERE n = 1
             UNION ALL
             SELECT {RunColumns}, 2 FROM (
                 SELECT {RunColumns}, row_number() OVER (PARTITION BY member ORDER BY ended_at, run_seq) AS n
                 FROM usage_ledger WHERE team_id = $team AND run_seq > $floor AND ended_at >= $to)
             WHERE n = 1
             """;
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$floor", floor);
        command.Parameters.AddWithValue("$from", start);
        command.Parameters.AddWithValue("$to", end);

        var read = new List<(UsageLedgerRow, long)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) read.Add((ReadRun(reader), reader.GetInt64(21)));
        }

        return LedgerFacts.TeamRuns(read, to, members);
    }

    public async Task<IReadOnlyList<UsageLedgerRow>> ReadTeamRunsEndedAsync(
        string team, long floor, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // `ended_at` is stored in the "O" form, so the period's edges compare as text in that form,
        // along `ix_usage_ledger_team_ended`.
        var period = from is not null && to is not null ? "AND ended_at >= $from AND ended_at < $to" : "";
        command.CommandText =
            $"""
             SELECT {RunColumns} FROM usage_ledger
             WHERE team_id = $team AND run_seq > $floor {period}
             ORDER BY ended_at, run_seq
             """;
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$floor", floor);

        if (from is { } start && to is { } end)
        {
            command.Parameters.AddWithValue("$from", Stamp(start));
            command.Parameters.AddWithValue("$to", Stamp(end));
        }

        return await ReadRunsAsync(command, ct);
    }

    private static string Stamp(DateTimeOffset at) =>
        Instant.Stamp(at);

    private static UsageLedgerRow ReadRun(SqliteDataReader reader) => LedgerFacts.ReadRun(reader);

    private static Task<IReadOnlyList<UsageLedgerRow>> ReadRunsAsync(SqliteCommand command, CancellationToken ct) =>
        LedgerFacts.ReadRunsAsync(command, ct);

    private static Task<IReadOnlyList<WorkflowLedgerRow>> ReadWorkflowsAsync(SqliteCommand command, CancellationToken ct) =>
        LedgerFacts.ReadWorkflowsAsync(command, ct);
}
