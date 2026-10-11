using System.ComponentModel;

namespace Harness.Contracts;

/// <summary>
/// The status of one repository: its commit shas, push/merge state, worktrees, and freshness of origin checks.
/// </summary>
public sealed record RepoStatus(
    [property: Description("The repository name derived from its URL")]
    string Name,
    
    [property: Description("Absolute path of the main clone (repos/{name}/main). Null unless the caller is a person.")]
    string? ClonePath,
    
    [property: Description("40-character sha of local main branch, or null if missing")]
    string? MainSha,
    
    [property: Description("40-character sha of local team branch, or null if missing")]
    string? TeamSha,
    
    [property: Description("Commits on local main not in origin/main. Null when unknown.")]
    int? MainAhead,
    
    [property: Description("Commits on origin/main not in local main. Null when unknown.")]
    int? MainBehind,
    
    [property: Description("Whether the main clone has uncommitted changes")]
    bool Dirty,
    
    [property: Description("Checked-out branch of the clone named main, or 'detached'. Null if unknown. The directory name is not this value.")]
    string? HeadCheckout,
    
    [property: Description("The team branch name (always 'team/{storedTeamId}')")]
    string TeamBranch,
    
    [property: Description("True if team branch is pushed to origin. Null if origin unknown.")]
    bool? TeamPushed,

    [property: Description("The ref that answered TeamPushed: a local team/{id}, origin/team/{id}, or null when unanswered.")]
    string? TeamPushedFrom,
    
    [property: Description("True if team branch is merged to main, by ANCESTRY or by CONTENT - see TeamMergedToMainBy for which. Null if origin unknown, or if ancestry said no and the content question could not be measured; never false on an unmeasured content answer.")]
    bool? TeamMergedToMain,

    [property: Description("Which question answered TeamMergedToMain, and only ever one of three values. 'ancestry' when the team ref is reachable from origin/main. 'content' when it is not, but TeamCommitsNotOnMain is 0 - every commit on the branch is patch-equivalent to one already upstream, which is what a branch whose commits were replayed onto a new base looks like. Null is NOT MEASURED or NOT MERGED, and must never be read as false on its own: TeamMergedToMain carries that answer, and it is null when unknown too. The delete-remote-branch route records the same two words in its audit row, so the two surfaces cannot disagree about one repository.")]
    string? TeamMergedToMainBy,

    [property: Description("True if the clone's local main is reachable from the team ref, so commits on it are already on the team branch. Null when there is no team ref or origin was never checked - NOT false, which would accuse a team that never pushed.")]
    bool? CloneMainOnTeamBranch,

    [property: Description("How many commits on the team branch carry changes origin/main does not have, by PATCH-ID rather than by sha - so work integrated with a cherry-pick counts as present. 0 means every change is already upstream, possibly under different commits. Null is NOT MEASURED (no team ref, origin never checked, or git cherry failed) and must never be read as 0. Conservative on purpose: a commit whose patch was modified during integration counts, which errs toward refusing.")]
    int? TeamCommitsNotOnMain,

    [property: Description("ISO-8601 UTC of the last fetch, or null if never fetched")]
    string? OriginCheckedAt,
    
    [property: Description("Result of ls-remote for the origin URL. Null on cheap (no-refresh) read.")]
    bool? OriginReachable,
    
    [property: Description("Git's error message when origin is unreachable")]
    string? OriginUnreachableReason,
    
    [property: Description("Worktrees for this repository")]
    IReadOnlyList<WorktreeStatus> Worktrees,

    [property: Description("The repository's default branch, which every 'main' on this record means: local main is this branch and origin/main is origin/<this>. Null when it is NOT KNOWN - then every main-relative field is null too, and the actions that need it refuse rather than guess 'main'.")]
    string? DefaultBranch = null,

    [property: Description("Where DefaultBranch came from: 'person' (set in Team settings, kept across Fetches until cleared), 'remote' (what origin's HEAD named on the last clone or successful Fetch), or null when not known.")]
    string? DefaultBranchSource = null,

    [property: Description("The clone's origin remote: the repository URL in the team's list, with any credential taken out. In contributor mode it is the fork.")]
    string? OriginUrl = null,

    [property: Description("The clone's upstream remote in contributor mode, with any credential taken out; null for an owned repository. When set, MainAhead and MainBehind are measured against upstream/<DefaultBranch>, and Bring current and Rebase use it.")]
    string? UpstreamUrl = null,

    [property: Description("The account the fork belongs to in contributor mode - the head owner of a pull request opened from team/{id}. Null for an owned repository.")]
    string? ForkOwner = null,

    [property: Description("Whether \"Sign off commits (DCO)\" is on: Open pull request then refuses while any commit on team/{id} since upstream/<DefaultBranch> lacks Signed-off-by.")]
    bool DcoSignOff = false,

    [property: Description("A person's note that the upstream project's CLA is signed, shown beside Open pull request. A record only; the platform signs nothing. Null when none.")]
    string? ClaSignedNote = null,

    [property: Description("The pull request recorded for team/{id} in contributor mode, as GitHub last described it - asked at most once a minute. Null when none is recorded or the repository is owned.")]
    RepoPullRequestStatus? PullRequest = null,

    [property: Description("How many commits origin/<DefaultBranch> holds that the team ref (the one TeamPushedFrom names) lacks. Above 0 is what the Git dialog's Bring current and merge is for. Null when not measured: no team ref, origin never checked, the default branch not known, or contributor mode.")]
    int? TeamBranchBehindDefault = null,

    [property: Description("When TeamBranchBehindDefault is above 0, the files changed both on the team branch and on origin/<DefaultBranch> since the two parted, bounded to 20. Bring current and merge runs no tests, so the dialog recommends running the suites first when this is not empty. Null when not measured.")]
    IReadOnlyList<string>? FilesChangedOnBothSides = null,

    [property: Description("True when a local team/{id} exists and origin does not have it, or holds an older commit of it, so Push would publish it as a fast-forward: the dialog says 'team/{id} is not pushed'. False when origin has it (or has commits the clone lacks). Null when there is no local team branch or origin was never checked.")]
    bool? TeamBranchUnpushed = null,

    [property: Description("Set when the clone's default branch holds commits origin/<DefaultBranch> lacks - a Manager moved it, which the delivery rule forbids: \"moved <default> in the clone; the work is on <commit>; the team branch is team/{id}\". Measured on every read and never reset by the platform. Null when it has not moved or could not be measured (default branch not known, no clone, no origin ref).")]
    string? DefaultBranchMoved = null,

    [property: Description("False when there is no working clone: none was made, or what is there is an empty clone - a .git with no ref, no HEAD and nothing beside it, what an interrupted clone leaves. The card reads it 'Not ready' and offers Fetch, which makes the clone. True otherwise.")]
    bool CloneReady = true);

/// <summary>
/// The recorded pull request, as the Git dialog shows it: GitHub's last answer and when it
/// was read, the landing word it means, and why the current read is unknown when it is.
/// </summary>
public sealed record RepoPullRequestStatus(
    [property: Description("The pull request's URL on the upstream.")]
    string Url,

    [property: Description("The pull request's number on the upstream.")]
    int Number,

    [property: Description("GitHub's last answer: 'open', 'closed' (without merging) or 'merged'.")]
    string State,

    [property: Description("ISO-8601 UTC of when GitHub gave that answer.")]
    string ReadAt,

    [property: Description("What it means now: 'in-review' (open), 'declined' (closed without merging), 'landed' (merged), or 'unknown' when GitHub could not be asked this time.")]
    string Landing,

    [property: Description("Why this read is unknown - GitHub unreachable, or the token refused. Null when GitHub answered.")]
    string? UnknownReason);

/// <summary>
/// Status of one worktree: path, branch, and position relative to main.
/// </summary>
public sealed record WorktreeStatus(
    [property: Description("Absolute path of the worktree")]
    string Path,
    
    [property: Description("Branch name (without refs/heads/), or null if detached")]
    string? Branch,
    
    [property: Description("40-character HEAD sha, or null if not available")]
    string? Sha,
    
    [property: Description("Member identifier inferred from path, or null if unknown")]
    string? Member,

    [property: Description("Commits on this worktree not in local main. Null when unknown.")]
    int? AheadMain,
    
    [property: Description("Commits on local main not in this worktree. Null when unknown.")]
    int? BehindMain,

    /// <summary>
    /// THE CONTENT ANSWER FOR ONE WORKTREE, the field the Git dialog needs to ask
    /// for. <see cref="AheadMain"/> is SHA ANCESTRY, so a worktree whose commits were REBASED onto
    /// main reads ahead while every one of its changes is already upstream - and the tidy step
    /// REFUSES on that reading rather than warning, so without this a team that did everything right could not
    /// be closed out through the product at all.
    ///
    /// Counted by <c>git cherry origin/main &lt;branch&gt;</c>, exactly as
    /// <c>RepoStatus.TeamCommitsNotOnMain</c> counts it for the team ref. IT IS
    /// CONSERVATIVE and that direction is load-bearing: a commit whose patch was modified during
    /// integration (a conflict resolved) still counts as outstanding, so the count errs toward
    /// "not equivalent", which errs toward refusing the one action that destroys a checkout.
    ///
    /// NULL IS NOT MEASURED, NEVER ZERO. A detached HEAD has no branch to name and a failed
    /// <c>cherry</c> is not evidence of nothing outstanding.
    /// </summary>
    [property: Description(
        "Commits on this worktree's branch whose change is not on origin/main, by patch-id. "
        + "Null when not measured - a detached HEAD, or an unmeasurable worktree.")]
    int? CommitsNotOnMain,

    [property: Description(
        "The card this tree is for - the key in its name, `wt_<Member>_<key>`: a card id, or "
        + "`w<correlation>` for an instruction that named no card. Null for any other tree.")]
    string? Card = null,

    [property: Description(
        "Whether the tree's card (or its workflow) is still open, so the clean-up leaves it. "
        + "False for a settled tree or one that is not a card's; null when not asked.")]
    bool? Open = null,

    [property: Description("Bytes the tree takes on disk, links not followed. Null when not measured.")]
    long? SizeBytes = null);

/// <summary>
/// Prerequisite git or gh executable.
/// </summary>
public sealed record Prerequisite(
    [property: Description("The executable name: 'git' or 'gh'")]
    string Command,
    
    [property: Description("Whether the executable was resolved on PATH")]
    bool Resolves,
    
    [property: Description("Human-readable status message")]
    string Message,
    
    [property: Description("Who uses this: 'platform' for git, 'agents' for gh")]
    string UsedBy);

/// <summary>
/// The status of all repositories for a team.
/// </summary>
public sealed record TeamRepoStatus(
    [property: Description("Git prerequisite status")]
    Prerequisite Git,
    
    [property: Description("GitHub CLI prerequisite status")]
    Prerequisite Gh,
    
    [property: Description("Repository statuses")]
    IReadOnlyList<RepoStatus> Repos,

    [property: Description("Whether a person has turned on concierge.mayMerge, so the Concierge may merge a "
        + "team branch with the repo tool's merge. Read when this status is read.")]
    bool ConciergeMayMerge = false);

/// <summary>
/// Result of an action: the repo name, its new status, a human message, and whether the action
/// did what it set out to do.
///
/// THE STATUS IS THE POINT. Every action route answers this, composed AFTER the git work, so a
/// card can repaint from the response rather than firing a second request to ask what it just
/// changed - and so the two answers cannot disagree in the window between them.
///
/// <see cref="Success"/> IS NOT REDUNDANT WITH THE STATUS CODE. The fetch route deliberately
/// answers 200 when origin is unreachable - a fact about the network, not a failure of the dialog -
/// and reports it as data, leaving the card's existing values wearing their age. Without this flag
/// "origin fetched" and "could not reach origin" are the same response shape, and the ladder cannot
/// tell whether the rung is done. The reason travels in <see cref="RepoStatus.OriginReachable"/>
/// and <see cref="RepoStatus.OriginUnreachableReason"/> rather than in fields of its own.
/// </summary>
public sealed record RepoActionResult(
    [property: Description("The repository name")]
    string Repo,

    [property: Description("The repository status after the action")]
    RepoStatus Status,

    [property: Description("Human-readable result message")]
    string Message,

    [property: Description("Whether the action did what it set out to do. False on the fetch route's reported-not-refused unreachable-origin arm, which is still a 200.")]
    bool Success);
