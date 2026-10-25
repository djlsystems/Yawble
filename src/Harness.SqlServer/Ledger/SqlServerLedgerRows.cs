using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;
using static Harness.Messaging.LedgerFacts;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// THE ONE WRITER OF <c>usage_ledger</c>, <c>workflow_ledger</c>, <c>delivery_ledger</c> AND
/// <c>nudge_ledger</c> ON SQL SERVER: SQLite's <c>LedgerRows</c> statement for statement, whose remarks
/// are the argument. What a row says is decided by <see cref="LedgerFacts"/> for both; this asks the
/// questions in T-SQL.
///
/// <para>
/// CALLED INSIDE THE APPEND'S TRANSACTION, under the log's append lock (<see cref="SqlServerAppendRows"/>),
/// so when a ledger row cannot be written the append throws and neither row is stored. The one call
/// outside it, <see cref="WriteDeliveryAsync"/>, runs in the pending delivery's transaction, which
/// does not take the append lock: two subscribers accepting one delivery at once both reach its
/// record, so the insert is decided under <c>UPDLOCK, HOLDLOCK</c> and one of them writes it.
/// </para>
/// </summary>
public static class SqlServerLedgerRows
{
    /// <summary>
    /// Writes <paramref name="stored"/>'s ledger row when it has one, and nothing otherwise.
    /// <paramref name="backfilled"/> marks a row recovered from the log at the ledger's start; such a
    /// write leaves a row already there, so a second pass adds nothing. Answers whether a row was written.
    /// </summary>
    public static async Task<bool> WriteAsync(
        SqlConnection connection, SqlTransaction transaction, Message stored, bool backfilled, CancellationToken ct)
    {
        if (LedgerRows.IsRunTerminal(stored)) return await WriteRunAsync(connection, transaction, stored, backfilled, ct);

        if (stored.Type is MessageTypes.WorkflowCompleted or MessageTypes.WorkflowClosed)
        {
            return await WriteWorkflowAsync(connection, transaction, stored, backfilled, ct);
        }

        if (LedgerRows.IsNudge(stored)) return await WriteNudgeAsync(connection, transaction, stored, ct);

        return false;
    }

    /// <summary>What the delivery <paramref name="deliverySeq"/> will be charged to, recorded as it is
    /// accepted; a delivery offered again adds nothing. Answers whether a row was written.</summary>
    public static async Task<bool> WriteDeliveryAsync(
        SqlConnection connection, SqlTransaction transaction, long deliverySeq, CancellationToken ct)
    {
        if (await RowAsync(connection, transaction, deliverySeq, ct) is not { } delivery) return false;

        var (triggerSource, triggerFiredAt) = await TriggerOfAsync(delivery, seq => RowAsync(connection, transaction, seq, ct));

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            IF NOT EXISTS (SELECT 1 FROM delivery_ledger WITH (UPDLOCK, HOLDLOCK) WHERE delivery_seq = @seq)
                INSERT INTO delivery_ledger (delivery_seq, correlation, queued_at, trigger_source, trigger_fired_at)
                VALUES (@seq, @correlation, @queued, @trigger, @firedAt);
            """;

        insert.Parameters.AddWithValue("@seq", delivery.Seq);
        insert.Parameters.AddWithValue("@correlation", delivery.CorrelationId);
        insert.Parameters.AddWithValue("@queued", delivery.OccurredAt);
        insert.Parameters.AddWithValue("@trigger", (object?)triggerSource ?? DBNull.Value);
        insert.Parameters.AddWithValue("@firedAt", (object?)triggerFiredAt ?? DBNull.Value);

        return await insert.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>The team's name as it is now, or its id when it has none: a snapshot.</summary>
    private const string TeamNameSql = "COALESCE((SELECT t.name FROM teams t WHERE t.id = @team), @team)";

    /// <summary>
    /// The insert, guarded for a backfill only: a live terminal row whose ledger key is already taken
    /// fails, as SQLite's plain INSERT does, and takes the append with it.
    /// </summary>
    private static string Insert(bool backfilled, string table, string key, string columns, string values) =>
        backfilled
            ? $"""
               IF NOT EXISTS (SELECT 1 FROM {table} WITH (UPDLOCK, HOLDLOCK) WHERE {key} = @seq)
                   INSERT INTO {table} ({columns}) VALUES ({values});
               """
            : $"INSERT INTO {table} ({columns}) VALUES ({values});";

    private static async Task<bool> WriteRunAsync(
        SqlConnection connection, SqlTransaction transaction, Message terminal, bool backfilled, CancellationToken ct)
    {
        var (teamId, member) = Split(terminal.Source);

        var attribution = terminal.CausationSeq is { } causeSeq
            ? await RecordedDeliveryAsync(connection, transaction, causeSeq, ct)
                ?? await LoggedDeliveryAsync(connection, transaction, causeSeq, ct)
            : null;

        var started = await StartedAsync(connection, transaction, terminal, ct);

        var outcome = await RunOutcomeAsync(
            terminal, () => BlockedAsync(connection, transaction, terminal, started?.Seq, ct));

        string? agent = null;
        if (teamId is not null)
        {
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT agent FROM team_members WHERE team = @team AND name = @name";
            lookup.Parameters.AddWithValue("@team", teamId);
            lookup.Parameters.AddWithValue("@name", member);
            agent = await lookup.ExecuteScalarAsync(ct) as string;
        }

        var usage = LedgerRows.Usage.Of(terminal.Payload);
        var kind = MemberKind(agent, usage);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = Insert(
            backfilled, "usage_ledger", "run_seq",
            """
            run_seq, correlation, team_id, team_name, member, member_kind, agent_preset,
            run_outcome, queued_at, started_at, ended_at, measured,
            tokens_in, tokens_cached_in, tokens_cache_creation, tokens_out, tokens_combined, billable,
            trigger_source, trigger_fired_at, backfilled
            """,
            $"""
             @seq, @correlation, @team, {TeamNameSql}, @member, @kind, @agent,
             @outcome, @queued, @started, @ended, @measured,
             @in, @cachedIn, @cacheCreation, @out, @combined, @billable,
             @trigger, @firedAt, @backfilled
             """);

        insert.Parameters.AddWithValue("@seq", terminal.Seq);
        insert.Parameters.AddWithValue("@correlation", terminal.CorrelationId);
        insert.Parameters.AddWithValue("@team", (object?)teamId ?? DBNull.Value);
        insert.Parameters.AddWithValue("@member", member);
        insert.Parameters.AddWithValue("@kind", (object?)kind ?? DBNull.Value);
        insert.Parameters.AddWithValue("@agent", (object?)agent ?? DBNull.Value);
        insert.Parameters.AddWithValue("@outcome", outcome);
        insert.Parameters.AddWithValue("@queued", (object?)attribution?.QueuedAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("@started", (object?)started?.OccurredAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("@ended", Instant.Stamp(terminal.OccurredAt));
        insert.Parameters.AddWithValue("@measured", usage.Measured ? 1L : 0L);
        Long(insert, "@in", usage.TokensIn);
        Long(insert, "@cachedIn", usage.CachedIn);
        Long(insert, "@cacheCreation", usage.CacheCreation);
        Long(insert, "@out", usage.TokensOut);
        Long(insert, "@combined", usage.Combined);
        Long(insert, "@billable", usage.Billable);
        insert.Parameters.AddWithValue("@trigger", (object?)attribution?.TriggerSource ?? DBNull.Value);
        insert.Parameters.AddWithValue("@firedAt", (object?)attribution?.TriggerFiredAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("@backfilled", backfilled ? 1L : 0L);

        return await insert.ExecuteNonQueryAsync(ct) > 0;
    }

    private static async Task<bool> WriteWorkflowAsync(
        SqlConnection connection, SqlTransaction transaction, Message close, bool backfilled, CancellationToken ct)
    {
        string? rootAt;
        await using (var root = connection.CreateCommand())
        {
            root.Transaction = transaction;
            root.CommandText = "SELECT occurred_at FROM messages WHERE seq = @root";
            root.Parameters.AddWithValue("@root", close.CorrelationId);
            rootAt = await root.ExecuteScalarAsync(ct) as string;
        }

        var teamId = MessageTeam.Of(close);

        // THE OUTCOME THE WORKFLOW SERVED WHEN IT CLOSED: its newest link's outcome, as linked.
        var outcome = (await SqlServerOutcomeLinks.CurrentAsync(connection, transaction, close.CorrelationId, ct))?.OutcomeId;

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = Insert(
            backfilled, "workflow_ledger", "close_seq",
            """
            close_seq, correlation, team_id, team_name, root_at, closed_at, how_closed,
            outcome_id_at_close, backfilled
            """,
            $"@seq, @correlation, @team, {TeamNameSql}, @rootAt, @closedAt, @how, @outcome, @backfilled");

        insert.Parameters.AddWithValue("@seq", close.Seq);
        insert.Parameters.AddWithValue("@correlation", close.CorrelationId);
        insert.Parameters.AddWithValue("@team", (object?)teamId ?? DBNull.Value);
        insert.Parameters.AddWithValue("@rootAt", (object?)rootAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("@closedAt", Instant.Stamp(close.OccurredAt));
        insert.Parameters.AddWithValue("@how", HowClosed(close));
        insert.Parameters.AddWithValue("@outcome", (object?)outcome ?? DBNull.Value);
        insert.Parameters.AddWithValue("@backfilled", backfilled ? 1L : 0L);

        return await insert.ExecuteNonQueryAsync(ct) > 0;
    }

    private static async Task<bool> WriteNudgeAsync(
        SqlConnection connection, SqlTransaction transaction, Message nudge, CancellationToken ct)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            IF NOT EXISTS (SELECT 1 FROM nudge_ledger WITH (UPDLOCK, HOLDLOCK) WHERE nudge_seq = @seq)
                INSERT INTO nudge_ledger (nudge_seq, correlation, nudged_at) VALUES (@seq, @correlation, @at);
            """;

        insert.Parameters.AddWithValue("@seq", nudge.Seq);
        insert.Parameters.AddWithValue("@correlation", nudge.CorrelationId);
        insert.Parameters.AddWithValue("@at", Instant.Stamp(nudge.OccurredAt));

        return await insert.ExecuteNonQueryAsync(ct) > 0;
    }

    private static async Task<Attribution?> RecordedDeliveryAsync(
        SqlConnection connection, SqlTransaction transaction, long deliverySeq, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT queued_at, trigger_source, trigger_fired_at FROM delivery_ledger WHERE delivery_seq = @seq";
        command.Parameters.AddWithValue("@seq", deliverySeq);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new Attribution(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>The fallback for a delivery with no record: the log, as it is now.</summary>
    private static async Task<Attribution?> LoggedDeliveryAsync(
        SqlConnection connection, SqlTransaction transaction, long deliverySeq, CancellationToken ct)
    {
        if (await RowAsync(connection, transaction, deliverySeq, ct) is not { } delivery) return null;

        var (triggerSource, triggerFiredAt) = await TriggerOfAsync(delivery, seq => RowAsync(connection, transaction, seq, ct));
        return new Attribution(delivery.OccurredAt, triggerSource, triggerFiredAt);
    }

    private static async Task<LinkedRow?> RowAsync(
        SqlConnection connection, SqlTransaction transaction, long seq, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT seq, type, source, occurred_at, causation_seq, correlation_id FROM messages WHERE seq = @seq";
        command.Parameters.AddWithValue("@seq", seq);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new LinkedRow(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetInt64(5));
    }

    /// <summary>The run's `started` row: the member's latest before the terminal and after the delivery.</summary>
    private static async Task<LinkedRow?> StartedAsync(
        SqlConnection connection, SqlTransaction transaction, Message terminal, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT seq, type, source, occurred_at, causation_seq FROM messages
             WHERE seq = (SELECT MAX(seq) FROM messages
                          WHERE source = @source COLLATE {SqlServerSql.NoCase} AND type = @started
                            AND seq < @terminal AND seq > @after)
             """;
        command.Parameters.AddWithValue("@source", terminal.Source);
        command.Parameters.AddWithValue("@started", MessageTypes.Started);
        command.Parameters.AddWithValue("@terminal", terminal.Seq);
        command.Parameters.AddWithValue("@after", terminal.CausationSeq ?? 0);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new LinkedRow(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }

    /// <summary>Whether the run published a run-level `blocked` (naming no item) or asked a person to
    /// decide, between its start and this row.</summary>
    private static async Task<bool> BlockedAsync(
        SqlConnection connection, SqlTransaction transaction, Message terminal, long? startedSeq, CancellationToken ct)
    {
        if (startedSeq is not { } from) return false;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT CASE WHEN EXISTS (
                 SELECT 1 FROM messages
                 WHERE source = @source COLLATE {SqlServerSql.NoCase} AND type IN (@blocked, @needsDecision)
                   AND seq > @from AND seq < @terminal
                   AND {SqlServerSql.Absent("payload", PayloadFields.Item)}) THEN 1 ELSE 0 END
             """;
        command.Parameters.AddWithValue("@source", terminal.Source);
        command.Parameters.AddWithValue("@blocked", MessageTypes.Blocked);
        command.Parameters.AddWithValue("@needsDecision", MessageTypes.NeedsDecision);
        command.Parameters.AddWithValue("@from", from);
        command.Parameters.AddWithValue("@terminal", terminal.Seq);

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0;
    }

    private static void Long(SqlCommand command, string name, long? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
