namespace Harness.Messaging;

/// <summary>
/// A MEMBER'S REMOVAL IS NOT A WORKFLOW - as SQL, in ONE place, because every reader that decides
/// what a team's workflows are asks it.
///
/// `MemberDeletion` appends one `agentContainer.removed` row with no causation, so the row is the
/// root of a correlation of its own. Nothing ever declares or closes that correlation, and read as a
/// workflow it is open forever with no terminal fact: `Running`, which the board shows as NO RESULT,
/// and the worst state of the team wins the tile.
///
/// THE RULE IS ON THE READ, NOT ON THE ROW. The row stays exactly as written - its place, its type,
/// its empty payload - because the Kanban projector unassigns the member's cards from it, and a rule
/// on the read covers every row already on a log the same way it covers one written tomorrow, with
/// nothing closed, declared or rewritten. So a team's workflows are the correlations its OTHER rows
/// are in: a removal row never puts its correlation on the list, in a count or under the span, and
/// a correlation that also holds other rows of the team is listed by those.
/// </summary>
internal static class NotAWorkflowSql
{
    /// <summary>
    /// True for a row that may put its correlation among a team's workflows, over the table alias
    /// <paramref name="alias"/>. Requires the parameter <c>$notAWorkflow</c>.
    /// </summary>
    public static string Counts(string alias) => $"{alias}.type <> $notAWorkflow";

    /// <summary>Binds the parameter the fragment needs.</summary>
    public static void Bind(Microsoft.Data.Sqlite.SqliteCommand command) =>
        command.Parameters.AddWithValue("$notAWorkflow", Contracts.MessageTypes.ContainerRemoved);
}
