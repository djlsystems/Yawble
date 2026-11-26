using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Guardrail;
using Yawble.Core.Launch;
using Yawble.Core.Logs;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// A run's successful model requests are not each a gateway_events row (each is a usage_events row); the first
/// per model vhost is, and the first after a credential problem or a rate limit, so the run's verdict still clears.
/// A host denied again in the run counts on its first row; the Network source's Times column and All activity's
/// " (Nx)" show the count. The guardrail check reads the thinned log as it would read every event.
/// Fake upstream and fake secrets only.
/// </summary>
public class GatewayLogThinningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static GatewayEvent E(string kind, string? vhost, int? status, string? detail = null, long run = 7, int minute = 0) =>
        new(run, "alpha/Wren", T0.AddMinutes(minute), kind, false, vhost, "POST", "/v1/messages", status, detail);

    private static GatewayEvent Fwd(string vhost, int status, long run = 7, int minute = 0, string? detail = null) =>
        E(GatewayEventKinds.Forward, vhost, status, detail, run, minute);

    private static (TempDb Db, GatewayLog Log) NewLog()
    {
        var db = new TempDb();
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
        Assert.Equal(50, f.Upstream.Requests.Count); // every request went upstream
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
    public async Task OneHostDeniedFiveTimes_IsOneRowCountedFive_InTheTimesColumnAndAllActivity()
    {
        await using var f = await GatewayFixture.StartAsync();
        await using var gateway = f.StartRun(33);
        for (var i = 0; i < 5; i++)
        {
            var (status, _, client) = await ProxyClient.RawAsync(gateway, $"GET http://denied.example/x{i} HTTP/1.1\r\nHost: denied.example\r\n\r\n");
            client.Dispose();
            Assert.StartsWith("HTTP/1.1 403", status);
        }
        var denied = Assert.Single(f.Log.Read(33), e => e.Kind == GatewayEventKinds.HostDenied);
        Assert.Equal(("denied.example", 5), (denied.Vhost, denied.Count));
        Assert.Equal("http://denied.example/x0", denied.Target);

        var logs = new LogService(f.Db.Dir, f.Db.ConnectionString, new FakeClock(DateTimeOffset.UtcNow.AddMinutes(5)));
        var net = Assert.Single(Rows(logs.Query(new LogQuery("gateway", Limit: 200))), r => S(r, "kind") == GatewayEventKinds.HostDenied);
        Assert.Equal("5", net["count"]!.ToString());
        var act = Assert.Single(Rows(logs.Query(new LogQuery(LogSources.Activity, Limit: 200))), r => S(r, "source") == "Network" && S(r, "event") == GatewayEventKinds.HostDenied);
        Assert.Equal("GET http://denied.example/x0 403 host not served by this run's gateway (5x)", S(act, "details"));
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
    public void TheFirstSuccessIsPerRunAndPerModelVhost_WhateverTheVhostsCase_AndOtherVhostsAreNotThinned()
    {
        var (db, log) = NewLog();
        using var _ = db;
        foreach (var vhost in Vhosts.ProductionUpstreams.Keys)
            for (var i = 0; i < 5; i++) log.Write(Fwd(i % 2 == 0 ? vhost : vhost.ToUpperInvariant(), 200 + i % 2)); // 200/201, mixed case
        for (var i = 0; i < 3; i++) log.Write(Fwd("registry.npmjs.org", 200));
        for (var i = 0; i < 3; i++) log.Write(Fwd(Vhosts.Model, 200, run: 8));

        var rows = log.Read(7);
        Assert.Equal(Vhosts.ProductionUpstreams.Count, rows.Count(e => e.Vhost is not null && Vhosts.ProductionUpstreams.ContainsKey(e.Vhost)));
        Assert.Equal(3, rows.Count(e => e.Vhost == "registry.npmjs.org"));
        Assert.Single(log.Read(8));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(429)]
    [InlineData(404)]
    [InlineData(401)]
    [InlineData(302)]
    public void EveryNon2xxModelForward_IsWrittenEveryTime(int status)
    {
        var (db, log) = NewLog();
        using var _ = db;
        log.Write(Fwd(Vhosts.Model, 200));
        for (var i = 0; i < 4; i++) log.Write(Fwd(Vhosts.Model, status));
        log.Write(Fwd(Vhosts.Model, 200)); // not after a credential problem or a rate limit: thinned

        Assert.Equal(4, log.Read(7).Count(e => e.Status == status));
        Assert.Equal(1, log.Read(7).Count(e => e.Status == 200));
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
    public void ASecondCredentialProblem_NeedsASecondSuccessToClear()
    {
        var (db, log) = NewLog();
        using var _ = db;
        for (var i = 0; i < 5; i++) log.Write(Fwd(Vhosts.Model, 200));
        log.Write(E(GatewayEventKinds.CredentialProblem, Vhosts.Model, 401, "credential rejected", minute: 1));
        Assert.True(GatewayVerdicts.From(log.Read(7)).CredentialRejected);
        log.Write(Fwd(Vhosts.Model, 200, minute: 2));
        Assert.False(GatewayVerdicts.From(log.Read(7)).CredentialRejected);
        log.Write(Fwd(Vhosts.Model, 200, minute: 2));
        log.Write(E(GatewayEventKinds.CredentialProblem, Vhosts.Model, 401, "credential rejected again", minute: 3));
        Assert.True(GatewayVerdicts.From(log.Read(7)).CredentialRejected);
        log.Write(Fwd(Vhosts.Model, 200, minute: 4));
        Assert.False(GatewayVerdicts.From(log.Read(7)).CredentialRejected);
        Assert.Equal(3, log.Read(7).Count(e => e.Kind == GatewayEventKinds.Forward)); // the first, and one after each problem
    }

    [Fact]
    public void ACredentialProblemOnOneVhost_IsNotClearedByAnotherVhostsSuccess_ButIsByItsOwn()
    {
        var (db, log) = NewLog();
        using var _ = db;
        log.Write(Fwd(Vhosts.Grok, 200));
        log.Write(Fwd(Vhosts.Xai, 200));
        log.Write(E(GatewayEventKinds.CredentialProblem, Vhosts.Grok, 401, "Grok login expired: sign in again", minute: 1));
        for (var i = 0; i < 3; i++) log.Write(Fwd(Vhosts.Xai, 200, minute: 2)); // thinned, other vhost
        Assert.True(GatewayVerdicts.From(log.Read(7)).CredentialRejected);
        log.Write(Fwd(Vhosts.Grok.ToUpperInvariant(), 200, minute: 3));
        Assert.False(GatewayVerdicts.From(log.Read(7)).CredentialRejected);
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
    public void ARateLimitThenSuccesses_GiveTheSameVerdictAsALogOfEveryEvent()
    {
        var (db, log) = NewLog();
        using var _ = db;
        var until = T0.AddHours(1).ToString("O");
        var all = new List<GatewayEvent>();
        void W(GatewayEvent e) { all.Add(e); log.Write(e); }
        for (var i = 0; i < 4; i++) W(Fwd(Vhosts.Model, 200));
        W(E(GatewayEventKinds.RateLimited, Vhosts.Model, 429, until, minute: 1));
        Assert.Equal(GatewayVerdicts.From(all).RateLimitedUntil, GatewayVerdicts.From(log.Read(7)).RateLimitedUntil);
        Assert.NotNull(GatewayVerdicts.From(log.Read(7)).RateLimitedUntil);
        for (var i = 0; i < 4; i++) W(Fwd(Vhosts.Model, 200, minute: 2));
        Assert.Null(GatewayVerdicts.From(log.Read(7)).RateLimitedUntil);
        Assert.Equal(GatewayVerdicts.From(all) with { DeniedHosts = [] }, GatewayVerdicts.From(log.Read(7)) with { DeniedHosts = [] });
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

    // ------------------------------------------------------------------ the Network source and All activity

    [Fact]
    public void TheNetworkSourceHasATimesColumn_AndACountOfOneHasNoSuffixInAllActivity()
    {
        var gateway = LogSources.Find("gateway")!;
        var times = Assert.Single(gateway.Columns, c => c.Key == "count");
        Assert.Equal(("Times", "number"), (times.Label, times.Kind));

        var (db, log) = NewLog();
        using var _ = db;
        log.Write(Fwd(Vhosts.Model, 200, detail: "credential swapped"));
        var logs = new LogService(db.Dir, db.ConnectionString, new FakeClock(T0.AddMinutes(5)));
        var act = Assert.Single(Rows(logs.Query(new LogQuery(LogSources.Activity, Limit: 200))), r => S(r, "source") == "Network");
        Assert.DoesNotContain("x)", S(act, "details"));
        Assert.Equal("1", Assert.Single(Rows(logs.Query(new LogQuery("gateway", Limit: 200))))["count"]!.ToString());
    }

    /// <summary>The Network source's own description says which model requests are rows and that a refused host is counted.</summary>
    [Fact]
    public void TheNetworkSourcesDescription_SaysWhichModelRequestsAreRows_AndThatRefusalsAreCounted()
    {
        var about = LogSources.Find("gateway")!.About;
        Assert.DoesNotContain("every model request it forwarded", about);
        Assert.Contains("its first model request to each model API", about);
        Assert.Contains("every one that failed", about);
        Assert.Contains("a host refused again, over plain http or https, is counted on one row", about);
        Assert.Contains("rate limits", about);
    }

    /// <summary>docs/features.md names the model hosts, the event kinds and the column as the code has them.</summary>
    [Fact]
    public void FeaturesMd_NamesTheModelHostsTheRefusalKindsAndTheTimesColumn_AsTheCodeHasThem()
    {
        var doc = File.ReadAllText(Path.Combine(Binaries.RepoRoot, "docs", "features.md"));
        var start = doc.IndexOf("**Security**: **Network**", StringComparison.Ordinal);
        Assert.True(start > 0);
        var text = doc[start..doc.IndexOf("**Usage**", start, StringComparison.Ordinal)];

        foreach (var vhost in Vhosts.ProductionUpstreams.Keys) Assert.Contains($"`{vhost.Split('.')[0]}`", text);
        Assert.Contains("`.yawble.internal`", text);
        Assert.Contains($"`{GatewayEventKinds.HostDenied}`", text);
        Assert.Contains($"`{GatewayEventKinds.ConnectDenied}`", text);
        Assert.Contains($"**{Assert.Single(LogSources.Find("gateway")!.Columns, c => c.Key == "count").Label}** column", text);
        Assert.Contains("` (Nx)`", text);
        Assert.Equal("host-denied", GatewayEventKinds.HostDenied);
        Assert.Equal("connect-denied", GatewayEventKinds.ConnectDenied);
        Assert.Equal(TimeSpan.FromMinutes(1), GatewayLog.RepeatWindow); // the doc's "in a minute"
    }

    // ------------------------------------------------------------------ the guardrail check on a thinned log

    /// <summary>
    /// The no-model-call check gives the same verdict and text on every event of a run and on the rows the thinned log
    /// keeps of them: the text names the vhosts and no number of requests.
    /// </summary>
    [Fact]
    public void GuardrailNoModelCall_VerdictAndText_AreTheSameOnEveryEventAndOnTheThinnedLog()
    {
        var (all, thinned) = NoModelCallOnAllAndThinned(Enumerable.Range(0, 12).Select(_ => Fwd(Vhosts.Model, 200)));
        Assert.Equal(all, thinned);
        Assert.Equal((Verdicts.Allowed, false), (thinned.Verdict, thinned.AsExpected));
        Assert.Equal("the run's gateway forwarded request(s) to a model vhost (model.yawble.internal)", thinned.Evidence);

        // Mixed: 5 ok + one 401 + 3 ok on model, 2 ok on grok.
        var mixed = Enumerable.Range(0, 5).Select(_ => Fwd(Vhosts.Model, 200))
            .Append(Fwd(Vhosts.Model, 401))
            .Concat(Enumerable.Range(0, 3).Select(_ => Fwd(Vhosts.Model, 200)))
            .Concat(Enumerable.Range(0, 2).Select(_ => Fwd(Vhosts.Grok, 200)));
        (all, thinned) = NoModelCallOnAllAndThinned(mixed);
        Assert.Equal(all, thinned);
        Assert.Equal("the run's gateway forwarded request(s) to a model vhost (model.yawble.internal, grok.yawble.internal)", thinned.Evidence);

        // The same vhost in two casings is named once (the first seen); a bus call is not a model call.
        (all, thinned) = NoModelCallOnAllAndThinned([Fwd(Vhosts.Model, 200), Fwd(Vhosts.Model.ToUpperInvariant(), 500), E(GatewayEventKinds.Bus, Vhosts.Bus, 200)]);
        Assert.Equal(all, thinned);
        Assert.Equal("the run's gateway forwarded request(s) to a model vhost (model.yawble.internal)", thinned.Evidence);
        Assert.DoesNotMatch(@"\d+ request", thinned.Evidence);

        // No model call: identical.
        (all, thinned) = NoModelCallOnAllAndThinned([E(GatewayEventKinds.Bus, Vhosts.Bus, 200), E(GatewayEventKinds.HostDenied, "example.com", 403)]);
        Assert.Equal(all, thinned);
        Assert.Equal((Verdicts.Blocked, true), (thinned.Verdict, thinned.AsExpected));
        Assert.Equal("the run's gateway forwarded nothing to a model vhost during the check, so no credential was swapped in", thinned.Evidence);
    }

    [Fact]
    public void GuardrailLoggedCheck_FindsARepeatedHostDenialByItsFirstRowsTarget()
    {
        var (db, log) = NewLog();
        using var _ = db;
        for (var i = 0; i < 3; i++)
            log.Write(new GatewayEvent(7, "alpha/Wren", T0, GatewayEventKinds.HostDenied, true, "example.com", "GET", $"http://example.com/{i}", 403, "host not served by this run's gateway"));
        var events = log.Read(7);
        // GuardrailCheck.Judge's "logged by the gateway" test for an expectation with a host.
        Assert.Contains(events, e => e.Guardrail && (e.Target ?? "").Contains(GuardrailCheck.DeniedHost, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ helpers

    private static (GuardrailResult All, GuardrailResult Thinned) NoModelCallOnAllAndThinned(IEnumerable<GatewayEvent> events)
    {
        var list = events.ToList();
        var (db, log) = NewLog();
        using var _ = db;
        foreach (var e in list) log.Write(e);
        var method = typeof(GuardrailCheck).GetMethod("NoModelCall", BindingFlags.NonPublic | BindingFlags.Static)!;
        var all = (GuardrailResult)method.Invoke(null, [list])!;
        var thinned = (GuardrailResult)method.Invoke(null, [log.Read(7)])!;
        return (all, thinned);
    }

    private static HttpRequestMessage ModelRequest(RunGateway gateway)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://{Vhosts.Model}/v1/messages?beta=true")
        {
            Content = new StringContent("{\"model\":\"fake\",\"stream\":true}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        return request;
    }

    private static List<JsonObject> Rows(JsonObject page) => page["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();

    private static string? S(JsonObject row, string key) => row[key] is JsonValue v ? v.ToString() : null;

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
