using Harness.Contracts;
using Harness.Messaging;
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
        var workflowWaits = new MessageWaitRegistry();
        var messages = new SqlServerMessageStore(connection, workflowWaits);

        builder.Services.AddSingleton<IMessageLog>(messages);
        builder.Services.AddSingleton<ICursors>(messages);
        builder.Services.AddSingleton<ISubscriptions>(messages);
        builder.Services.AddSingleton(workflowWaits);
        builder.Services.AddSingleton<IPendingDeliveries>(new SqlServerPendingDeliveries(connection));
        builder.Services.AddSingleton<IWakeStore>(new SqlServerWakeStore(connection));

        StoreGuard.Check(StoreContracts.Required, SqlServerStoreRegistry.Registered);
    }
}
