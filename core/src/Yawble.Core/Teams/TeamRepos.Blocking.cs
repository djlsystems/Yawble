using Yawble.Core.Bus;

namespace Yawble.Core.Teams;

/// <summary>
/// A branch that stops this team's member branches being made (B020): git cannot hold a ref <c>members/&lt;x&gt;</c> and refs
/// under <c>members/&lt;x&gt;/</c> at once, so an old <c>members/docs</c> (or a branch named just <c>members</c>) blocks every
/// <c>members/docs/&lt;name&gt;</c> of team "Docs". <c>Repo</c>: "main" or the extra repository's name, as the Git dialog's
/// Delete takes it. <c>Location</c>: <c>local</c> (a branch of that repository) or <c>remote</c> (origin's, as of the last fetch).
/// </summary>
public sealed record TeamGitBlockingBranch(string Name, string Repo, string Location);

/// <summary>A member's start refused because a branch blocks its member branch (B020). Nothing was changed.</summary>
public sealed class MemberBranchBlockedException(string message) : InvalidOperationException(message);

/// <summary>
/// B020: before a member branch members/&lt;team&gt;/&lt;name&gt; is made or pushed, the refs that would block it are looked
/// for: in the host repository (its branches, and origin's remote-tracking refs), its shared view, and the member's clone
/// (read with upload-pack, never git in the clone). Found: the member's start fails with a plain message naming the branch,
/// where it is and what to do, and nothing is deleted, renamed or moved. The same-team B016 move runs first (and itself
/// refuses a move that would clash). The Git dialog lists the blockers (<see cref="TeamGitStatus.BlockingBranches"/>) for
/// its Delete, under the usual approval rules.
/// </summary>
public sealed partial class TeamRepos
{
    /// <summary>The branch names that cannot exist beside <paramref name="branch"/>'s path: each of its parent paths
    /// (members, members/&lt;team&gt; for members/&lt;team&gt;/&lt;name&gt;).</summary>
    internal static IReadOnlyList<string> ParentPaths(string branch)
    {
        var parts = branch.Split('/');
        return Enumerable.Range(1, parts.Length - 1).Select(n => string.Join('/', parts[..n])).ToList();
    }

    /// <summary>The names that block a team's member branches: the parents of members/&lt;team&gt;/&lt;anyone&gt;.</summary>
    internal static IReadOnlyList<string> BlockingNames(string team) => ParentPaths(MemberBranchPrefix(team) + "x");

    /// <summary>The blocking refs in a repository the host owns or the person's (git runs there): its branches and, with
    /// <paramref name="withRemote"/>, origin's remote-tracking ones. Each as (branch name, "local" | "remote").</summary>
    private async Task<IReadOnlyList<(string Name, string Location)>> BlockingRefsAsync(string path, IReadOnlyList<string> names, bool withRemote,
        CancellationToken ct)
    {
        var wanted = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            wanted["refs/heads/" + n] = (n, "local");
            if (withRemote) wanted["refs/remotes/origin/" + n] = (n, "remote");
        }
        var r = await Git.TryRunAsync(path, ["for-each-ref", "--format=%(refname)", .. wanted.Keys], ct);
        if (r.ExitCode != 0) throw new GitException($"could not read the branches of {path}: {r.Stderr}");
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(wanted.ContainsKey).Select(x => wanted[x]).Distinct().ToList();
    }

    /// <summary>The blocking branches in a member's clone (by upload-pack from the host repository; nothing runs in the clone).</summary>
    private async Task<IReadOnlyList<string>> BlockingInCloneAsync(string repoPath, string clonePath, IReadOnlyList<string> names, CancellationToken ct)
    {
        if (!Directory.Exists(clonePath) || CloneGitDirProblem(clonePath) is not null) return [];
        var r = await Git.TryRunAsync(repoPath, ["ls-remote", "--heads", StrictUploadPack, "--", Path.Combine(clonePath, ".git")], ct);
        if (r.ExitCode != 0) throw new GitException($"could not read the branches of the clone {clonePath}: {r.Stderr}");
        var heads = r.Stdout.Split('\n').Select(l => l.Split('\t', 2)).Where(p => p.Length == 2).Select(p => p[1].Trim()).ToHashSet(StringComparer.Ordinal);
        return names.Where(n => heads.Contains("refs/heads/" + n)).ToList();
    }

    /// <summary>
    /// The plain refusal for a member branch that cannot be made: the branch, where it is, and what to do. Never git's own
    /// "cannot lock ref" text.
    /// </summary>
    internal static string BlockedMessage(string team, string branch, string where, bool inDialog) =>
        $"The branch {branch} in {where} stops team {team}'s member branches ({MemberBranchPrefix(team)}<name>): git cannot keep a branch "
        + "and branches under its name at once. Nothing was changed. "
        + (inDialog
            ? $"Rename or delete {branch} in the Git dialog, or rename the team, then start the member again."
            : $"Rename or delete {branch} there, or rename the team, then start the member again.");

    /// <summary>
    /// Throws <see cref="MemberBranchBlockedException"/> when a ref blocks the member's branch in one repository (the
    /// caller holds the team's lock). Only for a member branch of the team (a Manager's team/&lt;team&gt; and an old-style
    /// branch a member is still on are not made anew). <paramref name="repoLabel"/>: how the message names the repository.
    /// </summary>
    private async Task CheckMemberBranchFreeAsync(MemberRef member, string branch, string repoLabel, string repoPath, string sharedPath,
        string clonePath, CancellationToken ct)
    {
        if (member.Role == MemberRole.Manager || !branch.StartsWith(MemberBranchPrefix(member.Team), StringComparison.Ordinal)) return;
        var names = ParentPaths(branch);
        string? message = null;
        foreach (var (name, location) in await BlockingRefsAsync(repoPath, names, withRemote: true, ct))
        {
            message = location == "local"
                ? BlockedMessage(member.Team, name, $"{repoLabel} ({repoPath})", inDialog: true)
                : BlockedMessage(member.Team, name, $"origin of {repoLabel} (origin/{name}, as of the last fetch)", inDialog: true);
            break;
        }
        if (message is null && Directory.Exists(Path.Combine(sharedPath, "objects"))
            && (await BlockingRefsAsync(sharedPath, names, withRemote: false, ct)).Select(b => b.Name).FirstOrDefault() is { } inShared)
            message = BlockedMessage(member.Team, inShared, $"the team's shared view of {repoLabel} ({sharedPath}), which follows that repository", inDialog: true);
        if (message is null && (await BlockingInCloneAsync(repoPath, clonePath, names, ct)).FirstOrDefault() is { } inClone)
            message = BlockedMessage(member.Team, inClone, $"{member.Name}'s clone of {repoLabel} ({clonePath})", inDialog: false);
        if (message is null) return;
        _log?.Invoke($"{member.Qualified}: not started: {message}");
        throw new MemberBranchBlockedException(message);
    }

    /// <summary>
    /// The refs in one of the team's repositories (the main one, or a writable extra by name) that block members/&lt;slug&gt;/...
    /// for <paramref name="teamName"/>: its branches and origin's (as of the last fetch). For the Git dialog, and for any
    /// change of the team's name: a new name whose list is not empty would break its members' branches, so it is refused
    /// before anything moves.
    /// </summary>
    public async Task<IReadOnlyList<TeamGitBlockingBranch>> BlockingBranchesAsync(long teamId, string teamName, string? repoName = null,
        CancellationToken ct = default)
    {
        var repo = await GitRepoAsync(teamId, teamName, repoName, ct);
        return await WithTeamLock(teamId, () => BlockingLockedAsync(repo, teamName, ct), ct);
    }

    private async Task<IReadOnlyList<TeamGitBlockingBranch>> BlockingLockedAsync(TeamRepo repo, string teamName, CancellationToken ct)
    {
        var label = ExtrasOf(repo.TeamId).FirstOrDefault(e => string.Equals(Path.GetFullPath(e.Path), Path.GetFullPath(repo.Path), PathComparison))?.Name ?? "main";
        return (await BlockingRefsAsync(repo.Path, BlockingNames(teamName), withRemote: true, ct))
            .Select(b => new TeamGitBlockingBranch(b.Name, label, b.Location))
            .OrderBy(b => b.Name, StringComparer.Ordinal).ThenBy(b => b.Location, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Refuses a new name for the team when, in any of its writable repositories, a branch would block the new
    /// members/&lt;slug&gt;/... (B020). Call before anything is renamed or moved; nothing is changed here.
    /// </summary>
    public async Task CheckTeamNameFreeAsync(long teamId, string newName, CancellationToken ct = default)
    {
        var problems = new List<string>();
        if (RepoOf(teamId) is { } main && Directory.Exists(main.Path))
            problems.AddRange((await WithTeamLock(teamId, () => BlockingLockedAsync(main, newName, ct), ct)).Select(Describe));
        foreach (var extra in ExtrasOf(teamId).Where(e => e.Writable && Directory.Exists(e.Path)))
            problems.AddRange((await WithTeamLock(teamId, () => BlockingLockedAsync(new TeamRepo(teamId, extra.Path, false, extra.BaseBranch), newName, ct), ct)).Select(Describe));
        if (problems.Count == 0) return;
        throw new MemberBranchBlockedException(
            $"The team cannot be named {newName}: its member branches would be {MemberBranchPrefix(newName)}<name>, and {string.Join("; ", problems)} "
            + "already has that name (git cannot keep a branch and branches under its name at once). Nothing was changed. "
            + "Rename or delete that branch in the Git dialog, or pick another name.");

        static string Describe(TeamGitBlockingBranch b) =>
            b.Location == "local" ? $"the branch {b.Name} in repository {b.Repo}" : $"origin's branch {b.Name} (repository {b.Repo})";
    }

    /// <summary>The parent path of <paramref name="branch"/> origin already has as a branch (as of the last fetch), or null:
    /// pushing the branch would fail with git's "cannot lock ref".</summary>
    private async Task<string?> BlockedOnOriginAsync(string repoPath, string branch, CancellationToken ct) =>
        (await BlockingRefsAsync(repoPath, ParentPaths(branch), withRemote: true, ct)).Where(b => b.Location == "remote").Select(b => b.Name).FirstOrDefault();
}
