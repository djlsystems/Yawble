using Cronos;

namespace Yawble.Core.Triggers;

public static class TriggerKinds
{
    public const string Every = "every";
    public const string Cron = "cron";
    public const string Once = "once";

    /// <summary>A folder change (0017): interval_sec is how often the folder is looked at.</summary>
    public const string Folder = "folder";

    /// <summary>An event (0021): fires when an event of its name is published (by a watch), never on a schedule.</summary>
    public const string Event = "event";
    public static readonly IReadOnlyList<string> All = [Every, Cron, Once, Folder, Event];
}

/// <summary>A trigger's schedule, as stored.</summary>
public sealed record TriggerSchedule(string Kind, int? IntervalSec, string? Cron, string? Timezone, DateTimeOffset? FireAt);

/// <summary>
/// Schedule maths: the next occurrence, validation and a preview. A cron expression
/// takes 5 fields (minute hour day month weekday) or 6 (with seconds first); its timezone is an IANA or
/// Windows id (.NET resolves both).
/// </summary>
public static class TriggerScheduling
{
    public const int MinimumIntervalSeconds = 10;

    public static DateTimeOffset? NextOccurrence(TriggerSchedule s, DateTimeOffset after) => s.Kind switch
    {
        TriggerKinds.Cron => NextCron(s, after),
        TriggerKinds.Every => NextEvery(s.IntervalSec, s.FireAt, after),
        TriggerKinds.Once => s.FireAt is { } at && at > after ? at : null,
        TriggerKinds.Folder => after.AddSeconds(Math.Max(MinimumIntervalSeconds, s.IntervalSec ?? FolderWatch.DefaultPollSec)),
        _ => null,
    };

    /// <summary>Null when the schedule is valid, else the reason. <paramref name="now"/>: a once must be later.</summary>
    public static string? Validate(TriggerSchedule s, DateTimeOffset? now)
    {
        switch (s.Kind)
        {
            case TriggerKinds.Every:
                if (s.IntervalSec is null) return "An every schedule needs an interval.";
                return s.IntervalSec < MinimumIntervalSeconds ? $"The interval must be at least {MinimumIntervalSeconds} seconds." : null;
            case TriggerKinds.Cron:
                if (string.IsNullOrWhiteSpace(s.Cron)) return "A cron schedule needs an expression.";
                if (!TryParseCron(s.Cron, out _)) return "The cron expression is invalid (5 fields: minute hour day month weekday).";
                if (string.IsNullOrWhiteSpace(s.Timezone)) return "A cron schedule needs a timezone.";
                return TryZone(s.Timezone, out _) ? null : $"Unknown timezone '{s.Timezone}'.";
            case TriggerKinds.Once:
                if (s.FireAt is null) return "A once schedule needs a time.";
                return now is { } n && s.FireAt <= n ? "A once schedule must be in the future." : null;
            case TriggerKinds.Event:
                return null;
            case TriggerKinds.Folder:
                return s.IntervalSec is { } poll && poll < MinimumIntervalSeconds ? $"The folder is looked at every {MinimumIntervalSeconds} seconds at most often." : null;
            default:
                return "kind must be one of: every, cron, once, folder, event.";
        }
    }

    /// <summary>The next <paramref name="count"/> fires after <paramref name="now"/> (the dialog's preview).</summary>
    public static IReadOnlyList<DateTimeOffset> Preview(TriggerSchedule s, DateTimeOffset now, int count)
    {
        var list = new List<DateTimeOffset>();
        var after = now;
        while (list.Count < Math.Clamp(count, 1, 20) && NextOccurrence(s, after) is { } next && next > after)
        {
            list.Add(next);
            after = next;
        }
        return list;
    }

    private static DateTimeOffset? NextCron(TriggerSchedule s, DateTimeOffset after)
    {
        if (!TryParseCron(s.Cron, out var cron) || !TryZone(s.Timezone, out var zone)) return null;
        // An optional start: nothing before it.
        if (s.FireAt is { } start && start > after) return cron.GetNextOccurrence(start, zone, inclusive: true);
        return cron.GetNextOccurrence(after, zone);
    }

    private static DateTimeOffset? NextEvery(int? intervalSec, DateTimeOffset? anchor, DateTimeOffset after)
    {
        if (intervalSec is null || intervalSec < MinimumIntervalSeconds) return null;
        // No anchor means "from when it was armed" (returning null here would make every-N schedules never fire).
        var from = anchor ?? after;
        if (after < from) return from;
        var interval = TimeSpan.FromSeconds(intervalSec.Value).Ticks;
        var periods = (after.UtcTicks - from.UtcTicks) / interval + 1;
        return from.AddTicks(periods * interval);
    }

    private static bool TryParseCron(string? expression, out CronExpression cron)
    {
        cron = default!;
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        try
        {
            cron = CronExpression.Parse(expression.Trim(), fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    private static bool TryZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
