using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Logs;
using Yawble.Core.Tests.Bus;

namespace Yawble.Core.Tests.Logs;

/// <summary>
/// How old log entries are deleted: a log's tables together or not at all, a table a few thousand rows at a time, and
/// the rows of transcripts whose file is gone in one commit. A real migrated database in a temp folder; every row is
/// made up here.
/// </summary>
public sealed class LogPruneTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDb _db = new();
    private readonly LogService _logs;

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public LogPruneTests() => _logs = new LogService(_db.Dir, _db.ConnectionString, new Fixed(Now));

    public void Dispose() => _db.Dispose();

    private static string Ago(double days) => (Now - TimeSpan.FromDays(days)).ToString("O");

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private long Count(string sql)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void A_table_with_more_old_rows_than_one_statement_takes_loses_them_all_and_keeps_the_new()
    {
        var old = LogService.PruneChunk * 2 + 300;
        Exec($"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {old})
            INSERT INTO usage_events (at, run_key, member, vhost, path, status) SELECT @old, 'r-' || i, 'web/Ada', 'model.yawble.internal', '/v1/messages', 200 FROM n;
            """, ("@old", Ago(400)));
        Exec("INSERT INTO usage_events (at, run_key, member, vhost, path, status) VALUES (@new, 'r-new', 'web/Ada', 'model.yawble.internal', '/v1/messages', 200);", ("@new", Ago(1)));

        Assert.Equal(old, _logs.Prune("usage", _logs.CutoffFor(365)));

        Assert.Equal(1, Count("SELECT count(*) FROM usage_events;"));
        Assert.Equal(1, Count("SELECT count(*) FROM usage_events WHERE run_key = 'r-new';"));
    }

    /// <summary>The Approvals log reads four tables. When one of them cannot be pruned, none of them loses a row.</summary>
    [Fact]
    public void A_log_of_several_tables_is_pruned_from_all_of_them_or_none()
    {
        Exec("INSERT INTO teams (id, name, created_at) VALUES (1, 'web', @t);", ("@t", Ago(900)));
        Exec("INSERT INTO integration_asks (at, integration, team_id, member, args, reason, deliverable, causation_seq, state, decided_by, decided_at) VALUES (@a, 'az', 1, 'web/Ada', '[]', 'deploy', 1, 1, 'approved', 'pat', @a);", ("@a", Ago(500)));
        Exec("INSERT INTO network_asks (team_id, member, host, state, at, last_seen, decided_at, decided_by) VALUES (1, 'web/Ada', 'old.example', 'allowed', @a, @a, @a, 'pat');", ("@a", Ago(500)));
        Exec("CREATE TRIGGER network_asks_stay BEFORE DELETE ON network_asks BEGIN SELECT RAISE(ABORT, 'network asks stay'); END;");

        Assert.Throws<SqliteException>(() => _logs.Prune("approvals", _logs.CutoffFor(365)));

        Assert.Equal(1, Count("SELECT count(*) FROM integration_asks;"));
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks;"));

        Exec("DROP TRIGGER network_asks_stay;");
        Assert.Equal(2, _logs.Prune("approvals", _logs.CutoffFor(365)));
        Assert.Equal(0, Count("SELECT count(*) FROM integration_asks;"));
        Assert.Equal(0, Count("SELECT count(*) FROM network_asks;"));
    }

    [Fact]
    public void Transcripts_whose_file_is_gone_are_forgotten_together()
    {
        var kept = Path.Combine(_db.Dir, "transcripts", "kept.log");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllText(kept, "output");
        Exec("""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1200)
            INSERT INTO transcripts (run_id, member, path, started_at, ended_at, bytes) SELECT i, 'web/Ada', @gone || i, @a, @a, 6 FROM n;
            """, ("@gone", Path.Combine(_db.Dir, "transcripts", "gone-")), ("@a", Ago(1)));
        Exec("INSERT INTO transcripts (run_id, member, path, started_at, ended_at, bytes) VALUES (5000, 'web/Ada', @p, @a, @a, 6);", ("@p", kept), ("@a", Ago(1)));
        Exec("INSERT INTO transcripts (run_id, member, path, started_at, ended_at, bytes) VALUES (5001, 'web/Ada', @p, @a, NULL, 0);", ("@p", Path.Combine(_db.Dir, "transcripts", "running.log")), ("@a", Ago(0)));

        // One of them cannot go: none goes (they are deleted in one commit).
        Exec("CREATE TRIGGER transcript_stays BEFORE DELETE ON transcripts WHEN old.run_id = 900 BEGIN SELECT RAISE(ABORT, 'stays'); END;");
        Assert.Throws<SqliteException>(() => _logs.ForgetMissingTranscripts());
        Assert.Equal(1202, Count("SELECT count(*) FROM transcripts;"));

        Exec("DROP TRIGGER transcript_stays;");
        Assert.Equal(1200, _logs.ForgetMissingTranscripts());
        Assert.Equal(2, Count("SELECT count(*) FROM transcripts;"));
        Assert.Equal(2, Count("SELECT count(*) FROM transcripts WHERE run_id IN (5000, 5001);"));
        Assert.Equal(0, _logs.ForgetMissingTranscripts());
    }
}
