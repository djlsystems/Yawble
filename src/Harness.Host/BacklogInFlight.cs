using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHERE A BACKLOG ITEM IS BEING WORKED RIGHT NOW - what the list shows beside an item that has
/// been dispatched and whose workflow has not ended.
///
/// <para>
/// NOTHING HERE IS STORED. The dispatch record already names the team and the correlation, and the
/// log already says whether that correlation is open. This is a read of both, made once per list
/// request, and it is why the item's team link is NOT rewritten on dispatch and there is NO third
/// <c>BacklogState</c>: who may SEE an item, who is WORKING it and whether it is DONE are three
/// facts, and folding the second into either of the others would lose one of them.
/// </para>
/// </summary>
/// <param name="TeamId">The team the current dispatch went to. It exists, or there is no record.</param>
/// <param name="TeamName">That team's CURRENT label, not the one denormalised at dispatch time.</param>
/// <param name="Correlation">The workflow - the seq of the <c>backlog.item.dispatched</c> row.</param>
/// <param name="Running">
/// Whether a member of that team is <c>Running</c> under this workflow AT THIS INSTANT. Read off
/// the live container snapshots, which is free. An open workflow with nobody running is an ordinary
/// shape - a Manager that has not declared yet - and the screen shows the two differently because
/// a person deciding whether to intervene wants to know which.
/// </param>
public sealed record BacklogInFlight(string TeamId, string TeamName, long Correlation, bool Running);

/// <summary>
/// Derives <see cref="BacklogInFlight"/> for a set of items from their CURRENT dispatches.
///
/// <para>
/// ONE QUERY FOR THE SCREEN, NOT ONE PER ROW. The caller reads every item's latest dispatch in one
/// store query (<see cref="IBacklogStore.LatestDispatchesAsync"/>); this asks the log ONE
/// membership question over those correlations (<see cref="IMessageLog.OpenWorkflowsAmongAsync"/>)
/// and reads the rest from memory. The per-request cost is therefore two SQLite queries in total,
/// whatever the list's length, plus a scan of the container snapshots per open workflow.
/// </para>
///
/// <para>
/// THE OPEN PREDICATE IS THE PLATFORM'S OWN - no terminal row nothing has woken since, as the
/// Teams table counts open workflows - so the backlog and the Teams table can never disagree about
/// whether a workflow is over. And THE FLOOR IS THE TEAM'S, as every other reader of a team's
/// history applies it: a dispatch below a recreated team's floor belongs to the previous
/// incarnation and is not in flight, however open its rows read.
/// </para>
/// </summary>
public static class BacklogInFlightState
{
    /// <summary>
    /// In-flight state keyed by ITEM id, for every dispatch in <paramref name="latest"/> whose team
    /// still exists, whose correlation is above that team's floor, and whose workflow is open. Every
    /// other item is absent - and absent is what the wire renders as null.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, BacklogInFlight>> ForAsync(
        IEnumerable<BacklogDispatch> latest,
        TeamRegistry teams,
        ContainerHost host,
        IMessageLog log,
        CancellationToken ct = default)
    {
        var result = new Dictionary<long, BacklogInFlight>();
        var candidates = new List<(BacklogDispatch Dispatch, string Team)>();
        var floors = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var dispatch in latest)
        {
            // A TEAM THAT IS GONE IS WORKING NOTHING, however open its rows read: team deletion
            // never touches the log, so without this a deleted team's dispatch would be in flight
            // forever. The detail still shows the dispatch, marked `teamGone`; this is the list's
            // one-word answer and the word is no.
            if (teams.ExistingName(dispatch.TeamId) is not { } stored) continue;

            // THE TEAM'S FLOOR, memoised per team rather than per row. A dispatch below it belongs
            // to a previous incarnation of a team recreated under the same name.
            if (!floors.TryGetValue(stored, out var floor))
            {
                floor = teams.FloorFor(stored);
                floors[stored] = floor;
            }

            if (dispatch.Correlation <= floor) continue;

            candidates.Add((dispatch, stored));
        }

        if (candidates.Count == 0) return result;

        // THE ONE LOG QUERY. Distinct, because two items can in principle share a correlation only
        // by a fault, and asking twice would not make the answer different.
        var open = await log.OpenWorkflowsAmongAsync(
            candidates.Select(c => c.Dispatch.Correlation).Distinct().ToList(), ct);

        if (open.Count == 0) return result;

        // READ ONCE, not once per candidate: `Snapshots()` walks every live container.
        var snapshots = host.Snapshots();

        foreach (var (dispatch, stored) in candidates)
        {
            if (!open.Contains(dispatch.Correlation)) continue;

            result[dispatch.Item] = new BacklogInFlight(
                stored,
                teams.LabelFor(stored),
                dispatch.Correlation,
                RunningUnder(dispatch.Correlation, snapshots));
        }

        return result;
    }

    /// <summary>
    /// Whether any live container is <c>Running</c> under <paramref name="correlation"/>. Its own
    /// method so the answer can be pinned against synthetic snapshots rather than a live team, whose
    /// Manager runs on its own clock.
    ///
    /// <para>
    /// `CurrentCorrelation` IS WHAT MAKES THIS NARROW, exactly as it does in
    /// <c>WorkflowBusyState</c>: a member Running under another workflow on the same team is not
    /// this item's business, and counting it would light every item that team holds the moment
    /// any one of them was picked up.
    /// </para>
    /// </summary>
    public static bool RunningUnder(long correlation, IEnumerable<ContainerSnapshot> snapshots) =>
        snapshots.Any(s => s.State == ContainerState.Running && s.CurrentCorrelation == correlation);
}
