using Microsoft.Extensions.Hosting;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// Fires schedules when they are due, by SLEEPING UNTIL THE NEXT ONE IS DUE rather than waking on a
/// fixed tick and asking.
///
/// A polling sweep can only ever be as accurate as its own period: a ten-second schedule under a
/// fifteen-second tick would fire every fifteen seconds and record the occurrences in between as
/// missed, and the person watching would have no way to know the interval they typed was finer
/// than the platform's resolution.
///
/// Waiting for the actual instant makes the interval a person typed the interval they get, and is
/// CHEAPER than polling rather than more expensive: it wakes when there is something to do instead
/// of several thousand times a day to find nothing.
///
/// THREE THINGS BOUND THE WAIT, and each is there for a different failure:
///
/// 1. <b>The earliest due instant</b>, when there is one. The accuracy.
/// 2. <b><see cref="MaximumWait"/></b>, always. A schedule row can change without this process
///    hearing about it - another host, a hand-edited database, a clock that jumped - so it re-reads
///    on a slow heartbeat regardless. Without it, "nothing armed" would mean "sleep forever" and a
///    schedule created out of band would never run.
/// 3. <b><see cref="MinimumWait"/></b>, as a floor. A due instant in the past - a missed window, or
///    a row somebody armed with a past timestamp - would otherwise compute a zero or negative delay
///    and spin the loop hot against SQLite. Firing is not instantaneous, so a floor also keeps a
///    schedule whose runs overrun their own interval from starving everything else.
/// </summary>
public sealed class TriggerRunner(
    TriggerSweep sweep,
    ITriggerStore schedules,
    TriggerWakeSignal wake,
    TimeSpan maximumWait,
    ILogger<TriggerRunner> logger)
    : BackgroundService
{
    /// <summary>The longest this ever sleeps without re-reading, however far away the next due
    /// instant is. A heartbeat against state that changed without a signal reaching us.</summary>
    public static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(1);

    /// <summary>The shortest, so an overdue row cannot spin the loop.</summary>
    public static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await sweep.FireDueAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Logged and swallowed, for the reason every other loop in this codebase does it: a
                // failed sweep must not end the runner, or one bad row stops every schedule in the
                // tenant with nothing observing the fault.
                logger.LogWarning(exception, "A schedule sweep failed.");
            }

            TimeSpan delay;

            try
            {
                delay = await NextWaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The store is unreadable. Fall back to the heartbeat rather than stopping: the next
                // pass may well succeed, and a runner that quits on a transient error takes every
                // schedule with it.
                logger.LogWarning(exception, "Could not read the next due schedule.");
                delay = maximumWait;
            }

            try
            {
                // The RESULT IS IGNORED on purpose. Woken early by a write, or waited the delay out,
                // the next step is identical - re-read and fire whatever is due. Branching on it
                // would be two paths that must stay in step for no gain.
                await wake.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<TimeSpan> NextWaitAsync(CancellationToken ct)
    {
        var earliest = await schedules.EarliestDueAsync(ct);

        // Nothing armed. Sleep the heartbeat: a schedule created here signals us awake, and one
        // created any other way is picked up within the heartbeat rather than never.
        if (earliest is null) return maximumWait;

        var until = earliest.Value - DateTimeOffset.UtcNow;

        if (until > maximumWait) return maximumWait;

        return until < MinimumWait ? MinimumWait : until;
    }
}
