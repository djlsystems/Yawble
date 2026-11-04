using System.Data;
using System.Data.Common;

namespace Harness.Contracts;

/// <summary>
/// THE ONE INSERT INTO <c>intervention_ledger</c> ON SQLITE, inside the caller's transaction: the log's
/// append (a message's act, <see cref="AppendContext.Intervention"/>) and the tenant log's
/// (<see cref="TenantAuditRow"/>, <see cref="TriggerAudit.Intervention"/>) both write through it, so the
/// act lands with its log row or not at all. <c>SqlServerInterventionLedgerRow</c> is its twin, in
/// <c>@</c> parameters.
///
/// <para>
/// A KEY'S OWNER IS STORED AS THEY STOOD: an act with no email reads it from <c>users</c> in this same
/// statement, so the row keeps it after the key is revoked and the owner deleted. The workflow's outcome
/// is its newest link at this moment. No content, prompt, answer, secret or connection value is a column.
/// </para>
/// </summary>
public static class InterventionLedgerRow
{
    /// <summary>The statement, shared by both providers but for the parameter prefix and the one-row limit.</summary>
    public static string Insert(char prefix, bool top) =>
        $"""
         INSERT INTO intervention_ledger
             (occurred_at, actor_id, actor_email, kind, team_id, correlation, outcome_id_at_event,
              related_seq, duration_seconds, duration_measured)
         VALUES (
             {prefix}at, {prefix}actor,
             COALESCE({prefix}email, (SELECT u.email FROM users u WHERE u.id = {prefix}actor)),
             {prefix}kind, {prefix}team, {prefix}correlation,
             CASE WHEN {prefix}correlation IS NULL THEN NULL ELSE (
                 SELECT {(top ? "TOP 1 " : "")}l.outcome_id FROM workflow_outcome_links l
                 WHERE l.correlation = {prefix}correlation ORDER BY l.id DESC{(top ? "" : " LIMIT 1")}) END,
             {prefix}related, {prefix}duration, {prefix}measured)
         """;

    /// <summary>Appends <paramref name="act"/> at <paramref name="at"/>, for the workflow
    /// <paramref name="correlation"/> and the log row <paramref name="relatedSeq"/> (each null when there is none).</summary>
    public static Task AppendAsync(
        DbConnection connection, DbTransaction? transaction, InterventionAct act, long? correlation, long? relatedSeq,
        DateTimeOffset at, CancellationToken ct) =>
        WriteAsync(connection, transaction, Insert('$', top: false), '$', act, correlation, relatedSeq, at, ct);

    /// <summary>The write itself, for either provider's statement and prefix.</summary>
    public static async Task WriteAsync(
        DbConnection connection, DbTransaction? transaction, string sql, char prefix, InterventionAct act,
        long? correlation, long? relatedSeq, DateTimeOffset at, CancellationToken ct)
    {
        act.Checked();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        Add(command, prefix + "at", DbType.String, Instant.Stamp(at));
        Add(command, prefix + "actor", DbType.String, act.ActorId);
        Add(command, prefix + "email", DbType.String, act.ActorEmail);
        Add(command, prefix + "kind", DbType.String, act.Kind);
        Add(command, prefix + "team", DbType.String, act.Team);
        Add(command, prefix + "correlation", DbType.Int64, correlation);
        Add(command, prefix + "related", DbType.Int64, relatedSeq);
        Add(command, prefix + "duration", DbType.Int64, act.DurationSeconds);
        Add(command, prefix + "measured", DbType.Int32, act.DurationMeasured ? 1 : 0);

        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Add(DbCommand command, string name, DbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
