using System.Text;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;

namespace Yawble.Core.Teams;

/// <summary>
/// B016: member branches carry the team, members/&lt;team&gt;/&lt;name&gt;. A member whose clone is still on the old
/// members/&lt;name&gt; moves at its next start:
/// <list type="bullet">
/// <item>The host repository gets the new branch at its old branch's commit (create-only; an existing new branch is never
///   moved). Clone work it does not have yet comes back to the new name at the next fetch-back, by the usual rules.</item>
/// <item>In the clone, which the host never runs git in, the new branch is written as a loose ref file at the clone's own
///   old tip, and HEAD is pointed at it only when HEAD is the old branch. The index and working tree are not touched: it is
///   the same commit, so this is what <c>git switch -c</c> would do.</item>
/// <item>The old branch is left everywhere it is (host repository, clone, origin): never deleted, never forced. The person
///   deletes it in the Git dialog. The new one is pushed after the run like any branch origin does not have.</item>
/// </list>
/// Anything that would make the move unsafe (a clone .git the host may not read, a link on the ref path, a name clash with
/// an existing ref) leaves the member on its old branch, with the reason logged.
/// </summary>
public sealed partial class TeamRepos
{
    /// <summary>Moves the member's main clone (the caller holds the team's lock). True when the member is now on the new name.</summary>
    private async Task<bool> MoveLegacyBranchLockedAsync(MemberRef member, TeamRepo repo, CancellationToken ct)
    {
        if (member.Role == MemberRole.Manager) return false;
        if (Row(member.Id) is null && AdoptLegacyClone(member, CheckoutOf(member, repo).ClonePath) is { } adopted)
        {
            // A rehired member (its row went with RemoveMemberAsync) reusing a pre-B016 clone: record the branch the clone
            // is on, then move it like any old-style member. If the move cannot be made, the row still names the clone's branch.
            InsertClone(member.Id, CheckoutOf(member, repo).ClonePath, adopted);
            _log?.Invoke($"{member.Qualified}: reusing the clone on disk, on {adopted}");
        }
        if (Row(member.Id) is not { } row || !IsLegacyMemberBranch(row.Branch)) return false;
        var target = BranchFor(member.Team, member.Name);
        if (!await MoveCloneBranchAsync(member, repo.Path, row.Path, row.Branch, target, ct)) return false;
        UpdateClone(member.Id, "branch = $b", ("$b", target));
        _log?.Invoke($"{member.Qualified}: moved from {row.Branch} to {target} (the old branch is kept)");
        return true;
    }

    /// <summary>Moves the member's clone of a writable extra repository (the caller holds the team's lock).</summary>
    private async Task<ExtraCloneRow?> MoveLegacyExtraBranchLockedAsync(MemberRef member, ExtraRepo extra, ExtraCloneRow row, CancellationToken ct)
    {
        if (member.Role == MemberRole.Manager || !IsLegacyMemberBranch(row.Branch)) return row;
        var target = BranchFor(member.Team, member.Name);
        if (!await MoveCloneBranchAsync(member, extra.Path, row.Path, row.Branch, target, ct)) return row;
        UpdateExtraClone(member.Id, extra.Name, "branch = $b", ("$b", target));
        _log?.Invoke($"{member.Qualified}: {extra.Name}: moved from {row.Branch} to {target} (the old branch is kept)");
        return row with { Branch = target };
    }

    /// <summary>A clone left on disk with no row (its member was removed, a same-named one hired): the old-style branch to
    /// record for it when its HEAD is on <see cref="LegacyBranchFor"/> the member's name, else null (a new-style or missing
    /// clone needs nothing: the member's own branch is the one recorded). Read by file, no git in the clone.</summary>
    internal static string? AdoptLegacyClone(MemberRef member, string clonePath)
    {
        if (!Directory.Exists(clonePath) || !Directory.EnumerateFileSystemEntries(clonePath).Any() || CloneGitDirProblem(clonePath) is not null) return null;
        return LegacyBranchToAdopt(member.Role, member.Name, CloneHead.Read(Path.Combine(clonePath, ".git"))?.Branch);
    }

    /// <summary>The decision of <see cref="AdoptLegacyClone"/>: the reused clone's branch when it is the member's old-style one.</summary>
    internal static string? LegacyBranchToAdopt(MemberRole role, string memberName, string? cloneHeadBranch) =>
        role != MemberRole.Manager && cloneHeadBranch is not null && cloneHeadBranch == LegacyBranchFor(memberName) ? cloneHeadBranch : null;

    private async Task<bool> MoveCloneBranchAsync(MemberRef member, string repoPath, string clonePath, string from, string to, CancellationToken ct)
    {
        var why = await MoveProblemAsync(repoPath, clonePath, from, to, ct);
        if (why is null) return true;
        _log?.Invoke($"{member.Qualified}: stays on {from} for now, not moved to {to}: {why}");
        return false;
    }

    /// <summary>Does the move; returns null, or why it was not made (then nothing in the clone changed).</summary>
    private async Task<string?> MoveProblemAsync(string repoPath, string clonePath, string from, string to, CancellationToken ct)
    {
        if (!Directory.Exists(clonePath)) return null; // no clone yet: the next clone is made on the new name
        if (CloneGitDirProblem(clonePath) is { } problem) return problem;
        var gitDir = Path.Combine(clonePath, ".git");

        // The clone's own tip of the old branch (read with upload-pack, never git in the clone).
        var listed = await Git.TryRunAsync(repoPath, ["ls-remote", StrictUploadPack, "--", gitDir, "refs/heads/" + from], ct);
        if (listed.ExitCode != 0) return "could not read the clone's branch: " + listed.Stderr;
        var cloneTip = listed.Stdout.Split('\t', 2)[0].Trim();
        if (cloneTip.Length > 0 && !IsObjectId(cloneTip)) return "the clone's branch is not a commit id";
        var newInClone = await Git.TryRunAsync(repoPath, ["ls-remote", StrictUploadPack, "--", gitDir, "refs/heads/" + to], ct);
        if (newInClone.ExitCode != 0) return "could not read the clone's branches: " + newInClone.Stderr;
        var newTip = newInClone.Stdout.Split('\t', 2)[0].Trim();
        var cloneHasNew = newTip.Length > 0;
        // The clone already has the new name at another commit (an agent made it, or an earlier move stopped half-way and
        // work went on the old one): switching to it could hide work. Stay, and say why.
        if (cloneHasNew && newTip != cloneTip) return $"the clone already has {to} at another commit than {from}";
        if (!cloneHasNew && await RefClashAsync(repoPath, gitDir, to, ct) is { } cloneClash) return $"the clone already has {cloneClash}";

        // The host repository: the new branch at the old one's commit there (it holds every fetched-back run and any sync
        // since). Clone work it does not have yet, or that was parked, lands on the new name at the next fetch-back, by the
        // usual rules. No old branch here means no work fetched back yet: the next fetch-back makes the new one.
        if (await RevAsync(repoPath, "refs/heads/" + from, ct) is { } oldHere && await RevAsync(repoPath, "refs/heads/" + to, ct) is null)
        {
            if (await RefClashInRepoAsync(repoPath, to, ct) is { } clash) return $"the repository already has {clash}";
            var made = await Git.TryRunAsync(repoPath, ["update-ref", "-m", $"move {from} to {to}", "refs/heads/" + to, oldHere, ""], ct);
            if (made.ExitCode != 0) return "could not make the new branch in the repository: " + made.Stderr;
        }

        // The clone, by file: the new loose ref (unless the clone already has the branch) and HEAD.
        if (!cloneHasNew && cloneTip.Length > 0)
        {
            if (WriteLooseRef(gitDir, to, cloneTip) is { } refused) return refused;
        }
        var head = CloneHead.Read(gitDir);
        if (head?.Branch == from && WriteHead(gitDir, to) is { } headRefused) return headRefused;
        return null;
    }

    /// <summary>An existing ref of the clone that would stop refs/heads/<paramref name="branch"/> being made (a ref at one
    /// of its parent paths, or refs under it), read with upload-pack from the host repository.</summary>
    private async Task<string?> RefClashAsync(string repoPath, string gitDir, string branch, CancellationToken ct)
    {
        var all = await Git.TryRunAsync(repoPath, ["ls-remote", "--heads", StrictUploadPack, "--", gitDir], ct);
        return all.ExitCode != 0 ? "branches the host could not read: " + all.Stderr : Clash(all.Stdout.Split('\n')
            .Select(l => l.Split('\t', 2)).Where(p => p.Length == 2).Select(p => p[1].Trim()), branch);
    }

    private async Task<string?> RefClashInRepoAsync(string repoPath, string branch, CancellationToken ct) =>
        Clash((await Git.RunAsync(repoPath, ct, "for-each-ref", "--format=%(refname)", "refs/heads/")).Split('\n', StringSplitOptions.RemoveEmptyEntries), branch);

    private static string? Clash(IEnumerable<string> refs, string branch)
    {
        var wanted = "refs/heads/" + branch;
        foreach (var r in refs)
        {
            if (r == wanted) continue;
            if (wanted.StartsWith(r + "/", StringComparison.Ordinal) || r.StartsWith(wanted + "/", StringComparison.Ordinal)) return r;
        }
        return null;
    }

    /// <summary>Writes .git/refs/heads/&lt;branch&gt; in a clone: every folder on the way must be a plain folder (an agent could
    /// have put a link there), and the file must not exist yet. Returns null, or why not.</summary>
    private static string? WriteLooseRef(string gitDir, string branch, string sha)
    {
        if (!CloneHead.IsBranchName(branch)) return $"{branch} is not a branch name";
        var dir = Path.Combine(gitDir, "refs", "heads");
        if (IsLink(new DirectoryInfo(Path.Combine(gitDir, "refs"))) || IsLink(new DirectoryInfo(dir)) || !Directory.Exists(dir))
            return "the clone's .git/refs/heads is missing or a link";
        var parts = branch.Split('/');
        foreach (var part in parts[..^1])
        {
            dir = Path.Combine(dir, part);
            if (File.Exists(dir)) return $"the clone has a ref where {branch} needs a folder";
            if (IsLink(new DirectoryInfo(dir))) return "a folder on the clone's ref path is a link";
            Directory.CreateDirectory(dir);
            if (IsLink(new DirectoryInfo(dir))) return "a folder on the clone's ref path is a link";
        }
        var file = Path.Combine(dir, parts[^1]);
        if (Path.Exists(file)) return $"the clone already has {branch}";
        try
        {
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(Encoding.ASCII.GetBytes(sha + "\n"));
        }
        catch (IOException e)
        {
            return "could not write the clone's new branch: " + e.Message;
        }
        return null;
    }

    /// <summary>Points the clone's HEAD at refs/heads/&lt;branch&gt;, through a temporary file moved into place (never through a link).</summary>
    private static string? WriteHead(string gitDir, string branch)
    {
        var head = new FileInfo(Path.Combine(gitDir, "HEAD"));
        if (IsLink(head)) return "the clone's HEAD is a link";
        if (File.Exists(Path.Combine(gitDir, "HEAD.lock"))) return "the clone's HEAD is locked (a git command is running there?)";
        var temp = Path.Combine(gitDir, "HEAD.yawble-move");
        try
        {
            File.Delete(temp);
            File.WriteAllText(temp, $"ref: refs/heads/{branch}\n", new UTF8Encoding(false));
            File.Move(temp, head.FullName, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return "could not point the clone's HEAD at the new branch: " + e.Message;
        }
        return null;
    }

    /// <summary>
    /// The team's member branches in a repository, "branch sha" lines as for-each-ref gives them: members/&lt;team&gt;/*,
    /// never another team's, plus old-style members/&lt;name&gt; ones. <paramref name="legacyInUseOnly"/>: only the old-style
    /// ones a member of the team is still on (not yet moved), so a branch the move left behind is not synced or pushed again.
    /// </summary>
    private async Task<IReadOnlyList<(string Branch, string Sha)>> TeamMemberBranchesAsync(long teamId, string team, string path, bool legacyInUseOnly,
        CancellationToken ct)
    {
        var own = MemberBranchPrefix(team);
        var inUse = legacyInUseOnly ? LegacyBranchesInUse(teamId) : null;
        return (await Git.RunAsync(path, ct, "for-each-ref", "--format=%(refname:short) %(objectname)", "refs/heads/" + BranchPrefix))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', 2))
            .Where(p => p.Length == 2)
            .Select(p => (Branch: p[0], Sha: p[1]))
            .Where(b => b.Branch.StartsWith(own, StringComparison.Ordinal)
                || IsLegacyMemberBranch(b.Branch) && (inUse is null || inUse.Contains(b.Branch)))
            .ToList();
    }

    private HashSet<string> LegacyBranchesInUse(long teamId)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT mc.branch FROM member_clones mc JOIN members m ON m.id = mc.member_id WHERE m.team_id = $t "
            + "UNION SELECT x.branch FROM member_extra_clones x JOIN members m ON m.id = x.member_id WHERE m.team_id = $t;";
        cmd.Parameters.AddWithValue("$t", teamId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (IsLegacyMemberBranch(r.GetString(0))) set.Add(r.GetString(0));
        return set;
    }

    private static bool IsObjectId(string s) => s.Length is 40 or 64 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
