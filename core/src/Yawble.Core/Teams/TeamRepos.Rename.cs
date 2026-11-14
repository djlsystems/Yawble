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
/// <item>Except when the old branch is a parent path of the new one (team "Docs", member "Docs": members/docs to
///   members/docs/docs, card 104): git cannot keep both, so the old branch is renamed (repository, clone) and, on origin,
///   replaced by the new one at the push after the run when the new one holds all its commits.</item>
/// </list>
/// Anything that would make the move unsafe (a clone .git the host may not read, a link on the ref path, a name clash with
/// an existing ref here, in the clone or on origin's side: B020) leaves the member on its old branch, with the reason logged.
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
        // Card 104: the old branch is a parent path of the new one (team "Docs", member "Docs": members/docs to
        // members/docs/docs). Git cannot keep both, so here, and only here, the old branch is renamed, not kept: in the
        // repository by git branch -m, in the clone by file (MoveCloneRefThrough), and on origin by the push after the run.
        var through = to.StartsWith(from + "/", StringComparison.Ordinal);
        if (through && RepoUsers(repoPath).FirstOrDefault(u => u.Branch == from && !string.Equals(NormalPath(u.ClonePath), NormalPath(clonePath), PathComparison)) is { } other)
            return $"{from} is also the branch of team {other.Team}'s member {other.Member}";
        if (cloneHasNew && newTip != cloneTip) return $"the clone already has {to} at another commit than {from}";
        if (!cloneHasNew && await RefClashAsync(repoPath, gitDir, to, through ? from : null, ct) is { } cloneClash) return $"the clone already has {cloneClash}";
        // B020: the same check for the new name on origin's side, before anything moves (the push would fail later). The
        // member's own old branch there is replaced by the push after the run (PublishRepoAsync), when nobody else is on it.
        if ((await BlockersOnOriginAsync(repoPath, to, ct)).FirstOrDefault(b => !(through && b == from)) is { } onOrigin)
            return $"origin already has {onOrigin} (as of the last fetch)";

        // The host repository: the new branch at the old one's commit there (it holds every fetched-back run and any sync
        // since). Clone work it does not have yet, or that was parked, lands on the new name at the next fetch-back, by the
        // usual rules. No old branch here means no work fetched back yet: the next fetch-back makes the new one.
        var renamedHere = false;
        if (await RevAsync(repoPath, "refs/heads/" + from, ct) is { } oldHere && await RevAsync(repoPath, "refs/heads/" + to, ct) is null)
        {
            if (await RefClashInRepoAsync(repoPath, to, through ? from : null, ct) is { } clash) return $"the repository already has {clash}";
            var made = through
                ? await Git.TryRunAsync(repoPath, ["branch", "-m", from, to], ct)
                : await Git.TryRunAsync(repoPath, ["update-ref", "-m", $"move {from} to {to}", "refs/heads/" + to, oldHere, ""], ct);
            if (made.ExitCode != 0) return "could not make the new branch in the repository: " + made.Stderr;
            renamedHere = through;
        }

        if (through)
        {
            // The clone, by file, all or nothing (HEAD included); on failure the repository's rename is undone too.
            var refused = cloneTip.Length == 0 ? null : MoveCloneRefThrough(gitDir, from, to, cloneTip);
            if (refused is null && cloneTip.Length == 0 && CloneHead.Read(gitDir)?.Branch == from) refused = WriteHead(gitDir, to);
            if (refused is null) return null;
            if (renamedHere)
            {
                var back = await Git.TryRunAsync(repoPath, ["branch", "-m", to, from], ct);
                if (back.ExitCode != 0) refused += $"; and the repository's {to} could not be named back to {from}: {back.Stderr}";
            }
            return refused;
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
    private async Task<string?> RefClashAsync(string repoPath, string gitDir, string branch, string? except, CancellationToken ct)
    {
        var all = await Git.TryRunAsync(repoPath, ["ls-remote", "--heads", StrictUploadPack, "--", gitDir], ct);
        return all.ExitCode != 0 ? "branches the host could not read: " + all.Stderr : Clash(all.Stdout.Split('\n')
            .Select(l => l.Split('\t', 2)).Where(p => p.Length == 2).Select(p => p[1].Trim()), branch, except);
    }

    private async Task<string?> RefClashInRepoAsync(string repoPath, string branch, string? except, CancellationToken ct) =>
        Clash((await Git.RunAsync(repoPath, ct, "for-each-ref", "--format=%(refname)", "refs/heads/")).Split('\n', StringSplitOptions.RemoveEmptyEntries), branch, except);

    /// <summary>The first ref that clashes with refs/heads/<paramref name="branch"/>, other than refs/heads/<paramref name="except"/>
    /// (the old branch a move goes through, card 104).</summary>
    private static string? Clash(IEnumerable<string> refs, string branch, string? except = null)
    {
        var wanted = "refs/heads/" + branch;
        foreach (var r in refs)
        {
            if (r == wanted || except is not null && r == "refs/heads/" + except) continue;
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
    /// Card 104: renames the clone's branch <paramref name="from"/> to <paramref name="to"/> under it (members/docs to
    /// members/docs/docs), by file, as <c>git branch -m</c> would, without running git in the clone. The old ref (a loose file
    /// and/or a packed-refs line, at <paramref name="tip"/>) goes, the new loose ref is written at the same commit, the reflog
    /// moves with it, and HEAD follows when it was on the old branch. Every path on the way must be a plain folder or file
    /// (never a link). All or nothing: on any failure what was changed is put back. Returns null, or why not.
    /// </summary>
    internal static string? MoveCloneRefThrough(string gitDir, string from, string to, string tip)
    {
        if (!CloneHead.IsBranchName(from) || !CloneHead.IsBranchName(to) || !to.StartsWith(from + "/", StringComparison.Ordinal))
            return $"{to} is not a branch under {from}";
        var refs = Path.Combine(gitDir, "refs");
        var heads = Path.Combine(refs, "heads");
        if (IsLink(new DirectoryInfo(refs)) || IsLink(new DirectoryInfo(heads)) || !Directory.Exists(heads))
            return "the clone's .git/refs/heads is missing or a link";
        var fromParts = from.Split('/');
        var rest = to[(from.Length + 1)..].Split('/');
        string? PlainPath(string root, IEnumerable<string> parts)
        {
            var dir = root;
            foreach (var part in parts)
            {
                dir = Path.Combine(dir, part);
                if (IsLink(new DirectoryInfo(dir)) || IsLink(new FileInfo(dir))) return null;
            }
            return dir;
        }
        if (PlainPath(heads, fromParts) is not { } loose) return "a folder on the clone's ref path is a link";
        if (Directory.Exists(loose)) return $"the clone already has branches under {from}";
        var hasLoose = File.Exists(loose);
        if (hasLoose && File.ReadAllText(loose).Trim() != tip) return $"the clone's {from} moved while it was read";

        var packed = Path.Combine(gitDir, "packed-refs");
        if (IsLink(new FileInfo(packed))) return "the clone's packed-refs is a link";
        var packedText = File.Exists(packed) ? File.ReadAllText(packed) : null;
        string? newPacked = null;
        if (packedText is not null)
        {
            var lines = packedText.Split('\n').ToList();
            var at = lines.FindIndex(l => l.TrimEnd('\r').EndsWith(" refs/heads/" + from, StringComparison.Ordinal) && l.Length > 0 && l[0] != '#' && l[0] != '^');
            if (at >= 0)
            {
                if (!hasLoose && lines[at].Split(' ', 2)[0] != tip) return $"the clone's {from} moved while it was read";
                lines.RemoveAt(at);
                if (at < lines.Count && lines[at].StartsWith('^')) lines.RemoveAt(at);
                newPacked = string.Join('\n', lines);
            }
        }
        if (!hasLoose && newPacked is null) return $"the clone's {from} was not found";

        if (PlainPath(gitDir, ["logs", "refs", "heads", .. fromParts]) is not { } log) return "a folder on the clone's reflog path is a link";
        var head = new FileInfo(Path.Combine(gitDir, "HEAD"));
        var moveHead = CloneHead.Read(gitDir)?.Branch == from;
        if (moveHead && (IsLink(head) || File.Exists(Path.Combine(gitDir, "HEAD.lock")))) return "the clone's HEAD is a link or locked";

        // git ignores ref files ending in .lock, so the old one set aside is no branch while the new one is written.
        var aside = loose + ".yawble-move.lock";
        var logAside = log + ".yawble-move.lock";
        var newLog = Path.Combine([log, .. rest]);
        bool asideMade = false, packedWritten = false, logMoved = false;
        try
        {
            if (Path.Exists(aside) || Path.Exists(logAside)) return "a leftover of an earlier move is in the clone's refs";
            if (hasLoose) { File.Move(loose, aside); asideMade = true; }
            if (newPacked is not null) { WriteReplacing(packed, newPacked); packedWritten = true; }
            if (WriteLooseRef(gitDir, to, tip) is { } refused) throw new IOException(refused);
            if (File.Exists(log))
            {
                File.Move(log, logAside);
                logMoved = true;
                Directory.CreateDirectory(Path.GetDirectoryName(newLog)!);
                File.Move(logAside, newLog);
            }
            if (moveHead && WriteHead(gitDir, to) is { } headRefused) throw new IOException(headRefused);
            if (asideMade) File.Delete(aside);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Put back what changed, newest first. HEAD was not changed (it is written last, by an atomic move).
            try
            {
                if (logMoved)
                {
                    if (File.Exists(newLog)) File.Move(newLog, logAside);
                    DeleteEmptyUpTo(Path.GetDirectoryName(newLog)!, Path.GetDirectoryName(log)!);
                    if (File.Exists(logAside)) File.Move(logAside, log);
                }
                var newRef = Path.Combine([loose, .. rest]);
                if (File.Exists(newRef)) File.Delete(newRef);
                DeleteEmptyUpTo(Path.GetDirectoryName(newRef)!, Path.GetDirectoryName(loose)!);
                if (packedWritten) WriteReplacing(packed, packedText!);
                if (asideMade) File.Move(aside, loose);
            }
            catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
            {
                return $"could not move the clone's branch ({e.Message}), and putting it back failed: {undo.Message}";
            }
            return "could not move the clone's branch: " + e.Message;
        }

        static void WriteReplacing(string file, string text)
        {
            var temp = file + ".yawble-move";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, file, overwrite: true);
        }

        // Removes empty folders from dir up to (not including) stop.
        static void DeleteEmptyUpTo(string dir, string stop)
        {
            while (dir.Length > stop.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir)!;
            }
        }
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
