namespace Yawble.Core.Teams;

/// <summary>One branch the team made, as the Git dialog shows it.</summary>
public sealed record TeamGitBranch(string Branch, string Sha, int AheadOfBase, bool InTeamBranch);

/// <summary>
/// The team repository as the Git dialog shows it (one repository).
/// <c>Merge</c>: <c>none</c> (no team branch yet), <c>up-to-date</c> (the base already has it), <c>fast-forward</c>,
/// <c>merge</c> (a merge commit, never a rebase) or <c>conflict</c> (<c>ConflictFiles</c> names them).
/// Counts against origin are as of the last fetch.
/// </summary>
public sealed record TeamGitStatus(
    string RepoPath, bool Managed, string BaseBranch, string? CheckedOut, bool Dirty, string? Origin,
    string? BaseSha, string? OriginBaseSha, int? BaseAheadOfOrigin, int? BaseBehindOrigin,
    string TeamBranch, string? TeamSha, int TeamAheadOfBase, int TeamBehindBase,
    string Merge, IReadOnlyList<string> ConflictFiles, IReadOnlyList<TeamGitBranch> Members);

/// <summary>
/// What one Git dialog action did. <c>Outcome</c>: fetched, fetch-failed, brought-current, up-to-date, fast-forward,
/// merged, conflict, refused, push-rejected, push-failed. <c>Changed</c> is false whenever nothing moved.
/// </summary>
public sealed record TeamGitResult(string Outcome, string Message, bool Changed, string? Sha = null, IReadOnlyList<string>? Files = null)
{
    public TeamGitStatus? Status { get; init; }
}

/// <summary>
/// The Git dialog's actions on a team's repository (fetch, bring current, merge to main, ask
/// the team). The person's buttons, never an agent's. Every action holds the team's lock, so it never interleaves
/// with a run's fetch-back or push.
/// <list type="bullet">
/// <item>Nothing is ever forced, rebased or stashed. A merge that would conflict is computed in the object database
///   (<c>git merge-tree --write-tree</c>), so a conflict changes nothing at all.</item>
/// <item>Merge to main fast-forwards the base branch to the team branch when it contains the base, and otherwise joins
///   them with a merge commit whose second parent is the team's own tip: what lands is what the team tested.</item>
/// <item>With an origin, the base branch is merged against origin's copy (fetched first) and pushed there before the
///   local branch moves; the person's own commits on the base branch that origin lacks are never pushed for them.</item>
/// </list>
/// </summary>
public sealed partial class TeamRepos
{
    public async Task<TeamGitStatus> GitStatusAsync(long teamId, string team, CancellationToken ct = default, string? repoName = null)
    {
        var repo = await GitRepoAsync(teamId, team, repoName, ct);
        return await WithTeamLock(teamId, () => StatusLockedAsync(repo, team, ct), ct);
    }

    /// <summary>Fetches origin (remote-tracking refs only; nothing else moves), as the person's own git.</summary>
    public async Task<TeamGitResult> GitFetchAsync(long teamId, string team, CancellationToken ct = default, string? repoName = null)
    {
        var repo = await GitRepoAsync(teamId, team, repoName, ct);
        return await WithTeamLock(teamId, async () =>
        {
            var fetched = await FetchOriginLockedAsync(repo, ct);
            var result = fetched ?? new TeamGitResult("fetched", "Fetched origin.", false);
            return result with { Status = await StatusLockedAsync(repo, team, ct) };
        }, ct);
    }

    /// <summary>Fast-forwards the local base branch to origin's (after a fetch). Never a merge, never a force.</summary>
    public async Task<TeamGitResult> GitBringBaseCurrentAsync(long teamId, string team, CancellationToken ct = default, string? repoName = null)
    {
        var repo = await GitRepoAsync(teamId, team, repoName, ct);
        return await WithTeamLock(teamId, async () =>
        {
            var result = await BringBaseCurrentLockedAsync(repo, ct);
            if (result.Changed) await TryUpdateSharedLockedAsync(team, repo, ct);
            return result with { Status = await StatusLockedAsync(repo, team, ct) };
        }, ct);
    }

    /// <summary>Merges the team branch into the base branch (and origin's base branch, when there is an origin).</summary>
    public async Task<TeamGitResult> GitMergeTeamToBaseAsync(long teamId, string team, CancellationToken ct = default, string? repoName = null)
    {
        var repo = await GitRepoAsync(teamId, team, repoName, ct);
        return await WithTeamLock(teamId, async () =>
        {
            var result = await MergeLockedAsync(repo, team, ct);
            if (result.Changed) await TryUpdateSharedLockedAsync(team, repo, ct);
            return result with { Status = await StatusLockedAsync(repo, team, ct) };
        }, ct);
    }

    /// <summary>
    /// What the Manager is told when the team branch cannot merge cleanly: bring its branch current
    /// with the base from the shared view. Refreshes the shared view first, so the bundle holds the base as it is now.
    /// </summary>
    public async Task<string> GitAskTeamTextAsync(long teamId, string team, CancellationToken ct = default, string? repoName = null)
    {
        var repo = await GitRepoAsync(teamId, team, repoName, ct);
        return await WithTeamLock(teamId, async () =>
        {
            await TryUpdateSharedLockedAsync(team, repo, ct);
            var s = await StatusLockedAsync(repo, team, ct);
            if (s.TeamSha is null) throw new InvalidOperationException("the team has no team branch yet: nothing to bring current");
            var files = s.ConflictFiles.Count > 0 ? $" These files conflict: {string.Join(", ", s.ConflictFiles)}." : "";
            var shared = SharedPathOf(team, repo);
            var inRepo = string.IsNullOrWhiteSpace(repoName) ? "" : $" in repository {repoName}";
            var bundle = string.IsNullOrWhiteSpace(repoName) ? "$env:YAWBLE_TEAM_BUNDLE" : Path.Combine(shared, SharedBundleFile);
            var clone = string.IsNullOrWhiteSpace(repoName) ? "your clone" : $"your clone of {repoName}";
            return $"The person wants to merge your branch {s.TeamBranch}{inRepo} into {s.BaseBranch}, and it does not merge cleanly.{files} "
                + $"Bring {s.TeamBranch} current with {s.BaseBranch} in {clone}: `git fetch \"{bundle}\" {s.BaseBranch}` then "
                + "`git merge --no-edit FETCH_HEAD`. Resolve each conflict so both sides' intent survives; when a conflict is in a member's "
                + "work and you cannot tell the right result, tell that member what to change on its branch and merge its branch again. "
                + "Never rebase, never reset and never push. Commit the merge, check it builds and its tests pass, then call "
                + $"`workflow_complete` with the new {s.TeamBranch} commit; the person merges it to {s.BaseBranch} from the Git dialog.";
        }, ct);
    }

    // ------------------------------------------------------------------ under the team's lock

    private async Task<T> WithTeamLock<T>(long teamId, Func<Task<T>> work, CancellationToken ct)
    {
        var gate = _teamLocks.GetOrAdd(teamId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The repository the Git dialog acts on: the team's main one (no name), or one of its writable extra repositories
    /// by name (0022), as a TeamRepo of its own folder and base branch. A read-only reference has no team branch.
    /// </summary>
    private async Task<TeamRepo> GitRepoAsync(long teamId, string team, string? repoName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoName)) return await EnsureTeamRepoAsync(teamId, team, ct);
        var extra = ExtrasOf(teamId).FirstOrDefault(e => string.Equals(e.Name, repoName.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"team {team} has no repository named {repoName}");
        if (!extra.Writable) throw new ArgumentException($"{extra.Name} is a read-only reference: the team has no branch in it to merge");
        return new TeamRepo(teamId, extra.Path, false, extra.BaseBranch);
    }

    /// <summary>The shared view of whichever repository this is (the main one's, or an extra's by its folder).</summary>
    private string SharedPathOf(string team, TeamRepo repo) =>
        ExtrasOf(repo.TeamId).FirstOrDefault(e => string.Equals(Path.GetFullPath(e.Path), Path.GetFullPath(repo.Path), PathComparison)) is { } extra
            ? ExtraSharedPath(team, extra.Name)
            : SharedRepoPath(team);

    private async Task TryUpdateSharedLockedAsync(string team, TeamRepo repo, CancellationToken ct)
    {
        try
        {
            await UpdateSharedAtAsync(SharedPathOf(team, repo), repo.Path, repo.BaseBranch, ct);
        }
        catch (GitException e)
        {
            _log?.Invoke($"team {team}: the shared view was not updated: {e.Message}");
        }
    }

    private async Task<TeamGitStatus> StatusLockedAsync(TeamRepo repo, string team, CancellationToken ct)
    {
        var path = repo.Path;
        var baseRef = "refs/heads/" + repo.BaseBranch;
        var teamBranch = TeamBranchFor(team);
        var origin = await OriginUrlAsync(path, ct);
        var baseSha = await RevAsync(path, baseRef, ct);
        var originBase = origin is null ? null : await RevAsync(path, "refs/remotes/origin/" + repo.BaseBranch, ct);
        int? ahead = null, behind = null;
        if (baseSha is not null && originBase is not null)
        {
            ahead = await CountAsync(path, baseSha, originBase, ct);
            behind = await CountAsync(path, originBase, baseSha, ct);
        }
        // Merge previews are against what Merge to main would start from: origin's base when the local one is behind it.
        var start = originBase is not null && baseSha is not null && await IsAncestorAsync(path, baseSha, originBase, ct) ? originBase : baseSha;
        var teamSha = await RevAsync(path, "refs/heads/" + teamBranch, ct);
        var teamAhead = 0;
        var teamBehind = 0;
        var merge = "none";
        IReadOnlyList<string> conflicts = [];
        if (teamSha is not null && start is not null)
        {
            teamAhead = await CountAsync(path, teamSha, start, ct);
            teamBehind = await CountAsync(path, start, teamSha, ct);
            if (teamAhead == 0) merge = "up-to-date";
            else if (teamBehind == 0) merge = "fast-forward";
            else
            {
                var (tree, files) = await MergeTreeAsync(path, start, teamSha, ct);
                merge = tree is null ? "conflict" : "merge";
                conflicts = files;
            }
        }
        var members = new List<TeamGitBranch>();
        // This team's: members/<team>/*, and old-style members/<name> ones (B016 left them for the person to delete).
        foreach (var (branch, sha) in await TeamMemberBranchesAsync(repo.TeamId, team, path, legacyInUseOnly: false, ct))
        {
            var aheadOfBase = start is null ? 0 : await CountAsync(path, sha, start, ct);
            var inTeam = teamSha is not null && await IsAncestorAsync(path, sha, teamSha, ct);
            members.Add(new TeamGitBranch(branch, sha, aheadOfBase, inTeam));
        }
        var checkedOut = await CurrentBranchAsync(path, ct);
        // The working tree is the person's: their git (and its core.autocrlf) decides what changed. The app's config-free
        // git would call every CRLF checkout on Windows "uncommitted changes".
        var dirty = (await Pusher.RunAsync(path, ["status", "--porcelain", "--untracked-files=no"], ct)).Stdout.Length > 0;
        return new TeamGitStatus(path, repo.Managed, repo.BaseBranch, checkedOut, dirty, origin is null ? null : PersonGitPush.Redact(origin),
            baseSha, originBase, ahead, behind, teamBranch, teamSha, teamAhead, teamBehind, merge, conflicts, members);
    }

    /// <summary>Null when the fetch worked (or there is no origin to fetch); otherwise the failure, with nothing changed.</summary>
    private async Task<TeamGitResult?> FetchOriginLockedAsync(TeamRepo repo, CancellationToken ct)
    {
        if (await OriginUrlAsync(repo.Path, ct) is null)
            return new TeamGitResult("refused", "This repository has no origin: there is nothing to fetch.", false);
        var run = await Pusher.RunAsync(repo.Path, ["fetch", "--prune", "--no-tags", "origin"], ct);
        if (run.ExitCode == 0) return null;
        return new TeamGitResult("fetch-failed",
            "Could not fetch origin; nothing changed. git said: " + PersonGitPush.Redact(string.Join('\n', run.Stderr, run.Stdout).Trim()), false);
    }

    private async Task<TeamGitResult> BringBaseCurrentLockedAsync(TeamRepo repo, CancellationToken ct)
    {
        if (await FetchOriginLockedAsync(repo, ct) is { } failed) return failed;
        var path = repo.Path;
        var baseRef = "refs/heads/" + repo.BaseBranch;
        var local = await RevAsync(path, baseRef, ct);
        var remote = await RevAsync(path, "refs/remotes/origin/" + repo.BaseBranch, ct);
        if (remote is null) return new TeamGitResult("refused", $"origin has no {repo.BaseBranch} branch.", false);
        if (local == remote) return new TeamGitResult("up-to-date", $"{repo.BaseBranch} is already at origin's.", false, local);
        if (local is not null && !await IsAncestorAsync(path, local, remote, ct))
        {
            var mine = await CountAsync(path, local, remote, ct);
            return new TeamGitResult("refused",
                $"Your {repo.BaseBranch} has {mine} commit(s) origin does not have, so it cannot simply move to origin's. "
                + "Push or merge them yourself; the app never rebases, resets or pushes your own commits.", false);
        }
        if (await MoveBaseAsync(repo, local, remote, ct) is { } refused) return refused;
        return new TeamGitResult("brought-current", $"{repo.BaseBranch} moved to origin's {Short(remote)}.", true, remote);
    }

    private async Task<TeamGitResult> MergeLockedAsync(TeamRepo repo, string team, CancellationToken ct)
    {
        var path = repo.Path;
        var teamBranch = TeamBranchFor(team);
        var teamSha = await RevAsync(path, "refs/heads/" + teamBranch, ct);
        if (teamSha is null) return new TeamGitResult("refused", $"There is no {teamBranch} yet: the Manager makes it when it merges accepted work.", false);
        var hasOrigin = await OriginUrlAsync(path, ct) is not null;
        if (hasOrigin && await FetchOriginLockedAsync(repo, ct) is { } failed) return failed;

        var local = await RevAsync(path, "refs/heads/" + repo.BaseBranch, ct);
        var remote = hasOrigin ? await RevAsync(path, "refs/remotes/origin/" + repo.BaseBranch, ct) : null;
        string? start;
        if (remote is null) start = local;
        else if (local is null || local == remote || await IsAncestorAsync(path, local, remote, ct)) start = remote;
        else
        {
            var mine = await CountAsync(path, local, remote, ct);
            return new TeamGitResult("refused",
                $"Your {repo.BaseBranch} has {mine} commit(s) that origin does not have. Push them (or drop them) yourself first: "
                + $"the app never pushes your own commits on {repo.BaseBranch} for you. Nothing changed.", false);
        }
        if (start is null) return new TeamGitResult("refused", $"There is no {repo.BaseBranch} branch to merge into.", false);
        if (await IsAncestorAsync(path, teamSha, start, ct))
            return new TeamGitResult("up-to-date", $"{repo.BaseBranch} already has everything on {teamBranch}.", false, start);
        if (await DirtyCheckoutAsync(repo, ct) is { } dirty) return dirty;

        string merged;
        string outcome;
        if (await IsAncestorAsync(path, start, teamSha, ct))
        {
            merged = teamSha;
            outcome = "fast-forward";
        }
        else
        {
            var (tree, files) = await MergeTreeAsync(path, start, teamSha, ct);
            if (tree is null)
            {
                return new TeamGitResult("conflict",
                    $"{teamBranch} does not merge cleanly into {repo.BaseBranch}; nothing changed. Ask the team to bring it current.", false, null, files);
            }
            var (name, email) = await PersonIdentityAsync(path, ct);
            merged = await Git.RunAsync(path, ct, "-c", "user.name=" + name, "-c", "user.email=" + email,
                "commit-tree", tree, "-p", start, "-p", teamSha, "-m", $"Merge {teamBranch} into {repo.BaseBranch}");
            outcome = "merged";
        }

        if (hasOrigin)
        {
            var push = await Pusher.PushAsync(path, repo.BaseBranch, ct, source: merged);
            if (push.ExitCode != 0)
            {
                var said = PersonGitPush.Redact(string.Join('\n', push.Stderr, push.Stdout).Trim());
                return PersonGitPush.IsRefused(said)
                    ? new TeamGitResult("push-rejected", $"origin refused the new {repo.BaseBranch} (it moved meanwhile?); nothing changed. Fetch and try again. git said: {said}", false)
                    : new TeamGitResult("push-failed", $"Could not push {repo.BaseBranch} to origin; nothing changed. git said: {said}", false);
            }
        }
        if (await MoveBaseAsync(repo, local, merged, ct) is { } notMoved)
        {
            return hasOrigin
                ? notMoved with { Outcome = outcome, Changed = true, Sha = merged, Message = $"origin's {repo.BaseBranch} is now {Short(merged)}, but your local {repo.BaseBranch} did not move: {notMoved.Message}" }
                : notMoved;
        }
        var how = outcome == "fast-forward" ? "fast-forwarded" : "merged with a merge commit";
        var where = hasOrigin ? $"{repo.BaseBranch} and origin's {repo.BaseBranch}" : repo.BaseBranch;
        _log?.Invoke($"team {team}: {teamBranch} {how} into {repo.BaseBranch} at {Short(merged)}");
        return new TeamGitResult(outcome, $"{teamBranch} {how} into {where}: now {Short(merged)}.", true, merged);
    }

    /// <summary>
    /// Moves the local base branch from <paramref name="from"/> to <paramref name="to"/> (a descendant). A checked-out
    /// base moves with <c>git merge --ff-only</c> so its files follow; any other with a compare-and-swap update-ref.
    /// Null when it moved.
    /// </summary>
    private async Task<TeamGitResult?> MoveBaseAsync(TeamRepo repo, string? from, string to, CancellationToken ct)
    {
        var path = repo.Path;
        if (from == to) return null;
        if (await CurrentBranchAsync(path, ct) == repo.BaseBranch)
        {
            if (await DirtyCheckoutAsync(repo, ct) is { } dirty) return dirty;
            // The person's git moves the person's files, with their line endings.
            var ff = await Pusher.RunAsync(path, ["merge", "--ff-only", "-q", to], ct);
            return ff.ExitCode == 0 ? null
                : new TeamGitResult("refused", $"{repo.BaseBranch} could not fast-forward in {path}: {ff.Stderr}", false);
        }
        var update = await Git.TryRunAsync(path, ["update-ref", "refs/heads/" + repo.BaseBranch, to, from ?? new string('0', to.Length)], ct);
        return update.ExitCode == 0 ? null
            : new TeamGitResult("refused", $"{repo.BaseBranch} moved while the app was working; nothing more changed: {update.Stderr}", false);
    }

    /// <summary>A refusal when the base branch is checked out with changes: the app never stashes the person's work.</summary>
    private async Task<TeamGitResult?> DirtyCheckoutAsync(TeamRepo repo, CancellationToken ct)
    {
        if (await CurrentBranchAsync(repo.Path, ct) != repo.BaseBranch) return null;
        var status = await Pusher.RunAsync(repo.Path, ["status", "--porcelain", "--untracked-files=no"], ct);
        return status.Stdout.Length == 0 ? null
            : new TeamGitResult("refused",
                $"{repo.BaseBranch} is checked out in {repo.Path} with uncommitted changes. Commit or stash them yourself first; "
                + "the app never touches uncommitted work. Nothing changed.", false);
    }

    /// <summary>The merged tree, or null with the conflicting files (git merge-tree --write-tree: no ref or file moves).</summary>
    private async Task<(string? Tree, IReadOnlyList<string> Files)> MergeTreeAsync(string path, string ours, string theirs, CancellationToken ct)
    {
        var r = await Git.TryRunAsync(path, ["merge-tree", "--write-tree", "--name-only", "--no-messages", ours, theirs], ct);
        var lines = r.Stdout.Split('\n');
        if (r.ExitCode == 0) return (lines[0].Trim(), []);
        if (r.ExitCode == 1) return (null, lines.Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToArray());
        throw new GitException(GitProgram.LooksTooOld(r.Stderr) ? GitProgram.TooOldMessage("git merge-tree") : $"git merge-tree failed ({r.ExitCode}): {r.Stderr}");
    }

    private async Task<(string Name, string Email)> PersonIdentityAsync(string path, CancellationToken ct)
    {
        var name = await Pusher.RunAsync(path, ["config", "--get", "user.name"], ct);
        var email = await Pusher.RunAsync(path, ["config", "--get", "user.email"], ct);
        return (name.ExitCode == 0 && name.Stdout.Length > 0 ? name.Stdout : ManagedIdentityName,
            email.ExitCode == 0 && email.Stdout.Length > 0 ? email.Stdout : ManagedIdentityEmail);
    }

    private async Task<string?> OriginUrlAsync(string path, CancellationToken ct)
    {
        var r = await Git.TryRunAsync(path, ["config", "--get", "remote.origin.url"], ct);
        return r.ExitCode == 0 && r.Stdout.Length > 0 ? r.Stdout : null;
    }

    private async Task<string?> RevAsync(string path, string reference, CancellationToken ct)
    {
        var r = await Git.TryRunAsync(path, ["rev-parse", "--verify", "-q", reference + "^{commit}"], ct);
        return r.ExitCode == 0 && r.Stdout.Length > 0 ? r.Stdout : null;
    }

    private async Task<bool> IsAncestorAsync(string path, string ancestor, string of, CancellationToken ct) =>
        (await Git.TryRunAsync(path, ["merge-base", "--is-ancestor", ancestor, of], ct)).ExitCode == 0;

    /// <summary>Commits on <paramref name="of"/> that <paramref name="not"/> lacks.</summary>
    private async Task<int> CountAsync(string path, string of, string not, CancellationToken ct) =>
        int.TryParse((await Git.TryRunAsync(path, ["rev-list", "--count", of, "^" + not], ct)).Stdout, out var n) ? n : 0;

    private static string Short(string sha) => sha[..Math.Min(12, sha.Length)];
}
