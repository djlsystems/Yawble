using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// BRING CURRENT AND MERGE: the team branch is pushed, but origin's default branch has moved on
/// since. Origin's default branch is merged INTO the team branch in the team's clone (a merge
/// commit whose first parent is the team's own sha - never a rebase of pushed history, never
/// forced), the team branch is pushed, and then Merge to main runs exactly as its own button does.
/// Git only: it runs no tests.
/// </summary>
public static partial class RepoEndpoints
{
    private static void MapBringCurrentAndMerge(WebApplication app)
    {
        app.MapPost("/api/teams/{team}/repos/{repo}/bring-current-and-merge", BringCurrentAndMergeAsync)
            .WithName("BringCurrentAndMerge")
            .WithTags("Repos")
            .HumansOrConcierge(Permits.Merge)
            .WithSummary("Merge origin/main into the team branch, push it, then merge it to main")
            .WithDescription(
                "Fetches, then merges origin/main into team/{id} in the team's clone with a merge commit "
                + "(the team's commit is the first parent; nothing is rebased and nothing is forced), "
                + "pushes team/{id}, and then merges it to main exactly as Merge to main does. A merge "
                + "that conflicts changes nothing and pushes nothing: the merge is computed in the "
                + "object database, and the answer (409) names every conflicting file for the team or "
                + "a person to resolve on the team branch. Refused before anything is touched while a "
                + "member is working, while the clone has uncommitted changes or a detached HEAD, and "
                + "in contributor mode (409). It runs no tests." + DefaultBranchNote + ConciergeMergeNote);
    }

    private static async Task<IResult> BringCurrentAndMergeAsync(
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
        IUserStore users,
        ConciergeMergeGate mergeGate,
        CancellationToken ct)
    {
        // THE SAME QUESTION MERGE TO MAIN ASKS, asked here first: the team branch is pushed below,
        // before Merge to main runs, so a caller it would refuse must not get that far.
        var caller = await mergeGate.CheckAsync(httpContext, users, ct);
        if (caller.Refusal is { } refused) return refused;
        log = ConciergeMergeGate.For(log, caller);

        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        var email = caller.ActorEmail;
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        const string action = TenantActions.RepoBringCurrentAndMerge;

        // EVERY REFUSAL MERGE TO MAIN MAKES IS MADE HERE FIRST, before the team branch moves: a
        // refusal after the push would leave the team branch changed by a button that said no.
        var busyMembers = GetBusyMembers(teams, host, stored);
        if (busyMembers.Count > 0)
        {
            await log.WriteAsync(userId, email, action, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = $"{string.Join(", ", busyMembers)} still working." }), ct);
            return Results.Conflict(new { error = $"{string.Join(", ", busyMembers)} still working.", busy = busyMembers });
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

        if (teams.ContributorFor(stored, name).ContributorMode)
        {
            var contributing = $"{name} is in contributor mode: its origin is a fork, so the work goes upstream "
                + "with Open pull request, not Merge to main. Nothing was changed.";
            await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
                JsonSerializer.Serialize(new { refused = true, reason = contributing }), ct);
            return Results.Conflict(new { error = contributing, contributorMode = true });
        }

        if (teams.DefaultBranchFor(stored, name).Branch is not { } branch)
        {
            return await BranchNotKnownAsync(log, userId, email, action, stored, name, ct);
        }

        var status = await gitRunner.StatusAsync(clonePath, branch, $"refs/heads/team/{stored}", ct);
        if (status.Dirty)
        {
            var porcelain = await gitRunner.RunGitAsync(clonePath, ["status", "--porcelain"], ct);
            var files = BoundLines(porcelain.Stdout);
            const string dirty = "Clone has uncommitted changes; refusing to merge.";
            await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
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
            await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
                JsonSerializer.Serialize(new { refused = true, reason = detached }), ct);
            return Results.BadRequest(new { error = detached });
        }

        var fetchResult = await gitRunner.FetchAsync(clonePath, ct);
        if (fetchResult.ExitCode != 0)
        {
            var fetchDetail = BoundLines(fetchResult.Stderr);
            await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
                JsonSerializer.Serialize(new { success = false, stderr = fetchDetail }), ct);
            return Results.BadRequest(new { error = "Fetch failed.", detail = fetchDetail });
        }

        if (await DefaultBranchRecorder.RecordFromOriginAsync(teams, gitRunner, stored, name, clonePath, ct) != branch)
        {
            const string moved = "The repository's default branch changed when origin was fetched, so nothing was merged. Try again.";
            await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
                JsonSerializer.Serialize(new { refused = true, reason = moved }), ct);
            return Results.Conflict(new { error = moved });
        }

        var originBranch = $"refs/remotes/origin/{branch}";
        var teamBranch = $"team/{stored}";
        var localTeam = $"refs/heads/{teamBranch}";
        var remoteTeam = $"refs/remotes/origin/{teamBranch}";

        async Task<string?> ShaOfAsync(string reference)
        {
            var run = await gitRunner.RunGitAsync(clonePath, ["rev-parse", "--verify", "--quiet", reference], ct);
            return run.ExitCode == 0 ? run.Stdout.Trim() : null;
        }

        var originMain = await ShaOfAsync(originBranch);
        if (originMain is null)
        {
            var noOrigin = $"origin/{branch} does not exist.";
            await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
                JsonSerializer.Serialize(new { success = false, reason = noOrigin }), ct);
            return Results.BadRequest(new { error = noOrigin });
        }

        // The team ref Merge to main would use: the local branch when there is one, else origin's.
        var localSha = await ShaOfAsync(localTeam);
        var remoteSha = await ShaOfAsync(remoteTeam);
        var teamSha = localSha ?? remoteSha;
        if (teamSha is null)
        {
            var missing = $"Team branch {teamBranch} does not exist locally or on origin.";
            await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
                JsonSerializer.Serialize(new { success = false, reason = missing }), ct);
            return Results.BadRequest(new { error = missing });
        }

        // NEVER FORCED. The push below is a fast-forward of origin/team/{id} or nothing, so a local
        // branch that lacks commits origin's copy holds is refused here, before anything is written.
        if (localSha is not null && remoteSha is not null
            && (await gitRunner.RunGitAsync(clonePath, ["merge-base", "--is-ancestor", remoteTeam, localTeam], ct)).ExitCode != 0)
        {
            var diverged = $"origin/{teamBranch} has commits this clone does not have, so pushing would discard them. Nothing was changed.";
            await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
                JsonSerializer.Serialize(new { refused = true, reason = diverged }), ct);
            return Results.Conflict(new { error = diverged });
        }

        // MERGED IN THE OBJECT DATABASE, as Merge to main merges: HEAD, the index and the working
        // tree are not touched, so a conflict leaves the clone exactly as it was, with nothing to abort.
        string tip = teamSha;
        string? mergeCommit = null;
        var behind = await gitRunner.CountCommitsNotInAsync(clonePath, originBranch, teamSha, ct);
        if (behind is not 0)
        {
            var mergeTree = await gitRunner.MergeTreeAsync(clonePath, teamSha, originBranch, ct);
            var firstLine = mergeTree.Stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim();
            var mergedTree = IsObjectSha(firstLine) ? firstLine : null;
            var mergeOutput = BoundLines(mergeTree.Stdout + mergeTree.Stderr);

            if (mergedTree is null)
            {
                await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
                    JsonSerializer.Serialize(new { success = false, reason = "could not run", detail = mergeOutput }), ct);
                return Results.BadRequest(new { error = "The merge could not be run.", detail = mergeOutput });
            }

            if (mergeTree.ExitCode != 0)
            {
                var conflicts = BringCurrentAsk.ConflictedPaths(mergeTree.Stdout);
                var conflicted =
                    $"Merging origin/{branch} into {teamBranch} conflicts in {conflicts.Count} file{(conflicts.Count == 1 ? "" : "s")}, "
                    + "so nothing was pushed and the clone was not changed.";
                var resolve =
                    $"The team or a person must resolve {(conflicts.Count == 1 ? "it" : "them")} on {teamBranch}: "
                    + (conflicts.Count == 0 ? "git did not name the files." : string.Join(", ", conflicts) + ".");
                await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
                    JsonSerializer.Serialize(new { success = false, conflicted = true, reason = conflicted, conflicts }), ct);
                return Results.Conflict(new { error = conflicted, detail = resolve, conflicts });
            }

            var commitTree = await gitRunner.CommitTreeAsync(
                clonePath,
                mergedTree,
                teamSha,
                originMain,
                $"Merge origin/{branch} into {teamBranch}",
                $"Brought current from the Git dialog: {teamBranch} was behind origin/{branch}, so origin/{branch} "
                + "is merged into it here rather than the team's commits being replayed onto a new base.",
                ct);
            mergeCommit = commitTree.Stdout.Trim();
            if (commitTree.ExitCode != 0 || !IsObjectSha(mergeCommit))
            {
                var commitOutput = BoundLines(commitTree.Stdout + commitTree.Stderr);
                await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
                    JsonSerializer.Serialize(new { success = false, reason = "commit-tree failed", detail = commitOutput }), ct);
                return Results.BadRequest(new
                {
                    error = "The merge succeeded but the merge commit could not be written, so nothing was changed.",
                    detail = commitOutput,
                });
            }

            tip = mergeCommit;
        }

        // THE PUSH, THEN THE ROW THAT RECORDS IT - the ordering `repo.pushed` keeps.
        if (!string.Equals(tip, remoteSha, StringComparison.Ordinal))
        {
            var push = await gitRunner.PushRefspecAsync(clonePath, tip, localTeam, ct);
            if (push.ExitCode != 0)
            {
                var pushDetail = BoundLines(push.Stderr);
                var rejected = $"The push of {teamBranch} to origin was rejected, so nothing was changed on origin.";
                await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
                    JsonSerializer.Serialize(new { success = false, reason = rejected, stderr = pushDetail }), ct);
                return Results.BadRequest(new { error = rejected, detail = pushDetail });
            }
        }

        await log.WriteAsync(userId, email, action, $"{stored}/{name}", null,
            JsonSerializer.Serialize(new { success = true, pushed = teamBranch, mergeCommit, behind }), ct);

        // THE LOCAL TEAM BRANCH FOLLOWS, fast-forward only: merged into when it is checked out, moved
        // by name when it is not. A branch checked out in a member's tree refuses the move; origin
        // already has the commit, so that is said and nothing is undone.
        var localNote = string.Empty;
        if (localSha is not null && mergeCommit is not null)
        {
            var follow = string.Equals(status.HeadCheckout, teamBranch, StringComparison.Ordinal)
                ? await gitRunner.MergeFastForwardAsync(clonePath, mergeCommit, ct)
                : await gitRunner.FetchRefspecAsync(clonePath, mergeCommit, localTeam, ct);
            if (follow.ExitCode != 0)
            {
                localNote = $" The clone's own {teamBranch} could not be moved to it and still reads {ShortenSha(localSha)}.";
            }
        }

        var broughtCurrent = mergeCommit is null
            ? $"{teamBranch} already had everything on origin/{branch}."
            : $"origin/{branch} merged into {teamBranch} ({ShortenSha(mergeCommit)}) and pushed.{localNote}";

        // THEN MERGE TO MAIN, THE SAME HANDLER ITS OWN BUTTON CALLS - every check it makes, and its own row.
        var merged = await MergeToMainAsync(
            team, repo, httpContext, teams, gitRunner, paths, host, log, landedRecorder, landedCache, users, mergeGate, ct);
        if (merged is IValueHttpResult { Value: RepoActionResult done })
        {
            return Results.Ok(done with { Message = $"{broughtCurrent} {done.Message}" });
        }

        var code = merged is IStatusCodeHttpResult { StatusCode: { } c } ? c : StatusCodes.Status400BadRequest;
        var value = merged is IValueHttpResult { Value: { } v } ? JsonSerializer.SerializeToElement(v) : default;
        string? Field(string field) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;

        return Results.Json(
            new
            {
                error = $"{broughtCurrent} Merge to {branch} did not complete: {Field("error") ?? "it was refused."}",
                detail = Field("detail"),
                pushed = true,
            },
            statusCode: code);
    }
}
