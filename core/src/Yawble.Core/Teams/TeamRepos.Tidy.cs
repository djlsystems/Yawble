using System.Globalization;

namespace Yawble.Core.Teams;

/// <summary>One commit as the tidy plan lists it.</summary>
public sealed record TidyCommit(string Sha, string Subject);

/// <summary>
/// One branch of the tidy plan (B067), in exactly one <see cref="Class"/> (<see cref="TidyClasses"/>). <c>Where</c>: <c>local</c> (the
/// team repository), <c>origin</c> (as of the last fetch), <c>clone:&lt;member&gt;</c>.
/// </summary>
public sealed record TidyBranch(string Name, IReadOnlyList<string> Where, string Class)
{
    /// <summary>Why it is in its class, as text: for <c>contained</c>, git cherry's answer (the proof nothing is lost).</summary>
    public string? Proof { get; init; }
    /// <summary><c>own-work</c> and <c>origin-only</c>: its commits not in the base; <c>moved</c>: the team copy's commits the clone does not have.</summary>
    public IReadOnlyList<TidyCommit>? Commits { get; init; }
    /// <summary>The team repository's copy (origin's when only origin has it).</summary>
    public string? TeamSha { get; init; }
    /// <summary>The member clone's copy (moved, working, scratch).</summary>
    public string? CloneSha { get; init; }
    /// <summary>The member whose branch (or clone) it is.</summary>
    public string? Member { get; init; }
}

public static class TidyClasses
{
    /// <summary>Every copy is in the base branch (or origin's).</summary>
    public const string Merged = "merged";
    /// <summary>Not merged, but every commit has an equivalent in the base (cherry-picked): deletes like a merged one.</summary>
    public const string Contained = "contained";
    /// <summary>A member's working branch (its clone is recorded on it, or has it checked out): in use, kept.</summary>
    public const string Working = "working";
    /// <summary>The team's copy has commits of its own the member's clone no longer has (the clone was reset or rebased): realign.</summary>
    public const string Moved = "moved";
    /// <summary>Commits of its own, not in the base: needs a look.</summary>
    public const string OwnWork = "own-work";
    /// <summary>On origin only, nobody has it here, and not in the base: needs a look.</summary>
    public const string OriginOnly = "origin-only";
    /// <summary>A member clone's local-only branch (scratch/*, tmp/*, any branch only the clone has).</summary>
    public const string Scratch = "scratch";
}

/// <summary>One repository of the tidy plan. <c>Notes</c>: checks that could not run (a clone that could not be read).</summary>
public sealed record TidyRepo(string Repo, string Path, string BaseBranch, IReadOnlyList<TidyBranch> Branches)
{
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record TeamGitTidyPlan(IReadOnlyList<TidyRepo> Repos);

/// <summary>
/// What a realign did (or, planned, would do): the member branch in the team repository moved from <c>OldSha</c> to
/// <c>NewSha</c> (the clone's), and origin's copy from <c>OriginOldSha</c> when <c>OriginUpdated</c>.
/// </summary>
public sealed record TeamGitRealign(string Repo, string Branch, string Member, string? OldSha, string NewSha, string? OriginOldSha,
    bool OriginUpdated, bool Changed, string Message)
{
    /// <summary>A plan only (planOnly): something would change, nothing did yet.</summary>
    public bool Planned { get; init; }
}

/// <summary>A refusal with a code the window shows (<c>running</c>: the member runs; <c>stale</c>: it changed since it was shown).</summary>
public sealed class TeamGitRefusal(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// B067: tidy branches. One read-only plan of every branch of a team's repositories (the team repository, origin as of the
/// last fetch, every member clone), each in one class; and the realign of a member branch whose clone was reset or
/// rebased (the one forced update the app makes, always with the person's yes, never while the member runs, its old
/// commit recorded). Clones are read as everywhere else: their branches are fetched into a scratch repository of the
/// host's own (no git ever runs in a clone).
/// </summary>
public sealed partial class TeamRepos
{
    /// <summary>The log in the team repository's .git of every realign (old commit, new commit): restore from it.</summary>
    public const string RealignLog = Concierge.ConciergeGit.DeletedLog;

    private const string MainRepoName = "main";

    /// <summary>Where a realign fetches the clone's commit first (the host's own namespace, never in the bundle; removed after).</summary>
    public const string RealignStagingPrefix = "refs/sandbox/realign/";

    /// <summary>The tidy plan of the team's main repository and every writable extra one (B067 item 1). Changes nothing.</summary>
    public async Task<TeamGitTidyPlan> GitTidyPlanAsync(long teamId, string team, CancellationToken ct = default)
    {
        var repos = new List<(string Name, TeamRepo Repo)>();
        if (RepoOf(teamId) is { } main && Directory.Exists(main.Path)) repos.Add((MainRepoName, main));
        foreach (var e in ExtrasOf(teamId).Where(e => e.Writable && Directory.Exists(e.Path)))
            repos.Add((e.Name, new TeamRepo(teamId, e.Path, false, e.BaseBranch)));
        return await WithTeamLock(teamId, async () =>
        {
            var list = new List<TidyRepo>();
            foreach (var (name, repo) in repos) list.Add(await TidyRepoLockedAsync(teamId, team, name, repo, ct));
            return new TeamGitTidyPlan(list);
        }, ct);
    }

    private sealed record CloneRefs(string Member, string Prefix, string? Head, Dictionary<string, string> Branches);

    private async Task<TidyRepo> TidyRepoLockedAsync(long teamId, string team, string name, TeamRepo repo, CancellationToken ct)
    {
        var path = repo.Path;
        var notes = new List<string>();
        var baseSha = await RevAsync(path, "refs/heads/" + repo.BaseBranch, ct);
        var originBase = await RevAsync(path, "refs/remotes/origin/" + repo.BaseBranch, ct);
        var start = originBase is not null && baseSha is not null && await IsAncestorAsync(path, baseSha, originBase, ct) ? originBase : baseSha ?? originBase;
        var local = await RefMapAsync(path, "refs/heads/", ct);
        var remote = await RefMapAsync(path, "refs/remotes/origin/", ct);
        remote.Remove("HEAD");
        var users = RepoUsers(path);
        var own = users.Where(u => u.TeamId == teamId).ToList();
        var git = new Concierge.ConciergeGit(Git, Pusher);
        var hostCheckouts = await git.CheckedOutAsync(new Concierge.ActionRepo(name, path, repo.BaseBranch, []), ct);

        var scratch = Path.Combine(Path.GetTempPath(), "yawble-tidy-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            // The clones' branches, in one scratch repository that reads the team repository's objects in place.
            var clones = new List<CloneRefs>();
            await Git.RunAsync(null, ct, "init", "-q", "--bare", scratch);
            var common = await Git.RunAsync(path, ct, "rev-parse", "--path-format=absolute", "--git-common-dir");
            await File.WriteAllTextAsync(Path.Combine(scratch, "objects", "info", "alternates"), Path.Combine(common, "objects") + "\n", ct);
            for (var i = 0; i < own.Count; i++)
            {
                var u = own[i];
                if (!Directory.Exists(u.ClonePath)) continue;
                if (CloneGitDirProblem(u.ClonePath) is { } problem) { notes.Add($"{u.Member}'s clone was not read: {problem}"); continue; }
                var gitDir = Path.Combine(u.ClonePath, ".git");
                var prefix = $"refs/clones/{i}/";
                var fetched = await Git.TryRunAsync(scratch,
                    ["fetch", "-q", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", StrictUploadPack, "--", gitDir, $"+refs/heads/*:{prefix}*"], ct);
                if (fetched.ExitCode != 0) { notes.Add($"{u.Member}'s clone was not read: {fetched.Stderr}"); continue; }
                clones.Add(new CloneRefs(u.Member, prefix, Concierge.CloneHead.Read(gitDir)?.Branch, await RefMapAsync(scratch, prefix, ct)));
            }

            bool Skipped(string b) => string.Equals(b, repo.BaseBranch, StringComparison.OrdinalIgnoreCase) || NeverPublished.Contains(b);
            var names = local.Keys.Concat(remote.Keys).Concat(clones.SelectMany(c => c.Branches.Keys)).Where(b => !Skipped(b))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var branches = new List<TidyBranch>();
            foreach (var b in names)
            {
                var l = local.GetValueOrDefault(b);
                var r = remote.GetValueOrDefault(b);
                var inClones = clones.Where(c => c.Branches.ContainsKey(b)).ToList();
                var where = new List<string>();
                if (l is not null) where.Add("local");
                if (r is not null) where.Add("origin");
                where.AddRange(inClones.Select(c => "clone:" + c.Member));
                var holder = users.FirstOrDefault(u => string.Equals(u.Branch, b, StringComparison.OrdinalIgnoreCase));
                var checkedOutIn = inClones.FirstOrDefault(c => string.Equals(c.Head, b, StringComparison.OrdinalIgnoreCase));
                var teamCopy = l ?? r;

                if (teamCopy is null)
                {
                    // Only in clones: a member's own branch with nothing brought back yet, or a clone's local-only branch.
                    var c0 = inClones[0];
                    branches.Add(holder is not null || checkedOutIn is not null
                        ? new TidyBranch(b, where, TidyClasses.Working)
                        {
                            Member = holder?.Member ?? checkedOutIn!.Member, CloneSha = (checkedOutIn ?? c0).Branches[b],
                            Proof = $"{holder?.Member ?? checkedOutIn!.Member}'s working branch; nothing of it in the team repository yet",
                        }
                        : new TidyBranch(b, where, TidyClasses.Scratch)
                        {
                            Member = c0.Member, CloneSha = c0.Branches[b],
                            Proof = $"only in {string.Join(", ", inClones.Select(c => c.Member + "'s clone"))}; not in the team repository",
                        });
                    continue;
                }

                if (holder is not null)
                {
                    var clone = clones.FirstOrDefault(c => holder.TeamId == teamId && string.Equals(c.Member, holder.Member, StringComparison.Ordinal));
                    var cloneSha = clone?.Branches.GetValueOrDefault(holder.Branch);
                    if (cloneSha is not null)
                    {
                        // Moved: a copy has commits of its own (not merges: a sync's merge is not) that neither the clone nor
                        // the base has. A clone that is only ahead (work not fetched back yet) or behind a sync is not moved.
                        var dropped = new List<TidyCommit>();
                        string? movedCopy = null;
                        foreach (var copy in new[] { l, r }.OfType<string>().Distinct())
                            foreach (var c in await CommitsAsync(scratch, copy, [cloneSha, baseSha, originBase], ct))
                            {
                                movedCopy ??= copy;
                                if (dropped.All(d => d.Sha != c.Sha)) dropped.Add(c);
                            }
                        if (movedCopy is not null)
                        {
                            // Whether realigning loses anything: the copy's changes may all be in the base already (cherry-picked).
                            var inBase = baseSha is null ? null : await git.ContainedProofAsync(path, repo.BaseBranch, baseSha, movedCopy, ct);
                            branches.Add(new TidyBranch(b, where, TidyClasses.Moved)
                            {
                                Member = holder.Member, TeamSha = movedCopy, CloneSha = cloneSha, Commits = dropped,
                                Proof = $"the team's copy ({Short(teamCopy)}{(l is not null && r is not null && l != r ? $", origin's {Short(r)}" : "")}) has {dropped.Count} commit(s) "
                                        + $"{holder.Member}'s clone ({Short(cloneSha)}) does not have and that are not merged into {repo.BaseBranch}: the clone was reset or rebased"
                                        + (inBase is null ? "" : $"; nothing is lost by realigning it ({inBase})"),
                            });
                            continue;
                        }
                    }
                    var whose = holder.TeamId == teamId ? holder.Member : $"{holder.Member} (team {holder.Team})";
                    branches.Add(new TidyBranch(b, where, TidyClasses.Working)
                    {
                        Member = holder.Member, TeamSha = teamCopy, CloneSha = cloneSha,
                        Proof = $"{whose}'s working branch{(checkedOutIn is not null ? ", checked out in its clone" : "")}: in use, kept",
                    });
                    continue;
                }
                if (checkedOutIn is not null || hostCheckouts.ContainsKey(b))
                {
                    branches.Add(new TidyBranch(b, where, TidyClasses.Working)
                    {
                        Member = checkedOutIn?.Member, TeamSha = teamCopy, CloneSha = checkedOutIn?.Branches[b],
                        Proof = checkedOutIn is not null ? $"checked out in {checkedOutIn.Member}'s clone: in use, kept" : $"checked out in {hostCheckouts[b]}: in use, kept",
                    });
                    continue;
                }

                // Nobody's: merged, contained in the base (with git cherry's proof), or work of its own.
                var proofs = new List<string>();
                var unmerged = new List<string>();
                foreach (var copy in new[] { l, r }.OfType<string>().Distinct())
                {
                    if (baseSha is not null && await IsAncestorAsync(path, copy, baseSha, ct) || originBase is not null && await IsAncestorAsync(path, copy, originBase, ct)) continue;
                    var proof = baseSha is null ? null : await git.ContainedProofAsync(path, repo.BaseBranch, baseSha, copy, ct);
                    if (proof is null && originBase is not null && originBase != baseSha)
                        proof = await git.ContainedProofAsync(path, "origin/" + repo.BaseBranch, originBase, copy, ct);
                    if (proof is null) unmerged.Add(copy);
                    else proofs.Add(proof);
                }
                if (baseSha is null && originBase is null)
                    branches.Add(new TidyBranch(b, where, TidyClasses.OwnWork) { TeamSha = teamCopy, Proof = $"no {repo.BaseBranch} to compare with" });
                else if (unmerged.Count == 0 && proofs.Count == 0)
                    branches.Add(new TidyBranch(b, where, TidyClasses.Merged) { TeamSha = teamCopy, Proof = $"in {repo.BaseBranch}" });
                else if (unmerged.Count == 0)
                    branches.Add(new TidyBranch(b, where, TidyClasses.Contained) { TeamSha = teamCopy, Proof = string.Join(" | ", proofs) });
                else
                {
                    var commits = await CommitsAsync(path, unmerged[0], [start], ct);
                    branches.Add(new TidyBranch(b, where, l is null ? TidyClasses.OriginOnly : TidyClasses.OwnWork)
                    {
                        TeamSha = teamCopy, Commits = commits,
                        Proof = (l is null ? "only on origin, nobody has it here; " : "") + $"{commits.Count}{(commits.Count >= MaxTidyCommits ? "+" : "")} commit(s) not in {repo.BaseBranch}: needs a look",
                    });
                }
            }
            return new TidyRepo(name, path, repo.BaseBranch, branches) { Notes = notes };
        }
        finally
        {
            DeleteScratchRepo(scratch);
        }
    }

    /// <summary>At most this many commits are listed for one branch of the tidy plan.</summary>
    public const int MaxTidyCommits = 50;

    /// <summary>The non-merge commits of <paramref name="tip"/> none of <paramref name="not"/> has, newest first (at most <see cref="MaxTidyCommits"/>).</summary>
    private async Task<IReadOnlyList<TidyCommit>> CommitsAsync(string path, string tip, IEnumerable<string?> not, CancellationToken ct)
    {
        var r = await Git.TryRunAsync(path, ["log", "--no-merges", "--format=%H%x09%s", "--max-count=" + MaxTidyCommits, tip, .. not.OfType<string>().Select(n => "^" + n), "--"], ct);
        if (r.ExitCode != 0) return [];
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t', 2))
            .Select(p => new TidyCommit(p[0].Trim(), p.Length > 1 ? p[1] : "")).ToList();
    }

    private async Task<Dictionary<string, string>> RefMapAsync(string path, string prefix, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var r = await Git.TryRunAsync(path, ["for-each-ref", "--format=%(refname) %(objectname)", prefix], ct);
        if (r.ExitCode != 0) return map;
        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', 2);
            if (parts.Length == 2 && parts[0].StartsWith(prefix, StringComparison.Ordinal)) map[parts[0][prefix.Length..]] = parts[1];
        }
        return map;
    }

    private static void DeleteScratchRepo(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var f in Ops.FolderWalk.Files(dir)) f.Attributes = FileAttributes.Normal; // git's objects are read-only
            Directory.Delete(dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a temp folder; the OS cleans it */ }
    }

    // ------------------------------------------------------------------ realign (B067 item 3)

    /// <summary>
    /// Sets the team repository's (and origin's) copy of <paramref name="member"/>'s branch to what the member's clone has: the
    /// one forced update the app makes. The caller has the person's yes. With <paramref name="planOnly"/> nothing changes
    /// and the answer says what would. Refused (<see cref="TeamGitRefusal"/>): while the member runs (<paramref name="running"/>
    /// says why), when its clone cannot be read or has no such branch, when the branch is checked out in the team repository,
    /// when the clone's new commits carry AI attribution, and (<c>stale</c>) when the old or new commit is not the one the person
    /// was shown (<paramref name="expectOld"/>, <paramref name="expectNew"/>). Origin's copy moves first, with
    /// --force-with-lease on the commit last fetched (so a copy that moved on origin since is never overwritten), then the team
    /// repository's with a compare-and-swap; a failed push changes nothing. The old commit is recorded in the repository's
    /// .git/<see cref="RealignLog"/> and in the answer, with the command that restores it.
    /// </summary>
    public async Task<TeamGitRealign> GitBranchRealignAsync(long teamId, string team, string? repoName, string member,
        Func<CancellationToken, Task<string?>>? running, bool planOnly = false, string? expectOld = null, string? expectNew = null, CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(repoName) || (repoName.Trim().Equals(MainRepoName, StringComparison.OrdinalIgnoreCase)
            && !ExtrasOf(teamId).Any(e => e.Name.Equals(MainRepoName, StringComparison.OrdinalIgnoreCase))) ? null : repoName.Trim();
        var repo = await GitRepoAsync(teamId, team, name, ct);
        var shown = name ?? MainRepoName;
        return await WithTeamLock(teamId, async () =>
        {
            var path = repo.Path;
            var user = RepoUsers(path).FirstOrDefault(u => u.TeamId == teamId && string.Equals(u.Member, member.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? throw new TeamGitRefusal("no_such_member", $"{team} has no member {member} with a clone of {shown}");
            var branch = user.Branch;
            if (string.Equals(branch, repo.BaseBranch, StringComparison.OrdinalIgnoreCase) || NeverPublished.Contains(branch))
                throw new TeamGitRefusal("protected", $"{branch} is the base branch: it is never realigned");
            if (running is not null && await running(ct) is { } why)
                throw new TeamGitRefusal("running", $"{user.Member} is running ({why}), so its clone may still move: realign after the run ends. Nothing changed.");
            if (CloneGitDirProblem(user.ClonePath) is { } problem) throw new TeamGitRefusal("clone", $"{user.Member}'s clone cannot be read: {problem}. Nothing changed.");
            var gitDir = Path.Combine(user.ClonePath, ".git");
            var listed = await Git.TryRunAsync(path, ["ls-remote", StrictUploadPack, "--", gitDir, "refs/heads/" + branch], ct);
            if (listed.ExitCode != 0) throw new TeamGitRefusal("clone", $"{user.Member}'s clone cannot be read: {listed.Stderr}. Nothing changed.");
            var cloneSha = listed.Stdout.Split('\t', 2)[0].Trim();
            if (cloneSha.Length == 0) throw new TeamGitRefusal("no_clone_branch", $"{user.Member}'s clone has no branch {branch}. Nothing changed.");
            var old = await RevAsync(path, "refs/heads/" + branch, ct);
            var hasOrigin = await OriginUrlAsync(path, ct) is not null;
            var originOld = hasOrigin ? await RevAsync(path, "refs/remotes/origin/" + branch, ct) : null;
            if (expectOld is not null && !string.Equals(expectOld, old ?? "", StringComparison.OrdinalIgnoreCase)
                || expectNew is not null && !string.Equals(expectNew, cloneSha, StringComparison.OrdinalIgnoreCase))
                throw new TeamGitRefusal("stale", $"{branch} changed since it was shown (now: team {Short(old ?? "none")}, clone {Short(cloneSha)}). Nothing changed; look at the new plan.");
            if (await CheckedOutAnywhereAsync(path, branch, ct) is { } where)
                throw new TeamGitRefusal("checked_out", $"{branch} is checked out in {where}: its files would not follow, so it is left alone. Nothing changed.");
            var push = hasOrigin && originOld != cloneSha && PushesToOrigin(teamId);
            var originNote = !hasOrigin ? "" : originOld == cloneSha ? " Origin's copy is already there."
                : !PushesToOrigin(teamId) ? " Origin's copy is not changed: pushing to origin is off for this team."
                : $" Origin's copy: {(originOld is null ? "none" : Short(originOld))} -> {Short(cloneSha)} (--force-with-lease).";
            if (old == cloneSha && !push)
                return new TeamGitRealign(shown, branch, user.Member, old, cloneSha, originOld, false, false,
                    $"{branch} is already at {user.Member}'s clone ({Short(cloneSha)}).{originNote}");
            var plan = $"{branch} in {shown}: {(old is null ? "none" : Short(old))} -> {Short(cloneSha)} ({user.Member}'s clone).{originNote}";
            if (planOnly) return new TeamGitRealign(shown, branch, user.Member, old, cloneSha, originOld, false, false, plan) { Planned = true };

            var staging = RealignStagingPrefix + branch;
            try
            {
                var fetched = await Git.TryRunAsync(path,
                    ["fetch", "-q", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", StrictUploadPack, "--", gitDir, $"+refs/heads/{branch}:{staging}"], ct);
                if (fetched.ExitCode != 0) throw new TeamGitRefusal("clone", $"could not fetch {user.Member}'s {branch}: {fetched.Stderr}. Nothing changed.");
                // As at every fetch-back: no AI attribution in commits no branch has yet (refused, never stripped).
                var newCommits = await Git.RunAsync(path, ct, "log", CommitAttribution.LogFormat, cloneSha, "--not", "--branches", "--");
                if (CommitAttribution.ParseLog(newCommits) is { Count: > 0 } attributed)
                    throw new TeamGitRefusal("attribution", CommitAttribution.RefusalText(user.Member, branch, attributed) + " Nothing changed.");
                if (push)
                {
                    var run = await Pusher.RunAsync(path, ["push", "--porcelain", "--no-verify", $"--force-with-lease=refs/heads/{branch}:{originOld ?? ""}",
                        "origin", $"{cloneSha}:refs/heads/{branch}"], ct);
                    if (run.ExitCode != 0)
                    {
                        var said = PersonGitPush.Redact(string.Join('\n', run.Stderr, run.Stdout).Trim());
                        throw new TeamGitRefusal(PersonGitPush.IsRefused(said) ? "push_rejected" : "push_failed",
                            $"origin's {branch} was not changed (it moved on origin since the last fetch, or the push failed); nothing changed. git said: {said}");
                    }
                }
                var stamp = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
                var git = new Concierge.ConciergeGit(Git, Pusher);
                var record = $"{stamp} realigned {branch} {old ?? "none"} -> {cloneSha} ({user.Member}'s clone)" + (push ? $"; origin {originOld ?? "none"} -> {cloneSha}" : "") + "\n";
                var moved = await Git.TryRunAsync(path, ["update-ref", "-m", $"realign to {user.Member}'s clone", "refs/heads/" + branch, cloneSha, old ?? new string('0', cloneSha.Length)], ct);
                if (moved.ExitCode != 0)
                {
                    await git.AppendLogAsync(path, record.TrimEnd() + " (team repository NOT moved: it moved meanwhile)\n", ct);
                    throw new TeamGitRefusal("moved", (push ? $"origin's {branch} is now {Short(cloneSha)} (was {originOld ?? "none"}), but " : "")
                        + $"the team repository's {branch} moved while the app was working and was left as it is: {moved.Stderr}");
                }
                await git.AppendLogAsync(path, record, ct);
                _log?.Invoke($"team {team}: {record.TrimEnd()}");
                await TryUpdateSharedLockedAsync(team, repo, ct);
                var restore = (old is null ? $"git branch -D {branch}" : $"git update-ref refs/heads/{branch} {old}")
                              + (push ? originOld is null ? $" and git push origin --delete {branch}" : $" and git push --force-with-lease origin {originOld}:refs/heads/{branch}" : "");
                return new TeamGitRealign(shown, branch, user.Member, old, cloneSha, originOld, push, true,
                    $"realigned {branch} in {shown} to {user.Member}'s clone: {old ?? "none"} -> {cloneSha}."
                    + (push ? $" Origin's copy: {originOld ?? "none"} -> {cloneSha}." : originNote)
                    + $" The old commit is recorded in .git/{RealignLog}; restore with: {restore}");
            }
            finally
            {
                await Git.TryRunAsync(path, ["update-ref", "-d", staging], CancellationToken.None);
            }
        }, ct);
    }
}
