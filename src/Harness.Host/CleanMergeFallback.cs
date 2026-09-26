namespace Harness.Host;

/// <summary>
/// WHAT THE REBASE DOES WHEN IT WOULD CONFLICT BUT A MERGE WOULD NOT.
///
/// A team resolves a conflicting rebase by merging origin/main into main, as the Ask instruction
/// tells it to, and origin/main then moves on by unrelated commits. Rebasing drops merge commits
/// and replays the team's own commits, so it meets the conflicts the team already resolved and
/// refuses; a merge of the new commits is clean, so there are no files to hand back either.
/// Without this fallback the dialog is stuck.
///
/// A MERGE IS SAFE HERE. It rewrites nothing, and afterwards origin/main is an ancestor of main,
/// which is all Push and Merge to main need. Only a merge that git can complete without a conflict
/// is taken; anything else changes nothing.
/// </summary>
public static class CleanMergeFallback
{
    /// <summary>
    /// Merges origin/&lt;branch&gt; into the checked-out default branch when that merge is clean.
    /// True when it merged. The caller has already refused a dirty tree and a HEAD that is not
    /// <paramref name="branch"/>, the repository's stored default branch.
    /// </summary>
    public static async Task<bool> TryAsync(
        GitRunner git, string clonePath, string branch, CancellationToken ct, string remote = ContributorRemotes.Origin)
    {
        // `upstream` in contributor mode, which is what the rebase it stands in for went onto.
        var origin = $"{remote}/{branch}";

        // Asked first, writing nothing: merge-tree exits 0 only for a merge without conflicts.
        var probe = await git.MergeTreeAsync(clonePath, branch, origin, ct);
        if (probe.ExitCode != 0) return false;

        var merge = await git.RunGitAsync(
            clonePath, ["merge", "--no-edit", "-m", $"Merge {origin} into {branch}", origin], ct);
        if (merge.ExitCode == 0) return true;

        // It should not fail after a clean probe; if it does, put the clone back exactly as it was.
        await git.RunGitAsync(clonePath, ["merge", "--abort"], CancellationToken.None);
        return false;
    }
}
