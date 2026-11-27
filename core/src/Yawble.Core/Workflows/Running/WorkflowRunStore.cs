using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Workflows.Running;

/// <summary>A workflow member as the members row keeps it (0056): its workflow, pinned version (null = latest), settings, bindings, timeout.</summary>
public sealed record WorkflowMemberRow(long MemberId, long TeamId, string Team, string Name, long WorkflowId, int? Version, JsonObject Settings,
    JsonObject Bindings, int TimeoutMinutes, int MaxTells = Language.WorkflowTell.DefaultMaxTells)
{
    public string Qualified => $"{Team}/{Name}";
}

public sealed record WorkflowMemberState(long MemberId, JsonObject Variables, int ConsecutiveFailures, long? FailureDecisionSeq, string? UpdatedAt);

/// <summary>One workflow run (workflow_runs). Trigger: trigger:&lt;id&gt;, tell or test.</summary>
public sealed record WorkflowRunRow(long Id, long TeamId, string Team, long? MemberId, string? Member, long WorkflowId, string Workflow, int Version,
    string Trigger, string Status, bool DryRun, bool Test, JsonNode? Input, JsonObject? Settings, JsonObject? Bindings, JsonObject? Outputs,
    string? FailedStepId, string? Reason, string? Checkpoint, string StartedAt, string? EndedAt,
    string? WaitKind = null, long? WaitDeliverableId = null, string? WaitUntil = null);

/// <summary>One journaled step of a run (input and output truncated and scrubbed).</summary>
public sealed record WorkflowStepRow(int Seq, string? StepId, string? Type, JsonNode? Input, JsonNode? Output, string Status, bool Truncated,
    string StartedAt, string? EndedAt);

public sealed record WorkflowLogLine(string At, string Line);

public static class WorkflowRunStatus
{
    public const string Running = "running";

    /// <summary>Parked on a wait (a tell that waits): no run slot held, resumed when the wait ends; it still counts as the member's run.</summary>
    public const string Waiting = "waiting";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Interrupted = "interrupted";
}

/// <summary>
/// The storage of workflow members and their runs (0056): the members row's workflow columns, workflow_member_state (saved
/// variables, consecutive failures, the one failure decision), workflow_runs and workflow_run_steps (the journal), and a
/// run_records row per run (kind 'workflow'). Step input and output are scrubbed (<see cref="Connections.SecretScrubber"/>)
/// and truncated to <see cref="MaxField"/> characters.
/// </summary>
public sealed class WorkflowRunStore(string connectionString, TimeProvider? clock = null)
{
    public const int MaxField = 16_000;
    public const int DefaultTimeoutMinutes = 10;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // ------------------------------------------------------------------ workflow members

    public bool IsWorkflowMember(long memberId)
    {
        using var c = Open();
        return Scalar(c, "SELECT kind FROM members WHERE id = $id;", ("$id", memberId)) as string == "workflow";
    }

    public WorkflowMemberRow? Member(long memberId)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT m.id, t.id, t.name, m.name, m.workflow_id, m.workflow_version, m.settings, m.bindings, m.workflow_timeout_min, m.workflow_max_tells
            FROM members m JOIN teams t ON t.id = m.team_id WHERE m.id = $id AND m.kind = 'workflow' AND m.workflow_id IS NOT NULL;
            """, ("$id", memberId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new WorkflowMemberRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
            r.IsDBNull(5) ? null : r.GetInt32(5), ObjectOf(r, 6), ObjectOf(r, 7), r.IsDBNull(8) ? DefaultTimeoutMinutes : r.GetInt32(8),
            r.IsDBNull(9) ? Language.WorkflowTell.DefaultMaxTells : r.GetInt32(9));
    }

    /// <summary>
    /// Makes a member a workflow member, or changes one (the workflow, its version: null = latest, settings, bindings, timeout, and the
    /// most tells one run may make: null = 5).
    /// </summary>
    public void SetWorkflowMember(long memberId, long workflowId, int? version, JsonObject settings, JsonObject bindings, int? timeoutMinutes, int? maxTells = null)
    {
        using var c = Open();
        Exec(c, """
            UPDATE members SET kind = 'workflow', workflow_id = $s, workflow_version = $v, settings = $set, bindings = $b, workflow_timeout_min = $to,
                               workflow_max_tells = $mt
            WHERE id = $id;
            """, ("$s", workflowId), ("$v", version), ("$set", settings.ToJsonString()), ("$b", bindings.ToJsonString()),
            ("$to", timeoutMinutes), ("$mt", maxTells), ("$id", memberId));
        Exec(c, "INSERT OR IGNORE INTO workflow_member_state (member_id, updated_at) VALUES ($id, $t);", ("$id", memberId), ("$t", Now()));
    }

    /// <summary>
    /// When the member's next scheduled trigger fires (the soonest next_due_at of its enabled every, cron and once
    /// triggers, as stored: UTC ISO); null when it has none (folder and event triggers are not a schedule).
    /// </summary>
    public string? NextTriggerAt(long memberId)
    {
        using var c = Open();
        return Scalar(c, """
            SELECT MIN(next_due_at) FROM triggers
            WHERE member_id = $id AND enabled = 1 AND next_due_at IS NOT NULL AND kind IN ('every', 'cron', 'once');
            """, ("$id", memberId)) as string;
    }

    public string? TeamName(long teamId)
    {
        using var c = Open();
        return Scalar(c, "SELECT name FROM teams WHERE id = $id;", ("$id", teamId)) as string;
    }

    // ------------------------------------------------------------------ saved state

    public WorkflowMemberState State(long memberId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT variables, consecutive_failures, failure_decision_seq, updated_at FROM workflow_member_state WHERE member_id = $id;", ("$id", memberId));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new WorkflowMemberState(memberId, ObjectOf(r, 0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetString(3))
            : new WorkflowMemberState(memberId, [], 0, null, null);
    }

    /// <summary>The saved variables a run that succeeded leaves (written only then).</summary>
    public void SetVariables(long memberId, JsonObject variables)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO workflow_member_state (member_id, variables, updated_at) VALUES ($id, $v, $t)
            ON CONFLICT(member_id) DO UPDATE SET variables = excluded.variables, updated_at = excluded.updated_at;
            """, ("$id", memberId), ("$v", variables.ToJsonString()), ("$t", Now()));
    }

    /// <summary>Forgets one saved variable (the next run starts it from its initial value). False when it was not saved.</summary>
    public bool ResetVariable(long memberId, string name)
    {
        var state = State(memberId);
        if (!state.Variables.ContainsKey(name)) return false;
        var vars = (JsonObject)state.Variables.DeepClone();
        vars.Remove(name);
        SetVariables(memberId, vars);
        return true;
    }

    /// <summary>A member run ended: a success clears the failure count and the decision; a failure adds one. The new count.</summary>
    public int RecordOutcome(long memberId, bool succeeded)
    {
        using var c = Open();
        Exec(c, "INSERT OR IGNORE INTO workflow_member_state (member_id, updated_at) VALUES ($id, $t);", ("$id", memberId), ("$t", Now()));
        Exec(c, succeeded
                ? "UPDATE workflow_member_state SET consecutive_failures = 0, failure_decision_seq = NULL, updated_at = $t WHERE member_id = $id;"
                : "UPDATE workflow_member_state SET consecutive_failures = consecutive_failures + 1, updated_at = $t WHERE member_id = $id;",
            ("$id", memberId), ("$t", Now()));
        return (int)(long)Scalar(c, "SELECT consecutive_failures FROM workflow_member_state WHERE member_id = $id;", ("$id", memberId))!;
    }

    public void SetFailureDecision(long memberId, long? seq)
    {
        using var c = Open();
        Exec(c, "UPDATE workflow_member_state SET failure_decision_seq = $s, updated_at = $t WHERE member_id = $id;", ("$s", seq), ("$t", Now()), ("$id", memberId));
    }

    /// <summary>The person answered the failure decision: the count starts again (a new run of three raises a new one).</summary>
    public void ClearFailures(long memberId)
    {
        using var c = Open();
        Exec(c, "UPDATE workflow_member_state SET consecutive_failures = 0, failure_decision_seq = NULL, updated_at = $t WHERE member_id = $id;",
            ("$t", Now()), ("$id", memberId));
    }

    // ------------------------------------------------------------------ runs

    public long StartRun(long teamId, long? memberId, long workflowId, int version, string trigger, JsonNode? input, JsonObject settings,
        JsonObject bindings, bool dryRun, bool test, string status = WorkflowRunStatus.Running, string? reason = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var now = Now();
        var ended = status == WorkflowRunStatus.Running ? null : now;
        var id = (long)Scalar(c, """
            INSERT INTO workflow_runs (team_id, member_id, workflow_id, workflow_version, trigger, input, settings, bindings, status, dry_run, test, reason, started_at, ended_at)
            VALUES ($team, $m, $s, $v, $tr, $in, $set, $b, $st, $dry, $test, $r, $t, $e) RETURNING id;
            """, tx, ("$team", teamId), ("$m", memberId), ("$s", workflowId), ("$v", version), ("$tr", trigger), ("$in", Field(input).Text),
            ("$set", settings.ToJsonString()), ("$b", bindings.ToJsonString()), ("$st", status), ("$dry", dryRun ? 1 : 0), ("$test", test ? 1 : 0),
            ("$r", reason), ("$t", now), ("$e", ended))!;
        // run_records: every workflow run, beside the agents' runs (kind 'workflow').
        var team = Scalar(c, "SELECT name FROM teams WHERE id = $id;", tx, ("$id", teamId)) as string ?? "";
        var member = memberId is { } mid ? Scalar(c, "SELECT name FROM members WHERE id = $id;", tx, ("$id", mid)) as string ?? "" : "(test run)";
        Exec(c, tx, """
            INSERT INTO run_records (run_key, team, member, cli, kind, backend, started_at, end_class, reason, ended_at, duration_ms)
            VALUES ($k, $t, $m, 'workflow', 'workflow', 'workflow', $at, $ec, $r, $e, $d);
            """, ("$k", RunKey(id)), ("$t", team), ("$m", member), ("$at", now), ("$ec", ended is null ? null : status), ("$r", reason),
            ("$e", ended), ("$d", ended is null ? null : 0L));
        tx.Commit();
        return id;
    }

    public static string RunKey(long runId) => $"workflow:{runId}";

    public void Finish(long runId, string status, JsonObject? outputs, string? failedStepId, string? reason)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var now = Now();
        Exec(c, tx, """
            UPDATE workflow_runs SET status = $s, outputs = $o, failed_step_id = $f, reason = $r, ended_at = $t,
                                   wait_kind = NULL, wait_deliverable_id = NULL, wait_until = NULL
            WHERE id = $id;
            """,
            ("$s", status), ("$o", outputs is null ? null : Field(outputs).Text), ("$f", failedStepId), ("$r", Scrub(reason)), ("$t", now), ("$id", runId));
        var started = Scalar(c, "SELECT started_at FROM workflow_runs WHERE id = $id;", tx, ("$id", runId)) as string;
        long? duration = started is not null && DateTimeOffset.TryParse(started, out var s) ? (long)(_clock.GetUtcNow() - s).TotalMilliseconds : null;
        Exec(c, tx, "UPDATE run_records SET end_class = $e, reason = $r, ended_at = $t, duration_ms = $d WHERE run_key = $k;",
            ("$e", status == WorkflowRunStatus.Succeeded ? "completed" : status), ("$r", Scrub(reason)), ("$t", now), ("$d", duration), ("$k", RunKey(runId)));
        tx.Commit();
    }

    public void SetCheckpoint(long runId, string checkpointJson)
    {
        using var c = Open();
        Exec(c, "UPDATE workflow_runs SET checkpoint = $c WHERE id = $id;", ("$c", checkpointJson), ("$id", runId));
    }

    /// <summary>The run parks on a wait: status waiting, with what it waits on (its checkpoint, written before, holds the rest).</summary>
    public void Park(long runId, string kind, long? deliverable, DateTimeOffset? until)
    {
        using var c = Open();
        Exec(c, "UPDATE workflow_runs SET status = 'waiting', wait_kind = $k, wait_deliverable_id = $w, wait_until = $u WHERE id = $id AND status = 'running';",
            ("$k", kind), ("$w", deliverable), ("$u", until?.UtcDateTime.ToString("O")), ("$id", runId));
    }

    /// <summary>A waiting run's wait ended: it is running again (false when it was not waiting: another sweep took it, or it was cancelled).</summary>
    public bool Unpark(long runId)
    {
        using var c = Open();
        return Exec(c, "UPDATE workflow_runs SET status = 'running', wait_kind = NULL, wait_deliverable_id = NULL, wait_until = NULL WHERE id = $id AND status = 'waiting';",
            ("$id", runId)) == 1;
    }

    /// <summary>Runs parked on a wait, oldest first.</summary>
    public IReadOnlyList<WorkflowRunRow> Waiting() => Runs("WHERE r.status = 'waiting'", 10_000).OrderBy(r => r.Id).ToList();

    /// <summary>The member's waiting run, if it has one (it counts as the member's run: triggers that fire are skipped).</summary>
    public WorkflowRunRow? WaitingRunOf(long memberId) => Runs("WHERE r.status = 'waiting' AND r.member_id = $m", 1, ("$m", memberId)).FirstOrDefault();

    /// <summary>Runs still marked running: after a restart, the ones the old core was in the middle of.</summary>
    public IReadOnlyList<WorkflowRunRow> Unfinished() => Runs("WHERE r.status = 'running'", 10_000);

    public WorkflowRunRow? Run(long id) => Runs("WHERE r.id = $id", 1, ("$id", id)).SingleOrDefault();

    public IReadOnlyList<WorkflowRunRow> RecentRuns(long? teamId, long? memberId, int limit)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (teamId is { } t) { where.Add("r.team_id = $t"); args.Add(("$t", t)); }
        if (memberId is { } m) { where.Add("r.member_id = $m"); args.Add(("$m", m)); }
        return Runs(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where), Math.Clamp(limit, 1, 500), [.. args]);
    }

    private IReadOnlyList<WorkflowRunRow> Runs(string where, int limit, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"""
            SELECT r.id, r.team_id, t.name, r.member_id, m.name, r.workflow_id, s.workflow_key, r.workflow_version, r.trigger, r.status, r.dry_run, r.test,
                   r.input, r.settings, r.bindings, r.outputs, r.failed_step_id, r.reason, r.checkpoint, r.started_at, r.ended_at,
                   r.wait_kind, r.wait_deliverable_id, r.wait_until
            FROM workflow_runs r JOIN teams t ON t.id = r.team_id JOIN workflows s ON s.id = r.workflow_id LEFT JOIN members m ON m.id = r.member_id
            {where} ORDER BY r.id DESC LIMIT {limit};
            """, args);
        using var r = cmd.ExecuteReader();
        var list = new List<WorkflowRunRow>();
        while (r.Read())
        {
            list.Add(new WorkflowRunRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetInt64(5), r.GetString(6), r.GetInt32(7), r.GetString(8), r.GetString(9), r.GetInt64(10) != 0, r.GetInt64(11) != 0,
                NodeOf(r, 12), NodeOf(r, 13) as JsonObject, NodeOf(r, 14) as JsonObject, NodeOf(r, 15) as JsonObject,
                r.IsDBNull(16) ? null : r.GetString(16), r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetString(18),
                r.GetString(19), r.IsDBNull(20) ? null : r.GetString(20),
                r.IsDBNull(21) ? null : r.GetString(21), r.IsDBNull(22) ? null : r.GetInt64(22), r.IsDBNull(23) ? null : r.GetString(23)));
        }
        return list;
    }

    // ------------------------------------------------------------------ the journal

    public long StepStarted(long runId, string stepId, string type, JsonNode? input)
    {
        var f = Field(input);
        using var c = Open();
        return (long)Scalar(c, """
            INSERT INTO workflow_run_steps (run_id, kind, step_id, type, input, status, truncated, started_at)
            VALUES ($r, 'step', $s, $ty, $in, 'running', $tr, $t) RETURNING id;
            """, null, ("$r", runId), ("$s", stepId), ("$ty", type), ("$in", f.Text), ("$tr", f.Truncated ? 1 : 0), ("$t", Now()))!;
    }

    /// <summary>A step completed: its open row (StepStarted) is closed, or a row is added when there was none.</summary>
    public void StepCompleted(long runId, string stepId, string type, JsonNode? input, JsonNode? output, string status, DateTimeOffset started, DateTimeOffset ended)
    {
        var fi = Field(input);
        var fo = Field(output);
        using var c = Open();
        var open = Scalar(c, "SELECT id FROM workflow_run_steps WHERE run_id = $r AND kind = 'step' AND step_id = $s AND status IN ('running', 'waiting') ORDER BY id DESC LIMIT 1;",
            null, ("$r", runId), ("$s", stepId)) as long?;
        if (open is { } id)
            Exec(c, "UPDATE workflow_run_steps SET input = $in, output = $out, status = $st, truncated = $tr, started_at = $sa, ended_at = $e WHERE id = $id;",
                ("$in", fi.Text), ("$out", fo.Text), ("$st", status), ("$tr", fi.Truncated || fo.Truncated ? 1 : 0), ("$sa", started.ToString("O")),
                ("$e", ended.ToString("O")), ("$id", id));
        else
            Exec(c, """
                INSERT INTO workflow_run_steps (run_id, kind, step_id, type, input, output, status, truncated, started_at, ended_at)
                VALUES ($r, 'step', $s, $ty, $in, $out, $st, $tr, $sa, $e);
                """, ("$r", runId), ("$s", stepId), ("$ty", type), ("$in", fi.Text), ("$out", fo.Text), ("$st", status),
                ("$tr", fi.Truncated || fo.Truncated ? 1 : 0), ("$sa", started.ToString("O")), ("$e", ended.ToString("O")));
    }

    /// <summary>A run ended: steps it left open are closed with the run's end.</summary>
    public void CloseOpenSteps(long runId, string status)
    {
        using var c = Open();
        Exec(c, "UPDATE workflow_run_steps SET status = $s, ended_at = $t WHERE run_id = $r AND status IN ('running', 'waiting');", ("$s", status), ("$t", Now()), ("$r", runId));
    }

    // ------------------------------------------------------------------ the app's workflows settings (app_defaults)

    public const string TellOtherTeamsKey = "workflows.tellOtherTeams";

    /// <summary>Settings > Workflows: whether a workflow's tell may go to another team (on unless the person turned it off).</summary>
    public bool TellOtherTeams()
    {
        using var c = Open();
        return Scalar(c, "SELECT value FROM app_defaults WHERE key = $k;", ("$k", TellOtherTeamsKey)) is not string v
               || !string.Equals(v.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    }

    public void SetTellOtherTeams(bool on)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO app_defaults (key, value, updated_at) VALUES ($k, $v, $t)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """, ("$k", TellOtherTeamsKey), ("$v", on ? "true" : "false"), ("$t", Now()));
    }

    // ------------------------------------------------------------------ folder fires queued while a run is going (app_defaults)

    /// <summary>A member's queued folder fires are kept under this key and the member's id (no table of their own).</summary>
    public const string FolderQueueKeyPrefix = "workflows.folderQueue.";

    private static string FolderQueueKey(long memberId) => FolderQueueKeyPrefix + memberId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The member's queued folder fires as JSON (<see cref="WorkflowFolderQueue"/>); null when none are queued.</summary>
    public string? FolderQueue(long memberId)
    {
        using var c = Open();
        return Scalar(c, "SELECT value FROM app_defaults WHERE key = $k;", ("$k", FolderQueueKey(memberId))) as string;
    }

    /// <summary>Stores the member's queued folder fires; null or an empty queue removes them.</summary>
    public void SetFolderQueue(long memberId, string? json)
    {
        using var c = Open();
        if (json is null || json == "[]")
        {
            Exec(c, "DELETE FROM app_defaults WHERE key = $k;", ("$k", FolderQueueKey(memberId)));
            return;
        }
        Exec(c, """
            INSERT INTO app_defaults (key, value, updated_at) VALUES ($k, $v, $t)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """, ("$k", FolderQueueKey(memberId)), ("$v", json), ("$t", Now()));
    }

    /// <summary>The members that have queued folder fires.</summary>
    public IReadOnlyList<long> MembersWithFolderQueues()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT key FROM app_defaults WHERE key LIKE $p ORDER BY key;", ("$p", FolderQueueKeyPrefix + "%"));
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read())
            if (long.TryParse(r.GetString(0).AsSpan(FolderQueueKeyPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
                list.Add(id);
        return list;
    }

    /// <summary>Whether a queued folder fire may still run.</summary>
    public enum QueuedFire
    {
        /// <summary>The trigger is the member's enabled folder trigger, its team at work: the fire runs.</summary>
        Live,
        /// <summary>Its team is paused or archived: the fire stays queued.</summary>
        Held,
        /// <summary>The trigger was deleted, disabled or is no longer a folder trigger of the member: the fire is dropped.</summary>
        Gone,
    }

    public QueuedFire FolderFire(long memberId, long triggerId)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT t.enabled, t.kind, tm.paused_at, tm.archived_at FROM triggers t JOIN teams tm ON tm.id = t.team_id
            WHERE t.id = $id AND t.member_id = $m;
            """, ("$id", triggerId), ("$m", memberId));
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.GetInt64(0) == 0 || r.GetString(1) != Yawble.Core.Triggers.TriggerKinds.Folder) return QueuedFire.Gone;
        return r.IsDBNull(2) && r.IsDBNull(3) ? QueuedFire.Live : QueuedFire.Held;
    }

    public void Log(long runId, string line)
    {
        var text = Scrub(line) ?? "";
        if (text.Length > MaxField) text = text[..MaxField] + "…";
        using var c = Open();
        Exec(c, "INSERT INTO workflow_run_steps (run_id, kind, output, status, started_at) VALUES ($r, 'log', $o, 'log', $t);",
            ("$r", runId), ("$o", JsonSerializer.Serialize(text)), ("$t", Now()));
    }

    public IReadOnlyList<WorkflowStepRow> Steps(long runId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT step_id, type, input, output, status, truncated, started_at, ended_at FROM workflow_run_steps WHERE run_id = $r AND kind = 'step' ORDER BY id;",
            ("$r", runId));
        using var r = cmd.ExecuteReader();
        var list = new List<WorkflowStepRow>();
        var seq = 0;
        while (r.Read())
            list.Add(new WorkflowStepRow(++seq, r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), NodeOf(r, 2), NodeOf(r, 3),
                r.GetString(4), r.GetInt64(5) != 0, r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }

    public IReadOnlyList<WorkflowLogLine> LogLines(long runId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT started_at, output FROM workflow_run_steps WHERE run_id = $r AND kind = 'log' ORDER BY id;", ("$r", runId));
        using var r = cmd.ExecuteReader();
        var list = new List<WorkflowLogLine>();
        while (r.Read()) list.Add(new WorkflowLogLine(r.GetString(0), JsonSerializer.Deserialize<string>(r.GetString(1)) ?? ""));
        return list;
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>A JSON value as stored: scrubbed, and cut to <see cref="MaxField"/> (then kept as a JSON string of the cut text).</summary>
    internal static (string? Text, bool Truncated) Field(JsonNode? node)
    {
        if (node is null) return (null, false);
        var text = Scrub(node.ToJsonString())!;
        if (text.Length <= MaxField)
        {
            try
            {
                JsonNode.Parse(text);
                return (text, false);
            }
            catch (JsonException) { /* the scrubber cut through JSON: kept as text */ }
        }
        var cut = text.Length <= MaxField ? text : text[..MaxField] + "…";
        return (JsonSerializer.Serialize(cut), text.Length > MaxField);
    }

    private static string? Scrub(string? text) => text is null ? null : Connections.SecretScrubber.Scrub(text);

    private static JsonObject ObjectOf(SqliteDataReader r, int i) => NodeOf(r, i) as JsonObject ?? [];

    private static JsonNode? NodeOf(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        try { return JsonNode.Parse(r.GetString(i)); }
        catch (JsonException) { return JsonValue.Create(r.GetString(i)); }
    }

    private string Now() => _clock.GetUtcNow().ToString("O");

    private SqliteConnection Open()
    {
        var c = Storage.Db.Open(connectionString);
        return c;
    }

    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private static object? Scalar(SqliteConnection c, string sql, (string, object?) arg) => Scalar(c, sql, null, arg);

    private static object? Scalar(SqliteConnection c, string sql, SqliteTransaction? tx, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        cmd.Transaction = tx;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static int Exec(SqliteConnection c, string sql, params (string, object?)[] args) => Exec(c, null, sql, args);

    private static int Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        cmd.Transaction = tx;
        return cmd.ExecuteNonQuery();
    }
}
