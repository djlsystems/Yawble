using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHERE A TEAM'S ROWS BEGIN ON THE FEED: the log position its creation fell at. A team's identifier
/// is derived from its name, so a team deleted and created again under the same name reads as the
/// same team on every row its predecessor wrote; everything at or below this position is the
/// predecessor's.
///
/// <para>
/// Read for <c>GET /api/messages</c>: the live window cuts each team's rows here, and a member's
/// history page takes it as a floor beneath the member's own. The same turn of
/// <c>teams.created_utc</c> into a seq that <see cref="TeamActivityFrom.ScopeAsync"/> makes, without
/// the activity-from, which only the activity and token-runs reads take.
/// </para>
/// </summary>
internal static class TeamCreationFloor
{
    /// <summary>
    /// The highest seq written before <paramref name="team"/> was created, or null when the store
    /// cannot say when it was: no floor, never an invented one.
    /// </summary>
    public static async Task<long?> OfAsync(string team, ITeamStore store, IMessageLog log, CancellationToken ct) =>
        await store.CreatedAtAsync(team, ct) is { } created ? await log.LastSeqBeforeAsync(created, ct) : null;
}
