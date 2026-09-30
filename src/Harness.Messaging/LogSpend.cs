using System.Globalization;
using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Messaging;

/// <summary>
/// THE SPEND READS AS THEY WERE BEFORE THE LEDGER: projections of the message log's terminal rows.
///
/// <para>
/// KEPT AS THE REFERENCE THE LEDGER IS HELD TO, and read by nothing else. The spend reads on
/// <see cref="IMessageLog"/> now read <c>usage_ledger</c>, which Reset and team deletion never touch;
/// <c>LedgerParityTests</c> runs these and those over one fixture and requires the same figures, so a
/// change to how the ledger weighs or counts a run is caught against what the product always charged.
/// <see cref="BillableSum"/> is still the team Tokens tile's weighting
/// (<c>SqliteMessageStore.SumUsageForTeamAsync</c>).
/// </para>
/// </summary>
public sealed class LogSpend(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }

    private static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Spend since the last nudge. See <see cref="IMessageLog.GetSpendSinceNudgeAsync"/> for why the
    /// budget uses this and a screen does not.
    /// </summary>
    public Task<WorkflowSpend> GetSpendSinceNudgeAsync(long correlationId, CancellationToken ct = default) =>
        SpendAsync(correlationId, sinceLastNudge: true, ct);

    public Task<WorkflowSpend> GetWorkflowSpendAsync(long correlationId, CancellationToken ct = default) =>
        SpendAsync(correlationId, sinceLastNudge: false, ct);

    /// <summary>
    /// Billable tokens summed over completed/failed rows, weighted per row exactly as
    /// <see cref="InvocationUsage.BillableTokens"/> weights one run: cache read / 10, cache write
    /// * 5 / 4, a combined total as reported. <see cref="UsageSource.ExcludedEstimate"/> rows are
    /// excluded. Binds <c>$excluded</c>.
    /// Shared by the workflow spend and the team Tokens tile so the two cannot drift.
    /// </summary>
    internal const string BillableSum =
        """
        COALESCE(SUM(CASE
            WHEN COALESCE(json_extract(payload, '$.tokensSource'), '') <> $excluded
             AND (json_extract(payload, '$.tokensIn') IS NOT NULL
               OR json_extract(payload, '$.tokensTotal') IS NOT NULL)
            THEN IFNULL(CAST(json_extract(payload, '$.tokensIn') AS INTEGER), 0)
               + IFNULL(CAST(json_extract(payload, '$.tokensOut') AS INTEGER), 0)
               + IFNULL(CAST(json_extract(payload, '$.tokensTotal') AS INTEGER), 0)
               + IFNULL(CAST(json_extract(payload, '$.tokensCachedIn') AS INTEGER), 0) / 10
               + (IFNULL(CAST(json_extract(payload, '$.tokensCacheCreation') AS INTEGER), 0) * 5) / 4
            END), 0)
        """;

    private async Task<WorkflowSpend> SpendAsync(
        long correlationId, bool sinceLastNudge, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT {BillableSum}, {MeasuredCount}
            FROM messages
            WHERE correlation_id = $id
              AND type IN ($completed, $failed)
              AND json_extract(payload, '$.usageCountedOn') IS NULL
              AND seq > $since
            """;

        // THE WINDOW. Zero for the whole-workflow figure, so the clause costs nothing there;
        // for the budget it is the last instruction caused by the correlation itself - a nudge, by
        // construction - and 0 again when there has been none, which reads the whole workflow.
        long since = 0;

        if (sinceLastNudge)
        {
            await using var window = connection.CreateCommand();
            window.CommandText =
                """
                SELECT COALESCE(MAX(seq), 0) FROM messages
                WHERE correlation_id = $id
                  AND causation_seq = $id
                  AND type LIKE 'agentContainer.instruction.%'
                """;
            window.Parameters.AddWithValue("$id", correlationId);

            since = (await window.ExecuteScalarAsync(ct)) is long found ? found : 0;
        }

        command.Parameters.AddWithValue("$id", correlationId);
        command.Parameters.AddWithValue("$since", since);
        command.Parameters.AddWithValue("$completed", MessageTypes.Completed);
        command.Parameters.AddWithValue("$failed", MessageTypes.Failed);
        command.Parameters.AddWithValue("$excluded", UsageSource.ExcludedEstimate);
        command.Parameters.AddWithValue("$noModel", UsageSource.NoModel);

        await using var reader = await command.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            var tokensSpent = reader.GetInt64(0);
            var runsWithMeasuredUsage = checked((int)reader.GetInt64(1));
            var runsWithoutUsage = checked((int)reader.GetInt64(2));
            return new WorkflowSpend(tokensSpent, runsWithMeasuredUsage, runsWithoutUsage);
        }

        return new WorkflowSpend(0, 0, 0);
    }

    /// <summary>
    /// Measured and unmeasured runs, the two columns after <see cref="BillableSum"/>. Shared by the
    /// workflow spend and the trigger spend so a trigger's figure cannot drift from a workflow's.
    /// A row carrying <see cref="UsageSource.NoModel"/> ran no model (a plugin's run): it is MEASURED,
    /// and <see cref="BillableSum"/> adds nothing for it because it carries no figures. Any other row
    /// with no figures is unmeasured, never a zero. Binds <c>$excluded</c> and <c>$noModel</c>.
    /// </summary>
    internal const string MeasuredCount =
        """
        COALESCE(SUM(CASE
            WHEN ((json_extract(payload, '$.tokensIn') IS NOT NULL
              AND json_extract(payload, '$.tokensOut') IS NOT NULL)
              OR json_extract(payload, '$.tokensTotal') IS NOT NULL)
              AND COALESCE(json_extract(payload, '$.tokensSource'), '') <> $excluded
              OR COALESCE(json_extract(payload, '$.tokensSource'), '') = $noModel
            THEN 1 ELSE 0 END), 0),
        COALESCE(SUM(CASE
            WHEN ((json_extract(payload, '$.tokensIn') IS NULL
              OR json_extract(payload, '$.tokensOut') IS NULL)
              AND json_extract(payload, '$.tokensTotal') IS NULL
              OR COALESCE(json_extract(payload, '$.tokensSource'), '') = $excluded)
              AND COALESCE(json_extract(payload, '$.tokensSource'), '') <> $noModel
            THEN 1 ELSE 0 END), 0)
        """;

    public async Task<WorkflowSpend> GetTriggerSpendAsync(
        IReadOnlyCollection<string> sources, DateTimeOffset since, CancellationToken ct = default)
    {
        if (sources.Count == 0) return new WorkflowSpend(0, 0, 0);

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        var names = sources.Select((_, index) => $"$source{index}").ToArray();

        // THREE HOPS, all by causation: the trigger's instructions today; the rows that closed
        // them (and the run's hand-back, which wakes the Manager on its own row); the rows that
        // closed THOSE - the Manager runs they woke. Only terminal rows are counted.
        command.CommandText =
            $"""
            WITH fires AS (
                SELECT seq FROM messages
                WHERE source IN ({string.Join(", ", names)})
                  AND type LIKE 'agentContainer.instruction.%'
                  AND occurred_at >= $since
            ),
            runs AS (
                SELECT seq, type, payload FROM messages
                WHERE causation_seq IN (SELECT seq FROM fires)
                  AND type IN ($completed, $failed, $handback)
            ),
            counted AS (
                SELECT payload FROM runs WHERE type IN ($completed, $failed)
                UNION ALL
                SELECT payload FROM messages
                WHERE causation_seq IN (SELECT seq FROM runs)
                  AND type IN ($completed, $failed)
            )
            SELECT {BillableSum}, {MeasuredCount}
            FROM counted
            WHERE json_extract(payload, '$.usageCountedOn') IS NULL
            """;

        var index = 0;
        foreach (var source in sources)
        {
            command.Parameters.AddWithValue(names[index++], source);
        }

        command.Parameters.AddWithValue("$since", Stamp(since.ToUniversalTime()));
        command.Parameters.AddWithValue("$completed", MessageTypes.Completed);
        command.Parameters.AddWithValue("$failed", MessageTypes.Failed);
        command.Parameters.AddWithValue("$handback", MessageTypes.Handback);
        command.Parameters.AddWithValue("$excluded", UsageSource.ExcludedEstimate);
        command.Parameters.AddWithValue("$noModel", UsageSource.NoModel);

        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct)
            ? new WorkflowSpend(
                reader.GetInt64(0),
                checked((int)reader.GetInt64(1)),
                checked((int)reader.GetInt64(2)))
            : new WorkflowSpend(0, 0, 0);
    }

    public async Task<IReadOnlyList<Message>> ReadRecentRunsAsync(
        string member, int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            $"""
             SELECT {MessageRows.Columns}
             FROM messages
             WHERE source = $member COLLATE NOCASE
               AND type IN ($completed, $failed)
               AND json_extract(payload, '$.usageCountedOn') IS NULL
             ORDER BY seq DESC
             LIMIT $max
             """;

        command.Parameters.AddWithValue("$member", member);
        command.Parameters.AddWithValue("$completed", MessageTypes.Completed);
        command.Parameters.AddWithValue("$failed", MessageTypes.Failed);
        command.Parameters.AddWithValue("$max", max);

        return await MessageRows.ReadAllAsync(command, ct);
    }

}
