namespace Harness.Contracts;

/// <summary>
/// How much of an artifact may travel.
///
/// CHARACTERS, not tokens: an AC cannot count an arbitrary agent's tokens — a worker may not be a
/// model at all — and a character budget is at least honest about being an approximation rather than
/// pretending to a precision it does not have. Token estimates are recorded on the completed/failed
/// payload as reported by the Agent; they do not replace these character bounds.
///
/// A global default overridable per AC, exactly like the queue ceiling.
/// </summary>
/// <param name="ExcerptChars">
/// How much agent output is copied into the completed/failed payload. The payload IS the ledger and
/// the ledger is what the next prompt is built from, so this one bound applies all the way down.
/// </param>
/// <param name="ContextChars">How much rendered history one invocation may receive.</param>
public sealed record ArtifactLimits(int ExcerptChars = 4_000, int ContextChars = 32_000)
{
    public static readonly ArtifactLimits Default = new();
}
