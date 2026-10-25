using Harness.Contracts;
using Harness.SqlServer;
using Harness.SqlServer.Ledger;
using Microsoft.Data.SqlClient;

namespace Harness.Host.SqlServer;

/// <summary>
/// The SQL Server version of <see cref="IOutcomeStartRows"/>: <see cref="SqliteOutcomeStartRows"/>
/// statement for statement - each backlog item with no outcome takes its newest dispatch workflow's,
/// with its tenant row, and the done row is written, in one transaction. <see cref="BacklogOutcomeStart"/>
/// keeps the names and the rows' wording.
///
/// <para>
/// HERE IN THE HOST, beside its SQLite twin, though its interface is in Contracts: the names it writes
/// (<see cref="BacklogOutcomeStart.DoneName"/>, <see cref="LedgerStart.Actor"/>) are the Host's, and
/// Harness.SqlServer cannot reference the Host.
/// </para>
///
/// <para>
/// THE DONE ROW IS READ UNDER <c>UPDLOCK, HOLDLOCK</c> and the items under the same, so two Hosts
/// started at once cannot both fill an item or both write the row; the second waits and finds it.
/// </para>
/// </summary>
public sealed class SqlServerOutcomeStartRows(string connectionString) : IOutcomeStartRows
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task<int?> RunAsync(CancellationToken ct = default) =>
        SqlServerOpen.RunAsync<int?>(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

                await using (var done = connection.CreateCommand())
                {
                    done.Transaction = transaction;
                    done.CommandText = "SELECT 1 FROM tenant_settings WITH (UPDLOCK, HOLDLOCK) WHERE name = @name";
                    done.Parameters.AddWithValue("@name", BacklogOutcomeStart.DoneName);
                    if (await done.ExecuteScalarAsync(token) is not null) return null;
                }

                var candidates = new List<(string Id, string Title, long Correlation)>();
                await using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText =
                        """
                        SELECT i.id, i.title, d.correlation
                        FROM backlog_items i WITH (UPDLOCK, HOLDLOCK)
                        JOIN backlog_dispatches d ON d.id = (SELECT MAX(n.id) FROM backlog_dispatches n WHERE n.item = i.id)
                        WHERE i.outcome_id IS NULL
                        ORDER BY i.id
                        """;
                    await using var reader = await read.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
                }

                var filled = 0;
                foreach (var (id, title, correlation) in candidates)
                {
                    if (await SqlServerOutcomeLinks.CurrentAsync(connection, transaction, correlation, token) is not { OutcomeId: { } linked } link) continue;
                    if (await SqlServerOutcomeLinks.ResolveAsync(connection, transaction, linked, token) is not { } resolved) continue;

                    await using (var update = connection.CreateCommand())
                    {
                        update.Transaction = transaction;
                        update.CommandText = "UPDATE backlog_items SET outcome_id = @outcome WHERE id = @id AND outcome_id IS NULL";
                        update.Parameters.AddWithValue("@outcome", resolved.Id);
                        update.Parameters.AddWithValue("@id", id);
                        await update.ExecuteNonQueryAsync(token);
                    }

                    await SqlServerOutcomeLinks.AppendInheritedRowAsync(
                        connection, transaction, id, title, resolved.Id, link.OutcomeNameAtLink, correlation,
                        LedgerStart.Actor, OutcomeActorKind.Platform, link.Id, BacklogOutcomeStart.How, token);
                    filled++;
                }

                var now = Instant.Now();

                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        "INSERT INTO tenant_settings (name, value, updated_at, updated_by) VALUES (@name, @value, @at, @by)";
                    insert.Parameters.AddWithValue("@name", BacklogOutcomeStart.DoneName);
                    insert.Parameters.AddWithValue("@value", now);
                    insert.Parameters.AddWithValue("@at", now);
                    insert.Parameters.AddWithValue("@by", LedgerStart.Actor);
                    await insert.ExecuteNonQueryAsync(token);
                }

                await SqlServerTenantAuditRow.AppendAtAsync(
                    connection, transaction, BacklogOutcomeStart.DoneAudit(now, filled), Instant.Parse(now), token);

                await transaction.CommitAsync(token);
                return filled;
            },
            ct);
}
