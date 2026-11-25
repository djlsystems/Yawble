using System.Text.RegularExpressions;
using Yawble.Core.Monitor;

namespace Yawble.Core.Teams;

/// <summary>
/// The commits behind every count and the previews of Merge to main and Delete, for the Git dialog
/// (git.commitRange, git.mergePreview, git.mergeDiff, git.deletePreview), over <see cref="GitPreviews"/>. Reads only.
/// A deleted branch's commits are recorded (code events of kind branch-deleted), so the commit view still opens them.
/// </summary>
public sealed partial class TeamRepos
{
    private GitPreviews? _previews;

    /// <summary>The lists and previews, shared with the Concierge's tools.</summary>
    public GitPreviews Previews => _previews ??= new GitPreviews(Git);

    private static readonly Regex RangeRefPattern = new(@"^refs/(heads|remotes/origin)/[A-Za-z0-9._/\-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex FullObjectIdPattern = new("^([0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant);

    /// <summary>git.commitRange: the commits on <paramref name="to"/> that <paramref name="from"/> lacks (the pair a count gives).</summary>
    public async Task<CommitRange> GitCommitRangeAsync(long teamId, string team, string from, string to, int? limit = null, CancellationToken ct = default,
        string? repoName = null)
    {
        var repo = await GitRepoAsync(teamId, team, repoName, ct);
        var fromSha = await RangeEndAsync(repo.Path, team, from, ct);
        var toSha = await RangeEndAsync(repo.Path, team, to, ct);
        var list = await Previews.ListAsync(repo.Path, toSha, [fromSha], limit ?? GitPreviews.DefaultLimit, ct);
        return new CommitRange(from.Trim(), to.Trim(), fromSha, toSha, list.Count, list.Truncated, await Previews.HasOriginAsync(repo.Path, ct), list.Commits);
    }

    /// <summary>A range end: a commit ID (as the commit view reads one: an object, never a ref) or refs/heads/x, refs/remotes/origin/x.</summary>
    private async Task<string> RangeEndAsync(string path, string team, string end, CancellationToken ct)
    {
        var e = (end ?? "").Trim();
        if (e.Length == 0) throw new ArgumentException("from and to are required");
        if (CommitIdPattern.IsMatch(e))
        {
            var (full, ambiguous) = await ResolveCommitAsync(path, e.ToLowerInvariant(), ct);
            return full ?? throw new TeamGitRefusal("gone", GoneReason(team, e.ToLowerInvariant(), ambiguous));
        }
        if (!RangeRefPattern.IsMatch(e) || e.Contains("..", StringComparison.Ordinal) || e.EndsWith('/') || e.EndsWith(".lock", StringComparison.Ordinal))
            throw new ArgumentException($"'{end}' is not a commit ID or a refs/heads/ or refs/remotes/origin/ branch");
        return await RevAsync(path, e, ct) ?? throw new TeamGitRefusal("gone", $"there is no {e} in the team's repository");
    }

    /// <summary>git.mergePreview: what Merge to main would do now (nothing moves).</summary>
    public async Task<MergePreview> GitMergePreviewAsync(long teamId, string team, CancellationToken ct = default, string? repoName = null)
    {
        var repo = await GitRepoAsync(teamId, team, repoName, ct);
        return await WithTeamLock(teamId, () => Previews.MergePreviewAsync(repo.Path, repo.BaseBranch, TeamBranchFor(team), ct,
            dirty: async c => (await DirtyCheckoutAsync(repo, c))?.Message), ct);
    }

    /// <summary>git.mergeDiff: one file of a merge preview's combined diff (<paramref name="from"/>/<paramref name="to"/>: its diffFrom/diffTo).</summary>
    public async Task<CommitFileDiff> GitMergeDiffAsync(long teamId, string team, string from, string to, string file, CancellationToken ct = default,
        string? repoName = null)
    {
        if (string.IsNullOrEmpty(file)) throw new ArgumentException("path is required: the file of the diff to show");
        var repo = await GitRepoAsync(teamId, team, repoName, ct);
        foreach (var id in new[] { from, to })
        {
            if (!FullObjectIdPattern.IsMatch((id ?? "").Trim().ToLowerInvariant())) throw new ArgumentException($"'{id}' is not a full object ID (diffFrom / diffTo of git.mergePreview)");
            var kind = await Git.TryRunAsync(repo.Path, ["cat-file", "-t", id!.Trim().ToLowerInvariant()], ct);
            if (kind.ExitCode != 0 || kind.Stdout.Trim() is not ("commit" or "tree"))
                throw new TeamGitRefusal("gone", $"git no longer has {GitPreviews.Short(id.Trim())} in the team's repository: open the preview again");
        }
        return await Previews.FileDiffAsync(repo.Path, from.Trim().ToLowerInvariant(), to.Trim().ToLowerInvariant(), file, ct);
    }

    /// <summary>git.deletePreview: what deleting the local branch loses.</summary>
    public async Task<DeletePreview> GitDeletePreviewAsync(long teamId, string team, string branch, CancellationToken ct = default, string? repoName = null)
    {
        var b = (branch ?? "").Trim();
        if (b.Length == 0 || b.StartsWith('-') || b.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("branch is required: the local branch to preview");
        var repo = await GitRepoAsync(teamId, team, repoName, ct);
        return await WithTeamLock(teamId, () => Previews.DeletePreviewAsync(repo.Path, repo.BaseBranch, b, ct), ct);
    }

    /// <summary>
    /// After a branch was deleted (the Git dialog's Delete, the Concierge's git_branches_delete): its tip and every commit
    /// not in the base, as branch-deleted code events, so the commit view still says where they were. The primary
    /// repository only (as every code event). Never throws.
    /// </summary>
    public Task RecordBranchDeletedAsync(long teamId, string team, string repoPath, string baseBranch, string branch, string sha, CancellationToken ct = default) =>
        SafeRecordAsync(team, async () =>
        {
            if (!IsPrimaryRepo(teamId, repoPath)) return [];
            var now = _clock.GetUtcNow();
            var local = await RevAsync(repoPath, "refs/heads/" + baseBranch, ct);
            var remote = await RevAsync(repoPath, "refs/remotes/origin/" + baseBranch, ct);
            var revs = new List<string> { sha };
            if (local is not null) revs.Add("^" + local);
            if (remote is not null) revs.Add("^" + remote);
            var member = branch.StartsWith(BranchPrefix, StringComparison.Ordinal) ? MemberOfBranch(teamId, branch) : null;
            var list = new List<CodeEvent>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in await LogAsync(repoPath, revs, ct))
                if (seen.Add(c.Sha)) list.Add(new CodeEvent(CodeEvent.BranchDeleted, team, member, branch, c.Sha, c.Subject, [], now));
            if (seen.Add(sha))
            {
                var subject = (await LogAsync(repoPath, ["-1", sha], ct)).FirstOrDefault()?.Subject ?? "";
                list.Add(new CodeEvent(CodeEvent.BranchDeleted, team, member, branch, sha, subject, [], now));
            }
            return list;
        }, $"the delete of {branch}");
}
