using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>
/// THE ONE-TIME BACK-FILL OF BACKLOG ITEMS' OUTCOMES. A dispatched item now takes the outcome its
/// dispatch workflow is linked to (<see cref="OutcomeLinks"/>), but items dispatched before that
/// were linked only on the workflow. At the first start after the change, each item with no outcome
/// takes its NEWEST dispatch workflow's current outcome - the newest link, an unlink being none,
/// followed through <c>merged_into</c> - with its <c>backlog.item-outcome-inherited</c> row (actor
/// the platform), and <c>backlog.outcomesInheritedAt</c> is written with its own tenant row in the
/// same transaction. That row marks it done: a second start adds nothing, and a start that failed
/// part way tries again. Never a setting a person can change: <see cref="TenantSettings"/> does not
/// define it.
/// </summary>
public static class BacklogOutcomeStart
{
    public const string DoneName = "backlog.outcomesInheritedAt";

    /// <summary>The SQLite start, kept as one call for the operator paths and tests that hold a path.</summary>
    public static Task<int?> RunAsync(string databasePath, CancellationToken ct = default) =>
        new SqliteOutcomeStartRows(databasePath).RunAsync(ct);

    /// <summary>What the inherited item's tenant row says made the link it took.</summary>
    public const string How = "start back-fill";

    /// <summary>The tenant row the done row is written with, counting the items filled.</summary>
    public static TriggerAudit DoneAudit(string now, int filled) =>
        new(
            null, LedgerStart.Actor, TenantActions.TenantSettingChanged, DoneName, DoneName,
            JsonSerializer.Serialize(new { setting = DoneName, old = (string?)null, @new = now, items = filled }));
}

/// <summary>
/// The SQLite version of <see cref="IOutcomeStartRows"/>: the back-fill's SQL, moved here unchanged
/// from <see cref="BacklogOutcomeStart"/>, which keeps the name of the row that marks it done.
/// </summary>
public sealed class SqliteOutcomeStartRows(string databasePath) : IOutcomeStartRows
{
    /// <summary>How many items it filled, or null when it had already run.</summary>
    public async Task<int?> RunAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(
            SqliteOpen.ConnectionString(databasePath));
        await connection.OpenAsync(ct);
        await SqlitePragmas.ApplyAsync(connection, ct: ct);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var done = connection.CreateCommand())
        {
            done.Transaction = transaction;
            done.CommandText = "SELECT 1 FROM tenant_settings WHERE name = $name";
            done.Parameters.AddWithValue("$name", BacklogOutcomeStart.DoneName);
            if (await done.ExecuteScalarAsync(ct) is not null) return null;
        }

        var candidates = new List<(string Id, string Title, long Correlation)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT i.id, i.title, d.correlation
                FROM backlog_items i
                JOIN backlog_dispatches d ON d.id = (SELECT MAX(n.id) FROM backlog_dispatches n WHERE n.item = i.id)
                WHERE i.outcome_id IS NULL
                ORDER BY i.id
                """;
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }

        var filled = 0;
        foreach (var (id, title, correlation) in candidates)
        {
            if (await OutcomeLinks.CurrentAsync(connection, transaction, correlation, ct) is not { OutcomeId: { } linked } link) continue;
            if (await OutcomeLinks.ResolveAsync(connection, transaction, linked, ct) is not { } resolved) continue;

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE backlog_items SET outcome_id = $outcome WHERE id = $id AND outcome_id IS NULL";
                update.Parameters.AddWithValue("$outcome", resolved.Id);
                update.Parameters.AddWithValue("$id", id);
                await update.ExecuteNonQueryAsync(ct);
            }

            await OutcomeLinks.AppendInheritedRowAsync(
                connection, transaction, id, title, resolved.Id, link.OutcomeNameAtLink, correlation,
                LedgerStart.Actor, OutcomeActorKind.Platform, link.Id, BacklogOutcomeStart.How, ct);
            filled++;
        }

        var now = Instant.Now();

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO tenant_settings (name, value, updated_at, updated_by) VALUES ($name, $value, $at, $by)";
            insert.Parameters.AddWithValue("$name", BacklogOutcomeStart.DoneName);
            insert.Parameters.AddWithValue("$value", now);
            insert.Parameters.AddWithValue("$at", now);
            insert.Parameters.AddWithValue("$by", LedgerStart.Actor);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAtAsync(
            connection, transaction, BacklogOutcomeStart.DoneAudit(now, filled), Instant.Parse(now), ct);

        await transaction.CommitAsync(ct);
        return filled;
    }
}
