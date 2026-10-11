using Harness.Kanban;

namespace Harness.Host;

/// <summary>
/// The cards a workflow would leave behind if it were declared complete now.
///
/// Declaring a workflow complete moves every one of its cards to Done, so a card still in To Do,
/// one whose member was interrupted, or one that failed or blocked and was never picked back up
/// would read as delivered. A host restart can interrupt a developer mid-card, and without this
/// nothing but the Manager's attention would stand between that card and a silent Done.
///
/// A card is settled when its member handed back, its run completed, or it is already Done - on
/// its status or in the Done lane, where a person may have moved it. The declaring member's own
/// cards are excluded: it is running by definition, being the one making the call.
///
/// A workflow's cards include the ones it RESUMED: a card planned in one workflow and claimed with
/// <c>tell --card</c> in another is a loose end of both (<see cref="KanbanCard.BelongsTo"/>).
/// </summary>
public static class WorkflowLooseEnds
{
    private static readonly HashSet<string> Settled =
        new(StringComparer.OrdinalIgnoreCase) { "handback", "completed", "done" };

    /// <summary>Whether a card is settled: handed back, completed, or Done on its status or in the
    /// Done lane. Anything else - To Do, running, interrupted, failed, blocked - is still open.
    /// The same rule decides which cards' worktrees may be removed (<see cref="WorktreeRemoval"/>).</summary>
    public static bool IsSettled(KanbanCard card) =>
        Settled.Contains(card.Status)
        || string.Equals(card.LaneId, KanbanLanes.Done, StringComparison.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<string>> DescribeAsync(
        KanbanStore kanban, string team, long correlation, string declaringMember)
    {
        var board = await kanban.GetBoardAsync(new KanbanFilter(Team: team));

        return board.Cards
            .Where(card => card.BelongsTo(correlation))
            .Where(card => !string.Equals(card.Member, declaringMember, StringComparison.OrdinalIgnoreCase))
            .Where(card => !IsSettled(card))
            .Select(card => $"card {card.Id} '{card.Title}' ({card.Member ?? "unassigned"}, {card.Status})")
            .ToList();
    }
}
