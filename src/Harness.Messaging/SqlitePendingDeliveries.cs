using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// Pending deliveries over the same SQLite file as the log. It shares that FILE with
/// <see cref="SqliteMessageStore"/>, but not that store's connection shape - <c>SqliteMessageStore</c>
/// sets neither <c>Pooling</c> nor any pragma, and opens with <c>Cache=Shared</c>. This class is
/// mechanically closer to the Identity stores (<c>SqliteTeamStore</c>, <c>SqliteUserStore</c>,
/// <c>SqlitePrincipalStore</c>): same connection string shape, same <c>Pooling=false</c>, same
/// reliance on <c>MessageSchema</c>'s steps (and <c>OutcomeSchema</c>'s, for
/// <c>delivery_ledger</c>) having been applied by <c>SchemaMigrator</c> before this is constructed.
/// </summary>
public sealed class SqlitePendingDeliveries : IPendingDeliveries
{
    private readonly string _connectionString;

    /// <summary>Absence means the safe value. Only the test fixture asks for anything else.</summary>
    private readonly SqliteDurability _durability;

    public SqlitePendingDeliveries(string databasePath, SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        _durability = durability;
    }

    /// <summary>
    /// DO NOTHING on conflict rather than upserting `started = 0`. A re-offer of a delivery that is
    /// already in flight must not look un-started, or a restart would resume work an agent was
    /// half-way through - the exact outcome the started flag exists to prevent.
    ///
    /// <para>
    /// THE DELIVERY'S ATTRIBUTION IS RECORDED HERE, in the same transaction (<c>delivery_ledger</c>,
    /// <see cref="LedgerRows.WriteDeliveryAsync"/>): when it was queued and the trigger fire it
    /// answers, while the rows that say so are still on the log. A Reset before the run ends then
    /// cannot take them from its ledger row. The pending row goes first, so the transaction holds
    /// the write lock before it reads.
    /// </para>
    /// </summary>
    public async Task AddAsync(ContainerId subscriber, long seq, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO pending_deliveries (subscriber, seq, started)
                VALUES ($subscriber, $seq, 0)
                ON CONFLICT(subscriber, seq) DO NOTHING
                """;
            command.Parameters.AddWithValue("$subscriber", subscriber.ToString());
            command.Parameters.AddWithValue("$seq", seq);

            await command.ExecuteNonQueryAsync(ct);
        }

        await LedgerRows.WriteDeliveryAsync(connection, transaction, seq, ct);

        await transaction.CommitAsync(ct);
    }

    public async Task StartAsync(ContainerId subscriber, long seq, CancellationToken ct = default)
    {
        await ExecuteAsync(
            "UPDATE pending_deliveries SET started = 1 WHERE subscriber = $subscriber AND seq = $seq",
            subscriber, seq, ct);
    }

    public async Task RemoveAsync(ContainerId subscriber, long seq, CancellationToken ct = default)
    {
        await ExecuteAsync(
            "DELETE FROM pending_deliveries WHERE subscriber = $subscriber AND seq = $seq",
            subscriber, seq, ct);
    }

    public async Task DeferAsync(ContainerId subscriber, long seq, long fromRun, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // An UPSERT, not an update: the row is normally there, but a deferral must never be lost to
        // a row a failed write left missing.
        command.CommandText =
            """
            INSERT INTO pending_deliveries (subscriber, seq, started, deferred_from_run)
            VALUES ($subscriber, $seq, 0, $from)
            ON CONFLICT(subscriber, seq) DO UPDATE SET started = 0, deferred_from_run = $from
            """;
        command.Parameters.AddWithValue("$subscriber", subscriber.ToString());
        command.Parameters.AddWithValue("$seq", seq);
        command.Parameters.AddWithValue("$from", fromRun);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> RemoveAllAsync(ContainerId subscriber, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM pending_deliveries WHERE subscriber = $subscriber";
        command.Parameters.AddWithValue("$subscriber", subscriber.ToString());

        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> RemoveAllForTeamAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // The team half of a `Team/Name` key, matched NOCASE because ContainerId equality is.
        // `instr` answers 0 with no slash and `substr(s, 1, -1)` is empty rather than an error, so
        // the guard stops an unqualified row matching an empty team name and being deleted.
        command.CommandText =
            """
            DELETE FROM pending_deliveries
            WHERE instr(subscriber, '/') > 0
              AND substr(subscriber, 1, instr(subscriber, '/') - 1) = $team COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$team", team);

        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PendingDelivery>> ForAsync(
        ContainerId subscriber, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT seq, started, deferred_from_run FROM pending_deliveries
            WHERE subscriber = $subscriber
            ORDER BY seq
            """;
        command.Parameters.AddWithValue("$subscriber", subscriber.ToString());

        var rows = new List<PendingDelivery>();

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PendingDelivery(
                reader.GetInt64(0), reader.GetInt64(1) != 0, reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<TeamPendingDelivery>> ForTeamAsync(
        string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT subscriber, seq, started, deferred_from_run FROM pending_deliveries
            WHERE substr(subscriber, 1, length($teamPrefix)) = $teamPrefix COLLATE NOCASE
            ORDER BY subscriber, seq
            """;
        command.Parameters.AddWithValue("$teamPrefix", $"{team}/");

        var rows = new List<TeamPendingDelivery>();

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TeamPendingDelivery(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2) != 0,
                reader.IsDBNull(3) ? null : reader.GetInt64(3)));
        }

        return rows;
    }

    private async Task ExecuteAsync(string sql, ContainerId subscriber, long seq, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = sql;
        command.Parameters.AddWithValue("$subscriber", subscriber.ToString());
        command.Parameters.AddWithValue("$seq", seq);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Every connection sets busy_timeout again: Pooling=false makes each Open() a
    /// brand-new connection that starts with no pragmas at all.</summary>
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        SqlitePragmas.Apply(connection, durability: _durability);

        return connection;
    }
}
