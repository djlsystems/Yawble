using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE FOUR WORDS, and the fourth is the point.
///
/// <para>
/// A <c>const</c> set rather than an enum, for the reason <c>BacklogStates</c> and
/// <c>MergedToMainBy</c> are: these words go on the wire verbatim and are read by a client in
/// another language, so the string IS the contract and a C# enum name would be a second spelling
/// of it.
/// </para>
/// </summary>
public static class BacklogLandedStates
{
    /// <summary>The team's work is REACHABLE FROM <c>origin/main</c>. Proven, never inferred.</summary>
    public const string Landed = "landed";

    /// <summary>
    /// On a remote branch, and not on <c>origin/main</c>. Work can sit here for hours, and without
    /// this word it would read as done.
    /// </summary>
    public const string Pushed = "pushed";

    /// <summary>
    /// Commits in the team's clone that are on NO remote at all - commits on one disk, which a
    /// Backlog screen must never read as <c>implemented</c>.
    /// </summary>
    public const string Local = "local";

    /// <summary>
    /// NOBODY HAS SAID, WHICH IS NOT THE SAME FACT AS NOT DONE - the rule this codebase already
    /// holds for spend, applied here. Returned rather than guessed: the team is gone, the clone is
    /// missing, git failed or timed out, the clone holds nothing that can be traced to this item,
    /// or ancestry cannot answer because a rebase has broken it.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// CONTRIBUTOR MODE. The recorded pull request is open upstream: the work is in review,
    /// and nothing about the fork can say more.
    /// </summary>
    public const string InReview = "in-review";

    /// <summary>
    /// CONTRIBUTOR MODE. The recorded pull request was closed WITHOUT merging. Never
    /// <c>landed</c>, whatever reached the fork.
    /// </summary>
    public const string Declined = "declined";
}

/// <summary>
/// WHETHER THIS ITEM'S WORK ACTUALLY REACHED <c>main</c> - what <c>implemented</c>
/// ought to mean, DERIVED rather than stored.
/// </summary>
/// <param name="State">One of <see cref="BacklogLandedStates"/>, and never anything else.</param>
/// <param name="TeamId">
/// The team the CURRENT dispatch went to - the team whose clone was asked. It may no longer exist,
/// which is itself an answer and is <c>unknown</c>.
/// </param>
/// <param name="Detail">
/// ONE SENTENCE A PERSON CAN READ WITHOUT KNOWING ANY OF THIS. It is the whole value of
/// <c>unknown</c>: four items can read <c>unknown</c> for four different reasons, and a screen that
/// showed only the word would send the reader to the terminal every time.
/// </param>
/// <param name="ReadAt">
/// In contributor mode the answer is GitHub's, about the recorded pull request, and this is
/// when GitHub last gave it. Null for every answer derived from the clone alone.
/// </param>
/// <param name="LandedAt">
/// WHEN LANDED WAS FIRST PROVEN AND STORED. Set on every <c>landed</c> answered from the
/// dispatch's stored proof, which is what outlives the branch, the clone and the team. Null for
/// every other answer.
/// </param>
public sealed record BacklogLanded(
    string State, string TeamId, string Detail, DateTimeOffset? ReadAt = null, DateTimeOffset? LandedAt = null)
{
    /// <summary>
    /// What a derived <c>landed</c> stands on: per repository, the sha proven reachable and the
    /// default branch it was read on. Carried to the store, never to the wire - the sentence in
    /// <see cref="Detail"/> is what a person reads.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<LandedProof>? Proof { get; init; }
}

/// <summary>One repository's evidence behind a derived <c>landed</c>.</summary>
public sealed record LandedProof(string Repo, string Sha, string Branch);

/// <summary>
/// Derives <see cref="BacklogLanded"/> for a set of items from their CURRENT dispatches.
///
/// <para>
/// THE SHAPE IS <see cref="BacklogInFlightState"/>'S, DELIBERATELY, because that class is the
/// discipline wanted here: one store query for every item's latest dispatch, and a dispatch at or
/// below its team's floor derives nothing. ONE THING IS STORED, AND ONLY ONE: <c>landed</c>,
/// once proven by ancestry, is kept on the dispatch and answered from there ever after, because the
/// branch, clone and team it was proven from are tidied away. Nothing short of that is stored, so
/// nothing short of that can get stuck.
/// </para>
///
/// <para>
/// IT DIVERGES FROM THAT PRECEDENT IN ONE PLACE, ON PURPOSE. A team that no
/// longer exists derives NOTHING for <c>inFlight</c> - a gone team is working nothing, and the
/// one-word answer is no. Here it derives <c>unknown</c>: its clone went with it, so whether the
/// work landed is a question nobody on this machine can answer any more, and answering "no" would
/// be a confident lie.
/// </para>
///
/// <para>
/// WHAT IT CAN AND CANNOT SEE, SAID PLAINLY. A dispatch record carries a CORRELATION, not a sha, so
/// no derivation here can name the commits belonging to one item. The question actually answered is
/// <i>does this team's clone still hold work that has not reached <c>origin/main</c></i>, attributed
/// to the item its latest dispatch names. That granularity is enough to catch commits sitting in
/// one clone on no branch on origin, and it is why <c>landed</c> is claimed only from
/// POSITIVE evidence of work that is reachable, never from a clone in which nothing can be found.
/// A team that was handed an item and did nothing reads <c>unknown</c>, not <c>landed</c>.
/// </para>
///
/// <para>
/// NO NETWORK ON THE TEAM'S OWN PATH, AND ONE FETCH ON THE RECORDED-TIP PATH. The one
/// exception is <c>FromRecordedTipsAsync</c>: a dispatch that nothing else can answer, whose tip was
/// recorded, fetches the clone it asks - paid only until landed is proven and stored, cached per
/// dispatch, and inside the same budget. Everything else below is local: <c>rev-parse</c>, <c>merge-base</c>,
/// <c>rev-list</c> and <c>cherry</c> against refs this clone already has. There is no <c>fetch</c>
/// and no <c>ls-remote</c>, which the repo status route asks only behind an explicit
/// <c>?refresh=true</c> - a backlog list is read on every visit to the screen, and a network round
/// trip per team per visit is not a cost this page can carry. The price is that a remote-tracking
/// ref last updated an hour ago is what gets compared, which can only ever under-report landedness;
/// the arms below are written so that under-reporting comes out as <c>unknown</c> rather than as a
/// confident <c>pushed</c>.
/// </para>
///
/// <para>
/// WHAT IT COSTS, AND WHAT BOUNDS IT. <c>BacklogInFlight</c> is two SQLite queries for the whole
/// screen. This shells out to git, which is orders of magnitude dearer - roughly ten to twenty
/// child processes per repository. Three bounds, all of them deliberate:
/// <list type="bullet">
/// <item>PER TEAM, NOT PER ROW. The verdict is a fact about a team's clone, so it is computed once
/// per distinct team and shared by every item that team holds. A screen showing forty items
/// dispatched to four teams asks git four times, not forty.</item>
/// <item>A SHORT CACHE, <see cref="BacklogLandedCache"/>, keyed by team. A person working the
/// Backlog re-reads the list several times a minute and git state moves on the scale of minutes, so
/// the entries live <see cref="BacklogLandedCache.DefaultLifetime"/> and the second read of a busy
/// minute costs nothing at all.</item>
/// <item>A WHOLE-DERIVATION BUDGET, <see cref="Budget"/>. Teams not measured before it expires come
/// back <c>unknown</c> - "took too long" - rather than holding the page. <c>GitRunner</c> already
/// bounds each invocation at thirty seconds, which is the right bound for one command and far too
/// long for a list of them.</item>
/// </list>
/// </para>
/// </summary>
public static class BacklogLandedState
{
    /// <summary>
    /// How long the WHOLE derivation may take, however many teams are on the screen. A backlog list
    /// that blocks for thirty seconds is a broken screen, and the honest thing to put in the rows
    /// it did not reach is <c>unknown</c>.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Landed state keyed by ITEM id, for every dispatch in <paramref name="latest"/> whose
    /// correlation is above its team's floor. Every other item is absent - and absent is what the
    /// wire renders as null.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, BacklogLanded>> ForAsync(
        IEnumerable<BacklogDispatch> latest,
        TeamRegistry teams,
        TeamPaths paths,
        GitRunner git,
        BacklogLandedCache cache,
        CancellationToken ct = default,
        PullRequestStateReader? pullRequests = null,
        IBacklogStore? store = null)
    {
        var result = new Dictionary<long, BacklogLanded>();
        var candidates = new List<(BacklogDispatch Dispatch, string Team)>();
        var floors = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        foreach (var dispatch in latest)
        {
            // LANDED, ONCE PROVEN, IS KEPT. The stored proof is answered before anything
            // else is asked, because the branch, the clone and the team it was proven from may all
            // be gone - and a proven fact is not re-derived into `unknown`. Never downgraded; a
            // later rewrite of the default branch that drops the commit is out of scope.
            if (Stored(dispatch) is { } kept)
            {
                result[dispatch.Item] = kept;
                continue;
            }

            // THE TEAM IS GONE, AND THAT IS `unknown` RATHER THAN NOTHING. Deleting a team removes
            // its directory - its clone and every worktree - so the evidence this derivation reads
            // is not merely absent, it has been destroyed. `inFlight` answers no here and is right
            // to; this one must not, because "no" would assert the work is not on main. What can
            // still be asked is whether the tip its publish RECORDED is on the default branch in
            // another clone of the same repository.
            if (teams.ExistingName(dispatch.TeamId) is not { } stored)
            {
                var gone = $"{dispatch.TeamName} no longer exists, so its clone is gone too and nothing "
                    + "on this machine can say whether the work reached the default branch.";

                result[dispatch.Item] = await FromRecordedTipsAsync(
                    dispatch, null, teams, paths, git, cache, store, budget, ct) ?? Unknown(dispatch.TeamId, gone);
                continue;
            }

            // THE TEAM'S FLOOR, memoised per team rather than per row, exactly as BacklogInFlight
            // applies it. A dispatch below it belongs to a previous incarnation of a team recreated
            // under the same name, and the clone standing there today is not the one it ran in.
            if (!floors.TryGetValue(stored, out var floor))
            {
                floor = teams.FloorFor(stored);
                floors[stored] = floor;
            }

            if (dispatch.Correlation <= floor) continue;

            candidates.Add((dispatch, stored));
        }

        if (candidates.Count == 0) return result;

        // ONE VERDICT PER TEAM, SHARED BY EVERY ITEM IT HOLDS - the bound that keeps this affordable.
        var byTeam = new Dictionary<string, BacklogLanded>(StringComparer.OrdinalIgnoreCase);

        foreach (var (dispatch, stored) in candidates)
        {
            if (!byTeam.TryGetValue(stored, out var verdict))
            {
                verdict = cache.Get(stored)
                    ?? cache.Put(stored, await ForTeamAsync(stored, teams, paths, git, budget, ct, pullRequests));

                byTeam[stored] = verdict;
            }

            // THE TEAM IS HERE AND ITS CLONE CANNOT SAY - its branch was deleted after the merge,
            // or the clone itself is gone: the recorded tip is asked, of its own clone first and
            // then of any other clone of the repository, exactly as for a team that is gone.
            if (verdict.State == BacklogLandedStates.Unknown
                && await FromRecordedTipsAsync(dispatch, stored, teams, paths, git, cache, store, budget, ct) is { } fromTip)
            {
                result[dispatch.Item] = fromTip;
                continue;
            }

            result[dispatch.Item] = await KeepAsync(dispatch, verdict, store, ct);
        }

        return result;
    }

    /// <summary>
    /// A dispatch's STORED landed, answered as <c>landed</c> with when it was proven, or null when
    /// nothing has been proven for it yet.
    /// </summary>
    private static BacklogLanded? Stored(BacklogDispatch dispatch)
    {
        if (dispatch.LandedAt is not { } at) return null;

        DateTimeOffset? when = DateTimeOffset.TryParse(
            at, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

        var stamp = when?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? at;
        var what = dispatch.LandedSha is { } sha && dispatch.LandedBranch is { } branch && !sha.Contains(' ')
            ? $"{Short(sha)} was on origin/{branch}"
            : $"the work was on the default branch ({dispatch.LandedSha} on {dispatch.LandedBranch})";

        return new BacklogLanded(
            BacklogLandedStates.Landed,
            dispatch.TeamId,
            $"{what} when it was proven at {stamp}. Proven landed is kept after the branch or the team is gone.",
            LandedAt: when);
    }

    /// <summary>
    /// STORES A DERIVED <c>landed</c> ON THE DISPATCH the first time it is proven, and answers the
    /// verdict unchanged. Anything else is answered as it is and stores nothing: only positive
    /// evidence is kept, and only ancestry leaves a <see cref="BacklogLanded.Proof"/> - a merged
    /// pull request in contributor mode carries none, so that mode is untouched.
    /// </summary>
    private static async Task<BacklogLanded> KeepAsync(
        BacklogDispatch dispatch, BacklogLanded verdict, IBacklogStore? store, CancellationToken ct)
    {
        if (store is null
            || verdict.State != BacklogLandedStates.Landed
            || verdict.Proof is not { Count: > 0 } proof)
        {
            return verdict;
        }

        var (sha, branch) = proof.Count == 1
            ? (proof[0].Sha, proof[0].Branch)
            : (string.Join(", ", proof.Select(p => $"{p.Repo} {p.Sha}")),
                string.Join(", ", proof.Select(p => $"{p.Repo} {p.Branch}")));

        await store.RecordLandedAsync(dispatch.Id, sha, branch, ct);

        return verdict with { LandedAt = DateTimeOffset.UtcNow };
    }

    /// <summary>
    /// THE RECORDED TIP, ASKED OF A CLONE OF THE SAME REPOSITORY. For a dispatch whose
    /// team is gone (<paramref name="own"/> null), or whose team's own clone answered
    /// <c>unknown</c> - its branch deleted after the merge, or the clone gone. The team's own clone
    /// is asked first when it has one, then every other team's. Null when nothing was recorded,
    /// which leaves the caller's answer standing.
    ///
    /// <para>
    /// A FETCH, AND THE ONE NETWORK CALL THIS CLASS MAKES. The other clone's origin/&lt;default&gt;
    /// may be older than the merge, and a stale ref can only under-report; the fetch goes through
    /// <see cref="GitRunner.FetchAsync"/>, so it gets <c>GH_TOKEN</c> by that clone's team's
    /// remotes, the rule every fetch follows. It is paid only until landed is proven, is cached per
    /// dispatch like a team's verdict, and runs inside the same <see cref="Budget"/>.
    /// </para>
    ///
    /// <para>
    /// NEVER GUESSED. The sha must be an ancestor of origin/&lt;default&gt; in that clone, by the
    /// default branch stored for that clone's team. No clone anywhere is <c>unknown</c> and says so;
    /// a sha no clone holds, or one that is not reachable, is <c>unknown</c> too. A contributor-mode
    /// clone is never asked: its origin is a fork, and a merged pull request is the only proof there.
    /// </para>
    /// </summary>
    private static async Task<BacklogLanded?> FromRecordedTipsAsync(
        BacklogDispatch dispatch,
        string? own,
        TeamRegistry teams,
        TeamPaths paths,
        GitRunner git,
        BacklogLandedCache cache,
        IBacklogStore? store,
        CancellationTokenSource budget,
        CancellationToken ct)
    {
        if (store is null) return null;

        var tips = await store.TipsAsync(dispatch.Id, ct);
        if (tips.Count == 0) return null;

        var key = $"dispatch#{dispatch.Id}";
        if (cache.Get(key) is { } cached) return await KeepAsync(dispatch, cached, store, ct);

        BacklogLanded? worst = null;
        var proofs = new List<LandedProof>();

        foreach (var tip in tips)
        {
            BacklogLanded verdict;
            try
            {
                verdict = await TipAsync(dispatch.TeamId, own, tip, teams, paths, git, budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                verdict = Unknown(dispatch.TeamId, $"the check of {tip.Repo}'s recorded tip took too long and was stopped.");
            }

            if (verdict.Proof is { } proof) proofs.AddRange(proof);
            if (tips.Count > 1) verdict = verdict with { Detail = $"{tip.Repo}: {verdict.Detail}" };
            if (worst is null || Rank(verdict.State) > Rank(worst.State)) worst = verdict;
        }

        var answer = worst!.State == BacklogLandedStates.Landed ? worst with { Proof = proofs } : worst;

        return await KeepAsync(dispatch, cache.Put(key, answer), store, ct);
    }

    /// <summary>One recorded tip, against the first other clone of its repository that can answer.</summary>
    private static async Task<BacklogLanded> TipAsync(
        string teamId,
        string? own,
        BacklogDispatchTip tip,
        TeamRegistry teams,
        TeamPaths paths,
        GitRunner git,
        CancellationToken ct)
    {
        var sha = tip.Sha;
        var notReachable = new List<string>();

        foreach (var (other, clonePath, branch) in ClonesOf(tip.Repo, own, teams, paths))
        {
            // A FAILED FETCH IS NOT FATAL: the refs already here can still prove a yes, and a
            // stale ref can only fail to - which lands in `unknown` below, never in a no.
            await git.FetchAsync(clonePath, ct);

            var ancestor = await git.RunGitAsync(
                clonePath, ["merge-base", "--is-ancestor", sha, $"refs/remotes/origin/{branch}"], ct);

            if (ancestor.ExitCode == 0)
            {
                return new BacklogLanded(
                    BacklogLandedStates.Landed,
                    teamId,
                    $"the recorded tip {Short(sha)} is on origin/{branch} in {teams.LabelFor(other)}'s clone of {tip.Repo}.")
                {
                    Proof = [new LandedProof(tip.Repo, sha, branch)],
                };
            }

            // 1 is "not an ancestor"; anything else is this clone not holding the commit at all,
            // which says nothing about whether it landed.
            if (ancestor.ExitCode == 1) notReachable.Add($"{teams.LabelFor(other)}'s clone");
        }

        return notReachable.Count > 0
            ? Unknown(teamId,
                $"the recorded tip {Short(sha)} is not reachable from the default branch in "
                + $"{string.Join(", ", notReachable)} of {tip.Repo}, so whether it landed cannot be proven here.")
            : ClonesOf(tip.Repo, own, teams, paths).Any()
                ? Unknown(teamId,
                    $"no clone of {tip.Repo} on this instance holds the recorded tip {Short(sha)}, so whether it landed cannot be proven here.")
                : Unknown(teamId, $"no clone of {tip.Repo} on this instance to check the recorded tip against.");
    }

    /// <summary>
    /// Every clone of <paramref name="repo"/> on this instance that can be asked, the team's own
    /// (<paramref name="own"/>) first: cloned, with its default branch stored, and not in
    /// contributor mode.
    /// </summary>
    private static IEnumerable<(string Team, string ClonePath, string Branch)> ClonesOf(
        string repo, string? own, TeamRegistry teams, TeamPaths paths)
    {
        var ids = teams.All().Select(team => team.Id)
            .OrderBy(id => string.Equals(id, own, StringComparison.OrdinalIgnoreCase) ? 0 : 1);

        foreach (var team in ids)
        {
            if (!teams.ReposFor(team).Any(url => SameRepo(url, repo))) continue;
            if (teams.ContributorFor(team, repo).ContributorMode) continue;
            if (teams.DefaultBranchFor(team, repo).Branch is not { } branch) continue;

            string clonePath;
            try
            {
                clonePath = Path.Combine(paths.ReposFor(team), repo, "main");
            }
            catch (KeyNotFoundException)
            {
                continue;
            }

            if (Directory.Exists(clonePath)) yield return (team, clonePath, branch);
        }
    }

    private static bool SameRepo(string url, string repo)
    {
        try
        {
            return string.Equals(RepoUrls.DeriveName(url), repo, StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>
    /// One team's verdict, over every repository it holds.
    ///
    /// <para>
    /// THE WEAKEST ANSWER WINS, and the ranking is not alphabetical: <c>local</c> beats
    /// <c>pushed</c> beats <c>unknown</c> beats <c>landed</c>. A team with two repositories whose
    /// work landed in one and sits unpushed in the other has NOT got its work to main, and the row
    /// must say the urgent half. <c>unknown</c> outranks <c>landed</c> for the same reason in the
    /// other direction - one repository nobody could measure makes "landed" a claim about evidence
    /// that was never seen.
    /// </para>
    /// </summary>
    private static async Task<BacklogLanded> ForTeamAsync(
        string stored,
        TeamRegistry teams,
        TeamPaths paths,
        GitRunner git,
        CancellationTokenSource budget,
        CancellationToken ct,
        PullRequestStateReader? pullRequests)
    {
        if (budget.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Unknown(stored, "the check of this team's clone took too long and was stopped.");
        }

        var urls = teams.ReposFor(stored);
        if (urls.Count == 0)
        {
            return Unknown(stored, "this team has no repository, so there is nowhere to look.");
        }

        string reposRoot;
        try
        {
            reposRoot = paths.ReposFor(stored);
        }
        catch (KeyNotFoundException)
        {
            // RootFor throws for a team nobody registered a root for. It is a wiring fault rather
            // than a bad name, and a wiring fault is precisely something nobody has said.
            return Unknown(stored, "this team has no registered folder on this machine.");
        }

        BacklogLanded? worst = null;
        var many = urls.Count > 1;
        var proofs = new List<LandedProof>();

        foreach (var url in urls)
        {
            var repo = RepoUrls.DeriveName(url);
            var clonePath = Path.Combine(reposRoot, repo, "main");

            BacklogLanded verdict;
            try
            {
                // Compared against the stored default branch. Not known is `unknown`.
                var branch = teams.DefaultBranchFor(stored, repo).Branch;
                verdict = !Directory.Exists(clonePath)
                    ? Unknown(stored, $"{repo} is not cloned on this machine yet.")
                    : branch is null
                        ? Unknown(stored, $"{repo}'s default branch is not known, so there is nothing to compare the work against.")
                        : await ForRepoAsync(stored, repo, clonePath, branch, git, budget.Token,
                            teams.ContributorFor(stored, repo), pullRequests);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                verdict = Unknown(stored, $"the check of {repo} took too long and was stopped.");
            }

            if (many)
            {
                verdict = verdict with { Detail = $"{repo}: {verdict.Detail}" };
            }

            if (verdict.Proof is { } proof) proofs.AddRange(proof);
            if (worst is null || Rank(verdict.State) > Rank(worst.State)) worst = verdict;
        }

        // LANDED OVERALL STANDS ON EVERY REPOSITORY'S PROOF; one without a sha keeps nothing.
        return worst!.State == BacklogLandedStates.Landed
            ? worst with { Proof = proofs.Count == urls.Count ? proofs : null }
            : worst;
    }

    /// <summary>One repository's verdict. Every command is local and every claim is proven.</summary>
    private static async Task<BacklogLanded> ForRepoAsync(
        string stored,
        string repo,
        string clonePath,
        string branch,
        GitRunner git,
        CancellationToken ct,
        RepoContributor? contributor = null,
        PullRequestStateReader? pullRequests = null)
    {
        var originBranch = $"refs/remotes/origin/{branch}";
        var localBranch = $"refs/heads/{branch}";

        // NOTHING TO COMPARE AGAINST IS NOT "NOT LANDED". A clone with no origin/main - never
        // fetched, or an origin that has no main - cannot answer the question that was asked.
        if (!await ResolvesAsync(git, clonePath, originBranch, ct))
        {
            return Unknown(
                stored, $"{repo} has no origin/{branch} in its clone to compare the work against.");
        }

        // THE COMMITS ON NO REMOTE AT ALL ARE ASKED FIRST, because work on one disk is the
        // riskiest state and it outranks every other answer. The probe is TeamDeletion's - the
        // clone's own main, then each worktree's branch - which is what makes the two agree about
        // one repository. `rev-list --not --remotes` is a local question about this clone's
        // remote-tracking refs, and being ahead of all of them is conclusive without a network.
        var status = await git.StatusAsync(clonePath, branch, $"refs/heads/team/{stored}", ct);

        var offRemote = new List<string>();
        var unmeasured = new List<string>();

        async Task ProbeAsync(string reference, string label)
        {
            var count = await git.CountCommitsNotOnAnyRemoteAsync(clonePath, reference, ct);

            // A COUNT THAT COULD NOT BE TAKEN IS NOT A ZERO. Null is "not measured" here exactly as
            // it is on RepoStatus, and treating it as zero is how a screen says "safely on a remote"
            // about a branch nobody managed to read.
            if (count is null) unmeasured.Add(label);
            else if (count > 0) offRemote.Add($"{label} ({count} commit{(count == 1 ? "" : "s")})");
        }

        await ProbeAsync(localBranch, $"its clone's {branch}");

        foreach (var worktree in status.Worktrees)
        {
            var reference = worktree.Branch is { Length: > 0 }
                ? $"refs/heads/{worktree.Branch}"
                : worktree.Sha;

            if (reference is null) continue;

            await ProbeAsync(
                reference,
                worktree.Branch is { Length: > 0 } worktreeBranch ? worktreeBranch : "a detached worktree");
        }

        var work = await WorkRefAsync(git, clonePath, stored, localBranch, status, ct);

        // The clone's own main is already probed above, so the team branch is the only ref this
        // adds. Probing it twice would double-count it into the sentence.
        if (work is not null && work != localBranch) await ProbeAsync(work, $"team/{stored}");

        if (offRemote.Count > 0)
        {
            return new BacklogLanded(
                BacklogLandedStates.Local,
                stored,
                $"{repo} holds work on no remote at all - {string.Join(", ", offRemote)}. "
                + "It exists only on this machine.");
        }

        if (unmeasured.Count > 0)
        {
            return Unknown(
                stored,
                $"git could not be read for {string.Join(", ", unmeasured)} in {repo}, so whether "
                + "anything is unpushed there is unknown.");
        }

        // IN CONTRIBUTOR MODE LANDED MEANS THE PULL REQUEST WAS MERGED, and GitHub is asked
        // rather than ancestry: a squash or rebase merge upstream leaves no ancestry to prove. The
        // local arms above still come first - work on no remote is on one disk whatever GitHub says.
        if (contributor is { ContributorMode: true })
        {
            return await ForPullRequestAsync(stored, repo, work, contributor, pullRequests, ct);
        }

        // NO BRANCH OF ITS OWN IS `unknown`, NOT `landed`. A clone sitting exactly on origin/main with no team branch has nothing this
        // derivation can trace to the item: a team that did the work and a team that was handed the
        // item and never started look IDENTICAL from here. `landed` is claimed only from positive
        // evidence, so the honest word for "there is nothing here to point at" is the fourth one.
        if (work is null)
        {
            return Unknown(
                stored,
                $"{repo} holds no branch or commits of this team's own, so there is nothing here "
                + "to trace to this item.");
        }

        var ancestor = await git.RunGitAsync(
            clonePath, ["merge-base", "--is-ancestor", work, originBranch], ct);

        if (ancestor.ExitCode == 0)
        {
            // THE SHA IS THE PROOF THAT IS KEPT: the ref names a branch that will be
            // deleted, the commit it points at now is the fact.
            var tip = await git.RunGitAsync(clonePath, ["rev-parse", "--verify", work], ct);

            return new BacklogLanded(
                BacklogLandedStates.Landed,
                stored,
                $"the work on {work} is on origin/{branch} in {repo}.")
            {
                Proof = tip.ExitCode == 0 ? [new LandedProof(repo, tip.Stdout.Trim(), branch)] : null,
            };
        }

        // THE ANCESTRY HAZARD, AND IT IS WHY `unknown` IS NOT OPTIONAL.
        //
        // A REBASE BREAKS ANCESTRY. Every change is replayed onto a new base under a new sha, so
        // `merge-base --is-ancestor` answers NO for a branch that is merged in every sense a person
        // cares about - the false negative, the mirror image of calling unmerged work landed. The
        // Git dialog merges rather than rebases for this reason.
        //
        // SO A `no` FROM ANCESTRY IS NOT TAKEN AS A `no` UNTIL IT IS CORROBORATED. `git cherry`
        // asks the question ancestry cannot - is any commit here carrying a change origin/main does
        // not have - and it is CONSERVATIVE in the safe direction: a commit whose patch was modified
        // during integration still reports `+`, so it errs toward "outstanding".
        //
        // AND IT IS USED ONLY TO WITHHOLD AN ANSWER, NEVER TO MANUFACTURE ONE. Where ancestry and
        // content disagree - nothing outstanding by patch, yet not reachable - this returns
        // `unknown` and says so. It does NOT return `landed`: that would be claiming a merge from
        // patch-ids, which is a claim this derivation cannot prove.
        //
        // THAT IS DELIBERATELY MORE CONSERVATIVE THAN ITS NEIGHBOUR, AND THE DIFFERENCE IS ON
        // PURPOSE. `RepoStatus.TeamMergedToMain` resolves the same disagreement as merged, recorded
        // as `MergedToMainBy.Content`, and it is right to - it gates a button a person is about to
        // press and is reading the repository in front of them. This one sits on the Backlog screen
        // and is the evidence behind the word `implemented`, where a wrong yes is the failure
        // that matters most. When the two must be reconciled, reconcile them here rather
        // than weakening that one.
        //
        // A CHERRY THAT COULD NOT RUN IS NOT EVIDENCE OF ANYTHING. It falls to `unknown` as well.
        var cherry = await git.RunGitAsync(clonePath, ["cherry", originBranch, work], ct);

        if (cherry.ExitCode != 0)
        {
            return Unknown(
                stored,
                $"{work} is not reachable from origin/{branch} in {repo}, and git could not check "
                + "whether its changes are already there under other commits.");
        }

        var outstanding = cherry.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith('+'));

        if (outstanding == 0)
        {
            return Unknown(
                stored,
                $"{work} is not reachable from origin/{branch} in {repo}, yet every change on it is "
                + "already there under different commits - which is what a rebase leaves behind. "
                + "Whether this work landed cannot be settled by reachability.");
        }

        return new BacklogLanded(
            BacklogLandedStates.Pushed,
            stored,
            $"{work} is on a remote but not on origin/{branch} in {repo} - "
            + $"{outstanding} commit{(outstanding == 1 ? "" : "s")} still to land.");
    }

    /// <summary>
    /// WHICH REF CARRIES THIS TEAM'S WORK, resolved in the order <c>ReadRepoStatusAsync</c> and the
    /// merge route both resolve it: the local <c>team/{id}</c> branch, else <c>origin/team/{id}</c>
    /// when the local branch was never created, else the clone's own <c>main</c> WHEN IT IS AHEAD
    /// OF <c>origin/main</c>.
    ///
    /// <para>
    /// THE THIRD ARM IS NOT A FALLBACK, IT IS THE COMMON CASE ON THIS PLATFORM. The seeded manager
    /// skill has a manager merge its members' branches into the clone's own <c>main</c> and push
    /// that, so a great many teams never create a <c>team/{id}</c> branch at all - and
    /// <c>QuietTeamSweep</c>'s wrap-up limb already reads clone-main-ahead-of-origin/main as this
    /// team's unpushed work.
    /// </para>
    ///
    /// <para>
    /// IT IS GUARDED ON BEING AHEAD, AND THE GUARD IS THE WHOLE HONESTY OF IT. A clone sitting
    /// exactly on <c>origin/main</c> has done nothing of its own, and returning <c>main</c> there
    /// would let a team that never started read <c>landed</c> off somebody else's commits.
    /// </para>
    /// </summary>
    private static async Task<string?> WorkRefAsync(
        GitRunner git,
        string clonePath,
        string stored,
        string localBranch,
        GitRunner.GitStatus status,
        CancellationToken ct)
    {
        if (status.TeamSha is not null) return $"refs/heads/team/{stored}";

        if (await ResolvesAsync(git, clonePath, $"refs/remotes/origin/team/{stored}", ct))
        {
            return $"refs/remotes/origin/team/{stored}";
        }

        return status.MainAhead is > 0 ? localBranch : null;
    }

    /// <summary>
    /// One contributor-mode repository's verdict, from its recorded pull request: open is
    /// <c>in-review</c>, closed without merging <c>declined</c>, merged <c>landed</c>. GitHub
    /// unreachable, or the token refused, is <c>unknown</c> - with the last answer and when it was
    /// read said beside it, never passed off as the current one.
    /// </summary>
    private static async Task<BacklogLanded> ForPullRequestAsync(
        string stored,
        string repo,
        string? work,
        RepoContributor contributor,
        PullRequestStateReader? pullRequests,
        CancellationToken ct)
    {
        if (contributor.PullRequest is null)
        {
            return work is null
                ? Unknown(stored, $"{repo} holds no branch or commits of this team's own, and no pull request "
                    + "is recorded for it, so there is nothing to trace to this item.")
                : new BacklogLanded(
                    BacklogLandedStates.Pushed,
                    stored,
                    $"team/{stored} is on the fork of {repo}, and no pull request has been opened for it yet.");
        }

        if (pullRequests is null)
        {
            return Unknown(stored, $"pull request #{contributor.PullRequest.Number} on {repo}'s upstream was not asked about.");
        }

        var reading = await pullRequests.ReadAsync(stored, repo, ct);
        if (reading is null)
        {
            return Unknown(stored, $"no pull request is recorded for {repo}.");
        }

        var pull = reading.PullRequest;
        var when = pull.ReadAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
        if (reading.UnknownReason is { } why)
        {
            return new BacklogLanded(
                BacklogLandedStates.Unknown,
                stored,
                $"GitHub could not say what pull request #{pull.Number} ({pull.Url}) is now: {why} "
                + $"The last answer, read {when}, was {pull.State}.",
                pull.ReadAt);
        }

        var sentence = reading.Landing switch
        {
            BacklogLandedStates.Landed => $"pull request #{pull.Number} ({pull.Url}) was merged upstream.",
            BacklogLandedStates.Declined => $"pull request #{pull.Number} ({pull.Url}) was closed without merging.",
            BacklogLandedStates.InReview => $"pull request #{pull.Number} ({pull.Url}) is open upstream, in review.",
            _ => $"pull request #{pull.Number} ({pull.Url}) is in a state this platform does not know: {pull.State}.",
        };

        return new BacklogLanded(reading.Landing, stored, $"{sentence} Read {when}.", pull.ReadAt);
    }

    private static async Task<bool> ResolvesAsync(
        GitRunner git, string clonePath, string reference, CancellationToken ct) =>
        (await git.RunGitAsync(clonePath, ["rev-parse", "--verify", reference], ct)).ExitCode == 0;

    private static BacklogLanded Unknown(string stored, string detail) =>
        new(BacklogLandedStates.Unknown, stored, detail);

    /// <summary>
    /// <c>local</c> &gt; <c>declined</c> &gt; <c>pushed</c> &gt; <c>in-review</c> &gt; <c>unknown</c>
    /// &gt; <c>landed</c> (the two contributor-mode words sit where their urgency does). See
    /// <see cref="ForTeamAsync"/> for why that order and not another.
    /// </summary>
    private static int Rank(string state) => state switch
    {
        BacklogLandedStates.Local => 5,
        BacklogLandedStates.Declined => 4,
        BacklogLandedStates.Pushed => 3,
        BacklogLandedStates.InReview => 2,
        BacklogLandedStates.Unknown => 1,
        _ => 0,
    };
}

/// <summary>
/// THE BOUND THAT MAKES A GIT-BACKED DERIVATION AFFORDABLE ON A LIST SCREEN, keyed by team.
///
/// <para>
/// <see cref="BacklogInFlightState"/> costs two SQLite queries for the whole backlog. This one
/// spawns git, roughly ten to twenty child processes per repository, and the Backlog is re-read on
/// every visit and after every write. Per-team computation already collapses forty rows to four
/// teams; this collapses the four teams a person visits repeatedly within a minute to one
/// measurement each.
/// </para>
///
/// <para>
/// A LIFETIME, NOT AN INVALIDATION. Nothing in the platform is notified when somebody pushes from a
/// terminal, so there is no event this cache could listen to - an invalidation hook would cover the
/// paths the product drives and miss every one a person drives by hand, which is the larger half.
/// A short lifetime is honest about that: the answer is at most
/// <see cref="DefaultLifetime"/> old, and the Git dialog remains the place to go for a live read.
/// </para>
///
/// <para>
/// A SINGLETON HOLDING MUTABLE STATE, AND IT IS A PARAMETER RATHER THAN A STATIC FIELD, so a test
/// can hand in an empty one - or one with a zero lifetime - instead of inheriting whatever the last
/// test in the process left behind.
/// </para>
/// </summary>
public sealed class BacklogLandedCache(TimeSpan? lifetime = null, Func<DateTimeOffset>? now = null)
{
    /// <summary>
    /// Short enough that a person watching a push land sees it on the next click or two, long
    /// enough that clicking about the screen does not re-run git. Git state moves on the scale of
    /// minutes; a backlog list is read several times a minute.
    /// </summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _lifetime = lifetime ?? DefaultLifetime;
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (BacklogLanded Verdict, DateTimeOffset At)>
        _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The team's verdict while it is still fresh, or null - which means "go and measure".</summary>
    public BacklogLanded? Get(string team) =>
        _entries.TryGetValue(team, out var entry) && _now() - entry.At < _lifetime
            ? entry.Verdict
            : null;

    /// <summary>Stores and returns <paramref name="verdict"/>, so a call site can do both at once.</summary>
    public BacklogLanded Put(string team, BacklogLanded verdict)
    {
        _entries[team] = (verdict, _now());
        return verdict;
    }

    /// <summary>Forgets every entry. For a caller that has just changed the repository itself.</summary>
    public void Clear() => _entries.Clear();
}
