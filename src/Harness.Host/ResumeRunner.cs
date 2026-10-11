namespace Harness.Host;

/// <summary>
/// Runs the automatic-resume sweep on a timer. The sibling of <see cref="QuietTeamReaper"/>, and
/// deliberately the same shape.
/// </summary>
/// <remarks>
/// THE INTERVAL IS THE ACCURACY. A resume fires on the first tick at or after the moment the
/// provider named, so it is late by up to one interval and never early. Late is the safe direction:
/// early is a request against a budget that has not renewed, which fails exactly the way the run it
/// is resuming from did.
/// </remarks>
public sealed class ResumeRunner(
    ResumeSweep sweep,
    TimeSpan interval,
    ILogger<ResumeRunner> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Automatic resumes enabled; sweeping every {Interval}.", interval);

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }

                await sweep.SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A SWEEP THAT THREW MUST NOT TAKE THE TIMER WITH IT. The next tick re-derives
                // everything from the log, so one bad pass costs one interval and nothing else -
                // which is the whole benefit of holding no state between ticks.
                log.LogWarning(exception, "An automatic-resume sweep failed.");
            }
        }
    }
}
