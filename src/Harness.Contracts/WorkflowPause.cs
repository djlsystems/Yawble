namespace Harness.Contracts;

/// <summary>
/// WHICH FIGURE STOPPED A WORKFLOW, as a wire value rather than a literal at three call sites.
///
/// Two bounds can produce one pause - the team's own per-workflow figure and the instance's
/// `WorkflowSpendLimit` backstop - and "you are over budget" and "the platform stopped you" are
/// different sentences to the person reading them. A row that recorded only "over budget" leaves a reader unable to tell which
/// number to go and change.
/// </summary>
public static class LimitSources
{
    /// <summary>The team's own per-workflow budget fired. A person may change it on the team's
    /// settings screen.</summary>
    public const string Team = "team";

    /// <summary>The instance `WorkflowSpendLimit` fired, because the team has chosen nothing. It
    /// has no screen, deliberately - see `ContainerHost.OverBudgetAsync`.</summary>
    public const string Instance = "instance";
}

/// <summary>
/// WHETHER ONE WORKFLOW IS PAUSED, asked of the correlation's own thread.
///
/// <para>
/// ONE DEFINITION, BECAUSE IT IS ASKED IN THREE PLACES - the projection that builds
/// <see cref="TeamWorkflowTiming"/>, the pump's at-most-once check before it writes a pause row,
/// and <c>IdleWorkflowOffer</c>'s candidate test. Two of three changed is silent: a projection
/// that says PAUSED while the pump writes a second pause row, or an offer fired into a workflow
/// the pump will refuse to deliver it to, both look like the feature working.
/// </para>
///
/// <para>
/// IN <c>Contracts</c> AND NOT <c>Messaging</c>. <c>ContainerHost</c> lives in
/// <c>Harness.Containers</c> and <c>IdleWorkflowOffer</c> in <c>Harness.Host</c>, and the
/// first of those does not reference <c>Harness.Messaging</c> at all. <c>SqliteMessageStore</c>
/// expresses the same predicate as SQL for its own correlation-scoped query - it must be written
/// to look like this one and say so, the way <c>WorkflowOpenSql</c> documents its three callers.
/// </para>
///
/// <para>
/// THE PREDICATE: a correlation is paused when its newest <c>workflow.paused</c> row has no
/// <c>workflow.resumed</c> row after it. Newest-wins rather than a count, because a workflow can
/// be paused, resumed and paused again - counting would make the second pause invisible once the
/// first resume had landed.
/// </para>
/// </summary>
public static class WorkflowPause
{
    /// <summary>
    /// Whether this correlation is paused, read off a thread the caller already holds.
    ///
    /// <paramref name="thread"/> is one correlation's rows. Order is not assumed: the newest row of
    /// each kind is found by <c>Seq</c>, so a caller that read the thread in any order gets the same
    /// answer as one that read it in <c>Seq</c> order.
    /// </summary>
    public static bool IsPaused(IReadOnlyList<Message> thread) => PausedBy(thread) is not null;

    /// <summary>
    /// The newest <c>workflow.paused</c> row when <see cref="IsPaused"/>, else null.
    ///
    /// Returns the ROW rather than its payload so a caller can read whichever fields it needs -
    /// the projection wants <c>OccurredAt</c>, the reason and the limit; the pump wants only
    /// whether there is one.
    /// </summary>
    public static Message? PausedBy(IReadOnlyList<Message> thread)
    {
        Message? paused = null;
        long resumedAt = 0;

        foreach (var message in thread)
        {
            if (string.Equals(message.Type, MessageTypes.WorkflowPaused, StringComparison.Ordinal))
            {
                if (paused is null || message.Seq > paused.Seq) paused = message;
            }
            else if (string.Equals(message.Type, MessageTypes.WorkflowResumed, StringComparison.Ordinal))
            {
                if (message.Seq > resumedAt) resumedAt = message.Seq;
            }
        }

        return paused is not null && paused.Seq > resumedAt ? paused : null;
    }
}
