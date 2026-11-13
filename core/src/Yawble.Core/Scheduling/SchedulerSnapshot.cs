namespace Yawble.Core.Scheduling;

/// <summary>
/// The global run scheduler as the UI shows it: the header's running / limit and queued
/// count, every sandbox holding a slot, the one queue in admission order, and each team's load. Returned by the
/// IPC method scheduler.getState.
/// </summary>
public sealed record SchedulerSnapshot(
    int Running,
    int Limit,
    int Queued,
    IReadOnlyList<SlotHolder> Live,
    IReadOnlyList<QueuedEntry> Queue,
    IReadOnlyList<TeamLoad> Teams)
{
    /// <summary>
    /// The card text for a queued entry: a run shows "Queued · position N of M"; an interactive
    /// session's window shows "waiting for a run slot (position N)".
    /// </summary>
    public static string QueuedLabel(string kind, int position, int of) => kind == "interactive"
        ? $"waiting for a run slot (position {position})"
        : $"Queued · position {position} of {of}";
}

/// <summary>A live sandbox: a run (RunId) or an interactive session (SessionId), and how long it waited for its slot.</summary>
public sealed record SlotHolder(string Team, string Member, string Kind, long? RunId, long? SessionId, string QueuedAt, string StartedAt, long WaitedMs);

/// <summary>One queue entry. Position is 1-based in admission order across all teams; WaitingMs is the wait so far.</summary>
public sealed record QueuedEntry(int Position, int Of, string Team, string Member, string Kind, string EnqueuedAt, long WaitingMs, string Label);

/// <summary>
/// A team in the team list: sandboxes running and entries queued now, and from its history (runs and sessions)
/// how many slots it was given and how long it waited for them in total and at most.
/// </summary>
public sealed record TeamLoad(string Team, int Running, int Queued, int Admitted, long TotalWaitMs, long MaxWaitMs);
