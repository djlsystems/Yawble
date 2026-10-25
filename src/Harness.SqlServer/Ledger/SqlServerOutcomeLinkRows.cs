using System.Data.Common;
using Harness.Contracts;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// The SQL Server <see cref="IOutcomeLinkRows"/>: <see cref="SqlServerOutcomeLinks.WriteAsync"/> and
/// then <see cref="SqlServerTenantAuditRow.AppendAsync"/>, in the append's transaction, as SQLite's.
/// </summary>
public sealed class SqlServerOutcomeLinkRows : IOutcomeLinkRows
{
    public async Task WriteWithinAsync(
        DbConnection connection, DbTransaction transaction, long correlation, string outcomeId, string? team,
        string setBy, string setByKind, string how, TriggerAudit audit, CancellationToken ct)
    {
        var sqlServer = (SqlConnection)connection;
        var within = (SqlTransaction)transaction;

        await SqlServerOutcomeLinks.WriteAsync(sqlServer, within, correlation, outcomeId, team, setBy, setByKind, how, ct);
        await SqlServerTenantAuditRow.AppendAsync(sqlServer, within, audit, ct);
    }
}
