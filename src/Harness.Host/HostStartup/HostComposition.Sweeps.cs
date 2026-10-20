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
    internal void Sweeps()
    {
        // How long a console may sit unattended AND inactive before it is ended, and how often that is checked.
        //
        // Configurable for deployment AND for a demonstration: the default is far too long to sit through,
        // so setting ConciergeIdleTimeout to something like 00:02:00 is how a person watches this work.
        // `concierge.idleTimeout`, read at every sweep.
        var consoleIdle = tenantSettings.ConciergeIdleTimeout;

        // The sweep follows the window rather than being fixed. A one-minute sweep against a two-minute
        // window would make the window mean anything up to three minutes, which is exactly the kind of
        // vagueness that makes someone conclude the feature does not work.
        var consoleSweep = consoleIdle < TimeSpan.FromMinutes(4)
            ? TimeSpan.FromSeconds(15)
            : TimeSpan.FromMinutes(1);

        var scheduleSweepInterval = Intervals.Read(builder.Configuration, "ScheduleSweepInterval", TimeSpan.FromSeconds(15));
        var scheduleRunnerEnabled = Intervals.Enabled(builder.Configuration, "ScheduleRunnerEnabled");
        var quietSweepInterval = Intervals.Read(builder.Configuration, "QuietSweepInterval", TimeSpan.FromMinutes(1));
        var quietSweepEnabled = Intervals.Enabled(builder.Configuration, "QuietSweepEnabled");

        // THE CLOCK IN FRONT OF THE NUDGE. See `ResumeSweep` for the whole argument; these three
        // settings are the parts an operator is allowed to move.
        //
        // THE INTERVAL IS THE ACCURACY and a minute is chosen for the same reason the quiet sweep's is: a
        // resume is late by up to one tick and never early, and a provider's reset is a wall-clock minute
        // rather than a second. `ResumeMaxAutomatic` is the BOUND, and it is configurable DOWNWARDS as much
        // as up - setting it to 0 turns automatic resumes off entirely without removing the class, the
        // card, or a person's own Nudge.
        var resumeSweepInterval = Intervals.Read(builder.Configuration, "ResumeSweepInterval", TimeSpan.FromMinutes(1));
        var resumeSweepEnabled = Intervals.Enabled(builder.Configuration, "ResumeSweepEnabled");
        // The resume bound is the tenant setting `resume.maxAutomatic`, read at every sweep.

        // Registered even when the runner is gated off. Every schedule write signals it, and a route that
        // had to know whether a background service exists would be a second thing to keep in step.
        builder.Services.AddSingleton<TriggerWakeSignal>();

        // The doctor's record of the running Concierge sessions, written once the app is built: after each
        // sweep, and when a session starts or ends.
        recordConciergeSessions = null;
        builder.Services.AddSingleton<IHostedService>(sp =>
        {
            var accounts = sp.GetRequiredService<IUserStore>();
            return new ConciergeReaper(
                sp.GetRequiredService<ConciergeSessionStore>(),
                () => tenantSettings.ConciergeIdleTimeout,
                consoleSweep,
                sp.GetRequiredService<ILogger<ConciergeReaper>>(),
                sp.GetRequiredService<ITenantLog>(),
                async (user, ct) => (await accounts.FindByIdAsync(user, ct))?.Email,
                () => recordConciergeSessions?.Invoke());
        });
        builder.Services.AddSingleton<WorkflowEndings>();
        builder.Services.AddSingleton<QuietTeamSweep>();
        if (quietSweepEnabled)
        {
            builder.Services.AddSingleton<IHostedService>(sp => new QuietTeamReaper(
                sp.GetRequiredService<QuietTeamSweep>(),
                () => tenantSettings.QuietWindow,
                quietSweepInterval,
                sp.GetRequiredService<ILogger<QuietTeamReaper>>(),
                ct => SqliteMaintenance.CheckpointAsync(database, ct)));
        }

        // Written by scripts/ensure-agent-clis.sh at each container start, read by Diagnostics.
        builder.Services.AddSingleton(CliVersionHistory.In(dataRoot));

        // A DAILY COPY OF THE DATABASE. `Backups:Directory` defaults to `<dataRoot>/backups`,
        // which in the container is /data/backups, beside the pre-migration and --backup copies that
        // its own retention never touches. `Backups:Keep` is how many daily copies survive (default 7). Each
        // run also prunes the before-step copies in the same folder the migrator wrote them to, by
        // `Backups:KeepBeforeStep` and `Backups:KeepBeforeStepDays` - all read once, above.
        var backupsEnabled = Intervals.Enabled(builder.Configuration, "Backups:Enabled");
        if (backupsEnabled)
        {
            var dailyBackup = backupSettings.DailyBackupFor(database);
            builder.Services.AddSingleton(dailyBackup);
            builder.Services.AddSingleton<IHostedService>(sp => new DailyBackupRunner(
                dailyBackup,
                TimeSpan.FromHours(1),
                sp.GetRequiredService<ILogger<DailyBackupRunner>>()));
        }
        // THE MESSAGE LOG KEEPS `log.retentionDays` OF FINISHED WORK: a pass shortly after start and then
        // daily removes the rest through the log's one delete, never on a request's path. See LogRetention.
        builder.Services.AddSingleton(sp => new LogRetention(
            new SqliteLogRetention(database),
            sp.GetRequiredService<IMessageLog>(),
            sp.GetRequiredService<ITenantLog>(),
            () => tenantSettings.LogRetentionDays,
            sp.GetRequiredService<ILogger<LogRetention>>(),
            purged: () =>
            {
                sp.GetRequiredService<KanbanStore>().Invalidate();
                sp.GetRequiredService<RollupCache>().Invalidate();
                sp.GetRequiredService<WorkflowEndings>().Invalidate();
            }));
        builder.Services.AddHostedService(sp => new LogRetentionLoop(
            sp.GetRequiredService<LogRetention>(),
            sp.GetRequiredService<ILogger<LogRetentionLoop>>()));
        builder.Services.AddSingleton(sp => new ResumeSweep(
            sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<ContainerHost>(),
            sp.GetRequiredService<IMessageLog>(),
            ResumeSweep.DefaultMaxAutomaticResumes,
            sp.GetRequiredService<ILogger<ResumeSweep>>(),
            () => tenantSettings.ResumeMaxAutomatic));

        // THE THING THAT NOTICES A WORKFLOW NOBODY IS WORKING. See `IdleWorkflowOffer` for the
        // whole argument.
        //
        // A SERVICE RATHER THAN A STATIC, ONLY BECAUSE OF THE TWO BOUNDS. `TerminalPublish` beside it is a
        // static method taking its collaborators as arguments, and this would be too if it did not need
        // `causationDepthLimit` and `workflowSpendLimit` - both of which are read from configuration, and
        // one of which is declared BELOW the `ContainerHost` registration that ends up calling this. A
        // singleton closes over them here, where both are in scope, and the run-ending hook resolves it
        // through the provider exactly as it already resolves `TeamRegistry`.
        builder.Services.AddSingleton(sp => new IdleWorkflowOffer(
            sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<ContainerHost>(),
            sp.GetRequiredService<IMessageLog>(),
            sp.GetRequiredService<IPendingDeliveries>(),
            sp.GetRequiredService<ICursors>(),
            sp.GetRequiredService<ISubscriptions>(),
            () => tenantSettings.CausationDepthLimit,

            // NO `workflowSpendLimit` HERE. A raw configuration value would disagree with `ContainerHost`
            // about a configured 0 and could not see a team's own per-workflow figure, so both ask
            // `TeamRegistry.EffectiveWorkflowBudgetFor`, which is the one place that resolves it.
            sp.GetRequiredService<ILogger<IdleWorkflowOffer>>(),

            // A PENDING WAKE IS WORK IN THE WORKFLOW: nothing is offered while one waits to fire.
            sp.GetRequiredService<IWakeStore>()));
        builder.Services.AddSingleton<UndeclarableWorkflows>();

        // `wake_me`: A MEMBER WOKEN ONCE, LATER, IN THE WORKFLOW IT IS WORKING. Its own clock, so a test
        // moves the wakes' time and nothing else's; the limits read through delegates at every ask.
        builder.Services.AddSingleton(sp => new WakeClock(sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<WakeSignal>();
        builder.Services.AddSingleton(sp => new WakeMe(
            sp.GetRequiredService<IWakeStore>(),
            sp.GetRequiredService<IMessageLog>(),
            sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<ContainerHost>(),
            sp.GetRequiredService<WakeClock>(),
            sp.GetRequiredService<WakeSignal>(),
            () => tenantSettings.WakeMaxDelay,
            () => tenantSettings.CausationDepthLimit,
            sp.GetRequiredService<ILogger<WakeMe>>()));
        builder.Services.AddHostedService<WakeRunner>();
        if (resumeSweepEnabled)
        {
            builder.Services.AddSingleton<IHostedService>(sp => new ResumeRunner(
                sp.GetRequiredService<ResumeSweep>(),
                resumeSweepInterval,
                sp.GetRequiredService<ILogger<ResumeRunner>>()));
        }
        // THE FOLDER-CHANGE TRIGGER. Polled by the TriggerSweep below and announced to by the
        // Documents routes. Its areas are the team's documents and the file-browser roots marked allowWatch.
        builder.Services.AddSingleton(sp => new FolderWatch(
            sp.GetRequiredService<ITriggerStore>(),
            sp.GetRequiredService<IMessageLog>(),
            sp.GetRequiredService<ContainerHost>(),
            sp.GetRequiredService<IPendingDeliveries>(),
            team => sp.GetRequiredService<TeamDocuments>().RootFor(team),
            fileBrowser.Roots,
            dataRoot,
            int.TryParse(builder.Configuration["FolderWatch:MaxWatches"], out var maxWatches) && maxWatches > 0
                ? maxWatches
                : FolderWatch.DefaultMaximumWatches,
            sp.GetRequiredService<ILogger<FolderWatch>>()));
        builder.Services.AddSingleton<TriggerCost>();

        // WHAT AN ARCHIVED TEAM IS REFUSED AND SKIPS, and whether a team is quiet enough to archive or delete.
        builder.Services.AddSingleton<TeamArchive>();
        builder.Services.AddSingleton<TriggerSweep>();

        // THE ONE PATH A TRIGGER IS MADE BY: the Triggers dialog's route and a solution install.
        builder.Services.AddSingleton<TriggerCreation>();
        if (scheduleRunnerEnabled)
        {
            builder.Services.AddSingleton<IHostedService>(sp => new TriggerRunner(
                sp.GetRequiredService<TriggerSweep>(),
                sp.GetRequiredService<ITriggerStore>(),
                sp.GetRequiredService<TriggerWakeSignal>(),

                // A HEARTBEAT rather than a tick. The runner sleeps until the next schedule is actually
                // due; this only bounds how long it will go without re-reading, for state that changed
                // without a signal reaching it. `ScheduleSweepInterval` does not decide accuracy.
                scheduleSweepInterval,
                sp.GetRequiredService<ILogger<TriggerRunner>>()));
        }

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<PumpHeartbeat>();
        builder.Services.AddSingleton<LoginThrottle>();
        builder.Services.AddHostedService<PumpService>();
        builder.Services.AddHostedService<KanbanChangePush>();

        // THE MONITOR'S VIEW OF A RUNNING MEMBER'S FILES: read during every run, recorded for the replay,
        // pushed only to the people watching. See WorkspaceFileWatch.
        builder.Services.AddSingleton<WorkspaceFileWatchTiming>();
        builder.Services.AddSingleton<IWorkspaceRuns>(sp => new HostWorkspaceRuns(sp.GetRequiredService<ContainerHost>()));
        builder.Services.AddSingleton(sp => new WorkspaceFileWatch(
            sp.GetRequiredService<GitRunner>(),
            sp.GetRequiredService<TeamPaths>(),
            (change, ct) => sp.GetRequiredService<IHubContext<ContainerHub>>()
                .Clients.Group(WorkspaceFileWatch.Group).SendAsync(WorkspaceFileWatch.Message, change, ct),
            record: sp.GetRequiredService<WorkspaceFileChangeStore>(),
            logger: sp.GetRequiredService<ILogger<WorkspaceFileWatch>>()));
        builder.Services.AddHostedService<WorkspaceFileWatchService>();

        // WHAT EVERY AGENT RUN DID, step by step: its transcript followed on its worker from the run's start
        // to its end whether or not anybody watches, each step recorded (kind, tool name, time) and pushed
        // only to the people watching, as workspaceChanged is. Off every run's path. See AgentStepFollow.
        builder.Services.AddSingleton(sp => new AgentStepFollow(
            sp.GetRequiredService<LiveRuns>(),
            member => sp.GetRequiredService<ContainerHost>().Find(member) is { CurrentCausation: { } delivery } runtime
                ? new AgentStepRun(delivery, runtime.Snapshot().CurrentCorrelation)
                : null,
            member => LiveViewEndpoints.Locate(sp.GetRequiredService<RunDirectory>(), WorkersOf(sp), member, out var placed) is { } why
                ? (null, why)
                : (placed, null),
            sp.GetRequiredService<WorkerStreams>(),
            sp.GetRequiredService<AgentStepStore>(),
            (push, ct) => sp.GetRequiredService<IHubContext<ContainerHub>>()
                .Clients.Group(WorkspaceFileWatch.Group).SendAsync(AgentStepFollow.Message, push, ct),
            logger: sp.GetRequiredService<ILogger<AgentStepFollow>>()));

        // THE PER-RUN FOREIGN TOOLS CHECK, called by every member at the end of its run (`onTerminal`
        // above). What a preset allows is the catalog's isolation model (AgentCatalog.Allowance); a preset
        // that declares no allowed tools is not verified, and its runs are never reported clean. See
        // ForeignToolsCheck.
        builder.Services.AddSingleton(sp => new PresetAllowedTools(sp.GetRequiredService<AgentCatalog>().Allowance));
        builder.Services.AddSingleton<ForeignToolsCheck>();
        builder.Services.AddHostedService<DefaultBranchAtStart>();
        // The operator CLI's `plugin install` asks for a rescan by writing a file the Host polls: no restart, no API key.
        builder.Services.AddHostedService<PluginRescanRequests>();

        // THE OPERATOR CLI'S `solution install`: a request file only the Host and root can write, answered
        // by the installer the routes run.
        builder.Services.AddHostedService<SolutionRequests>();


        builder.Services.AddSignalR()
            .AddJsonProtocol(options => ConfigureHostJson(options.PayloadSerializerOptions));

        // Registered UNCONDITIONALLY, not behind IsDevelopment() as the vendor recipe has it. The container
        // runs Production, so the gated version would compile, pass, and serve nothing, naming no reason.

        // Enums as NAMES, not numbers. A browser receiving state 0 has to know the member order to mean
        // anything by it, and that coupling breaks silently the first time a value is inserted rather than
        // appended -- every client would then be confidently wrong rather than obviously broken.
        //
        // This is HALF the story. The hub is the other half:
        // see AddSignalR above, which needs the same converter added separately because SignalR never reads
        // these options.
        builder.Services.ConfigureHttpJsonOptions(options => ConfigureHostJson(options.SerializerOptions));

        // A BODY THAT DOES NOT BIND THROWS, in every environment rather than Development alone, so
        // `JsonBodyErrors` can answer it with a sentence naming the field instead of an empty 400.
        builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        builder.Services.AddHttpContextAccessor();
        // THE SIGN-IN PROBE: asked of a connected worker, recorded in agent-auth.json for the doctor.
        builder.Services.AddSingleton(sp => new AgentAuthProbe(
            sp.GetRequiredService<AgentCatalog>(), sp.GetRequiredService<AgentLaunchUser>(),
            sp.GetRequiredService<WorkerAsks>(), dataRoot, sp.GetRequiredService<ILogger<AgentAuthProbe>>(),
            installs: sp.GetRequiredService<AgentInstallProbe>(), measured: control ? sp.GetRequiredService<WorkerInstalls>() : null,
            gate: sp.GetRequiredService<AgentUpdateGate>()));
        // THE LAUNCH CHECK: each preset's free invocation through the member runner's own launch (AgentLaunchChecks).
        builder.Services.AddSingleton(sp => new AgentLaunchChecks(
            sp.GetRequiredService<AgentCatalog>(), sp.GetRequiredService<ProcessAgentRunner>(), dataRoot,
            sp.GetRequiredService<ILogger<AgentLaunchChecks>>(), sp.GetRequiredService<AgentUpdateGate>(), onThisMachine: !control));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentLaunchChecks>());

        // THE PRE-FLIGHT: what each preset's CLI would load, listed by the CLI itself as the agent user,
        // once the Host is serving and again after every catalog save. Never on the start path.
        // Each listing runs on the connected worker with the most headroom (WorkerListingRunner).
        builder.Services.AddSingleton<IListingRunner>(sp => new WorkerListingRunner(
            sp.GetRequiredService<WorkerAsks>(), pathIsTheWorkers: !control));
        builder.Services.AddSingleton(sp => new AgentToolPreflight(
            sp.GetRequiredService<AgentCatalog>(), sp.GetRequiredService<IListingRunner>(), dataRoot,
            sp.GetRequiredService<ILogger<AgentToolPreflight>>(),
            sp.GetRequiredService<AgentUpdateGate>(), onThisMachine: !control));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentToolPreflight>());
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<PlatformMcpTools>();
    }
}
