using Harness.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A TEAM IS QUIET WHEN NOTHING CAN STILL HAPPEN IN IT ON ITS OWN, judged as the platform judges it
/// rather than by how long nothing was seen: no member has a run going, nothing is queued for one
/// (a reset refuses a member that "holds queued work"), and no row a member subscribes to lies past
/// that member's pump cursor - appended, not yet handed out, which is how a Manager's wake or the
/// platform's idle offer looks in the moment between its row and its delivery (the arm
/// <c>IdleWorkflowOffer.UndeliveredAsync</c> exists for). A wait that only saw nothing new for a few
/// hundred milliseconds called that moment quiet under load, and the next row moved what the test
/// had just read.
/// </summary>
public static class TeamQuiet
{
    public static async Task<bool> IsQuietAsync(IServiceProvider services, string team, CancellationToken ct)
    {
        var log = services.GetRequiredService<IMessageLog>();

        var runs = (await log.ReadAfterAsync(0, [MessageTypes.Started, MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, ct))
            .Where(m => m.Source.StartsWith($"{team}/", StringComparison.OrdinalIgnoreCase));
        if (runs.GroupBy(m => m.Source).Any(g => g.MaxBy(m => m.Seq)!.Type == MessageTypes.Started)) return false;

        if ((await services.GetRequiredService<IPendingDeliveries>().ForTeamAsync(team, ct)).Count > 0) return false;

        var subscriptions = services.GetRequiredService<ISubscriptions>();
        var cursors = services.GetRequiredService<ICursors>();

        foreach (var subscriber in (await subscriptions.SubscribersAsync(ct))
                     .Where(s => string.Equals(s.Team, team, StringComparison.OrdinalIgnoreCase)))
        {
            var types = await subscriptions.ForAsync(subscriber, ct);

            // Subscribed to nothing, the pump never reads this member's cursor, so it never moves.
            if (types.Count == 0) continue;

            var position = await cursors.PositionAsync(subscriber, ct);
            var waiting = (await log.ReadAfterAsync(position, types, int.MaxValue, ct)).Any(row =>
                !string.Equals(row.Source, subscriber.ToString(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(MessageTeam.Of(row), subscriber.Team, StringComparison.OrdinalIgnoreCase));

            if (waiting) return false;
        }

        return true;
    }
}
