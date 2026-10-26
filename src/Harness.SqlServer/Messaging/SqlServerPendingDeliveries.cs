using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Messaging;

/// <summary>
/// Pending deliveries on SQL Server: <c>SqlitePendingDeliveries</c> statement for statement over the
/// <c>pending_deliveries</c> table of <see cref="SqlServerMessagingSchema"/>, whose subscriber column
/// compares without case as SQLite's NOCASE one does. Rows are read by <see cref="PendingRows"/>, as
/// SQLite's are.
///
/// <para>
/// TWO WRITERS AT ONCE. SQLite's single writer makes its `ON CONFLICT` upserts safe; here each
/// read-then-write takes UPDLOCK and HOLDLOCK on the key it reads, so two offers of one delivery
/// leave one row and neither fails on the key, and a deferral never interleaves with an offer.
/// </para>
/// </summary>
public sealed class SqlServerPendingDeliveries : IPendingDeliveries
{
    private readonly string _connectionString;
    private readonly ISqlServerAppendRows _appendRows;

    /// <param name="appendRows">Writes the delivery's ledger row in the same transaction, as
    /// <c>SqlitePendingDeliveries.AddAsync</c> writes <c>delivery_ledger</c>.</param>
    public SqlServerPendingDeliveries(string connectionString, ISqlServerAppendRows appendRows)
    {
        _connectionString = SqlServerOpen.ConnectionString(connectionString);
        _appendRows = appendRows ?? throw new ArgumentNullException(nameof(appendRows));
    }

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);

    /// <summary>
    /// NOTHING on a delivery already recorded, so a re-offer of one in flight does not look
    /// un-started. The pending row goes first, so the transaction holds its key before the ledger
    /// row reads the log.
    /// </summary>
    public Task AddAsync(ContainerId subscriber, long seq, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        IF NOT EXISTS (SELECT 1 FROM pending_deliveries WITH (UPDLOCK, HOLDLOCK)
                                       WHERE subscriber = @subscriber AND seq = @seq)
                            INSERT INTO pending_deliveries (subscriber, seq, started) VALUES (@subscriber, @seq, 0);
                        """;
                    command.Parameters.AddWithValue("@subscriber", subscriber.ToString());
                    command.Parameters.AddWithValue("@seq", seq);

                    await command.ExecuteNonQueryAsync(token);
                }

                await _appendRows.OnDeliveryAsync(connection, transaction, seq, token);

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    public Task StartAsync(ContainerId subscriber, long seq, CancellationToken ct = default) =>
        ExecuteAsync(
            "UPDATE pending_deliveries SET started = 1 WHERE subscriber = @subscriber AND seq = @seq",
            command => Bind(command, subscriber, seq),
            ct);

    public Task RemoveAsync(ContainerId subscriber, long seq, CancellationToken ct = default) =>
        ExecuteAsync(
            "DELETE FROM pending_deliveries WHERE subscriber = @subscriber AND seq = @seq",
            command => Bind(command, subscriber, seq),
            ct);

    /// <summary>An upsert, as SQLite's: a deferral must never be lost to a row a failed write left missing.</summary>
    public Task DeferAsync(ContainerId subscriber, long seq, long fromRun, CancellationToken ct = default) =>
        ExecuteAsync(
            """
            MERGE pending_deliveries WITH (HOLDLOCK) AS p
            USING (SELECT @subscriber AS subscriber, @seq AS seq) AS n ON p.subscriber = n.subscriber AND p.seq = n.seq
            WHEN MATCHED THEN UPDATE SET started = 0, deferred_from_run = @from
            WHEN NOT MATCHED THEN INSERT (subscriber, seq, started, deferred_from_run) VALUES (n.subscriber, n.seq, 0, @from);
            """,
            command =>
            {
                Bind(command, subscriber, seq);
                command.Parameters.AddWithValue("@from", fromRun);
            },
            ct);

    public Task<int> RemoveAllAsync(ContainerId subscriber, CancellationToken ct = default) =>
        ExecuteAsync(
            "DELETE FROM pending_deliveries WHERE subscriber = @subscriber",
            command => command.Parameters.AddWithValue("@subscriber", subscriber.ToString()),
            ct);

    /// <summary>The team half of a <c>Team/Name</c> key, without case; the slash guard is SQLite's.</summary>
    public Task<int> RemoveAllForTeamAsync(string team, CancellationToken ct = default) =>
        ExecuteAsync(
            """
            DELETE FROM pending_deliveries
            WHERE CHARINDEX(N'/', subscriber) > 0
              AND SUBSTRING(subscriber, 1, CHARINDEX(N'/', subscriber) - 1) = @team
            """,
            command => command.Parameters.AddWithValue("@team", team),
            ct);

    public Task<IReadOnlyList<PendingDelivery>> ForAsync(ContainerId subscriber, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<PendingDelivery>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT seq, started, deferred_from_run FROM pending_deliveries
                    WHERE subscriber = @subscriber
                    ORDER BY seq
                    """;
                command.Parameters.AddWithValue("@subscriber", subscriber.ToString());

                var rows = new List<PendingDelivery>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(PendingRows.Read(reader));
                return rows;
            },
            ct);

    /// <summary>A subscriber starting with <c>Team/</c>, without case, compared on its first
    /// characters as SQLite's <c>substr</c> does.</summary>
    public Task<IReadOnlyList<TeamPendingDelivery>> ForTeamAsync(string team, CancellationToken ct = default) =>
        ReadTeamAsync(
            $"""
            SELECT subscriber, seq, started, deferred_from_run FROM pending_deliveries
            WHERE LEFT(subscriber, LEN(@teamPrefix)) = @teamPrefix
            ORDER BY {SqlServerSql.NoCaseOrder("subscriber")}, seq
            """,
            command => command.Parameters.AddWithValue("@teamPrefix", $"{team}/"),
            ct);

    public Task<IReadOnlyList<TeamPendingDelivery>> AllAsync(CancellationToken ct = default) =>
        ReadTeamAsync(
            $"""
            SELECT subscriber, seq, started, deferred_from_run FROM pending_deliveries
            ORDER BY {SqlServerSql.NoCaseOrder("subscriber")}, seq
            """,
            _ => { },
            ct);

    private Task<IReadOnlyList<TeamPendingDelivery>> ReadTeamAsync(string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        RunAsync<IReadOnlyList<TeamPendingDelivery>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                bind(command);

                var rows = new List<TeamPendingDelivery>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(PendingRows.ReadTeam(reader));
                return rows;
            },
            ct);

    private static void Bind(SqlCommand command, ContainerId subscriber, long seq)
    {
        command.Parameters.AddWithValue("@subscriber", subscriber.ToString());
        command.Parameters.AddWithValue("@seq", seq);
    }

    private Task<int> ExecuteAsync(string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        SqlServerOpen.ExecuteAsync(_connectionString, sql, bind, ct);
}
