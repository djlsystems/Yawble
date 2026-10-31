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
    internal void Containers()
    {
        tenantSettings.Changed += name =>
        {
            // The per-run allowance moves the default, so a change to it re-reads the limit too.
            if (name is TenantSettings.WipMaxRunningName or TenantSettings.WipMemoryPerRunMbName)
            {
                wip.SetMax(tenantSettings.WipMaxRunning);
            }
        };
        builder.Services.AddSingleton(wip);

        builder.Services.AddSingleton(sp => new ContainerHost(
            sp.GetRequiredService<IMessageLog>(),
            sp.GetRequiredService<ICursors>(),
            sp.GetRequiredService<ISubscriptions>(),
            sp.GetRequiredService<ITranscriptStore>(),
            sp.GetRequiredService<IPendingDeliveries>(),
            sp.GetRequiredService<ITriggerStore>(),

            // WIRED HERE OR THE BOUND DOES NOTHING. The seam is an optional parameter, so an unwired host
            // is a host that never refuses a wake - and nothing would fail. `WorkflowBudgetAtTheWakeTests`
            // is the only thing that would notice, which is the same argument that keeps
            // `CredentialUseRunner`'s decorator wired. Read at every wake, never captured.
            workflowSpendLimitNow: () => tenantSettings.WorkflowSpendLimit,

            // THE FIGURE ACTUALLY IN FORCE FOR A TEAM, resolved per wake. Late-bound through the
            // provider rather than captured: `TeamRegistry` is registered BELOW this line and depends on
            // things registered below it again, so a captured instance would be a circular construction.
            // `sp` is the root provider and the registry is a singleton, so this resolves the same object
            // the routes write through - which is what makes a figure changed on a screen take effect on
            // the very next wake.
            //
            // WIRED HERE OR THE TEAM'S NUMBER DOES NOTHING. The seam is optional, so an unwired host
            // quietly falls back to the instance figure above and every team setting is ignored - and
            // nothing fails. The same argument that keeps the line above it wired.
            effectiveWorkflowBudget: (team, _) =>
                ValueTask.FromResult(sp.GetRequiredService<TeamRegistry>().EffectiveWorkflowBudgetFor(team)),

            wip: wip,

            // A MEMBER'S TREE FOR THE CARD IT WAS WOKEN FOR, per invocation, as `HARNESS_WORKTREE`
            // and in the instruction text. Late-bound through the provider for the reason above, and read
            // live so a repository added to a team after its members were created is named on the next wake.
            worktrees: (member, key) => sp.GetRequiredService<TeamRegistry>().WorktreesFor(member, key),

            // AN EVENT OR FOLDER TRIGGER HELD, asked by the pump before it fires one: its team is archived
            // (TeamArchive), its solution team waits for a missing required input (SolutionWait), or its
            // daily token cap is reached. Late-bound for the reason above: TriggerCost's tenant log is
            // registered below this line.
            triggerHeld: async (trigger, member, cause, ct) =>
                await sp.GetRequiredService<TeamArchive>().SkipIfArchivedAsync(trigger, member, DateTimeOffset.UtcNow, cause.Seq, ct)
                || await sp.GetRequiredService<SolutionWait>().SkipIfWaitingAsync(trigger, member, DateTimeOffset.UtcNow, cause.Seq, ct)
                || await sp.GetRequiredService<TriggerCost>()
                    .SkipIfCappedAsync(trigger, member, DateTimeOffset.UtcNow, nextDueAt: null, cause.Seq, ct),

            // Whether a member's card shows the eye: its preset has a live view this Host can read.
            // Asked per snapshot, so a catalog edit or a repoint shows on the next one.
            watchable: agent => LiveView.Watchable(
                sp.GetRequiredService<AgentCatalog>().Definition(agent)?.LiveView,
                sp.GetRequiredService<AgentLaunchUser>()),

            // THE PLATFORM PUBLISHES A TEAM'S BRANCHES WHEN A RUN ENDS, NOT ONLY WHEN A CARD IS ACCEPTED.
            //
            // The acceptance hook below (`workflow-complete`) is the first place a team's work is
            // published and it is necessary; it is not sufficient, because a run KILLED between doing the work and finishing
            // its turn never declares anything and never reaches that route. This is the second call site
            // of the SAME publisher, on the event the platform observes whatever the agent managed to do -
            // see `TerminalPublish`, which also states the bound a dying container waits under.
            //
            // Late-bound through the provider, because `TeamRegistry` is
            // registered below this line, so a captured instance would be a circular construction.
            //
            // THE IDLE-WORKFLOW OFFER ALSO HANGS HERE, AND SECOND. `TerminalPublish` puts the team's branches on origin; the
            // offer may WAKE the Manager under this same workflow, and a Manager woken to declare a
            // workflow whose branches have not been pushed yet would be racing the push it is about to
            // accept - the same ordering argument `workflow-complete` makes for publishing before it
            // appends. See `IdleWorkflowOffer` for what it decides and every reason it decides not to.
            // THE PER-RUN FOREIGN TOOLS CHECK, on the row that carries the run, inside the run: the
            // transcript the agent wrote is read for tools the platform did not give it. Late-bound for
            // the reason above. See ForeignToolsCheck.
            //
            // THEN A CONNECTION HOLD, on a Team Connections run a website held up (CON 6): it uses the
            // team's connections, not a bound slot, so the hold is read from its failed row. See
            // PluginSetup.RunEndedAsync.
            onTerminal: async (terminal, ct) =>
            {
                await sp.GetRequiredService<ForeignToolsCheck>().CheckAsync(terminal, ct);
                if (sp.GetService<PluginSetup>() is { } setup) await setup.RunEndedAsync(terminal, ct);
            },

            // AN ARCHIVED TEAM, asked by the pump for a paused team: its trigger-only events are skipped
            // (TeamArchive, through triggerHeld above) rather than left to fire at Resume. Late-bound for the
            // reason above.
            archived: team => sp.GetRequiredService<TeamRegistry>().IsArchived(team),

            onRunEnding: (member, causation, succeeded, ct) => RunEnding.RunAsync(
                sp.GetRequiredService<ILogger<ContainerHost>>(), member, causation, succeeded, ct,

                // NO LEASE OUTLIVES ITS RUN. First, because a run waiting on a push should not keep another
                // team's heavy work waiting too. Every way a run ends reaches this hook - completed, failed,
                // stopped, a runner that threw - and a Host that goes down holds no lease when it comes
                // back: they live in memory. Each step is guarded by RunEnding, so a release that throws
                // (a worker dropped as its run ended) never skips the publish below.
                releaseLeases: () => sp.GetRequiredService<LeaseActions>().EndedAsync(LeaseOwner.For(member), ct),

                // THE PUBLISH IGNORES `succeeded` AND THAT IS THE WHOLE POINT: a run killed between
                // doing the work and finishing its turn is the case this exists for, so its branch
                // matters MORE than a clean run's, not less.
                publish: () => TerminalPublish.OnRunEndingAsync(
                    sp.GetRequiredService<TeamRegistry>(),
                    sp.GetRequiredService<ITeamPublisher>(),
                    member,
                    causation,
                    ct,

                    // THE ONLY ARGUMENT PRODUCTION PASSES BEYOND THE REQUIRED ONES, and it is what
                    // stops the bound below giving the ordering away on a busy machine: `TerminalPublish`
                    // waits while git is still starting and finishing operations, and gives up when it has
                    // gone quiet. Without this the bound is elapsed time, and elapsed time cannot tell a
                    // slow publish from a hung origin - a HEALTHY push of a dozen branches to a LOCAL
                    // origin can take well over ten seconds on a loaded host. See `TerminalPublish.Budget`.
                    activity: () => sp.GetRequiredService<GitRunner>().Activity),

                // A MANAGER THAT MOVED ITS CLONE'S DEFAULT BRANCH IS REPORTED, BEFORE THE TERMINAL ROW, so
                // the report lands on its card and in the feed. Reported only; nothing is reset. See
                // `DefaultBranchMove`.
                defaultBranch: () => DefaultBranchMove.OnRunEndingAsync(
                    sp.GetRequiredService<TeamRegistry>(),
                    sp.GetRequiredService<TeamPaths>(),
                    sp.GetRequiredService<GitRunner>(),
                    sp.GetRequiredService<IMessageLog>(),
                    member,
                    causation,
                    ct),

                idleOffer: reads => sp.GetRequiredService<IdleWorkflowOffer>()
                    .OnRunEndingAsync(member, causation, succeeded, reads, ct),

                // A WORKFLOW ITS OWNER CANNOT DECLARE - a person told a plugin member directly - is
                // declared by the platform when nothing is left working it. The offer above goes only to
                // an owner that CAN declare, so at most one of the two acts. See `UndeclarableWorkflows`.
                //
                // ITS ANSWER IS THE HOOK'S: the run's terminal row says the workflow was declared, and the
                // pump passes over the Manager on it (PayloadFields.WorkflowDeclared).
                //
                // AND A WORKFLOW ALREADY DECLARED WHEN THE RUN ENDS SAYS SO THE SAME WAY - the owner of a
                // member-owned workflow declaring it in this run, typically on the idle offer. The offer went
                // to the owner only; waking the Manager on the run that answered it would hand it the same
                // decision, already made. Never on the Manager's own run, which wakes no Manager anyway; the
                // runtime keeps the key off a row closing a member's own `tell`.
                declare: async reads => await sp.GetRequiredService<UndeclarableWorkflows>()
                        .OnRunEndingAsync(member, causation, succeeded, reads, ct)
                    || (!member.Equals(new ContainerId(member.Team, TeamRegistry.DefaultManagerName))
                        && await WorkflowDeclaration.DeclaredAsync(sp.GetRequiredService<IMessageLog>(), causation, ct)))));
        // A container's credential and its environment, in one place - see AgentEnvironment for why those
        // two belong together rather than either side of the registry.
        // Registered ABOVE AgentEnvironment, which depends on it: TeamDocuments is the one place that
        // decides where a team's documents live, and a container is told that path as HARNESS_SHARED.
        // Composing the path a second time from dataRoot would be two stores of one fact.
        builder.Services.AddSingleton(new TeamDocuments(teamPaths));

        // WHERE A MEMBER CALLS BACK. Derived from this host's own binding rather than a literal port -
        // see MemberBaseAddress for why a literal port breaks every other instance's members, and why the
        // answer is the dev server's: derive it, and print what was chosen.
        memberBaseAddress = MemberBaseAddress.Resolve(
            builder.Configuration["MemberBaseAddress"],
            builder.Configuration["urls"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));

        // The MCP tools call the platform's own routes back on the address members are told, never on
        // the request's Host header: the caller writes that header.
        builder.Services.AddHttpClient(
            PlatformMcpTools.ClientName, client => client.BaseAddress = new Uri(memberBaseAddress));

        builder.Services.AddSingleton(sp => new AgentEnvironment(
            sp.GetRequiredService<IPrincipalStore>(),
            sp.GetRequiredService<AgentCatalog>(),
            memberBaseAddress,
            sp.GetRequiredService<TeamDocuments>()));

        // THE ONLY WRITER OF `subscriptions` - see EffectiveSubscriptions' own doc comment. Registered
        // before TeamRegistry, which is its first caller, and before MemberDeletion/TeamDeletion, its other
        // two.
        builder.Services.AddSingleton(sp => new EffectiveSubscriptions(
            sp.GetRequiredService<ITeamStore>(),
            sp.GetRequiredService<ITriggerStore>(),
            sp.GetRequiredService<ISubscriptions>(),
            sp.GetRequiredService<ContainerHost>()));

        builder.Services.AddSingleton(sp => new TeamRegistry(
            sp.GetRequiredService<ContainerHost>(),
            sp.GetRequiredService<AgentCatalog>(),
            sp.GetRequiredService<IMemberRunner>(),
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<ITeamStore>(),
            sp.GetRequiredService<AgentEnvironment>(),
            sp.GetRequiredService<FileBrowserPolicy>(),
            sp.GetRequiredService<IMessageLog>(),
            sp.GetRequiredService<EffectiveSubscriptions>(),
            sp.GetRequiredService<IRepoClone>(),

            // THE INSTANCE FIGURE, read at every call: the one `EffectiveWorkflowBudgetFor` falls back to
            // for a team that has chosen nothing.
            workflowSpendLimitNow: () => tenantSettings.WorkflowSpendLimit,
            skillDirectory: sp.GetRequiredService<SkillDirectory>(),
            // Resolved per call: the git runner reads the registry, so it cannot be built first.
            prepareClone: (contributor, clonePath, ct) =>
                sp.GetRequiredService<ContributorClone>().ApplyAsync(clonePath, contributor, ct),
            plugins: sp.GetRequiredService<PluginCatalog>(),
            pluginSettings: sp.GetRequiredService<IPluginMemberSettingsStore>(),
            secrets: sp.GetRequiredService<ISecretStore>(),
            removal: sp.GetRequiredService<FolderRemoval>(),
            localRepos: sp.GetRequiredService<LocalRepos>(),
            solutions: sp.GetRequiredService<ITeamSolutions>(),
            connections: sp.GetRequiredService<IConnectionStore>()));
    }
}
