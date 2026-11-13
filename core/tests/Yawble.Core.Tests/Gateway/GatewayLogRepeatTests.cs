using Microsoft.Data.Sqlite;
using Yawble.Core.Gateway;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>A refusal retried in a loop is logged a few times and then counted, not written once per try.</summary>
public class GatewayLogRepeatTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ARepeatedDenialIsWrittenAFewTimes_ThenCountedInOneRow()
    {
        using var db = new TempDb();
        db.OpenStore().Dispose(); // the schema
        var clock = new Clock(DateTimeOffset.UtcNow);
        var log = new GatewayLog(db.ConnectionString, clock);
        GatewayEvent Denied(string target) =>
            new(7, "alpha/Wren", clock.Now, GatewayEventKinds.ConnectDenied, true, null, "CONNECT", target, 403, "host not on this agent's allowlist");

        for (var i = 0; i < 1000; i++) log.Write(Denied("registry.npmjs.org:443"));
        log.Write(Denied("api.nuget.org:443")); // another target is its own
        Assert.Equal(GatewayLog.RepeatLimit + 1, Rows(db));

        clock.Now += GatewayLog.RepeatWindow + TimeSpan.FromSeconds(1);
        log.Write(Denied("registry.npmjs.org:443"));
        Assert.Equal(GatewayLog.RepeatLimit + 3, Rows(db)); // the count of the last window, then the new one
        Assert.Contains($"and {1000 - GatewayLog.RepeatLimit} more like it", Details(db));

        // Forwards are never thinned: each is what a run reached.
        for (var i = 0; i < 20; i++) log.Write(new GatewayEvent(7, "alpha/Wren", clock.Now, GatewayEventKinds.Forward, false, "model.yawble.internal", "POST", "/v1/messages", 200));
        Assert.Equal(GatewayLog.RepeatLimit + 23, Rows(db));
    }

    private static long Rows(TempDb db) => Scalar<long>(db, "SELECT count(*) FROM gateway_events;");

    private static string Details(TempDb db) => Scalar<string>(db, "SELECT group_concat(detail, ' | ') FROM gateway_events;");

    private static T Scalar<T>(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }
}
