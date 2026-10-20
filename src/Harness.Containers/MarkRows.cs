namespace Harness.Containers;

/// <summary>
/// The log row a member's trouble mark was set from: its seq, its workflow, when it was written, and
/// the card it is about when the run that wrote it named one. Memory only, beside the mark, never on
/// the snapshot. Until the row is in hand (the run's terminal row is appended after its mark) it is
/// the mark itself: no seq, the running workflow, and the instant the mark was set.
/// </summary>
public sealed record MarkRow(long? Seq, long? Correlation, DateTimeOffset At, string? Card);

/// <summary>
/// The rows behind a member's live <c>blocked</c>, <c>failed</c> and <c>needs-decision</c> marks, each
/// null when that mark is not set or was set with no row in hand.
/// </summary>
public sealed record MarkRows(MarkRow? Blocked, MarkRow? Failed, MarkRow? NeedsDecision);
