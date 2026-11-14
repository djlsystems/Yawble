using Microsoft.Data.Sqlite;
using Yawble.Core.Logs;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Logs;

/// <summary>
/// Gap tests for card 60 / 0326f54: pruning the Approvals log also prunes answered network asks. The preview counts
/// exactly what Prune deletes across both tables, oldest and newest span both, a waiting ask of either kind stays
/// however old, and the person's own retention for Approvals carries the network asks with it. Every row is made up.
/// </summary>
public sealed class LogPruneGapTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDb _db = new();
    private readonly LogService _logs;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public LogPruneGapTests()
    {
        _logs = new LogService(_db.Dir, _db.ConnectionString, new FixedClock(Now));
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

    private void Network(string at, string host, string state, string? decidedAt = null) =>
        Exec("INSERT INTO network_asks (at, team_id, member, host, method, last_seen, state, scope, decided_by, decided_at) VALUES (@at, 1, 'web/DeveloperAda', @h, 'CONNECT', @at, @s, @scope, @by, @d);",
            ("@at", at), ("@h", host), ("@s", state), ("@scope", state == "allowed" ? "agent" : null),
            ("@by", state == "pending" ? null : "pat"), ("@d", state == "pending" ? null : decidedAt ?? at));

    private void Tool(string at, string args, string state, string? decidedAt = null) =>
        Exec("INSERT INTO integration_asks (at, integration, team_id, member, args, reason, workflow, causation_seq, state, decided_by, decided_at) VALUES (@at, 'az', 1, 'web/DeveloperAda', @a, 'r', 1, 1, @s, @by, @d);",
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

    [Fact] // card 60: was skipped as the fault from 0326f54 (cut by at, not decided_at)
    public void AnOldAskAnsweredJustNow_SurvivesTheNextSweep()
    {
        // Raised 400 days ago, waited all that time, denied an hour ago. The cutoff is by when it was raised (at),
        // so this checks whether the person's fresh answer outlives the next daily sweep.
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

    [Fact] // card 62 re-check of 524cb07: an answered row with no answer time kept goes by when it was raised
    public void AnAnsweredAskWithNoDecidedAt_GoesByWhenItWasRaised()
    {
        Exec("INSERT INTO network_asks (at, team_id, member, host, method, last_seen, state) VALUES (@at, 1, 'web/DeveloperAda', 'old.example', 'CONNECT', @at, 'denied');", ("@at", Ago(400)));
        Exec("INSERT INTO network_asks (at, team_id, member, host, method, last_seen, state) VALUES (@at, 1, 'web/DeveloperAda', 'new.example', 'CONNECT', @at, 'allowed');", ("@at", Ago(3)));
        Exec("INSERT INTO integration_asks (at, integration, team_id, member, args, reason, workflow, causation_seq, state) VALUES (@at, 'az', 1, 'web/DeveloperAda', '[\"x\"]', 'r', 1, 1, 'approved');", ("@at", Ago(390)));
        Exec("INSERT INTO integration_asks (at, integration, team_id, member, args, reason, workflow, causation_seq, state) VALUES (@at, 'az', 1, 'web/DeveloperAda', '[\"y\"]', 'r', 1, 1, 'pending');", ("@at", Ago(900)));

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
