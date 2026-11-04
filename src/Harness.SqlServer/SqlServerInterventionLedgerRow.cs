using System.Data.Common;
using Harness.Contracts;

namespace Harness.SqlServer;

/// <summary>
/// THE ONE INSERT INTO <c>intervention_ledger</c> ON SQL SERVER: <see cref="InterventionLedgerRow"/>, the
/// same call with the same signature, its statement in <c>@</c> parameters and <c>TOP 1</c>. The SQL
/// Server log's append and <see cref="SqlServerTenantAuditRow"/> write through it, in the caller's
/// transaction.
/// </summary>
public static class SqlServerInterventionLedgerRow
{
    private static readonly string Sql = InterventionLedgerRow.Insert('@', top: true);

    public static Task AppendAsync(
        DbConnection connection, DbTransaction? transaction, InterventionAct act, long? correlation, long? relatedSeq,
        DateTimeOffset at, CancellationToken ct) =>
        InterventionLedgerRow.WriteAsync(connection, transaction, Sql, '@', act, correlation, relatedSeq, at, ct);
}
