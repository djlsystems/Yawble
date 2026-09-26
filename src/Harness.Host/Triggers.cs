using Cronos;
using Harness.Contracts;

namespace Harness.Host;

public static class Triggers
{
    /// <summary>
    /// The shortest interval an every-N schedule may have.
    ///
    /// TEN, and the bound is the AGENT rather than the scheduler.
    /// The runner sleeps until the due instant, so it could honour one second - but a run launches
    /// a process and takes seconds at best and minutes in the ordinary case. Below ten, a schedule
    /// with `idleOnly` set skips almost every occurrence, and one without it queues faster than it
    /// drains until the ceiling starts rejecting. Neither is a schedule; both look like a fault.
    ///
    /// A floor rather than a warning because the useless range is knowable in advance. What is NOT
    /// knowable is how long a given member takes, so anything above this is allowed and reports its
    /// own missed occurrences honestly.
    /// </summary>
    public const int MinimumIntervalSeconds = 10;

    public static DateTimeOffset? NextOccurrence(
        TriggerKind kind,
        string? expression,
        string? timezone,
        int? intervalSeconds,
        DateTimeOffset? fireAt,
        DateTimeOffset after)
    {
        return kind switch
        {
            TriggerKind.Cron => NextCronOccurrence(expression, timezone, fireAt, after),
            TriggerKind.Every => NextEveryOccurrence(intervalSeconds, fireAt, after),
            TriggerKind.Once => NextOnceOccurrence(fireAt, after),

            // A FOLDER TRIGGER IS ARMED AT ONCE: its first poll takes the baseline. Every later due
            // time is FolderWatch's to set, from the poll interval and the quiet period.
            TriggerKind.FolderChange => after,
            _ => null,
        };
    }

    /// <summary>
    /// THE ONE VALIDATOR for a trigger's shape, called by both the create and the update route.
    /// One copy, because two validators drift - over whether an every-N anchor is required, say -
    /// and a schedule one accepts the other cannot run.
    ///
    /// Takes the kind as the STRING the wire carries, case-insensitively. It checks what the clock
    /// maths beside it needs, so a row it accepts is a row <see cref="NextOccurrence"/> can arm: a
    /// cron expression that parses in the seconds format, a timezone that resolves.
    ///
    /// <paramref name="onceMustFollow"/> is the instant a once trigger must be after, or null to
    /// skip that check. The update route passes null: a once trigger that has already fired keeps
    /// its past instant, and refusing it would make it impossible to rename or disable.
    ///
    /// An event trigger has no clock shape; its catalog-declared type is checked at the route,
    /// which holds the EventCatalog this does not.
    /// </summary>
    public static string? Validate(
        string kind,
        string? expression,
        string? timezone,
        int? intervalSeconds,
        DateTimeOffset? fireAt,
        DateTimeOffset? onceMustFollow)
    {
        // BY NAME, never Enum.TryParse alone: that also accepts "1", which is not a kind anybody
        // can read back off the row.
        if (Enum.GetNames<TriggerKind>().FirstOrDefault(
                name => string.Equals(name, kind.Trim(), StringComparison.OrdinalIgnoreCase))
            is not { } name)
        {
            return "kind must be one of: cron, every, once, event, folderChange.";
        }

        var parsed = Enum.Parse<TriggerKind>(name);

        return parsed switch
        {
            TriggerKind.Cron => ValidateCron(expression, timezone),
            TriggerKind.Every => ValidateEvery(intervalSeconds),
            TriggerKind.Once => ValidateOnce(fireAt, onceMustFollow),
            _ => null,
        };
    }

    /// <summary>What <see cref="Preview"/> answers: the next firings, or the sentence that says
    /// why there are none.</summary>
    public sealed record SchedulePreview(IReadOnlyList<DateTimeOffset> Occurrences, string? Error);

    /// <summary>
    /// The next <paramref name="count"/> firings of a clock schedule after <paramref name="now"/>,
    /// computed by <see cref="NextOccurrence"/> exactly as the sweep arms and re-arms a row - so the
    /// cron builder's preview is the schedule, not a second opinion of it. A shape
    /// <see cref="Validate"/> refuses answers its sentence and no times.
    /// </summary>
    public static SchedulePreview Preview(
        TriggerKind kind,
        string? expression,
        string? timezone,
        int? intervalSeconds,
        DateTimeOffset? fireAt,
        DateTimeOffset now,
        int count)
    {
        if (kind is not (TriggerKind.Cron or TriggerKind.Every or TriggerKind.Once))
        {
            return new([], "Only a cron, every or once schedule has times to preview.");
        }

        if (Validate(kind.ToString(), expression, timezone, intervalSeconds, fireAt, onceMustFollow: null) is { } refusal)
        {
            return new([], refusal);
        }

        var occurrences = new List<DateTimeOffset>();
        var after = now;
        while (occurrences.Count < Math.Clamp(count, 1, 20)
               && NextOccurrence(kind, expression, timezone, intervalSeconds, fireAt, after) is { } next
               && next > after)
        {
            occurrences.Add(next);
            after = next;
        }

        return new(occurrences, null);
    }

    /// <summary>
    /// A cron schedule's next time. <paramref name="startsAt"/> (the row's `fireAt`) is when the
    /// loop begins: before it, the first time is the first cron time AT OR AFTER the start - a
    /// start on the dot fires then; once it has passed it changes nothing.
    /// </summary>
    private static DateTimeOffset? NextCronOccurrence(string? expression, string? timezone, DateTimeOffset? startsAt, DateTimeOffset after)
    {
        if (startsAt is { } start && start > after)
        {
            if (!TryParseCron(expression, out var fromStart) || !TryResolveTimeZone(timezone, out var startZone))
            {
                return null;
            }

            return fromStart.GetNextOccurrence(start, startZone, inclusive: true);
        }

        if (!TryParseCron(expression, out var cron))
        {
            return null;
        }

        if (!TryResolveTimeZone(timezone, out var zone))
        {
            return null;
        }

        return cron.GetNextOccurrence(after, zone);
    }

    private static DateTimeOffset? NextEveryOccurrence(int? intervalSeconds, DateTimeOffset? fireAt, DateTimeOffset after)
    {
        if (intervalSeconds is null || intervalSeconds.Value < MinimumIntervalSeconds)
        {
            return null;
        }

        // NO ANCHOR MEANS "FROM NOW", and returning null here instead is what made every-N
        // schedules unrunnable. `fireAt` is an OPTIONAL anchor - "every hour, starting at 09:00" -
        // and the browser never sends one, because there is nowhere to type it for an every-N
        // schedule. Validation accepts a null anchor, so a row could be created that the maths then
        // called unschedulable: `next_due_at` stayed null, and the store's due query skips a null
        // forever.
        //
        // Fixing it HERE rather than at the create route is what makes it stay fixed. The sweep
        // re-arms through this same function after every fire, so an anchorless row armed only at
        // creation would fire exactly once and then go dark again - a bug strictly harder to see
        // than never firing at all.
        var anchor = fireAt ?? after;

        if (after < anchor)
        {
            return anchor;
        }

        var intervalTicks = TimeSpan.FromSeconds(intervalSeconds.Value).Ticks;
        var elapsedTicks = after.UtcTicks - anchor.UtcTicks;
        var periods = (elapsedTicks / intervalTicks) + 1;
        return anchor.AddTicks(periods * intervalTicks);
    }

    private static DateTimeOffset? NextOnceOccurrence(DateTimeOffset? fireAt, DateTimeOffset after)
    {
        if (fireAt is null)
        {
            return null;
        }

        return fireAt.Value > after ? fireAt.Value : null;
    }

    private static string? ValidateCron(string? expression, string? timezone)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return "A cron schedule needs an expression.";
        }

        if (!TryParseCron(expression, out _))
        {
            return "Cron expression is invalid.";
        }

        if (string.IsNullOrWhiteSpace(timezone))
        {
            return "A cron schedule needs a timezone.";
        }

        if (!TryResolveTimeZone(timezone, out _))
        {
            return $"Unknown IANA timezone '{timezone}'.";
        }

        return null;
    }

    private static string? ValidateEvery(int? intervalSeconds)
    {
        if (intervalSeconds is null)
        {
            return "An every schedule needs intervalSeconds.";
        }

        if (intervalSeconds.Value < MinimumIntervalSeconds)
        {
            return $"intervalSeconds must be at least {MinimumIntervalSeconds}.";
        }

        // AN ANCHOR IS OPTIONAL. Absent, it is the moment the schedule is armed - see
        // NextEveryOccurrence.
        return null;
    }

    private static string? ValidateOnce(DateTimeOffset? fireAt, DateTimeOffset? mustFollow)
    {
        if (fireAt is null)
        {
            return "A once schedule needs fireAt.";
        }

        return mustFollow is { } now && fireAt.Value <= now
            ? "A once schedule must be in the future."
            : null;
    }

    private static bool TryParseCron(string? expression, out CronExpression cron)
    {
        cron = default!;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        try
        {
            cron = CronExpression.Parse(expression, CronFormat.IncludeSeconds);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    private static bool TryResolveTimeZone(string? timezone, out TimeZoneInfo zone)
    {
        zone = default!;
        if (string.IsNullOrWhiteSpace(timezone))
        {
            return false;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
