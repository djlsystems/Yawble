using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Identity;

public sealed class SqliteTriggerStore : ITriggerStore
{
    private readonly string _connectionString;

    /// <summary>Absence means the safe value. Only the test fixture asks for anything else.</summary>
    private readonly SqliteDurability _durability;

    public SqliteTriggerStore(string databasePath, SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        _durability = durability;
    }

    public async Task SaveAsync(TriggerRow row, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = Upsert;
        BindSave(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAsync(TriggerRow row, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = Upsert;
            BindSave(command, row);
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>The whole-row upsert. The capped-skip count is not in it: a save must not write a
    /// stale count back over one a skip has just taken.</summary>
    private const string Upsert =
            """
            INSERT INTO triggers
                (id, team, container, name, instruction, kind, expression, timezone,
                 interval_seconds, fire_at, idle_only, enabled, next_due_at, last_fired_at,
                 last_outcome, last_seq, missed_count, created_at, created_by, event_type, filter,
                 watch_root, watch_path, watch_glob, poll_seconds, quiet_seconds, min_interval_seconds,
                 wake_manager, daily_token_cap, outcome_id, configured_by_email)
            VALUES
                ($id, $team, $container, $name, $instruction, $kind, $expression, $timezone,
                 $intervalSeconds, $fireAt, $idleOnly, $enabled, $nextDueAt, $lastFiredAt,
                 $lastOutcome, $lastSeq, $missedCount, $createdAt, $createdBy, $eventType, $filter,
                 $watchRoot, $watchPath, $watchGlob, $pollSeconds, $quietSeconds, $minIntervalSeconds,
                 $wakeManager, $dailyTokenCap, $outcomeId, $configuredByEmail)
            ON CONFLICT(id) DO UPDATE SET
                team             = excluded.team,
                container        = excluded.container,
                name             = excluded.name,
                instruction      = excluded.instruction,
                kind             = excluded.kind,
                expression       = excluded.expression,
                timezone         = excluded.timezone,
                interval_seconds = excluded.interval_seconds,
                fire_at          = excluded.fire_at,
                idle_only        = excluded.idle_only,
                enabled          = excluded.enabled,
                next_due_at      = excluded.next_due_at,
                last_fired_at    = excluded.last_fired_at,
                last_outcome     = excluded.last_outcome,
                last_seq         = excluded.last_seq,
                missed_count     = excluded.missed_count,
                created_by       = excluded.created_by,
                event_type       = excluded.event_type,
                filter           = excluded.filter,

                -- A MOVED WATCH STARTS AGAIN FROM A FRESH BASELINE. Comparing the new folder's
                -- listing with the old folder's would fire on every file that happens to be there.
                last_fingerprint = CASE
                    WHEN triggers.watch_root IS excluded.watch_root
                     AND triggers.watch_path IS excluded.watch_path
                     AND triggers.watch_glob IS excluded.watch_glob
                    THEN triggers.last_fingerprint ELSE NULL END,
                last_listing = CASE
                    WHEN triggers.watch_root IS excluded.watch_root
                     AND triggers.watch_path IS excluded.watch_path
                     AND triggers.watch_glob IS excluded.watch_glob
                    THEN triggers.last_listing ELSE NULL END,
                pending_fingerprint = CASE
                    WHEN triggers.watch_root IS excluded.watch_root
                     AND triggers.watch_path IS excluded.watch_path
                     AND triggers.watch_glob IS excluded.watch_glob
                    THEN triggers.pending_fingerprint ELSE NULL END,
                watch_root           = excluded.watch_root,
                watch_path           = excluded.watch_path,
                watch_glob           = excluded.watch_glob,
                poll_seconds         = excluded.poll_seconds,
                quiet_seconds        = excluded.quiet_seconds,
                min_interval_seconds = excluded.min_interval_seconds,
                wake_manager         = excluded.wake_manager,
                daily_token_cap      = excluded.daily_token_cap,
                outcome_id           = excluded.outcome_id,

                -- A SAVE BY NO PERSON KEEPS THE CONFIGURER: only a person's email replaces it.
                configured_by_email  = COALESCE(excluded.configured_by_email, triggers.configured_by_email)
            """;

    public async Task<IReadOnlyList<TriggerRow>> ListForTeamAsync(
        string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT id, team, container, name, instruction, kind, expression, timezone,
                   interval_seconds, fire_at, idle_only, enabled, next_due_at, last_fired_at,
                   last_outcome, last_seq, missed_count, created_at, created_by, event_type, filter,
                   watch_root, watch_path, watch_glob, poll_seconds, quiet_seconds,
                   min_interval_seconds, last_poll_at, last_poll_ms, last_poll_entries,
                   last_poll_error, last_change_at, last_fingerprint, wake_manager, daily_token_cap,
                   capped_skips_day, capped_skips, outcome_id, configured_by_email
            FROM triggers
            WHERE team = $team COLLATE NOCASE
            ORDER BY created_at, id
            """;

        command.Parameters.AddWithValue("$team", team);
        return await ReadRowsAsync(command, ct);
    }

    public async Task<TriggerRow?> FindAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT id, team, container, name, instruction, kind, expression, timezone,
                   interval_seconds, fire_at, idle_only, enabled, next_due_at, last_fired_at,
                   last_outcome, last_seq, missed_count, created_at, created_by, event_type, filter,
                   watch_root, watch_path, watch_glob, poll_seconds, quiet_seconds,
                   min_interval_seconds, last_poll_at, last_poll_ms, last_poll_entries,
                   last_poll_error, last_change_at, last_fingerprint, wake_manager, daily_token_cap,
                   capped_skips_day, capped_skips, outcome_id, configured_by_email
            FROM triggers
            WHERE id = $id
            """;

        command.Parameters.AddWithValue("$id", id);
        return (await ReadRowsAsync(command, ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<TriggerRow>> DueAsync(
        DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT id, team, container, name, instruction, kind, expression, timezone,
                   interval_seconds, fire_at, idle_only, enabled, next_due_at, last_fired_at,
                   last_outcome, last_seq, missed_count, created_at, created_by, event_type, filter,
                   watch_root, watch_path, watch_glob, poll_seconds, quiet_seconds,
                   min_interval_seconds, last_poll_at, last_poll_ms, last_poll_entries,
                   last_poll_error, last_change_at, last_fingerprint, wake_manager, daily_token_cap,
                   capped_skips_day, capped_skips, outcome_id, configured_by_email
            FROM triggers
            WHERE enabled = 1
              AND next_due_at IS NOT NULL
              AND next_due_at <= $now
            ORDER BY next_due_at, id
            """;

        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
        return await ReadRowsAsync(command, ct);
    }

    public async Task<DateTimeOffset?> EarliestDueAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // MIN over the same index the due query uses, so this is a lookup rather than a scan.
        //
        // The value is compared as TEXT, which is exact here and would not be for a format chosen
        // any other way: every write uses round-trip "O", so the strings are fixed-width UTC and
        // sort identically to the instants they encode. A stored local-offset timestamp would sort
        // wrong, which is the reason the writes are pinned to "O" rather than a convention.
        command.CommandText =
            """
            SELECT MIN(next_due_at)
            FROM triggers
            WHERE enabled = 1 AND next_due_at IS NOT NULL
            """;

        var value = await command.ExecuteScalarAsync(ct);

        return value is string text && DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    public async Task RecordOutcomeAsync(
        string id,
        DateTimeOffset? firedAt,
        DateTimeOffset? nextDueAt,
        string? outcome,
        long? seq,
        int missed,
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // Narrow outcome-only update: no caller re-sends the full row.
        command.CommandText =
            """
            UPDATE triggers
            SET last_fired_at = $firedAt,
                next_due_at   = $nextDueAt,
                last_outcome  = $outcome,
                last_seq      = $seq,
                missed_count  = $missed
            WHERE id = $id
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$firedAt", ToDbValue(firedAt));
        command.Parameters.AddWithValue("$nextDueAt", ToDbValue(nextDueAt));
        command.Parameters.AddWithValue("$outcome", (object?)outcome ?? DBNull.Value);
        command.Parameters.AddWithValue("$seq", seq is null ? DBNull.Value : seq.Value);
        command.Parameters.AddWithValue("$missed", missed);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordFireAsync(
        string id, DateTimeOffset firedAt, string outcome, long seq, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // Narrower still than RecordOutcomeAsync: next_due_at and missed_count are left alone.
        command.CommandText =
            """
            UPDATE triggers
            SET last_fired_at = $firedAt,
                last_outcome  = $outcome,
                last_seq      = $seq
            WHERE id = $id
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$firedAt", ToDbValue(firedAt));
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$seq", seq);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordSkipAsync(string id, string outcome, long seq, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE triggers SET last_outcome = $outcome, last_seq = $seq WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$seq", seq);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountCappedSkipAsync(
        string id, DateTimeOffset dayStart, bool rearm, DateTimeOffset? nextDueAt, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var count = await CountCappedSkipAsync(connection, transaction, id, dayStart, rearm, nextDueAt, ct);
        await transaction.CommitAsync(ct);
        return count;
    }

    public async Task<int> CountCappedSkipAsync(
        DbConnection connection, DbTransaction transaction,
        string id, DateTimeOffset dayStart, bool rearm, DateTimeOffset? nextDueAt, CancellationToken ct = default)
    {
        await using var command = (SqliteCommand)connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;

        command.CommandText =
            """
            UPDATE triggers
            SET capped_skips     = CASE WHEN capped_skips_day IS $day THEN capped_skips + 1 ELSE 1 END,
                capped_skips_day = $day,
                last_outcome     = 'capped',
                next_due_at      = CASE WHEN $rearm = 1 THEN $nextDueAt ELSE next_due_at END
            WHERE id = $id
            RETURNING capped_skips
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$day", ToDbValue(dayStart));
        command.Parameters.AddWithValue("$rearm", rearm ? 1 : 0);
        command.Parameters.AddWithValue("$nextDueAt", ToDbValue(nextDueAt));

        return await command.ExecuteScalarAsync(ct) is long count ? (int)count : 0;
    }

    public async Task RecordCappedSkipAsync(string id, long seq, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await RecordCappedSkipAsync(connection, transaction, id, seq, audit, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task RecordCappedSkipAsync(
        DbConnection connection, DbTransaction transaction, string id, long seq, TriggerAudit audit,
        CancellationToken ct = default)
    {
        var sqlite = (SqliteConnection)connection;
        var within = (SqliteTransaction)transaction;

        await using (var command = sqlite.CreateCommand())
        {
            command.Transaction = within;
            command.CommandText = "UPDATE triggers SET last_seq = $seq WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$seq", seq);
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(sqlite, within, audit, ct);
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE triggers SET enabled = $enabled WHERE id = $id";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM triggers WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string id, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM triggers WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<int> DeleteForTeamAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM triggers WHERE team = $team COLLATE NOCASE";
        command.Parameters.AddWithValue("$team", team);

        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DeleteForContainerAsync(
        string team, string container, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            DELETE FROM triggers
            WHERE team = $team COLLATE NOCASE
              AND container = $container COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$container", container);

        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<TriggerRow>> ListForContainerAsync(
        string team, string container, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT id, team, container, name, instruction, kind, expression, timezone,
                   interval_seconds, fire_at, idle_only, enabled, next_due_at, last_fired_at,
                   last_outcome, last_seq, missed_count, created_at, created_by, event_type, filter,
                   watch_root, watch_path, watch_glob, poll_seconds, quiet_seconds,
                   min_interval_seconds, last_poll_at, last_poll_ms, last_poll_entries,
                   last_poll_error, last_change_at, last_fingerprint, wake_manager, daily_token_cap,
                   capped_skips_day, capped_skips, outcome_id, configured_by_email
            FROM triggers
            WHERE team = $team COLLATE NOCASE
              AND container = $container COLLATE NOCASE
            ORDER BY created_at, id
            """;

        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$container", container);
        return await ReadRowsAsync(command, ct);
    }

    public async Task<int> CountKindAsync(string kind, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM triggers WHERE kind = $kind COLLATE NOCASE";
        command.Parameters.AddWithValue("$kind", kind);

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<FolderWatchState> WatchStateAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT last_listing, pending_fingerprint, pending_since FROM triggers WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct)
            ? new FolderWatchState(
                ReadNullableString(reader, 0),
                ReadNullableString(reader, 1),
                ReadNullableDate(reader, 2))
            : new FolderWatchState(null, null, null);
    }

    public async Task RecordPollAsync(string id, FolderPollRecord poll, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            UPDATE triggers
            SET last_poll_at        = $polledAt,
                last_poll_ms        = $elapsedMs,
                last_poll_entries   = COALESCE($entries, last_poll_entries),
                last_poll_error     = $error,
                next_due_at         = $nextDueAt,
                last_fingerprint    = COALESCE($fingerprint, last_fingerprint),
                last_listing        = COALESCE($listing, last_listing),
                pending_fingerprint = $pendingFingerprint,
                pending_since       = $pendingSince,
                last_change_at      = COALESCE($changeAt, last_change_at)
            WHERE id = $id
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$polledAt", poll.PolledAt.ToString("O"));
        command.Parameters.AddWithValue("$elapsedMs", poll.ElapsedMs);
        command.Parameters.AddWithValue("$entries", (object?)poll.Entries ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)poll.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$nextDueAt", ToDbValue(poll.NextDueAt));
        command.Parameters.AddWithValue("$fingerprint", (object?)poll.Fingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$listing", (object?)poll.Listing ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$pendingFingerprint", (object?)poll.PendingFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$pendingSince", ToDbValue(poll.PendingSince));
        command.Parameters.AddWithValue("$changeAt", ToDbValue(poll.ChangeAt));

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// busy_timeout AND foreign_keys. The second is per-connection and defaults OFF in SQLite, so a
    /// cascade declared in schema does nothing on a connection that does not set it.
    /// </summary>
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        SqlitePragmas.Apply(connection, durability: _durability);

        return connection;
    }

    private static async Task<IReadOnlyList<TriggerRow>> ReadRowsAsync(
        SqliteCommand command, CancellationToken ct)
    {
        var rows = new List<TriggerRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TriggerRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                ReadNullableString(reader, 6),
                ReadNullableString(reader, 7),
                ReadNullableInt(reader, 8),
                ReadNullableDate(reader, 9),
                reader.GetInt64(10) == 1,
                reader.GetInt64(11) == 1,
                ReadNullableDate(reader, 12),
                ReadNullableDate(reader, 13),
                ReadNullableString(reader, 14),
                ReadNullableLong(reader, 15),
                reader.GetInt32(16),
                DateTimeOffset.Parse(reader.GetString(17), CultureInfo.InvariantCulture),
                reader.GetString(18),
                ReadNullableString(reader, 19),
                ReadNullableString(reader, 20),
                ReadNullableString(reader, 21),
                ReadNullableString(reader, 22),
                ReadNullableString(reader, 23),
                ReadNullableInt(reader, 24),
                ReadNullableInt(reader, 25),
                ReadNullableInt(reader, 26),
                ReadNullableDate(reader, 27),
                ReadNullableInt(reader, 28),
                ReadNullableInt(reader, 29),
                ReadNullableString(reader, 30),
                ReadNullableDate(reader, 31),
                ReadNullableString(reader, 32),
                reader.GetString(33),
                ReadNullableLong(reader, 34),
                ReadNullableDate(reader, 35),
                reader.GetInt32(36))
            {
                OutcomeId = ReadNullableString(reader, 37),
                ConfiguredByEmail = ReadNullableString(reader, 38),
            });
        }

        return rows;
    }

    private static void BindSave(SqliteCommand command, TriggerRow row)
    {
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$team", row.Team);
        command.Parameters.AddWithValue("$container", row.Container);
        command.Parameters.AddWithValue("$name", row.Name);
        command.Parameters.AddWithValue("$instruction", row.Instruction);
        command.Parameters.AddWithValue("$kind", row.Kind);
        command.Parameters.AddWithValue("$expression", (object?)row.Expression ?? DBNull.Value);
        command.Parameters.AddWithValue("$timezone", (object?)row.Timezone ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$intervalSeconds",
            row.IntervalSeconds is null ? DBNull.Value : row.IntervalSeconds.Value);
        command.Parameters.AddWithValue("$fireAt", ToDbValue(row.FireAt));
        command.Parameters.AddWithValue("$idleOnly", row.IdleOnly ? 1 : 0);
        command.Parameters.AddWithValue("$enabled", row.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$nextDueAt", ToDbValue(row.NextDueAt));
        command.Parameters.AddWithValue("$lastFiredAt", ToDbValue(row.LastFiredAt));
        command.Parameters.AddWithValue("$lastOutcome", (object?)row.LastOutcome ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$lastSeq", row.LastSeq is null ? DBNull.Value : row.LastSeq.Value);
        command.Parameters.AddWithValue("$missedCount", row.MissedCount);
        command.Parameters.AddWithValue("$createdAt", row.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$createdBy", row.CreatedBy);
        command.Parameters.AddWithValue("$eventType", (object?)row.EventType ?? DBNull.Value);
        command.Parameters.AddWithValue("$filter", (object?)row.Filter ?? DBNull.Value);
        command.Parameters.AddWithValue("$watchRoot", (object?)row.WatchRoot ?? DBNull.Value);
        command.Parameters.AddWithValue("$watchPath", (object?)row.WatchPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$watchGlob", (object?)row.WatchGlob ?? DBNull.Value);
        command.Parameters.AddWithValue("$pollSeconds", (object?)row.PollSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$quietSeconds", (object?)row.QuietSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$minIntervalSeconds", (object?)row.MinIntervalSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$wakeManager", row.WakeManager);
        command.Parameters.AddWithValue(
            "$dailyTokenCap", row.DailyTokenCap is null ? DBNull.Value : row.DailyTokenCap.Value);
        command.Parameters.AddWithValue("$outcomeId", (object?)row.OutcomeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$configuredByEmail", (object?)row.ConfiguredByEmail ?? DBNull.Value);
    }

    /// <summary>UTC, always: `next_due_at` is compared as TEXT, and a round-trip string with a local
    /// offset (what a cron time in a trigger's timezone comes back as) sorts wrong.</summary>
    private static object ToDbValue(DateTimeOffset? value) =>
        value is null ? DBNull.Value : value.Value.ToUniversalTime().ToString("O");

    private static string? ReadNullableString(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static int? ReadNullableInt(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetInt32(index);

    private static long? ReadNullableLong(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetInt64(index);

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index)
            ? null
            : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
}
