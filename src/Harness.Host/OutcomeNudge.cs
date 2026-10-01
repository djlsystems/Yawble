using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE NUDGE: one line in a MANAGER's run context saying what its workflow is for - the outcome it
/// serves, or that it has none and how to choose one. Every other member's context is the inner
/// builder's, unchanged. Added after the history, so nothing the history says moves.
///
/// <para>
/// A Manager never roots a workflow, so the links made at the roots (a dispatch, a trigger, a
/// person's or the Concierge's <c>tell</c>) leave it whatever arrives unlinked; this is how it
/// learns which those are. The line is a nudge, not a gate: the gate is
/// <see cref="OutcomeGate"/>, off by default.
/// </para>
/// </summary>
public sealed class OutcomeNudge(IContextBuilder inner, IOutcomeStore outcomes) : IContextBuilder
{
    public const string NoOutcome =
        "This workflow has no outcome. Choose one with `outcome` list/set, or propose one.";

    public static string Serves(Outcome outcome) =>
        $"This workflow serves the outcome \"{outcome.Name}\" ({outcome.Status}, id {outcome.Id}).";

    public async Task<string> BuildAsync(
        ContainerId container, Message waking, ArtifactLimits limits, long sinceSeq, CancellationToken ct = default)
    {
        var history = await inner.BuildAsync(container, waking, limits, sinceSeq, ct);

        if (!string.Equals(container.Name, TeamRegistry.DefaultManagerName, StringComparison.OrdinalIgnoreCase)
            || waking.CorrelationId == 0)
        {
            return history;
        }

        var line = await LineAsync(waking.CorrelationId, ct);
        return string.IsNullOrEmpty(history) ? line : history + "\n\n" + line;
    }

    /// <summary>The line for <paramref name="correlation"/>: the outcome its newest link names,
    /// followed through a merge, or <see cref="NoOutcome"/>.</summary>
    public async Task<string> LineAsync(long correlation, CancellationToken ct = default)
    {
        if (await outcomes.CurrentLinkAsync(correlation, ct) is not { OutcomeId: { } linked } link) return NoOutcome;

        var all = await outcomes.ListAsync(ct);
        var holder = OutcomeFigures.Resolution(all).GetValueOrDefault(linked, linked);
        var outcome = all.FirstOrDefault(o => o.Id == holder);

        return outcome is null
            ? $"This workflow serves the outcome \"{link.OutcomeNameAtLink}\"."
            : Serves(outcome);
    }
}
