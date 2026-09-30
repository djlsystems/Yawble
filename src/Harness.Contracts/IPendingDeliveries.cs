namespace Harness.Contracts;

/// <summary>One delivery a container accepted and has not finished.</summary>
/// <param name="Started">Whether the container had picked it up when the host stopped. An
/// un-started delivery is resumed; a started one is reported, because nothing can know how far
/// into it the agent got.</param>
/// <param name="DeferredFromRun">Set when the member DEFERRED this delivery out of a batched run:
/// the seq of that run's <c>agentContainer.started</c> row. Such a delivery is queued again as its
/// own next run, not closed. Null on every other delivery.</param>
public sealed record PendingDelivery(long Seq, bool Started, long? DeferredFromRun = null);

/// <summary>One delivery a team member accepted and has not finished. See <see cref="PendingDelivery"/>.</summary>
public sealed record TeamPendingDelivery(string Subscriber, long Seq, bool Started, long? DeferredFromRun = null);

/// <summary>
/// What a container has accepted and not yet finished, durably.
///
/// The queue itself is an in-memory Channel and the cursor advances when a message is OFFERED
/// rather than when it completes, so without this a host that stops between the two loses the work
/// with no redelivery to fall back on.
/// </summary>
public interface IPendingDeliveries
{
    /// <summary>Records an accepted delivery. Idempotent, and must not clear <c>Started</c> — a
    /// re-offer of something already in flight is not a fresh delivery.</summary>
    Task AddAsync(ContainerId subscriber, long seq, CancellationToken ct = default);

    Task StartAsync(ContainerId subscriber, long seq, CancellationToken ct = default);

    Task RemoveAsync(ContainerId subscriber, long seq, CancellationToken ct = default);

    /// <summary>
    /// Puts a delivery the member DEFERRED back in the queue: un-started again, naming the run it
    /// was deferred from. Not a removal - a deferred item is not closed, it is delivered again as
    /// its own run, and a restart in between re-offers it rather than losing or failing it.
    /// </summary>
    Task DeferAsync(ContainerId subscriber, long seq, long fromRun, CancellationToken ct = default);

    /// <summary>
    /// Removes EVERY outstanding delivery for a subscriber, for a container that has been deleted.
    /// Returns how many rows went, so a caller can say what it swept.
    /// </summary>
    /// <remarks>
    /// This table is keyed on <c>Team/Name</c> and has no foreign key, so nothing removes these rows
    /// when a team goes. Left behind, they are inherited by the next container created with the same
    /// qualified name - a recreated team's <c>Manager</c> picking up its predecessor's interrupted
    /// work and publishing <c>container.failed</c> for something it never accepted.
    /// </remarks>
    Task<int> RemoveAllAsync(ContainerId subscriber, CancellationToken ct = default);

    /// <summary>
    /// Removes EVERY outstanding delivery belonging to a team, whatever the container is called.
    /// Returns how many rows went.
    /// </summary>
    /// <remarks>
    /// BY TEAM, NOT BY ENUMERATED CONTAINER. <see cref="RemoveAllAsync"/> needs a container
    /// something can still name, so a sweep built on it misses any row whose container the registry
    /// cannot enumerate - skipped by restoration, cleanup threw mid-loop, or left by an older build.
    /// Those are precisely the rows the remark above warns about: inherited by the next container
    /// created with the same qualified name.
    ///
    /// REQUIRED rather than defaulted - a defaulted purge lets a store accept the call and drop it.
    /// </remarks>
    Task<int> RemoveAllForTeamAsync(string team, CancellationToken ct = default);

    /// <summary>This subscriber's outstanding deliveries, oldest first.</summary>
    Task<IReadOnlyList<PendingDelivery>> ForAsync(
        ContainerId subscriber, CancellationToken ct = default);

    /// <summary>This team's outstanding deliveries, oldest first within each subscriber.</summary>
    Task<IReadOnlyList<TeamPendingDelivery>> ForTeamAsync(
        string team, CancellationToken ct = default);

    /// <summary>Every outstanding delivery of every subscriber, for the start's sweep of what
    /// deleted teams left behind.</summary>
    Task<IReadOnlyList<TeamPendingDelivery>> AllAsync(CancellationToken ct = default);
}
