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
    internal void Workers()
    {
        // NoPermitRequired, and not an exemption from anything: every tool relays to an /api route with
        // the caller's own X-Api-Key (PlatformMcpTools.SendAsync), so TeamGate and PermitGate judge each
        // call on that route's own marker. RouteMarkerTests requires a marker here like everywhere else.
        app.MapMcp("/mcp").NoPermitRequired();
        SurfaceEndpoints.Map(app, dataRoot);
        Appearance.Map(app);
        CapacityEndpoints.Map(app);
        SiteEndpoints.Map(app);
        SiteApiEndpoints.Map(app);
        TenantSettingsEndpoints.Map(app);
        LedgerEndpoints.Map(app);
        TeamActivity.Map(app);
        TeamTokenRuns.Map(app);
        TeamActivityFrom.Map(app);
        WorkspaceFileChangeEndpoints.Map(app);
        AgentStepEndpoints.Map(app);
        OutcomeEndpoints.Map(app);
        HealthEndpoints.Map(app, database, dataRoot);
        VersionEndpoints.Map(app);
        ReleaseCheckEndpoints.Map(app);
        MarketplaceEndpoints.Map(app);
        // A worker's one connection. In control a worker joins the pool here; a Host that runs its runs
        // itself (all) welcomes none, and a worker that connects to it is told so, in a sentence, and stops.
        var workerConnections = new WorkerConnections(
            app.Services.GetRequiredService<WorkerPool>(),
            app.Services.GetRequiredService<WipLedger>(),
            async (envelope, ct) =>
            {
                await app.Services.GetRequiredService<WorkerAsks>().HandleAsync(envelope, ct);
                await app.Services.GetRequiredService<RunDirectory>().HandleAsync(envelope, ct);
                if (envelope.Event is RunMeasured or TerminalMeasured or WorkerCapacitySampled)
                {
                    await app.Services.GetRequiredService<CapacitySampler>().HandleAsync(envelope, ct);
                }

                await app.Services.GetRequiredService<WorkerStreams>().HandleAsync(envelope, ct);
            },
            worker => app.Services.GetRequiredService<RunDirectory>().OpenOn(worker),
            workerKey,
            takesWorkers: control,
            BuildVersion.Current.Version,
            settings: info => new RunWorkerSettings(
                ProcessAgentRunner.Allowance((tenantSettings.RunMemoryLimit(WorkerBounds.Of(info)), tenantSettings.RunMemoryCeiling(WorkerBounds.Of(info)))).Limit,
                TenantSettings.HostReserveMb),
            timings: WorkerBounds.Timings(builder.Configuration),
            diagnostics: app.Services.GetRequiredService<IDiagnosticsLog>(),
            log: app.Services.GetRequiredService<ILogger<WorkerConnections>>(),
            streams: app.Services.GetRequiredService<WorkerStreams>().Deliver);
        workerConnections.Changed += () => wip.SetMax(tenantSettings.WipMaxRunning);
        // The run limit is the sum of the connected workers' bounds, so --doctor's record of it is written
        // again whenever a worker joins or goes, as it is after a setting change; written at start only, it
        // said "no worker is connected" for as long as control ran.
        workerConnections.Changed += () => WipRecord.Of(tenantSettings, app.Services.GetRequiredService<RunMemoryLimits>()).Write(dataRoot, app.Logger);
        // A setting the figures a worker runs by are built from may have changed: every connected worker is
        // told now, not at its next welcome. Any change is said, since the figures are cheap to send and a
        // list of the settings they read would drift from the code that reads them.
        if (control)
        {
            tenantSettings.Changed += setting => Observed.Forget(workerConnections.SettingsChangedAsync(), app.Logger, "settings push to the workers");
        }
        // In control the start's retry of unfinished removals ran before any worker could connect: each worker
        // that joins retries them once, so what only the agent's pass can remove does not wait for a person.
        RetryWhenAWorkerJoins.Wire(
            control, workerConnections,
            ct => app.Services.GetRequiredService<UnfinishedRemovalRetry>().RetryAsync(ct: ct),
            app.Services.GetRequiredService<ILogger<WorkerConnections>>());
        // Each worker that joins is asked which agent CLIs it has, so whether one is installed is every
        // worker's answer, and two that disagree are seen.
        MeasureInstallsWhenAWorkerJoins.Wire(
            control, joined => workerConnections.Joined += joined,
            worker => app.Services.GetRequiredService<WorkerPool>().For(worker),
            () => MeasureInstallsWhenAWorkerJoins.Commands(app.Services.GetRequiredService<AgentCatalog>()),
            app.Services.GetRequiredService<WorkerAsks>(), app.Services.GetRequiredService<WorkerInstalls>(),
            app.Services.GetRequiredService<ILogger<WorkerConnections>>(), app.Services.GetRequiredService<AgentUpdateGate>());
        AgentUpdatesAtStart.Wire(
            control, builder.Configuration["HARNESS_IMAGE"], builder.Configuration["HARNESS_UPDATE_AGENTS"], joined => workerConnections.Joined += joined,
            () => AgentUpdatesAtStart.OnePerCommand(app.Services.GetRequiredService<AgentCatalog>()),
            agent => app.Services.GetRequiredService<AgentCliUpdater>().Request(agent, person: null),
            app.Services.GetRequiredService<ILogger<WorkerConnections>>());
        // EVERYTHING CONTROL MEASURES THROUGH A WORKER IS MEASURED AGAIN WHEN THE WORKERS CHANGE - the sign-in
        // probe, the launch check, the tool pre-flight and the CLI version record - as one pass once the
        // changes settle, and when an update ends, which also asks every placeable worker about that command.
        // Measured at start only, none of them had a worker to ask.
        RemeasureWhenWorkersChange.Wire(
            control,
            changed => workerConnections.Changed += changed,
            attached => workerConnections.Attached += attached,
            ended => app.Services.GetRequiredService<AgentUpdateGate>().UpdateEnded += ended,
            () =>
            {
                var auth = app.Services.GetRequiredService<AgentAuthProbe>();
                var launch = app.Services.GetRequiredService<AgentLaunchChecks>();
                var tools = app.Services.GetRequiredService<AgentToolPreflight>();
                return new RemeasureWhenWorkersChange(
                    [
                        ("the sign-in probe", ct => auth.ReportsAsync(ct)),
                        ("the launch check", ct => launch.ReportsAsync(ct, fresh: true)),
                        ("the tool pre-flight", ct => tools.PassAsync(ct)),
                        ("the CLI versions", ct => CliVersionsWhenWorkersChange.MeasureAsync(
                            app.Services.GetRequiredService<AgentCatalog>(), app.Services.GetRequiredService<WorkerAsks>(), dataRoot,
                            app.Services.GetRequiredService<AgentUpdateGate>(), app.Logger, ct)),
                    ],
                    () =>
                    {
                        auth.Forget();
                        launch.Forget();
                    },
                    log: app.Services.GetRequiredService<ILogger<RemeasureWhenWorkersChange>>(),
                    stopping: app.Lifetime.ApplicationStopping);
            },
            command =>
            {
                var pool = app.Services.GetRequiredService<WorkerPool>();
                return Task.WhenAll(pool.Workers.Select(worker => pool.For(worker)).OfType<IRunWorker>().Select(worker =>
                    MeasureInstallsWhenAWorkerJoins.MeasureAsync(
                        worker, [command], app.Services.GetRequiredService<WorkerAsks>(), app.Services.GetRequiredService<WorkerInstalls>(),
                        app.Logger, app.Lifetime.ApplicationStopping, app.Services.GetRequiredService<AgentUpdateGate>())));
            },
            app.Services.GetRequiredService<ILogger<WorkerConnections>>());
        // In all no worker joins: when an update ends, this machine's answers are taken again.
        if (!control)
        {
            app.Services.GetRequiredService<AgentUpdateGate>().UpdateEnded += command =>
            {
                app.Services.GetRequiredService<AgentInstallProbe>().Forget(command);
                app.Services.GetRequiredService<AgentAuthProbe>().Forget();
                app.Services.GetRequiredService<AgentLaunchChecks>().Refresh();
                app.Services.GetRequiredService<AgentToolPreflight>().Refresh();
            };
        }
        // Control's workers, recorded for --doctor (another process) on every change and every few seconds.
        System.Threading.Timer? workersRecording = null;
        if (control)
        {
            void RecordWorkers() => WorkersRecord.RecordSafely(
                () => WorkersRecord.Of(
                    app.Services.GetRequiredService<WorkerPool>(), app.Services.GetRequiredService<WipLedger>(),
                    worker => app.Services.GetRequiredService<RunDirectory>().OpenOn(worker), BuildVersion.Current.Version),
                dataRoot, app.Logger);
            workerConnections.Changed += RecordWorkers;
            workersRecording = new System.Threading.Timer(_ => RecordWorkers(), null, TimeSpan.Zero, WorkersRecord.Every);
        }
        recordConciergeSessions = () => _ = Task.Run(async () =>
        {
            // A session ended by shutdown has nothing left to record, and nothing left to record it with.
            if (app.Lifetime.ApplicationStopping.IsCancellationRequested) return;

            try
            {
                var consoles = app.Services.GetRequiredService<ConciergeSessionStore>();
                var accounts = app.Services.GetRequiredService<IUserStore>();
                var sessions = await ConciergeSessionsView.ReadAsync(
                    consoles, tenantSettings.ConciergeIdleTimeout, app.Services.GetRequiredService<CapacitySampler>().Terminal,
                    async (user, ct) => (await accounts.FindByIdAsync(user, ct))?.Email, CancellationToken.None);
                ConciergeSessionsRecord.Of(consoles.Clock.GetUtcNow(), tenantSettings.ConciergeIdleTimeout, sessions).Write(dataRoot, app.Logger);
            }
            catch (ObjectDisposedException)
            {
                // The Host stopped between the check and the read.
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                app.Logger.LogWarning("Concierge: could not record the sessions for the doctor: {Error}", exception.Message);
            }
        });
        app.Services.GetRequiredService<ConciergeSessionStore>().Changed += () => recordConciergeSessions?.Invoke();
        recordConciergeSessions();
        // ONE CALLBACK, IN THIS ORDER. The Host going down ends no admission hold: a waiter it withdraws, or
        // admits to a slot it frees, leaves its row open for the next start to close, marked unfinished - so
        // the ledger is told first, before dropping the workers frees a slot. One callback rather than three
        // because a token runs its callbacks newest first, and an order that depends on registration order
        // is an order the next registration silently breaks.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            wip.HostStopping();
            workerConnections.Stop();
            workersRecording?.Dispose();
        });
        Func<IReadOnlyList<WorkerSample>> workersNow = () => WorkersView.Of(
            app.Services.GetRequiredService<WorkerPool>(), app.Services.GetRequiredService<WipLedger>(), BuildVersion.Current.Version,
            worker => ConciergeSessionsView.TerminalsOn(
                app.Services.GetRequiredService<ConciergeSessionStore>(), worker, app.Services.GetRequiredService<CapacitySampler>().Terminal));
        WorkerEndpoints.MapList(app, workersNow);
        ServiceStatusEndpoints.Map(app, database, dataRoot, workersNow);
        app.Services.GetRequiredService<CapacitySampler>().Describe = workersNow;
        if (control)
        {
            app.Logger.LogInformation(
                workerKeys.Configured
                    ? "Control role: runs go to workers that connect at {Route} with the worker key; none runs in this process. CLI updates run on one connected worker, through this control's one update gate."
                    : "Control role: no worker key is configured (HARNESS_WORKER_KEY), so no worker can connect and every run waits for a worker ({Route}).",
                WorkerKeyGate.ConnectRoute);
        }
        WorkerEndpoints.Map(app, async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await workerConnections.AcceptAsync(socket, context.RequestAborted);
        });
        RemovalEndpoints.Map(app);
        LocalRepoEndpoints.Map(app);
    }
}
