using Harness.Contracts;
using Harness.SqlServer.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// WHAT EVERY SQL SERVER APPEND WRITES BESIDE ITS ROW: the ledger row (<see cref="SqlServerLedgerRows"/>)
/// and then the root's outcome link (<see cref="SqlServerOutcomeLinks"/>), in that order, as
/// <c>SqliteMessageStore</c>'s insert calls <c>LedgerRows.WriteAsync</c> and then
/// <c>OutcomeLinks.OnAppendAsync</c>; and the delivery's record when <c>pending_deliveries</c> accepts
/// one. Every call is in the caller's transaction, so a row that cannot be written takes the log row
/// with it.
/// </summary>
public sealed class SqlServerAppendRows : ISqlServerAppendRows
{
    public async Task OnAppendAsync(SqlConnection connection, SqlTransaction transaction, Message stored, CancellationToken ct)
    {
        await SqlServerLedgerRows.WriteAsync(connection, transaction, stored, backfilled: false, ct);
        await SqlServerOutcomeLinks.OnAppendAsync(connection, transaction, stored, ct);
    }

    public Task OnDeliveryAsync(SqlConnection connection, SqlTransaction transaction, long deliverySeq, CancellationToken ct) =>
        SqlServerLedgerRows.WriteDeliveryAsync(connection, transaction, deliverySeq, ct);
}
