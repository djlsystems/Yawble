namespace Yawble.Core.Bus;

/// <summary>The kinds of entry in the one global run queue (0008).</summary>
public static class QueueKinds
{
    public const string Run = "run";
    public const string Interactive = "interactive";
}

/// <summary>The scheduler's tables: interactive entries in the run queue, sessions, and how a run ended.</summary>
public sealed partial class BusStore
{
    /// <summary>
    /// Queues an interactive session for a member. Fails (returns null) when the member already holds a queue entry
    /// of either kind: one sandbox per member at a time.
    /// </summary>
    public QueueRow? EnqueueSession(long memberId)
    {
        var id = Scalar<long?>(
            """
            INSERT OR IGNORE INTO run_queue (member_id, enqueued_at, kind) VALUES ($m, $t, 'interactive')
            RETURNING id;
            """, ("$m", memberId), ("$t", Now()));
        return id is { } queueId ? Queue().Single(q => q.Id == queueId) : null;
    }

    /// <summary>A restart drops every queued interactive session: the window that asked for it is gone.</summary>
    public int DropQueuedSessions() => ExecCount("DELETE FROM run_queue WHERE kind = 'interactive';");

    public void SetFailureClass(long runId, string? failureClass) =>
        Exec("UPDATE runs SET failure_class = $f WHERE id = $id;", ("$f", failureClass), ("$id", runId));

    public string? FailureClassOf(long runId) => Scalar<string?>("SELECT failure_class FROM runs WHERE id = $id;", ("$id", runId));

    public sealed record SessionRow(
        long Id, long MemberId, long QueueId, string State, string QueuedAt, string StartedAt, string? EndedAt, string? Reason);

    public long InsertSession(long memberId, long queueId, string queuedAt) =>
        Scalar<long>(
            """
            INSERT INTO sessions (member_id, queue_id, state, queued_at, started_at)
            VALUES ($m, $q, 'running', $qa, $sa) RETURNING id;
            """, ("$m", memberId), ("$q", queueId), ("$qa", queuedAt), ("$sa", Now()));

    public void EndSession(long sessionId, string state, string? reason) =>
        Exec("UPDATE sessions SET state = $st, ended_at = $t, reason = $r WHERE id = $id AND state = 'running';",
            ("$st", state), ("$t", Now()), ("$r", reason), ("$id", sessionId));

    public IReadOnlyList<SessionRow> Sessions(string? state = null)
    {
        using var cmd = Command(
            $"""
             SELECT id, member_id, queue_id, state, queued_at, started_at, ended_at, reason
             FROM sessions {(state is null ? "" : "WHERE state = $st")} ORDER BY id;
             """, ("$st", state));
        using var r = cmd.ExecuteReader();
        var list = new List<SessionRow>();
        while (r.Read())
        {
            list.Add(new SessionRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        }
        return list;
    }

    private int ExecCount(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteNonQuery();
    }
}
