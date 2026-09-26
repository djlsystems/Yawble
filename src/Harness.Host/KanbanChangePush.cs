using Harness.Contracts;
using Harness.Kanban;
using Microsoft.AspNetCore.SignalR;

namespace Harness.Host;

/// <summary>
/// RAISES <c>kanbanChanged</c> FOR EVERY ROW THAT MOVES A CARD, TO EVERY BROWSER.
///
/// <c>containerChanged</c> is sent only to the groups of teams whose tabs a browser has open, so a
/// board refreshed on that alone would leave a finished team's last card In Progress on the
/// all-teams Kanban until somebody opened that team's tab. The client listens for
/// <c>kanbanChanged</c>, and this is what raises it.
///
/// A LOOP OVER THE LOG rather than a hook on every writer: rows are appended from many places, and
/// a tail of the log sees all of them. Once a second it reads the rows after the last one it saw
/// and, when any moves a card, sends one <c>kanbanChanged</c> per team to all connections. Every
/// person sees every team, and the payload is only a team id. The client refetches the board only
/// while the Kanban tab is showing.
/// </summary>
public static class KanbanChange
{
    /// <summary>The teams whose board moved in these rows, each named once, in first-seen order.</summary>
    public static IReadOnlyList<string> TeamsToNotify(IEnumerable<Message> rows) =>
        rows.Where(row => KanbanProjector.MovesTheBoard(row.Type))
            .Select(MessageTeam.Of)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

internal sealed class KanbanChangePush(
    IMessageLog log, IHubContext<ContainerHub> hub, ILogger<KanbanChangePush> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var last = await log.HighestSeqAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);

                var rows = await log.ReadRangeAsync(last, 500, stoppingToken);
                if (rows.Count == 0) continue;

                last = rows[^1].Seq;

                foreach (var team in KanbanChange.TeamsToNotify(rows))
                {
                    await hub.Clients.All.SendAsync("kanbanChanged", new { team }, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A missed push costs a stale board until the next one; it must not stop the loop.
                logger.LogWarning(exception, "kanbanChanged push failed; trying again on the next pass.");
            }
        }
    }
}
