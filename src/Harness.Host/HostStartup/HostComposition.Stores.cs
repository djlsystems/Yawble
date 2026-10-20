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
    internal async Task<bool> StoresAsync()
    {
        var workflowWaits = new MessageWaitRegistry();
        store = new SqliteMessageStore(database, workflowWaits);

        builder.Services.AddSingleton<IMessageLog>(store);
        builder.Services.AddSingleton<ICursors>(store);
        builder.Services.AddSingleton<ISubscriptions>(store);
        builder.Services.AddSingleton(workflowWaits);
        builder.Services.AddSingleton<IUsageLedger>(new SqliteUsageLedger(database));

        // EVERY ADMISSION HOLD, written down by admission as it happens (`admission_holds`), so the team
        // activity read can show a wait for a slot with its reason after the fact. The writer queues; a
        // write that fails is logged and admission carries on.
        var admissionHolds = new SqliteAdmissionHolds(database);
        admissionHoldWriter = new AdmissionHoldWriter(admissionHolds);

        // A HOST START CLOSES WHAT IT DID NOT SEE END: a hold still open belonged to a Host that went down
        // while the run waited. It is closed at this start, its reason unchanged, marked unfinished. A
        // failure is printed and the Host starts anyway; the rows stay open for the next start.
        try
        {
            if (await admissionHolds.CloseUnfinishedAsync(DateTimeOffset.UtcNow) is > 0 and var closed)
            {
                Console.WriteLine($"Closed {closed} admission hold(s) left open by the last run of this Host, marked unfinished.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"The admission holds left open could not be closed and are tried again at the next start: {exception.Message}");
        }
        builder.Services.AddSingleton<IAdmissionHoldLedger>(admissionHolds);
        builder.Services.AddSingleton(admissionHoldWriter);

        // THE OUTCOMES AND THEIR LINKS, in the same file: every person's write lands with its tenant_events
        // row in one transaction, written with the tenant log's own columns.
        var outcomeStore = new SqliteOutcomeStore(database, TenantAuditRow.AppendAsync);
        builder.Services.AddSingleton(outcomeStore);
        builder.Services.AddSingleton<IOutcomeStore>(outcomeStore);

        // THE GATE, read through a delegate so a person's change applies to the next declaration.
        builder.Services.AddSingleton(sp => new OutcomeGate(
            () => sp.GetRequiredService<TenantSettings>().OutcomesRequireForCompletion, outcomeStore));

        // THE CONCIERGE'S MERGE, read through a delegate so turning it off refuses the next merge of a
        // Concierge session already running.
        builder.Services.AddSingleton(sp => new ConciergeMergeGate(
            () => sp.GetRequiredService<TenantSettings>().ConciergeMayMerge));

        // THE CONCIERGE'S ARCHIVE, read through a delegate for the same reason.
        builder.Services.AddSingleton(sp => new ConciergeArchiveGate(
            () => sp.GetRequiredService<TenantSettings>().ConciergeMayArchive));
        builder.Services.AddSingleton(new LedgerIdentity(ledgerStart?.InstanceId, ledgerStart?.LedgerStartedAt));
        // THE ONE BOARD PROJECTION, scoped by each team's floor through TeamRegistry (late-bound: the registry
        // is composed after the stores), and told of the log's purges by retention and Reset.
        builder.Services.AddSingleton(sp => new KanbanStore(store, team => sp.GetRequiredService<TeamRegistry>().FloorFor(team)));

        // THE ROLLUP'S TIMINGS PER TEAM, kept until that team's rows move, and told of purges the same way.
        builder.Services.AddSingleton(new RollupCache(store));

        // What each container has accepted and not finished. Without it nothing is durable: a container's
        // queue is an in-memory Channel and its cursor advances when a message is OFFERED, so a host
        // stopping between the two would drop the work with no redelivery to fall back on and nothing to
        // say it had happened.
        builder.Services.AddSingleton<IPendingDeliveries>(new SqlitePendingDeliveries(database));
        builder.Services.AddSingleton<PendingDeliveriesAtStart>();

        // THE WAKES MEMBERS ASKED FOR (`wake_me`), stored so a restart keeps them. See `WakeMe`.
        builder.Services.AddSingleton<IWakeStore>(new SqliteWakeStore(database));

        // WHAT RUNNING MEMBERS' WORKTREES DID, recorded for the Monitor's replay. See WorkspaceFileChangeStore.
        builder.Services.AddSingleton(new WorkspaceFileChangeStore(database));

        // WHAT RUNNING AGENTS DID, step by step, recorded for the Monitor. See AgentStepStore.
        builder.Services.AddSingleton(new AgentStepStore(database));

        // Order carries no schema obligation - SchemaMigrator above has already applied every step
        // and set journal_mode on the file. These are readers and writers over a database that exists.
        users = new SqliteUserStore(database);
        var principals = new SqlitePrincipalStore(database);

        builder.Services.AddSingleton<IUserStore>(users);
        builder.Services.AddSingleton<IPrincipalStore>(principals);

        // THE WORKER KEY: what a worker process connects to control with (Workers:Key, or HARNESS_WORKER_KEY;
        // in an image, the file the entrypoint moved it to, Workers:KeyFile or HARNESS_WORKER_KEY_FILE).
        // Stored hashed as a principal with no permit, or removed when none is configured, so a key a
        // previous start stored never outlives its configuration. Taken on the worker connection alone.
        (workerKey, var workerKeyRefusal) = WorkerKeyFile.Read(
            builder.Configuration["Workers:Key"] ?? builder.Configuration["HARNESS_WORKER_KEY"],
            builder.Configuration["Workers:KeyFile"] ?? builder.Configuration[WorkerKeyFile.FileVariable]);
        if (workerKeyRefusal is not null)
        {
            Console.Error.WriteLine(workerKeyRefusal);
            Environment.ExitCode = 2;
            return false;
        }
        WorkerKeyGate.SetAsync(principals, workerKey).GetAwaiter().GetResult();
        workerKeys = new WorkerKeyGate(workerKey);
        builder.Services.AddSingleton(workerKeys);

        // Teams and their members, in the same file for the same reason accounts are - see AuthSchema's
        // remarks on why the non-message tables live together and why the Host does not own one directly.
        builder.Services.AddSingleton<ITeamStore>(new SqliteTeamStore(database));
        builder.Services.AddSingleton<ITriggerStore>(new SqliteTriggerStore(database));
        builder.Services.AddSingleton<ITenantLog>(new SqliteTenantLog(database));

        // A team's sites: their versions and their data (auth-015), and the service every caller - the
        // browser routes, a plugin's records, the team deletion - goes through. See SiteService.
        builder.Services.AddSingleton<ISiteStore>(new SqliteSiteStore(database));
        // The team lookup is LATE: TeamRegistry needs the member runtime, whose plugin runner needs this.
        builder.Services.AddSingleton(sp => new SiteService(
            sp.GetRequiredService<ISiteStore>(),
            team => sp.GetRequiredService<TeamRegistry>().ExistingName(team),
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<IMessageLog>(),
            removal: sp.GetRequiredService<FolderRemoval>()));
        builder.Services.AddSingleton<SiteCapability>();

        // The folders a deletion or reset could not finish removing (auth-012), retried at start and on
        // request. See FolderRemoval.
        builder.Services.AddSingleton<IUnfinishedRemovals>(new SqliteUnfinishedRemovals(database));

        // INSTANCE-WIDE SETTINGS, read once here - rows over appsettings.json over built-in defaults -
        // and then held in memory. Every consumer below is handed a delegate onto this, never a number, so
        // a person's change applies at its next use with no restart. See `TenantSettings`.
        tenantSettings = new TenantSettings(new SqliteTenantSettingsStore(database), builder.Configuration);
        await tenantSettings.LoadAsync();
        builder.Services.AddSingleton(tenantSettings);

        // "System packages" is installed by the entrypoint before the host starts, so the host keeps a
        // plain copy of the list on the volume for it: now, and on every change. See `SystemPackages`.
        SystemPackages.WriteFile(dataRoot, tenantSettings.SystemPackages);
        tenantSettings.Changed += name =>
        {
            if (name == TenantSettings.SystemPackagesName) SystemPackages.WriteFile(dataRoot, tenantSettings.SystemPackages);
        };
        builder.Services.AddSingleton<ISkillStore>(new SqliteSkillStore(database));
        // A person's edit lands with its tenant_events row in one transaction, as the outcomes' writes do.
        builder.Services.AddSingleton<IBacklogStore>(
            new SqliteBacklogStore(database, audit: TenantAuditRow.AppendAsync));
        builder.Services.AddSingleton<TenantLogging>();

        // THE THIRD STORE. Not the message log and not the tenant log: see IDiagnosticsLog for the
        // boundary, which is the whole of this feature's design work.
        //
        // THE BOUND IS RESOLVED HERE because this is the one place that knows the data root, and it is
        // DECLARED-OR-DERIVED rather than a constant: absent settings derive a row count from the size of
        // the volume this instance sits on, so a small host keeps a shorter log instead of filling its
        // disk, and nothing refuses. See DiagnosticRetention.
        //
        // Constructed rather than resolved by type, like every other store above it: its parameters are a
        // path and a bound, neither of which is a service.
        diagnosticsRetention = DiagnosticRetention.ForDataRoot(
            dataRoot,
            builder.Configuration.GetValue<int?>("Diagnostics:RetentionDays"),
            builder.Configuration.GetValue<long?>("Diagnostics:MaxRows"));

        diagnostics = new SqliteDiagnosticsLog(database, diagnosticsRetention);

        builder.Services.AddSingleton<IDiagnosticsLog>(diagnostics);
        builder.Services.AddSingleton<DiagnosticsRecorder>();

        // A SINGLETON because it is the only thing that can tell a reconnect from a first connection, and
        // a hub instance lives for one invocation. See TransportWatch for why this is in memory and lossy.
        builder.Services.AddSingleton<TransportWatch>();

        return true;
    }
}
