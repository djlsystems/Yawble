using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;
using Harness.Kanban;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Host;

/// <summary>
/// Repo Dashboard endpoints: GET /api/teams/{team}/repo-status and the seven POST actions of the
/// git ladder - fetch, bring-current, rebase, push, merge-to-main, delete-remote-branch and
/// cleanup-worktrees. Every one of the seven answers a <see cref="RepoActionResult"/>, carrying the
/// status it produced, so a card repaints from the response rather than asking again.
/// </summary>
public static partial class RepoEndpoints
{
    /// <summary>
    /// Appended to every action route that needs the repository's default branch: "main" in
    /// these routes' names and descriptions is that stored branch, never a literal.
    /// </summary>
    private const string DefaultBranchNote =
        " Here `main` and `origin/main` mean the repository's stored default branch (see "
        + "`defaultBranch` on the repo status). When it is not known the route answers 409 and "
        + "changes nothing; it never assumes `main`.";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/teams/{team}/repo-status", GetRepoStatusAsync)
            .WithName("RepoStatus")
            .WithDescription("Get the status of all repositories on a team, with git/gh prerequisite status")
            .RequirePermit(Permits.Read)
            .WithTags("Repos")
            .WithSummary("Get repository status for a team");

        app.MapPost("/api/teams/{team}/repos/{repo}/bring-current", BringCloneCurrentAsync)
            .WithName("BringCloneCurrent")
            .WithDescription(
                "Fetch and fast-forward merge a repo's main branch." + DefaultBranchNote
                + " In contributor mode (the repository has an upstream) both remotes are fetched, "
                + "main is fast-forwarded from upstream/<default>, and the fork's <default> is then "
                + "fast-forwarded to upstream/<default> with a push that is never forced (Sync fork). A fork "
                + "whose <default> has commits upstream does not have is refused with 409, naming how many, "
                + "and nothing is changed. `repo.forkSynced` is appended after that push succeeds.")
            .HumansOnly()
            .WithTags("Repos")
            .WithSummary("Bring a repository's main branch current");

        // IT NEVER REPLAYS THE TEAM'S COMMITS ONTO A NEW BASE. When the team branch is behind
        // origin/main this makes a REAL MERGE COMMIT whose second parent is the team's own sha, so
        // what reaches main is exactly what the team tested plus whatever main already had, and that
        // commit is still readable in history afterwards. main's history is not linear as a result and that
        // cost is accepted - `/rebase` remains for a team that wants a linear branch, but it
        // is not the road to a merge. Still fast-forward-only on the push,
        // still no force, anywhere.
        app.MapPost("/api/teams/{team}/repos/{repo}/merge-to-main", MergeToMainAsync)
            .WithName("MergeToMain")
            .WithDescription(
                "Fast-forwards origin/main to the team branch when it already contains origin/main, "
                + "and otherwise joins the two with a merge commit - never by replaying the team's "
                + "commits, so the team's sha survives as a parent and the branch on origin does not "
                + "move. A merge that conflicts is refused and changes nothing at all: the merge is "
                + "computed in the object database, so no branch moves, no file is touched, and "
                + "there is no half-merged state to abort. In contributor mode it "
                + "answers 409 and changes nothing: the work goes upstream with Open pull request." + DefaultBranchNote)
            .HumansOnly()
            .WithTags("Repos")
            .WithSummary("Merge a repository's team branch to main");

        // Bring current and merge: origin/main into the team branch, push, then Merge to main.
        MapBringCurrentAndMerge(app);

        // Open pull request, its draft, and Fork it for me.
        MapPullRequest(app);

        app.MapPost("/api/teams/{team}/repos/{repo}/cleanup-worktrees", CleanupRepoWorktreesAsync)
            .WithName("CleanupRepoWorktrees")
            .WithDescription("Remove deleted or unmerged worktrees")
            .HumansOnly()
            .WithTags("Repos")
            .WithSummary("Clean up repository worktrees");

        // .HumansOnly(), matching every action route in this file: WHO may act on a team's
        // repository is one decision, made once.
        app.MapPost("/api/teams/{team}/repos/{repo}/fetch", FetchRepoAsync)
            .WithTags("Repos")
            .HumansOnly()
            .WithSummary("Fetch origin, so the card can tell the truth about it")
            .WithDescription(
                "Non-destructive and idempotent: it updates remote-tracking refs and moves nothing else. "
                + "An unreachable origin is reported as such rather than as a failure of the dialog, and "
                + "the card keeps its existing values wearing their age. A successful fetch also runs "
                + "`git remote set-head origin --auto` and stores the branch origin's HEAD names as the "
                + "repository's default branch (not known when that fails); a person's choice from Team "
                + "settings is kept. A failed fetch changes nothing stored.");

        // A diverged clone cannot be brought home by a fast-forward, and a rebase that goes wrong
        // is materially more dangerous than a fetch, a merge or a worktree cleanup - so it gets no
        // less protection than any of them.
        //
        // IT IS NOT THE ROAD TO A MERGE, AND IT IS KEPT ANYWAY. `merge-to-main` merges a team branch
        // that is behind origin/main for itself, because replaying the tested commits onto a new
        // base discards that testing and can make the branch on origin unmergeable for good.
        // This one stays, as a button for a team that WANTS a linear branch, chosen rather
        // than forced.
        app.MapPost("/api/teams/{team}/repos/{repo}/rebase", RebaseRepoAsync)
            .WithTags("Repos")
            .HumansOnly()
            .WithSummary("Rebase the clone's main onto origin/main")
            .WithDescription(
                "Attempts `git rebase origin/main`. On any conflict it runs `git rebase --abort` "
                + "before answering, so the clone is left EXACTLY as it was found - never a "
                + "half-rebased tree. If the rebase would conflict but merging origin/main into main "
                + "would not (the team already merged it once), it merges instead and says so. A dirty working tree or a detached HEAD is refused before "
                + "anything is touched, and never stashed: this dialog does not get to decide that "
                + "somebody's uncommitted work is disposable." + DefaultBranchNote);

        // THE DEFINING CONSTRAINT OF THIS ENDPOINT: it never forces a push. No flag, no
        // confirmation, no forcing path reaches it from anywhere - see
        // `No_repo_endpoint_reaches_for_a_force_push`, which greps the source for the two spellings
        // of that flag by name so this comment is not the only thing enforcing it. If
        // origin/team/{id} holds a commit this clone does not have, pushing would discard it, so
        // the handler asks git first (`merge-base --is-ancestor`) and refuses before the push is
        // ever attempted, rather than trying the push and hoping git's own non-fast-forward
        // refusal saves it - that refusal exists to protect the exact case this must never touch
        // in the first place.
        app.MapPost("/api/teams/{team}/repos/{repo}/ask-team", AskTeamToBringCurrentAsync)
            .WithTags("Repos")
            .HumansOnly()
            .WithSummary("Ask the team's Manager to bring the clone's main current")
            .WithDescription(
                "For the case the rebase refuses: origin/main has moved and rebasing onto it conflicts. "
                + "Fetches, names the conflicting files with `git merge-tree` (which writes nothing), and "
                + "sends the team's Manager an instruction to merge origin/main into main, resolve those "
                + "files, run both suites and hand back. It starts a new workflow. The clone is not "
                + "touched here. 404 for an unknown team or repository, 409 when the team has no Manager." + DefaultBranchNote);

        app.MapPost("/api/teams/{team}/repos/{repo}/push", PushRepoAsync)
            .WithTags("Repos")
            .HumansOnly()
            .WithSummary("Push the clone's team branch (or main) to origin/team/{id}")
            .WithDescription(
                "Fast-forwards origin/team/{id} to the clone's local team/{id} when it exists and "
                + "carries local main, and otherwise to the clone's local main. Refuses with 409 when "
                + "the remote branch holds commits the clone does not have, rather than forcing "
                + "them away - there is deliberately no way to override that refusal from here." + DefaultBranchNote);

        // THE ONLY NEW ROUTE THAT DESTROYS ANYTHING. Its entire safety rests on one precondition,
        // asked BEFORE the delete and never bypassable from here: origin/team/{id} must already be
        // an ancestor of origin/main. A branch that is not on main yet is refused with 409, and the
        // test that matters most for this route is not "it deletes" but "the branch survives the
        // refusal" - see `Delete_remote_branch_refuses_while_the_work_is_not_on_origin_main`.
        app.MapPost("/api/teams/{team}/repos/{repo}/delete-remote-branch", DeleteRemoteBranchAsync)
            .WithTags("Repos")
            .HumansOnly()
            .WithSummary("Delete the team branch from origin, once its work is on main")
            .WithDescription(
                "Refuses with 409 unless origin/team/{id} is already an ancestor of origin/main - "
                + "deleting it otherwise would lose whatever commits it alone holds. There is no "
                + "flag to override that refusal." + DefaultBranchNote);
    }

    private static async Task<IResult> GetRepoStatusAsync(
        [Description(Describe.Team)] string team,
        HttpContext httpContext,
        TeamRegistry teams,
        GitRunner gitRunner,
        TeamPaths paths,
        ContainerHost host,
        CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        // The clone path is shown to a person and withheld from a machine principal.
        var caller = PrincipalClaims.From(httpContext.User);
        var isPerson = caller?.Kind == PrincipalKind.User;

        // Get refresh parameter from query string
        var refresh = httpContext.Request.Query.TryGetValue("refresh", out var refreshValue)
            && bool.TryParse(refreshValue.ToString(), out var r) && r;

        // Build prerequisites. Read per request, so installing git while the Host runs shows up
        // without a restart.
        var gitResolves = SystemCommand.Find("git") is not null;
        var ghResolves = PathSearch.Find("gh") is not null;

        var gitPrereq = new Prerequisite(
            Command: "git",
            Resolves: gitResolves,
            Message: gitResolves
                ? "git is installed on this machine's PATH."
                : "git was not found on this machine's PATH. Install git, then restart the Host.",
            UsedBy: "platform");

        var ghPrereq = new Prerequisite(
            Command: "gh",
            Resolves: ghResolves,
            Message: ghResolves
                ? "gh is installed on this machine's PATH."
                : "gh was not found on this machine's PATH.",
            UsedBy: "agents");

        // Build repo statuses
        var repoUrls = teams.ReposFor(stored);
        var repoStatuses = new List<RepoStatus>();

        foreach (var url in repoUrls)
        {
            var repoName = RepoUrls.DeriveName(url);
            var clonePath = Path.Combine(paths.ReposFor(stored), repoName, "main");

            // AN EMPTY CLONE IS NOT A CLONE: what a killed `git clone` leaves reads not ready, as a
            // missing one does, and the card offers Fetch, which makes it.
            if (!Directory.Exists(clonePath) || await RepoClone.IsEmptyCloneAsync(gitRunner, clonePath, ct))
            {
                // Repository not yet cloned
                repoStatuses.Add(new RepoStatus(
                    Name: repoName,
                    ClonePath: isPerson ? clonePath : null,
                    MainSha: null,
                    TeamSha: null,
                    MainAhead: null,
                    MainBehind: null,
                    Dirty: false,
                    HeadCheckout: null,
                    TeamBranch: $"team/{stored}",
                    TeamPushed: null,
                    TeamPushedFrom: null,
                    TeamMergedToMain: null,
                    TeamMergedToMainBy: null,
                    CloneMainOnTeamBranch: null,
                    TeamCommitsNotOnMain: null,
                    OriginCheckedAt: null,
                    OriginReachable: null,
                    OriginUnreachableReason: null,
                    Worktrees: [],
                    DefaultBranch: teams.DefaultBranchFor(stored, repoName).Branch,
                    DefaultBranchSource: DefaultBranchSource(teams.DefaultBranchFor(stored, repoName)),
                    OriginUrl: GitOutputRedaction.RedactUserInfo(url),
                    UpstreamUrl: GitOutputRedaction.RedactUserInfo(teams.ContributorFor(stored, repoName).UpstreamUrl),
                    CloneReady: false));
                repoStatuses[^1] = await WithContributorAsync(httpContext, repoStatuses[^1], stored, ct);
                continue;
            }

            // ORIGIN REACHABILITY IS MEASURED HERE AND PASSED IN, because this is the one caller
            // that wants a network round trip of its own: the action routes reuse the composition
            // below and pass whatever their own git work proved, without asking again.
            bool? originReachable = null;
            string? originUnreachableReason = null;
            if (refresh)
            {
                var lsRemoteResult = await gitRunner.LsRemoteAsync(clonePath, "origin", ct);
                originReachable = lsRemoteResult.ExitCode == 0;
                if (!originReachable.Value)
                {
                    // Bounded like every other git output that reaches a caller. This one does not
                    // touch `tenant_events`, but it rides `RepoStatus` to the card on EVERY status
                    // read of an unreachable origin, which is the most frequent of the lot.
                    originUnreachableReason = BoundLines(lsRemoteResult.Stderr.TrimEnd());
                }
            }

            repoStatuses.Add(await WithContributorAsync(httpContext, await ReadRepoStatusAsync(
                gitRunner,
                repoName,
                clonePath,
                stored,
                teams.DefaultBranchFor(stored, repoName),
                isPerson,
                originReachable,
                originUnreachableReason,
                ct,
                OpenKeysFor(httpContext, stored, ct),
                url,
                teams.ContributorFor(stored, repoName)), stored, ct));
        }

        var response = new TeamRepoStatus(Git: gitPrereq, Gh: ghPrereq, Repos: repoStatuses);
        return Results.Json(response);
    }

    private static async Task<IResult> BringCloneCurrentAsync(
        [Description(Describe.Team)] string team,
        [Description("The repository name (derived from the URL in the team's repos list)")]
        string repo,
        HttpContext httpContext,
        TeamRegistry teams,
        GitRunner gitRunner,
        TeamPaths paths,
        ContainerHost host,
        ITenantLog log,
        IRepoClone cloner,
        IMessageLog messages,
        ContributorClone contributorClone,
        CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        // Check if team has any running members
        var busyMembers = GetBusyMembers(teams, host, stored);
        if (busyMembers.Count > 0)
        {
            await log.WriteAsync(userId, email, TenantActions.RepoBringCurrent, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = $"{string.Join(", ", busyMembers)} still working." }), ct);
            return Results.Conflict(new { error = $"{string.Join(", ", busyMembers)} still working.", busy = busyMembers });
        }

        // Verify repo exists
        var repoUrl = teams.ReposFor(stored).FirstOrDefault(u => RepoUrls.DeriveName(u).Equals(repo, StringComparison.OrdinalIgnoreCase));
        if (repoUrl is null)
        {
            return Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." });
        }

        var clonePath = Path.Combine(paths.ReposFor(stored), repo, "main");

        // A CLONE THAT NEVER HAPPENED IS MADE HERE. The platform clones at team creation; when
        // that failed (a private repository before its credential was set, a network error) the
        // team had no way back but deletion. Bringing a missing clone current means making it -
        // and an empty one, what a killed clone leaves; anything else at the path is left alone.
        if (!Directory.Exists(clonePath) || await RepoClone.IsEmptyCloneAsync(gitRunner, clonePath, ct))
        {
            var made = (await cloner.EnsureAllAsync([(repoUrl, clonePath)], ct))[0];
            if (made.Result != RepoCloneResult.Cloned)
            {
                return Results.Json(
                    new { error = $"The repository could not be cloned: {made.Error ?? made.Result.ToString()}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            await teams.RecordRemoteDefaultBranchAsync(stored, RepoUrls.DeriveName(repoUrl), made.DefaultBranch, ct);
            await contributorClone.ApplyAsync(clonePath, teams.ContributorFor(stored, RepoUrls.DeriveName(repoUrl)), ct);

            await RepoReadyNotice.SendAsync(messages, stored, repo, clonePath, ct);
        }

        // Fetch origin, and upstream in contributor mode.
        var contributor = teams.ContributorFor(stored, RepoUrls.DeriveName(repoUrl));
        var baseRemote = ContributorRemotes.BaseFor(contributor);
        var (fetchResult, _) = await FetchRemotesAsync(gitRunner, contributor, clonePath, ct);
        if (fetchResult.ExitCode != 0)
        {
            await log.WriteAsync(userId, email, TenantActions.RepoBringCurrent, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, stderr = BoundLines(fetchResult.Stderr) }), ct);
            return Results.BadRequest(new { error = "Fetch failed.", detail = BoundLines(fetchResult.Stderr) });
        }

        // Every Fetch reads origin's HEAD; the branch used is the stored one. Not known
        // stops here - nothing is guessed.
        var branch = await DefaultBranchRecorder.RecordFromOriginAsync(
            teams, gitRunner, stored, RepoUrls.DeriveName(repoUrl), clonePath, ct);
        if (branch is null)
        {
            return await BranchNotKnownAsync(log, userId, email, TenantActions.RepoBringCurrent, stored, RepoUrls.DeriveName(repoUrl), ct);
        }

        // A FORK THAT HAS ITS OWN COMMITS ON <default> IS REFUSED BEFORE ANYTHING MOVES. Sync
        // fork is a fast-forward or nothing; forcing it would discard those commits, and bringing
        // the clone current while the fork stays behind would leave the two disagreeing about what
        // Bring current did. So the count is named and the clone is left exactly as it was.
        var upstreamBranch = $"refs/remotes/{ContributorRemotes.Upstream}/{branch}";
        var forkBranch = $"refs/remotes/{ContributorRemotes.Origin}/{branch}";
        var forkHasBranch = false;
        if (contributor.ContributorMode)
        {
            if ((await gitRunner.RunGitAsync(clonePath, ["rev-parse", "--verify", "--quiet", upstreamBranch], ct)).ExitCode != 0)
            {
                var missing = $"upstream/{branch} does not exist, so nothing was changed.";
                await log.WriteAsync(userId, email, TenantActions.RepoBringCurrent, $"{stored}/{repo}", null,
                    JsonSerializer.Serialize(new { success = false, reason = missing }), ct);
                return Results.BadRequest(new { error = missing });
            }

            forkHasBranch = (await gitRunner.RunGitAsync(
                clonePath, ["rev-parse", "--verify", "--quiet", forkBranch], ct)).ExitCode == 0;
            if (forkHasBranch)
            {
                var notUpstream = await gitRunner.CountCommitsNotInAsync(clonePath, forkBranch, upstreamBranch, ct);
                if (notUpstream is not 0)
                {
                    var diverged = notUpstream is { } count
                        ? $"The fork's {branch} has {count} commit{(count == 1 ? "" : "s")} that {(count == 1 ? "is" : "are")} not upstream; it is not synced, and nothing was changed."
                        : $"Whether the fork's {branch} is behind upstream/{branch} could not be read, so nothing was changed.";
                    await log.WriteAsync(userId, email, TenantActions.RepoBringCurrent, $"{stored}/{repo}", null,
                        JsonSerializer.Serialize(new { success = false, refused = true, reason = diverged, forkCommitsNotUpstream = notUpstream }), ct);
                    return Results.Conflict(new { error = diverged, forkCommitsNotUpstream = notUpstream });
                }
            }
        }

        // THE DIRECTORY NAMED `main` IS NOT A PROMISE ABOUT HEAD. Merging <remote>/<branch>
        // fast-forwards whatever is checked out. When that is team/<id>, the team branch
        // advances and the local default branch stays behind. Update its *ref* unless HEAD is it.
        var status = await gitRunner.StatusAsync(clonePath, branch, $"refs/heads/team/{stored}", ct);
        var updateResult = string.Equals(status.HeadCheckout, branch, StringComparison.Ordinal)
            ? await gitRunner.MergeFastForwardAsync(clonePath, $"{baseRemote}/{branch}", ct)
            : await gitRunner.FetchRefspecAsync(clonePath, $"refs/remotes/{baseRemote}/{branch}", $"refs/heads/{branch}", ct);
        if (updateResult.ExitCode != 0)
        {
            var message = updateResult.Stderr.Contains("not fast-forward", StringComparison.OrdinalIgnoreCase)
                || updateResult.Stderr.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
                ? $"{branch} has diverged. The team must bring its branch current first."
                : $"{baseRemote}/{branch} does not exist.";
            await log.WriteAsync(userId, email, TenantActions.RepoBringCurrent, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, reason = message }), ct);
            return Results.BadRequest(new { error = message });
        }

        // SYNC FORK: fast-forward the fork's <default> to upstream's. THE ONE PUSH OF A DEFAULT
        // BRANCH THE PLATFORM MAKES, only in contributor mode, only from upstream, never forced -
        // the refspec has no `+`, and the check above already refused a fork with commits of its
        // own. The row is appended AFTER the push succeeds, the ordering `repo.pushed` keeps.
        var said = $"{branch} brought current";
        if (contributor.ContributorMode)
        {
            // A fork with no <default> at all gains every upstream commit none of its branches has.
            var behind = forkHasBranch
                ? await gitRunner.CountCommitsNotInAsync(clonePath, upstreamBranch, forkBranch, ct)
                : int.TryParse((await gitRunner.RunGitAsync(
                    clonePath, ["rev-list", "--count", upstreamBranch, "--not", "--remotes=origin"], ct)).Stdout.Trim(),
                    out var gained) ? gained : null;
            if (!forkHasBranch || behind is not 0)
            {
                var push = await gitRunner.PushRefspecAsync(clonePath, upstreamBranch, $"refs/heads/{branch}", ct);
                if (push.ExitCode != 0)
                {
                    var detail = BoundLines(push.Stderr);
                    var refused = $"{branch} was brought current from upstream/{branch}, but the fork's {branch} "
                        + "could not be fast-forwarded to it, so the fork is not synced.";
                    await log.WriteAsync(userId, email, TenantActions.RepoBringCurrent, $"{stored}/{repo}", null,
                        JsonSerializer.Serialize(new { success = false, forkSynced = false, reason = refused, stderr = detail }), ct);
                    return Results.BadRequest(new { error = refused, detail });
                }

                await messages.AppendAsync(
                    new NewMessage(
                        MessageTypes.RepoForkSynced,
                        JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            [PayloadFields.Team] = stored,
                            [PayloadFields.Repo] = RepoUrls.DeriveName(repoUrl),
                            [PayloadFields.Branch] = branch,
                            [PayloadFields.Count] = behind,
                        }),
                        PrincipalClaims.From(httpContext.User)?.Id ?? "console",
                        null),
                    ct);

                said = $"{branch} brought current from upstream/{branch}, and the fork's {branch} was fast-forwarded to it";
            }
            else
            {
                said = $"{branch} brought current from upstream/{branch}; the fork's {branch} was already level with it";
            }
        }

        await log.WriteAsync(userId, email, TenantActions.RepoBringCurrent, $"{stored}/{repo}", null,
            JsonSerializer.Serialize(new { success = true, from = baseRemote }), ct);

        // THE NAME COMES FROM THE URL, NOT FROM THE CALLER'S SPELLING. The repo lookup above is
        // case-insensitive, so `repo` may be spelled differently from what the status route sends;
        // a card keyed on the name would then fail to match the row it is meant to repaint.
        return await ActionResultAsync(
            httpContext, gitRunner, RepoUrls.DeriveName(repoUrl), clonePath, stored,
            said, success: true,
            originReachable: true, originUnreachableReason: null, ct);
    }

    /// <summary>
    /// Fetches every remote the repository has - origin, and upstream in contributor mode -
    /// after bringing the clone's upstream remote in line with what is stored, so a remote set or
    /// cleared while the clone was missing, or edited by hand, is right before anything reads it.
    /// Answers the first failure and which remote it was, or a zero exit.
    /// </summary>
    private static async Task<(GitRunner.GitInvocation Run, string Remote)> FetchRemotesAsync(
        GitRunner gitRunner, RepoContributor contributor, string clonePath, CancellationToken ct)
    {
        var remote = await gitRunner.SetUpstreamRemoteAsync(clonePath, contributor.UpstreamUrl, ct);
        if (remote is not null) return (remote, ContributorRemotes.Upstream);

        var origin = await gitRunner.FetchAsync(clonePath, ct);
        if (origin.ExitCode != 0 || !contributor.ContributorMode) return (origin, ContributorRemotes.Origin);

        return (await gitRunner.FetchUpstreamAsync(clonePath, ct), ContributorRemotes.Upstream);
    }

    /// <summary>
    /// THE SAFE RUNG. Everything else in this file moves a branch, a worktree or origin itself;
    /// this moves only the clone's remote-tracking refs — <c>git fetch origin</c> and nothing more.
    /// HEAD, the working tree and every local branch are untouched, which is what lets a team run
    /// it as often as it likes without asking permission.
    ///
    /// AN UNREACHABLE ORIGIN IS A FACT ABOUT THE NETWORK, NOT A FAILURE OF THIS ENDPOINT. Every
    /// other action here answers 400 when its git command fails, because a failed merge or push
    /// really did fail to do what it promised. A failed fetch promised nothing but "ask origin" -
    /// refusing with 400 would make a flaky connection read as a dialog bug, and the whole point of
    /// this rung is to be the thing a person reaches for first without fear. So it answers 200
    /// either way and reports reachability as data, leaving whatever the dashboard already showed
    /// exactly where it was - stale, but never replaced with a failure banner over one bad request.
    /// </summary>
    private static async Task<IResult> FetchRepoAsync(
        [Description(Describe.Team)] string team,
        [Description("The repository name (derived from the URL in the team's repos list)")]
        string repo,
        HttpContext httpContext,
        TeamRegistry teams,
        GitRunner gitRunner,
        TeamPaths paths,
        ContainerHost host,
        ITenantLog log,
        IRepoClone cloner,
        IMessageLog messages,
        ContributorClone contributorClone,
        CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        // Check if team has any running members
        var busyMembers = GetBusyMembers(teams, host, stored);
        if (busyMembers.Count > 0)
        {
            await log.WriteAsync(userId, email, TenantActions.RepoFetch, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = $"{string.Join(", ", busyMembers)} still working." }), ct);
            return Results.Conflict(new { error = $"{string.Join(", ", busyMembers)} still working.", busy = busyMembers });
        }

        // Verify repo exists
        var repoUrl = teams.ReposFor(stored).FirstOrDefault(u => RepoUrls.DeriveName(u).Equals(repo, StringComparison.OrdinalIgnoreCase));
        if (repoUrl is null)
        {
            return Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." });
        }

        var clonePath = Path.Combine(paths.ReposFor(stored), repo, "main");

        // A CLONE THAT NEVER HAPPENED IS MADE HERE TOO, as Bring current makes one. A repository
        // attached while its token is unusable fails to clone, and with no clone to measure the Git
        // dialog offers only Fetch - so Fetch answering 404 "not cloned yet" would be a dead end.
        // Pinned by FetchMakesAMissingCloneTests. An empty clone, what a killed clone leaves, is
        // made the same way; anything else at the path is left alone (CloneNeverAdoptTests).
        if (!Directory.Exists(clonePath) || await RepoClone.IsEmptyCloneAsync(gitRunner, clonePath, ct))
        {
            var made = (await cloner.EnsureAllAsync([(repoUrl, clonePath)], ct))[0];
            if (made.Result is not (RepoCloneResult.Cloned or RepoCloneResult.AlreadyThere))
            {
                var why = made.Error ?? made.Result.ToString();
                await log.WriteAsync(userId, email, TenantActions.RepoFetch, $"{stored}/{repo}", null,
                    JsonSerializer.Serialize(new { success = false, cloned = false, reason = BoundLines(why) }), ct);
                return Results.Json(
                    new { error = $"The repository could not be cloned: {BoundLines(why)}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            // The workflow the failure opened is told, or its block outlives the clone.
            if (made.Result is RepoCloneResult.Cloned)
            {
                await teams.RecordRemoteDefaultBranchAsync(stored, RepoUrls.DeriveName(repoUrl), made.DefaultBranch, ct);
                await contributorClone.ApplyAsync(clonePath, teams.ContributorFor(stored, RepoUrls.DeriveName(repoUrl)), ct);
                await RepoReadyNotice.SendAsync(messages, stored, repo, clonePath, ct);
            }
        }

        // Fetch origin, and upstream in contributor mode. Fetches are the ONLY git commands
        // this handler runs - no merge, no checkout, no push - so there is nothing here that can
        // leave the clone in a half-changed state.
        var (fetchResult, fetchedRemote) = await FetchRemotesAsync(
            gitRunner, teams.ContributorFor(stored, RepoUrls.DeriveName(repoUrl)), clonePath, ct);

        if (fetchResult.ExitCode != 0)
        {
            // Reported, not refused: see the class doc above for why this is 200 rather than 400.
            //
            // BOUNDED LIKE EVERY OTHER GIT-OUTPUT SITE, and this is the one that fires MOST often:
            // it writes a permanent `tenant_events` row AND puts the text on the wire as
            // `originUnreachableReason` every time a dialog opens against an unreachable origin -
            // which, for a team whose credentials have expired, is every open, forever.
            var reason = BoundLines(fetchResult.Stderr.TrimEnd());
            await log.WriteAsync(userId, email, TenantActions.RepoFetch, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, reachable = false, reason }), ct);

            // SAME RECORD AS EVERY OTHER ARM, with `success: false` as the one thing separating
            // "fetched" from "could not reach origin" - they are both 200s, and a ladder that could
            // not tell them apart would tick the rung off either way. The reachability and the
            // reason travel in the status's OWN fields (`originReachable`, `originUnreachableReason`)
            // rather than in flat fields of their own: the card already reads them there.
            //
            // The status is still composed - every command behind it is local, so it costs no
            // network - and it carries exactly what the dashboard already showed, wearing its age.
            return await ActionResultAsync(
                httpContext, gitRunner, RepoUrls.DeriveName(repoUrl), clonePath, stored,
                fetchedRemote == ContributorRemotes.Upstream
                    ? "Upstream was not reachable. Existing status is unchanged."
                    : "Origin was not reachable. Existing status is unchanged.",
                success: false,
                originReachable: false, originUnreachableReason: reason, ct);
        }

        // On every successful Fetch, `git remote set-head origin --auto` and store the branch
        // origin's HEAD names - or not known when that fails. A person's choice is left alone, and
        // the failed arm above never reaches here, so a failed Fetch changes nothing stored.
        var branch = await DefaultBranchRecorder.RecordFromOriginAsync(
            teams, gitRunner, stored, RepoUrls.DeriveName(repoUrl), clonePath, ct);

        await log.WriteAsync(userId, email, TenantActions.RepoFetch, $"{stored}/{repo}", null,
            JsonSerializer.Serialize(new { success = true, reachable = true, defaultBranch = branch }), ct);

        // Fetch moved refs/remotes/origin/*, so the status composed here reflects what origin looks
        // like RIGHT NOW - the refreshed status this endpoint exists to produce.
        //
        // NO FLAT FIELDS BESIDE THE STATUS. reachable, mainSha, teamSha, mainAhead, mainBehind and
        // originCheckedAt are all `RepoStatus` fields, so sending them beside a status would be two
        // answers to one question waiting to disagree; `client.ts` types this response as
        // `RepoActionResult`.
        //
        // `originReachable: true` is MEASURED, not assumed: the fetch above just talked to origin
        // and came back 0. This is the one route that can answer it without a second round trip.
        return await ActionResultAsync(
            httpContext, gitRunner, RepoUrls.DeriveName(repoUrl), clonePath, stored,
            "origin fetched", success: true,
            originReachable: true, originUnreachableReason: null, ct);
    }

    /// <summary>
    /// THE TASK THE WHOLE FEATURE TURNS ON. A clone that is both ahead of and behind origin/main -
    /// diverged, not merely stale - cannot be brought home by <see cref="BringCloneCurrentAsync"/>,
    /// which can only fast-forward. This is the rebase that unsticks it, and its one rule that
    /// matters more than the rest: a rebase that cannot complete must leave NOTHING behind. An
    /// aborted rebase that fails to abort cleanly is worse than never offering the button, so
    /// every failure path below runs `git rebase --abort` before answering, and every precondition
    /// is checked BEFORE the clone is touched at all.
    /// </summary>
    private static async Task<IResult> AskTeamToBringCurrentAsync(
        [Description(Describe.Team)] string team,
        [Description("The repository name (derived from the URL in the team's repos list)")]
        string repo,
        HttpContext httpContext,
        TeamRegistry teams,
        GitRunner gitRunner,
        TeamPaths paths,
        ContainerHost host,
        IMessageLog messages,
        ITenantLog log,
        CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        var repoUrl = teams.ReposFor(stored).FirstOrDefault(u => RepoUrls.DeriveName(u).Equals(repo, StringComparison.OrdinalIgnoreCase));
        if (repoUrl is null)
        {
            return Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." });
        }

        var name = RepoUrls.DeriveName(repoUrl);
        var clonePath = Path.Combine(paths.ReposFor(stored), name, "main");
        if (!Directory.Exists(clonePath))
        {
            return Results.NotFound(new { error = "Repository not cloned yet." });
        }

        var manager = host.Find(new ContainerId(stored, TeamRegistry.DefaultManagerName));
        if (manager is null)
        {
            return Results.Conflict(new { error = "This team has no Manager to ask." });
        }

        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        // Fresh origin/<default>, so the files named are the ones the team will actually meet. A
        // successful fetch reads origin's HEAD too.
        var fetched = await gitRunner.FetchAsync(clonePath, ct);
        var branch = fetched.ExitCode == 0
            ? await DefaultBranchRecorder.RecordFromOriginAsync(teams, gitRunner, stored, name, clonePath, ct)
            : teams.DefaultBranchFor(stored, name).Branch;
        if (branch is null)
        {
            return await BranchNotKnownAsync(log, userId, email, TenantActions.RepoAskTeam, stored, name, ct);
        }

        var conflicts = BringCurrentAsk.ConflictedPaths(
            (await gitRunner.MergeTreeAsync(clonePath, branch, $"origin/{branch}", ct)).Stdout);

        var instruction = BringCurrentAsk.Instruction(name, branch, conflicts);
        var parts = InstructionText.Split(instruction, BringCurrentAsk.Subject(branch));
        var from = PrincipalClaims.From(httpContext.User)?.Id ?? "console";

        var message = await messages.AppendAsync(
            new NewMessage(
                MessageTypes.InstructionFor(manager.Id),
                JsonSerializer.Serialize(new { instruction, subject = parts.Subject, body = parts.Body }),
                from,
                null),
            ct);

        await log.WriteAsync(userId, email, TenantActions.RepoAskTeam, $"{stored}/{name}", null,
            JsonSerializer.Serialize(new { conflicts, seq = message.Seq }), ct);

        return Results.Ok(new { message.Seq, message.CorrelationId, conflicts });
    }

    private static async Task<IResult> RebaseRepoAsync(
        [Description(Describe.Team)] string team,
        [Description("The repository name (derived from the URL in the team's repos list)")]
        string repo,
        HttpContext httpContext,
        TeamRegistry teams,
        GitRunner gitRunner,
        TeamPaths paths,
        ContainerHost host,
        ITenantLog log,
        CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        // Check if team has any running members
        var busyMembers = GetBusyMembers(teams, host, stored);
        if (busyMembers.Count > 0)
        {
            await log.WriteAsync(userId, email, TenantActions.RepoRebase, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = $"{string.Join(", ", busyMembers)} still working." }), ct);
            return Results.Conflict(new { error = $"{string.Join(", ", busyMembers)} still working.", busy = busyMembers });
        }

        // Verify repo exists
        var repoUrl = teams.ReposFor(stored).FirstOrDefault(u => RepoUrls.DeriveName(u).Equals(repo, StringComparison.OrdinalIgnoreCase));
        if (repoUrl is null)
        {
            return Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." });
        }

        var clonePath = Path.Combine(paths.ReposFor(stored), repo, "main");

        if (!Directory.Exists(clonePath))
        {
            return Results.NotFound(new { error = "Repository not cloned yet." });
        }

        // The branch rebased is the stored default branch; not known stops before anything.
        var name = RepoUrls.DeriveName(repoUrl);
        if (teams.DefaultBranchFor(stored, name).Branch is not { } branch)
        {
            return await BranchNotKnownAsync(log, userId, email, TenantActions.RepoRebase, stored, name, ct);
        }

        // PRECONDITIONS, BEFORE ANYTHING IS TOUCHED. Neither of these is something `--abort` can
        // undo afterwards: a dirty tree was never git's to discard in the first place, and a
        // detached HEAD has no branch for a rebase to move.
        var status = await gitRunner.StatusAsync(clonePath, branch, $"refs/heads/team/{stored}", ct);

        if (status.Dirty)
        {
            // Name the files, not just "dirty" - the person clearing this needs to know what to
            // commit or remove. A dirty tree is a REFUSAL: never stash, never discard, because
            // this dialog does not get to decide that somebody's uncommitted work is disposable.
            var porcelain = await gitRunner.RunGitAsync(clonePath, ["status", "--porcelain"], ct);
            var files = BoundLines(porcelain.Stdout);
            const string dirty = "The clone has uncommitted changes, so it cannot be rebased.";
            await log.WriteAsync(userId, email, TenantActions.RepoRebase, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = dirty, files }), ct);
            return Results.Conflict(new
            {
                error = dirty,
                detail = "Commit or remove them first. Nothing here will discard them for you.",
                files,
            });
        }

        if (string.Equals(status.HeadCheckout, "detached", StringComparison.Ordinal))
        {
            const string detached = "The clone is on a detached HEAD, so there is nothing to rebase.";
            await log.WriteAsync(userId, email, TenantActions.RepoRebase, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = detached }), ct);
            return Results.Conflict(new { error = detached });
        }

        // THIS IS THE ONE ACTION IN THIS FILE THAT OPERATES ON HEAD RATHER THAN A NAMED REF, and
        // the detached arm above is not the whole of that problem. `git rebase origin/main` moves
        // WHATEVER IS CHECKED OUT: on a clone sitting on `team/{id}` it rewrites that branch, local
        // `main` never moves, and the ladder - which reads `mainAhead`/`mainBehind` about `main` -
        // offers Rebase again, forever, every click answering 200 and "Rebased onto origin/main."
        // while a branch the caller never named is quietly rewritten.
        //
        // EVERY SIBLING HERE IS DELIBERATE ABOUT THE SAME THING: `BringCloneCurrentAsync` and
        // `MergeToMainAsync` both carry "THE DIRECTORY NAMED `main` IS NOT A PROMISE ABOUT HEAD",
        // and `PushRepoAsync` pushes `main` by name. This one was not.
        //
        // REFUSED, NOT CHECKED OUT FOR THE CALLER. Moving somebody's HEAD is not this dialog's to
        // do - that is the same rule as never stashing a dirty tree - so the refusal names the
        // branch and leaves the decision where it belongs. NULL IS REFUSED TOO: a guard fails in
        // whichever direction is recoverable, and "check main out first" is recoverable where
        // rewriting a branch nobody can name is not.
        if (!string.Equals(status.HeadCheckout, branch, StringComparison.Ordinal))
        {
            var where = status.HeadCheckout is null ? "an unknown ref" : $"'{status.HeadCheckout}'";
            var elsewhere = $"The clone is on {where}, not {branch}, so a rebase here would move that branch instead of {branch}.";
            await log.WriteAsync(userId, email, TenantActions.RepoRebase, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = elsewhere }), ct);
            return Results.Conflict(new
            {
                error = elsewhere,
                detail = $"Check {branch} out in this clone first. Nothing here will move HEAD for you.",
            });
        }

        // Fetch origin - and upstream in contributor mode, which is then what the rebase goes
        // onto - so the ref reflects what is actually there before rebasing onto it.
        var contributor = teams.ContributorFor(stored, name);
        var baseRemote = ContributorRemotes.BaseFor(contributor);
        var (fetchResult, _) = await FetchRemotesAsync(gitRunner, contributor, clonePath, ct);
        if (fetchResult.ExitCode != 0)
        {
            // BOUNDED LIKE EVERY OTHER GIT-OUTPUT SITE IN THIS HANDLER: a permanent tenant_events
            // row must never hold unbounded stderr from an external process.
            var fetchDetail = BoundLines(fetchResult.Stderr);
            await log.WriteAsync(userId, email, TenantActions.RepoRebase, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, stderr = fetchDetail }), ct);
            return Results.BadRequest(new { error = "Fetch failed.", detail = fetchDetail });
        }

        // Every successful Fetch reads origin's HEAD. The rebase goes on with the branch
        // checked above; a different answer stops it rather than rebasing onto another branch.
        if (await DefaultBranchRecorder.RecordFromOriginAsync(teams, gitRunner, stored, name, clonePath, ct) != branch)
        {
            const string moved = "The repository's default branch changed when origin was fetched, so nothing was rebased. Try again.";
            await log.WriteAsync(userId, email, TenantActions.RepoRebase, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = moved }), ct);
            return Results.Conflict(new { error = moved });
        }

        GitRunner.GitInvocation rebase;
        try
        {
            rebase = await gitRunner.RunGitAsync(clonePath, ["rebase", $"{baseRemote}/{branch}"], ct);
        }
        catch (OperationCanceledException)
        {
            // THE CRITICAL PATH. `GitRunner`'s own 30-second timeout, or a client disconnect,
            // kills the git process tree and rethrows OUT OF `RunGitAsync` - it never returns a
            // `GitInvocation` at all. Without this catch, the one case that kills git WHILE it may
            // be sitting mid-rebase in `.git/rebase-merge` is the one case that never reaches the
            // abort below, and this host has no exception middleware to notice. `CancellationToken.None`
            // is deliberate: `ct` has just fired (that is what got us here), and an abort issued on
            // an already-cancelled token throws immediately and cleans up nothing.
            await gitRunner.RunGitAsync(clonePath, ["rebase", "--abort"], CancellationToken.None);
            throw;
        }

        if (rebase.ExitCode != 0)
        {
            // Rebase state (`.git/rebase-merge` or `.git/rebase-apply`) exists once git has started
            // replaying commits, and ONLY THEN - not for a failure before that point, such as a
            // missing git binary (exit 127) or an `origin/main` that does not exist. Checked BEFORE
            // the abort below runs, because a successful abort removes the very directories this
            // is asking about.
            var rebaseStarted = Directory.Exists(Path.Combine(clonePath, ".git", "rebase-merge"))
                || Directory.Exists(Path.Combine(clonePath, ".git", "rebase-apply"));

            // ABORT, ALWAYS, AND BEFORE ANYTHING ELSE IS ANSWERED - regardless of why the rebase
            // failed, and regardless of whether rebase state was ever created. A half-rebased tree
            // is the one outcome this endpoint must never produce. `--abort` is a no-op when there
            // is nothing to abort, so it is always safe to run. `CancellationToken.None`: `ct` can
            // legitimately cancel in the gap between the rebase call returning and this call
            // running, and an abort on a token that fires immediately cleans up nothing either.
            var abort = await gitRunner.RunGitAsync(clonePath, ["rebase", "--abort"], CancellationToken.None);

            var rebaseOutput = BoundLines(rebase.Stdout + rebase.Stderr);

            if (!rebaseStarted)
            {
                // The rebase never entered rebase state at all, so this is not a conflict - it is
                // "could not run the rebase", the same distinction `FetchRepoAsync`'s sibling
                // `Fetch failed.` draws. Reporting this as "would conflict" sends a person looking
                // for a merge conflict that was never there, when the real cause is a missing git
                // binary, a bad ref, or some other refusal on git's side.
                await log.WriteAsync(userId, email, TenantActions.RepoRebase, $"{stored}/{repo}", null,
                    JsonSerializer.Serialize(new { success = false, reason = "could not run", detail = rebaseOutput }), ct);
                return Results.BadRequest(new { error = "The rebase could not be run.", detail = rebaseOutput });
            }

            if (abort.ExitCode != 0)
            {
                // THE ABORT ITSELF FAILED - the one outcome this endpoint exists to prevent, and
                // the one case where telling the caller "nothing was changed" would be a lie
                // recorded forever in the tenant log. Say so plainly and name the directory a
                // person needs to go look at by hand, rather than asserting success unverified.
                var abortOutput = BoundLines(abort.Stdout + abort.Stderr);
                await log.WriteAsync(userId, email, TenantActions.RepoRebase, $"{stored}/{repo}", null,
                    JsonSerializer.Serialize(new { success = false, aborted = false, rebase = rebaseOutput, abort = abortOutput }), ct);
                return Results.Conflict(new
                {
                    error = "The rebase conflicted, and the automatic abort failed. "
                        + "The clone may still be mid-rebase - check .git/rebase-merge "
                        + "(or .git/rebase-apply) in the clone by hand.",
                    detail = rebaseOutput,
                    abortDetail = abortOutput,
                });
            }

            // A MERGE MAY BE CLEAN WHERE THE REBASE IS NOT: a rebase drops the team's own merge of
            // origin/main and replays its commits into the conflicts that merge resolved. See
            // `CleanMergeFallback`; only a merge git completes without a conflict is taken.
            if (await CleanMergeFallback.TryAsync(gitRunner, clonePath, branch, ct, baseRemote))
            {
                await log.WriteAsync(userId, email, TenantActions.RepoRebase, $"{stored}/{repo}", null,
                    JsonSerializer.Serialize(new { success = true, merged = true, rebase = rebaseOutput }), ct);

                return await ActionResultAsync(
                    httpContext, gitRunner, RepoUrls.DeriveName(repoUrl), clonePath, stored,
                    "A rebase would have replayed the team's own merge and conflicted, so " + baseRemote + "/" + branch + " was "
                        + "merged into " + branch + " instead. Nothing conflicted.",
                    success: true, originReachable: true, originUnreachableReason: null, ct);
            }

            await log.WriteAsync(userId, email, TenantActions.RepoRebase, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, aborted = true, reason = rebaseOutput }), ct);

            // WHICH FILES, so the dialog can say so and offer to hand them to the team. Read with
            // merge-tree, which writes nothing: the clone was just put back exactly as it was.
            var conflicts = BringCurrentAsk.ConflictedPaths(
                (await gitRunner.MergeTreeAsync(clonePath, branch, $"{baseRemote}/{branch}", CancellationToken.None)).Stdout);

            return Results.Conflict(new
            {
                error = "The rebase would conflict, so nothing was changed.",
                detail = rebaseOutput,
                conflicts,
            });
        }

        await log.WriteAsync(userId, email, TenantActions.RepoRebase, $"{stored}/{repo}", null,
            JsonSerializer.Serialize(new { success = true }), ct);

        return await ActionResultAsync(
            httpContext, gitRunner, RepoUrls.DeriveName(repoUrl), clonePath, stored,
            $"Rebased onto {baseRemote}/{branch}.", success: true,
            originReachable: true, originUnreachableReason: null, ct);
    }

    /// <summary>
    /// THE ONE PLACE THIS PLATFORM PUSHES. Fast-forwards <c>origin/team/{id}</c> to the clone's
    /// local <c>team/{id}</c> when it carries the default branch, otherwise to the local default
    /// branch. NEVER FORCES: before touching the remote, it asks git whether
    /// <c>origin/team/{id}</c> (as of a fresh fetch) is an ancestor of what is pushed. If
    /// it is not - somebody else pushed commits this clone never saw - the push would have to
    /// discard them, so this refuses with 409 and explains, rather than attempting the push and
    /// relying on git's own non-fast-forward rejection: that rejection protects exactly the commits
    /// this check exists to never put at risk in the first place. There is no flag, no
    /// confirmation and no forcing path from this dialog - see
    /// <c>No_repo_endpoint_reaches_for_a_force_push</c>, which greps this file for both spellings
    /// of that flag so the guarantee does not rest on this comment alone.
    /// </summary>
    private static async Task<IResult> PushRepoAsync(
        [Description(Describe.Team)] string team,
        [Description("The repository name (derived from the URL in the team's repos list)")]
        string repo,
        HttpContext httpContext,
        TeamRegistry teams,
        GitRunner gitRunner,
        TeamPaths paths,
        ContainerHost host,
        ITenantLog log,
        CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        // Check if team has any running members
        var busyMembers = GetBusyMembers(teams, host, stored);
        if (busyMembers.Count > 0)
        {
            await log.WriteAsync(userId, email, TenantActions.RepoPush, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = $"{string.Join(", ", busyMembers)} still working." }), ct);
            return Results.Conflict(new { error = $"{string.Join(", ", busyMembers)} still working.", busy = busyMembers });
        }

        // Verify repo exists
        var repoUrl = teams.ReposFor(stored).FirstOrDefault(u => RepoUrls.DeriveName(u).Equals(repo, StringComparison.OrdinalIgnoreCase));
        if (repoUrl is null)
        {
            return Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." });
        }

        var clonePath = Path.Combine(paths.ReposFor(stored), repo, "main");

        if (!Directory.Exists(clonePath))
        {
            return Results.NotFound(new { error = "Repository not cloned yet." });
        }

        // Fetch origin first, so the ancestor check below is asked against a CURRENT view of
        // origin/team/{id} rather than whatever this clone last happened to see - a stale
        // remote-tracking ref could let a genuinely diverged push through as if it were still a
        // fast-forward.
        var fetchResult = await gitRunner.FetchAsync(clonePath, ct);
        if (fetchResult.ExitCode != 0)
        {
            var fetchDetail = BoundLines(fetchResult.Stderr);
            await log.WriteAsync(userId, email, TenantActions.RepoPush, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, stderr = fetchDetail }), ct);
            return Results.BadRequest(new { error = "Fetch failed.", detail = fetchDetail });
        }

        // The stored default branch is still required: it is the fallback below, and a team whose
        // default branch is not known stops here with nothing pushed.
        var name = RepoUrls.DeriveName(repoUrl);
        if (await DefaultBranchRecorder.RecordFromOriginAsync(teams, gitRunner, stored, name, clonePath, ct) is not { } branch)
        {
            return await BranchNotKnownAsync(log, userId, email, TenantActions.RepoPush, stored, name, ct);
        }

        // WHAT IS PUSHED IS THE LOCAL TEAM BRANCH WHENEVER THE CLONE HAS ONE, whether or not it
        // contains the default branch. A Manager merges members' work into a local team/{id};
        // publishing the default branch in its place would overwrite origin/team/{id} with main,
        // leave that work in the clone and stop the dialog saying "not pushed". A team branch
        // that is behind the default branch after the push is offered Bring current and merge.
        // Only when there is no local team branch is the clone's default branch pushed instead.
        var localTeam = $"refs/heads/team/{stored}";
        var source = (await gitRunner.RunGitAsync(clonePath, ["rev-parse", "--verify", "--quiet", localTeam], ct)).ExitCode == 0
            ? localTeam
            : $"refs/heads/{branch}";

        var remoteRef = $"refs/remotes/origin/team/{stored}";
        var remoteRefLookup = await gitRunner.RunGitAsync(clonePath, ["rev-parse", "--verify", remoteRef], ct);
        var remoteExists = remoteRefLookup.ExitCode == 0;

        // NEVER FORCE. If the remote branch is not an ancestor of what we are about to push,
        // somebody else's commits are on it and a push would have to discard them. Refuse and
        // explain; there is deliberately no flag, no confirmation and no forcing path from this
        // dialog. When the branch does not exist yet on origin, there is nothing it could hold
        // that the clone lacks, so this is skipped and the push below creates it.
        if (remoteExists)
        {
            var ancestor = await gitRunner.RunGitAsync(
                clonePath, ["merge-base", "--is-ancestor", remoteRef, source], ct);
            if (ancestor.ExitCode != 0)
            {
                var message = $"origin/team/{stored} has commits this clone does not have, so pushing would discard them.";
                await log.WriteAsync(userId, email, TenantActions.RepoPush, $"{stored}/{repo}", null,
                    JsonSerializer.Serialize(new { refused = true, reason = message }), ct);
                return Results.Conflict(new
                {
                    error = message,
                    detail = "Rebase or fetch first. Nothing here will force-push.",
                });
            }
        }

        var pushResult = await gitRunner.PushRefspecAsync(clonePath, source, $"team/{stored}", ct);
        if (pushResult.ExitCode != 0)
        {
            var pushDetail = BoundLines(pushResult.Stderr);
            await log.WriteAsync(userId, email, TenantActions.RepoPush, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, stderr = pushDetail }), ct);
            return Results.BadRequest(new { error = "Push failed.", detail = pushDetail });
        }

        await log.WriteAsync(userId, email, TenantActions.RepoPush, $"{stored}/{repo}", null,
            JsonSerializer.Serialize(new { success = true, pushed = source }), ct);

        return await ActionResultAsync(
            httpContext, gitRunner, RepoUrls.DeriveName(repoUrl), clonePath, stored,
            source == localTeam ? $"Pushed team/{stored} to origin." : $"Pushed {branch} to team/{stored}.", success: true,
            originReachable: true, originUnreachableReason: null, ct);
    }

    /// <summary>
    /// THE ONLY NEW ACTION IN THIS FILE THAT DESTROYS ANYTHING. Its safety is entirely the
    /// precondition below: origin/team/{id} must already be an ancestor of origin/main before its
    /// remote ref is ever touched. Everything else here - the busy check, the repo lookup, the
    /// fetch-before-deciding - exists so that precondition is asked against a clone that is
    /// actually free to be read and against a CURRENT view of both refs, never a stale one.
    /// </summary>
    private static async Task<IResult> DeleteRemoteBranchAsync(
        [Description(Describe.Team)] string team,
        [Description("The repository name (derived from the URL in the team's repos list)")]
        string repo,
        HttpContext httpContext,
        TeamRegistry teams,
        GitRunner gitRunner,
        TeamPaths paths,
        ContainerHost host,
        ITenantLog log,
        CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        // Check if team has any running members
        var busyMembers = GetBusyMembers(teams, host, stored);
        if (busyMembers.Count > 0)
        {
            await log.WriteAsync(userId, email, TenantActions.RepoDeleteRemoteBranch, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = $"{string.Join(", ", busyMembers)} still working." }), ct);
            return Results.Conflict(new { error = $"{string.Join(", ", busyMembers)} still working.", busy = busyMembers });
        }

        // Verify repo exists
        var repoUrl = teams.ReposFor(stored).FirstOrDefault(u => RepoUrls.DeriveName(u).Equals(repo, StringComparison.OrdinalIgnoreCase));
        if (repoUrl is null)
        {
            return Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." });
        }

        var clonePath = Path.Combine(paths.ReposFor(stored), repo, "main");

        if (!Directory.Exists(clonePath))
        {
            return Results.NotFound(new { error = "Repository not cloned yet." });
        }

        // Fetch origin first, so the precondition below is asked against a CURRENT view of both
        // origin/team/{id} and origin/main. A stale origin/team/{id} could look already-merged
        // from an old fetch while the real branch on the server has moved on since - the wrong
        // direction to be wrong in for the one route here that deletes something.
        var fetchResult = await gitRunner.FetchAsync(clonePath, ct);
        if (fetchResult.ExitCode != 0)
        {
            var fetchDetail = BoundLines(fetchResult.Stderr);
            await log.WriteAsync(userId, email, TenantActions.RepoDeleteRemoteBranch, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, stderr = fetchDetail }), ct);
            return Results.BadRequest(new { error = "Fetch failed.", detail = fetchDetail });
        }

        // ALREADY GONE IS NOT "WOULD LOSE WORK", and without this probe it would be reported as one.
        // `merge-base --is-ancestor` exits 128 when a ref does not RESOLVE, which the check below
        // cannot tell from "resolves, and is not an ancestor" - so an absent `origin/team/{id}`
        // would be refused with "deleting it would lose work - merge it to main first", naming a branch
        // that is not there and sending the reader off to merge nothing. `PushRepoAsync` already
        // probes with `rev-parse --verify` for the mirror-image reason.
        //
        // ANSWERED 200 RATHER THAN REFUSED: the caller asked for this branch not to be on origin,
        // and it is not. A 409 would paint a red error across the card for a state that is exactly
        // what was wanted, and the fresh status this returns lets the ladder move on.
        var remoteRef = $"refs/remotes/origin/team/{stored}";
        var remoteLookup = await gitRunner.RunGitAsync(clonePath, ["rev-parse", "--verify", remoteRef], ct);
        if (remoteLookup.ExitCode != 0)
        {
            await log.WriteAsync(userId, email, TenantActions.RepoDeleteRemoteBranch, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = true, alreadyGone = true }), ct);
            return await ActionResultAsync(
                httpContext, gitRunner, RepoUrls.DeriveName(repoUrl), clonePath, stored,
                $"origin/team/{stored} is already gone.", success: true,
                originReachable: true, originUnreachableReason: null, ct);
        }

        // "On main" means on origin's copy of the stored default branch. Not known is a
        // refusal: nothing is deleted on a guess.
        var name = RepoUrls.DeriveName(repoUrl);
        if (await DefaultBranchRecorder.RecordFromOriginAsync(teams, gitRunner, stored, name, clonePath, ct) is not { } branch)
        {
            return await BranchNotKnownAsync(log, userId, email, TenantActions.RepoDeleteRemoteBranch, stored, name, ct);
        }

        // THE PRECONDITION IS WHAT MAKES THIS SAFE. This is the only action here that destroys
        // anything, and it is refused until the work it holds is already on origin/main - checked
        // BEFORE the remote ref is ever touched, never after and never overridable from here.
        var merged = await gitRunner.RunGitAsync(
            clonePath,
            ["merge-base", "--is-ancestor", remoteRef, $"refs/remotes/origin/{branch}"],
            ct);

        // ANCESTRY IS SUFFICIENT BUT NOT NECESSARY.
        //
        // A cherry-pick puts the same CHANGE on main under a different SHA, so ancestry answers no
        // for work that is demonstrably already there, and the branch could never be deleted.
        // `git cherry` asks the question ancestry cannot: is there any commit here
        // whose patch is not upstream?
        //
        // THE WIDENING IS SAFE BECAUSE `git cherry` IS CONSERVATIVE. A commit whose patch was
        // MODIFIED during integration - a conflict resolved - still reports `+`, so the count errs
        // toward "not equivalent", which errs toward refusing. The failure mode it leaves is a
        // branch that cannot be deleted from here, never one deleted while it still held work.
        //
        // Asked ONLY after ancestry fails, so the ordinary path costs nothing extra, and both are
        // asked BEFORE the remote ref is touched.
        var equivalent = false;
        if (merged.ExitCode != 0)
        {
            var cherry = await gitRunner.RunGitAsync(
                clonePath,
                ["cherry", $"refs/remotes/origin/{branch}", remoteRef],
                ct);

            // A cherry that could not run is NOT evidence of equivalence. Fall through to the
            // refusal, which is the direction that cannot lose anything.
            equivalent = cherry.ExitCode == 0
                && !cherry.Stdout
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => line.StartsWith('+'));
        }

        if (merged.ExitCode != 0 && !equivalent)
        {
            var message = $"origin/team/{stored} is not on origin/{branch} yet, so deleting it would lose work.";
            await log.WriteAsync(userId, email, TenantActions.RepoDeleteRemoteBranch, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = message }), ct);
            return Results.Conflict(new
            {
                error = message,
                detail = $"Merge it to {branch} first.",
            });
        }

        // Deleting a remote ref is a push with an EMPTY source - `git push origin :refs/heads/team/{id}`.
        // There is no fast-forward question the way updating a ref has one, so there is nothing
        // here for a forcing flag to apply to in the first place.
        var deleteResult = await gitRunner.PushRefspecAsync(clonePath, string.Empty, $"refs/heads/team/{stored}", ct);
        if (deleteResult.ExitCode != 0)
        {
            var deleteDetail = BoundLines(deleteResult.Stderr);
            await log.WriteAsync(userId, email, TenantActions.RepoDeleteRemoteBranch, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, stderr = deleteDetail }), ct);
            return Results.BadRequest(new { error = "Delete failed.", detail = deleteDetail });
        }

        // WHICH CONDITION PERMITTED THE DELETE IS RECORDED, and the row is kept forever. "Deleted
        // because every commit was reachable from origin/main" and "deleted because every CHANGE
        // was already there under other shas" are different facts, and only one of them can be
        // re-derived later from the repository - the branch is gone either way.
        //
        // THE SECOND WORD IS `content`, deliberately: it is the same fact the status reports as
        // TeamMergedToMainBy, and one repository answering "content" on the card while its audit
        // row said something else would read as two findings.
        //
        // THE WORDS THEMSELVES LIVE IN CONTRACTS - <see cref="MergedToMainBy"/> - because the CLI
        // renders the same vocabulary from ANOTHER ASSEMBLY, and a private copy here could not reach
        // it. Three surfaces, one store.
        var permittedBy = merged.ExitCode == 0 ? MergedToMainBy.Ancestry : MergedToMainBy.Content;

        await log.WriteAsync(userId, email, TenantActions.RepoDeleteRemoteBranch, $"{stored}/{repo}", null,
            JsonSerializer.Serialize(new { success = true, permittedBy }), ct);

        var how = merged.ExitCode == 0
            ? string.Empty
            : $" Its changes were already on {branch} under different commits.";

        return await ActionResultAsync(
            httpContext, gitRunner, RepoUrls.DeriveName(repoUrl), clonePath, stored,
            $"Deleted origin/team/{stored}.{how}", success: true,
            originReachable: true, originUnreachableReason: null, ct);
    }

    /// <summary>
    /// Truncates git output to a sane number of lines before it reaches a response body or a
    /// permanent <c>tenant_events</c> row. The reviewer's ruling was that the disclosure itself is
    /// fine here - repo-relative paths, no file contents, people-only route - but unbounded
    /// text in an append-only audit log is a different problem: one enormous porcelain listing or
    /// rebase transcript stored forever, for every attempt.
    /// </summary>
    private const int MaxDetailLines = 20;

    /// <summary>
    /// <b>IT REDACTS AS WELL AS BOUNDS, AND THAT IS WHY IT IS THE CHOKE POINT.</b>
    /// <c>Git_output_reaching_a_caller_is_bounded</c> already fails the build when git output reaches
    /// a response body or a <c>tenant_events</c> row without passing through here, so every one of
    /// those sites - every fetch, push, rebase, merge, worktree-remove and prune failure on this
    /// surface - is covered by putting <see cref="GitOutputRedaction"/> in this one method. Doing it
    /// per site would be sixteen calls, each of which a seventeenth route can forget.
    ///
    /// <para>
    /// <b>WHAT LEAKS HERE.</b> A team's remote may legally carry userinfo -
    /// <c>https://x-access-token:&lt;token&gt;@host/repo.git</c> is what <c>RepoUrls.Validate</c>
    /// accepts and what the platform's clone authenticates with - and git quotes the remote it could
    /// not reach into <c>fatal: unable to access '…'</c>. <c>tenant_events</c> keeps that row.
    /// </para>
    ///
    /// <para>
    /// <b>THE USERINFO HALF ONLY.</b> <see cref="GitOutputRedaction.Redact"/> ends in an 8-line bound
    /// and a 4,000-character ceiling; underneath the 20-line bound below, the first would make
    /// <see cref="MaxDetailLines"/> unreachable and <c>"… and N more line(s), truncated."</c> a
    /// number nobody could reproduce. Redaction here must not change what the text SAYS, only whether
    /// it carries a credential.
    /// </para>
    ///
    /// <para>
    /// Public rather than private because there is no <c>InternalsVisibleTo</c> from
    /// <c>Harness.Host</c> to its test projects, and the redaction is the half that cannot be
    /// proven through a route: git 2.55 sanitises the URL in its own <c>unable to access</c> line, so
    /// a live failed fetch against a credentialed remote passes whatever this method does.
    /// </para>
    /// </summary>
    public static string BoundLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var safe = GitOutputRedaction.RedactUserInfo(text) ?? string.Empty;

        // Trim the trailing newline BEFORE counting. `GitRunner.PumpAsync` appends one after the
        // last real line, and without trimming it first, `Split('\n')` turns it into a phantom
        // empty final element - counted as a line that does not exist, so "N more line(s)" was
        // always one too high.
        var normalized = safe.Replace("\r\n", "\n").TrimEnd('\n');
        var lines = normalized.Split('\n');
        if (lines.Length <= MaxDetailLines)
        {
            return safe;
        }

        var shown = string.Join('\n', lines.Take(MaxDetailLines));
        var remaining = lines.Length - MaxDetailLines;
        return $"{shown}\n... and {remaining} more line(s), truncated.";
    }

    private static async Task<IResult> MergeToMainAsync(
        [Description(Describe.Team)] string team,
        [Description("The repository name (derived from the URL in the team's repos list)")]
        string repo,
        HttpContext httpContext,
        TeamRegistry teams,
        GitRunner gitRunner,
        TeamPaths paths,
        ContainerHost host,
        ITenantLog log,
        BacklogTipRecorder landedRecorder,
        BacklogLandedCache landedCache,
        CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        // Check if team has any running members
        var busyMembers = GetBusyMembers(teams, host, stored);
        if (busyMembers.Count > 0)
        {
            await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = $"{string.Join(", ", busyMembers)} still working." }), ct);
            return Results.Conflict(new { error = $"{string.Join(", ", busyMembers)} still working.", busy = busyMembers });
        }

        // Verify repo exists
        var repoUrl = teams.ReposFor(stored).FirstOrDefault(u => RepoUrls.DeriveName(u).Equals(repo, StringComparison.OrdinalIgnoreCase));
        if (repoUrl is null)
        {
            return Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." });
        }

        var clonePath = Path.Combine(paths.ReposFor(stored), repo, "main");

        if (!Directory.Exists(clonePath))
        {
            return Results.NotFound(new { error = "Repository not cloned yet." });
        }

        // "main" in this handler is the stored default branch. Not known stops before
        // anything is touched; the value is read again after the fetch below.
        var name = RepoUrls.DeriveName(repoUrl);

        // IN CONTRIBUTOR MODE THERE IS NO MERGE TO MAIN. Origin is the fork, and pushing to
        // its default branch would diverge it from upstream - the next Bring current would refuse
        // it. The work goes upstream as a pull request, from Open pull request.
        if (teams.ContributorFor(stored, name).ContributorMode)
        {
            var contributing = $"{name} is in contributor mode: its origin is a fork, so the work goes upstream "
                + "with Open pull request, not Merge to main. Nothing was changed.";
            await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{name}", null,
                JsonSerializer.Serialize(new { refused = true, reason = contributing }), ct);
            return Results.Conflict(new { error = contributing, contributorMode = true });
        }

        if (teams.DefaultBranchFor(stored, name).Branch is not { } branch)
        {
            return await BranchNotKnownAsync(log, userId, email, TenantActions.RepoMergeToMain, stored, name, ct);
        }

        var status = await gitRunner.StatusAsync(clonePath, branch, $"refs/heads/team/{stored}", ct);

        if (status.Dirty)
        {
            // NAME THE FILES, the way `RebaseRepoAsync` does. The constraint is not "refuse a dirty
            // tree", it is "refuse it NAMING the files": a bare sentence tells the person clearing
            // this neither what to commit nor what to remove, and the two routes giving different
            // answers to the same condition is how one of them stops being maintained.
            var porcelain = await gitRunner.RunGitAsync(clonePath, ["status", "--porcelain"], ct);
            var files = BoundLines(porcelain.Stdout);
            const string dirty = "Clone has uncommitted changes; refusing to merge.";
            await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = dirty, files }), ct);
            return Results.BadRequest(new
            {
                error = dirty,
                detail = "Commit or remove them first. Nothing here will discard them for you.",
                files,
            });
        }

        if (string.Equals(status.HeadCheckout, "detached", StringComparison.Ordinal))
        {
            const string detached = "Clone is on a detached HEAD; refusing to merge.";
            await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = detached }), ct);
            return Results.BadRequest(new { error = detached });
        }

        // Fetch origin
        var fetchResult = await gitRunner.FetchAsync(clonePath, ct);
        if (fetchResult.ExitCode != 0)
        {
            await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, stderr = BoundLines(fetchResult.Stderr) }), ct);
            return Results.BadRequest(new { error = "Fetch failed.", detail = BoundLines(fetchResult.Stderr) });
        }

        // Every successful Fetch reads origin's HEAD. A different answer than the one the
        // checks above used stops here rather than merging into another branch.
        if (await DefaultBranchRecorder.RecordFromOriginAsync(teams, gitRunner, stored, name, clonePath, ct) != branch)
        {
            const string moved = "The repository's default branch changed when origin was fetched, so nothing was merged. Try again.";
            await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = moved }), ct);
            return Results.Conflict(new { error = moved });
        }

        var originBranch = $"origin/{branch}";

        // Use the same fallback the status already uses:
        // - local team/{id} if present
        // - else origin/team/{id} if present
        // - else refuse
        var teamRef = $"team/{stored}";
        var teamShaResult = await gitRunner.RunGitAsync(
            clonePath, ["rev-parse", "--verify", $"refs/heads/{teamRef}"], ct);
        
        string? resolvedRef;
        string? teamSha;
        
        if (teamShaResult.ExitCode == 0)
        {
            // Local team branch exists
            teamSha = teamShaResult.Stdout.Trim();
            resolvedRef = $"refs/heads/{teamRef}";
        }
        else
        {
            // Try origin/team/{id}
            var originTeamResult = await gitRunner.RunGitAsync(
                clonePath, ["rev-parse", "--verify", $"origin/{teamRef}"], ct);
            if (originTeamResult.ExitCode == 0)
            {
                teamSha = originTeamResult.Stdout.Trim();
                resolvedRef = $"origin/{teamRef}";
            }
            else
            {
                var missing = $"Team branch {teamRef} does not exist locally or on origin.";
                await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                    JsonSerializer.Serialize(new { success = false, reason = missing }), ct);
                return Results.BadRequest(new { error = missing });
            }
        }

        var mainShaResult = await gitRunner.RunGitAsync(
            clonePath, ["rev-parse", "--verify", $"refs/heads/{branch}"], ct);
        var mainSha = mainShaResult.ExitCode == 0 ? mainShaResult.Stdout.Trim() : null;
        var originMainResult = await gitRunner.RunGitAsync(
            clonePath, ["rev-parse", "--verify", originBranch], ct);
        var originMain = originMainResult.ExitCode == 0 ? originMainResult.Stdout.Trim() : null;

        // Name origin/main here, not the clone's local main: the two arms that can still reach this
        // compare against origin/main, and local main can sit anywhere relative to it. Naming local
        // main would let the two shas coincide and print the ref as rejected against itself - the
        // sha that was actually checked would never appear.
        //
        // IT IS A RACE MESSAGE AND IT IS NOT DEAD. A team branch behind origin/main is handled by
        // the merge arm below, so the only way either fast-forward arm sees a rejected push
        // is origin/main having moved between the fetch a moment ago and the push. "Bring the
        // branch current and re-gate" would be advice for a problem the caller does not have.
        //
        // IT OFFERS THAT RACE, IT DOES NOT ASSERT IT. This sentence is produced for ANY non-zero
        // exit from the push - a credential failure, a remote hook, a quota - and stating
        // "origin/main moved while this ran" would tell the reader a cause the server never stated.
        // Near-always right is still a guess. So the first sentence says only what is KNOWN, which
        // is that the push was rejected and nothing changed, and the race is offered as the likely
        // explanation. The authoritative answer is git's own stderr, which every caller of this
        // already carries as `detail`.
        string NotFastForwardMessage() =>
            $"The push of {resolvedRef} ({ShortenSha(teamSha)}) to {originBranch} "
            + $"({ShortenSha(originMain)}) was rejected, so nothing was changed. "
            + $"Most likely {originBranch} moved between the check and the push, in which case trying "
            + "again will work; the detail below is what the remote actually said.";

        if (originMain is null)
        {
            var noOrigin = $"{originBranch} does not exist.";
            await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, reason = noOrigin }), ct);
            return Results.BadRequest(new { error = noOrigin });
        }

        // A STALE TEAM BRANCH IS NOT THE SAME AS NOTHING TO INTEGRATE, and conflating them dead-ends
        // a team. `origin/team/{id}` records where the team's work WAS; after a rebase it holds
        // the pre-rebase copies, so `origin/main` is not an ancestor of it - while the clone's own
        // `main` carries exactly the same work, replayed, and fast-forwards `origin/main` perfectly.
        //
        // WHAT THE FALLBACK IS NOT FOR: "the team branch is behind origin/main". THE MERGE
        // ARM BELOW OWNS THAT CONDITION: a team branch that is merely behind main is joined to it
        // with a real merge commit, and no rebase is needed to merge anything. Sending the team to
        // Rebase instead would rewrite its commits onto a new base, so the team's sha would stop
        // being an ancestor of anything on origin and the branch would be unmergeable and
        // undeletable for good.
        //
        // IT EARNS ITS PLACE AS A RESCUE FOR A CLONE THAT HAS ALREADY BEEN REBASED. `/rebase`
        // remains a button for a team that wants a linear branch, so the post-rebase state is
        // reachable - deliberately, rather than as the only road to a merge. In that state
        // local `main` holds the replayed commits, `origin/team/{id}` holds the originals, and the
        // merge arm is the WRONG answer twice over: it would land the pre-rebase commits on main,
        // and local main - which is neither parent of that merge - could then not fast-forward to
        // it, leaving origin/main moved and the clone unable to follow. Pushing local main is the
        // one outcome that leaves the clone and origin agreeing.
        //
        // SO IT IS CHECKED FIRST, AND IT IS ONLY EVER A FALLBACK. It needs `origin/main` to be
        // an ancestor of local main - asked of git, not inferred - and local main to be STRICTLY
        // ahead of it, which between them are only reachable when this clone has already integrated
        // origin/main by some other means: a rebase the team chose, or a merge somebody did by hand.
        // A clone sitting exactly on origin/main has nothing of its own to add and falls through to
        // the merge arm, which is where the team branch gets integrated properly.
        //
        // STILL FAST-FORWARD ONLY, STILL NO FORCE, on this arm and on every other.
        //
        // WHAT IS LEFT BEHIND IS THE PRICE, AND IT IS PAID OUT LOUD. Commits reachable from
        // `origin/team/{id}` but not from local main are not integrated by this. The Tidied rung
        // refuses to delete that branch - `merge-base --is-ancestor` there still says no - and the
        // ladder says why rather than offering a button that must fail.
        var ancestor = await gitRunner.RunGitAsync(
            clonePath, ["merge-base", "--is-ancestor", originBranch, resolvedRef], ct);
        var integratedLocalMain = false;

        // The commit that is pushed to origin/main, and the left-hand side of the refspec. On the
        // two fast-forward arms it is a ref name; on the merge arm it is the sha of a commit that
        // no ref points at yet, which git resolves on the left of a refspec just as happily.
        string pushSource;

        // Set ONLY on the merge arm, and the one signal that tells the arms apart downstream.
        string? mergeCommit = null;

        if (ancestor.ExitCode == 0)
        {
            // FAST-FORWARD, THE ORDINARY CASE, UNCHANGED. The team branch already contains
            // origin/main, so origin/main can simply be moved up to it and there is no new object
            // to create. Never rely on HEAD: push the team ref (local or remote-tracking) to
            // origin/main BY NAME, and let the server refuse anything that is not a fast-forward.
            // When resolvedRef is "origin/team/{id}" this becomes
            // `git push origin origin/team/{id}:main` - a remote-tracking ref is pushed as it
            // stands, because it is already a name git resolves on the left of a refspec, while a
            // local ref is pushed by its short name.
            pushSource = resolvedRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? resolvedRef["refs/heads/".Length..]
                : resolvedRef;
        }
        else if (mainSha is not null
            && !string.Equals(mainSha, originMain, StringComparison.Ordinal)
            && (await gitRunner.RunGitAsync(
                clonePath, ["merge-base", "--is-ancestor", originBranch, $"refs/heads/{branch}"], ct)).ExitCode == 0)
        {
            // REASSIGNED, so everything downstream follows ONE answer to "which ref is being
            // integrated": the push source, the audit row's `mergedFrom`, the message a person
            // reads, and `NotFastForwardMessage()` if the push itself is rejected. A second variable
            // beside these is a second answer waiting to disagree.
            resolvedRef = $"refs/heads/{branch}";
            teamSha = mainSha;
            integratedLocalMain = true;
            pushSource = branch;
        }
        else
        {
            // THE MERGE ARM. The team branch is behind origin/main - it was cut from main, work
            // landed on both sides, and neither contains the other. THE TEAM'S COMMITS ARE NOT
            // REPLAYED ONTO A NEW BASE HERE, EVER. What reaches main is exactly what the team tested
            // plus whatever main already had, joined by ONE new object whose second parent is the
            // team's sha itself - so `merge-base --is-ancestor <team sha> main` keeps answering yes
            // and the branch on origin never moves. main's history stops being linear and that cost
            // is accepted.
            //
            // NOTHING IS RE-TESTED BEFORE THE PUSH, DELIBERATELY. A rewrite is what makes re-testing
            // necessary - a replayed commit is a tree nobody ever tested - and combining two
            // already-tested trees is not the same risk.

            // ALREADY THERE IS NOT A MERGE. With the team branch wholly contained in origin/main -
            // it was integrated by somebody else, or by an earlier click of this button - a merge
            // would write a commit that changes nothing and move main for no reason. Refused, and
            // told apart from the divergence above, because "your work is already on main" and
            // "your branch and main disagree" send a person to entirely different places.
            var alreadyOnMain = await gitRunner.RunGitAsync(
                clonePath, ["merge-base", "--is-ancestor", resolvedRef, originBranch], ct);
            if (alreadyOnMain.ExitCode == 0)
            {
                var nothing =
                    $"{resolvedRef} ({ShortenSha(teamSha)}) is already on {originBranch} ({ShortenSha(originMain)}); "
                    + "there is nothing to merge.";
                await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                    JsonSerializer.Serialize(new { success = false, reason = nothing }), ct);
                return Results.BadRequest(new { error = nothing });
            }

            // MERGED IN THE OBJECT DATABASE, NOT IN THE WORKING TREE. `git merge-tree --write-tree`
            // performs the same three-way merge `git merge` would and writes the resulting tree,
            // touching neither HEAD nor the index nor a single file on disk. That is what lets this
            // route keep the promise it has always kept - it does not check anything out, it does
            // not move somebody's HEAD, and the tests that assert HEAD is still on `team/{id}`
            // afterwards go on passing.
            //
            // A CONFLICT THEREFORE LEAVES NOTHING BEHIND BY CONSTRUCTION, which is a stronger form
            // of the guarantee `RebaseRepoAsync` buys with "abort, always, and before anything else
            // is answered". There is no `.git/rebase-merge`, no MERGE_HEAD, no half-written tree
            // and so no abort to forget on some future return path - not even the cancellation path
            // that needed its own `catch` over there.
            var mergeTree = await gitRunner.MergeTreeAsync(clonePath, originBranch, resolvedRef, ct);

            // THE FIRST LINE, NOT THE EXIT CODE, SAYS WHETHER A MERGE HAPPENED AT ALL. `merge-tree`
            // answers 1 both for a genuine conflict and for a refusal to merge - a ref it cannot
            // resolve, a git too old for `--write-tree` - and only the first tells a person to go
            // and resolve something. Same distinction `RebaseRepoAsync` draws with `rebaseStarted`:
            // reporting "would conflict" for a missing binary sends somebody hunting a conflict
            // that was never there. On a merge, conflicted or not, the first line is the tree oid.
            var firstLine = mergeTree.Stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim();
            var mergedTree = IsObjectSha(firstLine) ? firstLine : null;
            var mergeOutput = BoundLines(mergeTree.Stdout + mergeTree.Stderr);

            if (mergedTree is null)
            {
                await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                    JsonSerializer.Serialize(new { success = false, reason = "could not run", detail = mergeOutput }), ct);
                return Results.BadRequest(new { error = "The merge could not be run.", detail = mergeOutput });
            }

            if (mergeTree.ExitCode != 0)
            {
                var conflicted =
                    $"Merging {resolvedRef} ({ShortenSha(teamSha)}) into {originBranch} ({ShortenSha(originMain)}) "
                    + "conflicts, so nothing was changed.";
                await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                    JsonSerializer.Serialize(new { success = false, conflicted = true, reason = conflicted, detail = mergeOutput }), ct);
                return Results.Conflict(new
                {
                    error = conflicted,
                    detail = "Resolve it on the team branch and push again. The clone was not touched: "
                        + "no branch moved, no file changed, and there is nothing here to abort.\n" + mergeOutput,
                });
            }

            // ORIGIN/MAIN IS THE FIRST PARENT, so main's first-parent history stays main's own and
            // `git log --first-parent main` still reads as the sequence of integrations rather than
            // wandering off down the team branch. The team's sha is the second parent, by its SHA
            // rather than by its ref name - the ref is a moving target and the sha is the fact.
            var commitTree = await gitRunner.CommitTreeAsync(
                clonePath,
                mergedTree,
                originMain,
                teamSha!,
                $"Merge {resolvedRef} into {branch}",
                $"The team branch was behind {branch}, so its commits are joined here rather than "
                + $"replayed onto a new base: {teamSha} survives unchanged as the second parent, and "
                + $"what reaches {branch} is exactly what the team tested plus what {branch} already had.",
                ct);

            mergeCommit = commitTree.Stdout.Trim();
            if (commitTree.ExitCode != 0 || !IsObjectSha(mergeCommit))
            {
                // NOTHING HAS MOVED AT THIS POINT - the tree exists as a loose object and no ref
                // names it - so this is a plain refusal rather than a half-finished merge. The
                // likeliest cause by far is a clone with no `user.email`, and git's own message
                // says exactly that and exactly how to fix it, so it is passed through.
                var commitOutput = BoundLines(commitTree.Stdout + commitTree.Stderr);
                await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                    JsonSerializer.Serialize(new { success = false, reason = "commit-tree failed", detail = commitOutput }), ct);
                return Results.BadRequest(new
                {
                    error = "The merge succeeded but the merge commit could not be written, so nothing was changed.",
                    detail = commitOutput,
                });
            }

            pushSource = mergeCommit;
        }

        var pushResult = await gitRunner.PushRefspecAsync(clonePath, pushSource, branch, ct);
        if (pushResult.ExitCode != 0)
        {
            // THE MERGE ARM GETS ITS OWN SENTENCE. `NotFastForwardMessage()` names the team ref and
            // the origin/main it was measured against, which is what a person needs when the ref
            // they asked to push is the thing in question - and on this arm it is not: what was
            // pushed has origin/main as its first parent, so a rejection here is about origin/main
            // and not about the branch at all. Pointing them back at the branch would send them
            // after a problem they do not have. Nothing was left behind either way: the merge
            // commit is unreferenced, and the next click merges the origin/main that is there now.
            //
            // LIKELY, NOT KNOWN - the same correction `NotFastForwardMessage()` carries above. All
            // this arm observes is a non-zero exit from the push; that origin/main moved is an
            // inference from it, however good, and is not stated as fact. git's own stderr
            // goes out as `detail` and is where the real answer already is.
            var message = mergeCommit is null
                ? NotFastForwardMessage()
                : $"The push of the merge commit to {originBranch} was rejected, so nothing was changed. "
                    + $"Most likely {originBranch} moved while the merge was being prepared, in which "
                    + "case trying again will work; the detail below is what the remote actually said.";
            await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, reason = message, stderr = BoundLines(pushResult.Stderr) }), ct);
            return Results.BadRequest(new { error = message, detail = BoundLines(pushResult.Stderr) });
        }

        // Local main must also move: it is what MainAhead and the sweep read.
        // git fetch into the current branch is refused, so when HEAD is main, merge.
        // The ref that was pushed is the ref local main moves from, or the two can disagree:
        // checking one ref and acting on another would let this control report a merge it had
        // not performed.
        //
        // `pushSource` IS A SHA ON THE MERGE ARM AND BOTH HELPERS TAKE IT AS READILY AS A NAME -
        // `git merge --ff-only <sha>` and `git fetch . <sha>:main` are the same operations on the
        // same commit. Which keeps the sentence above true: still exactly one answer to "what was
        // pushed", still the same thing local main moves onto.
        //
        // SKIPPED ENTIRELY WHEN LOCAL MAIN IS WHAT WAS PUSHED: it is already at the sha origin/main
        // now holds, so there is nothing to move. Asking git to merge `main` into `main`, or to
        // fetch `main:main`, is a no-op that happens to succeed - and a no-op that has to succeed
        // is a dependency on git's behaviour where an `if` states the fact.
        GitRunner.GitInvocation? localUpdate = null;
        if (integratedLocalMain)
        {
            // Nothing to do.
        }
        else if (string.Equals(status.HeadCheckout, branch, StringComparison.Ordinal))
        {
            localUpdate = await gitRunner.MergeFastForwardAsync(clonePath, pushSource, ct);
        }
        else
        {
            localUpdate = await gitRunner.FetchRefspecAsync(clonePath, pushSource, $"refs/heads/{branch}", ct);
        }

        if (localUpdate is { ExitCode: not 0 })
        {
            // NAMES WHAT ORIGIN/MAIN ACTUALLY HOLDS NOW, which on the merge arm is the merge commit
            // and not the team tip. Reporting the team sha here would have a person go looking for
            // a commit origin/main does not point at.
            var message =
                $"{originBranch} moved to {ShortenSha(mergeCommit ?? teamSha)} but local {branch} could not "
                + $"fast-forward from {ShortenSha(mainSha)}.";
            await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { success = false, reason = message, stderr = BoundLines(localUpdate.Stderr) }), ct);
            return Results.BadRequest(new { error = message, detail = BoundLines(localUpdate.Stderr) });
        }

        // `mergedFrom` STAYS IN THE AUDIT ROW AND LEAVES THE RESPONSE. Which ref was merged is a
        // permanent fact about what this act did, so the tenant log keeps it; the response already
        // names it inside the message, and nothing client-side ever read the field.
        //
        // `mergeCommit` JOINS IT FOR THE SAME REASON, and null is the honest value on the two
        // fast-forward arms: "no new object was written" is a different fact from "a merge commit
        // was written and here it is", and the row is kept forever.
        await log.WriteAsync(userId, email, TenantActions.RepoMergeToMain, $"{stored}/{repo}", null,
            JsonSerializer.Serialize(new { success = true, mergedFrom = resolvedRef, mergeCommit }), ct);

        // LANDED IS STORED AT THE MOMENT IT LANDS. This route has just put the team's sha
        // on origin/<default> and knows it without asking anybody, so the backlog item reads
        // `landed` from here on - after the branch, the clone and the team are gone too. On the
        // merge arm the team's sha is the merge commit's second parent, reachable all the same.
        await landedRecorder.RecordMergedAsync(stored, name, teamSha!, branch, ct);
        landedCache.Clear();

        // WHICH REF WAS INTEGRATED IS THE SENTENCE, not a footnote. A person reading "Team branch
        // merged to main" after the local-main fallback ran would believe the branch on origin is
        // now on main - and it is not, which is exactly why the Tidied rung still refuses to delete
        // it. The two cases are said differently because they ARE different acts.
        //
        // AND A REAL MERGE IS A THIRD ACT, SAID AS ONE. main's history is not linear after this, and that is a thing a person should learn from the sentence they clicked for rather
        // than from `git log` a week later.
        var summary = integratedLocalMain
            ? $"This clone's {branch} merged to {originBranch} and pushed (from {resolvedRef}). "
              + "The team branch on origin was stale and was not integrated."
            : mergeCommit is not null
                ? $"Team branch merged to {branch} with a merge commit ({ShortenSha(mergeCommit)}) and pushed "
                  + $"(from {resolvedRef}). The branch was behind {branch}, so its commits were joined rather "
                  + "than replayed - nothing on origin moved, and the team's commit is a parent of the merge."
                : $"Team branch merged to {branch} and pushed (from {resolvedRef})";

        return await ActionResultAsync(
            httpContext, gitRunner, RepoUrls.DeriveName(repoUrl), clonePath, stored,
            summary, success: true,
            originReachable: true, originUnreachableReason: null, ct);
    }

    private static async Task<IResult> CleanupRepoWorktreesAsync(
        [Description(Describe.Team)] string team,
        [Description("The repository name (derived from the URL in the team's repos list)")]
        string repo,
        HttpContext httpContext,
        TeamRegistry teams,
        GitRunner gitRunner,
        TeamPaths paths,
        ContainerHost host,
        ITenantLog log,
        KanbanStore kanban,
        WorktreeRemoval worktrees,
        CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        // Check if team has any running members
        var busyMembers = GetBusyMembers(teams, host, stored);
        if (busyMembers.Count > 0)
        {
            await log.WriteAsync(userId, email, TenantActions.RepoCleanupWorktrees, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = $"{string.Join(", ", busyMembers)} still working." }), ct);
            return Results.Conflict(new { error = $"{string.Join(", ", busyMembers)} still working.", busy = busyMembers });
        }

        // Verify repo exists
        var repoUrl = teams.ReposFor(stored).FirstOrDefault(u => RepoUrls.DeriveName(u).Equals(repo, StringComparison.OrdinalIgnoreCase));
        if (repoUrl is null)
        {
            return Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." });
        }

        var clonePath = Path.Combine(paths.ReposFor(stored), repo, "main");

        if (!Directory.Exists(clonePath))
        {
            return Results.NotFound(new { error = "Repository not cloned yet." });
        }

        // SETTLED TREES ONLY, UNDER THE ONE NO-FORCE RULE. Every tree git knows for this
        // clone, plus any per-card tree on disk it has lost track of, minus the trees whose card (or
        // workflow) is still open - To Do, running, interrupted, failed or blocked. A tree that is
        // not a card's (a `wt_<Member>` tree) has no card to hold it open.
        //
        // THE REFUSAL OVER UNINTEGRATED COMMITS IS PER TREE, IN THE HELPER, NOT WHOLE-CLEANUP. With a
        // tree per card an open card's branch is ahead of main as a matter of course, so refusing the
        // button for it would refuse it forever. Commits living only in the checkout being removed
        // are protected by `WorktreeRemoval`'s own rule: a tree whose HEAD is not on origin
        // is left and named, and `git worktree remove` never deletes a branch.
        var statusResult = await gitRunner.StatusAsync(
            clonePath, teams.DefaultBranchFor(stored, repo).Branch, $"refs/heads/team/{stored}", ct);

        var (pass, kept) = await worktrees.CleanUpSettledAsync(
            paths, kanban, stored, repo, clonePath, statusResult.Worktrees.Select(wt => wt.Path), ct);

        await log.WriteAsync(userId, email, TenantActions.RepoCleanupWorktrees, $"{stored}/{repo}", null,
            JsonSerializer.Serialize(new
            {
                success = true,
                removed = pass.Removed,
                kept,
                left = pass.Left.Select(tree => new { path = tree.Path, reason = BoundLines(tree.Reason) }),
            }), ct);

        var summary = $"Removed {pass.Removed.Count} settled worktree(s).";
        if (kept.Count > 0)
            summary += $" Kept {kept.Count} for open cards: {string.Join(", ", kept.Select(Path.GetFileName))}.";
        if (pass.Left.Count > 0)
            summary += $" Left {pass.Left.Count} rather than force them: "
                + string.Join("; ", pass.Left.Select(tree => $"{tree.Path} - {tree.Reason}"));

        // THE ONE ACTION ROUTE THAT PASSES NULL, and it is the true answer rather than an omission:
        // removing and pruning worktrees is purely local, so this handler never contacts origin and
        // has no evidence either way. Its six siblings reach their success return only by having
        // fetched successfully, so they say TRUE; this one has nothing to say, and null is how this
        // codebase says NOT MEASURED. Copying `true` down here to make the six look consistent
        // would invent a network round trip that never happened.
        return await ActionResultAsync(
            httpContext, gitRunner, RepoUrls.DeriveName(repoUrl), clonePath, stored,
            summary, success: true,
            originReachable: null, originUnreachableReason: null, ct);
    }

    /// <summary>
    /// THE ONE PLACE A <see cref="RepoStatus"/> IS COMPOSED, and it has eight callers: the status
    /// route's loop and every action route's success return. It was inline in the status route when
    /// it had one caller; seven copies of the same three <c>merge-base --is-ancestor</c> questions
    /// is how a card starts disagreeing with itself depending on which button produced it.
    ///
    /// REACHABILITY IS PASSED IN, NEVER MEASURED HERE, because callers learn it in different ways:
    /// the status route asks <c>ls-remote</c> when <c>refresh</c> is requested, and an action route
    /// knows only what the git work it just did proved. Measuring it in here would put a network
    /// round trip on every caller and turn each action into an origin probe. NULL is a real value
    /// for this argument - "not measured", never "unreachable".
    ///
    /// WHICH CALLER PASSES WHAT IS DELIBERATELY NOT LISTED HERE. That list went stale within one
    /// commit of being written; the rule lives once, at <see cref="ActionResultAsync"/>, and is
    /// about EVIDENCE rather than about which route is asking.
    ///
    /// <paramref name="isPerson"/> is likewise a parameter rather than a <c>true</c> written inside:
    /// the clone's absolute path is shown to people only, and a helper that decides that for itself is
    /// a helper nobody can see making the decision.
    ///
    /// Every git command here is LOCAL - <c>StatusAsync</c>, <c>rev-parse</c> and
    /// <c>merge-base</c> - so composing a status costs no network even on the unreachable-origin
    /// arm.
    /// </summary>
    private static async Task<RepoStatus> ReadRepoStatusAsync(
        GitRunner gitRunner,
        string repoName,
        string clonePath,
        string storedTeamId,
        RepoDefaultBranch defaultBranch,
        bool isPerson,
        bool? originReachable,
        string? originUnreachableReason,
        CancellationToken ct,
        Func<IReadOnlyCollection<string>, Task<IReadOnlySet<string>>>? openKeys = null,
        string? originUrl = null,
        RepoContributor? contributor = null)
    {
        // "main" on this record is the stored default branch. Not known leaves every
        // main-relative answer null (not measured) rather than measuring against a guessed `main`.
        var branch = defaultBranch.Branch;
        var originMain = branch is null ? null : $"refs/remotes/origin/{branch}";

        // In contributor mode the clone is brought current against upstream, so that is what
        // ahead/behind measures; merged and pushed stay about origin, which is the fork.
        var baseRemote = contributor is null ? ContributorRemotes.Origin : ContributorRemotes.BaseFor(contributor);
        var gitStatus = await gitRunner.StatusAsync(
            clonePath, branch, $"refs/heads/team/{storedTeamId}", ct, baseRemote);

        // Determine pushed and merged status. Local team/{id} answers when it
        // exists; origin/team/{id} answers when the local branch was never created
        // (the push pattern the seeded skills tell a manager to use).
        bool? teamPushed = null;
        bool? teamMergedToMain = null;
        string? teamMergedToMainBy = null;
        bool? cloneMainOnTeamBranch = null;
        int? teamCommitsNotOnMain = null;
        var teamSha = gitStatus.TeamSha;
        string? teamPushedFrom = null;

        // HOW MANY COMMITS ON THE TEAM BRANCH CARRY CHANGES `origin/main` DOES NOT HAVE.
        //
        // Every other question here is about SHA ANCESTRY, and a cherry-pick produces a
        // different sha for the same change - so a team integrated that way reads as unmerged
        // forever while every one of its changes is already upstream.
        //
        // `git cherry` answers the CONTENT question by patch-id and is already in git: `+` for a
        // commit with no equivalent upstream, `-` for one that has. IT IS CONSERVATIVE, and that
        // direction is load-bearing - a commit whose patch was modified during integration (a
        // conflict resolved) reports `+`, so the count errs toward "not equivalent", which errs
        // toward refusing. Never make it err the other way.
        //
        // THE MEASUREMENT ITSELF LIVES AT `CherryCountAsync`, because the worktree rows need the same question
        // asked PER WORKTREE and two copies of a conservative count is how the dialog's two halves
        // start disagreeing about one repository.
        Task<int?> CommitsNotOnMainAsync(string teamRefName) =>
            originMain is null
                ? Task.FromResult<int?>(null)
                : CherryCountAsync(gitRunner, clonePath, originMain, teamRefName, ct);

        // MERGED TO MAIN HAS TWO ANSWERS, AND WHICH ONE SAID YES IS PART OF THE ANSWER.
        //
        // Ancestry alone reads FALSE FOREVER for a branch whose commits were REPLAYED ONTO A
        // NEW BASE: every change is on main under different shas, the branch is merged in every
        // sense a person cares about, and the Tidied step - which reads this field - could never
        // delete it.
        //
        // NOTHING NEW IS MEASURED HERE. `TeamCommitsNotOnMain` is already the patch-id answer, and a
        // second measurement of the same question is how two fields on one record start disagreeing.
        //
        // AN UNMEASURED CONTENT ANSWER IS UNKNOWN, NEVER FALSE. `git cherry` failing is not evidence
        // that work is missing, and a confident `false` here is what would let a card say NOT MERGED
        // about a branch nobody managed to measure. Null is how this record says NOT MEASURED
        // everywhere else, and the SPA's own `integratedToMain` resolves it the same way - so taking
        // the decision on the server does not change what it answers.
        static (bool? Merged, string? By) MergedToMain(bool ancestry, int? commitsNotOnMain)
        {
            if (ancestry) return (true, MergedToMainBy.Ancestry);
            if (commitsNotOnMain is null) return (null, null);
            if (commitsNotOnMain == 0) return (true, MergedToMainBy.Content);
            return (false, null);
        }

        // Whether the commits on this clone's local main are already on the team branch.
        // That main is ahead of origin/main is a different question and does not answer this one.
        // Ask the one that is being answered, against whichever ref answered teamSha.
        async Task<bool?> MainIsOnAsync(string teamRefName)
        {
            if (branch is null) return null;

            var reachable = await gitRunner.RunGitAsync(
                clonePath,
                ["merge-base", "--is-ancestor", $"refs/heads/{branch}", teamRefName],
                ct);
            return reachable.ExitCode == 0;
        }

        async Task<(bool? Merged, string? By)> MergedToOriginMainAsync(string teamRef, string teamRefName)
        {
            if (originMain is null) return (null, null);

            var isAncestorMain = await gitRunner.RunGitAsync(
                clonePath, ["merge-base", "--is-ancestor", teamRef, originMain], ct);
            teamCommitsNotOnMain = await CommitsNotOnMainAsync(teamRefName);
            return MergedToMain(isAncestorMain.ExitCode == 0, teamCommitsNotOnMain);
        }

        if (gitStatus.TeamSha is not null)
        {
            teamPushedFrom = $"team/{storedTeamId}";
            cloneMainOnTeamBranch = await MainIsOnAsync($"refs/heads/team/{storedTeamId}");
            if (gitStatus.OriginCheckedAt.HasValue)
            {
                var isAncestorOrigin = await gitRunner.RunGitAsync(
                    clonePath,
                    ["merge-base", "--is-ancestor", gitStatus.TeamSha, $"origin/team/{storedTeamId}"],
                    ct);
                teamPushed = isAncestorOrigin.ExitCode == 0;

                (teamMergedToMain, teamMergedToMainBy) = await MergedToOriginMainAsync(
                    gitStatus.TeamSha, $"refs/heads/team/{storedTeamId}");
            }
        }
        else
        {
            var originTeam = await gitRunner.RunGitAsync(
                clonePath,
                ["rev-parse", "--verify", $"origin/team/{storedTeamId}"],
                ct);
            if (originTeam.ExitCode == 0)
            {
                teamSha = originTeam.Stdout.Trim();
                teamPushed = true;
                teamPushedFrom = $"origin/team/{storedTeamId}";
                cloneMainOnTeamBranch = await MainIsOnAsync($"origin/team/{storedTeamId}");
                if (gitStatus.OriginCheckedAt.HasValue)
                {
                    (teamMergedToMain, teamMergedToMainBy) = await MergedToOriginMainAsync(
                        teamSha, $"origin/team/{storedTeamId}");
                }
            }
            else if (gitStatus.OriginCheckedAt.HasValue)
            {
                // NEITHER REF EXISTS, AND THAT IS AN ANSWER RATHER THAN AN ABSENCE OF ONE.
                //
                // Assigning nothing here would leave `teamPushed` at its initial null, and the card
                // would say `pushed unknown` about a team just established to have no branch
                // anywhere. `teamPushed === false` is what renders "not on origin" in the client.
                //
                // GUARDED ON `OriginCheckedAt`, and that guard is the whole correctness of this.
                // `rev-parse --verify origin/team/{id}` reads the LOCAL REMOTE-TRACKING REF, so on a
                // clone that has never fetched its absence says nothing about origin at all -
                // answering `false` there would be a confident lie in place of an honest "unknown".
                // With a fetch behind us the absence IS evidence, which is exactly the distinction
                // null carries everywhere else on this record.
                // ONLY `teamPushed`. "Is the team branch merged to main" has no answer when there
                // is no team branch, and `false` there would assert something about a ref that does
                // not exist. It also changes nothing: the ladder reads `teamMergedToMain !== true`,
                // which null already satisfies, so `false` would buy a claim and no behaviour.
                teamPushed = false;
            }
        }

        // BEHIND THE DEFAULT BRANCH, AND NOT PUSHED: the two states Bring current and merge and
        // Push answer. Measured only with a fetch behind us, as teamPushed is above.
        var teamRefName = teamPushedFrom is null
            ? null
            : gitStatus.TeamSha is not null ? $"refs/heads/team/{storedTeamId}" : $"refs/remotes/origin/team/{storedTeamId}";
        var (teamBranchBehindDefault, filesChangedOnBothSides) =
            teamRefName is not null && originMain is not null && gitStatus.OriginCheckedAt.HasValue
                && contributor?.ContributorMode != true
                ? await BehindDefaultAsync(gitRunner, clonePath, teamRefName, originMain, ct)
                : (null, null);
        var teamBranchUnpushed = gitStatus.TeamSha is not null && gitStatus.OriginCheckedAt.HasValue
            ? await TeamBranchUnpushedAsync(gitRunner, clonePath, storedTeamId, ct)
            : (bool?)null;

        // Map worktrees. ASYNC BECAUSE EACH ROW IS MEASURED rather than transcribed - see
        // `MapWorktreesAsync`. Every command it runs is local, like everything else here.
        var worktrees = await MapWorktreesAsync(gitRunner, gitStatus.Worktrees, clonePath, originMain, openKeys, ct);

        // The Manager's delivery rule, measured: work left on the clone's default branch. See `DefaultBranchMove`.
        var movedTo = await DefaultBranchMove.MovedToAsync(gitRunner, clonePath, branch, ct);

        var originCheckedAtStr = gitStatus.OriginCheckedAt.HasValue
            ? gitStatus.OriginCheckedAt.Value.ToString("O")
            : null;

        return new RepoStatus(
            Name: repoName,
            ClonePath: isPerson ? clonePath : null,
            MainSha: ShortenSha(gitStatus.MainSha),
            TeamSha: ShortenSha(teamSha),
            MainAhead: gitStatus.MainAhead,
            MainBehind: gitStatus.MainBehind,
            Dirty: gitStatus.Dirty,
            HeadCheckout: gitStatus.HeadCheckout,
            TeamBranch: $"team/{storedTeamId}",
            TeamPushed: teamPushed,
            TeamPushedFrom: teamPushedFrom,
            TeamMergedToMain: teamMergedToMain,
            TeamMergedToMainBy: teamMergedToMainBy,
            CloneMainOnTeamBranch: cloneMainOnTeamBranch,
            TeamCommitsNotOnMain: teamCommitsNotOnMain,
            OriginCheckedAt: originCheckedAtStr,
            OriginReachable: originReachable,
            OriginUnreachableReason: originUnreachableReason,
            Worktrees: worktrees,
            DefaultBranch: branch,
            DefaultBranchSource: DefaultBranchSource(defaultBranch),
            OriginUrl: GitOutputRedaction.RedactUserInfo(originUrl),
            UpstreamUrl: GitOutputRedaction.RedactUserInfo(contributor?.UpstreamUrl),
            TeamBranchBehindDefault: teamBranchBehindDefault,
            FilesChangedOnBothSides: filesChangedOnBothSides,
            TeamBranchUnpushed: teamBranchUnpushed,
            DefaultBranchMoved: movedTo is null ? null : DefaultBranchMove.Sentence(branch!, movedTo, storedTeamId));
    }

    /// <summary>The most files <see cref="BehindDefaultAsync"/> names; the dialog needs a few, not a diff.</summary>
    private const int MaxOverlapFiles = 20;

    /// <summary>
    /// How many commits <paramref name="originMain"/> has that <paramref name="teamRef"/> lacks and,
    /// when there are any, the files both sides changed since their merge base. Null for either
    /// answer git could not give: not measured, never zero.
    /// </summary>
    private static async Task<(int? Behind, IReadOnlyList<string>? Files)> BehindDefaultAsync(
        GitRunner gitRunner, string clonePath, string teamRef, string originMain, CancellationToken ct)
    {
        var behind = await gitRunner.CountCommitsNotInAsync(clonePath, originMain, teamRef, ct);
        if (behind is not > 0) return (behind, behind is null ? null : []);

        var mergeBase = await gitRunner.RunGitAsync(clonePath, ["merge-base", teamRef, originMain], ct);
        if (mergeBase.ExitCode != 0) return (behind, null);
        var basis = mergeBase.Stdout.Trim();

        async Task<HashSet<string>?> ChangedAsync(string tip)
        {
            var diff = await gitRunner.RunGitAsync(clonePath, ["diff", "--name-only", basis, tip], ct);
            return diff.ExitCode != 0
                ? null
                : diff.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        }

        var ours = await ChangedAsync(teamRef);
        var theirs = await ChangedAsync(originMain);
        if (ours is null || theirs is null) return (behind, null);

        return (behind, ours.Where(theirs.Contains).Order(StringComparer.Ordinal).Take(MaxOverlapFiles).ToList());
    }

    /// <summary>
    /// Whether the local team/{id} is ahead of origin's copy, or origin has none: what Push
    /// publishes as a fast-forward. See <see cref="GitRunner.BranchAheadOfOriginAsync"/>.
    /// </summary>
    private static Task<bool> TeamBranchUnpushedAsync(
        GitRunner gitRunner, string clonePath, string storedTeamId, CancellationToken ct) =>
        gitRunner.BranchAheadOfOriginAsync(clonePath, $"team/{storedTeamId}", ct);

    /// <summary>'person', 'remote', or null when not known. See <see cref="RepoStatus.DefaultBranchSource"/>.</summary>
    private static string? DefaultBranchSource(RepoDefaultBranch branch) =>
        branch.SetByPerson is not null ? "person" : branch.FromRemote is not null ? "remote" : null;

    /// <summary>
    /// The success answer every action route gives: the repo, the status it has just produced, a
    /// sentence, and whether the action did what it set out to do.
    ///
    /// <c>isPerson: true</c> IS ASSERTED AT THE CALL SITE RATHER THAN ASSUMED INSIDE THE HELPER -
    /// every action route in this file carries <c>.HumansOnly()</c>, so the clone's absolute host
    /// path this puts on the wire is going to a person. <c>Merge_to_main_refuses_machine_principals</c>
    /// is the proof that the marker bites.
    ///
    /// Reachability is NOT measured here: <paramref name="originReachable"/> is whatever the caller
    /// learned from the git work it just did. Adding an <c>ls-remote</c> here would make every
    /// action a network probe.
    ///
    /// THE RULE FOR THAT ARGUMENT, and it is a rule about EVIDENCE rather than about which command
    /// was used. Six of the seven action routes run <c>git fetch origin</c> and REFUSE with 400 on a
    /// non-zero exit, so reaching a success return at all proves origin was contacted - they pass
    /// TRUE. The field means "could we reach origin", not "did we ask with <c>ls-remote</c>": a
    /// successful fetch answers that question with MORE work than the status route's probe, not
    /// less, so reporting null there would under-report a fact the handler is already holding.
    /// Only a route that never touches the network passes null, which means NOT MEASURED - see
    /// <see cref="CleanupRepoWorktreesAsync"/>, the one such route, where null is the true answer.
    /// </summary>
    private static async Task<IResult> ActionResultAsync(
        HttpContext httpContext,
        GitRunner gitRunner,
        string repoName,
        string clonePath,
        string storedTeamId,
        string message,
        bool success,
        bool? originReachable,
        string? originUnreachableReason,
        CancellationToken ct)
    {
        var teams = httpContext.RequestServices.GetRequiredService<TeamRegistry>();
        var status = await ReadRepoStatusAsync(
            gitRunner,
            repoName,
            clonePath,
            storedTeamId,
            teams.DefaultBranchFor(storedTeamId, repoName),
            isPerson: true,
            originReachable,
            originUnreachableReason,
            ct,
            OpenKeysFor(httpContext, storedTeamId, ct),
            teams.ReposFor(storedTeamId).FirstOrDefault(u => string.Equals(RepoUrls.DeriveName(u), repoName, StringComparison.OrdinalIgnoreCase)),
            teams.ContributorFor(storedTeamId, repoName));

        status = await WithContributorAsync(httpContext, status, storedTeamId, ct);
        return Results.Ok(new RepoActionResult(repoName, status, message, success));
    }

    /// <summary>
    /// The contributor facts a status carries beside its git ones: the fork's owner, the
    /// DCO and CLA settings, and the recorded pull request - asked of GitHub here, when the Git
    /// dialog is read, at most once a minute per pull request (<see cref="PullRequestStateReader"/>).
    /// An owned repository gets none of them.
    /// </summary>
    private static async Task<RepoStatus> WithContributorAsync(
        HttpContext httpContext, RepoStatus status, string storedTeamId, CancellationToken ct)
    {
        var teams = httpContext.RequestServices.GetRequiredService<TeamRegistry>();
        var contributor = teams.ContributorFor(storedTeamId, status.Name);
        if (!contributor.ContributorMode)
        {
            return status with { DcoSignOff = contributor.DcoSignOff, ClaSignedNote = contributor.ClaSignedNote };
        }

        var reading = httpContext.RequestServices.GetService<PullRequestStateReader>() is { } reader
            ? await reader.ReadAsync(storedTeamId, status.Name, ct)
            : null;

        return status with
        {
            ForkOwner = contributor.ForkOwner,
            DcoSignOff = contributor.DcoSignOff,
            ClaSignedNote = contributor.ClaSignedNote,
            PullRequest = reading is null ? null : PullRequestStatus(reading),
        };
    }

    private static RepoPullRequestStatus PullRequestStatus(PullRequestReading reading) => new(
        reading.PullRequest.Url,
        reading.PullRequest.Number,
        reading.PullRequest.State,
        reading.PullRequest.ReadAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        reading.Landing,
        reading.UnknownReason);

    /// <summary>
    /// Which worktree keys still belong to open work on this team, asked of the board and
    /// the log - the rule <see cref="WorktreeRemoval.OpenKeysAsync"/> owns. Resolved from the request
    /// so every route composing a <see cref="RepoStatus"/> can mark the open trees.
    /// </summary>
    private static Func<IReadOnlyCollection<string>, Task<IReadOnlySet<string>>> OpenKeysFor(
        HttpContext httpContext, string storedTeamId, CancellationToken ct)
    {
        var kanban = httpContext.RequestServices.GetRequiredService<KanbanStore>();
        var messages = httpContext.RequestServices.GetRequiredService<IMessageLog>();

        return keys => WorktreeRemoval.OpenKeysAsync(kanban, messages, storedTeamId, keys, ct);
    }

    /// <summary>
    /// HOW MANY COMMITS ON <paramref name="refName"/> CARRY A CHANGE <paramref name="originMain"/>
    /// (origin's copy of the stored default branch) DOES NOT HAVE. One implementation, two callers: the team ref and every worktree.
    ///
    /// <c>git cherry</c> answers by PATCH-ID and is already in git: <c>+</c> for a commit with no
    /// equivalent upstream, <c>-</c> for one that has. IT IS CONSERVATIVE, and that direction is
    /// load-bearing - a commit whose patch was modified during integration (a conflict resolved)
    /// reports <c>+</c>, so the count errs toward "not equivalent", which errs toward refusing.
    /// Never make it err the other way.
    ///
    /// RUN FROM THE CLONE FOR EVERY REF, INCLUDING A WORKTREE'S OWN BRANCH. Worktrees share one
    /// object database and one ref store with the clone that made them, so the answer is identical
    /// and this never spawns git in a directory that may have been removed underneath us.
    /// </summary>
    private static async Task<int?> CherryCountAsync(
        GitRunner gitRunner,
        string clonePath,
        string originMain,
        string refName,
        CancellationToken ct)
    {
        var cherry = await gitRunner.RunGitAsync(clonePath, ["cherry", originMain, refName], ct);

        // A failure here is NOT evidence of zero. Null is "not measured", exactly as it is for
        // every other nullable on this record, and the ladder must not read it as merged.
        if (cherry.ExitCode != 0) return null;

        // Split on BOTH newline characters: git's output reaches us with CRLF on Windows, and
        // splitting on one of them alone leaves a stray carriage return on every line.
        return cherry.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith('+'));
    }

    /// <summary>
    /// EACH ROW CARRIES A CONTENT ANSWER AS WELL AS A SHA ONE.
    ///
    /// <c>AheadMain</c> and <c>BehindMain</c> arrive already counted by <c>GitRunner</c> and are
    /// pure SHA ANCESTRY, against this clone's LOCAL main. A rebase gives the same change a new sha,
    /// so a worktree whose work is entirely on origin/main still reports ahead - and the Git dialog
    /// REFUSES the tidy on that reading rather than warning, which alone would leave a team that did
    /// everything right with no way to close itself out through the product.
    ///
    /// <c>CommitsNotOnMain</c> is measured AGAINST <c>origin/main</c>, not local main, and that
    /// asymmetry with the two counts beside it is deliberate: this is the question "has the work
    /// LANDED", and a clone whose local main has run ahead of origin has not landed it yet.
    ///
    /// A DETACHED HEAD SENDS NULL. There is no branch for <c>cherry</c> to name, and zero would be
    /// a confident "nothing outstanding" about a tree nobody measured - which the client would then
    /// tidy away. Null is how this record says NOT MEASURED everywhere else.
    /// </summary>
    private static async Task<IReadOnlyList<WorktreeStatus>> MapWorktreesAsync(
        GitRunner gitRunner,
        IReadOnlyList<GitRunner.WorktreeInfo> gitWorktrees,
        string clonePath,
        string? originMain,
        Func<IReadOnlyCollection<string>, Task<IReadOnlySet<string>>>? openKeys,
        CancellationToken ct)
    {
        var result = new List<WorktreeStatus>();

        // A per-card tree's name carries its member and key; the key says whether its card
        // (or its workflow) is still open. Null when nobody asked, never a guess.
        static string LeafOf(string path) =>
            Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var keys = gitWorktrees
            .Select(wt => Worktrees.KeyOf(LeafOf(wt.Path)))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var open = openKeys is null ? null : await openKeys(keys);

        foreach (var wt in gitWorktrees)
        {
            var leaf = LeafOf(wt.Path);
            var key = Worktrees.KeyOf(leaf);
            string? member = Worktrees.MemberOf(leaf);

            // Try to infer member from path
            if (member is null && wt.Path.Contains("workspaces", StringComparison.OrdinalIgnoreCase))
            {
                var parts = wt.Path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (parts[i].Equals("workspaces", StringComparison.OrdinalIgnoreCase))
                    {
                        member = parts[i + 1];
                        break;
                    }
                }
            }

            var commitsNotOnMain = wt.Branch is null || originMain is null
                ? null
                : await CherryCountAsync(gitRunner, clonePath, originMain, wt.Branch, ct);

            result.Add(new WorktreeStatus(
                Path: wt.Path,
                Branch: wt.Branch,
                Sha: ShortenSha(wt.Sha),
                Member: member,
                AheadMain: wt.Ahead,
                BehindMain: wt.Behind,
                CommitsNotOnMain: commitsNotOnMain,
                Card: key,
                Open: open is null ? null : key is not null && open.Contains(key),
                SizeBytes: WorktreeRemoval.SizeOnDisk(wt.Path)));
        }

        return result;
    }

    /// <summary>
    /// Whether a line git printed is an object name rather than a message. Used to tell a
    /// <c>merge-tree</c> that merged-with-conflicts (first line is the tree it wrote) from one that
    /// declined to merge at all (first line is prose, or there is no first line) - two outcomes git
    /// reports with the same exit code and which send a person to completely different places.
    /// Length and hex only: it is asking about the SHAPE of what was printed, not whether that
    /// object exists, and a stricter test here would just be a second way to get the same answer.
    /// BOTH LENGTHS, because a repository can be sha256 and answering "that is not an object name"
    /// on one would turn every merge in it into "the merge could not be run".
    /// </summary>
    private static bool IsObjectSha(string? candidate) =>
        candidate is { Length: 40 } or { Length: 64 } && candidate.All(Uri.IsHexDigit);

    private static string? ShortenSha(string? sha)
    {
        if (sha is null) return null;
        if (sha.Length <= 7) return sha;
        return sha[..7];
    }

    /// <summary>
    /// The answer every action gives when the repository's default branch is not known: it
    /// says so and stops, 409, having changed nothing. It never guesses `main`.
    /// </summary>
    private static async Task<IResult> BranchNotKnownAsync(
        ITenantLog log, string? userId, string? email, string action, string stored, string repo, CancellationToken ct)
    {
        var error = DefaultBranchNotKnown.Message(repo);
        await log.WriteAsync(userId, email, action, $"{stored}/{repo}", null,
            JsonSerializer.Serialize(new { refused = true, reason = error }), ct);
        return Results.Conflict(new { error, defaultBranchKnown = false });
    }

    private static List<string> GetBusyMembers(TeamRegistry teams, ContainerHost host, string storedTeamId)
    {
        var busy = new List<string>();
        var teamContainers = teams.ContainerIdsOf(storedTeamId);

        foreach (var id in teamContainers)
        {
            if (host.Find(id) is { } container && container.State == ContainerState.Running)
            {
                busy.Add(id.Name);
            }
        }

        return busy;
    }
}
