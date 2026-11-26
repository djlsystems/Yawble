using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Logs;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Logs;

/// <summary>
/// All activity end to end, through the real UiRpc behind the audited RpcDispatcher: a team made, its member's trigger
/// saved and run, the team paused and deleted; then All activity's rows, its newest row, its member/team/app filters.
/// Also: an id-only row about a deleted thing matches no filter; org.summary's problems survive a flood of audit rows
/// in the hour; logs.tail and logs.query take app over the wire and refuse a bad value with bad_params; the per-source
/// Audit view keeps its columns. The rules row by row are in AllActivityTests.
/// </summary>
public sealed class AllActivityEndToEndTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Actor Pat = new(Actor.App, "pat");

    private readonly TempDb _db = new();
    private BusEngine? _engine;

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null) await _engine.DisposeAsync();
        _db.Dispose();
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    private static string? S(JsonObject row, string key) => row[key] is JsonValue v ? v.GetValue<string>() : null;

    private static List<JsonObject> Rows(JsonObject page) => page["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();

    private async Task<RpcDispatcher> RigAsync()
    {
        _engine = new BusEngine(_db.OpenStore(), new ScriptedRunner(), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        await _engine.StartAsync(Ct);
        var terminals = new TerminalHub(new AppEvents());
        var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
        var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
        var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
        var ui = new UiRpc(_engine, new PolicyStore(_db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
            new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, _engine), terminals, 16_384);
        return new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui, audit: new AuditLog(_db.ConnectionString));
    }

    /// <summary>A call as the window makes it (audited); a refusal is fine, it is audited too.</summary>
    private static async Task Call(RpcDispatcher rpc, string method, JsonObject p)
    {
        try { await rpc.DispatchAsAsync(Pat, method, p, Ct); }
        catch (RpcException) { }
        await Task.Delay(5, Ct); // distinct audit times
    }

    [Fact]
    public async Task A_deleted_teams_rows_keep_names_and_all_activity_takes_every_audit_row_end_to_end()
    {
        var rpc = await RigAsync();
        await _engine!.CreateTeamAsync("Pipe Sessions", ct: Ct);
        await _engine.AddMemberAsync("Pipe Sessions", "Ada", ct: Ct);
        var teamId = (await _engine.TeamsAsync(Ct)).Single().Id;
        var memberId = Convert.ToInt64(Scalar("SELECT id FROM members WHERE team_id = @t AND name LIKE '%Ada';", ("@t", teamId)));
        var memberKey = "Pipe Sessions/" + (string)Scalar("SELECT name FROM members WHERE id = @m;", ("@m", memberId))!;

        await Call(rpc, "triggers.save", new JsonObject { ["memberId"] = memberId, ["name"] = "nightly", ["kind"] = "every", ["intervalSec"] = 3600L, ["instruction"] = "check" });
        Assert.Equal("done", Scalar("SELECT outcome FROM audit_log WHERE method = 'triggers.save';"));
        var triggerId = Convert.ToInt64(Scalar("SELECT id FROM triggers WHERE member_id = @m;", ("@m", memberId)));
        await Call(rpc, "triggers.runNow", new JsonObject { ["id"] = triggerId });
        await Call(rpc, "terminal.stop", new JsonObject { ["memberId"] = memberId });
        await Call(rpc, "teams.pause", new JsonObject { ["teamId"] = teamId });
        await Wait.UntilAsync(() => _engine.IsQuiescentAsync(Ct), "quiet");
        await Call(rpc, "teams.delete", new JsonObject { ["teamId"] = teamId, ["confirm"] = "Pipe Sessions" });
        Assert.Equal(0L, Convert.ToInt64(Scalar("SELECT COUNT(*) FROM teams WHERE id = @t;", ("@t", teamId))));
        // An app-level call last: the newest thing that happened.
        await Call(rpc, "settings.save", new JsonObject { ["mode"] = "auto" });

        // Names were written at audit time, read before the delete.
        Assert.Equal($"id={triggerId} memberId={memberId} member={memberKey}", Scalar("SELECT target FROM audit_log WHERE method = 'triggers.runNow';"));
        Assert.Equal($"memberId={memberId} member={memberKey}", Scalar("SELECT target FROM audit_log WHERE method = 'terminal.stop';"));
        Assert.Equal($"teamId={teamId} team=Pipe Sessions", Scalar("SELECT target FROM audit_log WHERE method = 'teams.delete';"));

        var logs = new LogService(_db.Dir, _db.ConnectionString);
        var audit = Rows(logs.Query(new LogQuery(LogSources.Activity, Limit: 200, Filters: new Dictionary<string, string> { ["source"] = "Audit" })));
        Assert.Equal(Convert.ToInt64(Scalar("SELECT COUNT(*) FROM audit_log;")), audit.Count);
        var who = audit.Select(r => (S(r, "event"), S(r, "team"), S(r, "member"))).ToList();
        var name = memberKey["Pipe Sessions/".Length..];
        Assert.Contains(("triggers.save", "Pipe Sessions", name), who);
        Assert.Contains(("triggers.runNow", "Pipe Sessions", name), who);
        Assert.Contains(("terminal.stop", "Pipe Sessions", name), who);
        Assert.Contains(("teams.pause", "Pipe Sessions", "<Pipe Sessions>"), who);
        Assert.Contains(("teams.delete", "Pipe Sessions", "<Pipe Sessions>"), who);
        Assert.Contains(("settings.save", (string?)null, "app"), who);

        // The newest row of All activity (default order, nothing filtered) is the newest audit row.
        var newest = Rows(logs.Query(new LogQuery(LogSources.Activity, Limit: 1))).Single();
        Assert.Equal(("Audit", "settings.save"), (S(newest, "source"), S(newest, "event")));

        // Filters, by the names kept: the member's own rows; the team's rows plus its member's; the app's only.
        string?[] Events(LogQuery q) => [.. Rows(logs.Query(q with { Limit = 200, Filters = new Dictionary<string, string> { ["source"] = "Audit" } })).Select(r => S(r, "event")).Order()];
        Assert.Equal(["terminal.stop", "triggers.runNow", "triggers.save"], Events(new LogQuery(LogSources.Activity, Member: memberKey)));
        Assert.Equal(["teams.delete", "teams.pause", "terminal.stop", "triggers.runNow", "triggers.save"], Events(new LogQuery(LogSources.Activity, Team: "Pipe Sessions")));
        Assert.Equal(["settings.save"], Events(new LogQuery(LogSources.Activity, App: true)));
    }

    [Fact]
    public void An_old_id_only_row_about_a_deleted_thing_matches_no_filter()
    {
        Exec("INSERT INTO teams (id, name, created_at) VALUES (1, 'web', @t);", ("@t", DateTimeOffset.UtcNow.AddHours(-1).ToString("O")));
        Exec("INSERT INTO audit_log (at, via, user_name, method, target, outcome) VALUES (@a, 'app', 'pat', 'terminal.stop', 'memberId=99', 'done'), (@a, 'app', 'pat', 'teams.delete', 'teamId=98', 'done');",
            ("@a", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")));
        var logs = new LogService(_db.Dir, _db.ConnectionString);
        Assert.Equal(2, Rows(logs.Query(new LogQuery(LogSources.Activity))).Count);
        Assert.Empty(Rows(logs.Query(new LogQuery(LogSources.Activity, App: true))));
        Assert.Empty(Rows(logs.Query(new LogQuery(LogSources.Activity, Team: "web"))));
        Assert.Empty(Rows(logs.Query(new LogQuery(LogSources.Activity, Member: "web/DeveloperAda"))));
    }

    [Fact]
    public async Task Org_summary_problems_survive_a_busy_hour_of_audit_rows()
    {
        var now = DateTimeOffset.UtcNow;
        Exec("INSERT INTO run_records (run_key, team, member, cli, kind, backend, started_at, ended_at, end_class, exit_code) VALUES ('r1', 'web', 'DeveloperAda', 'claude', 'headless', 'wxc', @s, @e, 'failed', 1);",
            ("@s", now.AddMinutes(-30).ToString("O")), ("@e", now.AddMinutes(-20).ToString("O")));
        // More audit rows than a page, all newer than the failed run.
        for (var i = 0; i < LogService.MaxPage + 50; i++)
            Exec("INSERT INTO audit_log (at, via, user_name, method, target, outcome) VALUES (@a, 'app', 'pat', 'settings.save', NULL, 'done');",
                ("@a", now.AddMinutes(-10).AddMilliseconds(i).ToString("O")));
        var logs = new LogService(_db.Dir, _db.ConnectionString);
        await using var bus = new BusEngine(_db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false });
        var facts = await new AppOrgSummarySource(bus, null, null, null, null, logs, () => null, now).ReadAsync(Ct);
        var problem = Assert.Single(facts.Problems);
        Assert.Equal(("Runs", "web", "DeveloperAda", "error"), (problem.Source, problem.Team, problem.Member, problem.Level));
    }

    [Fact]
    public async Task Logs_tail_takes_app_over_the_wire_and_bad_values_are_bad_params()
    {
        var events = new AppEvents();
        var seen = new ConcurrentQueue<JsonObject>();
        using var listen = events.Subscribe(m => { seen.Enqueue(m); return Task.CompletedTask; });
        var logs = new LogService(_db.Dir, _db.ConnectionString);
        using var tail = new LogTail(events, since => logs.TailSince(since), interval: TimeSpan.FromHours(1));
        var rpc = new LogRpc(logs, tail);
        Exec("INSERT INTO teams (id, name, created_at) VALUES (1, 'web', @t);", ("@t", DateTimeOffset.UtcNow.AddHours(-1).ToString("O")));
        using (Actor.Use(Pat))
        {
            var started = (JsonObject)(await rpc.DispatchAsync("logs.tail", new JsonObject { ["level"] = "all", ["app"] = true }, Ct))!;
            var id = started["tail"]?.GetValue<string>() ?? started["id"]!.GetValue<string>();
            await Task.Delay(20, Ct);
            var audit = new AuditLog(_db.ConnectionString);
            audit.Record(Pat, "settings.save", new JsonObject { ["mode"] = "auto" }, "done");
            audit.Record(Pat, "teams.pause", new JsonObject { ["teamId"] = 1 }, "done");
            tail.PollOnce();

            var mark = Guid.NewGuid().ToString();
            events.Publish("test.mark", mark);
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!seen.Any(m => m["event"]?.GetValue<string>() == "test.mark" && m["data"]?.GetValue<string>() == mark))
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("the events were not delivered");
                await Task.Delay(10, Ct);
            }
            var pushed = seen.Where(m => m["event"]?.GetValue<string>() == LogTail.EntryEvent).Select(m => (JsonObject)m["data"]!)
                .Where(d => S(d, "tail") == id).Select(d => d["entry"]!.AsObject()).ToList();
            var entry = Assert.Single(pushed);
            Assert.Equal(("settings.save", (string?)null, "app"), (S(entry, "event"), S(entry, "team"), S(entry, "member")));

            var bad = await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("logs.tail", new JsonObject { ["level"] = "all", ["app"] = "yes" }, Ct));
            Assert.Equal("bad_params", bad.Code);
            var badQuery = await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("logs.query", new JsonObject { ["source"] = LogSources.Activity, ["app"] = 1 }, Ct));
            Assert.Equal("bad_params", badQuery.Code);
        }
    }

    [Fact]
    public void The_audit_view_keeps_its_columns_and_only_the_activity_log_offers_app()
    {
        var logs = new LogService(_db.Dir, _db.ConnectionString);
        var sources = logs.Sources().Select(s => s!.AsObject()).ToList();
        var auditSource = sources.Single(s => S(s, "id") == "audit");
        Assert.Equal(["at", "via", "user", "client", "method", "target", "outcome"],
            auditSource["columns"]!.AsArray().Select(c => c!["key"]!.GetValue<string>()));
        Assert.Equal([LogSources.Activity], sources.Where(s => s["app"]!.GetValue<bool>()).Select(s => S(s, "id")));
        Assert.All(sources, s => Assert.NotNull(s["app"]));
    }
}
