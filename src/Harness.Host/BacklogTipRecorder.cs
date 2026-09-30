using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WRITES WHAT <see cref="BacklogLandedState"/> NEEDS TO OUTLIVE A TIDY-UP: the tip the
/// team's publish pushed, and landed at the moment the Git dialog's Merge to main lands the branch.
///
/// <para>
/// WHICH DISPATCH. The workflow the publish ran in, when that workflow is a dispatch's own - its
/// correlation is the dispatch's. Otherwise the team's most recent current dispatch above its floor,
/// which is the same attribution <see cref="BacklogLandedState"/> reads: one verdict per team,
/// shared by the items it holds.
/// </para>
///
/// <para>
/// CONTRIBUTOR MODE IS NOT RECORDED. Its origin is a fork, so a tip reachable from the fork's
/// default branch proves nothing about upstream - a merged pull request is the only proof there.
/// </para>
/// </summary>
public sealed class BacklogTipRecorder(
    IBacklogStore backlog, IMessageLog log, TeamRegistry teams, TeamPaths paths, GitRunner git)
{
    /// <summary>How long a dispatch waits for its starting point before going on without it.</summary>
    public static readonly TimeSpan BaseBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// WHERE THE DISPATCH STARTED, read when it is made: per repository, origin's default branch
    /// after a fetch (GH_TOKEN by the rule a fetch uses) and the team branch if it has one. Only
    /// work beyond both is this dispatch's own, and only that is ever stored as landed.
    ///
    /// <para>
    /// NOTHING IS RECORDED WHEN IT CANNOT BE READ - no clone yet, a fetch that failed, no default
    /// branch known. A stale origin would put the start too early and count earlier work as this
    /// dispatch's; with no start the item is still read live by the existing rules, it is only
    /// never stored. Contributor mode records nothing: a merged pull request is its only proof.
    /// It never fails a dispatch.
    /// </para>
    /// </summary>
    public async Task RecordBaseAsync(BacklogDispatch dispatch, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(BaseBudget);

        foreach (var url in teams.ReposFor(dispatch.TeamId))
        {
            try
            {
                var repo = RepoUrls.DeriveName(url);
                if (teams.ContributorFor(dispatch.TeamId, repo).ContributorMode) continue;
                if (teams.DefaultBranchFor(dispatch.TeamId, repo).Branch is not { } branch) continue;

                var clonePath = Path.Combine(paths.ReposFor(dispatch.TeamId), repo, "main");
                if (!Directory.Exists(clonePath)) continue;
                if ((await git.FetchAsync(clonePath, budget.Token)).ExitCode != 0) continue;

                if (await ShaAsync(clonePath, $"refs/remotes/origin/{branch}", budget.Token) is not { } defaultSha) continue;

                var teamSha = await ShaAsync(clonePath, $"refs/heads/team/{dispatch.TeamId}", budget.Token)
                    ?? await ShaAsync(clonePath, $"refs/remotes/origin/team/{dispatch.TeamId}", budget.Token);

                await backlog.RecordBaseAsync(dispatch.Id, repo, defaultSha, teamSha, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is UriFormatException or KeyNotFoundException or IOException)
            {
                // Nothing recorded for this repository; it reads live by the existing rules.
            }
        }
    }

    private async Task<string?> ShaAsync(string clonePath, string reference, CancellationToken ct)
    {
        var parsed = await git.RunGitAsync(clonePath, ["rev-parse", "--verify", "--quiet", reference + "^{commit}"], ct);
        return parsed.ExitCode == 0 ? parsed.Stdout.Trim() : null;
    }

    /// <summary>Records <paramref name="sha"/> as the tip of <paramref name="repo"/> on the dispatch the publish belongs to.</summary>
    public async Task RecordTipAsync(string team, string repo, string sha, long? causation, CancellationToken ct)
    {
        if (teams.ContributorFor(team, repo).ContributorMode) return;

        if (await DispatchForAsync(team, causation, ct) is { } dispatch)
        {
            await backlog.RecordTipAsync(dispatch.Id, repo, sha, ct);
        }
    }

    /// <summary>
    /// MERGE TO MAIN HAS JUST LANDED <paramref name="sha"/> ON <paramref name="branch"/>, and knows
    /// it without asking anybody. Stored on each of the team's current dispatches whose OWN work is
    /// in the merge - <paramref name="sha"/> beyond where the dispatch started - and the tip
    /// recorded beside it. A dispatch made after the merged work, or one with no recorded start,
    /// gets no landed from this merge. With several repositories one merge proves only one of
    /// them, so nothing is stored and the next read derives it.
    /// </summary>
    public async Task RecordMergedAsync(string team, string repo, string sha, string branch, CancellationToken ct)
    {
        var single = teams.ReposFor(team).Count == 1;
        var clonePath = Path.Combine(paths.ReposFor(team), repo, "main");

        foreach (var dispatch in await CurrentForTeamAsync(team, ct))
        {
            var start = (await backlog.BasesAsync(dispatch.Id, ct))
                .FirstOrDefault(b => string.Equals(b.Repo, repo, StringComparison.OrdinalIgnoreCase));
            var own = start is null ? null : await BacklogLandedState.OwnWorkAsync(git, clonePath, sha, start, ct);

            // THE MERGE IS NOT THIS DISPATCH'S: its tip stays what its own publish recorded.
            if (own == false) continue;

            await backlog.RecordTipAsync(dispatch.Id, repo, sha, ct);
            if (single && own == true) await backlog.RecordLandedAsync(dispatch.Id, sha, branch, ct);
        }
    }

    private async Task<BacklogDispatch?> DispatchForAsync(string team, long? causation, CancellationToken ct)
    {
        if (causation is { } seq
            && await log.FindAsync(seq, ct) is { } row
            && await backlog.DispatchForCorrelationAsync(row.CorrelationId, ct) is { } own
            && string.Equals(own.TeamId, team, StringComparison.OrdinalIgnoreCase))
        {
            return own;
        }

        return (await CurrentForTeamAsync(team, ct)).MaxBy(d => d.Id);
    }

    /// <summary>
    /// Every item's CURRENT dispatch that went to this team, above its floor - a dispatch below it
    /// belongs to an earlier team of the same name.
    /// </summary>
    private async Task<IReadOnlyList<BacklogDispatch>> CurrentForTeamAsync(string team, CancellationToken ct)
    {
        var floor = teams.FloorFor(team);

        return (await backlog.LatestDispatchesAsync(ct))
            .Where(d => string.Equals(d.TeamId, team, StringComparison.OrdinalIgnoreCase) && d.Correlation > floor)
            .ToList();
    }
}
