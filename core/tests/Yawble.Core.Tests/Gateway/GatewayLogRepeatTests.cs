using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Logs;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// A refusal retried in a loop is not written once per try: a refused path is logged a few times a minute and then
/// counted; a refused host (plain http, host-denied) or CONNECT (https, connect-denied) is counted on
/// the run's first row for its authority (host:port). Other hosts, ports and runs are new rows, a host's http and
/// https refusals stay apart, and a connect-denied with no run is a plain row. The Network source's Times column and
/// All activity's " (Nx)" show the count. Temp databases and a loopback gateway with no allowed hosts; no real network.
/// </summary>
public class GatewayLogRepeatTests
{
    private static GatewayEvent Connect(FakeClock clock, string target, long? run = 7) =>
        new(run, "alpha/Wren", clock.Now, GatewayEventKinds.ConnectDenied, true, null, "CONNECT", target, 403, "host not on this agent's allowlist");

    private static (TempDb Db, FakeClock Clock, GatewayLog Log) NewLog()
    {
        var db = new TempDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        return (db, clock, new GatewayLog(db.ConnectionString, clock));
    }

    [Fact]
    public void ARepeatedPathDenialIsWrittenAFewTimes_ThenCountedInOneRow()
    {
        var (db, clock, log) = NewLog();
        using var _ = db;
        GatewayEvent Denied(string target) =>
            new(7, "alpha/Wren", clock.Now, GatewayEventKinds.PathDenied, true, "model.yawble.internal", "POST", target, 403, "path not allowed");

        for (var i = 0; i < 1000; i++) log.Write(Denied("/v1/files"));
        log.Write(Denied("/v1/batches")); // another target is its own
        Assert.Equal(GatewayLog.RepeatLimit + 1, Rows(db));

        clock.Now += GatewayLog.RepeatWindow + TimeSpan.FromSeconds(1);
        log.Write(Denied("/v1/files"));
        Assert.Equal(GatewayLog.RepeatLimit + 3, Rows(db)); // the count of the last window, then the new one
        Assert.Contains($"and {1000 - GatewayLog.RepeatLimit} more like it", Details(db));
        Assert.All(log.Read(7), e => Assert.Equal(1, e.Count));

        // Failed forwards are never thinned: each is what a run reached (successful ones are: GatewayLogThinningTests).
        for (var i = 0; i < 20; i++) log.Write(new GatewayEvent(7, "alpha/Wren", clock.Now, GatewayEventKinds.Forward, false, "model.yawble.internal", "POST", "/v1/messages", 500));
        Assert.Equal(GatewayLog.RepeatLimit + 23, Rows(db));
    }

    [Fact]
    public void ConnectRefused_FiveConnectRefusalsOfOneHostInARun_AreOneRow_CountFive()
    {
        var (db, clock, log) = NewLog();
        using var _ = db;

        for (var i = 0; i < 5; i++) log.Write(Connect(clock, i % 2 == 0 ? "registry.npmjs.org:443" : "Registry.NPMJS.org:443"));

        var row = Assert.Single(log.Read(7));
        Assert.Equal(5, row.Count);
        Assert.Equal("registry.npmjs.org:443", row.Target); // the first row's
    }

    [Fact]
    public void ConnectRefused_ARetryLoop_IsOneRow_NotSummarisedByTheWindow()
    {
        var (db, clock, log) = NewLog();
        using var _ = db;

        for (var i = 0; i < 1000; i++) log.Write(Connect(clock, "registry.npmjs.org:443"));
        clock.Now += GatewayLog.RepeatWindow + TimeSpan.FromSeconds(1);
        log.Write(Connect(clock, "registry.npmjs.org:443")); // after the window: still the same row

        var row = Assert.Single(log.Read(7));
        Assert.Equal(1001, row.Count);
        Assert.DoesNotContain("more like it", Details(db));
    }

    [Fact]
    public void ConnectRefused_TwoHosts_AreTwoRows_AndAnotherRunIsANewRow()
    {
        var (db, clock, log) = NewLog();
        using var _ = db;

        for (var i = 0; i < 3; i++) log.Write(Connect(clock, "registry.npmjs.org:443"));
        for (var i = 0; i < 2; i++) log.Write(Connect(clock, "api.nuget.org:443"));
        for (var i = 0; i < 4; i++) log.Write(Connect(clock, "registry.npmjs.org:443", run: 8));

        Assert.Equal([("registry.npmjs.org:443", 3), ("api.nuget.org:443", 2)], log.Read(7).Select(e => (e.Target!, e.Count)));
        Assert.Equal([("registry.npmjs.org:443", 4)], log.Read(8).Select(e => (e.Target!, e.Count)));
    }

    [Fact]
    public void ConnectRefused_AHostsHttpAndHttpsRefusals_AreTwoRows()
    {
        var (db, clock, log) = NewLog();
        using var _ = db;

        for (var i = 0; i < 3; i++)
        {
            log.Write(new GatewayEvent(7, "alpha/Wren", clock.Now, GatewayEventKinds.HostDenied, true, "denied.example", "GET", $"http://denied.example/x{i}", 403, "host not served by this run's gateway"));
            log.Write(Connect(clock, "denied.example:443"));
        }

        var rows = log.Read(7);
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, Assert.Single(rows, e => e.Kind == GatewayEventKinds.HostDenied).Count);
        Assert.Equal(3, Assert.Single(rows, e => e.Kind == GatewayEventKinds.ConnectDenied).Count);
    }

    [Fact]
    public void ConnectRefused_OtherKinds_AreUnchanged()
    {
        var (db, clock, log) = NewLog();
        using var _ = db;

        for (var i = 0; i < 3; i++)
        {
            log.Write(new GatewayEvent(7, "alpha/Wren", clock.Now, GatewayEventKinds.ConnectAllowed, false, null, "CONNECT", "api.nuget.org:443", 200));
            log.Write(new GatewayEvent(7, "alpha/Wren", clock.Now, GatewayEventKinds.Unauthorized, true, "model.yawble.internal", "POST", "/v1/messages", 401));
        }
        for (var i = 0; i < GatewayLog.RepeatLimit + 3; i++)
            log.Write(new GatewayEvent(7, "alpha/Wren", clock.Now, GatewayEventKinds.PathDenied, true, "model.yawble.internal", "POST", "/v1/files", 403));

        var rows = log.Read(7);
        Assert.Equal(3, rows.Count(e => e.Kind == GatewayEventKinds.ConnectAllowed));
        Assert.Equal(3, rows.Count(e => e.Kind == GatewayEventKinds.Unauthorized));
        Assert.Equal(GatewayLog.RepeatLimit, rows.Count(e => e.Kind == GatewayEventKinds.PathDenied)); // the repeat window
        Assert.All(rows, e => Assert.Equal(1, e.Count));
    }

    [Fact]
    public void ConnectRefused_WithNoRun_IsAPlainRowEachTime()
    {
        var (db, clock, log) = NewLog();
        using var _ = db;

        for (var i = 0; i < GatewayLog.RepeatLimit + 3; i++) log.Write(Connect(clock, "blocked.example:443", run: null));

        Assert.Equal(GatewayLog.RepeatLimit + 3, Scalar<long>(db, "SELECT count(*) FROM gateway_events WHERE run_id IS NULL AND kind = 'connect-denied' AND count = 1;"));
        Assert.Equal(GatewayLog.RepeatLimit + 3, Rows(db));
    }

    [Fact]
    public void ConnectRefused_WhenTheRunsRowIsPrunedAway_TheNextRefusalIsANewRow()
    {
        var (db, clock, log) = NewLog();
        using var _ = db;

        for (var i = 0; i < 3; i++) log.Write(Connect(clock, "blocked.example:443"));
        Exec(db, "DELETE FROM gateway_events;"); // the log is pruned
        log.Write(Connect(clock, "blocked.example:443"));
        log.Write(Connect(clock, "blocked.example:443"));

        Assert.Equal([("blocked.example:443", 2)], log.Read(7).Select(e => (e.Target!, e.Count)));
    }

    [Fact]
    public void ConnectRefused_OtherEventsBetweenTheRefusals_DoNotSplitTheRow()
    {
        var (db, clock, log) = NewLog();
        using var _ = db;

        for (var i = 0; i < 4; i++)
        {
            log.Write(Connect(clock, "blocked.example:443"));
            log.Write(new GatewayEvent(7, "alpha/Wren", clock.Now, GatewayEventKinds.ConnectAllowed, false, null, "CONNECT", "api.nuget.org:443", 200));
            log.Write(new GatewayEvent(7, "alpha/Wren", clock.Now, GatewayEventKinds.Forward, false, Vhosts.Model, "POST", "/v1/messages", 200));
            log.Write(new GatewayEvent(7, "alpha/Wren", clock.Now, GatewayEventKinds.Forward, false, Vhosts.Model, "POST", "/v1/messages", 500));
            clock.Now += TimeSpan.FromMinutes(2); // past the repeat window each time
        }

        var rows = log.Read(7);
        Assert.Equal(4, Assert.Single(rows, e => e.Kind == GatewayEventKinds.ConnectDenied).Count);
        Assert.Equal(4, rows.Count(e => e.Kind == GatewayEventKinds.ConnectAllowed));
        Assert.Single(rows, e => e.Kind == GatewayEventKinds.Forward && e.Status == 200); // successful forwards are thinned
        Assert.Equal(4, rows.Count(e => e.Kind == GatewayEventKinds.Forward && e.Status == 500));
        Assert.All(rows.Where(e => e.Kind != GatewayEventKinds.ConnectDenied), e => Assert.Equal(1, e.Count));
    }

    [Fact]
    public async Task ThroughTheGateway_ConnectRefusalsAreOneRowPerRunAndAuthority_HttpApart_CountsInTimesAndAllActivity()
    {
        await using var f = await GatewayFixture.StartAsync();
        await using (var gateway = f.StartRun(505))
        {
            // Five CONNECTs to one host (two in another case), two to another host, one to the first host on another
            // port, and three plain-http requests to the first host.
            foreach (var target in new[] { "blocked.example:443", "BLOCKED.example:443", "blocked.example:443", "Blocked.Example:443", "blocked.example:443" })
                await RefusedAsync(gateway, $"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n");
            for (var i = 0; i < 2; i++) await RefusedAsync(gateway, "CONNECT other.example:443 HTTP/1.1\r\nHost: other.example:443\r\n\r\n");
            await RefusedAsync(gateway, "CONNECT blocked.example:8443 HTTP/1.1\r\nHost: blocked.example:8443\r\n\r\n");
            for (var i = 0; i < 3; i++) await RefusedAsync(gateway, $"GET http://blocked.example/p{i} HTTP/1.1\r\nHost: blocked.example\r\n\r\n");
        }
        await using (var second = f.StartRun(506))
        {
            for (var i = 0; i < 2; i++) await RefusedAsync(second, "CONNECT blocked.example:443 HTTP/1.1\r\nHost: blocked.example:443\r\n\r\n");
        }

        var run = f.Log.Read(505);
        var connects = run.Where(e => e.Kind == GatewayEventKinds.ConnectDenied).Select(e => (e.Target!, e.Count)).ToList();
        Assert.Equal([("blocked.example:443", 5), ("other.example:443", 2), ("blocked.example:8443", 1)], connects);
        var http = Assert.Single(run, e => e.Kind == GatewayEventKinds.HostDenied);
        Assert.Equal((3, "http://blocked.example/p0"), (http.Count, http.Target));
        Assert.Equal([("blocked.example:443", 2)], f.Log.Read(506).Where(e => e.Kind == GatewayEventKinds.ConnectDenied).Select(e => (e.Target!, e.Count)));
        Assert.Equal(0, Scalar<long>(f.Db, "SELECT count(*) FROM gateway_events WHERE detail LIKE '%more like it%';"));

        // The run's verdict names every refused authority once.
        Assert.Equal(["blocked.example:443", "other.example:443", "blocked.example:8443"], GatewayVerdicts.From(run).DeniedHosts);

        // Network (Times column) and All activity (" (Nx)"), as the shell shows them.
        var logs = new LogService(f.Db.Dir, f.Db.ConnectionString, new FakeClock(DateTimeOffset.UtcNow.AddMinutes(5)));
        var net = JsonRows(logs.Query(new LogQuery("gateway", Limit: 500)));
        Assert.Equal(["2", "5"], net.Where(r => S(r, "kind") == GatewayEventKinds.ConnectDenied && S(r, "target") == "blocked.example:443").Select(r => S(r, "count")!).Order());
        Assert.Equal("1", S(Assert.Single(net, r => S(r, "target") == "blocked.example:8443"), "count"));
        Assert.Equal("3", S(Assert.Single(net, r => S(r, "kind") == GatewayEventKinds.HostDenied), "count"));
        var act = JsonRows(logs.Query(new LogQuery(LogSources.Activity, Limit: 500))).Where(r => S(r, "source") == "Network").Select(r => S(r, "details")!).ToList();
        Assert.Single(act, d => d.StartsWith("CONNECT blocked.example:443 403 ", StringComparison.Ordinal) && d.EndsWith(" (5x)", StringComparison.Ordinal));
        Assert.Single(act, d => d.StartsWith("CONNECT blocked.example:443 403 ", StringComparison.Ordinal) && d.EndsWith(" (2x)", StringComparison.Ordinal));
        Assert.Single(act, d => d.StartsWith("CONNECT other.example:443 403 ", StringComparison.Ordinal) && d.EndsWith(" (2x)", StringComparison.Ordinal));
        var once = Assert.Single(act, d => d.StartsWith("CONNECT blocked.example:8443 403 ", StringComparison.Ordinal));
        Assert.DoesNotContain("x)", once);
        Assert.Single(act, d => d.StartsWith("GET http://blocked.example/p0 403 ", StringComparison.Ordinal) && d.EndsWith(" (3x)", StringComparison.Ordinal));
    }

    private static async Task RefusedAsync(RunGateway gateway, string request)
    {
        var (status, _, client) = await ProxyClient.RawAsync(gateway, request);
        client.Dispose();
        Assert.StartsWith("HTTP/1.1 403", status);
    }

    private static List<JsonObject> JsonRows(JsonObject page) => page["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();

    private static string? S(JsonObject row, string key) => row[key] is JsonValue v ? v.ToString() : null;

    private static long Rows(TempDb db) => Scalar<long>(db, "SELECT count(*) FROM gateway_events;");

    private static string Details(TempDb db) => Scalar<string>(db, "SELECT COALESCE(group_concat(detail, ' | '), '') FROM gateway_events;");

    private static T Scalar<T>(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    private static void Exec(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
