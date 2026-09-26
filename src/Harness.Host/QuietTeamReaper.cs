using Harness.Messaging;

namespace Harness.Host;

/// <summary>
/// Runs the quiet-team sweep on a timer. The window is `quiet.window`, read at every sweep.
///
/// <para>
/// <paramref name="checkpoint"/> runs after every sweep, whether or not the sweep found anything:
/// the database's <c>PRAGMA wal_checkpoint(TRUNCATE)</c>, so the -wal is cut back on the same quiet
/// minute rather than on a timer of its own. It is separate from the sweep's try so a failed
/// sweep still checkpoints and a failed checkpoint is named as one.
/// </para>
/// </summary>
public sealed class QuietTeamReaper(
    QuietTeamSweep sweep,
    Func<TimeSpan> window,
    TimeSpan interval,
    ILogger<QuietTeamReaper> log,
    Func<CancellationToken, Task<CheckpointResult>>? checkpoint = null)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation(
            "Quiet-team sweep enabled with window {Window}; sweeping every {Interval}.",
            window(),
            interval);

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }

                await sweep.ReapIdleAsync(window(), DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                log.LogWarning(exception, "A quiet-team sweep failed.");
            }

            if (checkpoint is null) continue;

            try
            {
                var result = await checkpoint(stoppingToken);

                if (result.Busy)
                {
                    log.LogDebug(
                        "The WAL checkpoint was busy: {Checkpointed} of {Frames} frames copied; the next sweep tries again.",
                        result.CheckpointedFrames,
                        result.LogFrames);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                log.LogWarning(exception, "The WAL checkpoint after the quiet-team sweep failed.");
            }
        }
    }
}
