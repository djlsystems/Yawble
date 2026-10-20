using System.Data.Common;
using System.Globalization;

namespace Harness.Contracts;

/// <summary>
/// THE ONE INSERT INTO <c>tenant_events</c>. Every store that lands a person's change with its
/// tenant row in the same transaction appends through this, as does the tenant log's own write;
/// six hand-written copies of the statement were six places for a column to drift. Over
/// <see cref="DbConnection"/>, so the stores in every assembly share it without this one
/// referencing the SQLite package.
/// </summary>
public static class TenantAuditRow
{
    /// <summary>Appends <paramref name="audit"/> now, inside <paramref name="transaction"/> when given.</summary>
    public static Task AppendAsync(DbConnection connection, DbTransaction? transaction, TriggerAudit audit, CancellationToken ct) =>
        AppendAtAsync(connection, transaction, audit, DateTimeOffset.UtcNow, ct);

    /// <summary>Appends <paramref name="audit"/> stamped <paramref name="at"/>: for a change whose own record carries the same stamp.</summary>
    public static async Task AppendAtAsync(
        DbConnection connection, DbTransaction? transaction, TriggerAudit audit, DateTimeOffset at, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO tenant_events
                (occurred_at, actor_id, actor_email, action, subject, subject_name, detail)
            VALUES ($at, $actorId, $actorEmail, $action, $subject, $subjectName, $detail)
            """;

        Add(command, "$at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$actorId", audit.ActorId);
        Add(command, "$actorEmail", audit.ActorEmail);
        Add(command, "$action", audit.Action);
        Add(command, "$subject", audit.Subject);
        Add(command, "$subjectName", audit.SubjectName);
        Add(command, "$detail", audit.Detail);

        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Add(DbCommand command, string name, string? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = (object?)value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
