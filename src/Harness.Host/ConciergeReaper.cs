namespace Harness.Host;

/// <summary>
/// Ends console sessions nobody has come back to.
///
/// Per-person sessions are expensive: one child process per person per team, each holding a PTY
/// and an agent, and without this nothing would end any of them short of a host restart. This is
/// what bounds that.
///
/// The panel must NOT do this job. There is no close-that-kills on the panel chrome at all,
/// because the whole point of a console is that you close it, come
/// back, and your agent still has its conversation. So reaping is a background rule with a long
/// window, and closing the panel stays free.
///
/// Eight hours by default: long enough to keep a session across a working day, short enough not to
/// keep one across a weekend. Configurable through <c>ConciergeIdleTimeout</c> - which exists for a
/// practical reason as much as a deployment one, since nobody can sit through the default to watch
/// it work.
///
/// The window is a delegate onto `concierge.idleTimeout` and is read at every sweep, so a
/// change in the Tenant Settings dialog applies at the next one.
/// </summary>
public sealed class ConciergeReaper(
    ConciergeSessionStore consoles, Func<TimeSpan> window, TimeSpan sweep, ILogger<ConciergeReaper> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        log.LogInformation(
            "Console sessions idle for more than {Window} will be ended; sweeping every {Sweep}.",
            window(), sweep);

        using var timer = new PeriodicTimer(sweep);

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stopping)) return;

                foreach (var key in await consoles.ReapIdleAsync(window(), DateTimeOffset.UtcNow))
                {
                    // Logged individually and at Information: a terminal disappearing is something
                    // a person may come back to and wonder about, so there has to be a line saying
                    // it was deliberate.
                    log.LogInformation("Ended idle console {Console}.", key);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad sweep must not stop every later one. A reaper that dies quietly presents
                // hours later as a slow machine with too many processes on it, and nothing about
                // that points back here - so it is caught, logged, and the timer keeps running.
                log.LogError(ex, "A console sweep failed. The next one will still run.");
            }
        }
    }
}
