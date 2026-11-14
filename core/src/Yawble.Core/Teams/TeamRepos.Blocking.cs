using System.Text.Json.Serialization;
using Yawble.Core.Bus;

namespace Yawble.Core.Teams;

/// <summary>
/// A branch that stops this team's member branches being made (B020): git cannot hold a ref <c>members/&lt;x&gt;</c> and refs
/// under <c>members/&lt;x&gt;/</c> at once, so an old <c>members/docs</c> (or a branch named just <c>members</c>) blocks every
/// <c>members/docs/&lt;name&gt;</c> of team "Docs". <c>Repo</c>: "main" or the extra repository's name, as the Git dialog's
/// Delete takes it. <c>Location</c>: <c>local</c> (a branch of that repository) or <c>remote</c> (origin's, as of the last fetch).
/// <para><see cref="InUseBy"/> (card 104): set when a member (of this team or another one on the same repository) is still
/// recorded on that branch, its old-style members/&lt;name&gt;: for example "team Alpha's member Ada". Such a branch is not
/// deleted from the Git dialog (the dialog shows no Delete for it): it moves to <see cref="MovesTo"/> at that member's next
/// start, which frees the name.</para>
/// </summary>
public sealed record TeamGitBlockingBranch(string Name, string Repo, string Location)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? InUseBy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MovesTo { get; init; }
}

/// <summary>A member's start refused because a branch blocks its member branch (B020). Nothing was changed.</summary>
public sealed class MemberBranchBlockedException(string message) : InvalidOperationException(message);

/// <summary>
/// B020: before a member branch members/&lt;team&gt;/&lt;name&gt; is made or pushed, the refs that would block it are looked
/// for: in the host repository (its branches, and origin's remote-tracking refs), its shared view, and the member's clone
/// (read with upload-pack, never git in the clone). Found: the member's start fails with a plain message naming the branch,
/// where it is and what to do, and nothing is deleted, renamed or moved. The same-team B016 move runs first (and itself
/// refuses a move that would clash, except through the member's own old branch: team "Docs", member "Docs", card 104).
/// The Git dialog lists the blockers (<see cref="TeamGitStatus.BlockingBranches"/>) for its Delete, under the usual approval
/// rules; one a member of any team on the repository is still on is marked and never deleted from there.
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

    /// <summary>A member, of any team, whose clone of one repository (the team's main one or a writable extra) is recorded on
    /// <see cref="Branch"/>.</summary>
    internal sealed record RepoUser(long TeamId, string Team, string Member, string Branch, string ClonePath);

    /// <summary>
    /// Every member of every team that uses the repository at <paramref name="repoPath"/> (as its main repository or as an
    /// extra one), with the branch its clone is recorded on (card 104: teams sharing a repository see each other's old-style
    /// members/&lt;name&gt; branches, and one team's dialog must never delete another team's live branch).
    /// </summary>
    internal IReadOnlyList<RepoUser> RepoUsers(string repoPath)
    {
        var want = NormalPath(repoPath);
        var list = new List<RepoUser>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT t.id, t.name, m.name, mc.branch, mc.path, r.path FROM member_clones mc JOIN members m ON m.id = mc.member_id "
            + "JOIN teams t ON t.id = m.team_id JOIN team_repos r ON r.team_id = t.id "
            + "UNION ALL SELECT t.id, t.name, m.name, x.branch, x.path, e.path FROM member_extra_clones x JOIN members m ON m.id = x.member_id "
            + "JOIN teams t ON t.id = m.team_id JOIN team_extra_repos e ON e.team_id = t.id AND e.name = x.repo_name COLLATE NOCASE;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(NormalPath(r.GetString(5)), want, PathComparison))
                list.Add(new RepoUser(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    private static string NormalPath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>"team Alpha's member Ada", or "member Docs of this team" when it is <paramref name="teamId"/>'s own.</summary>
    internal static string Holder(RepoUser u, long teamId) =>
        u.TeamId == teamId ? $"member {u.Member} of this team" : $"team {u.Team}'s member {u.Member}";

    /// <summary>The member still on <paramref name="branch"/>, its old-style members/&lt;name&gt; (not <paramref name="except"/>'s own).</summary>
    private static RepoUser? OldBranchUser(IReadOnlyList<RepoUser> users, string branch, string? exceptClone = null) =>
        !IsLegacyMemberBranch(branch) ? null
            : users.FirstOrDefault(u => u.Branch == branch && (exceptClone is null || !string.Equals(NormalPath(u.ClonePath), NormalPath(exceptClone), PathComparison)));

    /// <summary>
    /// The plain refusal for a member branch that cannot be made: the branch, where it is, and what to do. Never git's own
    /// "cannot lock ref" text. Only actions that exist are named: the Git dialog's Delete, or (for a branch a member is
    /// still on) that member's next start, which moves it to members/&lt;its team&gt;/&lt;name&gt; and so frees the name.
    /// </summary>
    internal static string BlockedMessage(string team, string branch, string where, bool inDialog, RepoUser? heldBy = null, long teamId = 0)
    {
        var head = $"The branch {branch} in {where} stops team {team}'s member branches ({MemberBranchPrefix(team)}<name>): git cannot keep a branch "
            + "and branches under its name at once. Nothing was changed. ";
        if (heldBy is not null)
            return head + $"It is the old branch of {Holder(heldBy, teamId)}, who is still on it, so it is not deleted: it moves to "
                + $"{BranchFor(heldBy.Team, heldBy.Member)} at that member's next start. Start {heldBy.Member}"
                + (heldBy.TeamId == teamId ? "" : $" (team {heldBy.Team})") + " first, then start this member again.";
        return head + (inDialog
            ? $"To go on, delete {branch} in the Git dialog, then start the member again."
            : $"To go on, delete {branch} there, then start the member again.");
    }

    /// <summary>
    /// Throws <see cref="MemberBranchBlockedException"/> when a ref blocks the member's branch in one repository (the
    /// caller holds the team's lock). Only for a member branch of the team (a Manager's team/&lt;team&gt; and an old-style
    /// branch a member is still on are not made anew). <paramref name="repoLabel"/>: how the message names the repository.
    /// <para>The member's own old branch, just moved through (team "Docs", member "Docs": members/docs to members/docs/docs,
    /// card 104), does not refuse its own start where it is still left: in the shared view when the repository no longer
    /// has it (the shared view follows the repository, and is updated next), and on origin when the new branch holds its
    /// commits (the push after the run replaces it, see <see cref="PublishRepoAsync"/>).</para>
    /// </summary>
    private async Task CheckMemberBranchFreeAsync(MemberRef member, string branch, string repoLabel, string repoPath, string sharedPath,
        string clonePath, CancellationToken ct)
    {
        if (member.Role == MemberRole.Manager || !branch.StartsWith(MemberBranchPrefix(member.Team), StringComparison.Ordinal)) return;
        var names = ParentPaths(branch);
        var own = LegacyBranchFor(member.Name);
        var ownThrough = branch.StartsWith(own + "/", StringComparison.Ordinal);
        var users = RepoUsers(repoPath);
        string? message = null;
        var here = await BlockingRefsAsync(repoPath, names, withRemote: true, ct);
        foreach (var (name, location) in here)
        {
            var held = OldBranchUser(users, name, clonePath);
            if (location == "remote" && ownThrough && name == own && held is null && await OwnOldOnOriginIsHeldAsync(repoPath, own, branch, ct)) continue;
            message = location == "local"
                ? BlockedMessage(member.Team, name, $"{repoLabel} ({repoPath})", inDialog: true, held, member.TeamId)
                : BlockedMessage(member.Team, name, $"origin of {repoLabel} (origin/{name}, as of the last fetch)", inDialog: true, held, member.TeamId);
            break;
        }
        if (message is null && Directory.Exists(Path.Combine(sharedPath, "objects"))
            && (await BlockingRefsAsync(sharedPath, names, withRemote: false, ct)).Select(b => b.Name)
                .FirstOrDefault(n => !(ownThrough && n == own && !here.Contains((n, "local")))) is { } inShared)
            message = BlockedMessage(member.Team, inShared, $"the team's shared view of {repoLabel} ({sharedPath}), which follows that repository",
                inDialog: true, OldBranchUser(users, inShared, clonePath), member.TeamId);
        if (message is null && (await BlockingInCloneAsync(repoPath, clonePath, names, ct)).FirstOrDefault() is { } inClone)
            message = BlockedMessage(member.Team, inClone, $"{member.Name}'s clone of {repoLabel} ({clonePath})", inDialog: false);
        if (message is null) return;
        _log?.Invoke($"{member.Qualified}: not started: {message}");
        throw new MemberBranchBlockedException(message);
    }

    /// <summary>The old-style branch a member branch is under when the member is named like its team's slug (members/docs for
    /// members/docs/docs), else null: that member's B016 move goes through its own old branch (card 104).</summary>
    internal static string? OwnOldBranchOf(string team, string branch) =>
        branch == MemberBranchPrefix(team) + SlugOf(team) ? BranchPrefix + SlugOf(team) : null;

    /// <summary>Origin's copy of a member's own old branch (as of the last fetch) is contained in its new branch here: the
    /// new branch holds every commit of it, so replacing it on origin loses nothing.</summary>
    private async Task<bool> OwnOldOnOriginIsHeldAsync(string repoPath, string old, string branch, CancellationToken ct) =>
        await RevAsync(repoPath, "refs/remotes/origin/" + old, ct) is { } theirs
        && await RevAsync(repoPath, "refs/heads/" + branch, ct) is { } ours
        && (await Git.TryRunAsync(repoPath, ["merge-base", "--is-ancestor", theirs, ours], ct)).ExitCode == 0;

    /// <summary>
    /// The refs in one of the team's repositories (the main one, or a writable extra by name) that block members/&lt;slug&gt;/...
    /// for <paramref name="teamName"/>: its branches and origin's (as of the last fetch). For the Git dialog. Each one a
    /// member of any team on that repository is still on is marked (<see cref="TeamGitBlockingBranch.InUseBy"/>).
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
        var users = RepoUsers(repo.Path);
        return (await BlockingRefsAsync(repo.Path, BlockingNames(teamName), withRemote: true, ct))
            .Select(b => OldBranchUser(users, b.Name) is { } u
                ? new TeamGitBlockingBranch(b.Name, label, b.Location) { InUseBy = Holder(u, repo.TeamId), MovesTo = BranchFor(u.Team, u.Member) }
                : new TeamGitBlockingBranch(b.Name, label, b.Location))
            .OrderBy(b => b.Name, StringComparer.Ordinal).ThenBy(b => b.Location, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The parent paths of <paramref name="branch"/> origin already has as branches (as of the last fetch): pushing
    /// the branch would fail with git's "cannot lock ref".</summary>
    private async Task<IReadOnlyList<string>> BlockersOnOriginAsync(string repoPath, string branch, CancellationToken ct) =>
        (await BlockingRefsAsync(repoPath, ParentPaths(branch), withRemote: true, ct)).Where(b => b.Location == "remote").Select(b => b.Name).ToList();
}
