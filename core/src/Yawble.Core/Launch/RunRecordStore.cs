using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Launch;

/// <summary>The run_records table (0005): one row per sandbox, written at launch and at exit.</summary>
public sealed class RunRecordStore(string connectionString, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public sealed record Record(
        string RunKey, long? BusRunId, string Team, string Member, string Cli, string Kind, string Backend,
        int? SandboxPid, string? EndClass, int? ExitCode, bool SandboxTimedOut, long? KillLatencyMs, bool TreeSurvived,
        long? DurationMs, string? Reason, string Guardrails, string StartedAt, string? EndedAt,
        string? Agent = null, string? Model = null, string? ModelSource = null, string? CliVersion = null, string? Argv = null,
        string? BaseCommit = null);

    /// <summary>
    /// What the run really ran with (0033), written once the command line is built: the agent profile, the RESOLVED model and
    /// where it came from, the CLI version detected at start, and the argv (already redacted, see RunArgv). Start/end times
    /// are the row's started_at / ended_at, UTC.
    /// </summary>
    public void RanWith(string runKey, string agent, string model, string modelSource, string? cliVersion, IReadOnlyList<string> redactedArgv) =>
        Exec("UPDATE run_records SET agent = $a, model = $m, model_source = $ms, cli_version = $v, argv = $argv WHERE run_key = $k;",
            ("$a", agent), ("$m", model), ("$ms", modelSource), ("$v", (object?)cliVersion ?? DBNull.Value),
            ("$argv", JsonSerializer.Serialize(redactedArgv)), ("$k", runKey));

    /// <summary>The base-branch commit the member's clone was on when the run started (0034).</summary>
    public void BaseCommit(string runKey, string? sha) =>
        Exec("UPDATE run_records SET base_commit = $b WHERE run_key = $k;", ("$b", (object?)sha ?? DBNull.Value), ("$k", runKey));

    /// <summary>The CLI version the most recent run of this CLI started with (null when none recorded one).</summary>
    public string? LastVersion(string cli)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT cli_version FROM run_records WHERE cli = $c AND cli_version IS NOT NULL ORDER BY id DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$c", cli);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>The newest records first, optionally one member's.</summary>
    /// <param name="excludeTeam">A team whose runs are left out (before the limit is applied).</param>
    public IReadOnlyList<Record> Recent(string? team, string? member, int limit, string? excludeTeam = null) =>
        Query(team is null ? "" : member is null ? "WHERE team = $t COLLATE NOCASE" : "WHERE team = $t COLLATE NOCASE AND member = $m COLLATE NOCASE",
            [.. (team is null ? [] : new[] { ("$t", (object)team) }), .. (member is null ? [] : new[] { ("$m", (object)member) })])
            .Where(r => excludeTeam is null || !string.Equals(r.Team, excludeTeam, StringComparison.OrdinalIgnoreCase))
            .TakeLast(Math.Clamp(limit, 1, 500)).Reverse().ToList();

    /// <summary>
    /// A run starts. One that is not a bus run (a Concierge turn, an interactive terminal, an agent test) records who
    /// started it and how (0015_provenance.sql); a bus run's origin is its causation message's, so it records none.
    /// </summary>
    public void Started(string runKey, long? busRunId, string team, string member, string cli, string kind, string backend)
    {
        var actor = busRunId is null ? Provenance.Actor.Current ?? new Provenance.Actor(Provenance.Actor.Host, null) : null;
        Exec("""
            INSERT INTO run_records (run_key, bus_run_id, team, member, cli, kind, backend, started_at, via, user_name)
            VALUES ($k, $b, $t, $m, $c, $kind, $be, $s, $via, $user);
            """,
            ("$k", runKey), ("$b", (object?)busRunId ?? DBNull.Value), ("$t", team), ("$m", member), ("$c", cli),
            ("$kind", kind), ("$be", backend), ("$s", Now()), ("$via", (object?)actor?.Via ?? DBNull.Value), ("$user", (object?)actor?.User ?? DBNull.Value));
    }

    public void Spawned(string runKey, int pid) =>
        Exec("UPDATE run_records SET sandbox_pid = $p WHERE run_key = $k;", ("$p", pid), ("$k", runKey));

    public void Ended(string runKey, RunEnd end, SupervisedExit? exit, string? reason, IReadOnlyList<GuardrailEvent> guardrails) =>
        Exec("""
            UPDATE run_records SET end_class = $e, exit_code = $x, sandbox_timed_out = $mt, kill_latency_ms = $kl,
                tree_survived = $ts, duration_ms = $d, reason = $r, guardrails = $g, ended_at = $at
            WHERE run_key = $k;
            """,
            ("$e", RunEnds.Code(end)), ("$x", (object?)exit?.ExitCode ?? DBNull.Value), ("$mt", exit?.SandboxTimedOut == true ? 1 : 0),
            ("$kl", exit?.KillLatency is { } kl ? (long)kl.TotalMilliseconds : DBNull.Value),
            ("$ts", exit?.TreeSurvived == true ? 1 : 0),
            ("$d", exit is null ? DBNull.Value : (long)exit.Duration.TotalMilliseconds),
            ("$r", (object?)reason ?? DBNull.Value), ("$g", JsonSerializer.Serialize(guardrails)), ("$at", Now()), ("$k", runKey));

    /// <summary>A core restart: every record still open was a sandbox the old core owned. It died with it.</summary>
    public int MarkOpenInterrupted() =>
        Exec("""
            UPDATE run_records SET end_class = 'interrupted', reason = 'interrupted: the core stopped while this sandbox was running',
                ended_at = $at WHERE ended_at IS NULL;
            """, ("$at", Now()));

    public Record? Get(string runKey) => Query("WHERE run_key = $k", ("$k", runKey)).SingleOrDefault();

    public IReadOnlyList<Record> All() => Query("");

    private IReadOnlyList<Record> Query(string where, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT run_key, bus_run_id, team, member, cli, kind, backend, sandbox_pid, end_class, exit_code, sandbox_timed_out,
                   kill_latency_ms, tree_survived, duration_ms, reason, guardrails, started_at, ended_at,
                   agent, model, model_source, cli_version, argv, base_commit
            FROM run_records {where} ORDER BY id;
            """;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        var list = new List<Record>();
        while (r.Read())
        {
            list.Add(new Record(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetInt32(9), r.GetInt64(10) != 0, r.IsDBNull(11) ? null : r.GetInt64(11), r.GetInt64(12) != 0,
                r.IsDBNull(13) ? null : r.GetInt64(13), r.IsDBNull(14) ? null : r.GetString(14), r.GetString(15), r.GetString(16),
                r.IsDBNull(17) ? null : r.GetString(17),
                r.IsDBNull(18) ? null : r.GetString(18), r.IsDBNull(19) ? null : r.GetString(19), r.IsDBNull(20) ? null : r.GetString(20),
                r.IsDBNull(21) ? null : r.GetString(21), r.IsDBNull(22) ? null : r.GetString(22),
                r.IsDBNull(23) ? null : r.GetString(23)));
        }
        return list;
    }

    private int Exec(string sql, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }

    private string Now() => _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
}
