using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// WHAT A LEDGER ROW SAYS, decided once for every provider: the parts of <see cref="LedgerRows"/>
/// and <see cref="SqliteUsageLedger"/> that are not SQL. Each provider's writer asks its own
/// database the questions (the delivery's record, the run's start, whether it blocked, the member's
/// agent) and hands the answers here, so the two cannot word the same run differently. No SQL lives
/// in this file.
/// </summary>
public static class LedgerFacts
{
    /// <summary>The <c>usage_ledger</c> columns, in the order <see cref="ReadRun"/> reads them.</summary>
    public const string RunColumns =
        """
        run_seq, correlation, team_id, team_name, member, member_kind, agent_preset, run_outcome,
        queued_at, started_at, ended_at, measured, tokens_in, tokens_cached_in, tokens_cache_creation,
        tokens_out, tokens_combined, billable, trigger_source, trigger_fired_at, backfilled
        """;

    /// <summary>The <c>workflow_ledger</c> columns, in the order <see cref="ReadWorkflow"/> reads them.</summary>
    public const string WorkflowColumns =
        """
        close_seq, correlation, team_id, team_name, root_at, closed_at, how_closed,
        outcome_id_at_close, backfilled
        """;

    /// <summary>A log row as the ledger's lookups read it.</summary>
    public sealed record LinkedRow(
        long Seq, string Type, string Source, string OccurredAt, long? CausationSeq, long CorrelationId = 0);

    /// <summary>What a delivery is charged to: when it was queued and the trigger fire it answered.</summary>
    public sealed record Attribution(string? QueuedAt, string? TriggerSource, string? TriggerFiredAt);

    /// <summary>A member's source split at its first slash: the team, and the member (the whole
    /// source when it names no team).</summary>
    public static (string? Team, string Member) Split(string source)
    {
        var slash = source.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < source.Length - 1
            ? (source[..slash], source[(slash + 1)..])
            : (null, source);
    }

    /// <summary>Whether <paramref name="payload"/> is an object with <paramref name="field"/> set to
    /// something other than JSON null.</summary>
    public static bool HasField(string payload, string field)
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

    /// <summary>Whether <paramref name="payload"/>'s <paramref name="field"/> is JSON <c>true</c>.</summary>
    public static bool Bool(string payload, string field)
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

    /// <summary>An addressed instruction from a schedule or a trigger: a fire.</summary>
    public static bool IsTriggerFire(LinkedRow row) =>
        row.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
        && (row.Source.StartsWith("schedule:", StringComparison.Ordinal)
            || row.Source.StartsWith("trigger:", StringComparison.Ordinal));

    /// <summary>
    /// The trigger fire a delivery answers, in the ledger's two hops: the delivery itself is the fire,
    /// or - for a Manager woken by another run - it is that run's `completed`, `failed` or `handback`
    /// row and the fire is that row's own delivery. <paramref name="rowAt"/> reads one log row by seq.
    /// </summary>
    public static async Task<(string? Source, string? FiredAt)> TriggerOfAsync(
        LinkedRow delivery, Func<long, Task<LinkedRow?>> rowAt)
    {
        if (IsTriggerFire(delivery)) return (delivery.Source, delivery.OccurredAt);

        if (delivery.Type is MessageTypes.Completed or MessageTypes.Failed or MessageTypes.Handback
            && delivery.CausationSeq is { } upstream
            && await rowAt(upstream) is { } fire
            && IsTriggerFire(fire))
        {
            return (fire.Source, fire.OccurredAt);
        }

        return (null, null);
    }

    /// <summary>
    /// How the run ended, as the ledger names it: failed, blocked (asked only of a run that did not
    /// fail - <paramref name="blocked"/> is not called otherwise), quiet, or completed.
    /// </summary>
    public static async Task<string> RunOutcomeAsync(Message terminal, Func<Task<bool>> blocked) =>
        terminal.Type == MessageTypes.Failed
            ? "failed"
            : await blocked()
                ? "blocked"
                : Bool(terminal.Payload, PayloadFields.Quiet) ? "quiet" : "completed";

    /// <summary>The member's kind from its roster agent; a member no longer on the roster still has a
    /// kind when it ran no model.</summary>
    public static string? MemberKind(string? agent, LedgerRows.Usage usage) =>
        agent is not null
            ? MemberRef.KindOf(agent)
            : usage.NoModel ? MemberRef.PluginKind : null;

    /// <summary>How a workflow's close row closed it.</summary>
    public static string HowClosed(Message close) =>
        close.Type == MessageTypes.WorkflowCompleted ? WorkflowLedgerRow.Completed : WorkflowLedgerRow.Closed;

    /// <summary>One row of <see cref="RunColumns"/>, by ordinal.</summary>
    public static UsageLedgerRow ReadRun(DbDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            Text(reader, 2),
            Text(reader, 3),
            reader.GetString(4),
            Text(reader, 5),
            Text(reader, 6),
            reader.GetString(7),
            At(reader, 8),
            At(reader, 9),
            MessageRows.ReadStamp(reader.GetString(10)),
            reader.GetInt64(11) != 0,
            Long(reader, 12),
            Long(reader, 13),
            Long(reader, 14),
            Long(reader, 15),
            Long(reader, 16),
            Long(reader, 17),
            Text(reader, 18),
            At(reader, 19),
            reader.GetInt64(20) != 0);

    /// <summary>One row of <see cref="WorkflowColumns"/>, by ordinal.</summary>
    public static WorkflowLedgerRow ReadWorkflow(DbDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            Text(reader, 2),
            Text(reader, 3),
            At(reader, 4),
            MessageRows.ReadStamp(reader.GetString(5)),
            reader.GetString(6),
            Text(reader, 7),
            reader.GetInt64(8) != 0);

    public static async Task<IReadOnlyList<UsageLedgerRow>> ReadRunsAsync(DbCommand command, CancellationToken ct)
    {
        var rows = new List<UsageLedgerRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct)) rows.Add(ReadRun(reader));

        return rows;
    }

    public static async Task<IReadOnlyList<WorkflowLedgerRow>> ReadWorkflowsAsync(DbCommand command, CancellationToken ct)
    {
        var rows = new List<WorkflowLedgerRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct)) rows.Add(ReadWorkflow(reader));

        return rows;
    }

    /// <summary>
    /// One page of the export from each table's newest <paramref name="take"/> below the cursor: the
    /// newest <paramref name="take"/> of the two together, by the one seq space they share. A page
    /// shorter than <paramref name="take"/> is the end.
    /// </summary>
    public static LedgerPage Page(IReadOnlyList<UsageLedgerRow> runs, IReadOnlyList<WorkflowLedgerRow> workflows, int take)
    {
        var page = runs.Select(r => r.RunSeq)
            .Concat(workflows.Select(w => w.CloseSeq))
            .OrderDescending()
            .Take(take)
            .ToList();

        if (page.Count == 0) return new LedgerPage([], [], null);

        var floor = page[^1];

        return new LedgerPage(
            [.. runs.Where(r => r.RunSeq >= floor)],
            [.. workflows.Where(w => w.CloseSeq >= floor)],
            page.Count == take ? floor : null);
    }

    /// <summary>
    /// The team activity read's rows from its one statement: the inside candidates (part 0) kept when
    /// they began before <paramref name="to"/> - <c>queued_at</c> is not stored in the "O" form
    /// <c>ended_at</c> is, so the two are compared here rather than as text - and the edges (each
    /// member's last run before and first after) kept only for a member named or seen inside.
    /// </summary>
    public static IReadOnlyList<UsageLedgerRow> TeamRuns(
        IEnumerable<(UsageLedgerRow Row, long Part)> read, DateTimeOffset to, IReadOnlyCollection<string> members)
    {
        var rows = new Dictionary<long, UsageLedgerRow>();
        var edges = new List<UsageLedgerRow>();

        foreach (var (row, part) in read)
        {
            if (part == 0)
            {
                if ((row.QueuedAt ?? row.StartedAt ?? row.EndedAt) < to) rows[row.RunSeq] = row;
            }
            else
            {
                edges.Add(row);
            }
        }

        var named = new HashSet<string>(members.Concat(rows.Values.Select(r => r.Member)), StringComparer.OrdinalIgnoreCase);
        foreach (var row in edges)
        {
            if (named.Contains(row.Member)) rows.TryAdd(row.RunSeq, row);
        }

        return [.. rows.Values.OrderBy(r => r.RunSeq)];
    }

    /// <summary>The <c>admission_holds</c> columns, in the order <see cref="ReadHold"/> reads them.</summary>
    public const string HoldColumns =
        "id, team_id, team_name, member, delivery_seq, held_at, released_at, reason_kind, reason, unfinished";

    public static AdmissionHoldRow ReadHold(DbDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            Text(reader, 2),
            reader.GetString(3),
            Long(reader, 4),
            MessageRows.ReadStamp(reader.GetString(5)),
            At(reader, 6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt64(9) != 0);

    /// <summary>
    /// THE RETENTION PASS'S WHOLE WORKFLOWS, from rows read in (correlation, seq) order: each
    /// workflow is taken with every row it has, and only while the total stays within the batch, oldest
    /// workflow first. <see cref="Add"/> answers false once the batch is full, so the reader can stop.
    /// </summary>
    public sealed class WholeWorkflows(int max)
    {
        private readonly List<long> _chosen = [];
        private readonly List<long> _workflow = [];
        private long? _current;

        public bool Add(long correlation, long seq)
        {
            if (correlation != _current)
            {
                Close();
                if (_chosen.Count >= max) return false;
                _current = correlation;
            }

            _workflow.Add(seq);
            return true;
        }

        public IReadOnlyList<long> Finish()
        {
            Close();
            return _chosen;
        }

        private void Close()
        {
            if (_workflow.Count > 0 && _chosen.Count + _workflow.Count <= max) _chosen.AddRange(_workflow);
            _workflow.Clear();
        }
    }

    private static string? Text(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? Long(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? At(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : MessageRows.ReadStamp(reader.GetString(ordinal));
}
