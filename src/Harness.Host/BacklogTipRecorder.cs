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
///
/// <para>
/// A START THAT COULD NOT BE READ IS RETRIED, and only while it is still true: on each backlog read
/// of the item and as the team's publish begins, before its tip is recorded, for as long as the team branch is unchanged since the
/// dispatch. Once the team has committed, its start can no longer be told apart from its work, and
/// the retry stops for good (B0028).
/// </para>
/// </summary>
/// <param name="fetchBudget">
/// How long the start's FETCH may take, read on every recording - the fetch alone, not the local
/// reads around it. Null is <see cref="DefaultFetchBudget"/>.
/// </param>
/// <param name="retryInterval">
/// How soon a READ may try a failed start again. A person re-reads the Backlog several times a
/// minute, and an unreachable origin would otherwise cost each read a whole fetch budget. The
/// publish and a person's Record where it started now are never held back by it. Null is
/// <see cref="DefaultRetryInterval"/>.
/// </param>
public sealed class BacklogTipRecorder(
    IBacklogStore backlog, IMessageLog log, TeamRegistry teams, TeamPaths paths, GitRunner git,
    Func<TimeSpan>? fetchBudget = null, TimeSpan? retryInterval = null)
{
    /// <summary>How long a start's fetch may take before the start goes unrecorded, by default.</summary>
    public static readonly TimeSpan DefaultFetchBudget = TimeSpan.FromSeconds(10);

    /// <summary>How soon a read tries a failed start again, by default.</summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>The configuration key the fetch budget is read from, as a <see cref="TimeSpan"/>.</summary>
    public const string FetchBudgetKey = "Backlog:StartFetchBudget";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTimeOffset> _lastTried = new();

    /// <summary>
    /// WHERE THE DISPATCH STARTED, read when it is made: per repository, origin's default branch
    /// after a fetch (GH_TOKEN by the rule a fetch uses) and the team branch if it has one. Only
    /// work beyond both is this dispatch's own, and only that is ever stored as landed.
    ///
    /// <para>
    /// NOTHING IS RECORDED WHEN IT CANNOT BE READ - no clone yet, a fetch that failed or ran over its
    /// budget, no default branch known. A stale origin would put the start too early and count
    /// earlier work as this dispatch's. What IS kept then is why, and the team branch and default
    /// branch as the clone held them, unfetched: that is what lets <see cref="RetryStartAsync"/>
    /// tell later whether the team branch is still where it was. Contributor mode records nothing:
    /// a merged pull request is its only proof. It never fails a dispatch.
    /// </para>
    /// </summary>
    public async Task RecordBaseAsync(BacklogDispatch dispatch, CancellationToken ct)
    {
        foreach (var url in teams.ReposFor(dispatch.TeamId))
        {
            string repo;
            try
            {
                repo = RepoUrls.DeriveName(url);
            }
            catch (UriFormatException)
            {
                continue;
            }

            if (teams.ContributorFor(dispatch.TeamId, repo).ContributorMode) continue;

            await RecordOneAsync(dispatch, repo, null, ct);
        }
    }

    /// <summary>
    /// TRIES AGAIN EVERY START OF <paramref name="dispatch"/> THAT WAS NOT RECORDED, and records
    /// each only while the team branch is unchanged since the dispatch: absent, at the tip it had
    /// then, or - absent then - cut since at the default branch as it stood then. A team branch
    /// that has moved on stops that repository's retry for good. Answers whether anything was
    /// tried. A team that is gone, or a dispatch below its team's floor, is not tried.
    /// </summary>
    /// <param name="fromRead">
    /// A backlog read: held back by the retry interval after a failed try. The publish and a
    /// person's action pass false.
    /// </param>
    public async Task<bool> RetryStartAsync(BacklogDispatch dispatch, bool fromRead, CancellationToken ct)
    {
        if (teams.ExistingName(dispatch.TeamId) is not { } team) return false;
        if (dispatch.Correlation <= teams.FloorFor(team)) return false;

        var open = (await backlog.MissedStartsAsync(dispatch.Id, ct)).Where(m => m.StoppedAt is null).ToList();
        if (open.Count == 0) return false;

        var now = DateTimeOffset.UtcNow;
        if (fromRead
            && _lastTried.TryGetValue(dispatch.Id, out var last)
            && now - last < (retryInterval ?? DefaultRetryInterval))
        {
            return false;
        }

        _lastTried[dispatch.Id] = now;

        var recorded = (await backlog.BasesAsync(dispatch.Id, ct)).Select(b => b.Repo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var repos = teams.ReposFor(team).Select(SafeName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var missed in open)
        {
            if (recorded.Contains(missed.Repo) || !repos.Contains(missed.Repo)) continue;
            if (teams.ContributorFor(team, missed.Repo).ContributorMode) continue;

            await RecordOneAsync(dispatch, missed.Repo, missed, ct);
        }

        return true;
    }

    /// <summary>
    /// ONE REPOSITORY'S START, at dispatch (<paramref name="retry"/> null) or on a retry. Local reads
    /// first, then the fetch inside its budget, then the local reads again - the team branch is
    /// checked against the dispatch's both before the fetch and after it, since the fetch can show a
    /// team branch pushed from elsewhere.
    /// </summary>
    private async Task RecordOneAsync(
        BacklogDispatch dispatch, string repo, BacklogDispatchMissedStart? retry, CancellationToken ct)
    {
        var teamBranch = $"team/{dispatch.TeamId}";
        string? teamBefore = null;
        string? defaultBefore = null;

        async Task MissedAsync(string reason) =>
            await backlog.RecordMissedStartAsync(dispatch.Id, repo, reason, teamBefore, defaultBefore, ct);

        async Task<bool> MovedAsync(string? teamNow)
        {
            if (retry is null || Unchanged(retry, teamNow)) return false;

            await backlog.StopStartRetryAsync(
                dispatch.Id, repo,
                $"{teamBranch} had commits of its own before the start could be recorded, so it no longer can be", ct);
            return true;
        }

        try
        {
            var branch = teams.DefaultBranchFor(dispatch.TeamId, repo).Branch;
            var clonePath = Path.Combine(paths.ReposFor(dispatch.TeamId), repo, "main");

            if (!Directory.Exists(clonePath))
            {
                if (!await MovedAsync(null)) await MissedAsync("the team's clone was missing");
                return;
            }

            teamBefore = await TeamTipAsync(clonePath, dispatch.TeamId, ct);
            if (branch is not null) defaultBefore = await ShaAsync(clonePath, $"refs/remotes/origin/{branch}", ct);
            if (await MovedAsync(teamBefore)) return;

            if (branch is null)
            {
                await MissedAsync("the default branch was not known");
                return;
            }

            // THE BUDGET IS THE FETCH'S ALONE. The local reads around it are the dispatch's own
            // business and cost milliseconds; a slow origin is what this bounds.
            var budget = fetchBudget?.Invoke() ?? DefaultFetchBudget;
            using (var fetchCt = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                fetchCt.CancelAfter(budget);
                try
                {
                    if ((await git.FetchAsync(clonePath, fetchCt.Token)).ExitCode != 0)
                    {
                        await MissedAsync("the fetch from origin failed");
                        return;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await MissedAsync($"the fetch from origin took longer than {Seconds(budget)}");
                    return;
                }
            }

            if (await ShaAsync(clonePath, $"refs/remotes/origin/{branch}", ct) is not { } defaultSha)
            {
                await MissedAsync($"origin/{branch} could not be read after the fetch");
                return;
            }

            var teamSha = await TeamTipAsync(clonePath, dispatch.TeamId, ct);
            if (await MovedAsync(teamSha)) return;

            await backlog.RecordBaseAsync(dispatch.Id, repo, defaultSha, teamSha, ct);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or IOException)
        {
            await MissedAsync("git could not read the team's clone");
        }
    }

    /// <summary>
    /// THE TEAM BRANCH IS WHERE IT WAS WHEN THE DISPATCH WAS MADE: absent now, at the same tip, or
    /// absent then and cut since exactly at the default branch as the clone knew it then - which
    /// carries no commits of its own.
    /// </summary>
    private static bool Unchanged(BacklogDispatchMissedStart missed, string? teamNow) =>
        teamNow is null
        || string.Equals(teamNow, missed.TeamSha, StringComparison.OrdinalIgnoreCase)
        || (missed.TeamSha is null && string.Equals(teamNow, missed.DefaultSha, StringComparison.OrdinalIgnoreCase));

    private async Task<string?> TeamTipAsync(string clonePath, string team, CancellationToken ct) =>
        await ShaAsync(clonePath, $"refs/heads/team/{team}", ct)
        ?? await ShaAsync(clonePath, $"refs/remotes/origin/team/{team}", ct);

    private static string? SafeName(string url)
    {
        try
        {
            return RepoUrls.DeriveName(url);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string Seconds(TimeSpan budget) =>
        budget.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s";

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
    /// THE TEAM'S PUBLISH IS BEGINNING: the start of the dispatch it belongs to, when it was not
    /// recorded, is tried again before anything is pushed or a tip recorded - still only while the
    /// team branch is where it was when the dispatch was made. See <see cref="RetryStartAsync"/>.
    /// </summary>
    public async Task RetryForPublishAsync(string team, long? causation, CancellationToken ct)
    {
        if (await DispatchForAsync(team, causation, ct) is { } dispatch)
        {
            await RetryStartAsync(dispatch, fromRead: false, ct);
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
