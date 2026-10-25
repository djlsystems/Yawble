using System.Data.Common;
using System.Globalization;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// WHAT A TRIGGER STORE WRITES INTO A <c>triggers</c> COLUMN AND READS BACK OUT OF ONE, whichever
/// database holds the row: the column list, the value each saved column takes and the row reader. Both
/// providers' trigger stores call these, so a row either writes reads the same on the other; only the
/// SQL differs.
///
/// The reader takes <see cref="DbDataReader"/> and reads every integer with <c>GetInt64</c>, narrowing
/// where the record holds an <c>int</c>: SQLite's INTEGER and SQL Server's BIGINT both answer it.
/// </summary>
public static class TriggerRows
{
    /// <summary>The columns <see cref="Read"/> reads, in its order.</summary>
    public const string Columns =
        """
        id, team, container, name, instruction, kind, expression, timezone,
                   interval_seconds, fire_at, idle_only, enabled, next_due_at, last_fired_at,
                   last_outcome, last_seq, missed_count, created_at, created_by, event_type, filter,
                   watch_root, watch_path, watch_glob, poll_seconds, quiet_seconds,
                   min_interval_seconds, last_poll_at, last_poll_ms, last_poll_entries,
                   last_poll_error, last_change_at, last_fingerprint, wake_manager, daily_token_cap,
                   capped_skips_day, capped_skips, outcome_id, configured_by_email
        """;

    /// <summary>
    /// The value of every column the whole-row save writes, by parameter name without its prefix. The
    /// capped-skip count is not among them: a save must not write a stale count back over one a skip
    /// has just taken.
    /// </summary>
    public static IReadOnlyList<(string Name, object Value)> SaveValues(TriggerRow row) =>
    [
        ("id", row.Id),
        ("team", row.Team),
        ("container", row.Container),
        ("name", row.Name),
        ("instruction", row.Instruction),
        ("kind", row.Kind),
        ("expression", (object?)row.Expression ?? DBNull.Value),
        ("timezone", (object?)row.Timezone ?? DBNull.Value),
        ("intervalSeconds", row.IntervalSeconds is null ? DBNull.Value : row.IntervalSeconds.Value),
        ("fireAt", Stamp(row.FireAt)),
        ("idleOnly", row.IdleOnly ? 1 : 0),
        ("enabled", row.Enabled ? 1 : 0),
        ("nextDueAt", Stamp(row.NextDueAt)),
        ("lastFiredAt", Stamp(row.LastFiredAt)),
        ("lastOutcome", (object?)row.LastOutcome ?? DBNull.Value),
        ("lastSeq", row.LastSeq is null ? DBNull.Value : row.LastSeq.Value),
        ("missedCount", row.MissedCount),
        ("createdAt", Instant.Stamp(row.CreatedAt)),
        ("createdBy", row.CreatedBy),
        ("eventType", (object?)row.EventType ?? DBNull.Value),
        ("filter", (object?)row.Filter ?? DBNull.Value),
        ("watchRoot", (object?)row.WatchRoot ?? DBNull.Value),
        ("watchPath", (object?)row.WatchPath ?? DBNull.Value),
        ("watchGlob", (object?)row.WatchGlob ?? DBNull.Value),
        ("pollSeconds", (object?)row.PollSeconds ?? DBNull.Value),
        ("quietSeconds", (object?)row.QuietSeconds ?? DBNull.Value),
        ("minIntervalSeconds", (object?)row.MinIntervalSeconds ?? DBNull.Value),
        ("wakeManager", row.WakeManager),
        ("dailyTokenCap", row.DailyTokenCap is null ? DBNull.Value : row.DailyTokenCap.Value),
        ("outcomeId", (object?)row.OutcomeId ?? DBNull.Value),
        ("configuredByEmail", (object?)row.ConfiguredByEmail ?? DBNull.Value),
    ];

    /// <summary>The value of every column one folder poll writes (<see cref="FolderPollRecord"/>), by
    /// parameter name without its prefix.</summary>
    public static IReadOnlyList<(string Name, object Value)> PollValues(FolderPollRecord poll) =>
    [
        ("polledAt", Instant.Stamp(poll.PolledAt)),
        ("elapsedMs", poll.ElapsedMs),
        ("entries", (object?)poll.Entries ?? DBNull.Value),
        ("error", (object?)poll.Error ?? DBNull.Value),
        ("nextDueAt", Stamp(poll.NextDueAt)),
        ("fingerprint", (object?)poll.Fingerprint ?? DBNull.Value),
        ("listing", (object?)poll.Listing ?? DBNull.Value),
        ("pendingFingerprint", (object?)poll.PendingFingerprint ?? DBNull.Value),
        ("pendingSince", Stamp(poll.PendingSince)),
        ("changeAt", Stamp(poll.ChangeAt)),
    ];

    /// <summary>UTC, always: `next_due_at` is compared as TEXT, and a round-trip string with a local
    /// offset (what a cron time in a trigger's timezone comes back as) sorts wrong.</summary>
    public static object Stamp(DateTimeOffset? value) =>
        value is null ? DBNull.Value : Instant.Stamp(value.Value);

    public static TriggerRow Read(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            NullableString(reader, 6),
            NullableString(reader, 7),
            NullableInt(reader, 8),
            NullableDate(reader, 9),
            reader.GetInt64(10) == 1,
            reader.GetInt64(11) == 1,
            NullableDate(reader, 12),
            NullableDate(reader, 13),
            NullableString(reader, 14),
            NullableLong(reader, 15),
            checked((int)reader.GetInt64(16)),
            DateTimeOffset.Parse(reader.GetString(17), CultureInfo.InvariantCulture),
            reader.GetString(18),
            NullableString(reader, 19),
            NullableString(reader, 20),
            NullableString(reader, 21),
            NullableString(reader, 22),
            NullableString(reader, 23),
            NullableInt(reader, 24),
            NullableInt(reader, 25),
            NullableInt(reader, 26),
            NullableDate(reader, 27),
            NullableInt(reader, 28),
            NullableInt(reader, 29),
            NullableString(reader, 30),
            NullableDate(reader, 31),
            NullableString(reader, 32),
            reader.GetString(33),
            NullableLong(reader, 34),
            NullableDate(reader, 35),
            checked((int)reader.GetInt64(36)))
        {
            OutcomeId = NullableString(reader, 37),
            ConfiguredByEmail = NullableString(reader, 38),
        };

    /// <summary>The watch's memory between polls: <c>last_listing, pending_fingerprint, pending_since</c>
    /// in that order, or the empty state when the trigger has no row.</summary>
    public static async Task<FolderWatchState> ReadWatchStateAsync(DbDataReader reader, CancellationToken ct) =>
        await reader.ReadAsync(ct)
            ? new FolderWatchState(NullableString(reader, 0), NullableString(reader, 1), NullableDate(reader, 2))
            : new FolderWatchState(null, null, null);

    private static string? NullableString(DbDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static int? NullableInt(DbDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : checked((int)reader.GetInt64(index));

    private static long? NullableLong(DbDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetInt64(index);

    private static DateTimeOffset? NullableDate(DbDataReader reader, int index) =>
        reader.IsDBNull(index)
            ? null
            : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
}
