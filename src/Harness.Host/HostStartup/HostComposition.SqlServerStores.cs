using Harness.Contracts;
using Harness.Messaging;
using Harness.SqlServer;
using Harness.SqlServer.Ledger;
using Harness.SqlServer.Messaging;

namespace Harness.Host;

internal sealed partial class HostComposition
{
    /// <summary>
    /// THE SQL SERVER STORES, registered one module at a time as each is written. Each registration
    /// adds its interface to <see cref="SqlServerStoreRegistry.Registered"/> beside its
    /// `AddSingleton`; the guard at the end then refuses to start while any interface the Host
    /// resolves (<see cref="StoreContracts.Required"/>) is still without one.
    /// </summary>
    private void SqlServerStores()
    {
        var connection = databaseOptions.Connection!;

        // THE LOG, THE CURSORS AND THE SUBSCRIPTIONS: one instance behind the three, as on SQLite, with
        // the same in-process wait registry handed every appended row once it commits.
        //
        // EVERY APPEND WRITES ITS LEDGER AND OUTCOME ROWS IN ITS OWN TRANSACTION, and every accepted
        // delivery its record: the one writer, handed to both stores that append.
        var appendRows = new SqlServerAppendRows();
        var workflowWaits = new MessageWaitRegistry();
        var messages = new SqlServerMessageStore(connection, appendRows, workflowWaits);

        builder.Services.AddSingleton<IMessageLog>(messages);
        builder.Services.AddSingleton<ICursors>(messages);
        builder.Services.AddSingleton<ISubscriptions>(messages);
        builder.Services.AddSingleton(workflowWaits);
        builder.Services.AddSingleton<IPendingDeliveries>(new SqlServerPendingDeliveries(connection, appendRows));
        builder.Services.AddSingleton<IWakeStore>(new SqlServerWakeStore(connection));

        // THE ACCOUNTING AND THE OUTCOMES, over the same database: the ledger's reads, the admission
        // holds, the outcome store (each person's write with its tenant row, through the SQL Server
        // spelling of the tenant log's one insert), the log's old spend reads, the retention's choice,
        // and the ledger as a member's memory, which leaves out the installed plugins' high-volume events.
        builder.Services.AddSingleton<IUsageLedger>(new SqlServerUsageLedger(connection));
        builder.Services.AddSingleton<IAdmissionHoldLedger>(new SqlServerAdmissionHolds(connection));
        builder.Services.AddSingleton<IOutcomeStore>(new SqlServerOutcomeStore(connection, SqlServerTenantAuditRow.AppendAsync));
        builder.Services.AddSingleton<IOutcomeLinkRows>(new SqlServerOutcomeLinkRows());
        builder.Services.AddSingleton<ILogSpend>(new SqlServerLogSpend(connection));
        builder.Services.AddSingleton<ILogRetention>(new SqlServerLogRetention(connection));
        builder.Services.AddSingleton<ILedger>(sp => new SqlServerLedger(connection, sp.GetRequiredService<PluginCatalog>()));

        // THE START STEPS' ROWS, which the operator phase has already run (`StartRowsFor`); registered so
        // the guard counts them and a later caller resolves the same provider's.
        builder.Services.AddSingleton<ILedgerStartRows>(new Harness.Host.SqlServer.SqlServerLedgerStartRows(connection));
        builder.Services.AddSingleton<IOutcomeStartRows>(new Harness.Host.SqlServer.SqlServerOutcomeStartRows(connection));

        StoreGuard.Check(StoreContracts.Required, SqlServerStoreRegistry.Registered);
    }
}
