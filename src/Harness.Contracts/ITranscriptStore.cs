namespace Harness.Contracts;

/// <summary>
/// Where one run's raw bytes live.
///
/// DERIVED, never generated: one dequeued message produces exactly one invocation, so the waking
/// message's seq is already a unique key per run — and it is the same seq that carries the
/// correlation, so a transcript is tied to its workflow with nothing to remember and nothing to
/// forget.
/// </summary>
public sealed record TranscriptRef(ContainerId Container, long CauseSeq)
{
    public override string ToString() => $"{Container}/{CauseSeq}";
}

/// <summary>
/// Raw stdout and stderr of every run.
///
/// The consumer is a HUMAN, on demand. The context builder reads the ledger and
/// never this, which is what stops an agent that prints a megabyte from reaching the next prompt —
/// conflating the two is how a raw transcript ends up in a model's context.
/// </summary>
public interface ITranscriptStore
{
    Task<TranscriptRef> WriteAsync(
        ContainerId container, long causeSeq, string content, CancellationToken ct = default);

    /// <summary>The stored bytes, or null when nothing was written for that run.</summary>
    Task<string?> ReadAsync(TranscriptRef reference, CancellationToken ct = default);
}
