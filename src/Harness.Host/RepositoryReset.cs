using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>One team repository as a reset sees it: its clone, its STORED default branch (null is
/// not known, never `main`), and whether origin is a contributor-mode fork.</summary>
public sealed record RepoResetTarget(string Repo, string ClonePath, string? DefaultBranch, bool ContributorMode);

/// <summary>A worktree or branch a repository reset names: the repository, the tree's path or the
/// branch's name, the member it belongs to (null for the team branch), and why it was kept or what
/// was done to it.</summary>
public sealed record RepoResetItem(string Repo, string Name, string? Member = null, string? Reason = null);

/// <summary>What a repository reset will try, read before anything moves: every tree and branch it
/// may remove, and the team branch when every member is ticked. What the person was shown as lost,
/// and what the <c>team.reset-repositories</c> row names before the act.</summary>
public sealed record RepositoryResetPlan(
    IReadOnlyList<RepoResetItem> Worktrees,
    IReadOnlyList<RepoResetItem> Branches,

    /// <summary><c>team/&lt;id&gt;</c> when every member is ticked, else null: the team branch is
    /// reset only when nobody's work is left on the team.</summary>
    string? TeamBranch);

/// <summary>What Reset repositories would remove, for every member of the team: the dialog shows
/// the ticked members' lines, and <see cref="TeamBranch"/> when every member is ticked.
/// <see cref="DefaultBranchNotKnown"/> names each repository whose default branch is not known, for
/// which resetting with every member ticked is refused.</summary>
public sealed record RepositoryResetPreview(
    IReadOnlyList<RepoResetItem> Worktrees,
    IReadOnlyList<RepoResetItem> Branches,
    string TeamBranch,
    IReadOnlyList<string> DefaultBranchNotKnown);

/// <summary>What a repository reset did: each tree and branch removed, each one kept with the
/// reason, and what happened to the team branch.</summary>
public sealed record RepositoriesReset(
    IReadOnlyList<RepoResetItem> WorktreesRemoved,
    IReadOnlyList<RepoResetItem> WorktreesKept,
    IReadOnlyList<RepoResetItem> BranchesDeleted,
    IReadOnlyList<RepoResetItem> BranchesKept,
    IReadOnlyList<RepoResetItem> TeamBranchReset,
    IReadOnlyList<RepoResetItem> TeamBranchKept);

/// <summary>A reset of the team branch was asked for while a repository's default branch is not
/// known. The whole reset is refused with the existing sentence and nothing changes.</summary>
public sealed class ResetDefaultBranchNotKnownException(string repo)
    : InvalidOperationException(DefaultBranchNotKnown.Message(repo))
{
    public string Repo { get; } = repo;
}

/// <summary>The <c>team.reset-repositories</c> row could not be written, so nothing was reset.</summary>
public sealed class ResetNotRecordedException(string team, Exception inner)
    : InvalidOperationException(
        $"'{team}' was not reset: its tenant log row could not be written ({inner.Message}).", inner);

/// <summary>
/// RESET REPOSITORIES: a ticked member's worktrees and branches, and with every member ticked the
/// team branch, go back to where a new team starts - WITHOUT LOSING ANYTHING ONLY A PERSON COULD
/// DECIDE TO LOSE.
///
/// <para>
/// <b>TREES GO THROUGH <see cref="WorktreeRemoval"/></b>, exactly as a member deletion's do:
/// <c>git worktree remove</c> without <c>--force</c>, then <c>git worktree prune</c>. A tree with
/// uncommitted edits or commits on no remote stays, named in the result, the tenant row and the
/// team feed (<c>repo.worktreeLeft</c>).
/// </para>
///
/// <para>
/// <b>A BRANCH IS DELETED ONLY WHEN EVERY COMMIT ON IT IS ON A REMOTE</b> (merged, pushed, or
/// empty), with <c>git update-ref -d</c> against the sha it was measured at. One holding commits on
/// no remote, or still checked out in a tree that was kept, stays and is named the same way
/// (<c>repo.branchKept</c>).
/// </para>
///
/// <para>
/// <b>THE TEAM BRANCH</b>, only with every member ticked, is moved to the STORED default branch in
/// the clone - never assumed: not known refuses the whole reset in <see cref="PlanAsync"/> - and
/// only when it holds no commit that is on no remote and not on the default branch. On origin it
/// is deleted only when it holds nothing <c>origin/&lt;default&gt;</c> lacks, compared after fetching
/// both and deleted with a lease on the sha compared; otherwise it stays, named. A contributor-mode
/// origin is the fork, and is never touched.
/// </para>
///
/// <para>
/// <b>THE DEFAULT BRANCH IS NEVER WRITTEN</b>, on the clone or on origin: no member branch this
/// class deletes can be it (<see cref="Writable"/>), the team-branch step writes only
/// <c>team/&lt;id&gt;</c>, and nothing here pushes anything but that branch's delete.
/// </para>
/// </summary>
public sealed class RepositoryReset(GitRunner git, WorktreeRemoval worktrees, TeamPaths paths, IMessageLog log)
{
    /// <summary>
    /// What a reset of <paramref name="members"/> would try. Read-only. Throws
    /// <see cref="ResetDefaultBranchNotKnownException"/> when <paramref name="everyMember"/> and a
    /// cloned repository's default branch is not known: that step is refused, so the whole reset is.
    /// </summary>
    public async Task<RepositoryResetPlan> PlanAsync(
        string team, IReadOnlyList<RepoResetTarget> repos, IReadOnlyList<string> members, bool everyMember,
        CancellationToken ct)
    {
        var cloned = repos.Where(repo => Directory.Exists(repo.ClonePath)).ToList();

        if (everyMember && cloned.FirstOrDefault(repo => repo.DefaultBranch is null) is { } unknown)
        {
            throw new ResetDefaultBranchNotKnownException(unknown.Repo);
        }

        var trees = new List<RepoResetItem>();
        var branches = new List<RepoResetItem>();

        foreach (var repo in cloned)
        {
            var checkedOut = await CheckedOutAsync(repo.ClonePath, ct);
            var local = await git.LocalBranchesAsync(repo.ClonePath, ct);

            foreach (var member in members)
            {
                var mine = TreesOf(team, member, repo.Repo);
                trees.AddRange(mine.Select(tree => new RepoResetItem(repo.Repo, tree, member)));

                // The member's branches: its own `<member>/…` namespace, and - only with the default
                // branch known, so it can never be one of them - whatever its trees have checked out.
                var named = local.Where(branch => branch.StartsWith(member + "/", StringComparison.OrdinalIgnoreCase));
                var inTrees = repo.DefaultBranch is null
                    ? []
                    : mine.Select(tree => checkedOut.FirstOrDefault(c => SamePath(c.Path, tree)).Branch).OfType<string>();

                branches.AddRange(named.Concat(inTrees)
                    .Distinct(StringComparer.Ordinal)
                    .Where(branch => Writable(team, repo, branch))
                    .Select(branch => new RepoResetItem(repo.Repo, branch, member)));
            }
        }

        return new RepositoryResetPlan(trees, branches, everyMember ? TeamBranchOf(team) : null);
    }

    /// <summary>Carries out <paramref name="plan"/>. Never throws for a git refusal: what could not be
    /// done is kept and named.</summary>
    public async Task<RepositoriesReset> ResetAsync(
        string team, IReadOnlyList<RepoResetTarget> repos, RepositoryResetPlan plan, CancellationToken ct)
    {
        var treesRemoved = new List<RepoResetItem>();
        var treesKept = new List<RepoResetItem>();
        var branchesDeleted = new List<RepoResetItem>();
        var branchesKept = new List<RepoResetItem>();
        var teamReset = new List<RepoResetItem>();
        var teamKept = new List<RepoResetItem>();

        foreach (var repo in repos.Where(repo => Directory.Exists(repo.ClonePath)))
        {
            // 1. Trees, never forced, then pruned.
            var trees = plan.Worktrees.Where(tree => tree.Repo == repo.Repo).ToList();
            var pass = await worktrees.RemoveAsync(repo.ClonePath, trees.Select(tree => tree.Name), ct);

            foreach (var removed in pass.Removed)
            {
                treesRemoved.Add(new RepoResetItem(repo.Repo, removed, trees.FirstOrDefault(t => t.Name == removed)?.Member));
            }

            foreach (var left in pass.Left)
            {
                var member = trees.FirstOrDefault(t => t.Name == left.Path)?.Member;
                treesKept.Add(new RepoResetItem(repo.Repo, left.Path, member, left.Reason));

                if (member is not null)
                    await worktrees.ReportLeftAsync(new ContainerId(team, member), repo.Repo, left, causation: null, ct);
            }

            // What is checked out where, AFTER the removal: a branch in a tree that was kept stays.
            var checkedOut = await CheckedOutAsync(repo.ClonePath, ct);

            // 2. The members' branches.
            foreach (var branch in plan.Branches.Where(b => b.Repo == repo.Repo))
            {
                if (!Writable(team, repo, branch.Name)) continue;

                var reference = $"refs/heads/{branch.Name}";
                if (await ShaAsync(repo.ClonePath, reference, ct) is not { } sha) continue;

                var reason = await KeepBranchBecauseAsync(repo.ClonePath, branch.Name, reference, checkedOut, ct);

                if (reason is null)
                {
                    var delete = await git.RunGitAsync(repo.ClonePath, ["update-ref", "-d", reference, sha], ct);
                    reason = delete.ExitCode == 0 ? null : $"git refused to delete it: {Said(delete)}";
                }

                if (reason is null)
                {
                    branchesDeleted.Add(branch);
                }
                else
                {
                    var kept = branch with { Reason = reason };
                    branchesKept.Add(kept);
                    await ReportBranchKeptAsync(team, kept, ct);
                }
            }

            // 3. The team branch, only with every member ticked.
            if (plan.TeamBranch is { } teamBranch && repo.DefaultBranch is { } defaultBranch)
            {
                foreach (var outcome in new[]
                {
                    await ResetLocalTeamBranchAsync(repo, teamBranch, defaultBranch, checkedOut, ct),
                    await DeleteOriginTeamBranchAsync(repo, teamBranch, defaultBranch, ct),
                })
                {
                    if (outcome is { } done) (done.Kept ? teamKept : teamReset).Add(done.Item);
                }
            }
        }

        return new RepositoriesReset(treesRemoved, treesKept, branchesDeleted, branchesKept, teamReset, teamKept);
    }

    /// <summary>The team branch's name. The identifier, which is the half of a team's two names that
    /// cannot change.</summary>
    public static string TeamBranchOf(string team) => $"team/{team}";

    /// <summary>
    /// THE GUARD ON EVERY MEMBER BRANCH THIS CLASS DELETES: never the stored default branch, never
    /// the team branch (only its own step moves that). A default branch that is not known protects
    /// every name that could be it: a branch a member's tree has checked out is then not a candidate.
    /// </summary>
    private static bool Writable(string team, RepoResetTarget repo, string branch) =>
        !string.IsNullOrWhiteSpace(branch)
        && !string.Equals(branch, repo.DefaultBranch, StringComparison.Ordinal)
        && !string.Equals(branch, TeamBranchOf(team), StringComparison.Ordinal)
        && BranchNames.IsValid(branch);

    private async Task<string?> KeepBranchBecauseAsync(
        string clone, string branch, string reference, IReadOnlyList<(string Path, string? Branch)> checkedOut,
        CancellationToken ct)
    {
        if (checkedOut.FirstOrDefault(c => c.Branch == branch) is { Path: { } where })
        {
            return $"It is checked out in {where}, which was kept.";
        }

        var unpushed = await git.CountCommitsNotOnAnyRemoteAsync(clone, reference, ct);

        return unpushed switch
        {
            null => "Could not tell whether its commits are on a remote, so it was not deleted.",
            0 => null,
            _ => $"It holds {unpushed} commit(s) that are on no remote, so it was not deleted.",
        };
    }

    /// <summary>
    /// The clone's <c>team/&lt;id&gt;</c>, moved to the default branch: the local default branch, else
    /// origin's copy of it. Null when there is no local team branch. Kept when it holds a commit on
    /// no remote and not on the default branch, or when moving it would touch uncommitted edits.
    /// </summary>
    private async Task<(RepoResetItem Item, bool Kept)?> ResetLocalTeamBranchAsync(
        RepoResetTarget repo, string teamBranch, string defaultBranch,
        IReadOnlyList<(string Path, string? Branch)> checkedOut, CancellationToken ct)
    {
        var clone = repo.ClonePath;
        var reference = $"refs/heads/{teamBranch}";

        if (await ShaAsync(clone, reference, ct) is not { } old) return null;

        (RepoResetItem, bool) Keep(string reason) => (new(repo.Repo, teamBranch, null, reason), true);

        var basis = await ShaAsync(clone, $"refs/heads/{defaultBranch}", ct) is not null
            ? $"refs/heads/{defaultBranch}"
            : $"refs/remotes/origin/{defaultBranch}";

        if (await ShaAsync(clone, basis, ct) is not { } target)
        {
            return Keep($"{defaultBranch} is not in the clone, so it was not reset.");
        }

        var lost = await git.RunGitAsync(clone, ["rev-list", "--count", reference, "--not", "--remotes", basis], ct);
        if (lost.ExitCode != 0 || !int.TryParse(lost.Stdout.Trim(), out var count))
        {
            return Keep("Could not tell whether its commits are on a remote, so it was not reset.");
        }

        if (count > 0)
        {
            return Keep($"It holds {count} commit(s) that are on no remote and not on {defaultBranch}, so it was not reset.");
        }

        GitRunner.GitInvocation moved;

        if (checkedOut.FirstOrDefault(c => c.Branch == teamBranch) is { Path: { } where })
        {
            if (!SamePath(where, clone)) return Keep($"It is checked out in {where}, which was kept.");

            // Checked out in the clone itself: moved with the tree, and only when the tree is clean.
            var status = await git.RunGitAsync(clone, ["status", "--porcelain"], ct);
            if (status.ExitCode != 0 || !string.IsNullOrWhiteSpace(status.Stdout))
            {
                return Keep("It is checked out in the clone, which has uncommitted edits, so it was not reset.");
            }

            moved = await git.RunGitAsync(clone, ["reset", "--keep", target], ct);
        }
        else
        {
            // Against the sha it was measured at, so a branch moved since is not overwritten.
            moved = await git.RunGitAsync(clone, ["update-ref", reference, target, old], ct);
        }

        return moved.ExitCode == 0
            ? (new RepoResetItem(repo.Repo, teamBranch, null, $"Reset to {defaultBranch}."), false)
            : Keep($"git refused to reset it: {Said(moved)}");
    }

    /// <summary>
    /// <c>origin/team/&lt;id&gt;</c>, deleted only when it holds nothing <c>origin/&lt;default&gt;</c>
    /// lacks - by ancestry, or by patch (<c>git cherry</c>, which errs toward "not there").
    ///
    /// <para>
    /// <b>NEVER ON A VIEW THAT COULD NOT BE REFRESHED.</b> Origin is asked for both branches first
    /// (<c>git ls-remote</c>), and both are fetched before the compare: the clone's
    /// <c>refs/remotes/origin/…</c> may be older than a push from another clone. A failed read or
    /// fetch keeps the branch, named. Then the delete is leased on the sha that was compared
    /// (<see cref="GitRunner.DeleteOriginBranchIfAtAsync"/>), so a push that lands between the
    /// compare and the delete refuses it and the branch is kept, named. Null when origin has none.
    /// A contributor-mode origin is the fork: kept, never touched, not even fetched.
    /// </para>
    /// </summary>
    private async Task<(RepoResetItem Item, bool Kept)?> DeleteOriginTeamBranchAsync(
        RepoResetTarget repo, string teamBranch, string defaultBranch, CancellationToken ct)
    {
        var clone = repo.ClonePath;
        var name = $"origin/{teamBranch}";
        var remote = $"refs/remotes/origin/{teamBranch}";
        var basis = $"refs/remotes/origin/{defaultBranch}";

        (RepoResetItem, bool) Keep(string reason) => (new(repo.Repo, name, null, reason), true);

        if (repo.ContributorMode)
        {
            return await ShaAsync(clone, remote, ct) is null
                ? null
                : Keep("In contributor mode origin is the fork, and a reset never touches it.");
        }

        var listed = await git.RunGitAsync(
            clone, ["ls-remote", "origin", $"refs/heads/{teamBranch}", $"refs/heads/{defaultBranch}"], ct);
        if (listed.ExitCode != 0)
        {
            return await ShaAsync(clone, remote, ct) is null
                ? null
                : Keep($"Could not ask origin for it ({Said(listed)}), so it was not deleted.");
        }

        var heads = listed.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 2)
            .Select(parts => parts[1])
            .ToHashSet(StringComparer.Ordinal);

        if (!heads.Contains($"refs/heads/{teamBranch}")) return null;

        if (!heads.Contains($"refs/heads/{defaultBranch}"))
        {
            return Keep($"origin has no {defaultBranch} to compare it with, so it was not deleted.");
        }

        var fetched = await git.RunGitAsync(
            clone,
            ["fetch", "--no-tags", "origin", $"+refs/heads/{teamBranch}:{remote}", $"+refs/heads/{defaultBranch}:{basis}"],
            ct);
        if (fetched.ExitCode != 0)
        {
            return Keep($"Could not fetch it from origin ({Said(fetched)}), so it was not deleted.");
        }

        // The sha compared is the sha the delete is leased on.
        if (await ShaAsync(clone, remote, ct) is not { } compared || await ShaAsync(clone, basis, ct) is null)
        {
            return Keep("Could not read it after fetching it, so it was not deleted.");
        }

        var merged = await git.RunGitAsync(clone, ["merge-base", "--is-ancestor", compared, basis], ct);

        if (merged.ExitCode != 0)
        {
            var cherry = await git.RunGitAsync(clone, ["cherry", basis, compared], ct);
            var equivalent = cherry.ExitCode == 0
                && !cherry.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(line => line.StartsWith('+'));

            if (!equivalent)
            {
                return Keep($"It holds work origin/{defaultBranch} does not have, so it was not deleted.");
            }
        }

        var deleted = await git.DeleteOriginBranchIfAtAsync(clone, teamBranch, compared, ct);

        return deleted.ExitCode == 0
            ? (new RepoResetItem(repo.Repo, name, null, $"Deleted: it held nothing origin/{defaultBranch} lacks."), false)
            : Keep($"It changed on origin after it was compared, or git could not delete it, so it was kept: {Said(deleted)}");
    }

    /// <summary>Names a kept branch on the team feed. Source is the member it belongs to.</summary>
    private Task ReportBranchKeptAsync(string team, RepoResetItem branch, CancellationToken ct) =>
        branch.Member is null
            ? Task.CompletedTask
            : log.AppendAsync(
                new NewMessage(
                    MessageTypes.RepoBranchKept,
                    JsonSerializer.Serialize(new { repo = branch.Repo, branch = branch.Name, reason = branch.Reason }),
                    new ContainerId(team, branch.Member).ToString()),
                ct);

    /// <summary>Every tree of <paramref name="member"/> in <paramref name="repo"/>: its per-card trees
    /// and a per-member <c>wt_&lt;Member&gt;</c> from before trees were per card.</summary>
    private IReadOnlyList<string> TreesOf(string team, string member, string repo)
    {
        var trees = paths.WorktreesFor(new ContainerId(team, member), repo).ToList();
        var bare = Path.Combine(paths.ReposFor(team), repo, Worktrees.Prefix + member);

        if (Directory.Exists(bare)) trees.Add(bare);

        return trees;
    }

    /// <summary>Every tree git has registered for the clone, with the branch it has checked out
    /// (null when detached). The clone itself is the first.</summary>
    private async Task<IReadOnlyList<(string Path, string? Branch)>> CheckedOutAsync(string clone, CancellationToken ct)
    {
        var listed = await git.RunGitAsync(clone, ["worktree", "list", "--porcelain"], ct);
        var trees = new List<(string Path, string? Branch)>();

        if (listed.ExitCode != 0) return trees;

        string? path = null;
        string? branch = null;

        foreach (var line in listed.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).Append(""))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = line["branch refs/heads/".Length..];
            }
            else if (line.Length == 0 && path is not null)
            {
                trees.Add((path, branch));
                path = null;
                branch = null;
            }
        }

        return trees;
    }

    private async Task<string?> ShaAsync(string clone, string reference, CancellationToken ct)
    {
        var parsed = await git.RunGitAsync(clone, ["rev-parse", "--verify", "--quiet", reference + "^{commit}"], ct);
        return parsed.ExitCode == 0 ? parsed.Stdout.Trim() : null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.Ordinal);

    private static string Said(GitRunner.GitInvocation run) =>
        GitOutputRedaction.Redact((string.IsNullOrWhiteSpace(run.Stderr) ? run.Stdout : run.Stderr).Trim()) ?? "";
}
