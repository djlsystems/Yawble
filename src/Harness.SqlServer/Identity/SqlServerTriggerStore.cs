using System.Data.Common;
using Harness.Contracts;
using Harness.Identity;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Identity;

/// <summary>
/// <see cref="ITriggerStore"/> over <c>triggers</c> on SQL Server: SQLite's <c>SqliteTriggerStore</c>
/// write for write, whose remarks are the argument (a whole-row save that leaves the capped-skip count
/// alone, narrow outcome and poll writes, a moved watch starting from a fresh baseline). What a column
/// holds and how a row reads back are <see cref="TriggerRows"/>', shared with it.
///
/// <para>
/// A TEAM AND A CONTAINER FOLD CASE, AS SQLITE'S <c>COLLATE NOCASE</c> COLUMNS DO: the columns carry
/// the case-insensitive collation. A trigger id, a kind as stored and a watch's folder do not.
/// </para>
///
/// <para>
/// EVERY TEXT COMPARISON IS <see cref="SqlServerSql.Same"/>, so a trailing space decides it as it does
/// in SQLite: a trigger id arrives from a route, and a watch's folder is what a person typed. The one
/// exception is the upsert's match on the id, which compares as the primary key itself does, so a save
/// never fails on that key; a saved id is one the create route minted (a GUID), never typed.
/// </para>
///
/// <para>
/// THE SAVE IS ONE <c>MERGE</c> UNDER <c>HOLDLOCK</c>. SQLite's upsert runs alone; here two saves of one
/// new trigger would otherwise both find no row, and one would fail on the key. (The team lock below
/// already puts two saves on one team one after the other; the merge's own hold keeps that true of a
/// save whatever its team.) The merge also decides
/// whether the watch moved from the row it holds, so a poll's baseline is kept or dropped against the
/// row as it stands. THE CAPPED-SKIP COUNT IS ONE <c>UPDATE ... OUTPUT</c>: the statement that reads the
/// count writes it, so two skips cannot both be the first. Every other write is one statement on one
/// row.
/// </para>
///
/// <para>
/// LOCK ORDER: THE <c>teams</c> ROW, THEN THE <c>triggers</c> ROW, THEN <c>tenant_events</c>. Deleting a
/// team locks its row and then, through the cascade, its triggers; a save writes <c>triggers.team</c>
/// and so checks the team for the foreign key after locking its own key. Taken in those two orders, a
/// deletion and a save on one team each hold what the other waits for. So a save first takes its team's
/// row under <c>UPDLOCK, HOLDLOCK</c> (<see cref="TeamLock"/>) in its own transaction: both orders are
/// teams first. A narrow write never touches <c>team</c>, so it locks its one row. <c>tenant_events</c>
/// rows are inserts that read nothing, appended last. No write here takes the message log's append
/// lock; the overloads that run inside a caller's transaction take whatever that caller holds.
/// </para>
/// </summary>
public sealed class SqlServerTriggerStore(string connectionString) : ITriggerStore
{
    /// <summary>The row whose id is <c>@id</c>, exactly.</summary>
    private static readonly string IdMatch = SqlServerSql.Same("id", "@id");

    /// <summary>The team a save writes, taken before the trigger's own key: see the lock order above.</summary>
    private const string TeamLock = "SELECT COUNT(*) FROM teams WITH (UPDLOCK, HOLDLOCK) WHERE id = @team";

    private static readonly string Select = $"SELECT {TriggerRows.Columns} FROM triggers";

    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    /// <summary>
    /// The whole-row upsert. The capped-skip count is not in it: a save must not write a stale count
    /// back over one a skip has just taken. A moved watch drops its baseline, judged against the stored
    /// row with SQLite's <c>IS</c> (<see cref="SqlServerSql.SameOrBothNull"/>); a save by no person
    /// keeps the configurer.
    /// </summary>
    private static readonly string Upsert =
        $"""
         MERGE triggers WITH (HOLDLOCK) AS t
         USING (SELECT @id AS id) AS n ON t.id = n.id
         WHEN MATCHED THEN UPDATE SET
             team             = @team,
             container        = @container,
             name             = @name,
             instruction      = @instruction,
             kind             = @kind,
             expression       = @expression,
             timezone         = @timezone,
             interval_seconds = @intervalSeconds,
             fire_at          = @fireAt,
             idle_only        = @idleOnly,
             enabled          = @enabled,
             next_due_at      = @nextDueAt,
             last_fired_at    = @lastFiredAt,
             last_outcome     = @lastOutcome,
             last_seq         = @lastSeq,
             missed_count     = @missedCount,
             created_by       = @createdBy,
             event_type       = @eventType,
             filter           = @filter,
             last_fingerprint    = CASE WHEN {WatchUnmoved} THEN t.last_fingerprint ELSE NULL END,
             last_listing        = CASE WHEN {WatchUnmoved} THEN t.last_listing ELSE NULL END,
             pending_fingerprint = CASE WHEN {WatchUnmoved} THEN t.pending_fingerprint ELSE NULL END,
             watch_root           = @watchRoot,
             watch_path           = @watchPath,
             watch_glob           = @watchGlob,
             poll_seconds         = @pollSeconds,
             quiet_seconds        = @quietSeconds,
             min_interval_seconds = @minIntervalSeconds,
             wake_manager         = @wakeManager,
             daily_token_cap      = @dailyTokenCap,
             outcome_id           = @outcomeId,
             configured_by_email  = COALESCE(@configuredByEmail, t.configured_by_email)
         WHEN NOT MATCHED THEN INSERT
             (id, team, container, name, instruction, kind, expression, timezone,
              interval_seconds, fire_at, idle_only, enabled, next_due_at, last_fired_at,
              last_outcome, last_seq, missed_count, created_at, created_by, event_type, filter,
              watch_root, watch_path, watch_glob, poll_seconds, quiet_seconds, min_interval_seconds,
              wake_manager, daily_token_cap, outcome_id, configured_by_email)
         VALUES
             (@id, @team, @container, @name, @instruction, @kind, @expression, @timezone,
              @intervalSeconds, @fireAt, @idleOnly, @enabled, @nextDueAt, @lastFiredAt,
              @lastOutcome, @lastSeq, @missedCount, @createdAt, @createdBy, @eventType, @filter,
              @watchRoot, @watchPath, @watchGlob, @pollSeconds, @quietSeconds, @minIntervalSeconds,
              @wakeManager, @dailyTokenCap, @outcomeId, @configuredByEmail);
         """;

    /// <summary>The stored watch is the one being saved: root, folder and glob each the same, or both null.</summary>
    private static string WatchUnmoved =>
        $"{SqlServerSql.SameOrBothNull("t.watch_root", "@watchRoot")} "
        + $"AND {SqlServerSql.SameOrBothNull("t.watch_path", "@watchPath")} "
        + $"AND {SqlServerSql.SameOrBothNull("t.watch_glob", "@watchGlob")}";

    public Task SaveAsync(TriggerRow row, CancellationToken ct = default) => SaveWithAsync(row, null, ct);

    public Task SaveAsync(TriggerRow row, TriggerAudit audit, CancellationToken ct = default) =>
        SaveWithAsync(row, audit, ct);

    private Task SaveWithAsync(TriggerRow row, TriggerAudit? audit, CancellationToken ct) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var hold = Command(connection, transaction, TeamLock))
                {
                    hold.Parameters.AddWithValue("@team", row.Team);
                    await hold.ExecuteScalarAsync(token);
                }

                await using (var command = Command(connection, transaction, Upsert))
                {
                    foreach (var (name, value) in TriggerRows.SaveValues(row)) command.Parameters.AddWithValue("@" + name, value);
                    await command.ExecuteNonQueryAsync(token);
                }

                if (audit is not null) await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return 0;
            },
            ct);

    public Task<IReadOnlyList<TriggerRow>> ListForTeamAsync(string team, CancellationToken ct = default) =>
        ReadAsync(
            $"{Select} WHERE {SqlServerSql.Same("team", "@team")} ORDER BY created_at, id",
            command => command.Parameters.AddWithValue("@team", team),
            ct);

    public async Task<TriggerRow?> FindAsync(string id, CancellationToken ct = default) =>
        (await ReadAsync($"{Select} WHERE {IdMatch}", command => command.Parameters.AddWithValue("@id", id), ct))
            .SingleOrDefault();

    public Task<IReadOnlyList<TriggerRow>> DueAsync(DateTimeOffset now, CancellationToken ct = default) =>
        ReadAsync(
            $"""
             {Select}
             WHERE enabled = 1
               AND next_due_at IS NOT NULL
               AND next_due_at <= @now
             ORDER BY next_due_at, id
             """,
            command => command.Parameters.AddWithValue("@now", Instant.Stamp(now)),
            ct);

    /// <summary>MIN over the due query's index; the round-trip stamps sort as the instants they encode,
    /// as SQLite's remarks explain.</summary>
    public Task<DateTimeOffset?> EarliestDueAsync(CancellationToken ct = default) =>
        SqlServerOpen.RunAsync<DateTimeOffset?>(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT MIN(next_due_at) FROM triggers WHERE enabled = 1 AND next_due_at IS NOT NULL";

                return await command.ExecuteScalarAsync(token) is string text && Instant.TryParse(text, out var parsed)
                    ? parsed
                    : null;
            },
            ct);

    public Task RecordOutcomeAsync(
        string id, DateTimeOffset? firedAt, DateTimeOffset? nextDueAt, string? outcome, long? seq, int missed,
        CancellationToken ct = default) =>
        ExecuteAsync(
            $"""
             UPDATE triggers
             SET last_fired_at = @firedAt,
                 next_due_at   = @nextDueAt,
                 last_outcome  = @outcome,
                 last_seq      = @seq,
                 missed_count  = @missed
             WHERE {IdMatch}
             """,
            command =>
            {
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@firedAt", TriggerRows.Stamp(firedAt));
                command.Parameters.AddWithValue("@nextDueAt", TriggerRows.Stamp(nextDueAt));
                command.Parameters.AddWithValue("@outcome", (object?)outcome ?? DBNull.Value);
                command.Parameters.AddWithValue("@seq", seq is null ? DBNull.Value : seq.Value);
                command.Parameters.AddWithValue("@missed", missed);
            },
            ct);

    public Task RecordFireAsync(string id, DateTimeOffset firedAt, string outcome, long seq, CancellationToken ct = default) =>
        ExecuteAsync(
            $"UPDATE triggers SET last_fired_at = @firedAt, last_outcome = @outcome, last_seq = @seq WHERE {IdMatch}",
            command =>
            {
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@firedAt", TriggerRows.Stamp(firedAt));
                command.Parameters.AddWithValue("@outcome", outcome);
                command.Parameters.AddWithValue("@seq", seq);
            },
            ct);

    public Task RecordSkipAsync(string id, string outcome, long seq, CancellationToken ct = default) =>
        ExecuteAsync(
            $"UPDATE triggers SET last_outcome = @outcome, last_seq = @seq WHERE {IdMatch}",
            command =>
            {
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@outcome", outcome);
                command.Parameters.AddWithValue("@seq", seq);
            },
            ct);

    public Task<int> CountCappedSkipAsync(
        string id, DateTimeOffset dayStart, bool rearm, DateTimeOffset? nextDueAt, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                var count = await CountCappedSkipAsync(connection, transaction, id, dayStart, rearm, nextDueAt, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return count;
            },
            ct);

    /// <summary>
    /// ONE STATEMENT READS THE DAY'S COUNT AND WRITES THE NEXT: the update takes the row before it reads
    /// <c>capped_skips</c>, and <c>OUTPUT</c> answers what it wrote, so two skips at once count 1 and 2,
    /// never 1 and 1. <c>capped_skips_day</c> holds a round-trip stamp, which has no trailing space to
    /// decide, so SQLite's <c>IS</c> against a day that is never null is <c>=</c>.
    /// </summary>
    public async Task<int> CountCappedSkipAsync(
        DbConnection connection, DbTransaction transaction,
        string id, DateTimeOffset dayStart, bool rearm, DateTimeOffset? nextDueAt, CancellationToken ct = default)
    {
        await using var command = Command((SqlConnection)connection, (SqlTransaction)transaction,
            $"""
             UPDATE triggers
             SET capped_skips     = CASE WHEN capped_skips_day = @day THEN capped_skips + 1 ELSE 1 END,
                 capped_skips_day = @day,
                 last_outcome     = N'capped',
                 next_due_at      = CASE WHEN @rearm = 1 THEN @nextDueAt ELSE next_due_at END
             OUTPUT inserted.capped_skips
             WHERE {IdMatch}
             """);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@day", TriggerRows.Stamp(dayStart));
        command.Parameters.AddWithValue("@rearm", rearm ? 1 : 0);
        command.Parameters.AddWithValue("@nextDueAt", TriggerRows.Stamp(nextDueAt));

        return await command.ExecuteScalarAsync(ct) is long count ? (int)count : 0;
    }

    public Task RecordCappedSkipAsync(string id, long seq, TriggerAudit audit, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                await RecordCappedSkipAsync(connection, transaction, id, seq, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return 0;
            },
            ct);

    public async Task RecordCappedSkipAsync(
        DbConnection connection, DbTransaction transaction, string id, long seq, TriggerAudit audit,
        CancellationToken ct = default)
    {
        var sql = (SqlConnection)connection;
        var within = (SqlTransaction)transaction;

        await using (var command = Command(sql, within, $"UPDATE triggers SET last_seq = @seq WHERE {IdMatch}"))
        {
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@seq", seq);
            await command.ExecuteNonQueryAsync(ct);
        }

        await SqlServerTenantAuditRow.AppendAsync(sql, within, audit, ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) =>
        ExecuteAsync($"DELETE FROM triggers WHERE {IdMatch}", command => command.Parameters.AddWithValue("@id", id), ct);

    public Task DeleteAsync(string id, TriggerAudit audit, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var command = Command(connection, transaction, $"DELETE FROM triggers WHERE {IdMatch}"))
                {
                    command.Parameters.AddWithValue("@id", id);
                    await command.ExecuteNonQueryAsync(token);
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return 0;
            },
            ct);

    public Task<int> DeleteForTeamAsync(string team, CancellationToken ct = default) =>
        ExecuteAsync(
            $"DELETE FROM triggers WHERE {SqlServerSql.Same("team", "@team")}",
            command => command.Parameters.AddWithValue("@team", team),
            ct);

    public Task<int> DeleteForContainerAsync(string team, string container, CancellationToken ct = default) =>
        ExecuteAsync(
            $"DELETE FROM triggers WHERE {SqlServerSql.Same("team", "@team")} AND {SqlServerSql.Same("container", "@container")}",
            command =>
            {
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@container", container);
            },
            ct);

    public Task<IReadOnlyList<TriggerRow>> ListForContainerAsync(string team, string container, CancellationToken ct = default) =>
        ReadAsync(
            $"""
             {Select}
             WHERE {SqlServerSql.Same("team", "@team")}
               AND {SqlServerSql.Same("container", "@container")}
             ORDER BY created_at, id
             """,
            command =>
            {
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@container", container);
            },
            ct);

    /// <summary>SQLite compares <c>kind</c> under NOCASE here, though the column is not; so this names
    /// the case-insensitive collation on the comparison alone.</summary>
    public Task<int> CountKindAsync(string kind, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT COUNT(*) FROM triggers WHERE kind = @kind COLLATE {SqlServerSql.NoCase} AND DATALENGTH(kind) = DATALENGTH(@kind)";
                command.Parameters.AddWithValue("@kind", kind);
                return (int)(await command.ExecuteScalarAsync(token))!;
            },
            ct);

    public Task<FolderWatchState> WatchStateAsync(string id, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT last_listing, pending_fingerprint, pending_since FROM triggers WHERE {IdMatch}";
                command.Parameters.AddWithValue("@id", id);

                await using var reader = await command.ExecuteReaderAsync(token);
                return await TriggerRows.ReadWatchStateAsync(reader, token);
            },
            ct);

    public Task RecordPollAsync(string id, FolderPollRecord poll, CancellationToken ct = default) =>
        ExecuteAsync(
            $"""
             UPDATE triggers
             SET last_poll_at        = @polledAt,
                 last_poll_ms        = @elapsedMs,
                 last_poll_entries   = COALESCE(@entries, last_poll_entries),
                 last_poll_error     = @error,
                 next_due_at         = @nextDueAt,
                 last_fingerprint    = COALESCE(@fingerprint, last_fingerprint),
                 last_listing        = COALESCE(@listing, last_listing),
                 pending_fingerprint = @pendingFingerprint,
                 pending_since       = @pendingSince,
                 last_change_at      = COALESCE(@changeAt, last_change_at)
             WHERE {IdMatch}
             """,
            command =>
            {
                command.Parameters.AddWithValue("@id", id);
                foreach (var (name, value) in TriggerRows.PollValues(poll)) command.Parameters.AddWithValue("@" + name, value);
            },
            ct);

    private Task<int> ExecuteAsync(string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        SqlServerOpen.ExecuteAsync(_connectionString, sql, bind, ct);

    private Task<IReadOnlyList<TriggerRow>> ReadAsync(string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        SqlServerOpen.RunAsync<IReadOnlyList<TriggerRow>>(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                bind(command);

                var rows = new List<TriggerRow>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(TriggerRows.Read(reader));

                return rows;
            },
            ct);

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
