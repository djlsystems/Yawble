using Harness.Contracts;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Messaging;

/// <summary>
/// WHAT A SQL SERVER APPEND WRITES BESIDE ITS ROW, IN THE APPEND'S OWN TRANSACTION: the SQL Server
/// counterpart of the two calls <c>SqliteMessageStore</c> makes after its insert
/// (<c>LedgerRows.WriteAsync</c>, then <c>OutcomeLinks.OnAppendAsync</c>) and of the one
/// <c>SqlitePendingDeliveries.AddAsync</c> makes after its row (<c>LedgerRows.WriteDeliveryAsync</c>).
///
/// <para>
/// HANDED THE CONNECTION AND THE TRANSACTION, so a ledger or outcome row commits or rolls back with
/// the row it accounts for, exactly as on SQLite: when one cannot be written this throws and neither
/// is stored. The log's append lock is already held when <see cref="OnAppendAsync"/> runs.
/// </para>
///
/// <para>
/// REQUIRED BY BOTH STORES: an append that skipped it would leave a run unbilled and a root unlinked,
/// which SQLite's one insert path cannot do. The ledger module's <c>SqlServerAppendRows</c> is the
/// implementation the Host passes.
/// </para>
/// </summary>
public interface ISqlServerAppendRows
{
    /// <summary>After <paramref name="stored"/> is inserted (its seq, correlation and depth settled).</summary>
    Task OnAppendAsync(SqlConnection connection, SqlTransaction transaction, Message stored, CancellationToken ct);

    /// <summary>After a delivery of <paramref name="deliverySeq"/> is accepted onto <c>pending_deliveries</c>.</summary>
    Task OnDeliveryAsync(SqlConnection connection, SqlTransaction transaction, long deliverySeq, CancellationToken ct);
}
