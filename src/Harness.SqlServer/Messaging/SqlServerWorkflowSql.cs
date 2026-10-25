using Harness.Contracts;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Messaging;

/// <summary>
/// WHEN IS A WORKFLOW OPEN, AND WHICH ROWS NAME ONE - the T-SQL of SQLite's <c>WorkflowOpenSql</c> and
/// <c>NotAWorkflowSql</c>, whose remarks carry the argument for both predicates. Kept beside them in
/// shape and in parameter names so the two providers' readers can be compared line by line; only the
/// dialect differs (<c>@</c> parameters, and the case-insensitive <c>LIKE</c> SQLite has by default).
/// </summary>
internal static class SqlServerWorkflowSql
{
    /// <summary>
    /// Correlated against an outer alias `m`. Requires <c>@completed</c>, <c>@closed</c>,
    /// <c>@instructionPrefix</c> and <c>@cardPrefix</c>; <see cref="BindNotClosed"/> binds them.
    /// </summary>
    public const string NotClosed =
        $"""
        NOT EXISTS (
            SELECT 1 FROM messages d
            WHERE d.correlation_id = m.correlation_id
              AND d.type IN (@completed, @closed)
              AND NOT EXISTS (
                  SELECT 1 FROM messages woke
                  WHERE woke.correlation_id = d.correlation_id
                    AND woke.seq > d.seq
                    AND (woke.type LIKE @instructionPrefix {SqlServerSql.Like} OR woke.type LIKE @cardPrefix {SqlServerSql.Like})))
        """;

    public static void BindNotClosed(SqlCommand command)
    {
        command.Parameters.AddWithValue("@completed", MessageTypes.WorkflowCompleted);
        command.Parameters.AddWithValue("@closed", MessageTypes.WorkflowClosed);
        BindWakePrefixes(command);
    }

    /// <summary>The two prefixes a re-opening row's type starts with: an addressed instruction, a card event.</summary>
    public static void BindWakePrefixes(SqlCommand command)
    {
        command.Parameters.AddWithValue("@instructionPrefix", MessageTypes.InstructionPrefix + "%");
        command.Parameters.AddWithValue("@cardPrefix", MessageTypes.KanbanCardPrefix + "%");
    }

    /// <summary>
    /// True for a row that may put its correlation among a team's workflows: a member's removal is not
    /// one. Requires <c>@notAWorkflow</c>; <see cref="BindCounts"/> binds it.
    /// </summary>
    public static string Counts(string alias) => $"{alias}.type <> @notAWorkflow";

    public static void BindCounts(SqlCommand command) =>
        command.Parameters.AddWithValue("@notAWorkflow", MessageTypes.ContainerRemoved);
}
