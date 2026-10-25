using Harness.Contracts;
using Harness.Host.Auth;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;

namespace Harness.Host;

internal sealed partial class HostComposition
{
    // THE SERVICES OVER THE STORES, the same on either provider: each is called by `SqliteStoresAsync` and
    // `SqlServerStoresAsync` at its own place in their order, handed that provider's store. Nothing here
    // knows which provider it is; a service built only on one path is one the other Host silently lacks.

    /// <summary>
    /// EVERY ADMISSION HOLD, written down by admission as it happens (`admission_holds`), so the team
    /// activity read can show a wait for a slot with its reason after the fact. The writer queues; a
    /// write that fails is logged and admission carries on.
    /// </summary>
    private async Task AdmissionHoldsAsync(IAdmissionHoldLedger admissionHolds)
    {
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
    }

    /// <summary>The gates over the outcomes and the settings, the ledger's identity, and the board and
    /// the rollup over the log.</summary>
    private void OutcomesAndBoard(IOutcomeStore outcomeStore, IMessageLog log)
    {
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
        builder.Services.AddSingleton(sp => new KanbanStore(log, team => sp.GetRequiredService<TeamRegistry>().FloorFor(team)));

        // THE ROLLUP'S TIMINGS PER TEAM, kept until that team's rows move, and told of purges the same way.
        builder.Services.AddSingleton(new RollupCache(log));
    }

    /// <summary>
    /// What each container has accepted and not finished. Without it nothing is durable: a container's
    /// queue is an in-memory Channel and its cursor advances when a message is OFFERED, so a host
    /// stopping between the two would drop the work with no redelivery to fall back on and nothing to
    /// say it had happened.
    /// </summary>
    private void Deliveries(IPendingDeliveries pending)
    {
        builder.Services.AddSingleton(pending);
        builder.Services.AddSingleton<PendingDeliveriesAtStart>();
    }

    /// <summary>
    /// THE WORKER KEY: what a worker process connects to control with (Workers:Key, or HARNESS_WORKER_KEY;
    /// in an image, the file the entrypoint moved it to, Workers:KeyFile or HARNESS_WORKER_KEY_FILE).
    /// Stored hashed as a principal with no permit, or removed when none is configured, so a key a
    /// previous start stored never outlives its configuration. Taken on the worker connection alone.
    /// False when the key file is refused, which has been printed and the exit code set.
    /// </summary>
    private bool WorkerKey(IPrincipalStore principals)
    {
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

        return true;
    }

    /// <summary>
    /// A team's sites: their versions and their data (auth-015), and the service every caller - the
    /// browser routes, a plugin's records, the team deletion - goes through. See SiteService.
    /// </summary>
    private void Sites(ISiteStore sites)
    {
        builder.Services.AddSingleton(sites);
        // The team lookup is LATE: TeamRegistry needs the member runtime, whose plugin runner needs this.
        builder.Services.AddSingleton(sp => new SiteService(
            sp.GetRequiredService<ISiteStore>(),
            team => sp.GetRequiredService<TeamRegistry>().ExistingName(team),
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<IMessageLog>(),
            removal: sp.GetRequiredService<FolderRemoval>()));
        builder.Services.AddSingleton<SiteCapability>();
    }

    /// <summary>
    /// INSTANCE-WIDE SETTINGS, read once here - rows over appsettings.json over built-in defaults -
    /// and then held in memory. Every consumer below is handed a delegate onto this, never a number, so
    /// a person's change applies at its next use with no restart. See `TenantSettings`.
    /// </summary>
    private async Task TenantSettingsAsync(ITenantSettingsStore tenantSettingsStore)
    {
        builder.Services.AddSingleton(tenantSettingsStore);
        tenantSettings = new TenantSettings(tenantSettingsStore, builder.Configuration);
        await tenantSettings.LoadAsync();
        builder.Services.AddSingleton(tenantSettings);

        // "System packages" is installed by the entrypoint before the host starts, so the host keeps a
        // plain copy of the list on the volume for it: now, and on every change. See `SystemPackages`.
        SystemPackages.WriteFile(dataRoot, tenantSettings.SystemPackages);
        tenantSettings.Changed += name =>
        {
            if (name == TenantSettings.SystemPackagesName) SystemPackages.WriteFile(dataRoot, tenantSettings.SystemPackages);
        };
    }

    /// <summary>
    /// THE RECORDS THE HOST KEEPS OF ITSELF: the tenant log's writer, and THE THIRD STORE - not the
    /// message log and not the tenant log: see IDiagnosticsLog for the boundary, which is the whole of
    /// this feature's design work - with the recorder the startup facts go through.
    /// </summary>
    private void Records(IDiagnosticsLog diagnosticsLog)
    {
        builder.Services.AddSingleton<TenantLogging>();
        builder.Services.AddSingleton(diagnosticsLog);
        builder.Services.AddSingleton<DiagnosticsRecorder>();

        // A SINGLETON because it is the only thing that can tell a reconnect from a first connection, and
        // a hub instance lives for one invocation. See TransportWatch for why this is in memory and lossy.
        builder.Services.AddSingleton<TransportWatch>();
    }
}
