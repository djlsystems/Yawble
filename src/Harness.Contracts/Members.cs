namespace Harness.Contracts;

/// <summary>
/// HOW A MEMBER DOES ONE PIECE OF WORK. The seam between the member runtime - which owns
/// everything a member IS: its subscription, queue, admission, marks, rows and cancellation - and
/// whatever actually performs the work: a coding-agent CLI, an installed plugin executable.
///
/// STATELESS AND PER KIND, NOT PER MEMBER. One runner serves every member of its kind, and what a
/// member runs is resolved from <see cref="MemberInvocation.Implementation"/> on every invocation,
/// never cached per member: a per-member binding is two stores of one fact, and it drifted three
/// times before the catalog rule was written (see <c>ContainerHost</c> and <c>AgentCatalog</c>).
/// That is why this is a RUNNER and not an <c>IMember</c>: the member is the runtime plus its
/// persisted row; this is how it runs.
///
/// Deliberately absent: system prompts as a concept, prompt text, model usage as a requirement,
/// agent authentication, MCP, provider keys, live views and session ids. Those belong to the
/// agent implementation.
/// </summary>
public interface IMemberRunner
{
    Task<MemberResult> RunAsync(MemberInvocation invocation, CancellationToken ct = default);
}

/// <summary>One piece of work for one member.</summary>
/// <param name="Member">Who is running.</param>
/// <param name="Implementation">
/// What this member runs, as stored on <c>team_members.agent</c>: an Agent preset name such as
/// <c>claude-headless</c>, or <c>plugin:&lt;id&gt;</c> for an installed plugin. Read off the
/// member's definition on every wake.
/// </param>
/// <param name="Work">The batch, oldest first, all of one workflow; <c>Work[0]</c> woke it.</param>
/// <param name="Environment">The member's environment for this run, <c>HARNESS_CAUSATION</c> and
/// the card's worktree variables included.</param>
public sealed record MemberInvocation(
    ContainerId Member,
    string Implementation,
    IReadOnlyList<Message> Work,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    MemberRunContext Context)
{
    /// <summary>What every row this run causes names as its cause: the waking message.</summary>
    public long Causation => Work[0].Seq;
}

/// <summary>What the runtime knows about this run that an implementation may need.</summary>
/// <param name="FloorSeq">Everything at or below this seq belongs to whatever came before this
/// member; history is read above it.</param>
/// <param name="Worktrees">The card's worktrees, one per repository, or empty.</param>
/// <param name="BranchHint">The suggested branch for those worktrees, or null when there are none.</param>
/// <param name="UnreachableRoot">The team's root folder when it could not be reached at startup,
/// which every implementation must refuse on rather than run somewhere else.</param>
/// <param name="Instructions">
/// The member's standing instructions - today an agent member's composed system prompt, and empty
/// for a plugin. Opaque here: composing it is <c>TeamRegistry</c>'s business and reading it the
/// agent runner's.
/// </param>
public sealed record MemberRunContext(
    long FloorSeq,
    ArtifactLimits Limits,
    IReadOnlyList<RepoWorktree> Worktrees,
    string? BranchHint,
    string? UnreachableRoot,
    string Instructions);

/// <summary>What one piece of work came to.</summary>
/// <param name="Succeeded">Whether the run completed. The runner decides: an agent's rule has
/// three terms, a plugin's is its own result record.</param>
/// <param name="ExitCode">The process's exit code, -1 when it did not run to an exit.</param>
/// <param name="Output">What the card, the feed and the manager read. Written to the transcript
/// whole and excerpted on the row as <c>output</c>.</param>
/// <param name="LaunchError">The sentence for a run that did not begin or was cut off, written to
/// the row as <c>launchError</c>; null for a run that ran to an exit.</param>
/// <param name="FailureReason">What the failed mark says. Null on a success; a runner that fails
/// without one gets the runtime's plain fallback.</param>
/// <param name="FailureClass">One of <see cref="FailureClasses"/>, or null for
/// <see cref="FailureClasses.Unknown"/>.</param>
/// <param name="Usage">Metered usage, when the implementation measures any. Null is "not
/// measured" and is written as absent keys, never zeros.</param>
/// <param name="Transcript">A session transcript the implementation wrote, recorded on the row
/// for a person to read later.</param>
/// <param name="Quiet">A successful run that found nothing anyone needs to be woken for: its
/// `completed` row is written as usual, marked <see cref="PayloadFields.Quiet"/>, and wakes no
/// subscriber. Ignored on a failure, which always wakes. Only a plugin's result record sets it.</param>
public sealed record MemberResult(
    bool Succeeded,
    int ExitCode,
    string Output,
    string? LaunchError = null,
    string? FailureReason = null,
    string? FailureClass = null,
    DateTimeOffset? RetryAfter = null,
    int? ProcessId = null,
    InvocationUsage? Usage = null,
    RunTranscript? Transcript = null,
    bool Quiet = false)
{
    /// <summary>A run that did not begin or was cut off: the sentence is both the launch error
    /// and the reason.</summary>
    public static MemberResult NotRun(string sentence, string? failureClass = null, RunTranscript? transcript = null) =>
        new(false, -1, string.Empty, sentence, sentence, failureClass, Transcript: transcript);
}

/// <summary>A run's own session transcript: the absolute path its implementation wrote, and the
/// live-view format it is read in.</summary>
public sealed record RunTranscript(string Path, string Format);

/// <summary>
/// THE ONLY WAY AN IMPLEMENTATION REPORTS DURING A RUN WITHOUT A CREDENTIAL, with exactly the
/// effects the MCP routes have: the same row, the same mark, the same snapshot push, the same
/// idle-clock reset. Agents reach these through their MCP tools and the routes; a plugin through
/// its stdout records. Both go through one implementation so the two paths cannot drift.
/// </summary>
public interface IMemberReports
{
    Task<MemberReportOutcome> ProgressAsync(ContainerId member, string status, CancellationToken ct = default);

    Task<MemberReportOutcome> BlockedAsync(ContainerId member, string reason, int? item = null, CancellationToken ct = default);

    /// <summary>
    /// Defers one item of the running batch by its 1-based number: it is not closed when the run
    /// ends but delivered again as its own next run. Refused for the only item of a run.
    /// </summary>
    Task<MemberReportOutcome> DeferAsync(ContainerId member, int item, string reason, CancellationToken ct = default);

    Task<MemberReportOutcome> NeedsDecisionAsync(ContainerId member, string question, CancellationToken ct = default);

    Task<MemberReportOutcome> HandbackAsync(ContainerId member, string delivered, CancellationToken ct = default);

    /// <summary>
    /// Appends an event of <paramref name="type"/> with <paramref name="payload"/> (a JSON object),
    /// sourced as the member and caused by the message its current run is handling, so it joins
    /// that workflow. Only a type <see cref="EventCatalog"/> knows as <see cref="EventPublisher.Plugin"/>'s
    /// is accepted: a platform type is refused, whoever asks. Whose events a caller may publish is
    /// the caller's check.
    /// </summary>
    Task<MemberReportOutcome> PublishAsync(ContainerId member, string type, string payload, CancellationToken ct = default);
}

/// <summary>What a report came to: accepted, or refused with the sentence the route answers with.</summary>
public sealed record MemberReportOutcome(bool Accepted, string? Refusal = null, int Status = 204)
{
    public static readonly MemberReportOutcome Ok = new(true);

    public static MemberReportOutcome Refused(string sentence, int status = 409) => new(false, sentence, status);
}
