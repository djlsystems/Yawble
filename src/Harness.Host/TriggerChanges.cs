using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHAT A PERSON'S CHANGE TO A TRIGGER CHANGED, for its <c>schedule.changed</c> tenant row's
/// <c>changed</c> list: one entry per field that differs, by the field's name on the triggers
/// route, with its value before and after. Turned off and turned on are then told apart (an
/// <c>enabled</c> entry from true to false, or false to true), and so are a cap set and a schedule
/// edited. An instruction or a filter is named without its words: they can be long, and the trigger
/// itself holds them. When it was next due is the platform's, not the person's, so it is not listed.
/// </summary>
public static class TriggerChanges
{
    public static IReadOnlyList<Dictionary<string, object?>> Of(TriggerRow before, TriggerRow after)
    {
        var changed = new List<Dictionary<string, object?>>();

        void Value<T>(string field, T was, T now)
        {
            if (!EqualityComparer<T>.Default.Equals(was, now)) changed.Add(new() { ["field"] = field, ["from"] = was, ["to"] = now });
        }

        void Named(string field, string? was, string? now)
        {
            if (!string.Equals(was, now, StringComparison.Ordinal)) changed.Add(new() { ["field"] = field });
        }

        Value("name", before.Name, after.Name);
        Value("member", before.Container, after.Container);
        Named("instruction", before.Instruction, after.Instruction);
        Value("kind", before.Kind, after.Kind);
        Value("expression", before.Expression, after.Expression);
        Value("timezone", before.Timezone, after.Timezone);
        Value("intervalSeconds", before.IntervalSeconds, after.IntervalSeconds);
        Value("fireAt", before.FireAt, after.FireAt);
        Value("idleOnly", before.IdleOnly, after.IdleOnly);
        Value("enabled", before.Enabled, after.Enabled);
        Value("eventType", before.EventType, after.EventType);
        Named("filter", before.Filter, after.Filter);
        Value("watchRoot", before.WatchRoot, after.WatchRoot);
        Value("watchPath", before.WatchPath, after.WatchPath);
        Value("watchGlob", before.WatchGlob, after.WatchGlob);
        Value("pollSeconds", before.PollSeconds, after.PollSeconds);
        Value("quietSeconds", before.QuietSeconds, after.QuietSeconds);
        Value("minIntervalSeconds", before.MinIntervalSeconds, after.MinIntervalSeconds);
        Value("wakeManager", before.WakeManager, after.WakeManager);
        Value("dailyTokenCap", before.DailyTokenCap, after.DailyTokenCap);
        Value("outcomeId", before.OutcomeId, after.OutcomeId);

        return changed;
    }
}
