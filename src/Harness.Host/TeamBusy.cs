using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>A member that is working: its run loop is running or a delivery it accepted has started, or it has deliveries accepted and not started.</summary>
public sealed record BusyMember(string Name, bool Running, int Queued);

/// <summary>
/// THE ONE BUSY RULE. An archive, a delete, a repository action and the wait route's verdict all ask
/// the same question of a team - is any member working? - and this is the one answer: a member whose
/// run loop is Running or whose accepted delivery has started is running, and one with deliveries
/// accepted and not started has that many queued. A team is quiet when the list is empty.
/// </summary>
public static class TeamBusy
{
    public static async Task<IReadOnlyList<BusyMember>> MembersAsync(
        TeamRegistry teams, ContainerHost host, IPendingDeliveries pending, string stored, CancellationToken ct = default)
    {
        var rows = await pending.ForTeamAsync(stored, ct);
        var busy = new List<BusyMember>();

        foreach (var id in teams.ContainerIdsOf(stored))
        {
            var snapshot = host.Find(id)?.Snapshot();
            var mine = rows
                .Where(row => ContainerId.TryParse(row.Subscriber, out var subscriber) && subscriber.Equals(id))
                .ToList();

            var running = snapshot?.State == ContainerState.Running || mine.Any(row => row.Started);
            var queued = mine.Count(row => !row.Started);

            if (running || queued > 0) busy.Add(new BusyMember(snapshot?.Name ?? id.Name, running, queued));
        }

        return busy;
    }
}
