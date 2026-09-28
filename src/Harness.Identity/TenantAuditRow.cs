using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// One `tenant_events` row, inside the caller's transaction, so a settings write and its record
/// land together or not at all. The columns and the timestamp format are the tenant log's own.
/// </summary>
public static class TenantAuditRow
{
    public static async Task AppendAsync(
        SqliteConnection connection, SqliteTransaction transaction, TriggerAudit audit, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO tenant_events
                (occurred_at, actor_id, actor_email, action, subject, subject_name, detail)
            VALUES ($at, $actorId, $actorEmail, $action, $subject, $subjectName, $detail)
            """;

        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$actorId", (object?)audit.ActorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$actorEmail", (object?)audit.ActorEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", audit.Action);
        command.Parameters.AddWithValue("$subject", (object?)audit.Subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$subjectName", (object?)audit.SubjectName ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)audit.Detail ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);
    }
}
