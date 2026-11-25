using System.Globalization;
using System.Text.Json;

namespace Yawble.Core.Bus;

/// <summary>
/// One workflow's head, read for every workflow at once (the Workflows page): its id, first and last time, message
/// count, newest seq, whether it has ended (as <see cref="BusStore.WorkflowEnded"/> reads it), its root message and the
/// source of its first message from a member (which team it is, when the root names none).
/// </summary>
public sealed record WorkflowHead(long Id, DateTimeOffset First, DateTimeOffset Last, int Count, long MaxSeq, bool Ended,
    Message? Root, string? FirstMemberSource, string? EndType = null, DateTimeOffset? EndAt = null);

/// <summary>The Workflows page's reads, each one statement however many workflows there are.</summary>
public sealed partial class BusStore
{
    /// <summary>Every workflow's head, newest activity first. One statement.</summary>
    public IReadOnlyList<WorkflowHead> WorkflowHeads()
    {
        using var cmd = Command(
            """
            WITH g AS (
                SELECT correlation_id AS id, min(occurred_at) AS first_at, max(occurred_at) AS last_at, count(*) AS n, max(seq) AS max_seq,
                       max(CASE WHEN type IN ($done, $closed, $expired) THEN seq END) AS last_end,
                       max(CASE WHEN type LIKE 'agent.instruction.%' THEN seq END) AS last_instr,
                       min(CASE WHEN instr(source, '/') > 0 THEN seq END) AS first_member
                FROM messages GROUP BY correlation_id)
            SELECT g.id, g.first_at, g.last_at, g.n, g.max_seq, g.last_end, g.last_instr,
                   r.seq, r.type, r.payload, r.source, r.causation_seq, r.depth, r.occurred_at, f.source, e.type, e.occurred_at
            FROM g LEFT JOIN messages r ON r.seq = g.id LEFT JOIN messages f ON f.seq = g.first_member LEFT JOIN messages e ON e.seq = g.last_end
            ORDER BY g.max_seq DESC;
            """,
            ("$done", MessageTypes.WorkflowCompleted), ("$closed", MessageTypes.WorkflowClosed), ("$expired", MessageTypes.WorkflowExpired));
        using var r = cmd.ExecuteReader();
        var list = new List<WorkflowHead>();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            long? lastEnd = r.IsDBNull(5) ? null : r.GetInt64(5), lastInstr = r.IsDBNull(6) ? null : r.GetInt64(6);
            // Ended: an end with no instruction after it, i.e. the newest end is newer than the newest instruction.
            var ended = lastEnd is { } e && (lastInstr is not { } i || i < e);
            var root = r.IsDBNull(7) ? null : new Message(r.GetInt64(7), r.GetString(8), r.GetString(9), r.GetString(10), id,
                r.IsDBNull(11) ? null : r.GetInt64(11), r.GetInt32(12), ParseTime(r.GetString(13)));
            list.Add(new WorkflowHead(id, ParseTime(r.GetString(1)), ParseTime(r.GetString(2)), r.GetInt32(3), r.GetInt64(4), ended,
                root, r.IsDBNull(14) ? null : r.GetString(14),
                ended ? r.GetString(15) : null, ended ? ParseTime(r.GetString(16)) : null));
        }
        return list;
    }

    /// <summary>The messages of these workflows, oldest first, by workflow. One statement.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<Message>> Threads(IReadOnlyCollection<long> workflows)
    {
        var map = new Dictionary<long, IReadOnlyList<Message>>();
        if (workflows.Count == 0) return map;
        foreach (var g in Messages("WHERE correlation_id IN (SELECT value FROM json_each($ids))", ("$ids", Ids(workflows))).GroupBy(m => m.CorrelationId))
            map[g.Key] = g.ToList();
        return map;
    }

    /// <summary>The wakes not fired yet, by workflow. One statement.</summary>
    public IReadOnlyDictionary<long, int> OpenWakesOf(IReadOnlyCollection<long> workflows)
    {
        var map = new Dictionary<long, int>();
        if (workflows.Count == 0) return map;
        using var cmd = Command(
            """
            SELECT m.correlation_id, count(*) FROM wakes w JOIN messages m ON m.seq = w.causation_seq
            WHERE w.fired_seq IS NULL AND m.correlation_id IN (SELECT value FROM json_each($ids)) GROUP BY m.correlation_id;
            """, ("$ids", Ids(workflows)));
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = r.GetInt32(1);
        return map;
    }

    /// <summary>The expiry rows (Extend, warning) of these workflows. One statement.</summary>
    public IReadOnlyDictionary<long, WorkflowExpiryRow> WorkflowExpiries(IReadOnlyCollection<long> workflows)
    {
        var map = new Dictionary<long, WorkflowExpiryRow>();
        if (workflows.Count == 0) return map;
        using var cmd = Command("SELECT workflow, extended_at, warned_for, warned_at, warned_question FROM workflow_expiry WHERE workflow IN (SELECT value FROM json_each($ids));",
            ("$ids", Ids(workflows)));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            DateTimeOffset? T(int i) => r.IsDBNull(i) ? null : ParseTime(r.GetString(i));
            map[r.GetInt64(0)] = new WorkflowExpiryRow(r.GetInt64(0), T(1), T(2), T(3), r.IsDBNull(4) ? null : r.GetInt64(4));
        }
        return map;
    }

    private static string Ids(IEnumerable<long> ids) => JsonSerializer.Serialize(ids.Distinct());

    private static DateTimeOffset ParseTime(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

