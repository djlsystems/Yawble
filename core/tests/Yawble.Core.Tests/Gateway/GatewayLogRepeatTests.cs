using Microsoft.Data.Sqlite;
using Yawble.Core.Gateway;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// A refusal retried in a loop is not written once per try: a refused path is logged a few times a minute and then
/// counted; a refused host (plain http, host-denied) or CONNECT (https, connect-denied) is counted on
/// the run's first row for it.
/// </summary>
public class GatewayLogRepeatTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static GatewayEvent Connect(Clock clock, string target, long run = 7) =>
        new(run, "alpha/Wren", clock.Now, GatewayEventKinds.ConnectDenied, true, null, "CONNECT", target, 403, "host not on this agent's allowlist");

    [Fact]
    public void ARepeatedPathDenialIsWrittenAFewTimes_ThenCountedInOneRow()
    {
        using var db = new TempDb();
        db.OpenStore().Dispose(); // the schema
        var clock = new Clock(DateTimeOffset.UtcNow);
        var log = new GatewayLog(db.ConnectionString, clock);
        GatewayEvent Denied(string target) =>
            new(7, "alpha/Wren", clock.Now, GatewayEventKinds.PathDenied, true, "model.yawble.internal", "POST", target, 403, "path not allowed");

        for (var i = 0; i < 1000; i++) log.Write(Denied("/v1/files"));
        log.Write(Denied("/v1/batches")); // another target is its own
        Assert.Equal(GatewayLog.RepeatLimit + 1, Rows(db));

        clock.Now += GatewayLog.RepeatWindow + TimeSpan.FromSeconds(1);
        log.Write(Denied("/v1/files"));
        Assert.Equal(GatewayLog.RepeatLimit + 3, Rows(db)); // the count of the last window, then the new one
        Assert.Contains($"and {1000 - GatewayLog.RepeatLimit} more like it", Details(db));

        // Failed forwards are never thinned: each is what a run reached (successful ones are: GatewayLogThinningTests).
        for (var i = 0; i < 20; i++) log.Write(new GatewayEvent(7, "alpha/Wren", clock.Now, GatewayEventKinds.Forward, false, "model.yawble.internal", "POST", "/v1/messages", 500));
        Assert.Equal(GatewayLog.RepeatLimit + 23, Rows(db));
    }

    [Fact]
    public void ConnectRefused_FiveConnectRefusalsOfOneHostInARun_AreOneRow_CountFive()
    {
        using var db = new TempDb();
        db.OpenStore().Dispose();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var log = new GatewayLog(db.ConnectionString, clock);

        for (var i = 0; i < 5; i++) log.Write(Connect(clock, i % 2 == 0 ? "registry.npmjs.org:443" : "Registry.NPMJS.org:443"));

        var row = Assert.Single(log.Read(7));
        Assert.Equal(5, row.Count);
        Assert.Equal("registry.npmjs.org:443", row.Target); // the first row's
    }

    [Fact]
    public void ConnectRefused_ARetryLoop_IsOneRow_NotSummarisedByTheWindow()
    {
        using var db = new TempDb();
        db.OpenStore().Dispose();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var log = new GatewayLog(db.ConnectionString, clock);

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
        using var db = new TempDb();
        db.OpenStore().Dispose();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var log = new GatewayLog(db.ConnectionString, clock);

        for (var i = 0; i < 3; i++) log.Write(Connect(clock, "registry.npmjs.org:443"));
        for (var i = 0; i < 2; i++) log.Write(Connect(clock, "api.nuget.org:443"));
        for (var i = 0; i < 4; i++) log.Write(Connect(clock, "registry.npmjs.org:443", run: 8));

        Assert.Equal([("registry.npmjs.org:443", 3), ("api.nuget.org:443", 2)], log.Read(7).Select(e => (e.Target!, e.Count)));
        Assert.Equal([("registry.npmjs.org:443", 4)], log.Read(8).Select(e => (e.Target!, e.Count)));
    }

    [Fact]
    public void ConnectRefused_AHostsHttpAndHttpsRefusals_AreTwoRows()
    {
        using var db = new TempDb();
        db.OpenStore().Dispose();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var log = new GatewayLog(db.ConnectionString, clock);

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
        using var db = new TempDb();
        db.OpenStore().Dispose();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var log = new GatewayLog(db.ConnectionString, clock);

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
        Assert.Equal(GatewayLog.RepeatLimit, rows.Count(e => e.Kind == GatewayEventKinds.PathDenied)); // the window, as before
        Assert.All(rows, e => Assert.Equal(1, e.Count));
    }

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
}
