namespace Harness.Messaging;

/// <summary>
/// WHEN IS A WORKFLOW OPEN - as SQL, in ONE place, because it is asked in THREE.
///
/// `WorkflowsForTeamAsync` counts the open ones uncapped and takes the span over them, and
/// `OpenWorkflowsAmongAsync` asks the same question of a caller's own correlations. They MUST share
/// the predicate: two of three changed is silent, and the count and the span would then describe
/// different sets of workflows.
///
/// `WorkflowsForTeamAsync`'S LIST DOES NOT USE IT, and that is deliberate rather than a site
/// somebody forgot. The list is every workflow the team has run since its floor, open and closed alike;
/// filtering it here would render a team's three FINISHED workflows as one row under a heading
/// claiming the team held it open. The count beside that list uses this predicate, which is why
/// `openCount` and `workflows.length` are allowed to disagree.
///
/// OPEN MEANS NO TERMINAL ROW THAT NOTHING HAS WOKEN SINCE. Not "no terminal row ever", which would make a
/// re-woken workflow uncountable forever; and NOT "no terminal row followed by any row", which is
/// the trap:
///
/// AN AGENT DECLARES FROM INSIDE ITS RUN, so the platform publishes that run's `container.completed`
/// moments afterwards - seconds, sometimes tens of seconds. A predicate that
/// re-opened on ANY later row would therefore re-open every workflow ever declared, and the count
/// would never fall.
///
/// So only rows that WAKE a container re-open one: an addressed instruction, or a kanban card event
/// that a manager subscribes to. `container.started` / `completed` / `progress` / `failed` /
/// `blocked` are the tail of work already counted.
///
/// The shape is `TimingForAsync`'s `declarations` query one level up - a declaration is live unless
/// something has happened since - and is deliberately written to look like it.
///
/// THE `woke` SUBQUERY CARRIES NO TEAM FILTER, matching the existing unfiltered `d` subquery: a
/// correlation spanning two teams re-opens for BOTH the moment either one receives a later
/// instruction or card event, even one addressed to the other team. Defensible - work in that
/// workflow genuinely did continue - but a new degree of freedom, so it is written down rather than
/// left for someone to discover by tracing a cross-team correlation.
/// </summary>
internal static class WorkflowOpenSql
{
    /// <summary>
    /// Correlated against an outer alias `m`. Requires parameters <c>$completed</c>, <c>$closed</c>,
    /// <c>$instructionPrefix</c> and <c>$cardPrefix</c>.
    /// </summary>
    public const string NotClosed =
        """
        NOT EXISTS (
            SELECT 1 FROM messages d
            WHERE d.correlation_id = m.correlation_id
              AND d.type IN ($completed, $closed)
              AND NOT EXISTS (
                  SELECT 1 FROM messages woke
                  WHERE woke.correlation_id = d.correlation_id
                    AND woke.seq > d.seq
                    AND (woke.type LIKE $instructionPrefix OR woke.type LIKE $cardPrefix)))
        """;

    /// <summary>Binds the four parameters the fragment needs. One call site per command, so a query
    /// cannot use the predicate and forget half its parameters.</summary>
    public static void Bind(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        command.Parameters.AddWithValue("$completed", Contracts.MessageTypes.WorkflowCompleted);
        command.Parameters.AddWithValue("$closed", Contracts.MessageTypes.WorkflowClosed);
        command.Parameters.AddWithValue("$instructionPrefix", Contracts.MessageTypes.InstructionPrefix + "%");
        command.Parameters.AddWithValue("$cardPrefix", Contracts.MessageTypes.KanbanCardPrefix + "%");
    }
}
