namespace Harness.Contracts;

/// <summary>
/// What is fed into the NEXT invocation — the artifact that stops an agent
/// being amnesiac between wakes.
///
/// Reads the LEDGER, never a transcript. The hazard runs both ways: concatenating history
/// exhausts a context window in a handful of runs, and feeding nothing back makes every invocation
/// amnesiac. So this is bounded, and it says when it dropped something.
///
/// MVP: derived on demand, not stored. Writing a new context artifact becomes worth it when
/// compaction has a summary that needs a home; until then there is nothing to store that the ledger
/// does not already hold.
/// </summary>
public interface IContextBuilder
{
    /// <summary>
    /// The history to hand this container, excluding <paramref name="waking"/> itself — that arrives
    /// separately as the prompt, and repeating it would have the agent read its own instruction
    /// twice. Empty when there is no history.
    /// </summary>
    /// <param name="sinceSeq">
    /// The container's floor — see <see cref="ILedger.ReadAsync"/>. A container remembers nothing
    /// from before it existed, or a new team's manager reads a previous one's conversation.
    /// </param>
    Task<string> BuildAsync(
        ContainerId container,
        Message waking,
        ArtifactLimits limits,
        long sinceSeq,
        CancellationToken ct = default);
}
