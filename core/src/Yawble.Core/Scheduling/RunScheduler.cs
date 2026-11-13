namespace Yawble.Core.Scheduling;

/// <summary>
/// The global run scheduler's admission rule: one limit, app.maxConcurrentAgents, shared by every team;
/// strict first-come, first-served over one queue; no per-team share, no Manager priority, no reserved slot.
/// Changing the limit takes effect at the next admission and never stops a running sandbox.
/// </summary>
public static class RunScheduler
{
    /// <summary>The default for app.maxConcurrentAgents. The global policy overrides it.</summary>
    public const int DefaultMaxConcurrentAgents = 4;

    /// <summary>The queue entries to admit now, oldest first: as many as there are free slots, never more.</summary>
    public static IReadOnlyList<T> Admit<T>(int running, int limit, IReadOnlyList<T> queueOldestFirst)
    {
        var free = Math.Max(0, Math.Max(1, limit) - running);
        return queueOldestFirst.Take(free).ToArray();
    }

    /// <summary>1-based position of an entry in the queue, as "Queued · position N" shows it; null when not queued.</summary>
    public static int? PositionOf<T>(IReadOnlyList<T> queueOldestFirst, Func<T, bool> match)
    {
        for (var i = 0; i < queueOldestFirst.Count; i++)
        {
            if (match(queueOldestFirst[i])) return i + 1;
        }
        return null;
    }
}
