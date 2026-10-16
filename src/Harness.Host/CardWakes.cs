using Harness.Contracts;
using Harness.Kanban;

namespace Harness.Host;

/// <summary>
/// THE PENDING WAKES ON EACH CARD: those asked in the workflow the card is open in
/// (<see cref="KanbanCard.OpenWorkflow"/>), the card's own member's first, then by due time. Run
/// after <see cref="CardOpenWorkflows.AttachAsync{T}"/>. One read of the wakes per fetch, whatever the
/// board's size; a card with none carries null and its JSON has no <c>wakes</c>.
/// </summary>
public static class CardWakes
{
    public static async Task<IReadOnlyList<T>> AttachAsync<T>(
        IReadOnlyList<T> cards, IWakeStore wakes, CancellationToken ct = default)
        where T : KanbanCard
    {
        if (cards.Count == 0 || !cards.Any(card => card.OpenWorkflow is not null)) return cards;

        var byWorkflow = (await wakes.AllAsync(ct))
            .GroupBy(w => w.Workflow)
            .ToDictionary(g => g.Key, g => g.ToList());

        return cards
            .Select(card =>
            {
                if (card.OpenWorkflow is not { } open || !byWorkflow.TryGetValue(open.Workflow, out var found))
                {
                    return card;
                }

                var ordered = found
                    .OrderBy(w => string.Equals(w.Member, card.Member, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(w => w.DueAt)
                    .Select(w => w.View())
                    .ToList();

                return (T)(((KanbanCard)card) with { Wakes = ordered });
            })
            .ToList();
    }
}
