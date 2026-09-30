using Harness.Contracts;
using Harness.Kanban;

namespace Harness.Host;

/// <summary>
/// WHICH OPEN WORKFLOW EACH CARD BELONGS TO, and the row that joins it - what `kanban` and `status`
/// show so a Concierge asked to resume a card continues that card's workflow instead of rooting a
/// new one.
///
/// <para>
/// A card belongs to every workflow it was planned, claimed or told in (<see cref="KanbanCard.Workflows"/>).
/// The one named is the NEWEST of those still open, by the platform's own predicate
/// (<see cref="IMessageLog.OpenWorkflowsAmongAsync"/>), so the board, the Teams table and the
/// backlog never disagree about whether a workflow is over. A card whose workflows have all ended
/// names none: resuming it is new work.
/// </para>
///
/// <para>
/// ONE LOG QUERY PER FETCH, whatever the board's size. The latest row of each workflow is read off
/// the rows the caller already projected the board from.
/// </para>
/// </summary>
public static class CardOpenWorkflows
{
    /// <summary>Every card of <paramref name="cards"/>, each carrying its open workflow or null.</summary>
    public static async Task<IReadOnlyList<T>> AttachAsync<T>(
        IReadOnlyList<T> cards,
        IReadOnlyList<Message> messages,
        IMessageLog log,
        CancellationToken ct = default)
        where T : KanbanCard
    {
        if (cards.Count == 0) return cards;

        var open = await log.OpenWorkflowsAmongAsync(
            cards.SelectMany(WorkflowsOf).Distinct().ToList(), ct);

        var latest = new Dictionary<long, long>();
        foreach (var message in messages)
        {
            if (!open.Contains(message.CorrelationId)) continue;
            if (!latest.TryGetValue(message.CorrelationId, out var seq) || message.Seq > seq)
                latest[message.CorrelationId] = message.Seq;
        }

        return cards
            .Select(card => (T)(((KanbanCard)card) with { OpenWorkflow = Pick(card, open, latest) }))
            .ToList();
    }

    /// <summary>The workflows <paramref name="card"/> belongs to, oldest first.</summary>
    public static IReadOnlyList<long> WorkflowsOf(KanbanCard card) =>
        card.Workflows is { Count: > 0 } workflows ? workflows : [card.WorkflowSeq];

    private static CardWorkflow? Pick(
        KanbanCard card, IReadOnlySet<long> open, IReadOnlyDictionary<long, long> latest)
    {
        foreach (var workflow in WorkflowsOf(card).OrderByDescending(w => w))
        {
            if (!open.Contains(workflow)) continue;
            return new CardWorkflow(workflow, latest.TryGetValue(workflow, out var seq) ? seq : workflow);
        }

        return null;
    }
}
