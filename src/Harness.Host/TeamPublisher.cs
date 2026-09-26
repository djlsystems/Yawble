using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>What became of one repository the platform was asked to publish.</summary>
public enum TeamPublishResult
{
    /// <summary>The team named no repository, or nothing is at the clone path. NOT a failure and
    /// never reported as one - see <see cref="ITeamPublisher"/>.</summary>
    NoRepository,

    /// <summary>There was a clone and every branch in it is already on a remote, so no push was
    /// attempted. Silence, not a receipt.</summary>
    NothingToPublish,

    /// <summary>Branches reached origin. <see cref="RepoPublishOutcome.Branches"/> names them.</summary>
    Published,

    /// <summary>
    /// THE NAMED, RETRYABLE CONDITION. git was run and did not publish the work - an unreachable
    /// origin, a refused credential, a rejected ref, a push that ran out of time.
    /// <see cref="RepoPublishOutcome.Reason"/> carries git's own words, redacted;
    /// <see cref="RepoPublishOutcome.Branches"/> names what did NOT reach origin.
    /// </summary>
    PushFailed,

    /// <summary>
    /// Origin refused one or more branches because they do not extend what origin already holds:
    /// the branch was amended, rebased or reset after it was pushed. NOT retryable - the same push
    /// fails forever - and it does not stop the other branches, which are pushed and named in
    /// <see cref="RepoPublishOutcome.Published"/>. <see cref="RepoPublishOutcome.Branches"/> names
    /// the refused ones.
    /// </summary>
    Rejected,
}

/// <summary>
/// One repository, what happened to it, and which branches that was about.
/// </summary>
/// <param name="Repo">The folder name derived from the URL. NEVER the URL, which is where a
/// credential lives.</param>
/// <param name="Branches">On <see cref="TeamPublishResult.Published"/>, the branches now on origin.
/// On <see cref="TeamPublishResult.PushFailed"/>, the branches that are NOT - including the ones
/// never attempted, because a push stops at its first failure.</param>
/// <param name="Reason">Git's own words, through <see cref="GitOutputRedaction"/>. Null unless the
/// result is <see cref="TeamPublishResult.PushFailed"/>.</param>
public sealed record RepoPublishOutcome(
    string Repo,
    TeamPublishResult Result,
    IReadOnlyList<string> Branches,
    string? Reason = null,
    IReadOnlyList<string>? Published = null);

/// <summary>Every repository the publisher looked at, in the order the team names them.</summary>
public sealed record TeamPublishReport(IReadOnlyList<RepoPublishOutcome> Repos)
{
    public static readonly TeamPublishReport Nothing = new([]);

    /// <summary>Whether any repository ended in the retryable push condition. The route does NOT
    /// read this to decide anything - the acceptance lands either way - but a caller assembling a
    /// response or a test asserting the condition needs one question rather than a fold.</summary>
    public bool AnyPushFailed =>
        Repos.Any(r => r.Result is TeamPublishResult.PushFailed or TeamPublishResult.Rejected);
}

/// <summary>
/// MAKES A TEAM'S WORK EXIST ON ORIGIN, AND IS CALLED BY THE PLATFORM SO IT CANNOT BE FORGOTTEN.
///
/// <para>
/// Left to an agent, pushing is a judgement made at the END of a run, which is exactly the
/// moment a spend limit or a kill interrupts - so work would exist only under a team root, where
/// deleting the team destroys it, while its backlog item read `implemented`. A durable result must
/// not depend on a stochastic process choosing to act while it still has budget. The platform
/// CLONES the repository; it publishes it too.
/// </para>
///
/// <para>
/// <b>IT RUNS BEFORE THE ACCEPTANCE ROW, AND THAT ORDERING IS THE WHOLE GUARANTEE.</b>
/// `workflow.completed` is what drives a card to `done` (see `KanbanProjector.HandleWorkflowCompleted`),
/// so that row IS the acceptance. Called after it, this would be an optimisation; called before it,
/// a card cannot read `done` without the work having been on origin first.
/// </para>
///
/// <para>
/// <b>A PUSH IS NOT A MERGE.</b> `main` is never a destination here - see
/// <see cref="TeamPublisher.NeverPublished"/>. This publishes member branches so the work exists off
/// this machine; the Git dialog's ladder stays the integration path and the only thing that decides
/// what lands on main.
/// </para>
///
/// <para>
/// <b>A TEAM WITH NO REPOSITORY COMPLETES NORMALLY AND IS NEVER REFUSED.</b> Several teams
/// legitimately produce no code - a measurement or research task, say - and a
/// durability rule that refuses them is a rule that fires constantly and gets switched off. Nothing
/// on this interface can refuse anything: it returns a report and the caller carries on.
/// </para>
///
/// <para>
/// <b>A FAILED PUSH IS ITS OWN CONDITION AND NEVER THE RUN'S.</b> An unreachable origin is
/// TRANSPORT, not an agent fault. It is reported as `repo.pushFailed`, which is retryable because
/// nothing moved, and the acceptance still lands - refusing the acceptance is deliberately not an
/// option, since a member out of budget cannot satisfy a refusal.
/// </para>
///
/// <para>
/// <b>USE WHAT THE CLONE ALREADY USES.</b> No credential is read, composed or passed here: the push
/// runs in the clone the platform made, so git resolves the same remote and the same helper the
/// clone was made with. A second credential path is the thing this must not grow.
/// </para>
/// </summary>
public interface ITeamPublisher
{
    /// <summary>
    /// Publishes every branch this team owns that is not yet on a remote.
    ///
    /// NEVER THROWS, for the reason <c>RepoClone.EnsureAllAsync</c> does not: a workflow completion
    /// that 500s over an unreachable origin is worse than one that completes and says the work did
    /// not reach origin. The caller's own <paramref name="ct"/> is the one exception and propagates,
    /// because a cancelled REQUEST has no report to write.
    /// </summary>
    /// <param name="team">The stored team id, as <c>ExistingName</c> resolved it.</param>
    /// <param name="repoUrls">The team's repository URLs, in order. Empty is the ordinary case for a
    /// team that produces no code and is answered with <see cref="TeamPublishReport.Nothing"/>.</param>
    /// <param name="source">The member whose acceptance this is. Becomes the rows' <c>Source</c>,
    /// which is also what lets <c>MessageTeam.Of</c> recover the team with no payload field.</param>
    /// <param name="causation">The seq this run is reacting to, so the rows land INSIDE the workflow
    /// being accepted rather than rooting one of their own.</param>
    Task<TeamPublishReport> PublishAsync(
        string team,
        IReadOnlyList<string> repoUrls,
        ContainerId source,
        long? causation,
        CancellationToken ct);
}

/// <inheritdoc cref="ITeamPublisher"/>
/// <param name="paths">Where the team's clones are. The ONLY thing that composes a team path.</param>
/// <param name="git">The typed git operations. Nothing else here runs a process, and no
/// caller-supplied command line reaches it.</param>
/// <param name="log">Where the receipt and the failure condition are written.</param>
/// <remarks>
/// <b>THERE IS NO OFF SWITCH, WHERE <see cref="RepoClone"/> HAS ONE, AND THE ASYMMETRY IS EARNED.</b>
/// That flag exists because the host suite stands up hundreds of teams carrying unreachable example
/// URLs and a real clone apiece costs a network timeout apiece. This costs those same teams a
/// `Directory.Exists` that answers false - there is no clone to push from, precisely because cloning
/// was off - so the expense the flag would remove is not there to remove. A seam that can be
/// omitted is one something quietly omits, and this is the one mechanism in the product whose
/// omission is invisible until the work is already gone.
/// </remarks>
/// <param name="defaultBranchOf">
/// The stored default branch of (team, repository), which is never published either: it is
/// the branch `main` stands for below. Null, or a null answer, leaves only the fixed names.
/// </param>
public sealed class TeamPublisher(
    TeamPaths paths,
    GitRunner git,
    IMessageLog log,
    Func<string, string, string?>? defaultBranchOf = null) : ITeamPublisher
{
    /// <summary>
    /// THE BRANCHES THAT ARE NEVER A DESTINATION HERE, AND THIS IS THE ENFORCEMENT RATHER THAN THE
    /// DOCUMENTATION OF IT.
    ///
    /// <para>
    /// `main` because a push is not a merge: what lands on main is the Git dialog's decision, made
    /// by a person, through a ladder that fetches, checks ancestry and refuses a non-fast-forward
    /// before it pushes anything. A durability mechanism that also moved main would be an
    /// integration path nobody chose.
    /// </para>
    ///
    /// <para>
    /// `master` because it is the same branch under the name a repository this platform did not
    /// create may still use, and a rule spelled for one name only is a rule that is absent on those
    /// repositories - exactly where the reader would least expect it. `HEAD` because it is a symbolic
    /// ref rather than a branch and pushing it means "whatever is checked out", which is a
    /// destination nobody named.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> NeverPublished =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main", "master", "HEAD" };

    public async Task<TeamPublishReport> PublishAsync(
        string team,
        IReadOnlyList<string> repoUrls,
        ContainerId source,
        long? causation,
        CancellationToken ct)
    {
        if (repoUrls.Count == 0) return TeamPublishReport.Nothing;

        var outcomes = new List<RepoPublishOutcome>(repoUrls.Count);

        foreach (var url in repoUrls)
        {
            var outcome = await PublishOneAsync(team, url, ct);
            outcomes.Add(outcome);
            await ReportAsync(outcome, source, causation, ct);
        }

        return new TeamPublishReport(outcomes);
    }

    private async Task<RepoPublishOutcome> PublishOneAsync(string team, string url, CancellationToken ct)
    {
        string repo;

        try
        {
            repo = RepoUrls.DeriveName(url);
        }
        catch (UriFormatException)
        {
            // A URL THIS CANNOT NAME IS NOT THIS OPERATION'S TO REPORT ON. `RepoUrls.Validate`
            // refuses one at the door, so a stored team cannot hold it; if one ever gets past, a
            // completion is the wrong place to discover it and a row about it would name nothing
            // a reader could act on.
            return new RepoPublishOutcome(url, TeamPublishResult.NoRepository, []);
        }

        string clonePath;

        try
        {
            clonePath = Path.Combine(paths.ReposFor(team), repo, "main");
        }
        catch (KeyNotFoundException)
        {
            // `RootFor` THROWS for a team nobody registered, deliberately - see TeamPaths. A
            // completion is not the place to repair that, and it is not a push failure either.
            return new RepoPublishOutcome(repo, TeamPublishResult.NoRepository, []);
        }

        // A TEAM WITH NO CLONE IS THE ORDINARY CASE, NOT A FAULT. It is every measurement team, and
        // every team whose clone the platform could not make and already reported at creation.
        if (!Directory.Exists(clonePath)) return new RepoPublishOutcome(repo, TeamPublishResult.NoRepository, []);

        try
        {
            return await PushBranchesAsync(repo, clonePath, defaultBranchOf?.Invoke(team, repo), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER's cancellation, which is a request going away. Nothing to report to.
            throw;
        }
        catch (OperationCanceledException)
        {
            // `GitRunner` bounds every operation and THROWS when its own timer expires, having
            // killed the process tree. That is a transport failure wearing an exception, and it is
            // exactly the condition this type exists to name - so it is caught HERE rather than
            // allowed to become a 500 on a route whose job is to accept work.
            return new RepoPublishOutcome(
                repo, TeamPublishResult.PushFailed, [],
                "git ran out of time and was stopped. The origin may be unreachable.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new RepoPublishOutcome(
                repo, TeamPublishResult.PushFailed, [], GitOutputRedaction.Redact(exception.Message));
        }
    }

    private async Task<RepoPublishOutcome> PushBranchesAsync(
        string repo, string clonePath, string? defaultBranch, CancellationToken ct)
    {
        var candidates = (await git.LocalBranchesAsync(clonePath, ct))
            .Where(branch => !NeverPublished.Contains(branch)
                && !string.Equals(branch, defaultBranch, StringComparison.Ordinal))
            .ToList();

        var unpublished = new List<string>(candidates.Count);

        foreach (var branch in candidates)
        {
            // A LOCAL QUESTION BEFORE A NETWORK ONE. `git push` on a branch already on origin costs
            // a round trip and answers 0, so a team with a dozen branches would pay a dozen of them
            // on every single workflow completion. This counts commits reachable from the branch and
            // from NO remote-tracking ref, which is a read of this clone's own object store.
            //
            // NULL MEANS THE COUNT COULD NOT BE TAKEN, AND THAT PUSHES. The recoverable direction
            // here is towards durability: a redundant push is a round trip, a skipped one is the
            // loss this whole item exists to stop.
            var ahead = await git.CountCommitsNotOnAnyRemoteAsync(clonePath, branch, ct);

            if (ahead is 0) continue;

            unpublished.Add(branch);
        }

        if (unpublished.Count == 0) return new RepoPublishOutcome(repo, TeamPublishResult.NothingToPublish, []);

        var published = new List<string>(unpublished.Count);
        var rejected = new List<string>();
        string? rejection = null;

        for (var index = 0; index < unpublished.Count; index++)
        {
            var branch = unpublished[index];

            // BY REFSPEC, NAMING BOTH ENDS. `PushRefspecAsync` does not consult HEAD and the server
            // refuses anything that is not a fast-forward, so the destination is decided here and
            // by nothing else - which is what makes `NeverPublished` above a real guarantee rather
            // than a hope about what git would have chosen.
            var run = await git.PushRefspecAsync(clonePath, branch, branch, ct);

            if (run.ExitCode == 0)
            {
                published.Add(branch);
                continue;
            }

            // A REFUSED REF IS ONE BRANCH'S PROBLEM, NOT THE TRANSPORT'S. Origin answered, so the
            // branches after it can still go. Stopping here would hold every later branch back
            // because one had been amended after it was pushed.
            var stderr = string.IsNullOrWhiteSpace(run.Stderr) ? run.Stdout : run.Stderr;
            if (IsRefusedRef(stderr))
            {
                rejected.Add(branch);
                rejection ??= GitOutputRedaction.Redact(stderr);
                continue;
            }

            // STOPS AT THE FIRST FAILURE, and the reason is the commonest cause. An unreachable
            // origin, an expired credential and a missing git all fail EVERY branch identically, so
            // carrying on would spend one bounded timeout per branch inside a route a member is
            // waiting on, to learn the same sentence each time. Everything not yet attempted is
            // reported as not published, because that is what it is.
            return new RepoPublishOutcome(
                repo,
                TeamPublishResult.PushFailed,
                rejected.Concat(unpublished.Skip(index)).ToList(),

                // REDACTED AT THE WRITE, WHICH IS HERE - the boundary where git's own bytes become a
                // value this process passes on. git quotes the remote URL into `unable to access
                // '<url>'`, and a token in that URL's userinfo would otherwise reach an append-only
                // log that is read back into other agents' prompts. Nothing downstream of this line
                // ever holds the raw text, so no later caller can forget.
                GitOutputRedaction.Redact(
                    string.IsNullOrWhiteSpace(run.Stderr) ? run.Stdout : run.Stderr));
        }

        if (rejected.Count > 0)
            return new RepoPublishOutcome(repo, TeamPublishResult.Rejected, rejected, rejection, published);

        return new RepoPublishOutcome(repo, TeamPublishResult.Published, published);
    }

    /// <summary>Origin was reached and refused this ref because it is not a fast-forward.</summary>
    internal static bool IsRefusedRef(string gitOutput) =>
        gitOutput.Contains("[rejected]", StringComparison.Ordinal)
        || gitOutput.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
        || gitOutput.Contains("fetch first", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// THE ROWS A REPORTED OUTCOME BECOMES, OR NONE FOR ONE THAT IS DELIBERATELY SILENT. A refused
    /// branch writes two: a receipt for the branches that did go, and the failure for the one that did not.
    ///
    /// <para>
    /// <b>PUBLIC AND PURE BECAUSE THIS IS THE WRITE, AND THE WRITE IS WHERE REDACTION LIVES.</b> A
    /// credential that reaches an append-only log cannot be taken back, and a
    /// rule enforced only by a call site remembering is absent wherever nobody remembered. Every row
    /// this type appends is composed HERE and nowhere else, so there is one line to read and one
    /// line to test - and it is testable without a repository, a network or a process, which is what
    /// makes "a credential cannot reach the log" a spec somebody will actually run.
    /// </para>
    ///
    /// <para>
    /// <b>IT REDACTS AGAIN, OVER TEXT THAT WAS ALREADY REDACTED AT THE GIT BOUNDARY.</b> That is not
    /// a redundancy to tidy away: redaction is idempotent, it costs one pass over a bounded string,
    /// and the two sites answer different questions. The boundary one keeps raw bytes out of a value
    /// this process passes around; this one keeps them off the log whatever any future caller
    /// constructs an outcome from.
    /// </para>
    ///
    /// <para>
    /// SILENCE IS THE ANSWER FOR "NOTHING NEEDED DOING". A receipt on every workflow completion of
    /// every team that has no code, or whose branches are all already on origin, is a type its
    /// readers learn to skip - and this one has to be readable on the day it says something.
    /// </para>
    ///
    /// <para>
    /// THE REPOSITORY IS NAMED BY ITS FOLDER NAME, NEVER BY ITS URL. A URL is where a credential
    /// lives, and the folder name is what the Repo Dashboard and the worktrees skill already call it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Type, string Payload)> RowsFor(RepoPublishOutcome outcome)
    {
        var repo = GitOutputRedaction.Redact(outcome.Repo) ?? "";

        switch (outcome.Result)
        {
            case TeamPublishResult.NoRepository or TeamPublishResult.NothingToPublish:
                return [];

            case TeamPublishResult.Published:
                return [Pushed(repo, outcome.Branches)];

            case TeamPublishResult.Rejected:
                var rows = new List<(string Type, string Payload)>(2);
                if (outcome.Published is { Count: > 0 } published) rows.Add(Pushed(repo, published));
                rows.Add(Failed(repo, outcome, retryable: false));
                return rows;

            default:
                return [Failed(repo, outcome, retryable: true)];
        }
    }

    private static (string Type, string Payload) Pushed(string repo, IReadOnlyList<string> branches) =>
        (MessageTypes.RepoPushed, JsonSerializer.Serialize(new
        {
            repo,
            branches = GitOutputRedaction.Redact(string.Join(", ", branches)),
        }));

    // RETRYABLE IS ON THE ROW rather than only in the documentation beside it, because neither
    // `EventCatalog`'s summary nor the sentence `MessageText` composes is persisted. True for
    // transport: nothing moved on origin and the same push can succeed later. False for a refused
    // ref: origin holds a commit this clone rewrote away, and the same push fails every time until
    // a person reconciles the branch.
    private static (string Type, string Payload) Failed(string repo, RepoPublishOutcome outcome, bool retryable) =>
        (MessageTypes.RepoPushFailed, JsonSerializer.Serialize(new
        {
            repo,
            branches = GitOutputRedaction.Redact(string.Join(", ", outcome.Branches)),
            reason = GitOutputRedaction.Redact(outcome.Reason) is { Length: > 0 } reason
                ? reason
                : "git failed and said nothing.",
            retryable,
        }));

    private async Task ReportAsync(
        RepoPublishOutcome outcome, ContainerId source, long? causation, CancellationToken ct)
    {
        foreach (var row in RowsFor(outcome))
            await log.AppendAsync(new NewMessage(row.Type, row.Payload, source.ToString(), causation), ct);
    }
}
