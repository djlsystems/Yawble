using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Messaging;

/// <summary>
/// THE ONE WRITER OF <c>usage_ledger</c> AND <c>workflow_ledger</c> (see <see cref="OutcomeSchema"/>).
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

        if (stored.Type is MessageTypes.WorkflowCompleted or MessageTypes.WorkflowClosed)
        {
            return await WriteWorkflowAsync(connection, transaction, stored, backfilled, ct);
        }

        return false;
    }

    /// <summary>A `completed` or `failed` row that carries its run: not a batched run's extra row.</summary>
    public static bool IsRunTerminal(Message row) =>
        row.Type is MessageTypes.Completed or MessageTypes.Failed
        && !HasField(row.Payload, PayloadFields.UsageCountedOn);

    private static async Task<bool> WriteRunAsync(
        SqliteConnection connection, SqliteTransaction transaction, Message terminal, bool backfilled,
        CancellationToken ct)
    {
        var (teamId, member) = Split(terminal.Source);

        // THE DELIVERY THIS ROW CLOSED, and - for a Manager woken by another run - the row that woke
        // it and that row's own delivery: how a trigger's fire is traced to the runs it paid for.
        var cause = terminal.CausationSeq is { } causeSeq ? await RowAsync(connection, transaction, causeSeq, ct) : null;
        var causeOfCause = cause?.CausationSeq is { } upstream ? await RowAsync(connection, transaction, upstream, ct) : null;

        string? triggerSource = null;
        string? triggerFiredAt = null;

        if (cause is not null && IsTriggerFire(cause))
        {
            triggerSource = cause.Source;
            triggerFiredAt = cause.OccurredAt;
        }
        else if (cause is not null
            && cause.Type is MessageTypes.Completed or MessageTypes.Failed or MessageTypes.Handback
            && causeOfCause is not null && IsTriggerFire(causeOfCause))
        {
            triggerSource = causeOfCause.Source;
            triggerFiredAt = causeOfCause.OccurredAt;
        }

        // THE RUN'S `started` row: this member's latest one before the terminal and after the
        // delivery, so another run's start can never be taken for this one's. None is NULL.
        var started = await StartedAsync(connection, transaction, terminal, ct);

        var outcome = terminal.Type == MessageTypes.Failed
            ? "failed"
            : await BlockedAsync(connection, transaction, terminal, started?.Seq, ct)
                ? "blocked"
                : Bool(terminal.Payload, PayloadFields.Quiet) ? "quiet" : "completed";

        var agent = teamId is null ? null : await ScalarTextAsync(
            connection, transaction,
            "SELECT agent FROM team_members WHERE team = $team AND name = $name",
            ct, ("$team", teamId), ("$name", member));

        var usage = Usage.Of(terminal.Payload);

        // A member no longer on the roster still has a kind when it ran no model.
        var kind = agent is not null
            ? MemberRef.KindOf(agent)
            : usage.NoModel ? MemberRef.PluginKind : null;

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"""
             INSERT {(backfilled ? "OR IGNORE " : "")}INTO usage_ledger (
                 run_seq, correlation, team_id, team_name, member, member_kind, agent_preset,
                 run_outcome, queued_at, started_at, ended_at, measured,
                 tokens_in, tokens_cached_in, tokens_cache_creation, tokens_out, tokens_combined, billable,
                 trigger_source, trigger_fired_at, backfilled)
             VALUES (
                 $seq, $correlation, $team, {TeamNameSql}, $member, $kind, $agent,
                 $outcome, $queued, $started, $ended, $measured,
                 $in, $cachedIn, $cacheCreation, $out, $combined, $billable,
                 $trigger, $firedAt, $backfilled)
             """;

        insert.Parameters.AddWithValue("$seq", terminal.Seq);
        insert.Parameters.AddWithValue("$correlation", terminal.CorrelationId);
        insert.Parameters.AddWithValue("$team", (object?)teamId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$member", member);
        insert.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        insert.Parameters.AddWithValue("$agent", (object?)agent ?? DBNull.Value);
        insert.Parameters.AddWithValue("$outcome", outcome);
        insert.Parameters.AddWithValue("$queued", (object?)cause?.OccurredAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("$started", (object?)started?.OccurredAt ?? DBNull.Value);
        insert.Parameters.AddWithValue("$ended", Stamp(terminal.OccurredAt));
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

        return await insert.ExecuteNonQueryAsync(ct) > 0;
    }

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
        insert.Parameters.AddWithValue("$closedAt", Stamp(close.OccurredAt));
        insert.Parameters.AddWithValue(
            "$how", close.Type == MessageTypes.WorkflowCompleted ? WorkflowLedgerRow.Completed : WorkflowLedgerRow.Closed);
        insert.Parameters.AddWithValue("$outcome", (object?)outcome ?? DBNull.Value);
        insert.Parameters.AddWithValue("$backfilled", backfilled ? 1 : 0);

        return await insert.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// The team's name as it is now - a snapshot, as <c>backlog_dispatches</c> keeps one, so a
    /// deleted team's rows still say what it was called. Its id when it has no name of its own.
    /// </summary>
    private const string TeamNameSql = "COALESCE((SELECT t.name FROM teams t WHERE t.id = $team), $team)";

    private static bool IsTriggerFire(LinkedRow row) =>
        row.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
        && (row.Source.StartsWith("schedule:", StringComparison.Ordinal)
            || row.Source.StartsWith("trigger:", StringComparison.Ordinal));

    private sealed record LinkedRow(long Seq, string Type, string Source, string OccurredAt, long? CausationSeq);

    private static async Task<LinkedRow?> RowAsync(
        SqliteConnection connection, SqliteTransaction transaction, long seq, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT seq, type, source, occurred_at, causation_seq FROM messages WHERE seq = $seq";
        command.Parameters.AddWithValue("$seq", seq);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new LinkedRow(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4));
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
    /// start and this row - the rule <c>ReadRunsAsync</c> reads a run as Blocked by.</summary>
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
                            WHERE source = $source COLLATE NOCASE AND type = $blocked
                              AND seq > $from AND seq < $terminal
                              AND json_extract(payload, '$.{PayloadFields.Item}') IS NULL)
             """;
        command.Parameters.AddWithValue("$source", terminal.Source);
        command.Parameters.AddWithValue("$blocked", MessageTypes.Blocked);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$terminal", terminal.Seq);

        return (long)(await command.ExecuteScalarAsync(ct))! != 0;
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

    private static (string? Team, string Member) Split(string source)
    {
        var slash = source.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < source.Length - 1
            ? (source[..slash], source[(slash + 1)..])
            : (null, source);
    }

    private static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static bool HasField(string payload, string field)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind != JsonValueKind.Null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Bool(string payload, string field)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
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
