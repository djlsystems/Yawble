using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Logs;
using Xunit;

namespace Yawble.Core.Tests.Logs;

/// <summary>
/// All activity offers the Level filter: each row carries the level logs.tail gives the same entry (a failed run is an
/// error, a blocked message a warning, a progress message info), logs.query level picks one level, and logs.sources says
/// the source has levels. Over a real migrated temp database; every row is made up here.
/// </summary>
public sealed class ActivityLevelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDb _db = new();
    private readonly LogService _logs;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public ActivityLevelTests()
    {
        _db.OpenStore().Dispose(); // the schema
        _logs = new LogService(_db.Dir, _db.ConnectionString, new Clock(Now));
        var at = (Now - TimeSpan.FromMinutes(5)).ToString("O");
        Exec("INSERT INTO run_records (run_key, team, member, cli, kind, backend, started_at, ended_at, end_class, exit_code) VALUES ('k1', 'web', 'DeveloperAda', 'claude', 'agent', 'host', @t, @t, 'failed', 1);", ("@t", at));
        Exec("INSERT INTO run_records (run_key, team, member, cli, kind, backend, started_at, ended_at, end_class, exit_code) VALUES ('k2', 'web', 'DeveloperAda', 'claude', 'agent', 'host', @t, @t, 'completed', 0);", ("@t", at));
        Exec("INSERT INTO messages (type, payload, source, depth, occurred_at, via) VALUES ('agent.blocked', '{\"reason\":\"no failed words here\"}', 'web/DeveloperAda', 0, @t, 'agent');", ("@t", at));
        // Its payload says "failed", but a message's level is its type's alone: progress is info.
        Exec("INSERT INTO messages (type, payload, source, depth, occurred_at, via) VALUES ('agent.progress', '{\"status\":\"the build failed, retrying\"}', 'web/DeveloperAda', 0, @t, 'agent');", ("@t", at));
    }

    public void Dispose() => _db.Dispose();

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private List<JsonObject> Rows(string? level = null) =>
        [.. _logs.Query(new LogQuery(LogSources.Activity, Level: level, Limit: 200))["rows"]!.AsArray().Select(r => r!.AsObject())];

    private static string Event(JsonObject r) => r["event"]!.GetValue<string>();

    [Fact]
    public void EachRow_CarriesTheLevelTheTailGivesIt()
    {
        var rows = Rows();
        Assert.Equal(4, rows.Count);
        var levels = rows.ToDictionary(Event, r => r["level"]!.GetValue<string>());
        Assert.Equal("error", levels["run failed"]);
        Assert.Equal("info", levels["run completed"]);
        Assert.Equal("warning", levels["agent.blocked"]);
        Assert.Equal("info", levels["agent.progress"]);

        // logs.tail reads the same rows with the same levels.
        var tailed = _logs.TailSince("0000").ToDictionary(e => e.Entry.Event!, e => e.Entry.Level);
        foreach (var (ev, level) in levels) Assert.Equal(level, tailed[ev]);
    }

    [Fact]
    public void TheLevelFilter_PicksOneLevel_AndDefaultAndAllKeepEveryRow()
    {
        Assert.Equal(["run failed"], Rows("error").Select(Event));
        Assert.Equal(["agent.blocked"], Rows("warning").Select(Event));
        Assert.Equal(["agent.progress", "run completed"], Rows("info").Select(Event).Order(StringComparer.Ordinal));
        Assert.Empty(Rows("debug")); // nothing in the activity log is routine enough to hide
        Assert.Equal(4, Rows("default").Count);
        Assert.Equal(4, Rows("all").Count);
        Assert.Equal(1L, _logs.Query(new LogQuery(LogSources.Activity, Level: "error"))["total"]!.GetValue<long>());
    }

    [Fact]
    public async Task LogsSources_SaysAllActivityAndDiagnosticsHaveLevels_AndLogsQueryTakesTheLevel()
    {
        var withLevels = _logs.Sources().Select(s => s!.AsObject()).Where(s => s["levels"]!.GetValue<bool>()).Select(s => s["id"]!.GetValue<string>());
        Assert.Equal([LogSources.Activity, FileLogs.Diagnostics.Id], withLevels.Order(StringComparer.Ordinal));

        var rpc = new LogRpc(_logs);
        var page = (JsonObject)(await rpc.DispatchAsync("logs.query", new JsonObject { ["source"] = LogSources.Activity, ["level"] = "warning" }, Ct))!;
        Assert.Equal(["agent.blocked"], page["rows"]!.AsArray().Select(r => Event(r!.AsObject())));
    }

    [Fact]
    public void ASourceWithoutLevels_GivesNoLevel_AndIgnoresTheFilter()
    {
        var rows = _logs.Query(new LogQuery("runs", Level: "error"))["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.ContainsKey("level")));
    }
}
