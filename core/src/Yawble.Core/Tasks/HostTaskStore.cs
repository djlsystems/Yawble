using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yawble.Core.Integrations;

namespace Yawble.Core.Tasks;

/// <summary>A task ask as stored in integration_asks (integration = 'task:&lt;name&gt;'), with what the person was shown.</summary>
public sealed record TaskAskRow(long Id, long TeamId, string TaskName, long TaskId, string Member, string Reason, long Workflow, long CausationSeq,
    string State, string? Note, string At, string? CommitSha, string? DefinitionHash, string? FilesHash, string? DetailJson);

/// <summary>The database side of host tasks: definitions, the one standing approval per task, asks, calls and run records.</summary>
public sealed class HostTaskStore(string connectionString, TimeProvider clock)
{
    public string Now() => clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ definitions

    private const string TaskColumns = "id, team_id, name, repo, program, args, workdir, timeout_s, env_names, watch, pin_mode, command_line_only, updated_at, updated_by, env_from_connections";

    private static HostTask ReadTask(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1),
        new HostTaskDef(r.GetString(2), r.GetString(3), r.GetString(4), JsonSerializer.Deserialize<string[]>(r.GetString(5)) ?? [], r.GetString(6), r.GetInt32(7),
            JsonSerializer.Deserialize<string[]>(r.GetString(8)) ?? [], JsonSerializer.Deserialize<string[]>(r.GetString(9)) ?? [], r.GetString(10), r.GetInt64(11) == 1)
        {
            EnvFromConnections = JsonSerializer.Deserialize<EnvBinding[]>(r.GetString(14), BindingJson) ?? [],
        },
        r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13));

    /// <summary>env_from_connections: [{name, connectionId, field, connectionName}], never a value.</summary>
    private static readonly JsonSerializerOptions BindingJson = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<HostTask> Tasks(long teamId) =>
        Query($"SELECT {TaskColumns} FROM host_tasks WHERE team_id = $t ORDER BY name;", ReadTask, ("$t", teamId));

    public HostTask? Task(long teamId, string name) =>
        Query($"SELECT {TaskColumns} FROM host_tasks WHERE team_id = $t AND name = $n;", ReadTask, ("$t", teamId), ("$n", name)).FirstOrDefault();

    public HostTask? TaskById(long id) => Query($"SELECT {TaskColumns} FROM host_tasks WHERE id = $i;", ReadTask, ("$i", id)).FirstOrDefault();

    /// <summary>Inserts, or replaces the same name's definition. Editing deletes the approval. Returns the task.</summary>
    public HostTask Save(long teamId, HostTaskDef d, string? by)
    {
        var existing = Task(teamId, d.Name);
        var args = JsonSerializer.Serialize(d.Args);
        var env = JsonSerializer.Serialize(d.EnvNames);
        var watch = JsonSerializer.Serialize(d.Watch);
        var bindings = JsonSerializer.Serialize(d.EnvFromConnections, BindingJson);
        if (existing is null)
        {
            Exec("""
                INSERT INTO host_tasks (team_id, name, repo, program, args, workdir, timeout_s, env_names, watch, pin_mode, command_line_only, env_from_connections, created_at, updated_at, updated_by)
                VALUES ($t, $n, $repo, $p, $a, $w, $s, $e, $wa, $m, $c, $ef, $now, $now, $by);
                """, ("$t", teamId), ("$n", d.Name), ("$repo", d.Repo), ("$p", d.Program), ("$a", args), ("$w", d.Workdir), ("$s", d.TimeoutSeconds), ("$e", env),
                ("$wa", watch), ("$m", d.PinMode), ("$c", d.CommandLineOnly ? 1 : 0), ("$ef", bindings), ("$now", Now()), ("$by", by));
        }
        else
        {
            Exec("""
                UPDATE host_tasks SET repo = $repo, program = $p, args = $a, workdir = $w, timeout_s = $s, env_names = $e, watch = $wa, pin_mode = $m,
                    command_line_only = $c, env_from_connections = $ef, updated_at = $now, updated_by = $by WHERE id = $id;
                """, ("$id", existing.Id), ("$repo", d.Repo), ("$p", d.Program), ("$a", args), ("$w", d.Workdir), ("$s", d.TimeoutSeconds), ("$e", env), ("$wa", watch),
                ("$m", d.PinMode), ("$c", d.CommandLineOnly ? 1 : 0), ("$ef", bindings), ("$now", Now()), ("$by", by));
            RevokeApproval(existing.Id);
        }
        return Task(teamId, d.Name)!;
    }

    public void Delete(long taskId) => Exec("DELETE FROM host_tasks WHERE id = $i;", ("$i", taskId));

    // ------------------------------------------------------------------ the standing approval

    public ApprovalRow? Approval(long taskId) => Query(
        "SELECT task_id, definition_hash, files_hash, definition_json, files_json, commit_sha, approved_by, via, approved_at FROM host_task_approvals WHERE task_id = $i;",
        r => new ApprovalRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), S(r, 6), S(r, 7), r.GetString(8)),
        ("$i", taskId)).FirstOrDefault();

    /// <summary>Stores "Always for this team" at the shown hashes, replacing any older one.</summary>
    public void SetApproval(long taskId, string defHash, string filesHash, string defJson, string filesJson, string commit, Provenance.Actor? who) =>
        Exec("""
            INSERT INTO host_task_approvals (task_id, definition_hash, files_hash, definition_json, files_json, commit_sha, approved_by, via, approved_at)
            VALUES ($i, $dh, $fh, $dj, $fj, $c, $by, $via, $now)
            ON CONFLICT(task_id) DO UPDATE SET definition_hash = $dh, files_hash = $fh, definition_json = $dj, files_json = $fj, commit_sha = $c,
                approved_by = $by, via = $via, approved_at = $now;
            """, ("$i", taskId), ("$dh", defHash), ("$fh", filesHash), ("$dj", defJson), ("$fj", filesJson), ("$c", commit), ("$by", who?.User), ("$via", who?.Via), ("$now", Now()));

    public void RevokeApproval(long taskId) => Exec("DELETE FROM host_task_approvals WHERE task_id = $i;", ("$i", taskId));

    // ------------------------------------------------------------------ asks

    private const string AskColumns = "id, team_id, integration, task_id, member, reason, workflow, causation_seq, state, note, at, commit_sha, definition_hash, files_hash, detail_json";

    private static TaskAskRow ReadAsk(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2)["task:".Length..], r.GetInt64(3), r.GetString(4), r.GetString(5),
        r.GetInt64(6), r.GetInt64(7), r.GetString(8), S(r, 9), r.GetString(10), S(r, 11), S(r, 12), S(r, 13), S(r, 14));

    public TaskAskRow? Ask(long id) => Query($"SELECT {AskColumns} FROM integration_asks WHERE id = $i AND task_id IS NOT NULL;", ReadAsk, ("$i", id)).FirstOrDefault();

    public TaskAskRow? PendingAsk(long taskId) =>
        Query($"SELECT {AskColumns} FROM integration_asks WHERE task_id = $i AND state = 'pending' ORDER BY id LIMIT 1;", ReadAsk, ("$i", taskId)).FirstOrDefault();

    public IReadOnlyList<TaskAskRow> PendingAsks(long? teamId = null) =>
        Query($"SELECT {AskColumns} FROM integration_asks WHERE task_id IS NOT NULL AND state = 'pending' {(teamId is null ? "" : "AND team_id = $t")} ORDER BY id;",
            ReadAsk, ("$t", teamId));

    public long InsertAsk(HostTask task, string member, string reason, long workflow, long causation, string commit, string defHash, string filesHash, string detailJson) =>
        Scalar("""
            INSERT INTO integration_asks (at, integration, team_id, member, args, reason, workflow, causation_seq, task_id, commit_sha, definition_hash, files_hash, detail_json)
            VALUES ($at, $i, $t, $m, '[]', $r, $w, $c, $task, $commit, $dh, $fh, $d) RETURNING id;
            """, ("$at", Now()), ("$i", "task:" + task.Def.Name), ("$t", task.TeamId), ("$m", member), ("$r", Clip(reason, 500)), ("$w", workflow), ("$c", causation),
            ("$task", task.Id), ("$commit", commit), ("$dh", defHash), ("$fh", filesHash), ("$d", detailJson));

    /// <summary>Closes a pending ask. A closed-as-stale ask is 'declined' with a note starting "stale:" (the state column has no other word for it).</summary>
    public bool CloseAsk(long id, string state, string? note, Provenance.Actor? who) =>
        Count("UPDATE integration_asks SET state = $s, decided_by = $by, decided_at = $now, note = $note WHERE id = $id AND state = 'pending';",
            ("$s", state), ("$by", who?.User), ("$now", Now()), ("$note", note), ("$id", id)) == 1;

    /// <summary>When the task's last real decline was, or null (a stale closing is not a decline).</summary>
    public (string At, string? Note)? LastDecline(long taskId) => Query(
        "SELECT decided_at, note FROM integration_asks WHERE task_id = $i AND state = 'declined' AND (note IS NULL OR note NOT LIKE 'stale:%') ORDER BY id DESC LIMIT 1;",
        r => (r.GetString(0), S(r, 1)), ("$i", taskId)).Select(x => ((string, string?)?)x).FirstOrDefault();

    public int AsksSince(long teamId, DateTimeOffset since) => (int)Scalar(
        "SELECT count(*) FROM integration_asks WHERE team_id = $t AND task_id IS NOT NULL AND at >= $s;",
        ("$t", teamId), ("$s", since.ToString("O", CultureInfo.InvariantCulture)));

    // ------------------------------------------------------------------ calls (the existing log)

    public void Call(HostTask task, string? member, long? workflow, string decision, string? rule, TaskRunRow? run, Provenance.Actor? who) =>
        Exec("""
            INSERT INTO integration_calls (at, integration, team, member, workflow, args, decision, rule, exit_code, duration_ms, output_bytes, via, user_name)
            VALUES ($at, $i, $team, $m, $w, '[]', $d, $r, $x, $ms, $b, $via, $u);
            """, ("$at", Now()), ("$i", "task:" + task.Def.Name), ("$team", TeamName(task.TeamId)), ("$m", member), ("$w", workflow), ("$d", decision), ("$r", rule),
            ("$x", run?.ExitCode), ("$ms", run?.EndedAt is { } e ? (long)(DateTimeOffset.Parse(e, CultureInfo.InvariantCulture) - DateTimeOffset.Parse(run.StartedAt, CultureInfo.InvariantCulture)).TotalMilliseconds : null),
            ("$b", run?.OutputBytes), ("$via", who?.Via), ("$u", who?.User));

    public string? TeamName(long teamId) => Query("SELECT name FROM teams WHERE id = $i;", r => r.GetString(0), ("$i", teamId)).FirstOrDefault();

    // ------------------------------------------------------------------ runs

    private const string RunColumns = "id, task_id, team_id, task_name, state, started_at, ended_at, commit_sha, head_after, definition_hash, files_hash, program_resolved, exit_code, " +
        "timed_out, killed_by, output_bytes, truncated, log_path, log_sha256, dirty_after, ask_id, requested_by, missing_env";

    private static TaskRunRow ReadRun(SqliteDataReader r) => new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5),
        S(r, 6), S(r, 7), S(r, 8), S(r, 9), S(r, 10), S(r, 11), r.IsDBNull(12) ? null : r.GetInt32(12), r.GetInt64(13) == 1, S(r, 14), r.IsDBNull(15) ? null : r.GetInt64(15),
        r.GetInt64(16) == 1, S(r, 17), S(r, 18), r.IsDBNull(19) ? null : r.GetInt64(19) == 1, r.IsDBNull(20) ? null : r.GetInt64(20), S(r, 21), S(r, 22));

    public TaskRunRow? Run(long id) => Query($"SELECT {RunColumns} FROM host_task_runs WHERE id = $i;", ReadRun, ("$i", id)).FirstOrDefault();

    public IReadOnlyList<TaskRunRow> Runs(long taskId, int limit = 20) =>
        Query($"SELECT {RunColumns} FROM host_task_runs WHERE task_id = $i ORDER BY id DESC LIMIT $n;", ReadRun, ("$i", taskId), ("$n", limit));

    public IReadOnlyList<TaskRunRow> RecentRuns(long? teamId, int limit) =>
        Query($"SELECT {RunColumns} FROM host_task_runs {(teamId is null ? "" : "WHERE team_id = $t")} ORDER BY id DESC LIMIT $n;", ReadRun, ("$t", teamId), ("$n", limit));

    public TaskRunRow? RunningRun(long taskId) =>
        Query($"SELECT {RunColumns} FROM host_task_runs WHERE task_id = $i AND state = 'running' LIMIT 1;", ReadRun, ("$i", taskId)).FirstOrDefault();

    public TaskRunRow? RunningRunInRepo(string repoPath) =>
        Query($"SELECT {RunColumns} FROM host_task_runs WHERE repo_path = $p AND state = 'running' LIMIT 1;", ReadRun, ("$p", repoPath)).FirstOrDefault();

    public long InsertRun(HostTask task, string repoPath, long? askId, string? requestedBy, string commit, string defHash, string filesHash, string program, string? missingEnv,
        Provenance.Actor? who) =>
        Scalar("""
            INSERT INTO host_task_runs (task_id, team_id, task_name, repo_path, ask_id, requested_by, started_at, commit_sha, definition_hash, files_hash, program_resolved, missing_env, state, via, user_name)
            VALUES ($t, $team, $n, $p, $a, $rb, $now, $c, $dh, $fh, $prog, $me, 'running', $via, $u) RETURNING id;
            """, ("$t", task.Id), ("$team", task.TeamId), ("$n", task.Def.Name), ("$p", repoPath), ("$a", askId), ("$rb", requestedBy), ("$now", Now()), ("$c", commit),
            ("$dh", defHash), ("$fh", filesHash), ("$prog", program), ("$me", missingEnv), ("$via", who?.Via), ("$u", who?.User));

    public void FinishRun(long id, string state, int? exitCode, bool timedOut, string? killedBy, long? bytes, bool truncated, string? logPath, string? logSha,
        string? headAfter, bool? dirtyAfter) =>
        Exec("""
            UPDATE host_task_runs SET state = $s, ended_at = $now, exit_code = $x, timed_out = $to, killed_by = $kb, output_bytes = $b, truncated = $tr, log_path = $lp,
                log_sha256 = $ls, head_after = $h, dirty_after = $d WHERE id = $id;
            """, ("$id", id), ("$s", state), ("$now", Now()), ("$x", exitCode), ("$to", timedOut ? 1 : 0), ("$kb", killedBy), ("$b", bytes), ("$tr", truncated ? 1 : 0),
            ("$lp", logPath), ("$ls", logSha), ("$h", headAfter), ("$d", dirtyAfter is null ? null : dirtyAfter.Value ? 1 : 0));

    /// <summary>On app start: a run left 'running' means the app died (the job object killed the process).</summary>
    public int MarkInterrupted() => Count(
        "UPDATE host_task_runs SET state = 'interrupted', ended_at = $now, killed_by = 'app-exit' WHERE state = 'running';", ("$now", Now()));

    // ------------------------------------------------------------------ plumbing

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        using var fk = c.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        fk.ExecuteNonQuery();
        return c;
    }

    private static void Bind(SqliteCommand cmd, string sql, (string, object?)[] args)
    {
        foreach (var (n, v) in args) if (sql.Contains(n, StringComparison.Ordinal)) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }

    private void Exec(string sql, params (string, object?)[] args) => Count(sql, args);

    private int Count(string sql, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, sql, args);
        return cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, sql, args);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
