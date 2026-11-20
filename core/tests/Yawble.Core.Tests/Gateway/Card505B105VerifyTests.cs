using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Logs;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// Card 505 (B105 verify), the tester's own checks of bf65f72 (card 504): a CONNECT refused again in a run is counted on
/// that run's first row for the authority (host:port); other hosts, ports and runs are new rows; a host's http and https
/// refusals stay apart; path-denied keeps its 5-per-minute window; a connect-denied with no run is a plain row; the
/// Network source's Times column and All activity's " (Nx)" show the count; docs/features.md says so. Temp databases,
/// a loopback gateway with no allowed hosts, no real network.
/// </summary>
public class Card505B105VerifyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private static GatewayEvent Connect(FakeClock clock, string target, long? run = 50) =>
        new(run, "beta/Tess", clock.Now, GatewayEventKinds.ConnectDenied, true, null, "CONNECT", target, 403, "host not on this agent's allowlist");

    // ------------------------------------------------------------------ point 1: through the real gateway

    [Fact]
    public async Task P1_ThroughTheGateway_OneRowPerRunAndAuthority_HttpApart_CountsInTimesAndAllActivity()
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
        Assert.Equal(0, Scalar(f.Db.ConnectionString, "SELECT count(*) FROM gateway_events WHERE detail LIKE '%more like it%';"));

        // The run's verdict still names every refused authority once.
        Assert.Equal(["blocked.example:443", "other.example:443", "blocked.example:8443"], GatewayVerdicts.From(run).DeniedHosts);

        // Network (Times column) and All activity (" (Nx)"), as the shell shows them.
        var logs = new LogService(f.Db.Dir, f.Db.ConnectionString, new FakeClock(DateTimeOffset.UtcNow.AddMinutes(5)));
        var net = Rows(logs.Query(new LogQuery("gateway", Limit: 500)));
        Assert.Equal(["2", "5"], net.Where(r => S(r, "kind") == GatewayEventKinds.ConnectDenied && S(r, "target") == "blocked.example:443").Select(r => S(r, "count")!).Order());
        Assert.Equal("1", S(Assert.Single(net, r => S(r, "target") == "blocked.example:8443"), "count"));
        Assert.Equal("3", S(Assert.Single(net, r => S(r, "kind") == GatewayEventKinds.HostDenied), "count"));
        var act = Rows(logs.Query(new LogQuery(LogSources.Activity, Limit: 500))).Where(r => S(r, "source") == "Network").Select(r => S(r, "details")!).ToList();
        Assert.Single(act, d => d.StartsWith("CONNECT blocked.example:443 403 ", StringComparison.Ordinal) && d.EndsWith(" (5x)", StringComparison.Ordinal));
        Assert.Single(act, d => d.StartsWith("CONNECT blocked.example:443 403 ", StringComparison.Ordinal) && d.EndsWith(" (2x)", StringComparison.Ordinal));
        Assert.Single(act, d => d.StartsWith("CONNECT other.example:443 403 ", StringComparison.Ordinal) && d.EndsWith(" (2x)", StringComparison.Ordinal));
        var once = Assert.Single(act, d => d.StartsWith("CONNECT blocked.example:8443 403 ", StringComparison.Ordinal));
        Assert.DoesNotContain("x)", once);
        Assert.Single(act, d => d.StartsWith("GET http://blocked.example/p0 403 ", StringComparison.Ordinal) && d.EndsWith(" (3x)", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ point 1: the log's rules

    [Fact]
    public void P1_ConnectDeniedWithNoRun_IsAPlainRowEachTime()
    {
        using var db = new TempDb();
        db.OpenStore().Dispose();
        var clock = new FakeClock(T0);
        var log = new GatewayLog(db.ConnectionString, clock);
        for (var i = 0; i < GatewayLog.RepeatLimit + 3; i++) log.Write(Connect(clock, "blocked.example:443", run: null));
        Assert.Equal(GatewayLog.RepeatLimit + 3, Scalar(db.ConnectionString, "SELECT count(*) FROM gateway_events WHERE run_id IS NULL AND kind = 'connect-denied' AND count = 1;"));
        Assert.Equal(GatewayLog.RepeatLimit + 3, Scalar(db.ConnectionString, "SELECT count(*) FROM gateway_events;"));
    }

    [Fact]
    public void P1_ARunsConnectRow_PrunedAway_TheNextRefusalIsANewRow()
    {
        using var db = new TempDb();
        db.OpenStore().Dispose();
        var clock = new FakeClock(T0);
        var log = new GatewayLog(db.ConnectionString, clock);
        for (var i = 0; i < 3; i++) log.Write(Connect(clock, "blocked.example:443"));
        Exec(db.ConnectionString, "DELETE FROM gateway_events;"); // the log was pruned
        log.Write(Connect(clock, "blocked.example:443"));
        log.Write(Connect(clock, "blocked.example:443"));
        Assert.Equal([("blocked.example:443", 2)], log.Read(50).Select(e => (e.Target!, e.Count)));
    }

    [Fact]
    public void P1_OtherEventsBetweenTheRefusals_DoNotSplitTheRow_AndStayAsBefore()
    {
        using var db = new TempDb();
        db.OpenStore().Dispose();
        var clock = new FakeClock(T0);
        var log = new GatewayLog(db.ConnectionString, clock);
        for (var i = 0; i < 4; i++)
        {
            log.Write(Connect(clock, "blocked.example:443"));
            log.Write(new GatewayEvent(50, "beta/Tess", clock.Now, GatewayEventKinds.ConnectAllowed, false, null, "CONNECT", "api.nuget.org:443", 200));
            log.Write(new GatewayEvent(50, "beta/Tess", clock.Now, GatewayEventKinds.Forward, false, Vhosts.Model, "POST", "/v1/messages", 200));
            log.Write(new GatewayEvent(50, "beta/Tess", clock.Now, GatewayEventKinds.Forward, false, Vhosts.Model, "POST", "/v1/messages", 500));
            clock.Now += TimeSpan.FromMinutes(2); // past the repeat window each time
        }
        var rows = log.Read(50);
        Assert.Equal(4, Assert.Single(rows, e => e.Kind == GatewayEventKinds.ConnectDenied).Count);
        Assert.Equal(4, rows.Count(e => e.Kind == GatewayEventKinds.ConnectAllowed));
        Assert.Single(rows, e => e.Kind == GatewayEventKinds.Forward && e.Status == 200); // B103 thinning unchanged
        Assert.Equal(4, rows.Count(e => e.Kind == GatewayEventKinds.Forward && e.Status == 500));
        Assert.All(rows.Where(e => e.Kind != GatewayEventKinds.ConnectDenied), e => Assert.Equal(1, e.Count));
    }

    [Fact]
    public void P1_PathDenied_KeepsTheFivePerMinuteWindow_AndItsSummaryRow()
    {
        using var db = new TempDb();
        db.OpenStore().Dispose();
        var clock = new FakeClock(T0);
        var log = new GatewayLog(db.ConnectionString, clock);
        GatewayEvent Path() => new(50, "beta/Tess", clock.Now, GatewayEventKinds.PathDenied, true, Vhosts.Model, "POST", "/v1/files", 403, "path not allowed");

        for (var i = 0; i < 12; i++) log.Write(Path());
        Assert.Equal(GatewayLog.RepeatLimit, log.Read(50).Count);
        clock.Now += GatewayLog.RepeatWindow + TimeSpan.FromSeconds(1);
        log.Write(Path());
        var rows = log.Read(50);
        Assert.Equal(GatewayLog.RepeatLimit + 2, rows.Count);
        Assert.Contains(rows, e => (e.Detail ?? "").Contains($"and {12 - GatewayLog.RepeatLimit} more like it", StringComparison.Ordinal));
        Assert.All(rows, e => Assert.Equal(1, e.Count));
    }

    // ------------------------------------------------------------------ point 3: docs/features.md and the About text

    [Fact]
    public void P3_FeaturesMd_SaysConnectDeniedIsCounted_AndOnlyPathsUseTheWindow()
    {
        var doc = File.ReadAllText(System.IO.Path.Combine(Binaries.RepoRoot, "docs", "features.md")).Replace("\r\n", "\n");
        var start = doc.IndexOf("**Security**: **Network**", StringComparison.Ordinal);
        Assert.True(start > 0);
        var text = doc[start..doc.IndexOf("**Usage**", start, StringComparison.Ordinal)];
        var flat = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        Assert.Contains("(`host-denied`) and a refused HTTPS connection (`connect-denied`), refused again in the same run, are both counted on one row", flat);
        Assert.Contains("for `connect-denied`, the host and port it asked for", flat);
        Assert.Contains("A host's plain-http and HTTPS refusals stay two rows", flat);
        Assert.Contains("**Times** column says how many times", flat);
        Assert.Contains("ends in ` (Nx)` when it counts more than one (` (5x)` for five)", flat);
        Assert.Contains("Refused paths are not folded into one row: the first few of each in a minute are written", flat);
        Assert.DoesNotContain("(`connect-denied`) and refused paths are not folded", flat); // the old text
        Assert.Contains("a host refused again, over plain http or https, is counted on one row", LogSources.Find("gateway")!.About);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task RefusedAsync(RunGateway gateway, string request)
    {
        var (status, _, client) = await ProxyClient.RawAsync(gateway, request);
        client.Dispose();
        Assert.StartsWith("HTTP/1.1 403", status);
    }

    private static List<JsonObject> Rows(JsonObject page) => page["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();

    private static string? S(JsonObject row, string key) => row[key] is JsonValue v ? v.ToString() : null;

    private static long Scalar(string cs, string sql)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Exec(string cs, string sql)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
