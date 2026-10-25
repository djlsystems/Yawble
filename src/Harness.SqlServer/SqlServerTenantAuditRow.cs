using System.Data.Common;
using System.Globalization;
using Harness.Contracts;

namespace Harness.SqlServer;

/// <summary>
/// THE ONE INSERT INTO <c>tenant_events</c> ON SQL SERVER: <c>TenantAuditRow</c>, the same two calls
/// with the same signatures, its statement in <c>@</c> parameters. Every SQL Server store that lands a
/// person's change with its tenant row in the same transaction appends through this, so the column
/// list is written once per provider. The table is the identity module's.
/// </summary>
public static class SqlServerTenantAuditRow
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
            VALUES (@at, @actorId, @actorEmail, @action, @subject, @subjectName, @detail)
            """;

        Add(command, "@at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@actorId", audit.ActorId);
        Add(command, "@actorEmail", audit.ActorEmail);
        Add(command, "@action", audit.Action);
        Add(command, "@subject", audit.Subject);
        Add(command, "@subjectName", audit.SubjectName);
        Add(command, "@detail", audit.Detail);

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
