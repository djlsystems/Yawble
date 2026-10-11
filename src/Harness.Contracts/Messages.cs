namespace Harness.Contracts;

/// <summary>
/// A message on the log, as stored. Everything an Agent Container reacts to, and everything it
/// publishes about itself, is one of these.
///
/// <see cref="Seq"/>, <see cref="CorrelationId"/> and <see cref="Depth"/> are **assigned by the log**
/// and cannot be supplied by a publisher — see <see cref="NewMessage"/> for why that is deliberate
/// rather than tidy.
/// </summary>
/// <param name="Seq">Position on the log. Monotonic, gap-free per append, and the identity of the message.</param>
/// <param name="Type">What happened, or what is being asked for. The only thing a subscription matches on.</param>
/// <param name="Payload">JSON. Opaque to the log.</param>
/// <param name="Source">Who published it — an AC's qualified id (<c>Team/Name</c>), a console, the
/// host itself.</param>
/// <param name="CorrelationId">
/// The workflow this message belongs to: the <see cref="Seq"/> of the message that began the thread.
/// A message with no cause correlates to itself, so every message is always in exactly one workflow
/// and no separate identifier has to be generated or remembered.
/// </param>
/// <param name="CausationSeq">The message that directly caused this one, if any.</param>
/// <param name="Depth">
/// Hops from the root of the causation chain. Zero for a message with no cause. This is the only
/// value that can bound a runaway: manager to worker to completion to manager is a cycle with no
/// natural floor, and depth is what gives it one.
/// </param>
public sealed record Message(
    long Seq,
    string Type,
    string Payload,
    string Source,
    long CorrelationId,
    long? CausationSeq,
    int Depth,
    DateTimeOffset OccurredAt);

/// <summary>
/// A message on the way in.
///
/// It carries no Seq, no CorrelationId and no Depth **on purpose**. All three are derived by the log
/// from <see cref="CausationSeq"/>, and a publisher able to set them could forge a causation chain —
/// declaring depth 0 on the thousandth hop of a loop, or claiming a workflow it was never part of.
/// Depth is the only bound on a runaway, so the value that determines it must not be an input.
///
/// This is also what makes correlation propagation free: a container
/// passes the seq of the message that woke it as <see cref="CausationSeq"/>, and the thread follows
/// with nothing to remember and nothing to forget.
/// </summary>
/// <param name="CausationSeq">
/// The message being reacted to, or null to begin a new workflow. Null is a deliberate act — it is
/// how a manager starts unrelated work — rather than the shrug it looks like.
/// </param>
public sealed record NewMessage(
    string Type,
    string Payload,
    string Source,
    long? CausationSeq = null);
