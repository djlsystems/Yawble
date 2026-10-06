using Harness.Contracts;
using Harness.Kanban;

namespace Harness.Host;

/// <summary>
/// WHICH OUTCOME EACH CARD'S WORK SERVES - the board's card tag and its Outcome filter.
///
/// <para>
/// A card's outcome is its OPEN workflow's (<see cref="KanbanCard.OpenWorkflow"/>), else its latest
/// workflow's, so run this after <see cref="CardOpenWorkflows.AttachAsync{T}"/>. A workflow's outcome
/// is its newest link followed through <c>merged_into</c>; a card whose workflow has none carries
/// null and shows no tag.
/// </para>
///
/// <para>
/// ONE QUERY PER BOARD READ, whatever the board's size (<see cref="IOutcomeStore.CurrentOutcomesAsync"/>).
/// </para>
/// </summary>
public static class CardOutcomes
{
    /// <summary>Every card of <paramref name="cards"/>, each carrying its outcome or null.</summary>
    public static async Task<IReadOnlyList<T>> AttachAsync<T>(
        IReadOnlyList<T> cards, IOutcomeStore outcomes, CancellationToken ct = default)
        where T : KanbanCard
    {
        if (cards.Count == 0) return cards;

        var found = await outcomes.CurrentOutcomesAsync(cards.Select(WorkflowOf).Distinct().ToList(), ct);

        return cards
            .Select(card => (T)(((KanbanCard)card) with { Outcome = found.GetValueOrDefault(WorkflowOf(card)) }))
            .ToList();
    }

    /// <summary>
    /// THE OUTCOME FILTER, over cards that already carry their outcome: an id keeps the cards whose
    /// tag names it, <see cref="KanbanFilter.NoOutcome"/> keeps the cards with no tag, and nothing
    /// keeps every card. Applied after the projection, over cards already in the caller's scope, so
    /// it narrows and never widens. A merged outcome's id is resolved to the outcome its work moved
    /// to, which is what the cards carry.
    /// </summary>
    public static async Task<IReadOnlyList<T>> MatchingAsync<T>(
        IReadOnlyList<T> cards, string? outcome, IOutcomeStore outcomes, CancellationToken ct = default)
        where T : KanbanCard
    {
        // SEVERAL MAY BE ASKED (`KanbanFilter.Values`): a card is kept when its tag is any of them,
        // and `none` among them keeps the cards with no tag as well.
        var asked = KanbanFilter.Values(outcome);
        if (asked.Count == 0) return cards;

        var none = false;
        var wanted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in asked)
        {
            if (string.Equals(value, KanbanFilter.NoOutcome, StringComparison.OrdinalIgnoreCase))
            {
                none = true;
                continue;
            }

            // Only a merged id asks again, and only when that id is asked for.
            var id = value;
            for (var hop = 0; hop < 64 && await outcomes.FindAsync(id, ct) is
                 { Status: OutcomeStatus.Merged, MergedInto: { } next }; hop++)
            {
                id = next;
            }

            wanted.Add(id);
        }

        return cards
            .Where(card => card.Outcome is null ? none : wanted.Contains(card.Outcome.Id))
            .ToList();
    }

    /// <summary>The workflow whose outcome <paramref name="card"/> shows.</summary>
    private static long WorkflowOf(KanbanCard card) =>
        card.OpenWorkflow?.Workflow ?? CardOpenWorkflows.WorkflowsOf(card).Max();
}
