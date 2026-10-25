using Harness.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Harness.Messaging;
using Harness.Identity;
using Harness.SqlServer;
using Harness.Host.SqlServer;
using Harness.SqlServer.Backlog;
using Harness.SqlServer.Host;
using Harness.SqlServer.Identity;
using Harness.SqlServer.Ledger;
using Harness.SqlServer.Messaging;
using Harness.SqlServer.Skills;

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

        // THE ACCOUNTS, THE TEAMS AND THE TENANT'S OWN RECORDS: people and their password hashes, machine
        // principals and their credential hashes (each the same text SQLite stores, so either verifies the
        // other's), teams and members, the tenant log, the instance-wide settings rows and the folders a
        // removal left. Every write that carries a tenant row lands with it in one transaction.
        builder.Services.AddSingleton<IUserStore>(new SqlServerUserStore(connection));
        builder.Services.AddSingleton<IPrincipalStore>(new SqlServerPrincipalStore(connection));
        builder.Services.AddSingleton<ITeamStore>(new SqlServerTeamStore(connection));
        builder.Services.AddSingleton<ITenantLog>(new SqlServerTenantLog(connection));
        builder.Services.AddSingleton<ITenantSettingsStore>(new SqlServerTenantSettingsStore(connection));
        builder.Services.AddSingleton<IUnfinishedRemovals>(new SqlServerUnfinishedRemovals(connection));

        // A TEAM'S TRIGGERS, SITES AND SOLUTION ROW, AND THE DIAGNOSTICS LOG: each person's change with its
        // tenant row, a site's data writes checked against its limits one at a time, and the diagnostics
        // log bounded as on SQLite, by the data root's volume or the operator's settings.
        builder.Services.AddSingleton<ITriggerStore>(new SqlServerTriggerStore(connection));
        builder.Services.AddSingleton<ISiteStore>(new SqlServerSiteStore(connection));
        builder.Services.AddSingleton<ITeamSolutionStore>(new SqlServerTeamSolutionStore(connection));

        diagnosticsRetention = DiagnosticsRetention();
        builder.Services.AddSingleton<IDiagnosticsLog>(new SqlServerDiagnosticsLog(connection, diagnosticsRetention));

        // THE SKILL INDEX AND THE BACKLOG: a team skill's write and a person's backlog change each land with
        // its tenant row in one transaction, the backlog's through the SQL Server spelling of the tenant
        // log's one insert, as on SQLite.
        builder.Services.AddSingleton<ISkillStore>(new SqlServerSkillStore(connection));
        builder.Services.AddSingleton<IBacklogStore>(new SqlServerBacklogStore(connection, audit: SqlServerTenantAuditRow.AppendAsync));

        // THE HOST'S OWN ROWS: the Monitor's steps and file changes, a plugin member's settings and secret
        // values, the plugin keys a person set, a plugin removal's tenant row, and the OAuth and mailbox
        // connections. Every credential is the ciphertext the shared classes seal (`PluginSecretStore`,
        // `PluginMemberSecretStore`, `ConnectionCipher`), stored as the same text SQLite holds, and every
        // person's write lands with its tenant row in one transaction.
        builder.Services.AddSingleton<IAgentStepStore>(new SqlServerAgentStepStore(connection));
        builder.Services.AddSingleton<IWorkspaceFileChangeStore>(new SqlServerWorkspaceFileChangeStore(connection));
        builder.Services.AddSingleton<IPluginMemberSettingsStore>(new SqlServerPluginMemberSettings(connection));
        builder.Services.AddSingleton<IPluginMemberSecretRows>(new SqlServerPluginMemberSecretRows(connection));
        builder.Services.AddSingleton<IPluginSecretRows>(new SqlServerPluginSecretRows(connection));
        builder.Services.AddSingleton<IPluginRemovalRows>(new SqlServerPluginRemovalRows(connection));
        builder.Services.AddSingleton<IConnectionStore>(sp => new SqlServerConnectionStore(connection, sp.GetRequiredService<IDataProtectionProvider>()));

        // THE DATABASE ITSELF: whether it answers (`SELECT 1`), and the housekeeping SQLite's file needs,
        // which here is nothing - the checkpoint is a no-op and the daily copy says that point-in-time
        // restore is the backup instead.
        builder.Services.AddSingleton<IDatabaseHealth>(new SqlServerDatabaseHealth(connection));
        builder.Services.AddSingleton<IDatabaseMaintenance>(new SqlServerDatabaseMaintenance());

        StoreGuard.Check(StoreContracts.Required, SqlServerStoreRegistry.Registered);
    }
}
