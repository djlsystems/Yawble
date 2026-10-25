using System.Globalization;
using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// <see cref="ILogSpend"/> on SQL Server: the spend reads as they were before the ledger, projections
/// of the log's terminal rows, SQLite's <c>LogSpend</c> clause for clause - kept as the reference the
/// ledger is held to, and read by nothing else.
///
/// <para>
/// THE DIALECT: <c>json_extract(..) IS [NOT] NULL</c> is <see cref="SqlServerSql.Present"/> /
/// <see cref="SqlServerSql.Absent"/>, read per row before the sums; <c>CAST(json_extract(..) AS INTEGER)</c> is
/// <c>TRY_CAST(JSON_VALUE(..) AS BIGINT)</c>; a count of rows is cast to BIGINT, because a SUM of 1s
/// is an INT here; and the instruction prefix's LIKE is case-insensitive, as SQLite's is.
/// </para>
/// </summary>
public sealed class SqlServerLogSpend(string connectionString) : ILogSpend
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    private static string Number(string field) => $"COALESCE(TRY_CAST(JSON_VALUE(payload, '$.{field}') AS BIGINT), 0)";

    private static string Has(string field) => $"CASE WHEN {SqlServerSql.Present("payload", field)} THEN 1 ELSE 0 END";

    /// <summary>
    /// WHAT THE SUMS READ OF EACH ROW, computed in a derived table first: SQL Server refuses an
    /// aggregate over an expression holding a subquery, and <see cref="SqlServerSql.Present"/> is one.
    /// </summary>
    private static readonly string Facts =
        $"""
         COALESCE(JSON_VALUE(payload, '$.tokensSource'), N'') AS src,
         {Has("tokensIn")} AS has_in, {Has("tokensOut")} AS has_out, {Has("tokensTotal")} AS has_total,
         {Number("tokensIn")} AS n_in, {Number("tokensOut")} AS n_out, {Number("tokensTotal")} AS n_total,
         {Number("tokensCachedIn")} AS n_cached_in, {Number("tokensCacheCreation")} AS n_cache_creation
         """;

    /// <summary>SQLite's <c>LogSpend.BillableSum</c> over <see cref="Facts"/>: the run's weights, an
    /// excluded estimate left out. Binds <c>@excluded</c>.</summary>
    private const string BillableSum =
        """
        CAST(COALESCE(SUM(CASE
            WHEN src <> @excluded
             AND (has_in = 1 OR has_total = 1)
            THEN n_in + n_out + n_total + n_cached_in / 10 + (n_cache_creation * 5) / 4
            END), 0) AS BIGINT)
        """;

    /// <summary>SQLite's <c>LogSpend.MeasuredCount</c> over <see cref="Facts"/>: measured and
    /// unmeasured runs. Binds <c>@excluded</c> and <c>@noModel</c>.</summary>
    private const string MeasuredCount =
        """
        CAST(COALESCE(SUM(CASE
            WHEN ((has_in = 1 AND has_out = 1) OR has_total = 1)
              AND src <> @excluded
              OR src = @noModel
            THEN 1 ELSE 0 END), 0) AS BIGINT),
        CAST(COALESCE(SUM(CASE
            WHEN ((has_in = 0 OR has_out = 0) AND has_total = 0
              OR src = @excluded)
              AND src <> @noModel
            THEN 1 ELSE 0 END), 0) AS BIGINT)
        """;

    private static readonly string NotCounted = SqlServerSql.Absent("payload", PayloadFields.UsageCountedOn);

    public Task<WorkflowSpend> GetSpendSinceNudgeAsync(long correlationId, CancellationToken ct = default) =>
        SpendAsync(correlationId, sinceLastNudge: true, ct);

    public Task<WorkflowSpend> GetWorkflowSpendAsync(long correlationId, CancellationToken ct = default) =>
        SpendAsync(correlationId, sinceLastNudge: false, ct);

    private Task<WorkflowSpend> SpendAsync(long correlationId, bool sinceLastNudge, CancellationToken ct) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                long since = 0;

                if (sinceLastNudge)
                {
                    await using var window = connection.CreateCommand();
                    window.CommandText =
                        $"""
                         SELECT COALESCE(MAX(seq), 0) FROM messages
                         WHERE correlation_id = @id
                           AND causation_seq = @id
                           AND type LIKE N'agentContainer.instruction.%' {SqlServerSql.Like}
                         """;
                    window.Parameters.AddWithValue("@id", correlationId);

                    since = Convert.ToInt64(await window.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                }

                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     SELECT {BillableSum}, {MeasuredCount}
                     FROM (
                         SELECT {Facts} FROM messages
                         WHERE correlation_id = @id
                           AND type IN (@completed, @failed)
                           AND {NotCounted}
                           AND seq > @since
                     ) r
                     """;
                command.Parameters.AddWithValue("@id", correlationId);
                command.Parameters.AddWithValue("@since", since);
                command.Parameters.AddWithValue("@completed", MessageTypes.Completed);
                command.Parameters.AddWithValue("@failed", MessageTypes.Failed);
                BindWeights(command);

                return await ReadSpendAsync(command, token);
            },
            ct);

    public async Task<WorkflowSpend> GetTriggerSpendAsync(
        IReadOnlyCollection<string> sources, DateTimeOffset since, CancellationToken ct = default)
    {
        if (sources.Count == 0) return new WorkflowSpend(0, 0, 0);

        return await SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();

                var names = new List<string>(sources.Count);
                foreach (var source in sources)
                {
                    var name = $"@source{names.Count}";
                    names.Add(name);
                    command.Parameters.AddWithValue(name, source);
                }

                // THREE HOPS, all by causation: the trigger's instructions; the rows that closed them
                // and the run's hand-back; the rows that closed THOSE. Only terminal rows are counted.
                command.CommandText =
                    $"""
                     WITH fires AS (
                         SELECT seq FROM messages
                         WHERE source IN ({string.Join(", ", names)})
                           AND type LIKE N'agentContainer.instruction.%' {SqlServerSql.Like}
                           AND occurred_at >= @since
                     ),
                     runs AS (
                         SELECT seq, type, payload FROM messages
                         WHERE causation_seq IN (SELECT seq FROM fires)
                           AND type IN (@completed, @failed, @handback)
                     ),
                     counted AS (
                         SELECT payload FROM runs WHERE type IN (@completed, @failed)
                         UNION ALL
                         SELECT payload FROM messages
                         WHERE causation_seq IN (SELECT seq FROM runs)
                           AND type IN (@completed, @failed)
                     )
                     SELECT {BillableSum}, {MeasuredCount}
                     FROM (SELECT {Facts} FROM counted WHERE {NotCounted}) r
                     """;
                command.Parameters.AddWithValue("@since", Instant.Stamp(since.ToUniversalTime()));
                command.Parameters.AddWithValue("@completed", MessageTypes.Completed);
                command.Parameters.AddWithValue("@failed", MessageTypes.Failed);
                command.Parameters.AddWithValue("@handback", MessageTypes.Handback);
                BindWeights(command);

                return await ReadSpendAsync(command, token);
            },
            ct);
    }

    public async Task<IReadOnlyList<Message>> ReadRecentRunsAsync(
        string member, int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        return await SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     SELECT TOP (@max) {MessageRows.Columns}
                     FROM messages
                     WHERE source = @member COLLATE {SqlServerSql.NoCase}
                       AND type IN (@completed, @failed)
                       AND {NotCounted}
                     ORDER BY seq DESC
                     """;
                command.Parameters.AddWithValue("@member", member);
                command.Parameters.AddWithValue("@completed", MessageTypes.Completed);
                command.Parameters.AddWithValue("@failed", MessageTypes.Failed);
                command.Parameters.AddWithValue("@max", max);

                return await MessageRows.ReadAllAsync(command, token);
            },
            ct);
    }

    private static void BindWeights(SqlCommand command)
    {
        command.Parameters.AddWithValue("@excluded", UsageSource.ExcludedEstimate);
        command.Parameters.AddWithValue("@noModel", UsageSource.NoModel);
    }

    private static async Task<WorkflowSpend> ReadSpendAsync(SqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct)
            ? new WorkflowSpend(
                reader.GetInt64(0),
                checked((int)reader.GetInt64(1)),
                checked((int)reader.GetInt64(2)))
            : new WorkflowSpend(0, 0, 0);
    }
}
