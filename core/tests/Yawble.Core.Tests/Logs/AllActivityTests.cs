using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Ipc;
using Yawble.Core.Logs;
using Yawble.Core.Provenance;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Logs;

/// <summary>
/// All activity takes every audit row. Its member column is the member a row names, "&lt;Team&gt;" for
/// a row about a team, or "app" (team empty) for a row about neither; the member filter keeps a member's own rows, the
/// team filter adds the team's rows, app: true keeps the app's. Names are written into the target when the row is
/// written, so a row about a team or member deleted since still reads; per-member actions by their own id (a trigger's
/// Run now) carry their member. logs.tail delivers the same rows. Over a real migrated database; every row is made up here.
/// </summary>
public sealed class AllActivityTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDb _db = new();
    private readonly LogService _logs;
    private readonly AuditLog _audit;
    private readonly Clock _clock = new(Now);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    public AllActivityTests()
    {
        _logs = new LogService(_db.Dir, _db.ConnectionString, _clock);
        _audit = new AuditLog(_db.ConnectionString, _clock);
        Exec("INSERT INTO teams (id, name, created_at) VALUES (1, 'web', @t), (8, 'Pipe Sessions', @t);", ("@t", Ago(10)));
        Exec("INSERT INTO members (id, team_id, name, role, cli, instructions, created_at) VALUES (10, 1, 'Manager', 'manager', 'claude', '', @t), (11, 1, 'DeveloperAda', 'member', 'claude', '', @t), (17, 8, 'DeveloperX', 'member', 'claude', '', @t);", ("@t", Ago(10)));
    }

    public void Dispose() => _db.Dispose();

    private static string Ago(double minutes) => (Now - TimeSpan.FromMinutes(minutes)).ToString("O");

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private string? Scalar(string sql)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    private static readonly Actor Pat = new(Actor.App, "pat");

    /// <summary>Records a call the way the dispatcher does: the names read first, then the row.</summary>
    private void Call(string method, JsonObject p, string outcome = "done", Action? meanwhile = null)
    {
        var names = _audit.Names(method, p);
        meanwhile?.Invoke();
        _audit.Record(Pat, method, p, outcome, names);
        _clock.Advance(TimeSpan.FromSeconds(1));
    }

    private List<JsonObject> Audit(LogQuery q) =>
        _logs.Query(q with { Limit = 200, Filters = new Dictionary<string, string> { ["source"] = "Audit" } })["rows"]!.AsArray()
            .Select(r => r!.AsObject()).ToList();

    private static string? S(JsonObject row, string key) => row[key] is JsonValue v ? v.GetValue<string>() : null;

    private static (string? Team, string? Member, string? Event) Who(JsonObject r) => (S(r, "team"), S(r, "member"), S(r, "event"));

    private void SeedThree()
    {
        Call("members.saveSettings", new JsonObject { ["memberId"] = 11 });
        Call("teams.pause", new JsonObject { ["teamId"] = 1 });
        Call("settings.save", new JsonObject { ["mode"] = "auto" });
    }

    [Fact]
    public void All_activity_has_a_member_a_team_and_an_app_audit_row_with_their_member_column()
    {
        SeedThree();
        var rows = Audit(new LogQuery(LogSources.Activity));
        Assert.Contains(("web", "DeveloperAda", "members.saveSettings"), rows.Select(Who));
        Assert.Contains(("web", "<web>", "teams.pause"), rows.Select(Who));
        Assert.Contains(((string?)null, "app", "settings.save"), rows.Select(Who));
        Assert.Equal(3, rows.Count);
        // Every audit row is in All activity: as many as the Audit log has.
        Assert.Equal(_logs.Query(new LogQuery("audit"))["total"]!.GetValue<long>(), rows.Count);
    }

    [Fact]
    public void The_member_filter_keeps_that_members_rows_only()
    {
        SeedThree();
        var rows = Audit(new LogQuery(LogSources.Activity, Member: "web/DeveloperAda"));
        Assert.Equal(["members.saveSettings"], rows.Select(r => S(r, "event")));
    }

    [Fact]
    public void The_team_filter_takes_its_members_rows_and_its_team_rows_not_the_apps()
    {
        SeedThree();
        Call("teams.pause", new JsonObject { ["teamId"] = 8 });
        var rows = Audit(new LogQuery(LogSources.Activity, Team: "web"));
        Assert.Equal(["members.saveSettings", "teams.pause"], rows.Select(r => S(r, "event")).Order());
        Assert.All(rows, r => Assert.Equal("web", S(r, "team")));
        // The team column's values offer the team, and no empty one for the app's rows.
        Assert.Equal(["Pipe Sessions", "web"], _logs.Values(LogSources.Activity, "team").Select(v => v!.GetValue<string>()).Order());
    }

    [Fact]
    public void The_app_filter_keeps_the_apps_rows_only_and_the_sources_say_who_offers_it()
    {
        SeedThree();
        Call("backups.now", new JsonObject());
        var rows = _logs.Query(new LogQuery(LogSources.Activity, App: true, Limit: 200))["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Equal(["backups.now", "settings.save"], rows.Select(r => S(r, "event")).Order());
        Assert.All(rows, r => Assert.Equal(((string?)null, "app"), (S(r, "team"), S(r, "member"))));
        // Only the activity log has app rows: another source with app: true has none.
        Assert.Empty(_logs.Query(new LogQuery("audit", App: true))["rows"]!.AsArray());
        Assert.Empty(_logs.Query(new LogQuery("diagnostics", App: true))["rows"]!.AsArray());
        var sources = _logs.Sources().Select(s => s!.AsObject()).ToList();
        Assert.True(sources.Single(s => S(s, "id") == LogSources.Activity)!["app"]!.GetValue<bool>());
        Assert.False(sources.Single(s => S(s, "id") == "audit")!["app"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Logs_query_and_values_take_app_over_rpc()
    {
        SeedThree();
        var rpc = new LogRpc(_logs);
        var page = (JsonObject)(await rpc.DispatchAsync("logs.query", new JsonObject { ["source"] = LogSources.Activity, ["app"] = true }, TestContext.Current.CancellationToken))!;
        Assert.Equal("settings.save", S(Assert.Single(page["rows"]!.AsArray())!.AsObject(), "event"));
        var values = (JsonArray)(await rpc.DispatchAsync("logs.values", new JsonObject { ["source"] = LogSources.Activity, ["column"] = "source", ["app"] = true }, TestContext.Current.CancellationToken))!;
        Assert.Equal(["Audit"], values.Select(v => v!.GetValue<string>()));
        await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("logs.query", new JsonObject { ["source"] = LogSources.Activity, ["app"] = "yes" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_row_about_a_deleted_team_and_its_member_keeps_their_names()
    {
        Call("terminal.stop", new JsonObject { ["memberId"] = 17 });
        Call("triggers.save", new JsonObject { ["teamId"] = 8, ["memberId"] = 17, ["name"] = "nightly" });
        // teams.delete: its names are read before the call removes the team.
        Call("teams.delete", new JsonObject { ["teamId"] = 8 }, meanwhile: () => Exec("DELETE FROM members WHERE team_id = 8; DELETE FROM teams WHERE id = 8;"));

        Assert.Equal("memberId=17 member=Pipe Sessions/DeveloperX", Scalar("SELECT target FROM audit_log WHERE method = 'terminal.stop';"));
        Assert.Equal("teamId=8 team=Pipe Sessions", Scalar("SELECT target FROM audit_log WHERE method = 'teams.delete';"));
        Assert.Equal("teamId=8 memberId=17 name=nightly team=Pipe Sessions member=Pipe Sessions/DeveloperX", Scalar("SELECT target FROM audit_log WHERE method = 'triggers.save';"));

        var rows = Audit(new LogQuery(LogSources.Activity)).Select(Who).ToList();
        Assert.Contains(("Pipe Sessions", "DeveloperX", "terminal.stop"), rows);
        Assert.Contains(("Pipe Sessions", "DeveloperX", "triggers.save"), rows);
        Assert.Contains(("Pipe Sessions", "<Pipe Sessions>", "teams.delete"), rows);
        // And its filters still find them by the names.
        Assert.Equal(3, Audit(new LogQuery(LogSources.Activity, Team: "Pipe Sessions")).Count);
        Assert.Equal(2, Audit(new LogQuery(LogSources.Activity, Member: "Pipe Sessions/DeveloperX")).Count);
    }

    [Fact]
    public void An_old_row_about_a_deleted_id_shows_the_id_and_no_name_and_teams_create_is_its_teams()
    {
        Exec("INSERT INTO audit_log (at, via, user_name, method, target, outcome) VALUES (@a, 'app', 'pat', 'terminal.stop', 'memberId=99', 'done'), (@a, 'app', 'pat', 'teams.delete', 'teamId=98', 'done'), (@a, 'app', 'pat', 'teams.create', 'name=Pipe Sessions', 'done'), (@a, 'app', 'pat', 'teams.setEnv', 'team=web member=DeveloperAda', 'done');", ("@a", Ago(1)));
        var rows = Audit(new LogQuery(LogSources.Activity));
        var old = rows.Single(r => S(r, "event") == "terminal.stop");
        Assert.Equal(((string?)null, (string?)null), (S(old, "team"), S(old, "member")));
        Assert.Contains("memberId=99", S(old, "details"));
        Assert.Equal(((string?)null, (string?)null), (S(rows.Single(r => S(r, "event") == "teams.delete"), "team"), S(rows.Single(r => S(r, "event") == "teams.delete"), "member")));
        Assert.Equal(("Pipe Sessions", "<Pipe Sessions>"), (S(rows.Single(r => S(r, "event") == "teams.create"), "team"), S(rows.Single(r => S(r, "event") == "teams.create"), "member")));
        Assert.Equal(("web", "DeveloperAda"), (S(rows.Single(r => S(r, "event") == "teams.setEnv"), "team"), S(rows.Single(r => S(r, "event") == "teams.setEnv"), "member")));
    }

    [Fact]
    public void Triggers_run_now_carries_its_member()
    {
        Exec("INSERT INTO triggers (id, team_id, member_id, name, kind, interval_sec, instruction, created_at, updated_at) VALUES (1, 8, 17, 'nightly', 'every', 3600, 'check', @t, @t);", ("@t", Ago(5)));
        Call("triggers.runNow", new JsonObject { ["id"] = 1 });
        Assert.Equal("id=1 memberId=17 member=Pipe Sessions/DeveloperX", Scalar("SELECT target FROM audit_log WHERE method = 'triggers.runNow';"));
        var row = Assert.Single(Audit(new LogQuery(LogSources.Activity, Member: "Pipe Sessions/DeveloperX")));
        Assert.Equal("triggers.runNow", S(row, "event"));

        // A trigger deleted by the call still gets its member, read before.
        Call("triggers.delete", new JsonObject { ["id"] = 1 }, meanwhile: () => Exec("DELETE FROM triggers WHERE id = 1;"));
        Assert.Equal("id=1 memberId=17 member=Pipe Sessions/DeveloperX", Scalar("SELECT target FROM audit_log WHERE method = 'triggers.delete';"));
        // An id of something that is not a member's (a backlog item) gets nothing added.
        Call("backlog.delete", new JsonObject { ["id"] = 1 });
        Assert.Equal("id=1", Scalar("SELECT target FROM audit_log WHERE method = 'backlog.delete';"));
    }

    [Fact]
    public async Task The_dispatcher_writes_the_names_read_before_the_call()
    {
        var dispatcher = new RpcDispatcher(new SandboxPlatformInfo(), () => [], audit: _audit);
        await Assert.ThrowsAsync<RpcException>(() => dispatcher.DispatchAsAsync(Pat, "teams.delete", new JsonObject { ["teamId"] = 8 }, TestContext.Current.CancellationToken));
        Assert.Equal("teamId=8 team=Pipe Sessions", Scalar("SELECT target FROM audit_log WHERE method = 'teams.delete';"));
    }

    [Fact]
    public async Task Logs_tail_delivers_a_new_app_level_audit_row_and_its_filters_place_it()
    {
        var events = new AppEvents();
        var seen = new ConcurrentQueue<JsonObject>();
        using var listen = events.Subscribe(m => { seen.Enqueue(m); return Task.CompletedTask; });
        using var tail = new LogTail(events, since => _logs.TailSince(since), _clock, TimeSpan.FromHours(1));
        var all = tail.Start(new LogTailFilter(All: true), Pat);
        var app = tail.Start(new LogTailFilter(All: true, App: true), Pat);
        var web = tail.Start(new LogTailFilter(All: true, Team: "web"), Pat);
        var ada = tail.Start(new LogTailFilter(All: true, Member: "web/DeveloperAda"), Pat);
        _clock.Advance(TimeSpan.FromSeconds(1));
        SeedThree();
        tail.PollOnce();

        var mark = Guid.NewGuid().ToString();
        events.Publish("test.mark", mark);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!seen.Any(m => m["event"]?.GetValue<string>() == "test.mark" && m["data"]?.GetValue<string>() == mark))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the events were not delivered");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        var pushed = seen.Where(m => m["event"]?.GetValue<string>() == LogTail.EntryEvent).Select(m => (JsonObject)m["data"]!).ToList();
        List<string?> Of(string id) => [.. pushed.Where(d => S(d, "tail") == id).Select(d => S(d["entry"]!.AsObject(), "event")).Order()];

        Assert.Equal(["members.saveSettings", "settings.save", "teams.pause"], Of(all));
        Assert.Equal(["settings.save"], Of(app));
        Assert.Equal(["members.saveSettings", "teams.pause"], Of(web));
        Assert.Equal(["members.saveSettings"], Of(ada));
        var entry = pushed.First(d => S(d, "tail") == app)["entry"]!.AsObject();
        Assert.Equal(((string?)null, "app", "Audit"), (S(entry, "team"), S(entry, "member"), S(entry, "source")));
        var team = pushed.First(d => S(d, "tail") == web && S(d["entry"]!.AsObject(), "event") == "teams.pause")["entry"]!.AsObject();
        Assert.Equal(("web", "<web>"), (S(team, "team"), S(team, "member")));
    }

    [Fact]
    public void Who_of_a_target_reads_names_with_spaces()
    {
        Assert.Equal("Pipe Sessions/DeveloperX", AuditLog.WhoOf("team=Pipe Sessions member=DeveloperX id=4", "teams.setEnv", null, null));
        Assert.Equal("Pipe Sessions/<Pipe Sessions>", AuditLog.WhoOf("teamId=8 team=Pipe Sessions", "teams.delete", null, null));
        Assert.Equal("Yawble Engineering Team/<Yawble Engineering Team>", AuditLog.WhoOf("team=Yawble Engineering Team item=3", "companion.backlog_add", null, null));
        Assert.Equal(AuditLog.AppKey, AuditLog.WhoOf(null, "settings.save", null, null));
        Assert.Equal(AuditLog.AppKey, AuditLog.WhoOf("id=1", "backlog.delete", null, null));
        Assert.Equal(AuditLog.AppKey, AuditLog.WhoOf("name=nightly", "teams.import", null, null));
    }
}
