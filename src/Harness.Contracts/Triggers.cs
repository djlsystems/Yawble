using System.Data.Common;

namespace Harness.Contracts;

/// <summary>A durable trigger row. Only platform-neutral primitives cross this boundary.</summary>
public sealed record TriggerRow(
    string Id,
    string Team,
    string Container,
    string Name,
    string Instruction,
    string Kind,
    string? Expression,
    string? Timezone,
    int? IntervalSeconds,
    DateTimeOffset? FireAt,
    bool IdleOnly,
    bool Enabled,
    DateTimeOffset? NextDueAt,
    DateTimeOffset? LastFiredAt,
    string? LastOutcome,
    long? LastSeq,
    int MissedCount,
    DateTimeOffset CreatedAt,
    string CreatedBy,

    /// <summary>Which event type fires this trigger. NULL for a clock-driven one.</summary>
    string? EventType = null,

    /// <summary>
    /// The filter, evaluated BEFORE the wake is enqueued. NULL means every message of that type.
    /// Before, not after, and that is the whole reason it exists: a wake costs an agent invocation
    /// and a filter costs nothing, so the container decides whether to spend and the agent decides
    /// what to do with it.
    /// </summary>
    string? Filter = null,

    /// <summary>`documents`, or `root:&lt;file-browser root name&gt;`. NULL unless the kind is
    /// folderChange - and the same for every watch field below.</summary>
    string? WatchRoot = null,

    /// <summary>The watched folder, relative to <see cref="WatchRoot"/>. Empty is the root itself.</summary>
    string? WatchPath = null,

    /// <summary>Optional wildcard narrowing which files count. NULL means every file.</summary>
    string? WatchGlob = null,
    int? PollSeconds = null,
    int? QuietSeconds = null,
    int? MinIntervalSeconds = null,
    DateTimeOffset? LastPollAt = null,
    int? LastPollMs = null,
    int? LastPollEntries = null,

    /// <summary>A sentence when the last poll could not list the folder; NULL when it could.</summary>
    string? LastPollError = null,

    /// <summary>When this watch last PUBLISHED `file.changed`. <see cref="LastFiredAt"/> is when
    /// that event last woke the member, which the event path records.</summary>
    DateTimeOffset? LastChangeAt = null,
    string? LastFingerprint = null,

    /// <summary>What a run this trigger started does to the Manager when it ends - one of
    /// <see cref="WakeManagerPolicy"/>'s values. `always` for a row from before the column, which is
    /// today's behaviour; the create route gives a new trigger
    /// <see cref="WakeManagerPolicy.OnHandbackOrFailure"/>.</summary>
    string WakeManager = WakeManagerPolicy.Always,

    /// <summary>Billable tokens this trigger's runs may spend per day in its timezone, the Manager
    /// runs they woke included. NULL is no cap.</summary>
    long? DailyTokenCap = null,

    /// <summary>The start of the day, in the trigger's timezone, that <see cref="CappedSkips"/>
    /// counts. Written only by <see cref="ITriggerStore.CountCappedSkipAsync"/>; a save never
    /// touches it.</summary>
    DateTimeOffset? CappedSkipsDay = null,

    /// <summary>How many fires the daily cap skipped on <see cref="CappedSkipsDay"/>. Only the first
    /// of them wrote a `schedule.skipped` row and a tenant row.</summary>
    int CappedSkips = 0)
{
    /// <summary>The outcome this trigger's fires serve (<c>auth-017</c>), or null. A fire rooting a
    /// workflow links it to this outcome, attributed to <see cref="CreatedBy"/>.</summary>
    public string? OutcomeId { get; init; }

    /// <summary>The email of the person who last created or changed this trigger (<c>auth-018</c>),
    /// the <c>set_by</c> of its fires' outcome links. Null for a row from before the step, and on a
    /// save by no person, which keeps the email already stored.</summary>
    public string? ConfiguredByEmail { get; init; }
}

/// <summary>
/// The `tenant_events` row a settings write appends in the SAME transaction as the write, so a
/// change with no record of it cannot land. Named for triggers, the first writes to carry it; team
/// and instance settings carry it too.
/// </summary>
public sealed record TriggerAudit(
    string? ActorId,
    string? ActorEmail,
    string Action,
    string? Subject,
    string? SubjectName,
    string? Detail);

/// <summary>
/// What a folder watch remembers between polls that the trigger row does not show: the listing the
/// last fingerprint was taken from (so a change can say WHICH files moved), and a change that is
/// still inside its quiet period.
/// </summary>
public sealed record FolderWatchState(
    string? Listing,
    string? PendingFingerprint,
    DateTimeOffset? PendingSince);

/// <summary>One poll's outcome, written by <see cref="ITriggerStore.RecordPollAsync"/> alone.</summary>
public sealed record FolderPollRecord(
    DateTimeOffset PolledAt,
    int ElapsedMs,
    int? Entries,
    string? Error,
    DateTimeOffset? NextDueAt,
    string? Fingerprint,
    string? Listing,
    string? PendingFingerprint,
    DateTimeOffset? PendingSince,
    DateTimeOffset? ChangeAt);

public interface ITriggerStore
{
    Task SaveAsync(TriggerRow row, CancellationToken ct = default);

    /// <summary>Saves <paramref name="row"/> and appends <paramref name="audit"/> to
    /// `tenant_events` in one transaction: both land or neither does.</summary>
    Task SaveAsync(TriggerRow row, TriggerAudit audit, CancellationToken ct = default);
    Task<IReadOnlyList<TriggerRow>> ListForTeamAsync(string team, CancellationToken ct = default);
    Task<TriggerRow?> FindAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<TriggerRow>> DueAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// The soonest moment any enabled trigger is due, or null when none is armed.
    ///
    /// What lets the runner SLEEP UNTIL something is due rather than waking on a fixed tick and
    /// asking. A polling sweep can only be as accurate as its own period - a ten-second trigger
    /// under a fifteen-second tick fires every fifteen and records the rest as missed, which is
    /// honest and useless. Waiting for this instant instead makes the interval a person typed the
    /// interval they get.
    ///
    /// Cheaper than polling as well as more accurate: it wakes when there is something to do
    /// rather than several thousand times a day to find nothing. Indexed by
    /// `ix_triggers_enabled_next_due_at`, which the due query already relies on.
    /// </summary>
    Task<DateTimeOffset?> EarliestDueAsync(CancellationToken ct = default);

    /// <summary>
    /// Records ONE run outcome by touching only outcome columns. A whole-row re-save here would race
    /// a concurrent edit and write stale values back over columns that had not changed.
    /// </summary>
    Task RecordOutcomeAsync(
        string id,
        DateTimeOffset? firedAt,
        DateTimeOffset? nextDueAt,
        string? outcome,
        long? seq,
        int missed,
        CancellationToken ct = default);

    /// <summary>
    /// Records an event-driven fire of a trigger that keeps its own schedule - a folder watch, whose
    /// next_due_at is its next POLL. Touches only last_fired_at, last_outcome and last_seq; clearing
    /// next_due_at here, as <see cref="RecordOutcomeAsync"/> does for a plain event trigger, would
    /// stop the watch from ever being polled again.
    /// </summary>
    Task RecordFireAsync(
        string id, DateTimeOffset firedAt, string outcome, long seq, CancellationToken ct = default);

    /// <summary>
    /// Records a fire that did not happen - a daily cap reached on an event or folder trigger - by
    /// touching only last_outcome and last_seq. Its due time and last fire are not this skip's.
    /// </summary>
    Task RecordSkipAsync(string id, string outcome, long seq, CancellationToken ct = default);

    /// <summary>
    /// Counts one fire skipped for the daily cap on the day starting <paramref name="dayStart"/>,
    /// and returns how many that day has skipped, this one included - 1 means it is the first,
    /// the one that is logged. Sets the outcome `capped`. A clock trigger (<paramref name="rearm"/>)
    /// also stores <paramref name="nextDueAt"/>, the time it sleeps until; an event or folder
    /// trigger's due time is not the skip's to move. One statement, so two skips cannot both be
    /// the first.
    /// </summary>
    Task<int> CountCappedSkipAsync(
        string id, DateTimeOffset dayStart, bool rearm, DateTimeOffset? nextDueAt, CancellationToken ct = default);

    /// <summary>Records the logged capped skip's row as the trigger's last, and appends
    /// <paramref name="audit"/> to `tenant_events`, in one transaction.</summary>
    Task RecordCappedSkipAsync(string id, long seq, TriggerAudit audit, CancellationToken ct = default);

    /// <summary><see cref="CountCappedSkipAsync(string, DateTimeOffset, bool, DateTimeOffset?, CancellationToken)"/>
    /// inside the caller's transaction on this store's database (<see cref="IMessageLog.AppendWithinAsync"/>),
    /// so the first skip's count, its `schedule.skipped` row and its tenant row commit together.</summary>
    Task<int> CountCappedSkipAsync(
        DbConnection connection, DbTransaction transaction,
        string id, DateTimeOffset dayStart, bool rearm, DateTimeOffset? nextDueAt, CancellationToken ct = default);

    /// <summary><see cref="RecordCappedSkipAsync(string, long, TriggerAudit, CancellationToken)"/>
    /// inside the caller's transaction on this store's database.</summary>
    Task RecordCappedSkipAsync(
        DbConnection connection, DbTransaction transaction, string id, long seq, TriggerAudit audit,
        CancellationToken ct = default);

    Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Deletes the row and appends <paramref name="audit"/> to `tenant_events` in one
    /// transaction: both land or neither does.</summary>
    Task DeleteAsync(string id, TriggerAudit audit, CancellationToken ct = default);
    Task<int> DeleteForTeamAsync(string team, CancellationToken ct = default);
    Task<int> DeleteForContainerAsync(
        string team, string container, CancellationToken ct = default);

    /// <summary>What a per-member dialog reads: one container's triggers. Folds case, matching
    /// `container`'s COLLATE NOCASE. Also what the effective-subscription derivation reads -
    /// <c>EffectiveSubscriptions.EffectiveTypesAsync</c> calls this per container rather than
    /// scanning every enabled event trigger in the store, since it only ever needs one container's
    /// contribution at a time.</summary>
    Task<IReadOnlyList<TriggerRow>> ListForContainerAsync(
        string team, string container, CancellationToken ct = default);

    /// <summary>How many rows of this kind exist, enabled or not - the per-instance watch cap.</summary>
    Task<int> CountKindAsync(string kind, CancellationToken ct = default);

    Task<FolderWatchState> WatchStateAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Records one folder poll by touching only poll columns, for the reason
    /// <see cref="RecordOutcomeAsync"/> is narrow. A NULL <see cref="FolderPollRecord.Fingerprint"/>,
    /// <see cref="FolderPollRecord.Listing"/> or <see cref="FolderPollRecord.ChangeAt"/> leaves the
    /// stored value alone: a poll that could not list must not forget the baseline.
    /// </summary>
    Task RecordPollAsync(string id, FolderPollRecord poll, CancellationToken ct = default);
}
