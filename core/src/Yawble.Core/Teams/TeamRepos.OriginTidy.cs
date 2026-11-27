namespace Yawble.Core.Teams;

public sealed partial class TeamRepos
{
    /// <summary>
    /// Whether <paramref name="sha"/> adds nothing to <paramref name="baseSha"/>: the base contains it, or its only
    /// commits the base lacks are merges (the app's "Sync main into ..." before new work) and merging it into the base
    /// changes no file.
    /// </summary>
    private async Task<bool> AddsNothingToAsync(string path, string sha, string baseSha, CancellationToken ct)
    {
        if (sha == baseSha || await IsAncestorAsync(path, sha, baseSha, ct)) return true;
        var own = await Git.TryRunAsync(path, ["rev-list", "--count", "--no-merges", sha, "^" + baseSha], ct);
        if (own.ExitCode != 0 || own.Stdout != "0") return false;
        var (tree, _) = await MergeTreeAsync(path, baseSha, sha, ct);
        if (tree is null) return false;
        var baseTree = await Git.TryRunAsync(path, ["rev-parse", "--verify", "-q", baseSha + "^{tree}"], ct);
        return baseTree.ExitCode == 0 && baseTree.Stdout == tree;
    }

    /// <summary>Whether a branch is this team's own: its team branch, or a branch of one of its members.</summary>
    private static bool IsTeamsOwnBranch(string team, string branch) =>
        string.Equals(branch, TeamBranchFor(team), StringComparison.Ordinal) || branch.StartsWith(MemberBranchPrefix(team), StringComparison.Ordinal);

    /// <summary>
    /// Deletes from origin the team's own branches (team/&lt;team&gt;, members/&lt;team&gt;/*) that add nothing to origin's base
    /// branch any more: their work is merged. A branch whose copy in the team repository holds work origin's base lacks
    /// is kept (that work is pushed, not deleted). Each delete is a compare-and-delete on the commit origin was last seen
    /// at, so a branch somebody moved meanwhile stays; the commit is logged, and the branch stays in the team repository
    /// and the members' clones. Never the base branch, never another team's branch. The caller holds the team lock.
    /// Returns the branches deleted; a failed delete is logged and never fails the caller.
    /// </summary>
    private async Task<IReadOnlyList<string>> DeleteMergedOnOriginLockedAsync(string team, string repoPath, string baseBranch, CancellationToken ct)
    {
        var deleted = new List<string>();
        try
        {
            if (await RevAsync(repoPath, "refs/remotes/origin/" + baseBranch, ct) is not { } originBase) return deleted;
            const string prefix = "refs/remotes/origin/";
            var listed = await Git.RunAsync(repoPath, ct, "for-each-ref", "--format=%(refname) %(objectname)", prefix);
            foreach (var line in listed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', 2);
                if (parts.Length != 2 || !parts[0].StartsWith(prefix, StringComparison.Ordinal)) continue;
                var (branch, onOrigin) = (parts[0][prefix.Length..], parts[1].Trim());
                if (!IsTeamsOwnBranch(team, branch) || NeverPublished.Contains(branch) || string.Equals(branch, baseBranch, StringComparison.Ordinal)) continue;
                if (!await AddsNothingToAsync(repoPath, onOrigin, originBase, ct)) continue;
                if (await RevAsync(repoPath, "refs/heads/" + branch, ct) is { } here && !await AddsNothingToAsync(repoPath, here, originBase, ct)) continue;

                var run = await Pusher.RunAsync(repoPath, ["push", "origin", $"--force-with-lease=refs/heads/{branch}:{onOrigin}", $":refs/heads/{branch}"], ct);
                if (run.ExitCode != 0)
                {
                    var said = PersonGitPush.Redact(string.Join('\n', run.Stderr, run.Stdout).Trim());
                    _log?.Invoke($"team {team}: {branch} is merged into {baseBranch} but was not deleted on origin: {said}");
                    continue;
                }
                await Git.TryRunAsync(repoPath, ["update-ref", "-d", parts[0], onOrigin], ct);
                deleted.Add(branch);
                _log?.Invoke($"team {team}: deleted {branch} on origin (it was at {onOrigin}): its work is in {baseBranch}");
            }
        }
        catch (Exception e) when (e is GitException or IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"team {team}: merged branches on origin were not tidied: {PersonGitPush.Redact(e.Message)}");
        }
        return deleted;
    }
}
