using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE TWO BOUNDS ON A WORKFLOW, AS SENTENCES: the chain limit and the workflow budget. Said in ONE
/// place, because `tell` refuses with them and a wake is refused at its ask and skipped at its fire
/// with them, and an agent reading two wordings of one bound would take them for two rules.
/// </summary>
public static class WorkflowBounds
{
    /// <summary>A message at <paramref name="depth"/> would pass the chain limit.</summary>
    public static string DepthRefusal(int depth) =>
        $"This job is already {depth} steps deep, which is the limit. "
        + "Something is going round in circles. Stop dispatching and report what you "
        + "have to whoever asked you.";

    /// <summary>The workflow has spent past its budget since its last nudge.</summary>
    public static string BudgetRefusal(long spent, long bound) =>
        $"This workflow has spent {spent:N0} tokens, which exceeds the limit of {bound:N0}. "
        + "Something is going too far. Stop dispatching and report what you "
        + "have to whoever asked you.";

    /// <summary>
    /// The budget's own sentence when <paramref name="correlation"/> is over its team's figure now,
    /// or null when it is not: the figure through `EffectiveWorkflowBudgetFor` and the spend since
    /// the last nudge, as the `tell` route and the pump read them. Unmeasured spend never convicts.
    /// </summary>
    public static async Task<string?> OverBudgetAsync(
        TeamRegistry teams, IMessageLog log, string team, long correlation, CancellationToken ct)
    {
        if (teams.EffectiveWorkflowBudgetFor(team) is not { } bound) return null;

        var spend = await log.GetSpendSinceNudgeAsync(correlation, ct);

        return spend.RunsWithMeasuredUsage > 0 && spend.TokensSpent > bound
            ? BudgetRefusal(spend.TokensSpent, bound)
            : null;
    }
}
