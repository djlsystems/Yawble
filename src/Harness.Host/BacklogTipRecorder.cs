using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WRITES WHAT <see cref="BacklogLandedState"/> NEEDS TO OUTLIVE A TIDY-UP (B0025): the tip the
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
public sealed class BacklogTipRecorder(IBacklogStore backlog, IMessageLog log, TeamRegistry teams)
{
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
    /// it without asking anybody. Stored on each of the team's current dispatches that has no landed
    /// yet, and the tip recorded beside it. With several repositories one merge proves only one of
    /// them, so nothing is stored and the next read derives it.
    /// </summary>
    public async Task RecordMergedAsync(string team, string repo, string sha, string branch, CancellationToken ct)
    {
        var single = teams.ReposFor(team).Count == 1;

        foreach (var dispatch in await CurrentForTeamAsync(team, ct))
        {
            await backlog.RecordTipAsync(dispatch.Id, repo, sha, ct);
            if (single) await backlog.RecordLandedAsync(dispatch.Id, sha, branch, ct);
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
