namespace Yawble.Core.Teams;

/// <summary>How far a team's branch is behind its base branch. <c>Age</c>: how old the oldest base commit it lacks is.</summary>
public sealed record TeamBehind(string TeamBranch, string BaseBranch, int Behind, TimeSpan? Age)
{
    /// <summary>"team/x is 3 commits behind main" (what the team page, team_status and system_status say).</summary>
    public string Text => $"{TeamBranch} is {Behind} commit{(Behind == 1 ? "" : "s")} behind {BaseBranch}";
}

/// <summary>Whether the team branch merges cleanly into the base branch (checked before a workflow is declared complete).</summary>
public sealed record TeamMergeCheck(string TeamBranch, string BaseBranch, bool Clean, IReadOnlyList<string> Files)
{
    /// <summary>"team/x does not merge cleanly into main (a.txt, b.txt)".</summary>
    public string Text => $"{TeamBranch} does not merge cleanly into {BaseBranch} ({string.Join(", ", Files)})";
}

/// <summary>
/// Watching: read-only questions about the team branch and its base, asked inside the host-owned team repository.
/// Nothing here fetches, moves a ref or writes a file; a team with no repository or no team branch yet has no answer (null).
/// </summary>
public sealed partial class TeamRepos
{
    /// <summary>
    /// What the team branch lacks of the base branch. The base is read as Merge to main reads it: origin's copy (as of the last
    /// fetch) when the local base is only behind it. Null when the team has no repository, no team branch, or no base yet.
    /// </summary>
    public async Task<TeamBehind?> BehindBaseAsync(long teamId, string team, DateTimeOffset now, CancellationToken ct = default)
    {
        if (RepoOf(teamId) is not { } repo) return null;
        return await WithTeamLock(teamId, async () =>
        {
            var (teamBranch, teamSha, start) = await TeamAndStartAsync(repo, team, ct);
            if (teamSha is null || start is null) return null;
            var behind = await CountAsync(repo.Path, start, teamSha, ct);
            if (behind == 0) return new TeamBehind(teamBranch, repo.BaseBranch, 0, null);
            // The oldest base commit the team branch lacks: its committer date.
            var log = await Git.TryRunAsync(repo.Path, ["log", "--format=%ct", start, "^" + teamSha], ct);
            var oldest = log.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => long.TryParse(l.Trim(), out var s) ? s : (long?)null).Where(s => s is not null).Select(s => s!.Value).DefaultIfEmpty(-1).Min();
            TimeSpan? age = oldest < 0 ? null : now - DateTimeOffset.FromUnixTimeSeconds(oldest);
            return new TeamBehind(teamBranch, repo.BaseBranch, behind, age);
        }, ct);
    }

    /// <summary>
    /// Whether the team branch merges cleanly into the base branch (computed in the object database: nothing changes). Null when
    /// there is no team branch or base to compare. Runs whatever the team's sync setting says: it only reports.
    /// </summary>
    public async Task<TeamMergeCheck?> MergeCheckAsync(long teamId, string team, CancellationToken ct = default)
    {
        if (RepoOf(teamId) is not { } repo) return null;
        return await WithTeamLock(teamId, async () =>
        {
            var (teamBranch, teamSha, start) = await TeamAndStartAsync(repo, team, ct);
            if (teamSha is null || start is null) return null;
            if (await IsAncestorAsync(repo.Path, teamSha, start, ct) || await IsAncestorAsync(repo.Path, start, teamSha, ct))
                return new TeamMergeCheck(teamBranch, repo.BaseBranch, true, []);
            var (tree, files) = await MergeTreeAsync(repo.Path, start, teamSha, ct);
            return new TeamMergeCheck(teamBranch, repo.BaseBranch, tree is not null, files);
        }, ct);
    }

    /// <summary>
    /// The line team_status / system_status carry: "team/x is 3 commits behind main" (with what the team's sync setting makes of it),
    /// or null when the branch is in step or does not exist. Never throws: a status read must not fail on a repository.
    /// </summary>
    public async Task<string?> BehindLineAsync(long teamId, string team, CancellationToken ct = default)
    {
        try
        {
            if (await BehindBaseAsync(teamId, team, DateTimeOffset.UtcNow, ct) is not { Behind: > 0 } b) return null;
            return b.Text + (SyncsToBase(teamId) ? "; the app syncs it before new work, or press Sync now" : "; syncing before new work is off for this team, so press Sync now");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<(string TeamBranch, string? TeamSha, string? Start)> TeamAndStartAsync(TeamRepo repo, string team, CancellationToken ct)
    {
        var teamBranch = TeamBranchFor(team);
        var teamSha = await RevAsync(repo.Path, "refs/heads/" + teamBranch, ct);
        var local = await RevAsync(repo.Path, "refs/heads/" + repo.BaseBranch, ct);
        var remote = await OriginUrlAsync(repo.Path, ct) is null ? null : await RevAsync(repo.Path, "refs/remotes/origin/" + repo.BaseBranch, ct);
        var start = remote is not null && (local is null || local == remote || await IsAncestorAsync(repo.Path, local, remote, ct)) ? remote : local;
        return (teamBranch, teamSha, start);
    }
}

