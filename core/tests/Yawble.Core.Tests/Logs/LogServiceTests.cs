using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Logs;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Logs;

/// <summary>
/// The Logs dialog's service over a real migrated database: every source reads, filters, searches, sorts and pages; a
/// member's activity gathers every log about it; pruning says what it deletes and deletes only that; retention is kept
/// per source and swept. Every row is made up here.
/// </summary>
public sealed class LogServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDb _db = new();
    private readonly Clock _clock = new(Now);

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
    private readonly LogService _logs;

    public LogServiceTests()
    {
        _logs = new LogService(_db.Dir, _db.ConnectionString, _clock);
        Seed();
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

    private string Transcript(string member, long run)
    {
        var path = Path.Combine(_db.Dir, "transcripts", member.Replace('/', Path.DirectorySeparatorChar), $"run-{run}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "output");
        return path;
    }

    private void Seed()
    {
        Exec("INSERT INTO teams (id, name, created_at) VALUES (1, 'web', @t), (2, 'docs', @t);", ("@t", Ago(400)));
        Exec("INSERT INTO members (id, team_id, name, role, cli, instructions, created_at) VALUES (10, 1, 'Manager', 'manager', 'claude', '', @t), (11, 1, 'DeveloperAda', 'member', 'claude', '', @t), (12, 2, 'Manager', 'manager', 'codex', '', @t);", ("@t", Ago(400)));
        // Audit: one about Ada by id, one about her by name, one about the team only, and an old one.
        Exec("INSERT INTO audit_log (at, via, user_name, method, target, outcome) VALUES (@a, 'app', 'pat', 'members.saveSettings', 'memberId=11', 'done'), (@b, 'cloud', 'viewer@example.com', 'teams.setEnv', 'team=web member=DeveloperAda', 'done'), (@c, 'app', 'pat', 'teams.pause', 'teamId=1', 'done'), (@d, 'app', 'pat', 'teams.pause', 'teamId=2', 'done');",
            ("@a", Ago(1)), ("@b", Ago(2)), ("@c", Ago(3)), ("@d", Ago(500)));
        Exec("INSERT INTO gateway_events (run_id, member, at, kind, method, target, status, detail) VALUES (1, 'web/DeveloperAda', @a, 'connect-denied', 'CONNECT', 'evil.example:443', 403, 'not an allowed host'), (1, 'web/DeveloperAda', @b, 'forward', 'POST', '/v1/messages', 200, NULL), (2, 'docs/Manager', @c, 'forward', 'POST', '/v1/messages', 200, NULL);",
            ("@a", Ago(1)), ("@b", Ago(400)), ("@c", Ago(1)));
        Exec("INSERT INTO run_records (run_key, team, member, cli, kind, backend, end_class, exit_code, duration_ms, started_at, ended_at) VALUES ('r-1', 'web', 'DeveloperAda', 'claude', 'headless', 'wxc', 'completed', 0, 61000, @a, @a), ('r-2', 'docs', 'Manager', 'codex', 'headless', 'wxc', 'failed', 1, 1000, @b, @b);",
            ("@a", Ago(1)), ("@b", Ago(2)));
        Exec("INSERT INTO integration_calls (at, integration, team, member, args, decision, rule) VALUES (@a, 'gh', 'web', 'web/DeveloperAda', '[\"pr\",\"list\"]', 'allow', 'preset');", ("@a", Ago(1)));
        Exec("INSERT INTO integration_asks (at, integration, team_id, member, args, reason, workflow, causation_seq, state, decided_by, decided_at) VALUES (@a, 'az', 1, 'web/DeveloperAda', '[\"login\"]', 'deploy', 1, 1, 'approved', 'pat', @a), (@b, 'az', 1, 'web/DeveloperAda', '[\"group\"]', 'old but waiting', 1, 1, 'pending', NULL, NULL);",
            ("@a", Ago(1)), ("@b", Ago(600)));
        Exec("INSERT INTO usage_events (at, run_key, member, vhost, path, status, model, input_tokens, output_tokens) VALUES (@a, 'r-1', 'web/DeveloperAda', 'model.yawble.internal', '/v1/messages', 200, 'opus', 1200, 300);", ("@a", Ago(1)));
        Exec("INSERT INTO messages (type, payload, source, depth, occurred_at, via) VALUES ('agent.instruction.web/DeveloperAda', '{\"text\":\"add the login page\"}', 'web/Manager', 0, @a, 'agent'), ('agent.handback', '{\"delivered\":\"login page on members/developerada\"}', 'web/DeveloperAda', 0, @b, 'agent'), ('agent.instruction.docs/Manager', '{\"text\":\"write the guide\"}', 'person', 0, @c, 'app');",
            ("@a", Ago(1.5)), ("@b", Ago(1.2)), ("@c", Ago(1)));
        Exec("INSERT INTO agent_changes (at, kind, subject, from_agent, to_agent, from_model, to_model, effective_model, actor_via, actor_user, how) VALUES (@a, 'member-agent', 'member:web/DeveloperAda', 'claude', 'codex', '', '', '', 'app', 'pat', 'settings');", ("@a", Ago(3)));
        var recent = Transcript("web/DeveloperAda", 1);
        var old = Transcript("web/DeveloperAda", 2);
        Exec("INSERT INTO transcripts (run_id, member, path, started_at, ended_at, bytes) VALUES (1, 'web/DeveloperAda', @p1, @a, @a, 6), (2, 'web/DeveloperAda', @p2, @b, @b, 6), (3, 'web/DeveloperAda', @p3, @a, @a, 6);",
            ("@p1", recent), ("@p2", old), ("@p3", Path.Combine(_db.Dir, "transcripts", "gone.log")), ("@a", Ago(1)), ("@b", Ago(40)));
    }

    private JsonObject Q(LogQuery q) => _logs.Query(q);

    private static List<JsonObject> Rows(JsonObject page) => page["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();

    [Fact]
    public void EverySourceAnswers_WithAndWithoutATeamOrMember()
    {
        foreach (var s in _logs.Sources().Select(x => x!["id"]!.GetValue<string>()))
        {
            Assert.NotNull(Q(new LogQuery(s))["rows"]);
            Assert.NotNull(Q(new LogQuery(s, Team: "web"))["rows"]);
            Assert.NotNull(Q(new LogQuery(s, Member: "web/DeveloperAda", Text: "a"))["rows"]);
        }
    }

    [Fact]
    public void AMembersActivity_GathersEveryLogAboutIt_AndNothingElse()
    {
        var page = Q(new LogQuery(LogSources.Activity, Member: "web/DeveloperAda", Limit: 200));
        var rows = Rows(page);
        Assert.All(rows, r => Assert.Equal("DeveloperAda", r["member"]!.GetValue<string>()));
        var sources = rows.Select(r => r["source"]!.GetValue<string>()).ToHashSet();
        Assert.Equal(["Agent changes", "Approvals", "Audit", "Messages", "Model usage", "Network", "Runs", "Tool calls", "Transcripts"], sources.Order());
        // Both audit entries about her: by id and by name; never the team-only one.
        Assert.Equal(2, rows.Count(r => r["source"]!.GetValue<string>() == "Audit"));
        // What she was told, and what she handed back.
        Assert.Contains(rows, r => r["event"]!.GetValue<string>() == "instruction from web/Manager");
        Assert.Contains(rows, r => r["event"]!.GetValue<string>() == "agent.handback");
        Assert.Equal(rows.Count, page["total"]!.GetValue<long>());
        // Newest first by default.
        var times = rows.Select(r => r["at"]!.GetValue<string>()).ToList();
        Assert.Equal(times.OrderDescending(StringComparer.Ordinal), times);
    }

    [Fact]
    public void TheManagersActivity_IncludesTheInstructionsItSent()
    {
        var rows = Rows(Q(new LogQuery(LogSources.Activity, Member: "web/Manager")));
        Assert.Contains(rows, r => r["event"]!.GetValue<string>() == "instruction to web/DeveloperAda");
    }

    [Fact]
    public void FiltersSearchAndSort()
    {
        var denied = Rows(Q(new LogQuery("gateway", Filters: new Dictionary<string, string> { ["kind"] = "connect-denied" })));
        Assert.Equal("evil.example:443", Assert.Single(denied)["target"]!.GetValue<string>());

        Assert.Single(Rows(Q(new LogQuery("audit", Text: "VIEWER@example"))));
        Assert.Empty(Rows(Q(new LogQuery("audit", Text: "100%_"))));

        var asc = Rows(Q(new LogQuery("runs", Sort: "duration", Descending: false)));
        Assert.Equal(["docs", "web"], asc.Select(r => r["team"]!.GetValue<string>()));
        Assert.Equal(61.0, asc[1]["duration"]!.GetValue<double>());

        Assert.Equal(["web"], Rows(Q(new LogQuery("runs", Team: "web"))).Select(r => r["team"]!.GetValue<string>()));
        // The audit entries that name the team: by name with a member, and by id.
        Assert.Equal(2, Rows(Q(new LogQuery("audit", Team: "web"))).Count);

        var since = Rows(Q(new LogQuery("audit", From: Ago(2.5))));
        Assert.Equal(2, since.Count);
    }

    [Fact]
    public void PagesFollowOneSnapshot_AndSayWhereTheNextStarts()
    {
        var first = Q(new LogQuery("audit", Limit: 2));
        Assert.Equal(4, first["total"]!.GetValue<long>());
        Assert.Equal(2, first["next"]!.GetValue<int>());
        // An entry written after the first page is not in the later pages of that list.
        Exec("INSERT INTO audit_log (at, via, method, outcome) VALUES (@a, 'app', 'teams.resume', 'done');", ("@a", Now.AddMinutes(1).ToString("O")));
        var second = Q(new LogQuery("audit", Limit: 2, Offset: 2, AsOf: first["asOf"]!.GetValue<string>()));
        Assert.Equal(2, Rows(second).Count);
        Assert.Null(second["next"]);
        Assert.Null(second["total"]);
        var ids = Rows(first).Concat(Rows(second)).Select(r => r["_id"]!.GetValue<string>()).ToList();
        Assert.Equal(ids.Distinct(), ids);
    }

    [Fact]
    public void FilterValues_AreTheOnesInTheLog()
    {
        Assert.Equal(["forward", "connect-denied"], _logs.Values("gateway", "kind").Select(v => v!.GetValue<string>()));
        Assert.Equal(["connect-denied", "forward"], _logs.Values("gateway", "kind", member: "web/DeveloperAda").Select(v => v!.GetValue<string>()).Order());
        Assert.Throws<ArgumentException>(() => _logs.Values("gateway", "detail"));
    }

    [Fact]
    public void PruningSaysWhatGoes_ThenDeletesOnlyThat()
    {
        var cutoff = _logs.CutoffFor(365);
        var preview = _logs.PrunePreview("audit", cutoff);
        Assert.Equal(1, preview.Count);
        Assert.Equal(Ago(500), preview.Oldest);
        Assert.Contains("who changed what", preview.Loss);
        Assert.Equal(1, _logs.Prune("audit", cutoff));
        Assert.Equal(3, Q(new LogQuery("audit"))["total"]!.GetValue<long>());

        // A request still waiting for an answer is never pruned, however old.
        Assert.Equal(0, _logs.PrunePreview("approvals", "9999-12-31").Count - 1);
        _logs.Prune("approvals", "9999-12-31");
        Assert.Equal("pending", Assert.Single(Rows(Q(new LogQuery("approvals"))))["state"]!.GetValue<string>());

        // Team messages and the board's history are kept.
        Assert.Throws<ArgumentException>(() => _logs.PrunePreview("messages", cutoff));
        Assert.Throws<ArgumentException>(() => _logs.Prune(LogSources.Activity, cutoff));
    }

    [Fact]
    public void PruningTranscripts_DeletesTheirFiles()
    {
        var old = Path.Combine(_db.Dir, "transcripts", "web", "DeveloperAda", "run-2.log");
        var preview = _logs.PrunePreview("transcripts", _logs.CutoffFor(30));
        Assert.Equal(1, preview.Count);
        Assert.Equal(1, preview.Files);
        Assert.Equal(1, _logs.Prune("transcripts", _logs.CutoffFor(30)));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(Path.Combine(_db.Dir, "transcripts", "web", "DeveloperAda", "run-1.log")));
    }

    [Fact]
    public void Retention_DefaultsSetAndSweep()
    {
        Assert.Equal(365, _logs.RetentionDays("audit"));
        Assert.Equal(30, _logs.RetentionDays("transcripts"));
        Assert.Equal(14, _logs.RetentionDays("diagnostics"));
        _logs.SetRetention("gateway", 0);
        Assert.Equal(0, _logs.RetentionDays("gateway"));
        Assert.Throws<ArgumentException>(() => _logs.SetRetention("messages", 30));
        Assert.Throws<ArgumentException>(() => _logs.SetRetention("audit", -1));

        var swept = _logs.Sweep();
        Assert.Equal(1, swept["audit"]); // the 500-day-old entry
        Assert.False(swept.ContainsKey("gateway")); // kept forever now: its 400-day-old entry stays
        Assert.Equal(2, swept["transcripts"]); // the 40-day-old one, and the one whose file is gone
        Assert.Equal(3, Q(new LogQuery("gateway"))["total"]!.GetValue<long>());
    }

    [Fact]
    public void Diagnostics_AreWrittenByTheTee_ReadFilteredAndPrunedByDay()
    {
        var log = new DiagnosticLog(_db.Dir, "core", _clock);
        using (var tee = log.Tee(TextWriter.Null))
        {
            tee.WriteLine("web/DeveloperAda: cloned the team repository");
            tee.Write("Logs: pruned 3 entries\nsecond ");
            tee.WriteLine("line");
        }
        _clock.Advance(TimeSpan.FromDays(20));
        new DiagnosticLog(_db.Dir, "shell", _clock).Write("the core exited (1)");

        var all = Rows(Q(new LogQuery("diagnostics", AsOf: "9999")));
        Assert.Equal(4, all.Count);
        Assert.Equal("the core exited (1)", all[0]["text"]!.GetValue<string>());
        Assert.Single(Rows(Q(new LogQuery("diagnostics", Member: "web/DeveloperAda", AsOf: "9999"))));
        Assert.Single(Rows(Q(new LogQuery("diagnostics", Filters: new Dictionary<string, string> { ["origin"] = "shell" }, AsOf: "9999"))));

        var preview = new LogService(_db.Dir, _db.ConnectionString, _clock).PrunePreview("diagnostics", new LogService(_db.Dir, _db.ConnectionString, _clock).CutoffFor(14));
        Assert.Equal(3, preview.Count);
        Assert.Equal(3, new LogService(_db.Dir, _db.ConnectionString, _clock).Prune("diagnostics", preview.Cutoff));
        Assert.Single(Directory.GetFiles(Path.Combine(_db.Dir, "logs")));
    }

    [Fact]
    public void TheCloudLinkFile_IsReadAndPrunedInPlace()
    {
        File.WriteAllText(Path.Combine(_db.Dir, "cloud-link-audit.log"),
            $"{Ago(400)}\tviewer@example.com\tbus.tellManager\tdone\n{Ago(1)}\tviewer@example.com\tteams.pause\trefused: no\n");
        Assert.Equal(2, Q(new LogQuery("cloudlink"))["total"]!.GetValue<long>());
        Assert.Equal(1, _logs.Prune("cloudlink", _logs.CutoffFor(365)));
        Assert.Equal("teams.pause", Assert.Single(Rows(Q(new LogQuery("cloudlink"))))["method"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- calls

    [Fact]
    public async Task OnlyThePersonsWindowPrunesOrSetsRetention_AndPruningNeedsAYes()
    {
        var rpc = new LogRpc(_logs);
        var ct = TestContext.Current.CancellationToken;
        using (Actor.Use(new Actor(Actor.Cloud, "viewer@example.com")))
        {
            Assert.NotNull(await rpc.DispatchAsync("logs.query", new JsonObject { ["source"] = "audit" }, ct));
            foreach (var m in new[] { "logs.prunePreview", "logs.prune", "logs.setRetention" })
                Assert.Equal("forbidden", (await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync(m, new JsonObject { ["source"] = "audit", ["all"] = true }, ct))).Code);
        }
        using (Actor.Use(Actor.Window))
        {
            var preview = (JsonObject)(await rpc.DispatchAsync("logs.prunePreview", new JsonObject { ["source"] = "audit", ["olderThanDays"] = 365 }, ct))!;
            Assert.Equal(1, preview["count"]!.GetValue<long>());
            Assert.Equal("needs_confirm", (await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("logs.prune", new JsonObject { ["source"] = "audit", ["olderThanDays"] = 365 }, ct))).Code);
            var done = (JsonObject)(await rpc.DispatchAsync("logs.prune", new JsonObject { ["source"] = "audit", ["before"] = preview["cutoff"]!.GetValue<string>(), ["confirm"] = true }, ct))!;
            Assert.Equal(1, done["deleted"]!.GetValue<long>());
            Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("logs.query", new JsonObject { ["source"] = "nope" }, ct))).Code);
        }
    }

    [Fact]
    public void ACloudViewerReadsLogs_NeverChangesThem()
    {
        Assert.Null(RemoteAccess.Refusal("logs.query", null, allowHostTerminals: false));
        Assert.Null(RemoteAccess.Refusal("logs.sources", null, allowHostTerminals: false));
        Assert.Equal(LogRpc.LocalOnly, RemoteAccess.Refusal("logs.prune", null, allowHostTerminals: true));
        Assert.Equal(LogRpc.LocalOnly, RemoteAccess.Refusal("logs.setRetention", null, allowHostTerminals: true));
    }

    [Fact]
    public void TheAuditKeepsChangesOnly()
    {
        foreach (var read in new[] { "integrations.asks", "monitor.steps", "bus.workflows", "logs.query", "backlog.item", "agents.versionStatus" })
            Assert.True(AuditLog.IsReadOnly(read, null), read);
        Assert.True(AuditLog.IsReadOnly("teams.push", new JsonObject { ["teamId"] = 1 }));
        Assert.False(AuditLog.IsReadOnly("teams.push", new JsonObject { ["teamId"] = 1, ["on"] = false }));
        Assert.False(AuditLog.IsReadOnly("teams.pause", null));
        Assert.False(AuditLog.IsReadOnly("logs.prune", null));
    }
}
