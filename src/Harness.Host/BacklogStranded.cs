using Harness.Contracts;
using Harness.Kanban;

namespace Harness.Host;

/// <summary>
/// A DISPATCH WHOSE WORK FINISHED SOMEWHERE ELSE. The item's workflow is still open and was Blocked
/// or Failed, while a LATER workflow on the same team took one of its cards to Done - the work was
/// re-sent as a new workflow and finished there, and the original was left open with nobody inside
/// it to declare it.
///
/// <para>
/// THIS IS A NOTICE AND AN OFFER, NEVER AN ACT. Only the owner of a workflow declares it and only a
/// person closes one, so the platform does not close the original, does not mark the item and does
/// not wake anybody. It says what happened and names the existing person-only close route
/// (<c>POST /api/teams/{team}/workflows/{correlation}/close</c>); a person decides.
/// </para>
///
/// <para>
/// NOTHING HERE IS STORED, exactly as <see cref="BacklogInFlight"/>: the dispatch names the
/// workflow, the log says its state, and the board says where its cards went.
/// </para>
/// </summary>
/// <param name="TeamId">The team the dispatch went to.</param>
/// <param name="Workflow">The dispatch's own workflow, open, Blocked or Failed.</param>
/// <param name="State">The log's word for it: <c>Blocked</c> or <c>Failed</c>.</param>
/// <param name="ContinuedIn">The latest later workflow a card of it was done in.</param>
/// <param name="Cards">The original workflow's cards that were done in a later one.</param>
/// <param name="Notice">What the row says, in a sentence.</param>
/// <param name="Close">The close a person may make. Offered, not taken.</param>
public sealed record BacklogStranded(
    string TeamId,
    long Workflow,
    string State,
    long ContinuedIn,
    IReadOnlyList<string> Cards,
    string Notice,
    BacklogStrandedClose Close);

/// <param name="Route">The existing person-only close route for the original workflow.</param>
/// <param name="By">Always <c>person</c>: the route is <c>.HumansOnly()</c>.</param>
/// <param name="Reason">A suggested reason, which a person may change.</param>
public sealed record BacklogStrandedClose(string Route, string By, string Reason);

/// <summary>Derives <see cref="BacklogStranded"/> for items already known to be in flight.</summary>
public static class BacklogStrandedState
{
    /// <summary>The two workflow states that leave a dispatch stranded when its work finished
    /// elsewhere. Anything else - Running, Awaiting, Undeclared - is a workflow still being
    /// worked or waiting on its own Manager, and is not the platform's to point at.</summary>
    public static readonly IReadOnlySet<string> Stuck =
        new HashSet<string>(StringComparer.Ordinal) { "Blocked", "Failed" };

    /// <summary>
    /// Stranded state keyed by ITEM id. ONLY IN-FLIGHT ITEMS ARE ASKED: a closed or completed
    /// workflow is not stranded, and <see cref="BacklogInFlightState"/> has already applied the
    /// team's existence, its floor and the open predicate. The board is read once per team, and
    /// only for a team with a Blocked or Failed dispatch, so an ordinary list costs one log query
    /// per in-flight item and no board read at all.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, BacklogStranded>> ForAsync(
        IReadOnlyDictionary<long, BacklogInFlight> inFlight,
        TeamRegistry teams,
        IMessageLog log,
        KanbanStore kanban,
        CancellationToken ct = default)
    {
        var result = new Dictionary<long, BacklogStranded>();
        var boards = new Dictionary<string, KanbanBoard>(StringComparer.OrdinalIgnoreCase);

        foreach (var (item, flight) in inFlight)
        {
            var timing = await log.WorkflowForTeamAsync(
                flight.TeamId, flight.Correlation, teams.FloorFor(flight.TeamId), ct);

            if (timing.State is not { } state || !Stuck.Contains(state)) continue;

            if (!boards.TryGetValue(flight.TeamId, out var board))
            {
                board = await kanban.GetBoardAsync(new KanbanFilter(Team: flight.TeamId));
                boards[flight.TeamId] = board;
            }

            if (Of(flight.TeamId, flight.Correlation, state, board.Cards) is { } stranded)
            {
                result[item] = stranded;
            }
        }

        return result;
    }

    /// <summary>
    /// The pure rule, over a board already read: the cards of <paramref name="workflow"/> that are
    /// in Done and were told in a LATER workflow. Null when there are none.
    /// </summary>
    public static BacklogStranded? Of(string team, long workflow, string state, IEnumerable<KanbanCard> cards)
    {
        var moved = cards
            .Where(card => card.BelongsTo(workflow))
            .Where(card => string.Equals(card.LaneId, KanbanLanes.Done, StringComparison.OrdinalIgnoreCase))
            .Select(card => (card.Id, Later: (card.Workflows ?? [card.WorkflowSeq]).Where(w => w > workflow).DefaultIfEmpty(0).Max()))
            .Where(pair => pair.Later > 0)
            .ToList();

        if (moved.Count == 0) return null;

        var continuedIn = moved.Max(pair => pair.Later);
        var notice = $"The work continued in workflow {continuedIn}.";

        return new BacklogStranded(
            team,
            workflow,
            state,
            continuedIn,
            [.. moved.Select(pair => pair.Id)],
            notice,
            new BacklogStrandedClose(
                $"/api/teams/{Uri.EscapeDataString(team)}/workflows/{workflow}/close",
                "person",
                $"{state}; the work continued in workflow {continuedIn}."));
    }
}
