using System.Globalization;
using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Messaging;

/// <summary>
/// WHICH MESSAGE-LOG ROWS THE DAILY RETENTION PASS MAY HAND TO <see cref="IMessageLog.DeleteAsync"/>.
/// It chooses and never deletes: the log's one delete does that, so a row a survivor cites, or one
/// still queued for a run, is retained there whatever is chosen here.
///
/// <para>
/// A WORKFLOW IS CHOSEN FROM ONLY WHEN IT IS OVER: closed by <see cref="WorkflowOpenSql.NotClosed"/>,
/// the one place "open" is defined, with its newest declaration or close older than the cut-off - or
/// it is no workflow at all (no instruction, card, declaration or close: an event nothing woke on).
/// An open workflow loses nothing, however old. So does one a live backlog dispatch names (stats not
/// frozen): its item's members, instructions and notices are read from the log on every read, and a
/// purge would change them. So does one a pending wake belongs to.
/// </para>
///
/// <para>
/// THE ROOT AND EVERY DECLARATION AND CLOSE GO LAST, AND ONLY WITH THE REST. The open read
/// (<see cref="IMessageLog.OpenWorkflowsAmongAsync"/>, which the outcome figures and the backlog ask)
/// admits a workflow by any row it still has, so a workflow left with its root and without its
/// declaration would read open again - and the outcome figures would change. <see cref="BodyAsync"/>
/// never chooses those rows; <see cref="WholeAsync"/> chooses a workflow's every remaining row at
/// once, and only when each of them may go. A workflow a backlog dispatch (stored landed, frozen
/// stats) or an outcome link names keeps them for good: a restored item derives from them, and a
/// person links a workflow by its root. The workflow ledger needs none of them - its reads are its
/// own rows - so a workflow it records goes whole when nothing else holds it.
/// </para>
/// </summary>
public sealed class SqliteLogRetention(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    /// <summary>The workflows whose rows may be chosen: over before the cut-off, or no workflow.</summary>
    private const string Eligible =
        $"""
         SELECT m.correlation_id
         FROM (
             SELECT correlation_id,
                    MAX(CASE WHEN type IN ($completed, $closed) THEN occurred_at END) AS over_at,
                    MAX(CASE WHEN type IN ($completed, $closed) OR type LIKE $instructionPrefix OR type LIKE $cardPrefix
                        THEN 1 ELSE 0 END) AS worked
             FROM messages
             GROUP BY correlation_id
         ) m
         WHERE (m.worked = 0 OR (m.over_at < $cutoff AND NOT ({WorkflowOpenSql.NotClosed})))
           AND m.correlation_id NOT IN (SELECT correlation FROM backlog_dispatches WHERE frozen_stats IS NULL)
           AND m.correlation_id NOT IN (SELECT workflow FROM wakes)
         """;

    /// <summary>A row that goes only with its whole workflow: the root, a declaration, a close.</summary>
    private const string Head = "(r.seq = r.correlation_id OR r.type IN ($completed, $closed))";

    /// <summary>A row that may go at all: older than the cut-off, not queued for a run, not a pending wake.</summary>
    private const string Removable =
        """
        r.occurred_at < $cutoff
        AND r.seq NOT IN (SELECT seq FROM pending_deliveries)
        AND r.seq NOT IN (SELECT asked_seq FROM wakes)
        """;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }

    /// <summary>
    /// At most <paramref name="max"/> rows after <paramref name="afterSeq"/>, oldest first, of the
    /// eligible workflows, never a root, declaration or close.
    /// </summary>
    public async Task<IReadOnlyList<long>> BodyAsync(
        DateTimeOffset cutoff, long afterSeq, int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT r.seq FROM messages r
             WHERE r.seq > $after
               AND {Removable}
               AND NOT {Head}
               AND r.correlation_id IN ({Eligible})
             ORDER BY r.seq
             LIMIT $max
             """;
        Bind(command, cutoff);
        command.Parameters.AddWithValue("$after", afterSeq);
        command.Parameters.AddWithValue("$max", max);

        var seqs = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) seqs.Add(reader.GetInt64(0));
        return seqs;
    }

    /// <summary>
    /// Whole eligible workflows - every row each still has - that no backlog dispatch or outcome link
    /// names and whose every row may go, oldest workflow first, adding a workflow only while the total
    /// stays within <paramref name="max"/>.
    /// </summary>
    public async Task<IReadOnlyList<long>> WholeAsync(
        DateTimeOffset cutoff, int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        await using var connection = Open();
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
                     AND NOT (k.occurred_at < $cutoff
                              AND k.seq NOT IN (SELECT seq FROM pending_deliveries)
                              AND k.seq NOT IN (SELECT asked_seq FROM wakes)))
             ORDER BY r.correlation_id, r.seq
             """;
        Bind(command, cutoff);

        var chosen = new List<long>();
        var workflow = new List<long>();
        long? current = null;

        void Close()
        {
            if (workflow.Count > 0 && chosen.Count + workflow.Count <= max) chosen.AddRange(workflow);
            workflow.Clear();
        }

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var correlation = reader.GetInt64(0);
            if (correlation != current)
            {
                Close();
                if (chosen.Count >= max) break;
                current = correlation;
            }

            workflow.Add(reader.GetInt64(1));
        }

        Close();
        return chosen;
    }

    private static void Bind(SqliteCommand command, DateTimeOffset cutoff)
    {
        // The log stamps `occurred_at` as round-trip UTC text of one width, so comparing the text
        // compares the time.
        command.Parameters.AddWithValue("$cutoff", cutoff.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        WorkflowOpenSql.Bind(command);
    }
}
