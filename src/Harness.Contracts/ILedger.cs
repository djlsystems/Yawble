namespace Harness.Contracts;

/// <summary>
/// One Agent Container's immutable record: what it was commanded, and what came back, in order.
///
/// A PROJECTION over the message log, not a second store. The log is already append-only, ordered
/// and correlated, so a separate ledger would mean two writes per event that must not diverge —
/// against a log write that is deliberately best-effort. One write path means the ledger cannot be
/// wrong about what happened.
///
/// <see cref="IMessageLog"/> deliberately knows nothing about containers, so this is a second reader
/// over the same table rather than a query added there.
///
/// RETENTION: this is the source for re-deriving context, so purging below it destroys ground truth
/// — which is the thing additive compaction exists to protect. Nothing purges today; a future purge
/// must respect the ledger, and <see cref="ICursors.SlowestAsync"/> is where that conversation
/// happens.
/// </summary>
public interface ILedger
{
    /// <summary>
    /// The most recent <paramref name="max"/> entries strictly after <paramref name="sinceSeq"/> and
    /// strictly before <paramref name="beforeSeq"/>, oldest first.
    /// </summary>
    /// <param name="sinceSeq">
    /// The container's FLOOR — the log's head at the moment this container came into existence.
    ///
    /// Load-bearing for isolation, not an optimisation. The log outlives any team, and a
    /// <see cref="ContainerId"/> can be reused once its container is deleted; without a floor a
    /// newly created container adopts every message a previous same-(team, name) one ever
    /// published, and the derived context carries stale history into this one's prompt.
    ///
    /// <see cref="ICursors"/> already holds this line for DELIVERY — a container created now must
    /// not wake up owing a day of other people's work. This is the same rule applied to memory.
    ///
    /// Zero reads the whole ledger, which is what an audit wants.
    /// </param>
    /// <param name="correlation">
    /// WHICH WORKFLOW, or null for every one of them.
    ///
    /// REQUIRED AND POSITIONAL, exactly as <paramref name="sinceSeq"/> is and for the identical
    /// reason: a scope a caller can omit is the same defect wearing a parameter list. Every call
    /// site has to decide, and a new one cannot fail to.
    ///
    /// A member holds several workflows at once and must keep them apart - it is woken for A, works
    /// on A, and is woken for B with B's history in front of it. The rows already carry
    /// `correlation_id`; nothing asked for it until now.
    ///
    /// THE BOUND IN <paramref name="max"/> IS APPLIED INSIDE THIS SCOPE, not after it. Reading the
    /// most recent `max` rows and filtering afterwards leaves the defect exactly where it was: the
    /// busy workflow still consumes the window and the quiet one is still starved, and every test
    /// that does not measure the two together still passes.
    ///
    /// Null is for an AUDIT, which wants the container's whole memory whatever workflow it belonged
    /// to. It is not a default and no production caller passes it.
    /// </param>
    /// <param name="beforeSeq">
    /// Exclusive upper bound. A container passes the seq of the message that woke it, so its own
    /// waking instruction is not also handed back to it as history. <see cref="long.MaxValue"/> for
    /// everything.
    /// </param>
    /// <param name="max">
    /// Bounded from the RECENT end, because the newest exchanges are the relevant ones, then
    /// returned oldest-first because that is what reads correctly. A limit on an ascending scan
    /// would keep the oldest entries and drop everything that just happened — the opposite of a
    /// memory.
    /// </param>
    Task<IReadOnlyList<Message>> ReadAsync(
        ContainerId container,
        long sinceSeq,
        long? correlation,
        long beforeSeq,
        int max,
        CancellationToken ct = default);
}
