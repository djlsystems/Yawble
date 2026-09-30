namespace Harness.Contracts;

/// <summary>
/// THE ACCOUNTING, READ. Every finished run's time and tokens and every workflow's completion or
/// close, as <c>usage_ledger</c> and <c>workflow_ledger</c> keep them: written with the log row they
/// account for, and never updated or deleted - not by Reset, not by team deletion, not by retention.
/// The spend reads on <see cref="IMessageLog"/> (<c>GetWorkflowSpendAsync</c>,
/// <c>GetSpendSinceNudgeAsync</c>, <c>GetTriggerSpendAsync</c>) read the same table.
/// </summary>
public interface IUsageLedger
{
    /// <summary>
    /// <paramref name="member"/>'s last <paramref name="max"/> runs, newest first - one row per run,
    /// never a batched run's extra rows. What a member's recent cost reads.
    /// </summary>
    Task<IReadOnlyList<UsageLedgerRow>> ReadRecentRunsAsync(
        ContainerId member, int max, CancellationToken ct = default);

    /// <summary>
    /// The NEWEST completion or close of one workflow, or null when it has none. A workflow woken
    /// after a close and declared again has a row for each; the newest is the answer.
    /// </summary>
    Task<WorkflowLedgerRow?> ReadWorkflowAsync(long correlation, CancellationToken ct = default);

    /// <summary>
    /// One page of the ledger for export: runs that ended and workflows that closed at or after
    /// <paramref name="since"/>, newest first, with a seq below <paramref name="before"/> when it is
    /// given. Runs and workflows share one seq cursor (both are keyed by the log row they account
    /// for): the page is the newest <paramref name="take"/> of the two together.
    /// </summary>
    Task<LedgerPage> ReadPageAsync(
        DateTimeOffset since, long? before, int take, CancellationToken ct = default);
}

/// <summary>One <c>usage_ledger</c> row: one finished run. NULL token figures are "not measured",
/// never zero.</summary>
public sealed record UsageLedgerRow(
    long RunSeq,
    long Correlation,
    string? TeamId,
    string? TeamName,
    string Member,
    string? MemberKind,
    string? AgentPreset,
    string RunOutcome,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset EndedAt,
    bool Measured,
    long? TokensIn,
    long? TokensCachedIn,
    long? TokensCacheCreation,
    long? TokensOut,
    long? TokensCombined,
    long? Billable,
    string? TriggerSource,
    DateTimeOffset? TriggerFiredAt,
    bool Backfilled);

/// <summary>One <c>workflow_ledger</c> row: one completion or close of a workflow.
/// <see cref="ElapsedSeconds"/> is that workflow's elapsed time and is never summed across
/// workflows.</summary>
public sealed record WorkflowLedgerRow(
    long CloseSeq,
    long Correlation,
    string? TeamId,
    string? TeamName,
    DateTimeOffset? RootAt,
    DateTimeOffset ClosedAt,
    string HowClosed,
    long? OutcomeIdAtClose,
    bool Backfilled)
{
    public const string Completed = "completed";
    public const string Closed = "closed";

    public double? ElapsedSeconds => RootAt is { } root ? (ClosedAt - root).TotalSeconds : null;
}

/// <summary>A page of the ledger, and the seq to pass as <c>before</c> for the next, older page
/// (null when this page is the last).</summary>
public sealed record LedgerPage(
    IReadOnlyList<UsageLedgerRow> Runs,
    IReadOnlyList<WorkflowLedgerRow> Workflows,
    long? NextBefore);
