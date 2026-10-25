using System.Data.Common;
using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Messaging;

/// <summary>
/// The SQLite <see cref="IOutcomeLinkRows"/>: <see cref="OutcomeLinks.WriteAsync"/> and then
/// <see cref="TenantAuditRow.AppendAsync"/>, the two calls the tell route made itself before.
/// </summary>
public sealed class SqliteOutcomeLinkRows : IOutcomeLinkRows
{
    public async Task WriteWithinAsync(
        DbConnection connection, DbTransaction transaction, long correlation, string outcomeId, string? team,
        string setBy, string setByKind, string how, TriggerAudit audit, CancellationToken ct)
    {
        var sqlite = (SqliteConnection)connection;
        var within = (SqliteTransaction)transaction;

        await OutcomeLinks.WriteAsync(sqlite, within, correlation, outcomeId, team, setBy, setByKind, how, ct);
        await TenantAuditRow.AppendAsync(sqlite, within, audit, ct);
    }
}
