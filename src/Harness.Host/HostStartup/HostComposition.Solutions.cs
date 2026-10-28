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
    internal async Task SolutionsAsync()
    {
        // WHICH SOLUTION PACKAGE EACH TEAM CAME FROM (`team_solutions`, auth-016), held in memory as well so
        // a team's summary reads it with no round trip. Written by the install and the update, swept by a
        // team's deletion.
        var teamSolutions = new TeamSolutionIndex(teamSolutionRows);
        await teamSolutions.LoadAsync();
        builder.Services.AddSingleton(teamSolutions);
        builder.Services.AddSingleton<ITeamSolutionStore>(teamSolutions);
        builder.Services.AddSingleton<ITeamSolutions>(teamSolutions);

        // THE INSTALL AND UPDATE OF A SOLUTION PACKAGE: the check, then the stores a person's own clicks
        // use, step by step, undone in reverse when a step fails. See SolutionInstaller.
        builder.Services.AddSingleton(sp =>
        {
            var agents = sp.GetRequiredService<AgentCatalog>();
            var probe = sp.GetRequiredService<AgentInstallProbe>();
            var runAs = sp.GetRequiredService<AgentLaunchUser>();
            var listPush = sp.GetRequiredService<TeamListPush>();

            return new SolutionInstaller(
                sp.GetRequiredService<SolutionService>(),
                sp.GetRequiredService<PluginInstaller>(),
                pluginCatalog,
                sp.GetRequiredService<TeamRegistry>(),
                sp.GetRequiredService<TeamRepoSetup>(),
                sp.GetRequiredService<TeamDeletion>(),
                sp.GetRequiredService<MemberDeletion>(),
                sp.GetRequiredService<TeamSkills>(),
                sp.GetRequiredService<TeamPaths>(),
                sp.GetRequiredService<SiteService>(),
                sp.GetRequiredService<TriggerCreation>(),
                sp.GetRequiredService<ITeamSolutionStore>(),
                sp.GetRequiredService<TeamDocuments>(),
                sp.GetRequiredService<FolderWatch>(),
                sp.GetRequiredService<TenantLogging>(),

                // THE AGENT a package's agent members run when it names none (SolutionDefaultAgent).
                () => SolutionDefaultAgent.Of(agents, probe),
                sp.GetRequiredService<Connections>(),
                sp.GetRequiredService<IConnectionStore>(),
                sp.GetRequiredService<IPluginMemberSettingsStore>(),
                new TeamAnnouncements(
                    team => listPush.AnnounceCreatedAsync(team),
                    async team =>
                    {
                        var viewers = (await listPush.EntitledViewerIdsAsync(team)).ToArray();
                        return () => listPush.AnnounceDeletedAsync(team, viewers);
                    }),
                runAs.Switches ? runAs.Gid : -1,
                sp.GetRequiredService<ISecretStore>(),
                sp.GetRequiredService<TriggerSweep>(),
                sp.GetRequiredService<IOutcomeStore>(),
                sp.GetRequiredService<FolderRemoval>(),
                sp.GetRequiredService<PluginMemberSecretStore>());
        });

        // WHAT A SOLUTION TEAM WAITS FOR, asked before every trigger's fire. The installer is late-bound:
        // it fires run-at-install through TriggerSweep, which asks this.
        builder.Services.AddSingleton(sp => new SolutionWait(
            sp.GetRequiredService<ITeamSolutionStore>(),
            () => sp.GetRequiredService<SolutionInstaller>(),
            sp.GetRequiredService<ContainerHost>(),
            sp.GetRequiredService<IMessageLog>(),
            sp.GetRequiredService<ITriggerStore>(),
            sp.GetRequiredService<TenantLogging>()));

        // THE SOLUTIONS LAUNCHER AND A SOLUTION'S CONTROL PANEL: reads only; every control is an existing route.
        builder.Services.AddSingleton(sp => new SolutionPanels(
            sp.GetRequiredService<ITeamSolutionStore>(),
            sp.GetRequiredService<SolutionInstaller>(),
            sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<ContainerHost>(),
            sp.GetRequiredService<ITriggerStore>(),
            sp.GetRequiredService<TriggerCost>(),
            sp.GetRequiredService<IMessageLog>(),
            sp.GetRequiredService<SiteService>(),
            sp.GetRequiredService<TeamDocuments>(),
            pluginCatalog));

        // The board's notice for a package a workflow wrote, checked through that one door when the
        // workflow is declared complete.
        builder.Services.AddSingleton(sp => new SolutionNotice(
            sp.GetRequiredService<SolutionService>(), sp.GetRequiredService<TeamDocuments>(), sp.GetRequiredService<IMessageLog>()));

        // What each role is offered, for the "Available skills" list in every system prompt. Filled with
        // the custom skills at start, before any team is restored, and refreshed after every skill write.
        builder.Services.AddSingleton(new SkillDirectory());

        // Team skills: a team's own custom skills, offered only to its members. What an installer calls.
        builder.Services.AddSingleton(sp => new TeamSkills(
            sp.GetRequiredService<ISkillStore>(),
            sp.GetRequiredService<SkillDirectory>(),
            sp.GetRequiredService<TeamRegistry>()));

        // Whether the CLI a preset names is on this machine at all. A SINGLETON because the few-second
        // cache is the whole reason it is a class rather than a static method: a per-request instance would
        // walk PATH once per preset on every render, and one that lived forever would go on reporting a
        // problem the person had just fixed. It runs no candidate binary - it resolves a name and stops -
        // so it takes no dependency on anything that could.
        // Constructed rather than resolved by type: its parameters are the injection seams the specs
        // drive (a clock, the cache window, the workers' answers) and none of them is resolved by type, so leaving the
        // container to choose a constructor would either fail to find one or quietly pick the wrong shape
        // the day another is added.
        // In CONTROL its answer is the workers' and never this machine's PATH, which has no agent CLI on it:
        // what each placeable worker measured (WorkerInstalls), from the sign-in probe and from the
        // measurement each worker gets as it joins. In `all` the runs are this machine's, and so is the PATH.
        builder.Services.AddSingleton(sp => WorkerInstalls.Over(sp.GetRequiredService<WorkerPool>()));
        // A command the update gate holds reads updating, in both roles, and never not installed.
        builder.Services.AddSingleton(sp => control
            ? new AgentInstallProbe(workers: sp.GetRequiredService<WorkerInstalls>(), gate: sp.GetRequiredService<AgentUpdateGate>())
            : new AgentInstallProbe(gate: sp.GetRequiredService<AgentUpdateGate>()));
        // The one path from the progress ROUTE to whatever is currently running that member. Singleton
        // because both ends have to be looking at the same object for a heartbeat to mean anything.
        builder.Services.AddSingleton<RunHeartbeat>();
        // THE INSTANCE-WIDE LEASES (`heavy`), across every team. The holder count is read through a
        // delegate on every call, so a settings write applies at the next acquire or release. ILeaseState
        // is the read-only view the activity monitor reads. See InstanceLeases and LeaseActions.
        builder.Services.AddSingleton(sp =>
            new InstanceLeases(() => sp.GetRequiredService<TenantSettings>().LeasesHeavyHolders));
        builder.Services.AddSingleton<ILeaseState>(sp => sp.GetRequiredService<InstanceLeases>());
        // BY FACTORY over the shared worker: a lease's moves reach the runs as protocol messages.
        builder.Services.AddSingleton(sp => new LeaseActions(
            sp.GetRequiredService<InstanceLeases>(), sp.GetRequiredService<IRunWorker>(), sp.GetRequiredService<IMemberReports>()));
        // WRAPPED, AND THE WRAPPING IS LOAD-BEARING. `ProcessAgentRunner` spawns the process and reads what
        // it wrote; `CredentialUseRunner` answers what the SERVER saw while it ran, which is how a run that
        // did nothing is told from one that decided there was nothing to do. Unwrap this and every result
        // reports "not measured" - which AgentResult.Succeeded treats as success, silently, which is the
        // defect it exists to catch. `AgentRunnerCompositionTests` asserts what comes out of here.
        // Who agent children run as, decided once here and logged at start. `agent` when the
        // Host can switch to it, the Host's own user otherwise - see AgentLaunchUser.
        builder.Services.AddSingleton(_ => InProcessWorker.LaunchUser(
            builder.Configuration["Agents:RunAs"] ?? Environment.GetEnvironmentVariable("HARNESS_AGENT_USER")));
        // The runs in flight, for the live route. The runner records; nothing is stored.
        builder.Services.AddSingleton<LiveRuns>();
        // WHAT EACH MEMBER RUN'S CREDENTIALS ARE, held in memory so its reports and its transcript are
        // redacted of it as the run's output is. Nothing is stored. See RunSecrets.
        builder.Services.AddSingleton<RunSecrets>();
        // WHO MAY USE AN AGENT CLI'S SHARED INSTALL: member runs share it, a platform update has it alone,
        // and a launch that arrives during an update waits for it. One per Host, shared by the runner, the
        // Concierge's launch and the updater, or the hold holds nothing.
        builder.Services.AddSingleton<AgentUpdateGate>();
        // WHAT CONTROL ASKS A WORKER ABOUT ITS AGENT CLIS - a sign-in probe, a listing, a version, an update,
        // a removal as the agent - and the answers, by request. Asked of the connected worker with the most
        // measured headroom; in a Host that runs its runs itself that is its own worker, over the transport.
        builder.Services.AddSingleton(sp => new WorkerAsks(() => sp.GetRequiredService<WorkerPool>().Worker(null)));
        // A person's update runs once, on one connected worker, when the gate has drained (AgentCliUpdater).
        builder.Services.AddSingleton(sp => new AgentCliUpdater(
            sp.GetRequiredService<AgentCatalog>(),
            sp.GetRequiredService<AgentUpdateGate>(),
            sp.GetRequiredService<AgentLaunchUser>(),
            dataRoot,
            sp.GetRequiredService<WorkerAsks>()));
        // THE RUN WORKER, IN THIS PROCESS. Control talks to runs only through the run protocol: a run is
        // started, reported and ended by records over the in-process transport, and this worker is the one
        // place that launches, measures and limits them. ONE worker, shared by the runner, the leases, the
        // reports and the capacity sample (RunWorkerCompositionTests). It is composed here from what control
        // owns - the idle-clock registry, who agent children run as, the shared update gate, the memory
        // settings and the heavy allowance's rule - and decides the rest itself: HOW MUCH MEMORY ONE RUN MAY
        // USE, AND HOW (a cgroup per run, an rlimit per process, or nothing, decided once from the
        // container's own cgroup and logged at start; see RunMemoryLimits), and THE HEAVY ALLOWANCE, which
        // raises the run holding the `heavy` lease to the container's limit less the Host reserve less what
        // the other runs are measured to use, and lowers it when it lets go (RunAllowances). The cgroup and
        // proc roots are configuration so a test Host reads a fixture.
        // WHAT WORKERS STREAM TO CONTROL - a terminal's output, a followed transcript, a file's text - in
        // both roles: in one process the Host's own worker hands its chunks over by direct call.
        builder.Services.AddSingleton<WorkerStreams>();
        // A PERSON'S SIGN-IN BROWSER, run on a worker and kept by control; its rows are the table setting type's,
        // its session the row's secret cell in the member's own secret store.
        builder.Services.AddSingleton<ISignInRows, PluginSignInRows>();
        builder.Services.AddSingleton(sp => new SignInSessions(
            () => sp.GetRequiredService<WorkerPool>().Worker(null), sp.GetRequiredService<WorkerStreams>(),
            sp.GetRequiredService<ISignInRows>(), log: sp.GetRequiredService<ILogger<SignInSessions>>()));
        // THE AGENT'S FILES, read on a worker as the agent: a finished run's transcript and a run's tool check.
        builder.Services.AddSingleton(sp => new WorkerReads(
            WorkersOf(sp), sp.GetRequiredService<WorkerStreams>()));
        builder.Services.AddSingleton(sp => new RunDirectory(
            () => sp.GetRequiredService<IMemberReports>(),
            () => sp.GetRequiredService<IDiagnosticsLog>(),
            sp.GetRequiredService<LiveRuns>(),
            secrets: sp.GetRequiredService<RunSecrets>()));
    }
}
