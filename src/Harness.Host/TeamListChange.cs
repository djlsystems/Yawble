using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// One team whose PRESENCE in this person's visible list changed.
///
/// The payload stays deliberately narrow: whether the team was created or deleted is the event
/// name, and the browser re-reads `/api/overview` rather than trusting a second copy of the row.
/// </summary>
internal sealed record TeamListChange(string Team);

[JsonSerializable(typeof(TeamListChange))]
internal sealed partial class HostJsonContext : JsonSerializerContext;

/// <summary>
/// Announces a create/delete to the PEOPLE whose team list can legitimately change because of it.
///
/// Team-scoped pushes stay on <c>Clients.Group(team)</c>; this exists for the one class of change
/// that no team group can carry: a team that has just appeared or has just gone away.
/// </summary>
internal sealed class TeamListPush(
    IUserStore users,
    IHubContext<ContainerHub> hub)
{
    public async Task<IReadOnlyList<string>> EntitledViewerIdsAsync(
        string team,
        CancellationToken ct = default)
    {
        var viewers = new List<string>();

        // Every person reaches every team, so every account is a viewer of any team's change.
        foreach (var user in await users.ListAsync(ct))
        {
            viewers.Add(user.Id);
        }

        return viewers;
    }

    public async Task AnnounceCreatedAsync(string team, CancellationToken ct = default) =>
        await AnnounceAsync("teamCreated", team, await EntitledViewerIdsAsync(team, ct), ct);

    public async Task AnnounceDeletedAsync(
        string team,
        IReadOnlyCollection<string> viewers,
        CancellationToken ct = default) =>
        await AnnounceAsync("teamDeleted", team, viewers, ct);

    private async Task AnnounceAsync(
        string eventName,
        string team,
        IReadOnlyCollection<string> viewers,
        CancellationToken ct)
    {
        if (viewers.Count == 0) return;

        var payload = new TeamListChange(team);

        foreach (var viewer in viewers)
        {
            await hub.Clients
                .Group(ContainerHub.PersonGroup(viewer))
                .SendAsync(eventName, payload, ct);
        }
    }
}
