using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// A run's successful model requests are not each a gateway_events row (each is a usage_events row); the first
/// per model vhost is, and the first after a credential problem or a rate limit, so the run's verdict still clears.
/// A host denied again in the run counts on its first row. Fake upstream and fake secrets only.
/// </summary>
public class GatewayLogThinningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static GatewayEvent E(string kind, string? vhost, int? status, string? detail = null, long run = 7, int minute = 0) =>
        new(run, "alpha/Wren", T0.AddMinutes(minute), kind, false, vhost, "POST", "/v1/messages", status, detail);

    private static (TempDb Db, GatewayLog Log) NewLog()
    {
        var db = new TempDb();
        db.OpenStore().Dispose(); // the schema
        return (db, new GatewayLog(db.ConnectionString, new FakeClock(T0)));
    }

    // ------------------------------------------------------------------ through the run's gateway

    [Fact]
    public async Task FiftyModelRequests_AreOneForwardRow_AndFiftyUsageRows()
    {
        await using var f = await GatewayFixture.StartAsync();
        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        await using var gateway = f.StartRun(31);
        using var http = ProxyClient.For(gateway);
        f.Upstream.ReleaseStream();

        for (var i = 0; i < 50; i++)
        {
            using var response = await http.SendAsync(ModelRequest(gateway), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("message_stop", await response.Content.ReadAsStringAsync(Ct));
        }

        await Wait.UntilAsync(() => Scalar(f.Db, "SELECT count(*) FROM usage_events WHERE run_key = 'run-31';") == 50, "fifty usage rows");
        var forward = Assert.Single(f.Log.Read(31), e => e.Kind == GatewayEventKinds.Forward);
        Assert.Equal((Vhosts.Model, 200), (forward.Vhost, forward.Status));
        Assert.Contains("credential swapped", forward.Detail); // the swap is still shown, once
        Assert.Equal(GatewayVerdict.Clean with { DeniedHosts = [] }, GatewayVerdicts.From(f.Log.Read(31)) with { DeniedHosts = [] });
    }

    [Fact]
    public async Task AFailedModelRequest_IsStillWritten_AndASuccessAfterItClearsTheCredentialProblem()
    {
        await using var f = await GatewayFixture.StartAsync();
        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        await using var gateway = f.StartRun(32);
        using var http = ProxyClient.For(gateway);
        f.Upstream.ReleaseStream();

        for (var i = 0; i < 3; i++) (await http.SendAsync(ModelRequest(gateway), Ct)).Dispose();
        f.Upstream.RefuseAll = true;
        using (var refused = await http.SendAsync(ModelRequest(gateway), Ct)) Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.True(GatewayVerdicts.From(f.Log.Read(32)).CredentialRejected);

        f.Upstream.RefuseAll = false;
        for (var i = 0; i < 3; i++) (await http.SendAsync(ModelRequest(gateway), Ct)).Dispose();

        var forwards = f.Log.Read(32).Where(e => e.Kind == GatewayEventKinds.Forward).Select(e => e.Status).ToList();
        Assert.Equal([200, 401, 200], forwards); // first success, the failure, the first success after the problem
        Assert.False(GatewayVerdicts.From(f.Log.Read(32)).CredentialRejected);
    }

    [Fact]
    public async Task OneHostDeniedFiveTimes_IsOneRowCountedFive()
    {
        await using var f = await GatewayFixture.StartAsync();
        await using var gateway = f.StartRun(33);
        for (var i = 0; i < 5; i++)
        {
            var (status, _, client) = await ProxyClient.RawAsync(gateway, $"GET http://example.com/try{i} HTTP/1.1\r\nHost: example.com\r\n\r\n");
            client.Dispose();
            Assert.StartsWith("HTTP/1.1 403", status);
        }
        var denied = Assert.Single(f.Log.Read(33), e => e.Kind == GatewayEventKinds.HostDenied);
        Assert.Equal(("example.com", 5), (denied.Vhost, denied.Count));
    }

    // ------------------------------------------------------------------ the log itself

    [Fact]
    public void OnlyTheFirstSuccessPerRunAndModelVhost_IsWritten_FailuresAndOtherKindsAlways()
    {
        var (db, log) = NewLog();
        using var _ = db;
        for (var i = 0; i < 50; i++) log.Write(E(GatewayEventKinds.Forward, Vhosts.Model, 200));
        log.Write(E(GatewayEventKinds.Forward, Vhosts.Grok, 200));        // another model vhost: its own first
        log.Write(E(GatewayEventKinds.Forward, Vhosts.Model, 200, run: 8)); // another run: its own first
        log.Write(E(GatewayEventKinds.Forward, Vhosts.Model, 500));
        log.Write(E(GatewayEventKinds.Forward, Vhosts.Model, 500));
        log.Write(E(GatewayEventKinds.Forward, "tool.example", 200));      // not a model vhost
        log.Write(E(GatewayEventKinds.Forward, "tool.example", 200));
        log.Write(E(GatewayEventKinds.Bus, Vhosts.Bus, 200));
        log.Write(E(GatewayEventKinds.Bus, Vhosts.Bus, 200));

        var run7 = log.Read(7);
        Assert.Equal(1, run7.Count(e => e.Kind == GatewayEventKinds.Forward && e.Vhost == Vhosts.Model && e.Status == 200));
        Assert.Equal(1, run7.Count(e => e.Vhost == Vhosts.Grok));
        Assert.Equal(2, run7.Count(e => e.Status == 500));
        Assert.Equal(2, run7.Count(e => e.Vhost == "tool.example"));
        Assert.Equal(2, run7.Count(e => e.Kind == GatewayEventKinds.Bus));
        Assert.Single(log.Read(8));
    }

    [Fact]
    public void ACredentialProblemThenASuccess_ClearsTheVerdict()
    {
        var (db, log) = NewLog();
        using var _ = db;
        for (var i = 0; i < 10; i++) log.Write(E(GatewayEventKinds.Forward, Vhosts.Grok, 200, minute: 0));
        log.Write(E(GatewayEventKinds.CredentialProblem, Vhosts.Grok, 401, "Grok login expired: sign in again", minute: 1));
        Assert.True(GatewayVerdicts.From(log.Read(7)).CredentialRejected);

        for (var i = 0; i < 10; i++) log.Write(E(GatewayEventKinds.Forward, Vhosts.Grok, 200, minute: 2));
        var verdict = GatewayVerdicts.From(log.Read(7));
        Assert.False(verdict.CredentialRejected);
        Assert.Null(verdict.Reason);
        Assert.Equal(2, log.Read(7).Count(e => e.Kind == GatewayEventKinds.Forward)); // one before, one after the problem
    }

    [Fact]
    public void ARateLimitThenASuccess_ClearsTheVerdict()
    {
        var (db, log) = NewLog();
        using var _ = db;
        var until = T0.AddHours(2).ToString("O");
        log.Write(E(GatewayEventKinds.Forward, Vhosts.Model, 200));
        log.Write(E(GatewayEventKinds.Forward, Vhosts.Model, 429, minute: 1));
        log.Write(E(GatewayEventKinds.RateLimited, Vhosts.Model, 429, until, minute: 1));
        Assert.Equal(T0.AddHours(2), GatewayVerdicts.From(log.Read(7)).RateLimitedUntil);

        log.Write(E(GatewayEventKinds.Forward, Vhosts.Model, 200, minute: 10));
        log.Write(E(GatewayEventKinds.Forward, Vhosts.Model, 200, minute: 11));
        Assert.Null(GatewayVerdicts.From(log.Read(7)).RateLimitedUntil);
        // A rate limit on another vhost is not cleared by this one's success.
        log.Write(E(GatewayEventKinds.RateLimited, Vhosts.Grok, 429, until, minute: 12));
        log.Write(E(GatewayEventKinds.Forward, Vhosts.Model, 200, minute: 13));
        Assert.Equal(T0.AddHours(2), GatewayVerdicts.From(log.Read(7)).RateLimitedUntil);
    }

    [Fact]
    public void ARepeatedHostDenial_CountsOnTheRunsRow_PerHostAndRun()
    {
        var (db, log) = NewLog();
        using var _ = db;
        GatewayEvent Denied(string host, long run = 7, string path = "/") =>
            new(run, "alpha/Wren", T0, GatewayEventKinds.HostDenied, true, host, "GET", path, 403, "host not served by this run's gateway");

        for (var i = 0; i < 5; i++) log.Write(Denied("example.com", path: $"/p{i}"));
        log.Write(Denied("other.example"));
        log.Write(Denied("example.com", run: 8));

        var row = Assert.Single(log.Read(7), e => e.Vhost == "example.com");
        Assert.Equal(5, row.Count);
        Assert.Equal("/p0", row.Target); // the first one's row
        Assert.Equal(1, Assert.Single(log.Read(7), e => e.Vhost == "other.example").Count);
        Assert.Equal(1, Assert.Single(log.Read(8)).Count);
        Assert.Equal(3, Scalar(db, "SELECT count(*) FROM gateway_events;"));
        Assert.Equal(7, Scalar(db, "SELECT sum(count) FROM gateway_events;"));
    }

    [Fact]
    public void AHostDenialWhoseRowWasPruned_IsWrittenAgain()
    {
        var (db, log) = NewLog();
        using var _ = db;
        var denied = new GatewayEvent(7, "alpha/Wren", T0, GatewayEventKinds.HostDenied, true, "example.com", "GET", "/", 403, "no");
        log.Write(denied);
        Exec(db, "DELETE FROM gateway_events;");
        log.Write(denied);
        log.Write(denied);
        Assert.Equal(2, Assert.Single(log.Read(7)).Count);
    }

    // ------------------------------------------------------------------ helpers

    private static HttpRequestMessage ModelRequest(RunGateway gateway)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://{Vhosts.Model}/v1/messages?beta=true")
        {
            Content = new StringContent("{\"model\":\"fake\",\"stream\":true}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        return request;
    }

    private static long Scalar(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
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
