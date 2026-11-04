using System.Text.Json;
using Harness.Contracts;
using Microsoft.Data.Sqlite;
using static Harness.Messaging.LedgerFacts;

namespace Harness.Messaging;

/// <summary>
/// THE ONE WRITER OF <c>usage_ledger</c>, <c>workflow_ledger</c>, <c>delivery_ledger</c>,
/// <c>nudge_ledger</c> AND <c>execution_snapshots</c> (see <see cref="OutcomeSchema"/>).
///
/// <para>
/// CALLED FROM <c>SqliteMessageStore.InsertAsync</c>, inside the append's own transaction, which is
/// the single place every row reaches the log - so no path that writes a run's terminal row or a
/// workflow's close can skip its ledger row, and when the ledger row cannot be written the append
/// throws and neither row is stored. The backfill calls the same method over the rows already on the
/// log, so a recovered row is built exactly as a live one is.
/// </para>
///
/// <para>
/// ONE ROW PER RUN. A run's terminal row is its `completed` or `failed` row that carries no
/// <c>usageCountedOn</c>: a batched run closes each delivery with its own row, and every row after
/// the first names the first and carries no figures, so they write nothing here - a run is billed
/// once, as the spend reads always billed it.
/// </para>
/// </summary>
public static class LedgerRows
{
    /// <summary>
    /// Writes <paramref name="stored"/>'s ledger row when it has one, and nothing otherwise.
    /// <paramref name="backfilled"/> marks a row recovered from the log at the start that applied
    /// <c>outcome-001</c>; such a write ignores a row already there, so a second pass adds nothing.
    /// Answers whether a row was written.
    /// </summary>
    public static async Task<bool> WriteAsync(
        SqliteConnection connection, SqliteTransaction transaction, Message stored, bool backfilled,
        CancellationToken ct)
    {
        if (IsRunTerminal(stored)) return await WriteRunAsync(connection, transaction, stored, backfilled, ct);

        // A RUN'S START WRITES ITS SNAPSHOT, live only: a backfill would price history at today's
        // configuration, which is exactly what a snapshot exists not to do.
        if (stored.Type == MessageTypes.Started) return !backfilled && await WriteSnapshotAsync(connection, transaction, stored, ct);

        if (stored.Type is MessageTypes.WorkflowCompleted or MessageTypes.WorkflowClosed)
        {
            return await WriteWorkflowAsync(connection, transaction, stored, backfilled, ct);
        }

        if (IsNudge(stored)) return await WriteNudgeAsync(connection, transaction, stored, ct);

        return false;
    }

    /// <summary>
    /// A NUDGE: an instruction caused by the workflow's root itself - the rows the budget's window
    /// starts at (<c>GetSpendSinceNudgeAsync</c>). By construction, as the nudge route writes it.
    /// </summary>
    public static bool IsNudge(Message row) =>
        row.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
        && row.CausationSeq is { } cause && cause == row.CorrelationId;

    /// <summary>
    /// Records what the delivery <paramref name="deliverySeq"/> will be charged to when its run
    /// ends - when it was queued and the trigger fire it answers - at the moment it is accepted, in
    /// the caller's transaction (<c>SqlitePendingDeliveries.AddAsync</c>). Resolved from the log now,
    /// while the rows it follows are still there. A delivery offered again (a re-offer, a restart,
    /// a second subscriber of the same row) resolves the same answer and adds nothing. Answers
    /// whether a row was written.
    /// </summary>
    public static async Task<bool> WriteDeliveryAsync(
        SqliteConnection connection, SqliteTransaction transaction, long deliverySeq, CancellationToken ct)
    {
        if (await RowAsync(connection, transaction, deliverySeq, ct) is not { } delivery) return false;

        var (triggerSource, triggerFiredAt) = await TriggerOfAsync(connection, transaction, delivery, ct);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT OR IGNORE INTO delivery_ledger (delivery_seq, correlation, queued_at, trigger_source, trigger_fired_at)
            VALUES ($seq, $correlation, $queued, $trigger, $firedAt)
            """;

        insert.Parameters.AddWithValue("$seq", delivery.Seq);
        insert.Parameters.AddWithValue("$correlation", delivery.CorrelationId);
        insert.Parameters.AddWithValue("$queued", delivery.OccurredAt);
        insert.Parameters.AddWithValue("$trigger", (object?)triggerSource ?? DBNull.Value);
        insert.Parameters.AddWithValue("$firedAt", (object?)triggerFiredAt ?? DBNull.Value);

        return await insert.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>A `completed` or `failed` row that carries its run: not a batched run's extra row.</summary>
    public static bool IsRunTerminal(Message row) =>
        row.Type is MessageTypes.Completed or MessageTypes.Failed
        && !LedgerFacts.HasField(row.Payload, PayloadFields.UsageCountedOn);

    private static async Task<bool> WriteRunAsync(
        SqliteConnection connection, SqliteTransaction transaction, Message terminal, bool backfilled,
        CancellationToken ct)
    {
        var (teamId, member) = LedgerFacts.Split(terminal.Source);

        // THE DELIVERY THIS ROW CLOSED: when it was queued and the trigger fire it answered, as
        // recorded when it was accepted. Only a delivery accepted before `outcome-003` has no record,
        // and is resolved from the log as it was then - NULL when a Reset has since taken the rows.
        var attribution = terminal.CausationSeq is { } causeSeq
            ? await RecordedDeliveryAsync(connection, transaction, causeSeq, ct)
                ?? await LoggedDeliveryAsync(connection, transaction, causeSeq, ct)
            : null;

        var queuedAt = attribution?.QueuedAt;
        var triggerSource = attribution?.TriggerSource;
        var triggerFiredAt = attribution?.TriggerFiredAt;

        // THE RUN'S `started` row: this member's latest one before the terminal and after the
        // delivery, so another run's start can never be taken for this one's. None is NULL.
        var started = await StartedAsync(connection, transaction, terminal, ct);

        var outcome = await LedgerFacts.RunOutcomeAsync(
            terminal, () => BlockedAsync(connection, transaction, terminal, started?.Seq, ct));

        var agent = teamId is null ? null : await ScalarTextAsync(
            connection, transaction,
            "SELECT agent FROM team_members WHERE team = $team AND name = $name",
            ct, ("$team", teamId), ("$name", member));

        var usage = Usage.Of(terminal.Payload);

        // A member no longer on the roster still has a kind when it ran no model.
        var kind = LedgerFacts.MemberKind(agent, usage);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"""
             INSERT {(backfilled ? "OR IGNORE " : "")}INTO usage_ledger (
                 run_seq, correlation, team_id, team_name, member, member_kind, agent_preset,
                 run_outcome, queued_at, started_at, ended_at, measured,
                 tokens_in, tokens_cached_in, tokens_cache_creation, tokens_out, tokens_combined, billable,
                 trigger_source, trigger_fired_at, backfilled, start_seq, origin_kind, origin_actor_id)
             VALUES (
                 $seq, $correlation, $team, {TeamNameSql}, $member, $kind, $agent,
                 $outcome, $queued, $started, $ended, $measured,
                 $in, $cachedIn, $cacheCreation, $out, $combined, $billable,
                 $trigger, $firedAt, $backfilled, $startSeq, $originKind, $originActor)
             """;

        // THE WORKFLOW'S ORIGIN, as the terminal row carries it (its root's, messages-007): a live row
        // only, as a backfill reads rows from before origins were derived.
        var origin = backfilled ? null : await OriginOfAsync(connection, transaction, terminal.Seq, ct);
        insert.Parameters.AddWithValue("$originKind", (object?)origin?.Kind ?? DBNull.Value);
        insert.Parameters.AddWithValue("$originActor", (object?)origin?.ActorId ?? DBNull.Value);

        insert.Parameters.AddWithValue("$seq", terminal.Seq);
        insert.Parameters.AddWithValue("$correlation", terminal.CorrelationId);
        insert.Parameters.AddWithValue("$team", (object?)teamId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$member", member);
        insert.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        insert.Parameters.AddWithValue("$agent", (object?)agent ?? DBNull.Value);
        insert.Parameters.AddWithValue("$outcome", outcome);
        insert.Parameters.AddWithValue("$queued", (object?)queuedAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("$started", (object?)started?.OccurredAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("$ended", Instant.Stamp(terminal.OccurredAt));
        insert.Parameters.AddWithValue("$measured", usage.Measured ? 1 : 0);
        insert.Parameters.AddWithValue("$in", (object?)usage.TokensIn ?? DBNull.Value);
        insert.Parameters.AddWithValue("$cachedIn", (object?)usage.CachedIn ?? DBNull.Value);
        insert.Parameters.AddWithValue("$cacheCreation", (object?)usage.CacheCreation ?? DBNull.Value);
        insert.Parameters.AddWithValue("$out", (object?)usage.TokensOut ?? DBNull.Value);
        insert.Parameters.AddWithValue("$combined", (object?)usage.Combined ?? DBNull.Value);
        insert.Parameters.AddWithValue("$billable", (object?)usage.Billable ?? DBNull.Value);
        insert.Parameters.AddWithValue("$trigger", (object?)triggerSource ?? DBNull.Value);
        insert.Parameters.AddWithValue("$firedAt", (object?)triggerFiredAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("$backfilled", backfilled ? 1 : 0);
        insert.Parameters.AddWithValue("$startSeq", (object?)started?.Seq ?? DBNull.Value);

        return await insert.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// A RUN'S CONFIGURATION AT START (<c>execution_snapshots</c>, <c>outcome-008</c>): the member's
    /// roster row (its preset, its own rate and billing account, <c>auth-034</c>), the tenant's rate and
    /// currency, and the workflow's current outcome link, all read in this append's transaction,
    /// priced by <see cref="EffectiveCost"/>. One per
    /// `started` row; never updated, so nothing after start changes what it says.
    /// </summary>
    private static async Task<bool> WriteSnapshotAsync(
        SqliteConnection connection, SqliteTransaction transaction, Message started, CancellationToken ct)
    {
        var (teamId, member) = LedgerFacts.Split(started.Source);

        EffectiveCost.Member? row = null;

        if (teamId is not null)
        {
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText =
                "SELECT agent, est_hourly_rate, billing_account_id FROM team_members WHERE team = $team AND name = $name";
            lookup.Parameters.AddWithValue("$team", teamId);
            lookup.Parameters.AddWithValue("$name", member);

            await using var reader = await lookup.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                row = new EffectiveCost.Member(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        var cost = EffectiveCost.Of(
            row,
            await SettingAsync(connection, transaction, ExecutionSnapshotFacts.AgentHourlyRateSetting, ct),
            await SettingAsync(connection, transaction, ExecutionSnapshotFacts.CurrencySetting, ct));

        var link = await OutcomeLinks.CurrentAsync(connection, transaction, started.CorrelationId, ct);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"""
             INSERT INTO execution_snapshots (
                 start_seq, correlation, team_id, team_name, member, member_kind, agent_preset,
                 rate_amount, rate_currency, rate_source, billing_account_id, link_id, started_at,
                 origin_kind, origin_actor_id, origin_actor_email, origin_trigger)
             VALUES (
                 $seq, $correlation, $team, {TeamNameSql}, $member, $kind, $agent,
                 $amount, $currency, $source, $account, $link, $at,
                 $originKind, $originActor, $originEmail, $originTrigger)
             """;

        // THE WORKFLOW'S ORIGIN, as the started row carries it: its root's, derived at append.
        var origin = await OriginOfAsync(connection, transaction, started.Seq, ct);
        insert.Parameters.AddWithValue("$originKind", (object?)origin?.Kind ?? DBNull.Value);
        insert.Parameters.AddWithValue("$originActor", (object?)origin?.ActorId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$originEmail", (object?)origin?.ActorEmail ?? DBNull.Value);
        insert.Parameters.AddWithValue("$originTrigger", (object?)origin?.Trigger ?? DBNull.Value);

        insert.Parameters.AddWithValue("$seq", started.Seq);
        insert.Parameters.AddWithValue("$correlation", started.CorrelationId);
        insert.Parameters.AddWithValue("$team", (object?)teamId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$member", member);
        insert.Parameters.AddWithValue("$kind", (object?)cost.Kind ?? DBNull.Value);
        insert.Parameters.AddWithValue("$agent", (object?)cost.Preset ?? DBNull.Value);
        insert.Parameters.AddWithValue("$amount", (object?)cost.Amount ?? DBNull.Value);
        insert.Parameters.AddWithValue("$currency", (object?)cost.Currency ?? DBNull.Value);
        insert.Parameters.AddWithValue("$source", cost.Source);
        insert.Parameters.AddWithValue("$account", (object?)cost.BillingAccountId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$link", (object?)link?.Id ?? DBNull.Value);
        insert.Parameters.AddWithValue("$at", Instant.Stamp(started.OccurredAt));

        return await insert.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>A <c>tenant_settings</c> row's stored value, or null when there is no row.</summary>
    private static Task<string?> SettingAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken ct) =>
        ScalarTextAsync(connection, transaction, "SELECT value FROM tenant_settings WHERE name = $name", ct, ("$name", name));

    private static async Task<bool> WriteWorkflowAsync(
        SqliteConnection connection, SqliteTransaction transaction, Message close, bool backfilled,
        CancellationToken ct)
    {
        var rootAt = await ScalarTextAsync(
            connection, transaction, "SELECT occurred_at FROM messages WHERE seq = $root",
            ct, ("$root", close.CorrelationId));

        var teamId = MessageTeam.Of(close);

        // THE OUTCOME THE WORKFLOW SERVED WHEN IT CLOSED: its newest link's outcome, as linked. A
        // later merge is followed on read; nothing here is rewritten.
        var outcome = (await OutcomeLinks.CurrentAsync(connection, transaction, close.CorrelationId, ct))?.OutcomeId;

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"""
             INSERT {(backfilled ? "OR IGNORE " : "")}INTO workflow_ledger (
                 close_seq, correlation, team_id, team_name, root_at, closed_at, how_closed,
                 outcome_id_at_close, backfilled)
             VALUES ($seq, $correlation, $team, {TeamNameSql}, $rootAt, $closedAt, $how, $outcome, $backfilled)
             """;

        insert.Parameters.AddWithValue("$seq", close.Seq);
        insert.Parameters.AddWithValue("$correlation", close.CorrelationId);
        insert.Parameters.AddWithValue("$team", (object?)teamId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$rootAt", (object?)rootAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("$closedAt", Instant.Stamp(close.OccurredAt));
        insert.Parameters.AddWithValue("$how", LedgerFacts.HowClosed(close));
        insert.Parameters.AddWithValue("$outcome", (object?)outcome ?? DBNull.Value);
        insert.Parameters.AddWithValue("$backfilled", backfilled ? 1 : 0);

        return await insert.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// The team's name as it is now - a snapshot, as <c>backlog_dispatches</c> keeps one, so a
    /// deleted team's rows still say what it was called. Its id when it has no name of its own.
    /// </summary>
    private const string TeamNameSql = "COALESCE((SELECT t.name FROM teams t WHERE t.id = $team), $team)";

    private static async Task<bool> WriteNudgeAsync(
        SqliteConnection connection, SqliteTransaction transaction, Message nudge, CancellationToken ct)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT OR IGNORE INTO nudge_ledger (nudge_seq, correlation, nudged_at)
            VALUES ($seq, $correlation, $at)
            """;

        insert.Parameters.AddWithValue("$seq", nudge.Seq);
        insert.Parameters.AddWithValue("$correlation", nudge.CorrelationId);
        insert.Parameters.AddWithValue("$at", Instant.Stamp(nudge.OccurredAt));

        return await insert.ExecuteNonQueryAsync(ct) > 0;
    }

    private static async Task<Attribution?> RecordedDeliveryAsync(
        SqliteConnection connection, SqliteTransaction transaction, long deliverySeq, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT queued_at, trigger_source, trigger_fired_at FROM delivery_ledger WHERE delivery_seq = $seq";
        command.Parameters.AddWithValue("$seq", deliverySeq);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new Attribution(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>The fallback for a delivery accepted before <c>outcome-003</c>: the log, as it is now.</summary>
    private static async Task<Attribution?> LoggedDeliveryAsync(
        SqliteConnection connection, SqliteTransaction transaction, long deliverySeq, CancellationToken ct)
    {
        if (await RowAsync(connection, transaction, deliverySeq, ct) is not { } delivery) return null;

        var (triggerSource, triggerFiredAt) = await TriggerOfAsync(connection, transaction, delivery, ct);
        return new Attribution(delivery.OccurredAt, triggerSource, triggerFiredAt);
    }

    /// <summary>The trigger fire a delivery answers: <see cref="LedgerFacts.TriggerOfAsync"/> over this database.</summary>
    private static Task<(string? Source, string? FiredAt)> TriggerOfAsync(
        SqliteConnection connection, SqliteTransaction transaction, LinkedRow delivery, CancellationToken ct) =>
        LedgerFacts.TriggerOfAsync(delivery, seq => RowAsync(connection, transaction, seq, ct));

    private static async Task<LinkedRow?> RowAsync(
        SqliteConnection connection, SqliteTransaction transaction, long seq, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT seq, type, source, occurred_at, causation_seq, correlation_id FROM messages WHERE seq = $seq";
        command.Parameters.AddWithValue("$seq", seq);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new LinkedRow(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetInt64(5));
    }

    private static async Task<LinkedRow?> StartedAsync(
        SqliteConnection connection, SqliteTransaction transaction, Message terminal, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT seq, type, source, occurred_at, causation_seq FROM messages
            WHERE seq = (SELECT MAX(seq) FROM messages
                         WHERE source = $source COLLATE NOCASE AND type = $started
                           AND seq < $terminal AND seq > $after)
            """;
        command.Parameters.AddWithValue("$source", terminal.Source);
        command.Parameters.AddWithValue("$started", MessageTypes.Started);
        command.Parameters.AddWithValue("$terminal", terminal.Seq);
        command.Parameters.AddWithValue("$after", terminal.CausationSeq ?? 0);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new LinkedRow(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }

    /// <summary>Whether the run published a run-level `blocked` (one naming no <c>item</c>) between its
    /// start and this row - the rule <c>ReadRunsAsync</c> reads a run as Blocked by - or asked a
    /// person to decide (`needs-decision`), which leaves the member waiting on a person just as a
    /// block does: the team activity read shows both as blocked, after a Reset too.</summary>
    private static async Task<bool> BlockedAsync(
        SqliteConnection connection, SqliteTransaction transaction, Message terminal, long? startedSeq,
        CancellationToken ct)
    {
        if (startedSeq is not { } from) return false;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT EXISTS (SELECT 1 FROM messages
                            WHERE source = $source COLLATE NOCASE AND type IN ($blocked, $needsDecision)
                              AND seq > $from AND seq < $terminal
                              AND json_extract(payload, '$.{PayloadFields.Item}') IS NULL)
             """;
        command.Parameters.AddWithValue("$source", terminal.Source);
        command.Parameters.AddWithValue("$blocked", MessageTypes.Blocked);
        command.Parameters.AddWithValue("$needsDecision", MessageTypes.NeedsDecision);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$terminal", terminal.Seq);

        return (long)(await command.ExecuteScalarAsync(ct))! != 0;
    }

    /// <summary>The origin row <paramref name="seq"/> was stored with (messages-007), or null for a row
    /// from before it.</summary>
    private static async Task<MessageOrigin?> OriginOfAsync(
        SqliteConnection connection, SqliteTransaction transaction, long seq, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT origin_kind, origin_actor_id, origin_actor_email, origin_trigger, publisher_kind FROM messages WHERE seq = $seq";
        command.Parameters.AddWithValue("$seq", seq);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0)) return null;

        return new MessageOrigin(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? PublisherKinds.Host : reader.GetString(4));
    }

    private static async Task<string?> ScalarTextAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);

        return await command.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// A terminal row's figures, read by the rule the spend SQL on the log counted by: an excluded
    /// estimate is unmeasured; a run that ran no model is measured, has no tokens and costs 0; a
    /// combined total is billed as reported; a split is weighted as
    /// <see cref="InvocationUsage.BillableTokens"/> weights it; anything else is unmeasured. Never a
    /// zero for something that was not measured.
    /// </summary>
    public sealed record Usage(
        bool Measured, bool NoModel, long? TokensIn, long? CachedIn, long? CacheCreation, long? TokensOut,
        long? Combined, long? Billable)
    {
        private static readonly Usage Unmeasured = new(false, false, null, null, null, null, null, null);

        public static Usage Of(string payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Unmeasured;

                var source = root.TryGetProperty("tokensSource", out var named) && named.ValueKind == JsonValueKind.String
                    ? named.GetString() ?? ""
                    : "";

                if (source == UsageSource.NoModel) return new(true, true, null, null, null, null, null, 0);
                if (source == UsageSource.ExcludedEstimate) return Unmeasured;

                var tokensIn = Long(root, "tokensIn");
                var tokensOut = Long(root, "tokensOut");
                var total = Long(root, "tokensTotal");
                var cachedIn = Long(root, "tokensCachedIn");
                var cacheCreation = Long(root, "tokensCacheCreation");

                if (total is { } combined) return new(true, false, null, null, null, null, combined, combined);
                if (tokensIn is null || tokensOut is null) return Unmeasured;

                // THE SAME INTEGER WEIGHTS AS InvocationUsage.BillableTokens and the log's SQL.
                var billable = tokensIn.Value + tokensOut.Value + ((cachedIn ?? 0) / 10) + (((cacheCreation ?? 0) * 5) / 4);

                return new(true, false, tokensIn, cachedIn, cacheCreation, tokensOut, null, billable);
            }
            catch (JsonException)
            {
                return Unmeasured;
            }
        }

        private static long? Long(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
                ? number
                : null;
    }
}
