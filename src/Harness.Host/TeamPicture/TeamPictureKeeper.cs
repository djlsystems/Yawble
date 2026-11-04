using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// KEEPS EVERY TEAM PICTURE CURRENT WITHOUT AN AGENT RUN. A tail of the log, as
/// <see cref="KanbanChangePush"/> is: one scalar read a second, then the rows after the last one seen.
/// Each team named by a row is marked dirty; a dirty team with its picture on is recomputed
/// (<see cref="TeamPictureFigures"/>) at most every <see cref="Interval"/>, and once more on the pass
/// after, because a run's ledger row is written just after its terminal log row. A team with work in
/// flight is recomputed every <see cref="RunningHeartbeat"/> as well, so "how long it has run"
/// moves. Nothing here is on a run's path, and a failed pass is logged and tried again.
///
/// At start it publishes the shipped template again for every team whose live version is not it
/// (<see cref="TeamPicture.RepublishAllAsync"/>).
/// </summary>
internal sealed class TeamPictureKeeper(
    IMessageLog log, TeamPicture picture, TeamPictureFigures figures, TeamRegistry teams,
    ILogger<TeamPictureKeeper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan RunningHeartbeat = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long last;

        try
        {
            last = await log.HighestSeqAsync(stoppingToken);
            await picture.RepublishAllAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The Team pictures were not republished at start.");
            last = 0;
        }

        var again = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var heartbeat = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);

                var dirty = new HashSet<string>(again, StringComparer.OrdinalIgnoreCase);
                var fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                again.Clear();

                if (await log.HighestSeqAsync(stoppingToken) > last)
                {
                    while (true)
                    {
                        var rows = await log.ReadRangeAsync(last, 500, stoppingToken);
                        if (rows.Count == 0) break;
                        last = rows[^1].Seq;

                        foreach (var row in rows)
                        {
                            if (MessageTeam.Of(row) is { } team && teams.ExistingName(team) is { } stored) fresh.Add(stored);
                        }

                        if (rows.Count < 500) break;
                    }

                    // Once more on the next pass: a run's ledger row lands just after its log row.
                    foreach (var team in fresh)
                    {
                        dirty.Add(team);
                        again.Add(team);
                    }
                }

                if (DateTimeOffset.UtcNow - heartbeat >= RunningHeartbeat)
                {
                    heartbeat = DateTimeOffset.UtcNow;
                    foreach (var team in teams.TeamsWithTeamPicture()) dirty.Add(team);
                }

                foreach (var team in dirty)
                {
                    if (!teams.HasTeamPicture(team)) continue;

                    try
                    {
                        await figures.RecomputeAsync(team, stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "The Team picture of team {Team} was not recomputed; trying again.", team);
                        again.Add(team);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "The Team picture pass failed; trying again on the next one.");
            }
        }
    }
}
