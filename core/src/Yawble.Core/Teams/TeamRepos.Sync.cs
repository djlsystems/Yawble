namespace Yawble.Core.Teams;

/// <summary>
/// What bringing a team's branches up to its base branch did. <c>Outcome</c>: <c>off</c> (the team's setting is
/// off: nothing was touched), <c>no-base</c> (the repository has no base branch yet), <c>in-step</c> (nothing was behind),
/// <c>synced</c> (<c>Moved</c> names the branches that moved) or <c>conflict</c> (the team branch could not be brought up:
/// <c>Conflicts</c> names the files, <c>Note</c> is what the Manager's first instruction says and what Needs You shows).
/// </summary>
public sealed record BranchSyncResult(string Outcome, string BaseBranch, string? BaseSha, int Behind,
    IReadOnlyList<string> Moved, IReadOnlyList<string> Conflicts, string? Note)
{
    public static BranchSyncResult Off(string baseBranch) => new("off", baseBranch, null, 0, [], [], null);
}

/// <summary>
/// Sync before new work: before a workflow's first run the host brings the team branch, and every member
/// branch, up to the base branch INSIDE THE HOST-OWNED team repository, then refreshes the shared view. Member clones
/// are never touched (no git runs inside a clone): a clone fetches its synced branch from the bundle, or is made from it.
/// The merge is computed in the object database (<c>merge-tree --write-tree</c>) and the branch moved with a
/// compare-and-swap <c>update-ref</c>, so a conflict changes nothing; nothing is rebased, forced or pushed, and the base
/// branch and the person's checkout never move.
/// </summary>
public sealed partial class TeamRepos
{
    /// <summary>Per team, on by default (0034): bring branches up to the base before new work.</summary>
    public bool SyncsToBase(long teamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sync_to_base FROM teams WHERE id = $t;";
        cmd.Parameters.AddWithValue("$t", teamId);
        return cmd.ExecuteScalar() is not long v || v != 0;
    }

    public void SetSyncsToBase(long teamId, bool on)
    {
        Exec("UPDATE teams SET sync_to_base = $v WHERE id = $t;", ("$v", on ? 1 : 0), ("$t", teamId));
        if (!on) SetSyncNote(teamId, null); // nothing is synced, so there is nothing to be asked about
    }

    /// <summary>The note a conflicting sync left for the team (null when there is none).</summary>
    public string? SyncNote(long teamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sync_note FROM teams WHERE id = $t;";
        cmd.Parameters.AddWithValue("$t", teamId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>The teams whose last sync conflicted: each is a Needs You item.</summary>
    public IReadOnlyList<(long TeamId, string Team, string Note)> SyncNotes()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, sync_note FROM teams WHERE sync_note IS NOT NULL AND archived_at IS NULL ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, string)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    private void SetSyncNote(long teamId, string? note) =>
        Exec("UPDATE teams SET sync_note = $n WHERE id = $t;", ("$n", (object?)note ?? DBNull.Value), ("$t", teamId));

    /// <summary>Adds a line to the team's note (a fetch-back that could not bring a clone's work in), once.</summary>
    private void AddSyncNote(long teamId, string line)
    {
        var note = SyncNote(teamId);
        if (note is not null && note.Contains(line, StringComparison.Ordinal)) return;
        SetSyncNote(teamId, note is null ? line : note + "\n" + line);
    }

    /// <summary>The "Sync now" button: the same sync, on demand, whatever the team's setting says.</summary>
    public Task<BranchSyncResult> SyncNowAsync(long teamId, string team, CancellationToken ct = default) =>
        SyncBranchesAsync(teamId, team, "now", ct, force: true);

    /// <summary>
    /// Brings the team branch and the member branches up to the base branch (see the type's remarks).
    /// <paramref name="label"/> names the workflow in the merge commit: "Sync main into team/x before workflow #N".
    /// With the team's setting off (and not <paramref name="force"/>) nothing is touched.
    /// </summary>
    public async Task<BranchSyncResult> SyncBranchesAsync(long teamId, string team, string label, CancellationToken ct = default, bool force = false)
    {
        var repo = await EnsureTeamRepoAsync(teamId, team, ct);
        if (!force && !SyncsToBase(teamId)) return BranchSyncResult.Off(repo.BaseBranch);
        return await WithTeamLock(teamId, () => SyncLockedAsync(repo, team, label, ct), ct);
    }

    private async Task<BranchSyncResult> SyncLockedAsync(TeamRepo repo, string team, string label, CancellationToken ct)
    {
        var path = repo.Path;
        var baseName = repo.BaseBranch;
        if (await OriginUrlAsync(path, ct) is not null)
        {
            if (AppCloned(path)) await RefreshFromOriginAsync(path, baseName, ct);
            else
            {
                var fetched = await Pusher.RunAsync(path, ["fetch", "-q", "--no-tags", "origin"], ct);
                if (fetched.ExitCode != 0) _log?.Invoke($"team {team}: could not fetch origin before the sync: {PersonGitPush.Redact(fetched.Stderr)}");
            }
        }
        var local = await RevAsync(path, "refs/heads/" + baseName, ct);
        var remote = await OriginUrlAsync(path, ct) is null ? null : await RevAsync(path, "refs/remotes/origin/" + baseName, ct);
        // The same starting point as Merge to main: origin's base when the local one is only behind it. The person's own
        // base branch never moves here.
        var target = remote is not null && (local is null || local == remote || await IsAncestorAsync(path, local, remote, ct)) ? remote : local;
        if (target is null) return new BranchSyncResult("no-base", baseName, null, 0, [], [], null);

        var teamBranch = TeamBranchFor(team);
        var branches = new List<string>();
        if (await RevAsync(path, "refs/heads/" + teamBranch, ct) is not null) branches.Add(teamBranch);
        branches.AddRange((await Git.RunAsync(path, ct, "for-each-ref", "--format=%(refname:short)", "refs/heads/" + BranchPrefix))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var checkedOut = await CurrentBranchAsync(path, ct);
        var moved = new List<string>();
        var behindTeam = 0;
        IReadOnlyList<string> conflicts = [];
        var teamConflict = false;
        foreach (var branch in branches)
        {
            if (branch == checkedOut)
            {
                _log?.Invoke($"team {team}: {branch} is checked out in {path}; not synced");
                continue;
            }
            var (outcome, behind, files) = await BringBranchUpAsync(path, branch, target, $"Sync {baseName} into {branch} before workflow #{label}", ct);
            if (outcome == SyncStep.Moved) moved.Add(branch);
            if (branch == teamBranch && outcome == SyncStep.Conflict)
            {
                teamConflict = true;
                behindTeam = behind;
                conflicts = files;
            }
            else if (outcome == SyncStep.Conflict)
            {
                _log?.Invoke($"team {team}: {branch} does not merge cleanly with {baseName} ({string.Join(", ", files)}); left as it was");
            }
        }

        // The shared view and its bundle always follow the repository, so members fetch what the host now has.
        await TryUpdateSharedLockedAsync(team, repo, ct);

        // Clone work an earlier fetch-back could not bring in (it conflicts with its synced branch) is still said, until
        // the member merges and the next fetch-back lands it.
        var parked = new List<string>(await ParkedNotesAsync(repo.TeamId, path, ct));
        foreach (var extra in ExtrasOf(repo.TeamId).Where(e => e.Writable && Directory.Exists(e.Path)))
            parked.AddRange(await ParkedNotesAsync(repo.TeamId, extra.Path, ct));
        string? Joined(string? first) => string.Join('\n', new[] { first }.Concat(parked).Where(n => n is not null)) is { Length: > 0 } j ? j : null;

        if (teamConflict)
        {
            var note = $"{teamBranch} is {behindTeam} commit{(behindTeam == 1 ? "" : "s")} behind {baseName} and could not be synced automatically "
                + $"(conflicts in: {string.Join(", ", conflicts)}). Merge {baseName} into it first and resolve the conflicts before other work.";
            SetSyncNote(repo.TeamId, Joined(note));
            _log?.Invoke($"team {team}: {note}");
            return new BranchSyncResult("conflict", baseName, target, behindTeam, moved, conflicts, Joined(note));
        }
        SetSyncNote(repo.TeamId, Joined(null));
        if (moved.Count > 0) _log?.Invoke($"team {team}: brought {string.Join(", ", moved)} up to {baseName} at {Short(target)}");
        return new BranchSyncResult(moved.Count > 0 ? "synced" : "in-step", baseName, target, 0, moved, [], Joined(null));
    }

    private enum SyncStep { InStep, Moved, Conflict, Raced }

    /// <summary>
    /// Brings one branch up to <paramref name="target"/>: nothing when it has it; a fast-forward when the branch has no
    /// work of its own; otherwise a merge commit, only when the merge is clean. Never touches a file or ref on a conflict.
    /// </summary>
    private async Task<(SyncStep Outcome, int Behind, IReadOnlyList<string> Files)> BringBranchUpAsync(
        string path, string branch, string target, string message, CancellationToken ct)
    {
        var sha = await RevAsync(path, "refs/heads/" + branch, ct);
        if (sha is null) return (SyncStep.InStep, 0, []);
        if (await IsAncestorAsync(path, target, sha, ct)) return (SyncStep.InStep, 0, []);
        var behind = await CountAsync(path, target, sha, ct);
        string next;
        if (await IsAncestorAsync(path, sha, target, ct)) next = target;
        else
        {
            var (tree, files) = await MergeTreeAsync(path, sha, target, ct);
            if (tree is null) return (SyncStep.Conflict, behind, files);
            next = await Git.RunAsync(path, ct, "-c", "user.name=" + ManagedIdentityName, "-c", "user.email=" + ManagedIdentityEmail,
                "commit-tree", tree, "-p", sha, "-p", target, "-m", message);
        }
        var update = await Git.TryRunAsync(path, ["update-ref", "-m", "branch sync", "refs/heads/" + branch, next, sha], ct);
        return update.ExitCode == 0 ? (SyncStep.Moved, behind, []) : (SyncStep.Raced, behind, []);
    }

    /// <summary>
    /// The base-branch commit a member's branch was built on: the newest base commit the branch contains (as the team
    /// repository has it), or the base branch's tip when the member has no branch yet (its clone is made from the base).
    /// </summary>
    public async Task<string?> CloneBaseCommitAsync(string repoPath, string baseBranch, string memberBranch, CancellationToken ct)
    {
        return await CloneBaseCommitAsync(repoPath, baseBranch, memberBranch, clonePath: null, ct);
    }

    /// <summary>
    /// The base-branch commit the member's CLONE is on: its checked-out tip (read from its .git/HEAD file and with
    /// <c>ls-remote</c> through the strict upload-pack, never by running git in the clone), as the team repository knows
    /// it, merged-base with the base branch. Falls back to the team repository's view of the member's branch when the
    /// clone's tip cannot be read or is not in the team repository yet.
    /// </summary>
    internal async Task<string?> CloneBaseCommitAsync(string repoPath, string baseBranch, string memberBranch, string? clonePath, CancellationToken ct)
    {
        if (clonePath is not null && await CloneTipAsync(repoPath, clonePath, ct) is { } tip)
        {
            var at = await Git.TryRunAsync(repoPath, ["merge-base", tip, "refs/heads/" + baseBranch], ct);
            if (at.ExitCode == 0 && at.Stdout.Length >= 40) return at.Stdout.Trim();
        }
        return await TeamViewBaseCommitAsync(repoPath, baseBranch, memberBranch, ct);
    }

    /// <summary>The clone's checked-out commit when the host repository has it (null otherwise). No git runs in the clone.</summary>
    private async Task<string?> CloneTipAsync(string repoPath, string clonePath, CancellationToken ct)
    {
        if (CloneGitDirProblem(clonePath) is not null) return null;
        var gitDir = Path.Combine(clonePath, ".git");
        string? sha;
        switch (Concierge.CloneHead.Read(gitDir))
        {
            case { Detached: { } detached }:
                sha = detached;
                break;
            case { Branch: { } branch }:
                var listed = await Git.TryRunAsync(repoPath, ["ls-remote", StrictUploadPack, "--", gitDir, "refs/heads/" + branch], ct);
                sha = listed.ExitCode == 0 ? listed.Stdout.Split('\t', 2)[0].Trim() : null;
                break;
            default:
                return null;
        }
        return string.IsNullOrEmpty(sha) ? null : await RevAsync(repoPath, sha, ct);
    }

    private async Task<string?> TeamViewBaseCommitAsync(string repoPath, string baseBranch, string memberBranch, CancellationToken ct)
    {
        var branch = await RevAsync(repoPath, "refs/heads/" + memberBranch, ct);
        if (branch is null) return await BaseCommitAsync(repoPath, baseBranch, ct);
        var mb = await Git.TryRunAsync(repoPath, ["merge-base", branch, "refs/heads/" + baseBranch], ct);
        return mb.ExitCode == 0 && mb.Stdout.Length >= 40 ? mb.Stdout.Trim() : null;
    }
}
