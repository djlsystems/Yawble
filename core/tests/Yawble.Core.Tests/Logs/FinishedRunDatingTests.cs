using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Link;
using Yawble.Core.Logs;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Logs;

/// <summary>
/// A run's Activity row is dated by its end once it has ended (by its start while it runs), so a run that started
/// two hours ago and failed just now is one of org.summary's problems of the last hour, and an open logs.tail pushes it
/// once. Over a real migrated database; the runs are made up here.
/// </summary>
public sealed class FinishedRunDatingTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly AppEvents _events = new();
    private readonly ConcurrentQueue<JsonObject> _seen = new();
    private readonly IDisposable _listen;

    public FinishedRunDatingTests() => _listen = _events.Subscribe(m => { _seen.Enqueue(m); return Task.CompletedTask; });

    public void Dispose()
    {
        _listen.Dispose();
        _db.Dispose();
    }

    private static string Iso(DateTimeOffset t) => t.ToString("O");

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void RunStarted(string key, DateTimeOffset at) =>
        Exec("INSERT INTO run_records (run_key, team, member, cli, kind, backend, started_at) VALUES (@k, 'web', 'DeveloperAda', 'claude', 'headless', 'wxc', @s);",
            ("@k", key), ("@s", Iso(at)));

    private void RunEnded(string key, string endClass, DateTimeOffset at) =>
        Exec("UPDATE run_records SET end_class = @e, exit_code = 1, ended_at = @at WHERE run_key = @k;", ("@e", endClass), ("@at", Iso(at)), ("@k", key));

    [Fact]
    public async Task A_run_started_two_hours_ago_that_failed_just_now_is_an_org_summary_problem()
    {
        var now = DateTimeOffset.UtcNow;
        RunStarted("r-old-fail", now.AddHours(-2));
        RunEnded("r-old-fail", "failed", now.AddSeconds(-5));
        // Still running since two hours ago: not a problem, and its row keeps its start.
        RunStarted("r-still-running", now.AddHours(-2));

        var logs = new LogService(_db.Dir, _db.ConnectionString);
        await using var bus = new BusEngine(_db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false });
        var source = new AppOrgSummarySource(bus, null, null, null, null, logs, () => null, now);
        var facts = await source.ReadAsync(Ct);

        var problem = Assert.Single(facts.Problems, p => p.Source == "Runs");
        Assert.Equal(("web", "DeveloperAda", "error"), (problem.Team, problem.Member, problem.Level));
        Assert.StartsWith("run failed", problem.Message);
        Assert.True(now - problem.At < TimeSpan.FromMinutes(1), $"dated {problem.At:O}, by its end");

        // logs.query: the finished run sits where it ended, the running one where it started.
        var rows = logs.Query(new LogQuery(LogSources.Activity, Sort: "at", Descending: false))["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
        var runs = rows.Where(r => r["source"]?.ToString() == "Runs").Select(r => (r["event"]!.ToString(), r["at"]!.ToString())).ToList();
        Assert.Equal([("run running", Iso(now.AddHours(-2))), ("run failed", Iso(now.AddSeconds(-5)))], runs);
    }

    [Fact]
    public async Task A_run_that_ends_after_a_tail_opens_is_pushed_once()
    {
        var logs = new LogService(_db.Dir, _db.ConnectionString);
        using var tail = new LogTail(_events, since => logs.TailSince(since), interval: TimeSpan.FromHours(1));
        RunStarted("r-old-fail", DateTimeOffset.UtcNow.AddHours(-2));
        var id = tail.Start(new LogTailFilter(), Actor.CloudViewer("ada@example.com"));
        tail.PollOnce(); // still running, dated by its start before the tail opened: nothing
        Assert.Empty(await Pushed());

        RunEnded("r-old-fail", "failed", DateTimeOffset.UtcNow.AddSeconds(1));
        tail.PollOnce();
        tail.PollOnce();
        tail.PollOnce();

        var pushed = Assert.Single(await Pushed());
        Assert.Equal(id, pushed["tail"]!.GetValue<string>());
        var entry = pushed["entry"]!.AsObject();
        Assert.Equal(("Runs", "run failed", LogLevels.Error), (entry["source"]!.GetValue<string>(), entry["event"]!.GetValue<string>(), entry["level"]!.GetValue<string>()));
        Assert.Equal(("web", "DeveloperAda"), (entry["team"]!.GetValue<string>(), entry["member"]!.GetValue<string>()));
    }

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
}
