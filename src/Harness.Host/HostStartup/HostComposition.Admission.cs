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
    internal void Admission()
    {
        if (!control)
        {
        builder.Services.AddSingleton(sp => InProcessWorker.Create(
            WorkerId.Local,
            sp.GetRequiredService<RunHeartbeat>(),
            sp.GetRequiredService<AgentLaunchUser>(),
            sp.GetRequiredService<AgentUpdateGate>(),
            tenantSettings.RunMemoryLimit,
            tenantSettings.RunMemoryCeiling,
            tenantSettings.HeavyRunMemoryLimit,
            async (envelope, ct) =>
            {
                await sp.GetRequiredService<WorkerAsks>().HandleAsync(envelope, ct);
                await sp.GetRequiredService<RunDirectory>().HandleAsync(envelope, ct);
                if (envelope.Event is RunMeasured or TerminalMeasured or WorkerCapacitySampled)
                {
                    await sp.GetRequiredService<CapacitySampler>().HandleAsync(envelope, ct);
                }

                await sp.GetRequiredService<WorkerStreams>().HandleAsync(envelope, ct);
                if (envelope.Event is SignInBrowserSaved) await sp.GetRequiredService<SignInSessions>().HandleAsync(envelope, ct);
            },
            sp.GetRequiredService<ILogger<ProcessAgentRunner>>(),
            sp.GetRequiredService<ILogger<RunAllowances>>(),
            builder.Configuration["Capacity:CgroupRoot"],
            builder.Configuration["Capacity:ProcRoot"],
            sp.GetRequiredService<WorkerStreams>().Deliver,
            tester: sp.GetService<ConnectionTester>()));
        builder.Services.AddSingleton<IRunWorker>(sp =>
        {
            var worker = sp.GetRequiredService<InProcessWorker>().Worker;
            sp.GetRequiredService<WorkerPool>().Connect(worker);
            return worker;
        });
        builder.Services.AddSingleton(sp => sp.GetRequiredService<InProcessWorker>().Memory!);
        builder.Services.AddSingleton(sp => sp.GetRequiredService<InProcessWorker>().Allowances!);
        }
        else
        {
            // CONTROL ALONE: no run worker in this process, so no launcher and no run's memory mechanism here.
            // Every run goes to the worker its member was placed on, each of which decides its own mechanism.
            builder.Services.AddSingleton<IRunWorker>(sp => new RunWorkers(
                sp.GetRequiredService<WorkerPool>(), member => sp.GetRequiredService<WipLedger>().PlacedOn(member)));
            builder.Services.AddSingleton(_ => RunMemoryLimits.NotAvailable(
                "this Host runs no member itself (--Role control): each worker limits its own runs and logs how when it starts"));
        }
        // Member TMPDIRs on the data volume, never a /tmp the engine may hold in memory (MemberTemp).
        builder.Services.AddSingleton(_ => MemberTemp.RootUnder(dataRoot));
        // BY FACTORY: the runner has a second constructor, which builds a worker of its own from parts, and
        // production's is this one, over the shared worker.
        builder.Services.AddSingleton(sp => new ProcessAgentRunner(
            sp.GetRequiredService<AgentCatalog>(),
            sp.GetRequiredService<IRunWorker>(),
            sp.GetRequiredService<RunDirectory>(),
            control ? null : sp.GetRequiredService<InProcessWorker>().Host.Launcher,
            () => ProcessAgentRunner.Allowance((tenantSettings.RunMemoryLimit(), tenantSettings.RunMemoryCeiling())),
            sp.GetRequiredService<MemberTempRoot>().Path,
            member => sp.GetRequiredService<WorkerPool>().Worker(sp.GetRequiredService<WipLedger>().PlacedOn(member)),
            control
                ? member => sp.GetRequiredService<WorkerPool>().For(sp.GetRequiredService<WipLedger>().PlacedOn(member)) is RemoteWorker placed
                    ? ProcessAgentRunner.Allowance((
                        tenantSettings.RunMemoryLimit(WorkerBounds.Of(placed.Info)), tenantSettings.RunMemoryCeiling(WorkerBounds.Of(placed.Info))))
                    : null
                : null,
            // In control no worker in this process takes a run's share of its CLI's install, so the runner
            // takes it from this control's one gate, around every run on every worker. In all the Host's own
            // worker takes it from the same gate, and the runner does not, so a run counts once.
            control ? sp.GetRequiredService<AgentUpdateGate>() : null,
            () => sp.GetRequiredService<IMemberReports>()));
        builder.Services.AddSingleton<IAgentRunner>(sp => new CredentialUseRunner(
            sp.GetRequiredService<ProcessAgentRunner>(),
            sp.GetRequiredService<IPrincipalStore>(),
            sp.GetRequiredService<AgentCatalog>()));

        // WHAT A MEMBER'S REPORT DOES - row, mark, card push, idle clock - written once, for the MCP routes
        // and for a plugin member's stdout alike.
        builder.Services.AddSingleton(sp => new MemberReports(
            sp.GetRequiredService<ContainerHost>(),
            sp.GetRequiredService<IMessageLog>(),
            sp.GetRequiredService<IRunWorker>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<RunSecrets>()));
        builder.Services.AddSingleton<IMemberReports>(sp => sp.GetRequiredService<MemberReports>());

        // WHAT EVERY MEMBER RUNS THROUGH. The member runtime hands its work, as data, to this; the agent
        // adapter turns it into the prompt and history an agent CLI has always been given, over the
        // IAgentRunner stack above. Tests that substitute IAgentRunner keep working because this reads it.
        //
        // ROUTED PER INVOCATION: `plugin:<id>` to the plugin runner, anything else to the agent adapter. The
        // choice is made here and nowhere in the pump.
        builder.Services.AddSingleton(sp => new AgentMemberRunner(
            sp.GetRequiredService<IAgentRunner>(),
            sp.GetRequiredService<IContextBuilder>()));
        builder.Services.AddSingleton(sp => new PluginMemberRunner(
            sp.GetRequiredService<PluginCatalog>(),
            sp.GetRequiredService<IMemberReports>(),
            sp.GetRequiredService<IRunWorker>(),
            sp.GetRequiredService<RunDirectory>(),
            sp.GetRequiredService<AgentLaunchUser>(),
            sp.GetRequiredService<IPluginMemberSettingsStore>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<Connections>(),
            sp.GetRequiredService<SiteService>(),
            memberSecrets: sp.GetRequiredService<IPluginMemberSecrets>())
        {
            // TEAM CONNECTIONS' `test <name>` runs the connection's own test, on a worker, as a person's does.
            ConnectionTests = sp.GetRequiredService<WorkerAsks>(),
            Answers = sp.GetRequiredService<IMessageLog>(),
            // A website check's screenshot goes to the team's documents; a session to the asking member's workspace.
            Documents = sp.GetRequiredService<TeamDocuments>(),
            Paths = sp.GetRequiredService<TeamPaths>(),
        });
        builder.Services.AddSingleton<IMemberRunner>(sp => new MemberRunnerRouter(
            sp.GetRequiredService<AgentMemberRunner>(),
            sp.GetRequiredService<PluginMemberRunner>()));


        // Instance-wide, shared across users. 0 is unlimited. Absent means the smaller of max(1, CPUs - 1)
        // and, under a container memory limit, limit / wip.memoryPerRunMb (`TenantSettings.RunLimit`). The container's cgroup is a blast radius; this is
        // the scheduler. Held work is listed by GET /api/wip.
        //
        // `wip.maxRunning`: settable at runtime. One place reads it - here, at start, and on every
        // change through `WipLedger.SetMax`, which applies it under the ledger's lock and evicts nothing.
        //
        // AND MEASURED HEADROOM: a run the limit has room for still waits while memory in use or memory
        // pressure is over its `admission.*` threshold, read through delegates from the capacity sampler's
        // last measurement (`HeadroomGate`). Not measured falls back to the limit alone. The cgroup and proc
        // roots are configuration so a test Host reads a fixture, never the machine it runs on.
        //
        // AND A WORKER: admission places each run on a worker with room, and the headroom it reads is that
        // worker's, fed by its own capacity samples. In `all`, one: the Host's own (WorkerPool).
        headroom = new HeadroomGate(
            () => tenantSettings.AdmissionMemoryPercent, () => tenantSettings.AdmissionMemoryPressurePercent);
        // In control the pool starts empty and fills as workers connect, each with a gate of its own and,
        // under the default run limit, its own bound; the default limit is then the sum of their bounds.
        // Workers are ranked by headroom less wip.memoryPerRunMb for each run placed since their last
        // sample, so a burst spreads.
        workers = control
            ? new WorkerPool(
                _ => new HeadroomGate(() => tenantSettings.AdmissionMemoryPercent, () => tenantSettings.AdmissionMemoryPressurePercent),
                info => tenantSettings.WorkerBound(WorkerBounds.Of(info)),
                memoryPerRunMb: () => tenantSettings.WipMemoryPerRunMb)
            : new WorkerPool([(WorkerId.Local, headroom)]);
        if (control)
        {
            tenantSettings.Workers = () => [.. workers.Entries().Where(e => e.DroppedAt is null).Select(e => WorkerBounds.Of(e.Info))];
        }

        wip = new WipLedger(tenantSettings.WipMaxRunning, workers, admissionHoldWriter);
        workers.Placed = wip.PlacedCount;
        builder.Services.AddSingleton(headroom);
        builder.Services.AddSingleton(workers);

        // The `heavy` lease's holders and queue, read from the instance's leases on every sample; the
        // capacity sample and its push carry them.
        builder.Services.AddSingleton<IHeavyLeaseView>(sp => new HeavyLeaseFromLeases(sp.GetRequiredService<ILeaseState>()));
        // The worker measures - its cgroup and each run's process group, on SampleCapacity - and this
        // sampler composes the sample from what it says.
        builder.Services.AddSingleton(sp => new CapacitySampler(
            control
                ? () => [.. workers.Entries().Where(e => e.Worker is not null && e.DroppedAt is null).Select(e => (e.Worker!, e.Gate))]
                : () => [(sp.GetRequiredService<IRunWorker>(), workers.Gate(WorkerId.Local))],
            wip,
            sp.GetRequiredService<IHeavyLeaseView>(),
            () => tenantSettings.AdmissionMemoryPercent,
            () => tenantSettings.AdmissionMemoryPressurePercent,
            push: sample => sp.GetRequiredService<IHubContext<ContainerHub>>()
                .Clients.Group(ContainerHub.PeopleGroup).SendAsync(CapacityEndpoints.PushName, sample),
            logger: sp.GetRequiredService<ILogger<CapacitySampler>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CapacitySampler>());
    }
}
