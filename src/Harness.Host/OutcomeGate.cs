using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE OUTCOME GATE: the tenant setting <c>outcomes.requireForCompletion</c>, off by default and
/// read through <paramref name="required"/> on every declaration, never captured.
///
/// <para>
/// On, an AGENT's <c>workflow-complete</c> for a workflow with no outcome is refused (409) with a
/// sentence naming the <c>outcome</c> tool. It is asked by that route only: a person's close
/// (<c>POST /api/teams/{team}/workflows/{correlation}/close</c>) and the platform's own declarations
/// (<c>declaredByPlatform</c>, <see cref="UndeclarableWorkflows"/>) never reach it, so it never
/// blocks a person or the platform.
/// </para>
/// </summary>
public sealed class OutcomeGate(Func<bool> required, IOutcomeStore outcomes)
{
    public const string Refusal =
        "This workflow has no outcome, and this instance requires one before a workflow is declared "
        + "complete. Link it with the `outcome` tool - a Manager's: `list` the outcomes and `set` the "
        + "one this work serves, or `propose` one - then declare again.";

    /// <summary>The refusal for declaring <paramref name="correlation"/>, or null when it may be.</summary>
    public async Task<string?> RefusalAsync(long correlation, CancellationToken ct = default)
    {
        if (!required()) return null;
        return await outcomes.CurrentLinkAsync(correlation, ct) is null ? Refusal : null;
    }
}
