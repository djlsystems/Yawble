using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// Ends console sessions nobody is watching and that have stopped working.
///
/// Per-person sessions are expensive: one child process per person, each holding a PTY and an agent
/// and part of a worker's capacity, and without this nothing would end any of them short of a host
/// restart. This is what bounds that.
///
/// The panel must NOT do this job. There is no close-that-kills on the panel chrome at all,
/// because the whole point of a console is that you close it, come
/// back, and your agent still has its conversation. So reaping is a background rule, and closing the
/// panel stays free.
///
/// A session is ended only when it is unwatched AND inactive for the window
/// (<see cref="ConciergeSessionStore.ReapIdleAsync"/>): a Concierge left running a long task keeps
/// running. One hour by default, short because a session that is truly done is holding a worker.
/// Configurable through <c>ConciergeIdleTimeout</c> - which exists for a practical reason as much as
/// a deployment one, since nobody can sit through the default to watch it work.
///
/// The window is a delegate onto `concierge.idleTimeout` and is read at every sweep, so a
/// change in the Tenant Settings dialog applies at the next one. Each session ended writes a
/// <see cref="TenantActions.ConciergeEndedIdle"/> row saying why.
/// </summary>
/// <param name="emailOf">The person's email for the row, by user id; null when it cannot be read.</param>
/// <param name="swept">Told after every sweep, ended or not.</param>
public sealed class ConciergeReaper(
    ConciergeSessionStore consoles,
    Func<TimeSpan> window,
    TimeSpan sweep,
    ILogger<ConciergeReaper> log,
    ITenantLog? tenantLog = null,
    Func<string, CancellationToken, Task<string?>>? emailOf = null,
    Action? swept = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        log.LogInformation(
            "Console sessions unwatched and inactive for more than {Window} will be ended; sweeping every {Sweep}.",
            window(), sweep);

        using var timer = new PeriodicTimer(sweep, consoles.Clock);

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stopping)) return;

                await SweepAsync(consoles.Clock.GetUtcNow(), stopping);
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

    /// <summary>One sweep at <paramref name="now"/>: ends what the rule ends, and records each.</summary>
    public async Task<IReadOnlyList<ConciergeReaped>> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var ended = await consoles.ReapIdleAsync(window(), now);

        foreach (var reaped in ended)
        {
            // Logged individually and at Information: a terminal disappearing is something
            // a person may come back to and wonder about, so there has to be a line saying
            // it was deliberate.
            log.LogInformation("Ended idle console {Console}: {Reason}", reaped.Key, reaped.Reason);
            await RecordAsync(reaped, ct);
        }

        swept?.Invoke();
        return ended;
    }

    /// <summary>The tenant row. A row that cannot be written is logged: the end has happened, and stands.</summary>
    private async Task RecordAsync(ConciergeReaped reaped, CancellationToken ct)
    {
        if (tenantLog is null) return;

        try
        {
            var email = emailOf is null ? null : await emailOf(reaped.Key.User, ct);
            await tenantLog.WriteAsync(
                null, null, TenantActions.ConciergeEndedIdle, reaped.Key.User, email,
                JsonSerializer.Serialize(new
                {
                    worker = reaped.Worker,
                    window = reaped.Window.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
                    lastViewerAt = reaped.LastViewerAt,
                    neverViewed = reaped.NeverViewed,
                    lastActivityAt = reaped.LastActivityAt,
                    lastActivity = reaped.LastActivity,
                    reason = reaped.Reason,
                }),
                ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.LogWarning(exception, "Idle console {Console} was ended, and its tenant row could not be written.", reaped.Key);
        }
    }
}
