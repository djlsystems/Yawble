namespace Harness.Tests;

/// <summary>
/// A HOST CLOCK FAR FROM MIDNIGHT. Reads 12:00:00 UTC of the current UTC day when it is made, and
/// runs on at real speed from there - timers, delays and timestamps are the real ones, only the wall
/// clock is moved. Given to a Host as its <see cref="TimeProvider"/>, every daily figure it keeps (a
/// trigger's spend today, its cap, its sleep until tomorrow) is read on a day that has twelve hours
/// left in it, whatever the real wall clock says - so a test whose run crosses 00:00 UTC no longer
/// sees the day reset under it.
///
/// A test reads "now" from this clock, never from <see cref="DateTimeOffset.UtcNow"/>: a time it
/// fires a sweep at, or arms a trigger for, has to be on the Host's day, not the wall clock's.
/// </summary>
internal sealed class NoonClock : TimeProvider
{
    private readonly TimeSpan _offset;

    public NoonClock()
    {
        var now = System.GetUtcNow();
        _offset = new DateTimeOffset(now.UtcDateTime.Date.AddHours(12), TimeSpan.Zero) - now;
    }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + _offset;
}

/// <summary>
/// A TRIGGER'S DAY FAR FROM MIDNIGHT, for a Host on the real clock: the fixed-offset IANA zone
/// (<c>Etc/GMT±N</c>) whose local time is between 12:00 and 13:00 right now. A cron trigger given
/// it keeps its spend today and its cap on one day for the next eleven hours, whatever the UTC wall
/// clock reads. Where a Host can be given a <see cref="NoonClock"/> instead, that is the better seam.
/// </summary>
internal static class NoonZone
{
    public static string Now()
    {
        // UTC+k reads 12:xx when k = 12 - the UTC hour; k is always within the zones' -12..+14.
        var k = 12 - DateTimeOffset.UtcNow.Hour;

        // The Etc zones' sign is POSIX's, the reverse of ISO's: UTC+5 is "Etc/GMT-5".
        return k switch
        {
            0 => "Etc/GMT",
            > 0 => $"Etc/GMT-{k}",
            _ => $"Etc/GMT+{-k}",
        };
    }
}
