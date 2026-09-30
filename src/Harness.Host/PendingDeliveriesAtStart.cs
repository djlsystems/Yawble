using Harness.Contracts;

namespace Harness.Host;

/// <summary>What the start's sweep removed, and what it found and left.</summary>
/// <param name="Removed">Each subscriber of a team that no longer exists, with how many of its
/// rows went.</param>
/// <param name="LeftForGoneMembers">Each subscriber of a LIVE team that names no member of it,
/// with how many rows it holds. Logged, never removed.</param>
public sealed record PendingDeliverySweep(
    IReadOnlyList<(string Subscriber, int Rows)> Removed,
    IReadOnlyList<(string Subscriber, int Rows)> LeftForGoneMembers);

/// <summary>
/// At start, removes every <c>pending_deliveries</c> row whose team no longer exists, and logs one
/// line naming how many and for which team/member.
///
/// <para>
/// WHY. The table is keyed on <c>Team/Name</c> with no foreign key. A team deletion clears its
/// queue today (<c>RemoveAllForTeamAsync</c>), but rows left by a deletion from before that, or by
/// one that stopped half-way, are received by nothing and removed by no route, and a team later
/// created under the same id would inherit them.
/// </para>
/// <para>
/// JUDGED BY THE <c>teams</c> ROWS, not by the registry: a row restoration skipped is still a team,
/// and its deliveries are not this sweep's to remove. A live team's deliveries are never touched,
/// including one for a member that no longer exists on it: member deletion removes those, so one
/// found is logged and left - a member re-added under the same name is the case to think about,
/// not to change silently. No tenant row: no person acted.
/// </para>
/// </summary>
public sealed class PendingDeliveriesAtStart(
    IPendingDeliveries pending, ITeamStore store, ILogger<PendingDeliveriesAtStart> logger)
{
    public async Task<PendingDeliverySweep> SweepAsync(CancellationToken ct = default)
    {
        var teams = (await store.TeamsAsync(ct)).Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var members = (await store.MembersAsync(ct))
            .Select(m => $"{m.Team}/{m.Name}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = new List<(string, int)>();
        var left = new List<(string, int)>();

        // An unqualified subscriber has no team part to judge, and is left as it is.
        foreach (var subscriber in (await pending.AllAsync(ct))
            .Where(row => row.Subscriber.Contains('/'))
            .GroupBy(row => row.Subscriber, StringComparer.OrdinalIgnoreCase))
        {
            var team = subscriber.Key[..subscriber.Key.IndexOf('/')];

            if (!teams.Contains(team))
            {
                removed.Add((subscriber.Key, subscriber.Count()));
            }
            else if (!members.Contains(subscriber.Key))
            {
                left.Add((subscriber.Key, subscriber.Count()));
            }
        }

        // By team, as a team deletion removes them, so a row of any spelling of a gone team goes.
        foreach (var team in removed
            .Select(r => r.Item1[..r.Item1.IndexOf('/')])
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await pending.RemoveAllForTeamAsync(team, ct);
        }

        if (removed.Count > 0)
        {
            logger.LogWarning(
                "Removed {Count} queued deliveries for teams that no longer exist: {Subscribers}",
                removed.Sum(r => r.Item2),
                Describe(removed));
        }

        if (left.Count > 0)
        {
            logger.LogWarning(
                "Left {Count} queued deliveries for members that no longer exist on a live team: {Subscribers}",
                left.Sum(r => r.Item2),
                Describe(left));
        }

        return new PendingDeliverySweep(removed, left);
    }

    private static string Describe(IEnumerable<(string Subscriber, int Rows)> rows) =>
        string.Join(", ", rows.Select(r => $"{r.Subscriber} ({r.Rows})"));
}
