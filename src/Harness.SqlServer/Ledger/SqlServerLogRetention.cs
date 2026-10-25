using Harness.Contracts;
using Harness.Messaging;
using Harness.SqlServer.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// <see cref="ILogRetention"/> on SQL Server: which log rows the daily pass may hand to
/// <see cref="IMessageLog.DeleteAsync"/>, SQLite's <c>SqliteLogRetention</c> clause for clause. That
/// class's remarks are the argument (a workflow is chosen from only when it is over; the root,
/// declarations and closes go last and only with the rest). It chooses and never deletes.
///
/// <para>
/// NO LOCK HERE. It only reads, and the log's delete re-checks every row it is handed - a row a
/// survivor cites, one queued for a run or a pending wake's - inside its own transaction under the
/// append lock, so a row that became held between this read and that delete is retained there. The
/// pass hands over at most its batch (5,000 rows, <c>LogRetention.DefaultBatch</c>) a call, so that
/// delete's transaction stays short.
/// </para>
/// </summary>
public sealed class SqlServerLogRetention(string connectionString) : ILogRetention
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    /// <summary>The workflows whose rows may be chosen: over before the cut-off, or no workflow.</summary>
    private static readonly string Eligible =
        $"""
         SELECT m.correlation_id
         FROM (
             SELECT correlation_id,
                    MAX(CASE WHEN type IN (@completed, @closed) THEN occurred_at END) AS over_at,
                    MAX(CASE WHEN type IN (@completed, @closed)
                               OR type LIKE @instructionPrefix {SqlServerSql.Like}
                               OR type LIKE @cardPrefix {SqlServerSql.Like}
                        THEN 1 ELSE 0 END) AS worked
             FROM messages
             GROUP BY correlation_id
         ) m
         WHERE (m.worked = 0 OR (m.over_at < @cutoff AND NOT ({SqlServerWorkflowSql.NotClosed})))
           AND m.correlation_id NOT IN (SELECT correlation FROM backlog_dispatches WHERE frozen_stats IS NULL)
           AND m.correlation_id NOT IN (SELECT workflow FROM wakes)
         """;

    /// <summary>A row that goes only with its whole workflow: the root, a declaration, a close.</summary>
    private const string Head = "(r.seq = r.correlation_id OR r.type IN (@completed, @closed))";

    /// <summary>A row that may go at all: older than the cut-off, not queued for a run, not a pending wake.</summary>
    private const string Removable =
        """
        r.occurred_at < @cutoff
        AND r.seq NOT IN (SELECT seq FROM pending_deliveries)
        AND r.seq NOT IN (SELECT asked_seq FROM wakes)
        """;

    public async Task<IReadOnlyList<long>> BodyAsync(
        DateTimeOffset cutoff, long afterSeq, int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        return await SqlServerOpen.RunAsync<IReadOnlyList<long>>(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     SELECT TOP (@max) r.seq FROM messages r
                     WHERE r.seq > @after
                       AND {Removable}
                       AND NOT {Head}
                       AND r.correlation_id IN ({Eligible})
                     ORDER BY r.seq
                     """;
                Bind(command, cutoff);
                command.Parameters.AddWithValue("@after", afterSeq);
                command.Parameters.AddWithValue("@max", max);

                var seqs = new List<long>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) seqs.Add(reader.GetInt64(0));
                return seqs;
            },
            ct);
    }

    public async Task<IReadOnlyList<long>> WholeAsync(
        DateTimeOffset cutoff, int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        return await SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     SELECT r.correlation_id, r.seq FROM messages r
                     WHERE r.correlation_id IN ({Eligible})
                       AND r.correlation_id NOT IN (SELECT correlation FROM backlog_dispatches)
                       AND r.correlation_id NOT IN (SELECT correlation FROM workflow_outcome_links)
                       AND NOT EXISTS (
                           SELECT 1 FROM messages k
                           WHERE k.correlation_id = r.correlation_id
                             AND NOT (k.occurred_at < @cutoff
                                      AND k.seq NOT IN (SELECT seq FROM pending_deliveries)
                                      AND k.seq NOT IN (SELECT asked_seq FROM wakes)))
                     ORDER BY r.correlation_id, r.seq
                     """;
                Bind(command, cutoff);

                var whole = new LedgerFacts.WholeWorkflows(max);

                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    if (!whole.Add(reader.GetInt64(0), reader.GetInt64(1))) break;
                }

                return whole.Finish();
            },
            ct);
    }

    private static void Bind(SqlCommand command, DateTimeOffset cutoff)
    {
        // The log stamps `occurred_at` as round-trip UTC text of one width, so comparing the text
        // compares the time.
        command.Parameters.AddWithValue("@cutoff", Instant.Stamp(cutoff));
        SqlServerWorkflowSql.BindNotClosed(command);
    }
}
