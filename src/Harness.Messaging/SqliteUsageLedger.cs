using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Messaging;

/// <summary>
/// <see cref="IUsageLedger"/> over <c>usage_ledger</c> and <c>workflow_ledger</c> in the log's own
/// database file. Reads only: the one writer is <see cref="LedgerRows"/>, inside the append.
/// </summary>
public sealed class SqliteUsageLedger(string databasePath) : IUsageLedger
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    private const string RunColumns =
        """
        run_seq, correlation, team_id, team_name, member, member_kind, agent_preset, run_outcome,
        queued_at, started_at, ended_at, measured, tokens_in, tokens_cached_in, tokens_cache_creation,
        tokens_out, tokens_combined, billable, trigger_source, trigger_fired_at, backfilled
        """;

    private const string WorkflowColumns =
        """
        close_seq, correlation, team_id, team_name, root_at, closed_at, how_closed,
        outcome_id_at_close, backfilled
        """;

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
        var stamp = since.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
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

        var page = runs.Select(r => r.RunSeq)
            .Concat(workflows.Select(w => w.CloseSeq))
            .OrderDescending()
            .Take(take)
            .ToList();

        if (page.Count == 0) return new LedgerPage([], [], null);

        var floor = page[^1];

        return new LedgerPage(
            [.. runs.Where(r => r.RunSeq >= floor)],
            [.. workflows.Where(w => w.CloseSeq >= floor)],
            page.Count == take ? floor : null);
    }

    private static async Task<IReadOnlyList<UsageLedgerRow>> ReadRunsAsync(SqliteCommand command, CancellationToken ct)
    {
        var rows = new List<UsageLedgerRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new UsageLedgerRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                Text(reader, 2),
                Text(reader, 3),
                reader.GetString(4),
                Text(reader, 5),
                Text(reader, 6),
                reader.GetString(7),
                At(reader, 8),
                At(reader, 9),
                MessageRows.ReadStamp(reader.GetString(10)),
                reader.GetInt64(11) != 0,
                Long(reader, 12),
                Long(reader, 13),
                Long(reader, 14),
                Long(reader, 15),
                Long(reader, 16),
                Long(reader, 17),
                Text(reader, 18),
                At(reader, 19),
                reader.GetInt64(20) != 0));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<WorkflowLedgerRow>> ReadWorkflowsAsync(SqliteCommand command, CancellationToken ct)
    {
        var rows = new List<WorkflowLedgerRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WorkflowLedgerRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                Text(reader, 2),
                Text(reader, 3),
                At(reader, 4),
                MessageRows.ReadStamp(reader.GetString(5)),
                reader.GetString(6),
                Text(reader, 7),
                reader.GetInt64(8) != 0));
        }

        return rows;
    }

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? Long(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? At(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : MessageRows.ReadStamp(reader.GetString(ordinal));
}
