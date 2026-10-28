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
    internal void Concierge()
    {
        // THE INSTANCE'S LOCAL REPOSITORIES: bare, under <dataRoot>/repos, the Host's and read-only to the
        // agent. A team names one as `local:<name>`; see LocalRepos.
        builder.Services.AddSingleton(sp => new LocalRepos(
            dataRoot, sp.GetRequiredService<GitRunner>(), sp.GetRequiredService<FolderRemoval>()));

        // The one delete of a local repository: Admin -> Repositories' and a team deletion's.
        builder.Services.AddSingleton(sp => new LocalRepoDeletion(
            sp.GetRequiredService<LocalRepos>(), sp.GetRequiredService<TeamRegistry>(), sp.GetRequiredService<ITenantLog>()));

        // FORGIVING TEAM REPOSITORIES: a team's default local repository, and the `ls-remote` check
        // with its choices before a URL is used. The registry is resolved per call: it is built after this.
        builder.Services.AddSingleton<IRemoteRepoCheck>(sp => new RemoteRepoCheck(sp.GetRequiredService<GitRunner>()));
        builder.Services.AddSingleton(sp => new TeamRepoSetup(
            sp.GetRequiredService<LocalRepos>(),
            () => sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<IRemoteRepoCheck>(),
            sp.GetRequiredService<IGitHubContributor>()));
        builder.Services.AddSingleton<LocalRepoOnNeed>();

        // The instance's git identity (GIT_AUTHOR_NAME / GIT_AUTHOR_EMAIL, set with the operator CLI's `secret set`)
        // and the one place a clone is brought in line with its contributor settings.
        builder.Services.AddSingleton(new InstanceGitIdentity(builder.Configuration));
        builder.Services.AddSingleton(sp => new ContributorClone(
            sp.GetRequiredService<GitRunner>(), sp.GetRequiredService<InstanceGitIdentity>()));

        // THE PLATFORM MAKES A TEAM'S MAIN CLONE. Wired here rather than defaulted inside TeamRegistry,
        // because a seam that can be omitted is one something quietly omits - and the omission here looks
        // like a team with a repository and nothing at its path.
        //
        // A TEAM CREATE FINISHES WHEN ITS CALLER GOES AWAY: the create, its clone and a dispatch into it run
        // on the Host's lifetime rather than the request's. See DetachedWork.
        builder.Services.AddSingleton<DetachedWork>();

        builder.Services.AddSingleton<IRepoClone>(sp => new RepoClone(
            sp.GetRequiredService<GitRunner>(), localRepos: sp.GetRequiredService<LocalRepos>(),
            removal: sp.GetRequiredService<FolderRemoval>()));

        // THE PLATFORM PUBLISHES A TEAM'S WORK TO ORIGIN, on the acceptance of a card, BEFORE the row that
        // accepts it. Wired here for the reason `IRepoClone` above it is - a seam that can be omitted is one
        // something quietly omits, and left to agents alone most teams' work would exist only under a team
        // root. See `ITeamPublisher`.
        //
        // NO `enabled` FLAG BESIDE IT, deliberately: with cloning off there is no clone to push from, so the
        // cost this would switch off is a `Directory.Exists` that answers false.
        builder.Services.AddSingleton<ITeamPublisher>(sp => new TeamPublisher(
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<GitRunner>(),
            sp.GetRequiredService<IMessageLog>(),
            // Resolved per call, not at construction, so the publisher does not need the registry
            // to exist before it does.
            (team, repo) => sp.GetRequiredService<TeamRegistry>().DefaultBranchFor(team, repo).Branch,
            // THE TIP A DISPATCH RECORDS, so `landed` outlives the branch and the team.
            (team, repo, sha, causation, ct) =>
                sp.GetRequiredService<BacklogTipRecorder>().RecordTipAsync(team, repo, sha, causation, ct),
            // A DISPATCH'S START NOT RECORDED AT DISPATCH, tried again before the push and its tip.
            (team, causation, ct) => sp.GetRequiredService<BacklogTipRecorder>().RetryForPublishAsync(team, causation, ct)));

        builder.Services.AddSingleton(sp => new BacklogTipRecorder(
            sp.GetRequiredService<IBacklogStore>(),
            sp.GetRequiredService<IMessageLog>(),
            sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<GitRunner>(),
            // READ ON EVERY RECORDING, so a changed appsettings value applies to the next dispatch.
            () => builder.Configuration.GetValue<TimeSpan?>(BacklogTipRecorder.FetchBudgetKey) is { } budget && budget > TimeSpan.Zero
                ? budget
                : BacklogTipRecorder.DefaultFetchBudget));

        builder.Services.AddSingleton(sp => new TeamAccess(
            sp.GetRequiredService<IUserStore>(),
            sp.GetRequiredService<IPrincipalStore>(),
            sp.GetRequiredService<TeamRegistry>()));

        builder.Services.AddSingleton<TeamListPush>();

        // THE ONE REMOVAL a team root, a member's workspace and a reset's folders go through: the marker
        // last, agent content removed as the agent through its launch prefix, and whatever remains recorded
        // to be retried. See FolderRemoval.
        // The agent's pass goes to a connected worker; in control always, in all only where agents run as
        // another user.
        builder.Services.AddSingleton(sp => new FolderRemoval(
            sp.GetRequiredService<AgentLaunchUser>(),
            sp.GetRequiredService<IUnfinishedRemovals>(),
            agent: new WorkerAgentPass(sp.GetRequiredService<WorkerAsks>(), always: control)));

        // IMAGES A PERSON GIVES THEIR CONCIERGE. The cap and the retention are configuration rather than
        // tenant settings: they bound one route and one sweep, and nothing reads them while running.
        builder.Services.AddSingleton(sp => new ConciergeAttachments(
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<AgentLaunchUser>(),
            sp.GetRequiredService<FolderRemoval>(),
            long.TryParse(builder.Configuration["ConciergeAttachmentMaxBytes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attachmentMax)
                && attachmentMax > 0
                ? attachmentMax
                : ConciergeAttachments.DefaultMaxBytes,
            Intervals.ReadPositive(builder.Configuration, "ConciergeAttachmentRetention", ConciergeAttachments.DefaultRetention)));

        // A TEAM'S ENVIRONMENT FOR A PREVIEW, as a file in the Concierge's own folder. See PreviewEnvFiles.
        builder.Services.AddSingleton(sp => new PreviewEnvFiles(
            sp.GetRequiredService<TeamPaths>(), sp.GetRequiredService<AgentLaunchUser>()));

        builder.Services.AddSingleton(sp => new UnfinishedRemovalRetry(
            sp.GetRequiredService<FolderRemoval>(),
            sp.GetRequiredService<IUnfinishedRemovals>(),
            sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<ContainerHost>(),
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<LocalRepos>()));

        // Constructed BY HAND, like ContainerHost, and for the same reason: every dependency here is one a
        // deletion would silently skip if it were optional. A TeamDeletion missing its pending-delivery
        // store deletes a team and leaves rows the next team of that name inherits.
        builder.Services.AddSingleton(sp => new TeamDeletion(
            sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<ContainerHost>(),
            sp.GetRequiredService<ITeamStore>(),
            sp.GetRequiredService<ICursors>(),
            sp.GetRequiredService<ISubscriptions>(),
            sp.GetRequiredService<EffectiveSubscriptions>(),
            sp.GetRequiredService<IPendingDeliveries>(),
            sp.GetRequiredService<ITriggerStore>(),
            sp.GetRequiredService<IPrincipalStore>(),
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<GitRunner>(),
            sp.GetRequiredService<FolderRemoval>(),
            sp.GetRequiredService<SiteService>(),
            sp.GetRequiredService<TeamSkills>(),
            sp.GetRequiredService<ITeamSolutionStore>(),
            wakes: sp.GetRequiredService<IWakeStore>(),
            fileChanges: sp.GetRequiredService<IWorkspaceFileChangeStore>(),
            runSteps: sp.GetRequiredService<IAgentStepStore>(),
            signInProfiles: sp.GetRequiredService<SignInProfileCleanup>()));

        // By hand for the reason TeamDeletion is: every dependency here is one a reset would silently skip
        // if it were optional. A TeamReset missing its pending-delivery store cannot tell a member that has
        // accepted work from an idle one, and would floor a member out from under work it was promised.
        builder.Services.AddSingleton(sp => new TeamReset(
            sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<ContainerHost>(),
            sp.GetRequiredService<ITeamStore>(),
            sp.GetRequiredService<ICursors>(),
            sp.GetRequiredService<IPendingDeliveries>(),
            sp.GetRequiredService<IMessageLog>(),
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<FolderRemoval>(),
            sp.GetRequiredService<RepositoryReset>(),
            sp.GetRequiredService<IWorkspaceFileChangeStore>(),
            sp.GetRequiredService<IAgentStepStore>(),
            purged: () =>
            {
                sp.GetRequiredService<KanbanStore>().Invalidate();
                sp.GetRequiredService<RollupCache>().Invalidate();
                sp.GetRequiredService<WorkflowEndings>().Invalidate();
            }));

        // Reset repositories: trees through the one WorktreeRemoval, git through the one GitRunner (as the
        // agent, and into a local origin as the Host), kept branches named on the log.
        builder.Services.AddSingleton(sp => new RepositoryReset(
            sp.GetRequiredService<GitRunner>(),
            sp.GetRequiredService<WorktreeRemoval>(),
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<IMessageLog>()));

        // By hand for the reason TeamDeletion is: every dependency here is one a deletion would silently
        // skip if it were optional, and a MemberDeletion missing its pending-delivery store leaves rows the
        // NEXT member of that name inherits - on a team that is still running, so there is no "the team is
        // gone" to explain the failure away with.
        builder.Services.AddSingleton(sp => new MemberDeletion(
            sp.GetRequiredService<TeamRegistry>(),
            sp.GetRequiredService<ContainerHost>(),
            sp.GetRequiredService<ICursors>(),
            sp.GetRequiredService<EffectiveSubscriptions>(),
            sp.GetRequiredService<IPendingDeliveries>(),
            sp.GetRequiredService<ITriggerStore>(),
            sp.GetRequiredService<IPrincipalStore>(),
            sp.GetRequiredService<TeamPaths>(),
            sp.GetRequiredService<IMessageLog>(),
            builder.Configuration["GitExecutable"] ?? "git",
            sp.GetRequiredService<AgentLaunchUser>(),
            sp.GetRequiredService<FolderRemoval>(),
            sp.GetRequiredService<SignInProfileCleanup>()));

        // Git runner for repository operations: as the agent, and GH_TOKEN only for a team the
        // registry says has a GitHub remote - see GitRunner's class comment.
        builder.Services.AddSingleton(sp => new GitRunner(
            builder.Configuration["GitExecutable"] ?? "git",
            runAs: sp.GetRequiredService<AgentLaunchUser>(),
            remotesFor: clonePath =>
            {
                var registry = sp.GetRequiredService<TeamRegistry>();
                var paths = sp.GetRequiredService<TeamPaths>();
                var clone = Path.GetFullPath(clonePath);
                foreach (var team in registry.All())
                {
                    try
                    {
                        var repos = Path.GetFullPath(paths.ReposFor(team.Id)) + Path.DirectorySeparatorChar;
                        if (clone.StartsWith(repos, StringComparison.Ordinal)) return registry.RemotesFor(team.Id);
                    }
                    catch (KeyNotFoundException)
                    {
                    }
                }

                return [];
            },
            // A clone whose origin is, by the registry, a local repository: its pushes go into the bare
            // repository as the Host, and GH_TOKEN is never handed to it.
            localOriginFor: clonePath =>
            {
                var registry = sp.GetRequiredService<TeamRegistry>();
                var paths = sp.GetRequiredService<TeamPaths>();
                var clone = Path.GetFullPath(clonePath);
                foreach (var team in registry.All())
                {
                    try
                    {
                        var repos = Path.GetFullPath(paths.ReposFor(team.Id)) + Path.DirectorySeparatorChar;
                        if (!clone.StartsWith(repos, StringComparison.Ordinal)) continue;

                        var folder = clone[repos.Length..].Split(Path.DirectorySeparatorChar)[0];
                        var url = registry.ReposFor(team.Id)
                            .FirstOrDefault(u => string.Equals(RepoUrls.DeriveName(u), folder, StringComparison.OrdinalIgnoreCase));
                        return url is not null && LocalRepos.IsLocal(url)
                            ? sp.GetRequiredService<LocalRepos>().PathForReference(url)
                            : null;
                    }
                    catch (KeyNotFoundException)
                    {
                    }
                }

                return null;
            }));

        // The one place a settled card's worktree is removed - never forced - shared by the
        // workflow-completed handler, member deletion and the Git dialog's clean-up button.
        builder.Services.AddSingleton(sp => new WorktreeRemoval(
            sp.GetRequiredService<GitRunner>(), sp.GetRequiredService<IMessageLog>()));

        // The Backlog's `landed` derivation shells out to git, and the list is re-read on every
        // visit to the screen - so one measurement per team is shared for a short lifetime rather than
        // re-run per request. A SINGLETON because that is the only scope a cache can usefully have here.
        builder.Services.AddSingleton(new BacklogLandedCache());

        // What contributor mode asks GitHub - fork, find, open and read a pull request - through gh
        // with the instance's GH_TOKEN, as the agent user, and only from a person's button. Tests replace
        // it with a fake: no automated test calls GitHub. The reader asks at most once a minute per pull request.
        builder.Services.AddSingleton<IGitHubContributor>(sp => new GhContributor(sp.GetRequiredService<AgentLaunchUser>()));
        builder.Services.AddSingleton(sp => new PullRequestStateReader(
            sp.GetRequiredService<IGitHubContributor>(), sp.GetRequiredService<TeamRegistry>()));

        // A PERSON'S TERMINAL RUNS ON A WORKER, in both roles: control relays it (WorkerPtyEngine), and in one
        // process the Host's own worker spawns it over the in-process transport.
        builder.Services.AddSingleton(sp => new WorkerPtyEngine(
            WorkersOf(sp), sp.GetRequiredService<WorkerStreams>(),
            log: sp.GetRequiredService<ILogger<WorkerPtyEngine>>()));
        builder.Services.AddSingleton<IPtyEngine>(sp => sp.GetRequiredService<WorkerPtyEngine>());
        builder.Services.AddSingleton<SteeringWorkflows>();
        builder.Services.AddSingleton<ConciergeLaunches>();
        builder.Services.AddSingleton(sp => new ConciergeLaunchFactory(
            sp.GetRequiredService<TeamPaths>(),
            memberBaseAddress,
            sp.GetRequiredService<IPrincipalStore>(),
            sp.GetRequiredService<AgentCatalog>(),
            sp.GetRequiredService<SkillDirectory>(),
            // Who the agent runs as is the worker's to decide and refuse; control alone has no agent child.
            control ? null : sp.GetRequiredService<AgentLaunchUser>(),
            sp.GetRequiredService<AgentUpdateGate>(),
            previewPorts));
        builder.Services.AddSingleton(sp =>
        {
            // Resolved ONCE, here, and captured - never re-resolved inside the delegates. The revoke runs
            // from ConciergeSessionStore.DisposeAsync during host shutdown, by which time the service
            // provider is already disposed, so a `sp.GetRequiredService` in there throws
            // ObjectDisposedException out of disposal and takes every live session's cleanup with it.
            var launcher = sp.GetRequiredService<ConciergeLaunchFactory>();
            var leaseActions = sp.GetRequiredService<LeaseActions>();
            var launches = sp.GetRequiredService<ConciergeLaunches>();

            return new ConciergeSessionStore(
                sp.GetRequiredService<IPtyEngine>(),
                launches.LaunchAsync,
                // A Concierge's run is its session: a lease it took ends with it, through the same path
                // as a run's end, so a member the queue hands it to is un-paused and its card told.
                // And the images the person attached and the preview env files go with it, however it ended.
                sp.GetRequiredService<ConciergeAttachments>().RemovingOnEnd(
                    sp.GetRequiredService<PreviewEnvFiles>().RemovingOnEnd(
                        leaseActions.Releasing((key, ct) => launcher.RevokeAsync(key.User, ct)))),
                sp.GetRequiredService<TimeProvider>(),
                launches.FloorOf,
                sp.GetRequiredService<ILogger<ConciergeSessionStore>>());
        });

        // HOW DEEP ONE CHAIN OF CAUSATION MAY GO before the platform stops it.
        //
        // `depth` is computed and stored on every message, and this is what reads it. Team scoping stops
        // two teams' managers waking each other, and does nothing whatever about a manager and one worker
        // ping-ponging INSIDE one team. `Ceiling` does not help - it bounds a queue's depth, which is how
        // much work is waiting, not how far from a human's instruction that work has drifted.
        //
        // The bound is on CAUSATION rather than on message count on purpose: a manager fanning out to six
        // workers at once is fine and is depth 1, while a manager and a worker answering each other twenty
        // times is the runaway, and only depth tells them apart.
        //
        // 25 is generous rather than tuned. A real job - console instructs manager, manager dispatches to a
        // developer, developer completes, manager dispatches verification, tester completes, manager reports
        // - is six or seven, so this is roughly four times the deepest thing anyone has run. It is a
        // backstop against a loop, not a budget anyone should feel.
        //
        // IT IS EVADABLE, and that is worth stating rather than discovering. An agent that omits its
        // causation starts a fresh chain at depth 0, and nothing here can tell that from a person opening a
        // new job. The `tell` tool always sends it, so this bounds the ACCIDENT - the manager that keeps answering
        // itself - which is the failure that actually costs money. It is not a defence against an agent
        // deliberately covering its tracks, and a tighter one would need a per-invocation credential.
        //
        // `causation.depthLimit`: read on every tell from `tenantSettings`, 0 meaning no limit.
    }
}
