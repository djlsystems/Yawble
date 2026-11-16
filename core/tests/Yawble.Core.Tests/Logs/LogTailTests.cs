using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Logs;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Logs;

/// <summary>
/// logs.tail / logs.untail and the "logs.tailEntry" push: filtered on this computer by level, team and member,
/// redacted the way logs.query redacts, tagged per tail so two viewers each get only theirs, and stopped by untail,
/// by the viewer leaving and by the link dropping. Entries, viewers and the link's state are all made up here.
/// </summary>
public sealed class LogTailTests : IDisposable
{
    private static readonly string Secret = "ghp_" + new string('A', 30);
    private static readonly Actor Ada = Actor.CloudViewer("ada@example.com");
    private static readonly Actor Bob = Actor.CloudViewer("bob@example.com");

    private readonly AppEvents _events = new();
    private readonly ConcurrentQueue<JsonObject> _seen = new();
    private readonly IDisposable _listen;
    private readonly List<IDisposable> _dispose = [];

    public LogTailTests() => _listen = _events.Subscribe(m => { _seen.Enqueue(m); return Task.CompletedTask; });

    public void Dispose()
    {
        _listen.Dispose();
        foreach (var d in _dispose) d.Dispose();
    }

    private LogTail NewTail(Func<string, IReadOnlyList<(string, LogTailEntry)>>? read = null)
    {
        var t = new LogTail(_events, read, interval: TimeSpan.FromHours(1));
        _dispose.Add(t);
        return t;
    }

    private static LogTailEntry Entry(string level, string? team = "web", string? member = "DeveloperAda", string text = "something", bool workflow = false) =>
        new("2026-11-16T10:00:00.0000000+00:00", "Runs", level, team, member, "run", text, workflow);

    /// <summary>Every logs.tailEntry pushed so far, once the events published before now are delivered.</summary>
    private async Task<List<JsonObject>> Pushed()
    {
        var mark = Guid.NewGuid().ToString();
        _events.Publish("test.mark", mark);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!_seen.Any(m => m["event"]?.GetValue<string>() == "test.mark" && m["data"]?.GetValue<string>() == mark))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the events were not delivered");
            await Task.Delay(10);
        }
        return _seen.Where(m => m["event"]?.GetValue<string>() == LogTail.EntryEvent).Select(m => (JsonObject)m["data"]!).ToList();
    }

    private static async Task<JsonNode?> Call(LogRpc rpc, Actor who, string method, JsonObject? p = null)
    {
        using var _ = Actor.Use(who);
        return await rpc.DispatchAsync(method, p, CancellationToken.None);
    }

    [Fact]
    public async Task Default_level_passes_warnings_errors_and_workflow_events_only()
    {
        var tail = NewTail();
        var id = tail.Start(new LogTailFilter(), Ada);
        tail.Write(Entry(LogLevels.Error, text: "e"));
        tail.Write(Entry(LogLevels.Warning, text: "w"));
        tail.Write(Entry(LogLevels.Info, text: "i"));
        tail.Write(Entry(LogLevels.Info, text: "handback", workflow: true));
        var texts = (await Pushed()).Select(d => d["entry"]!["text"]!.GetValue<string>()).ToList();
        Assert.Equal(["e", "w", "handback"], texts);
        Assert.All(await Pushed(), d => Assert.Equal(id, d["tail"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Level_all_passes_every_level()
    {
        var tail = NewTail();
        tail.Start(new LogTailFilter(All: true), Ada);
        tail.Write(Entry(LogLevels.Info, text: "i"));
        tail.Write(Entry(LogLevels.Error, text: "e"));
        Assert.Equal(["i", "e"], (await Pushed()).Select(d => d["entry"]!["text"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Team_and_member_filters_pass_only_their_entries()
    {
        var tail = NewTail();
        tail.Start(new LogTailFilter(All: true, Team: "web"), Ada);
        tail.Write(Entry(LogLevels.Info, "web", "DeveloperAda", "web-ada"));
        tail.Write(Entry(LogLevels.Info, "docs", "Manager", "docs"));
        tail.Write(Entry(LogLevels.Info, null, null, "no team named"));
        Assert.Equal(["web-ada"], (await Pushed()).Select(d => d["entry"]!["text"]!.GetValue<string>()));

        var byMember = new LogTailFilter(All: true, Member: "web/DeveloperAda");
        Assert.True(byMember.Matches(Entry(LogLevels.Info, "WEB", "developerada")));
        Assert.False(byMember.Matches(Entry(LogLevels.Info, "web", "Manager")));
        Assert.False(byMember.Matches(Entry(LogLevels.Info, "docs", "DeveloperAda")));
        // The Diagnostics log names members in passing: matched by its text, as logs.query matches it.
        var diag = new LogTailEntry("t", "diagnostics", LogLevels.Info, null, null, "core", "team web: DeveloperAda cloned", MemberInText: true);
        Assert.True(byMember.Matches(diag));
        Assert.True(new LogTailFilter(All: true, Team: "web").Matches(diag));
        Assert.False(new LogTailFilter(All: true, Team: "docs").Matches(diag));
    }

    [Fact]
    public async Task A_secret_in_a_line_is_redacted_the_way_logs_query_redacts_it()
    {
        using var db = new TempDb();
        var tail = NewTail();
        tail.Start(new LogTailFilter(All: true), Ada);
        var diagnostics = new DiagnosticLog(db.Dir, "core") { Tail = tail };
        diagnostics.Write($"clone failed: https://x:{Secret}@example.com/repo");

        var entry = (JsonObject)Assert.Single(await Pushed())["entry"]!;
        var text = entry["text"]!.GetValue<string>();
        Assert.DoesNotContain(Secret, text);
        Assert.Contains("[redacted]", text);
        Assert.Equal("diagnostics", entry["source"]!.GetValue<string>());
        Assert.Equal(LogLevels.Error, entry["level"]!.GetValue<string>());

        // logs.query reads the very same line the very same way.
        var logs = new LogService(db.Dir, db.ConnectionString);
        var row = (JsonObject)Assert.Single(logs.Query(new LogQuery("diagnostics")).AsObject()["rows"]!.AsArray())!;
        Assert.Equal(text, row["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Nothing_is_pushed_after_logs_untail()
    {
        using var db = new TempDb();
        var tail = NewTail();
        var rpc = new LogRpc(new LogService(db.Dir, db.ConnectionString), tail);
        var id = (await Call(rpc, Ada, "logs.tail", new JsonObject { ["level"] = "all" }))!["tail"]!.GetValue<string>();
        tail.Write(Entry(LogLevels.Info, text: "before"));
        var stopped = await Call(rpc, Ada, "logs.untail", new JsonObject { ["tail"] = id });
        Assert.Equal(1, stopped!["stopped"]!.GetValue<int>());
        tail.Write(Entry(LogLevels.Error, text: "after"));
        Assert.Equal(["before"], (await Pushed()).Select(d => d["entry"]!["text"]!.GetValue<string>()));
        Assert.Equal(0, tail.Count);
    }

    [Fact]
    public async Task Another_viewer_cannot_untail_mine()
    {
        using var db = new TempDb();
        var tail = NewTail();
        var rpc = new LogRpc(new LogService(db.Dir, db.ConnectionString), tail);
        var id = (await Call(rpc, Ada, "logs.tail"))!["tail"]!.GetValue<string>();
        Assert.Equal(0, (await Call(rpc, Bob, "logs.untail", new JsonObject { ["tail"] = id }))!["stopped"]!.GetValue<int>());
        Assert.Equal(0, (await Call(rpc, Bob, "logs.untail"))!["stopped"]!.GetValue<int>());
        Assert.Equal(1, tail.Count);
    }

    [Fact]
    public async Task Nothing_is_pushed_after_the_viewer_disconnects()
    {
        var tail = NewTail();
        tail.Start(new LogTailFilter(All: true), Ada);
        tail.Start(new LogTailFilter(All: true), Bob);
        // The link's state as CloudLink publishes it: Ada has left, Bob still views.
        _events.Publish(CloudLink.StateEvent, new JsonObject { ["state"] = "connected", ["viewers"] = new JsonArray("bob@example.com") });
        await WaitFor(() => tail.Count == 1);
        tail.Write(Entry(LogLevels.Info, text: "for bob"));
        var pushed = Assert.Single(await Pushed());
        Assert.Equal("for bob", pushed["entry"]!["text"]!.GetValue<string>());

        // The link drops: every cloud tail ends, and nothing more goes up.
        _events.Publish(CloudLink.StateEvent, new JsonObject { ["state"] = "offline", ["viewers"] = new JsonArray() });
        await WaitFor(() => tail.Count == 0);
        tail.Write(Entry(LogLevels.Error, text: "after"));
        Assert.Single(await Pushed());
    }

    [Fact]
    public async Task Two_viewers_with_different_filters_each_get_only_theirs()
    {
        var tail = NewTail();
        var a = tail.Start(new LogTailFilter(All: true, Team: "web"), Ada);
        var b = tail.Start(new LogTailFilter(Team: "docs"), Bob);
        tail.Write(Entry(LogLevels.Info, "web", "DeveloperAda", "web info"));
        tail.Write(Entry(LogLevels.Info, "docs", "Manager", "docs info"));
        tail.Write(Entry(LogLevels.Error, "docs", "Manager", "docs error"));
        var pushed = await Pushed();
        Assert.Equal(["web info"], pushed.Where(d => d["tail"]!.GetValue<string>() == a).Select(d => d["entry"]!["text"]!.GetValue<string>()));
        Assert.Equal(["docs error"], pushed.Where(d => d["tail"]!.GetValue<string>() == b).Select(d => d["entry"]!["text"]!.GetValue<string>()));
    }

    [Fact]
    public async Task No_tail_means_no_work()
    {
        var reads = 0;
        var tail = NewTail(_ => { reads++; return []; });
        tail.PollOnce();
        tail.Write(Entry(LogLevels.Error));
        Assert.Equal(0, reads);
        Assert.Empty(await Pushed());

        tail.Start(new LogTailFilter(), Ada);
        tail.PollOnce();
        Assert.Equal(1, reads);
        tail.Stop(null, Ada);
        tail.PollOnce();
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task The_activity_read_pushes_each_new_entry_once()
    {
        var rows = new List<(string, LogTailEntry)>();
        var tail = NewTail(since => rows.Where(r => string.CompareOrdinal(r.Item2.At, since) >= 0).ToList());
        tail.Start(new LogTailFilter(All: true), Ada);
        var at = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O");
        rows.Add(("r1", Entry(LogLevels.Info, text: "one") with { At = at }));
        tail.PollOnce();
        rows.Add(("r2", Entry(LogLevels.Info, text: "two") with { At = at }));
        tail.PollOnce();
        tail.PollOnce();
        Assert.Equal(["one", "two"], (await Pushed()).Select(d => d["entry"]!["text"]!.GetValue<string>()));
    }

    [Fact]
    public void The_activity_read_leaves_model_usage_out_and_reads_levels_and_workflow_events()
    {
        using var db = new TempDb();
        void Exec(string sql)
        {
            using var c = new SqliteConnection(db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        const string At = "2026-11-16T10:00:00.0000000+00:00";
        Exec($"INSERT INTO usage_events (at, run_key, member, vhost, path, status, model, input_tokens, output_tokens) VALUES ('{At}', 'r-1', 'web/DeveloperAda', 'model.yawble.internal', '/v1/messages', 200, 'opus', 1200, 300);");
        Exec($"INSERT INTO gateway_events (run_id, member, at, kind, method, target, status, detail) VALUES (1, 'web/DeveloperAda', '{At}', 'connect-denied', 'CONNECT', 'evil.example:443', 403, 'not an allowed host');");
        Exec($"INSERT INTO messages (type, payload, source, depth, occurred_at, via) VALUES ('agent.handback', '{{\"delivered\":\"done {Secret}\"}}', 'web/DeveloperAda', 0, '{At}', 'agent'), ('agent.progress', '{{}}', 'web/DeveloperAda', 0, '{At}', 'agent');");

        var rows = new LogService(db.Dir, db.ConnectionString).TailSince("2026-11-16T00:00:00");
        Assert.DoesNotContain(rows, r => r.Entry.Source == "Model usage");
        var network = Assert.Single(rows, r => r.Entry.Source == "Network").Entry;
        Assert.Equal(("web", "DeveloperAda", LogLevels.Warning), (network.Team, network.Member, network.Level));
        var handback = Assert.Single(rows, r => r.Entry.Event == "agent.handback").Entry;
        Assert.True(handback.Workflow);
        Assert.DoesNotContain(Secret, handback.ToJson()["text"]!.GetValue<string>());
        Assert.False(Assert.Single(rows, r => r.Entry.Event == "agent.progress").Entry.Workflow);
        Assert.Empty(new LogService(db.Dir, db.ConnectionString).TailSince("2026-11-17"));
    }

    [Fact]
    public void The_entry_carries_no_token_cost_or_usage_field()
    {
        var json = Entry(LogLevels.Error).ToJson();
        Assert.Equal(["at", "source", "level", "team", "member", "event", "text", "workflow"], json.Select(kv => kv.Key));
        Assert.DoesNotContain(json, kv => new[] { "token", "cost", "usage", "price" }.Any(w => kv.Key.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Bad_params_are_refused()
    {
        using var db = new TempDb();
        var rpc = new LogRpc(new LogService(db.Dir, db.ConnectionString), NewTail());
        var e = await Assert.ThrowsAsync<RpcException>(() => Call(rpc, Ada, "logs.tail", new JsonObject { ["level"] = "debug" }));
        Assert.Equal("bad_params", e.Code);
        e = await Assert.ThrowsAsync<RpcException>(() => Call(rpc, Ada, "logs.tail", new JsonObject { ["member"] = "DeveloperAda" }));
        Assert.Equal("bad_params", e.Code);
        var unwired = new LogRpc(new LogService(db.Dir, db.ConnectionString));
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<RpcException>(() => Call(unwired, Ada, "logs.tail"))).Code);
    }

    [Fact]
    public void A_viewer_reopening_past_the_limit_ends_their_oldest_tail()
    {
        var tail = NewTail();
        for (var i = 0; i < LogTail.MaxPerViewer + 2; i++) tail.Start(new LogTailFilter(), Ada);
        tail.Start(new LogTailFilter(), Bob);
        Assert.Equal(LogTail.MaxPerViewer + 1, tail.Count);
    }

    [Theory]
    [InlineData(null, "Cloud Link: connection ended: reset", LogLevels.Info)]
    [InlineData(null, "team web: clone failed: exit 128", LogLevels.Error)]
    [InlineData(null, "Cloud Link: audit log not written: busy", LogLevels.Warning)]
    [InlineData("run failed", "claude", LogLevels.Error)]
    [InlineData("run stopped", "claude", LogLevels.Warning)]
    [InlineData("run completed", "claude", LogLevels.Info)]
    [InlineData("connect-denied", "CONNECT evil.example:443", LogLevels.Warning)]
    public void Levels_are_read_from_what_the_entry_says(string? ev, string text, string level) =>
        Assert.Equal(level, LogLevels.Of(ev, text));

    private static async Task WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
