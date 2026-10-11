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

    /// <summary>How many items it filled, or null when it had already run.</summary>
    public static async Task<int?> RunAsync(string databasePath, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        await SqlitePragmas.ApplyAsync(connection, ct: ct);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var done = connection.CreateCommand())
        {
            done.Transaction = transaction;
            done.CommandText = "SELECT 1 FROM tenant_settings WHERE name = $name";
            done.Parameters.AddWithValue("$name", DoneName);
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
                LedgerStart.Actor, OutcomeActorKind.Platform, link.Id, "start back-fill", ct);
            filled++;
        }

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO tenant_settings (name, value, updated_at, updated_by) VALUES ($name, $value, $at, $by)";
            insert.Parameters.AddWithValue("$name", DoneName);
            insert.Parameters.AddWithValue("$value", now);
            insert.Parameters.AddWithValue("$at", now);
            insert.Parameters.AddWithValue("$by", LedgerStart.Actor);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText =
                """
                INSERT INTO tenant_events (occurred_at, actor_id, actor_email, action, subject, subject_name, detail)
                VALUES ($at, NULL, $by, $action, $name, $name, $detail)
                """;
            audit.Parameters.AddWithValue("$at", now);
            audit.Parameters.AddWithValue("$by", LedgerStart.Actor);
            audit.Parameters.AddWithValue("$action", TenantActions.TenantSettingChanged);
            audit.Parameters.AddWithValue("$name", DoneName);
            audit.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(new { setting = DoneName, old = (string?)null, @new = now, items = filled }));
            await audit.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return filled;
    }
}
