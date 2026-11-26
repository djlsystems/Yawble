using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Logs;
using Yawble.Core.Tests.Bus;

namespace Yawble.Core.Tests.Logs;

/// <summary>
/// How old log entries are deleted: a log's tables together or not at all, a table a few thousand rows at a time, and
/// the rows of transcripts whose file is gone in one commit. Pruning the Approvals log also prunes answered network
/// asks: the preview counts exactly what Prune deletes across both tables, an ask goes by when it was answered, and a
/// waiting ask of either kind stays however old. A real migrated database in a temp folder; every row is made up here.
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

    public LogPruneTests()
    {
        _logs = new LogService(_db.Dir, _db.ConnectionString, new Fixed(Now));
        Exec("INSERT INTO teams (id, name, created_at) VALUES (1, 'web', @t);", ("@t", Ago(900)));
    }

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
        return Convert.ToInt64(cmd.ExecuteScalar());
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

    // ------------------------------------------------------------------ the Approvals log: tool-call asks and network asks

    private void Network(string at, string host, string state, string? decidedAt = null) =>
        Exec("INSERT INTO network_asks (at, team_id, member, host, method, last_seen, state, scope, decided_by, decided_at) VALUES (@at, 1, 'web/DeveloperAda', @h, 'CONNECT', @at, @s, @scope, @by, @d);",
            ("@at", at), ("@h", host), ("@s", state), ("@scope", state == "allowed" ? "agent" : null),
            ("@by", state == "pending" ? null : "pat"), ("@d", state == "pending" ? null : decidedAt ?? at));

    private void Tool(string at, string args, string state, string? decidedAt = null) =>
        Exec("INSERT INTO integration_asks (at, integration, team_id, member, args, reason, deliverable, causation_seq, state, decided_by, decided_at) VALUES (@at, 'az', 1, 'web/DeveloperAda', @a, 'r', 1, 1, @s, @by, @d);",
            ("@at", at), ("@a", args), ("@s", state), ("@by", state == "pending" ? null : "pat"), ("@d", state == "pending" ? null : decidedAt ?? at));

    /// <summary>Old answered asks of both kinds, old waiting ones of both kinds, and recent answered ones.</summary>
    private void SeedMixed()
    {
        Network(Ago(800), "oldest.example", "denied");      // the oldest entry of all is a network ask
        Network(Ago(500), "pypi.org", "allowed");
        Network(Ago(900), "waiting.example", "pending");    // older still, but waiting
        Network(Ago(10), "nuget.org", "allowed");
        Tool(Ago(600), "[\"login\"]", "approved");
        Tool(Ago(370), "[\"group\"]", "declined");          // the newest prunable entry is a tool-call ask
        Tool(Ago(700), "[\"vm\"]", "pending");
        Tool(Ago(5), "[\"account\"]", "approved");
    }

    [Fact]
    public void Preview_CountsBothTables_SpansOldestAndNewestAcrossThem_AndPruneDeletesExactlyThat()
    {
        SeedMixed();
        var cutoff = _logs.CutoffFor(365);

        var preview = _logs.PrunePreview("approvals", cutoff);
        Assert.Equal(4, preview.Count);
        Assert.Equal(Ago(800), preview.Oldest);   // from network_asks
        Assert.Equal(Ago(370), preview.Newest);   // from integration_asks
        Assert.Equal(0, preview.Files);
        Assert.Contains("which host", preview.Loss);
        Assert.Contains("never pruned", preview.Loss);

        var before = Count("SELECT count(*) FROM network_asks") + Count("SELECT count(*) FROM integration_asks");
        Assert.Equal(preview.Count, _logs.Prune("approvals", cutoff));
        var after = Count("SELECT count(*) FROM network_asks") + Count("SELECT count(*) FROM integration_asks");
        Assert.Equal(preview.Count, before - after);

        // What is left: each kind's waiting ask (however old) and its recent answer.
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks WHERE state = 'pending' AND host = 'waiting.example'"));
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks WHERE host = 'nuget.org'"));
        Assert.Equal(2, Count("SELECT count(*) FROM network_asks"));
        Assert.Equal(1, Count("SELECT count(*) FROM integration_asks WHERE state = 'pending'"));
        Assert.Equal(2, Count("SELECT count(*) FROM integration_asks"));

        // A second preview finds nothing more, and a second prune deletes nothing.
        var again = _logs.PrunePreview("approvals", cutoff);
        Assert.Equal(0, again.Count);
        Assert.Null(again.Oldest);
        Assert.Null(again.Newest);
        Assert.Equal(0, _logs.Prune("approvals", cutoff));
    }

    [Fact]
    public void OnlyNetworkAsksPastTheCutoff_PreviewAndPruneStillCountThem()
    {
        Network(Ago(400), "a.example", "allowed");
        Network(Ago(450), "b.example", "denied");
        Network(Ago(1000), "c.example", "pending");
        Tool(Ago(1), "[\"login\"]", "approved");

        var cutoff = _logs.CutoffFor(365);
        var preview = _logs.PrunePreview("approvals", cutoff);
        Assert.Equal(2, preview.Count);
        Assert.Equal(Ago(450), preview.Oldest);
        Assert.Equal(Ago(400), preview.Newest);
        Assert.Equal(2, _logs.Prune("approvals", cutoff));
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks WHERE state = 'pending'"));
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks"));
        Assert.Equal(1, Count("SELECT count(*) FROM integration_asks"));
    }

    [Fact]
    public void AWaitingNetworkAsk_IsNeverPruned_EvenWithACutoffOfNow()
    {
        Network(Ago(2000), "ancient.example", "pending");
        // Older than the 24 hours a denial is kept for, so the cutoff of now still prunes it.
        Network(Ago(25.0 / 24), "yesterday.example", "denied");

        var cutoff = Now.AddSeconds(1).ToString("O");
        Assert.Equal(1, _logs.PrunePreview("approvals", cutoff).Count);
        Assert.Equal(1, _logs.Prune("approvals", cutoff));
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks WHERE state = 'pending' AND host = 'ancient.example'"));
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks"));
    }

    [Fact]
    public void ThePersonsApprovalsRetention_CarriesTheNetworkAsks()
    {
        Network(Ago(40), "forty.example", "allowed");
        Network(Ago(20), "twenty.example", "denied");
        Network(Ago(60), "waiting.example", "pending");
        _logs.SetRetention("approvals", 30);

        Assert.Equal(1, _logs.Sweep()["approvals"]);
        Assert.Equal(0, Count("SELECT count(*) FROM network_asks WHERE host = 'forty.example'"));
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks WHERE host = 'twenty.example'"));
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks WHERE host = 'waiting.example'"));
    }

    [Fact]
    public void AnOldAskAnsweredJustNow_SurvivesTheNextSweep()
    {
        // Raised 400 days ago, waited all that time, denied an hour ago: the prune goes by when it was answered
        // (decided_at), so the person's fresh answer outlives the next daily sweep.
        Network(Ago(400), "slow.example", "denied", decidedAt: Ago(1.0 / 24));
        _logs.Sweep();
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks WHERE host = 'slow.example'"));
    }

    [Fact]
    public void AnOldToolCallAskAnsweredJustNow_SurvivesTheSweep_AndThePreviewGoesByTheAnswerTime()
    {
        Tool(Ago(400), "[\"slow\"]", "declined", decidedAt: Ago(1.0 / 24));   // raised long ago, answered an hour ago
        Tool(Ago(500), "[\"old\"]", "approved", decidedAt: Ago(380));         // answered past the retention
        Network(Ago(600), "late.example", "allowed", decidedAt: Ago(370));    // raised and answered past it
        Network(Ago(700), "fresh.example", "denied", decidedAt: Ago(2));

        var preview = _logs.PrunePreview("approvals", _logs.CutoffFor(365));
        Assert.Equal(2, preview.Count);
        Assert.Equal(Ago(380), preview.Oldest);
        Assert.Equal(Ago(370), preview.Newest);

        Assert.Equal(2, _logs.Sweep()["approvals"]);
        Assert.Equal(1, Count("SELECT count(*) FROM integration_asks WHERE args = '[\"slow\"]'"));
        Assert.Equal(0, Count("SELECT count(*) FROM integration_asks WHERE args = '[\"old\"]'"));
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks WHERE host = 'fresh.example'"));
        Assert.Equal(0, Count("SELECT count(*) FROM network_asks WHERE host = 'late.example'"));
    }

    /// <summary>An answered row with no answer time goes by when it was raised.</summary>
    [Fact]
    public void AnAnsweredAskWithNoDecidedAt_GoesByWhenItWasRaised()
    {
        Exec("INSERT INTO network_asks (at, team_id, member, host, method, last_seen, state) VALUES (@at, 1, 'web/DeveloperAda', 'old.example', 'CONNECT', @at, 'denied');", ("@at", Ago(400)));
        Exec("INSERT INTO network_asks (at, team_id, member, host, method, last_seen, state) VALUES (@at, 1, 'web/DeveloperAda', 'new.example', 'CONNECT', @at, 'allowed');", ("@at", Ago(3)));
        Exec("INSERT INTO integration_asks (at, integration, team_id, member, args, reason, deliverable, causation_seq, state) VALUES (@at, 'az', 1, 'web/DeveloperAda', '[\"x\"]', 'r', 1, 1, 'approved');", ("@at", Ago(390)));
        Exec("INSERT INTO integration_asks (at, integration, team_id, member, args, reason, deliverable, causation_seq, state) VALUES (@at, 'az', 1, 'web/DeveloperAda', '[\"y\"]', 'r', 1, 1, 'pending');", ("@at", Ago(900)));

        var preview = _logs.PrunePreview("approvals", _logs.CutoffFor(365));
        Assert.Equal(2, preview.Count);
        Assert.Equal(Ago(400), preview.Oldest);
        Assert.Equal(Ago(390), preview.Newest);
        Assert.Equal(2, _logs.Sweep()["approvals"]);
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks WHERE host = 'new.example'"));
        Assert.Equal(1, Count("SELECT count(*) FROM network_asks"));
        Assert.Equal(1, Count("SELECT count(*) FROM integration_asks WHERE state = 'pending'"));
        Assert.Equal(1, Count("SELECT count(*) FROM integration_asks"));
    }
}
