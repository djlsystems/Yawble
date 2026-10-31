using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;
using Harness.Containers;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Capacity;
using Harness.Host.Solutions;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;
using Harness.Skills;

namespace Harness.Host;

internal sealed partial class HostComposition
{
    // THE SQLITE STORES, the reference: moved here unchanged from StoresAsync, same order, same behaviour.
    internal async Task<bool> SqliteStoresAsync()
    {
        var workflowWaits = new MessageWaitRegistry();
        store = new SqliteMessageStore(database, workflowWaits);

        builder.Services.AddSingleton<IMessageLog>(store);
        builder.Services.AddSingleton<ICursors>(store);
        builder.Services.AddSingleton<ISubscriptions>(store);
        builder.Services.AddSingleton(workflowWaits);
        builder.Services.AddSingleton<IUsageLedger>(new SqliteUsageLedger(database));

        // The ledger reads the same file the store writes. A second READER, never a second writer - which
        // is what makes it impossible for the ledger to disagree with the log about what happened. The
        // installed plugins' high-volume events stay out of it, as `agentContainer.progress` does.
        builder.Services.AddSingleton<ILedger>(sp => new SqliteLedger(database, sp.GetRequiredService<PluginCatalog>()));

        await AdmissionHoldsAsync(new SqliteAdmissionHolds(database));

        // THE OUTCOMES AND THEIR LINKS, in the same file: every person's write lands with its tenant_events
        // row in one transaction, written with the tenant log's own columns.
        var outcomeStore = new SqliteOutcomeStore(database, TenantAuditRow.AppendAsync);
        builder.Services.AddSingleton<IOutcomeStore>(outcomeStore);
        builder.Services.AddSingleton<IOutcomeLinkRows>(new SqliteOutcomeLinkRows());
        OutcomesAndBoard(outcomeStore, store);

        Deliveries(new SqlitePendingDeliveries(database));

        // THE WAKES MEMBERS ASKED FOR (`wake_me`), stored so a restart keeps them. See `WakeMe`.
        builder.Services.AddSingleton<IWakeStore>(new SqliteWakeStore(database));

        // WHAT RUNNING MEMBERS' WORKTREES DID, recorded for the Monitor's replay. See WorkspaceFileChangeStore.
        // Registered as the class too: the Host's own tests resolve it by type. Consumers take the interface.
        var workspaceFileChanges = new WorkspaceFileChangeStore(database);
        builder.Services.AddSingleton(workspaceFileChanges);
        builder.Services.AddSingleton<IWorkspaceFileChangeStore>(workspaceFileChanges);

        // WHAT RUNNING AGENTS DID, step by step, recorded for the Monitor. See AgentStepStore.
        var agentSteps = new AgentStepStore(database);
        builder.Services.AddSingleton(agentSteps);
        builder.Services.AddSingleton<IAgentStepStore>(agentSteps);

        // THE REST OF WHAT TOUCHES THE FILE DIRECTLY, each behind its interface: the checks and sweeps that
        // used to take a path now take one of these.
        builder.Services.AddSingleton<IDatabaseHealth>(new SqliteDatabaseHealth(database));
        builder.Services.AddSingleton<IDatabaseMaintenance>(new SqliteDatabaseMaintenance(database));
        builder.Services.AddSingleton<IDatabaseTrouble>(SqliteDatabaseTrouble.Instance);
        builder.Services.AddSingleton<ILogRetention>(new SqliteLogRetention(database));
        builder.Services.AddSingleton<IPluginSecretRows>(new SqlitePluginSecretRows(database));
        builder.Services.AddSingleton<IPluginMemberSecretRows>(new SqlitePluginMemberSecretRows(database));
        builder.Services.AddSingleton<IPluginRemovalRows>(new SqlitePluginRemovalRows(database));
        builder.Services.AddSingleton<IPluginSecretMoveRows>(new SqlitePluginSecretMoveRows(database));

        // A plugin member's settings rows (`team_member_config`): the provider's own, so the plugin
        // composition below takes whichever database the Host runs on.
        builder.Services.AddSingleton<IPluginMemberSettingsStore>(new SqlitePluginMemberSettings(database));

        // OAuth and mailbox connections. Registered as the class too, for the same reason as above.
        builder.Services.AddSingleton(sp => new ConnectionStore(database, sp.GetRequiredService<IDataProtectionProvider>()));
        builder.Services.AddSingleton<IConnectionStore>(sp => sp.GetRequiredService<ConnectionStore>());
        builder.Services.AddSingleton<IMcpServerStore>(sp => new McpServerStore(database, sp.GetRequiredService<IDataProtectionProvider>()));

        // Order carries no schema obligation - SchemaMigrator above has already applied every step
        // and set journal_mode on the file. These are readers and writers over a database that exists.
        users = new SqliteUserStore(database);
        var principals = new SqlitePrincipalStore(database);

        builder.Services.AddSingleton<IUserStore>(users);
        builder.Services.AddSingleton<IPrincipalStore>(principals);

        if (!WorkerKey(principals)) return false;

        // Teams and their members, in the same file for the same reason accounts are - see AuthSchema's
        // remarks on why the non-message tables live together and why the Host does not own one directly.
        builder.Services.AddSingleton<ITeamStore>(new SqliteTeamStore(database));
        builder.Services.AddSingleton<ITriggerStore>(new SqliteTriggerStore(database));
        builder.Services.AddSingleton<ITenantLog>(new SqliteTenantLog(database));

        Sites(new SqliteSiteStore(database));

        // The folders a deletion or reset could not finish removing (auth-012), retried at start and on
        // request. See FolderRemoval.
        builder.Services.AddSingleton<IUnfinishedRemovals>(new SqliteUnfinishedRemovals(database));

        await TenantSettingsAsync(new SqliteTenantSettingsStore(database));
        builder.Services.AddSingleton<ISkillStore>(new SqliteSkillStore(database));
        // A person's edit lands with its tenant_events row in one transaction, as the outcomes' writes do.
        builder.Services.AddSingleton<IBacklogStore>(
            new SqliteBacklogStore(database, audit: TenantAuditRow.AppendAsync));

        // THE DIAGNOSTICS LOG (see Records). THE BOUND IS RESOLVED HERE because this is the one place that
        // knows the data root, and it is
        // DECLARED-OR-DERIVED rather than a constant: absent settings derive a row count from the size of
        // the volume this instance sits on, so a small host keeps a shorter log instead of filling its
        // disk, and nothing refuses. See DiagnosticRetention.
        //
        // Constructed rather than resolved by type, like every other store above it: its parameters are a
        // path and a bound, neither of which is a service.
        diagnosticsRetention = DiagnosticsRetention();

        diagnostics = new SqliteDiagnosticsLog(database, diagnosticsRetention);

        Records(diagnostics);

        // WHICH SOLUTION PACKAGE EACH TEAM CAME FROM: the rows the solutions phase loads its index over.
        teamSolutionRows = new SqliteTeamSolutionStore(database);

        return true;
    }

    /// <summary>The diagnostics log's bound, declared or derived from the data root's volume, for either
    /// database: see the remarks where the SQLite log is built.</summary>
    private DiagnosticRetention DiagnosticsRetention() =>
        DiagnosticRetention.ForDataRoot(
            dataRoot,
            builder.Configuration.GetValue<int?>("Diagnostics:RetentionDays"),
            builder.Configuration.GetValue<long?>("Diagnostics:MaxRows"));
}
