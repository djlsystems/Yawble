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
using Yawble.Core.Storage;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// The tester's own checks of 447bb5f (GatewayLog thinning, host-denied count, 0050, Network
/// Times column and " (Nx)") and 9d32165 (0051, docs/features.md Logs > Network). Fake upstream, fake secrets, temp
/// databases only.
/// </summary>
public class NetworkLogThinningVerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static GatewayEvent Fwd(string vhost, int status, long run = 40, int minute = 0, string? detail = null) =>
        new(run, "beta/Tess", T0.AddMinutes(minute), GatewayEventKinds.Forward, false, vhost, "POST", "/v1/messages", status, detail);

    private static GatewayEvent Ev(string kind, string? vhost, int? status, string? detail = null, long run = 40, int minute = 0) =>
        new(run, "beta/Tess", T0.AddMinutes(minute), kind, false, vhost, "POST", "/v1/messages", status, detail);

    // ------------------------------------------------------------------ point 1: 50 model requests -> 1 forward + 50 usage

    [Fact]
    public async Task P1_FiftyModelRequestsThroughTheGateway_OneForwardRow_FiftyUsageRows()
    {
        await using var f = await GatewayFixture.StartAsync();
        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        await using var gateway = f.StartRun(464);
        using var http = ProxyClient.For(gateway);
        f.Upstream.ReleaseStream();

        for (var i = 0; i < 50; i++)
        {
            using var r = await http.SendAsync(Model(gateway), Ct);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            await r.Content.ReadAsStringAsync(Ct);
        }

        await Wait.UntilAsync(() => Scalar(f.Db.ConnectionString, "SELECT count(*) FROM usage_events WHERE run_key = 'run-464';") == 50, "50 usage rows");
        Assert.Equal(50, f.Upstream.Requests.Count); // every request really went upstream
        Assert.Equal(1, Scalar(f.Db.ConnectionString, "SELECT count(*) FROM gateway_events WHERE run_id = 464 AND kind = 'forward';"));
        Assert.Equal(0, Scalar(f.Db.ConnectionString, "SELECT count(*) FROM gateway_events WHERE run_id = 464 AND kind = 'forward' AND status <> 200;"));
    }

    [Fact]
    public void P1_PerRunAndPerModelVhost_CaseInsensitive_NonModelVhostsUnthinned()
    {
        using var db = new TempDb();
        var log = new GatewayLog(db.ConnectionString, new FakeClock(T0));
        foreach (var vhost in Vhosts.ProductionUpstreams.Keys)
            for (var i = 0; i < 5; i++) log.Write(Fwd(i % 2 == 0 ? vhost : vhost.ToUpperInvariant(), 200 + i % 2)); // 200/201, mixed case
        for (var i = 0; i < 3; i++) log.Write(Fwd("registry.npmjs.org", 200));
        for (var i = 0; i < 3; i++) log.Write(Fwd(Vhosts.Model, 200, run: 41));
        var rows = log.Read(40);
        Assert.Equal(Vhosts.ProductionUpstreams.Count, rows.Count(e => IsModel(e.Vhost)));
        Assert.Equal(3, rows.Count(e => e.Vhost == "registry.npmjs.org"));
        Assert.Single(log.Read(41));
    }

    // ------------------------------------------------------------------ point 2: a failed request is still written

    [Fact]
    public async Task P2_A401ThroughTheGateway_IsWritten_AmongThinnedSuccesses()
    {
        await using var f = await GatewayFixture.StartAsync();
        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        await using var gateway = f.StartRun(465);
        using var http = ProxyClient.For(gateway);
        f.Upstream.ReleaseStream();
        for (var i = 0; i < 4; i++) (await http.SendAsync(Model(gateway), Ct)).Dispose();
        f.Upstream.RefuseAll = true;
        for (var i = 0; i < 2; i++) (await http.SendAsync(Model(gateway), Ct)).Dispose();

        var forwards = f.Log.Read(465).Where(e => e.Kind == GatewayEventKinds.Forward).Select(e => e.Status).ToList();
        Assert.Equal([200, 401, 401], forwards);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(429)]
    [InlineData(404)]
    [InlineData(302)]
    public void P2_EveryNon2xxModelForward_IsWrittenEveryTime(int status)
    {
        using var db = new TempDb();
        var log = new GatewayLog(db.ConnectionString, new FakeClock(T0));
        log.Write(Fwd(Vhosts.Model, 200));
        for (var i = 0; i < 4; i++) log.Write(Fwd(Vhosts.Model, status));
        log.Write(Fwd(Vhosts.Model, 200)); // not after a credential problem / rate limit: thinned
        Assert.Equal(4, log.Read(40).Count(e => e.Status == status));
        Assert.Equal(1, log.Read(40).Count(e => e.Status == 200));
    }

    // ------------------------------------------------------------------ point 3: problem then success clears the verdict

    [Fact]
    public void P3_CredentialProblem_ThenSuccess_Clears_AndASecondProblemNeedsASecondSuccess()
    {
        using var db = new TempDb();
        var log = new GatewayLog(db.ConnectionString, new FakeClock(T0));
        for (var i = 0; i < 5; i++) log.Write(Fwd(Vhosts.Model, 200));
        log.Write(Ev(GatewayEventKinds.CredentialProblem, Vhosts.Model, 401, "credential rejected", minute: 1));
        Assert.True(GatewayVerdicts.From(log.Read(40)).CredentialRejected);
        log.Write(Fwd(Vhosts.Model, 200, minute: 2));
        Assert.False(GatewayVerdicts.From(log.Read(40)).CredentialRejected);
        log.Write(Fwd(Vhosts.Model, 200, minute: 2));
        log.Write(Ev(GatewayEventKinds.CredentialProblem, Vhosts.Model, 401, "credential rejected again", minute: 3));
        Assert.True(GatewayVerdicts.From(log.Read(40)).CredentialRejected);
        log.Write(Fwd(Vhosts.Model, 200, minute: 4));
        Assert.False(GatewayVerdicts.From(log.Read(40)).CredentialRejected);
        Assert.Equal(3, log.Read(40).Count(e => e.Kind == GatewayEventKinds.Forward)); // first, after #1, after #2
    }

    [Fact]
    public void P3_ProblemOnOneVhost_IsNotClearedByAnotherVhostsThinnedSuccess_ButIsByItsOwn()
    {
        using var db = new TempDb();
        var log = new GatewayLog(db.ConnectionString, new FakeClock(T0));
        log.Write(Fwd(Vhosts.Grok, 200));
        log.Write(Fwd(Vhosts.Xai, 200));
        log.Write(Ev(GatewayEventKinds.CredentialProblem, Vhosts.Grok, 401, "Grok login expired: sign in again", minute: 1));
        for (var i = 0; i < 3; i++) log.Write(Fwd(Vhosts.Xai, 200, minute: 2)); // thinned, other vhost
        Assert.True(GatewayVerdicts.From(log.Read(40)).CredentialRejected);
        log.Write(Fwd(Vhosts.Grok.ToUpperInvariant(), 200, minute: 3));
        Assert.False(GatewayVerdicts.From(log.Read(40)).CredentialRejected);
    }

    [Fact]
    public void P3_RateLimit_ThenSuccess_Clears_SameAsUnthinnedLog()
    {
        using var db = new TempDb();
        var log = new GatewayLog(db.ConnectionString, new FakeClock(T0));
        var until = T0.AddHours(1).ToString("O");
        var all = new List<GatewayEvent>();
        void W(GatewayEvent e) { all.Add(e); log.Write(e); }
        for (var i = 0; i < 4; i++) W(Fwd(Vhosts.Model, 200));
        W(Ev(GatewayEventKinds.RateLimited, Vhosts.Model, 429, until, minute: 1));
        Assert.Equal(GatewayVerdicts.From(all).RateLimitedUntil, GatewayVerdicts.From(log.Read(40)).RateLimitedUntil);
        Assert.NotNull(GatewayVerdicts.From(log.Read(40)).RateLimitedUntil);
        for (var i = 0; i < 4; i++) W(Fwd(Vhosts.Model, 200, minute: 2));
        Assert.Null(GatewayVerdicts.From(log.Read(40)).RateLimitedUntil);
        Assert.Equal(GatewayVerdicts.From(all) with { DeniedHosts = [] }, GatewayVerdicts.From(log.Read(40)) with { DeniedHosts = [] });
    }

    // ------------------------------------------------------------------ point 4: 5 denials of one host -> one row count 5

    [Fact]
    public async Task P4_FivePlainHttpDenialsOfOneHost_OneRowCountFive_InTimesColumnAndAllActivity()
    {
        await using var f = await GatewayFixture.StartAsync();
        await using var gateway = f.StartRun(466);
        for (var i = 0; i < 5; i++)
        {
            var (status, _, client) = await ProxyClient.RawAsync(gateway, $"GET http://denied.example/x{i} HTTP/1.1\r\nHost: denied.example\r\n\r\n");
            client.Dispose();
            Assert.StartsWith("HTTP/1.1 403", status);
        }
        var row = Assert.Single(f.Log.Read(466), e => e.Kind == GatewayEventKinds.HostDenied);
        Assert.Equal(5, row.Count);
        Assert.Equal("http://denied.example/x0", row.Target);

        var logs = new LogService(f.Db.Dir, f.Db.ConnectionString, new FakeClock(DateTimeOffset.UtcNow.AddMinutes(5)));
        var net = Assert.Single(Rows(logs.Query(new LogQuery("gateway", Limit: 200))), r => S(r, "kind") == GatewayEventKinds.HostDenied);
        Assert.Equal("5", net["count"]!.ToString());
        var act = Assert.Single(Rows(logs.Query(new LogQuery(LogSources.Activity, Limit: 200))), r => S(r, "source") == "Network" && S(r, "event") == GatewayEventKinds.HostDenied);
        Assert.EndsWith(" (5x)", S(act, "details"));
        Assert.Equal("GET http://denied.example/x0 403 host not served by this run's gateway (5x)", S(act, "details"));
    }

    [Fact]
    public void P4_Network_TimesColumnIsDescribed_AndACountOfOneHasNoSuffix()
    {
        var gateway = LogSources.Find("gateway")!;
        var times = Assert.Single(gateway.Columns, c => c.Key == "count");
        Assert.Equal(("Times", "number"), (times.Label, times.Kind));

        using var db = new TempDb();
        var log = new GatewayLog(db.ConnectionString, new FakeClock(T0));
        log.Write(Fwd(Vhosts.Model, 200, detail: "credential swapped"));
        var logs = new LogService(db.Dir, db.ConnectionString, new FakeClock(T0.AddMinutes(5)));
        var act = Assert.Single(Rows(logs.Query(new LogQuery(LogSources.Activity, Limit: 200))), r => S(r, "source") == "Network");
        Assert.DoesNotContain("x)", S(act, "details"));
        Assert.Equal("1", Assert.Single(Rows(logs.Query(new LogQuery("gateway", Limit: 200))))["count"]!.ToString());
    }

    /// <summary>
    /// A CONNECT refusal (https to a host off the allowlist, kind connect-denied) refused again in the
    /// run is counted on its first row, as host-denied is (it was five rows, each count 1, under the repeat window).
    /// </summary>
    [Fact]
    public async Task P4_FiveConnectDenialsOfOneHost_OneRowCountFive()
    {
        await using var f = await GatewayFixture.StartAsync();
        await using var gateway = f.StartRun(467);
        for (var i = 0; i < 5; i++)
        {
            var (status, _, client) = await ProxyClient.RawAsync(gateway, "CONNECT blocked.example:443 HTTP/1.1\r\nHost: blocked.example:443\r\n\r\n");
            client.Dispose();
            Assert.StartsWith("HTTP/1.1 403", status);
        }
        var row = Assert.Single(f.Log.Read(467), e => e.Kind == GatewayEventKinds.ConnectDenied);
        Assert.Equal(5, row.Count);
        Assert.Equal("blocked.example:443", row.Target);

        var logs = new LogService(f.Db.Dir, f.Db.ConnectionString, new FakeClock(DateTimeOffset.UtcNow.AddMinutes(5)));
        var net = Assert.Single(Rows(logs.Query(new LogQuery("gateway", Limit: 200))), r => S(r, "kind") == GatewayEventKinds.ConnectDenied);
        Assert.Equal("5", net["count"]!.ToString());
        var act = Assert.Single(Rows(logs.Query(new LogQuery(LogSources.Activity, Limit: 200))), r => S(r, "source") == "Network" && S(r, "event") == GatewayEventKinds.ConnectDenied);
        Assert.EndsWith(" (5x)", S(act, "details"));
    }

    // ------------------------------------------------------------------ point 5: migrations 0050 + 0051

    [Fact]
    public void P5_FreshDatabase_AppliesBoth_CountColumnDefaultsToOne()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-464-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "fresh.db")};Pooling=False";
        try
        {
            var applied = SchemaMigrator.Migrate(cs);
            Assert.Contains("0050_gateway_event_count", applied);
            Assert.Contains("0051_gateway_model_forwards", applied);
            Assert.Equal(SchemaMigrator.Available, applied);
            Assert.Equal(1, Scalar(cs, "SELECT count(*) FROM pragma_table_info('gateway_events') WHERE name = 'count' AND \"notnull\" = 1 AND dflt_value = '1';"));
            Exec(cs, "INSERT INTO gateway_events (run_id, member, at, kind, guardrail) VALUES (1, 'a/b', '2026-11-18T00:00:00Z', 'bus', 0);");
            Assert.Equal(1, Scalar(cs, "SELECT count FROM gateway_events;"));
            Assert.Equal(SchemaMigrator.Available, SchemaMigrator.Migrate(cs)); // again: nothing new, no error
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void P5_ExistingPre0050Database_GainsCount_KeepsEachRunsFirstForwardPerModelVhost_RestUntouched()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-464-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "old.db")};Pooling=False";
        try
        {
            // The database as a build before the log thinning left it: every migration up to 0049, applied the migrator's way.
            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                Exec(c, "PRAGMA foreign_keys=ON; CREATE TABLE schema_migrations (name TEXT PRIMARY KEY NOT NULL, applied_at TEXT NOT NULL) STRICT;");
                foreach (var name in SchemaMigrator.Available.Where(n => string.CompareOrdinal(n, "0050") < 0))
                {
                    using var tx = c.BeginTransaction();
                    Exec(c, ReadMigration(name), tx);
                    Exec(c, $"INSERT INTO schema_migrations (name, applied_at) VALUES ('{name}', '2026-11-01T00:00:00Z');", tx);
                    tx.Commit();
                }
                Assert.Equal(0, Scalar(c, "SELECT count(*) FROM pragma_table_info('gateway_events') WHERE name = 'count';"));
            }

            var kept = new List<long>();
            var gone = new List<long>();
            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                // Run 1: 20 successful model requests on model, 3 on grok, a credential problem and a 401 in between.
                kept.Add(Row(c, 1, GatewayEventKinds.Forward, Vhosts.Model, 200));
                for (var i = 0; i < 9; i++) gone.Add(Row(c, 1, GatewayEventKinds.Forward, Vhosts.Model, 200));
                kept.Add(Row(c, 1, GatewayEventKinds.Forward, Vhosts.Model, 401));
                kept.Add(Row(c, 1, GatewayEventKinds.CredentialProblem, Vhosts.Model, 401));
                kept.Add(Row(c, 1, GatewayEventKinds.Forward, Vhosts.Model, 200)); // the success after the problem
                for (var i = 0; i < 9; i++) gone.Add(Row(c, 1, GatewayEventKinds.Forward, Vhosts.Model, 200));
                kept.Add(Row(c, 1, GatewayEventKinds.Forward, Vhosts.Grok, 200));
                gone.Add(Row(c, 1, GatewayEventKinds.Forward, Vhosts.Grok, 200));
                gone.Add(Row(c, 1, GatewayEventKinds.Forward, Vhosts.Grok, 200));
                // Run 2: its own first, then repeats.
                kept.Add(Row(c, 2, GatewayEventKinds.Forward, Vhosts.Model, 200));
                gone.Add(Row(c, 2, GatewayEventKinds.Forward, Vhosts.Model, 200));
                // Host denials from before 0050 (one row each), bus calls, connects, non-model forwards: all kept.
                for (var i = 0; i < 3; i++) kept.Add(Row(c, 1, GatewayEventKinds.HostDenied, "denied.example", 403));
                kept.Add(Row(c, 1, GatewayEventKinds.Bus, Vhosts.Bus, 200));
                kept.Add(Row(c, 1, GatewayEventKinds.ConnectAllowed, null, null));
                kept.Add(Row(c, 1, GatewayEventKinds.Forward, "registry.npmjs.org", 200));
                kept.Add(Row(c, 1, GatewayEventKinds.Forward, "registry.npmjs.org", 200));
            }

            var applied = SchemaMigrator.Migrate(cs);
            Assert.Equal(SchemaMigrator.Available, applied);

            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                Assert.Equal(kept.Order(), Ids(c));
                Assert.Equal(kept.Count, Scalar(c, "SELECT count(*) FROM gateway_events WHERE count = 1;"));
            }
            // The verdict over what is left: run 1's credential problem is cleared, as the old log said.
            Assert.False(GatewayVerdicts.From(new GatewayLog(cs).Read(1)).CredentialRejected);

            // The app writes on the migrated database: the count works, the old denial rows are left as they were.
            var log = new GatewayLog(cs, new FakeClock(T0));
            for (var i = 0; i < 4; i++) log.Write(Ev(GatewayEventKinds.HostDenied, "denied.example", 403, run: 3));
            Assert.Equal(4, Assert.Single(log.Read(3)).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// On an existing database 0051 keeps the first success after each credential problem / rate limit, so a
    /// run written before the log thinning whose problem was cleared by a success still reads as cleared (it read as rejected with
    /// 9d32165's 0051).
    /// </summary>
    [Fact]
    public void P5_0051_KeepsTheSuccessThatClearedAnOldCredentialProblemOrRateLimit()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-464-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "t.db")};Pooling=False";
        try
        {
            SchemaMigrator.Migrate(cs);
            using var c = new SqliteConnection(cs);
            c.Open();
            Exec(c, "DELETE FROM schema_migrations WHERE name = '0051_gateway_model_forwards';");
            Row(c, 9, GatewayEventKinds.Forward, Vhosts.Model, 200);
            Row(c, 9, GatewayEventKinds.CredentialProblem, Vhosts.Model, 401);
            Row(c, 9, GatewayEventKinds.Forward, Vhosts.Model, 200);
            Row(c, 9, GatewayEventKinds.Forward, Vhosts.Model, 200);
            Row(c, 10, GatewayEventKinds.Forward, Vhosts.Grok, 200);
            Row(c, 10, GatewayEventKinds.RateLimited, Vhosts.Grok, 429);
            Row(c, 10, GatewayEventKinds.Forward, Vhosts.Grok, 200);
            var log = new GatewayLog(cs);
            var before9 = GatewayVerdicts.From(log.Read(9));
            var before10 = GatewayVerdicts.From(log.Read(10));
            Assert.False(before9.CredentialRejected);
            SchemaMigrator.Migrate(cs);
            Assert.Equal(1, Scalar(c, "SELECT count(*) FROM schema_migrations WHERE name = '0051_gateway_model_forwards';"));
            Assert.Equal(before9 with { DeniedHosts = [] }, GatewayVerdicts.From(log.Read(9)) with { DeniedHosts = [] });
            Assert.Equal(before10 with { DeniedHosts = [] }, GatewayVerdicts.From(log.Read(10)) with { DeniedHosts = [] });
            Assert.Equal(3, log.Read(9).Count); // first ok, problem, the ok after it; the second ok after it is gone
            Assert.Equal(3, log.Read(10).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 0051 against the live rule: random logs from before the thinning (every event a row) through the migration give exactly the rows
    /// GatewayLog.Write keeps for the same events. Runs, mixed-case model vhosts, non-model vhosts, non-2xx forwards,
    /// credential problems and rate limits on either vhost, and rows with no run.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void P5_0051_EqualsTheLiveGatewayLogRule_OnRandomLogs(int seed)
    {
        var rnd = new Random(seed);
        string[] vhosts = [Vhosts.Model, Vhosts.Model.ToUpperInvariant(), Vhosts.Grok, Vhosts.Xai, "registry.npmjs.org"];
        long?[] runs = [1, 2, 3, null];
        var events = new List<GatewayEvent>();
        for (var i = 0; i < 400; i++)
        {
            var run = runs[rnd.Next(runs.Length)];
            var vhost = vhosts[rnd.Next(vhosts.Length)];
            var roll = rnd.Next(20);
            events.Add(roll switch
            {
                0 => new(run, "beta/Tess", T0, GatewayEventKinds.CredentialProblem, false, vhost, "POST", "/v1/messages", 401, null),
                1 => new(run, "beta/Tess", T0, GatewayEventKinds.RateLimited, false, vhost, "POST", "/v1/messages", 429, null),
                2 => new(run, "beta/Tess", T0, GatewayEventKinds.Forward, false, vhost, "POST", "/v1/messages", 500, null),
                _ => new(run, "beta/Tess", T0, GatewayEventKinds.Forward, false, vhost, "POST", "/v1/messages", 200 + rnd.Next(2), null),
            });
        }

        var dir = Directory.CreateTempSubdirectory("yawble-464-").FullName;
        try
        {
            // Live: GatewayLog.Write on a fresh database.
            var live = $"Data Source={Path.Combine(dir, "live.db")};Pooling=False";
            SchemaMigrator.Migrate(live);
            var log = new GatewayLog(live, new FakeClock(T0));
            foreach (var e in events) log.Write(e);

            // Migrated: every event a row, then 0051.
            var old = $"Data Source={Path.Combine(dir, "old.db")};Pooling=False";
            SchemaMigrator.Migrate(old);
            using (var c = new SqliteConnection(old))
            {
                c.Open();
                Exec(c, "DELETE FROM schema_migrations WHERE name = '0051_gateway_model_forwards';");
                foreach (var e in events) RowOf(c, e);
            }
            SchemaMigrator.Migrate(old);
            Assert.Equal(1, Scalar(old, "SELECT count(*) FROM schema_migrations WHERE name = '0051_gateway_model_forwards';"));

            var a = Rows(live);
            var b = Rows(old);
            Assert.True(b.Count < events.Count); // the migration did delete
            Assert.Equal(a, b);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }

        static List<string> Rows(string cs)
        {
            using var c = new SqliteConnection(cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT ifnull(run_id, -1) || '|' || kind || '|' || ifnull(vhost, '') || '|' || ifnull(status, '') FROM gateway_events ORDER BY id;";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    // ------------------------------------------------------------------ point 6: GuardrailCheck report

    /// <summary>
    /// The check's own guard on the events as the old log stored them (every one) vs as the thinned log stores them.
    /// The text names the vhosts and no number, so it is the same either way (it was "forwarded 12 request(s)"
    /// vs "forwarded 1 request(s)" before).
    /// </summary>
    [Fact]
    public void P6_GuardrailNoModelCall_VerdictAndText_SameOnFullAndThinnedLog()
    {
        var (before, after) = NoModelCallBeforeAfter(Enumerable.Range(0, 12).Select(_ => Fwd(Vhosts.Model, 200)));
        Assert.Equal(before, after);
        Assert.Equal((Verdicts.Allowed, false), (after.Verdict, after.AsExpected));
        Assert.Equal("the run's gateway forwarded request(s) to a model vhost (model.yawble.internal)", after.Evidence);

        // Mixed: 5 ok + one 401 + 3 ok on model, 2 ok on grok.
        var mixed = Enumerable.Range(0, 5).Select(_ => Fwd(Vhosts.Model, 200))
            .Append(Fwd(Vhosts.Model, 401))
            .Concat(Enumerable.Range(0, 3).Select(_ => Fwd(Vhosts.Model, 200)))
            .Concat(Enumerable.Range(0, 2).Select(_ => Fwd(Vhosts.Grok, 200)));
        (before, after) = NoModelCallBeforeAfter(mixed);
        Assert.Equal(before, after);
        Assert.Equal("the run's gateway forwarded request(s) to a model vhost (model.yawble.internal, grok.yawble.internal)", after.Evidence);

        // The same vhost in two casings is named once (the first seen); a bus call is not a model call.
        (before, after) = NoModelCallBeforeAfter([Fwd(Vhosts.Model, 200), Fwd(Vhosts.Model.ToUpperInvariant(), 500), Ev(GatewayEventKinds.Bus, Vhosts.Bus, 200)]);
        Assert.Equal(before, after);
        Assert.Equal("the run's gateway forwarded request(s) to a model vhost (model.yawble.internal)", after.Evidence);
        Assert.DoesNotMatch(@"\d+ request", after.Evidence);

        // No model call: identical.
        (before, after) = NoModelCallBeforeAfter([Ev(GatewayEventKinds.Bus, Vhosts.Bus, 200), Ev(GatewayEventKinds.HostDenied, "example.com", 403)]);
        Assert.Equal(before, after);
        Assert.Equal((Verdicts.Blocked, true), (after.Verdict, after.AsExpected));
        Assert.Equal("the run's gateway forwarded nothing to a model vhost during the check, so no credential was swapped in", after.Evidence);
    }

    [Fact]
    public void P6_GuardrailLoggedCheck_FindsARepeatedHostDenialByItsFirstRowsTarget()
    {
        using var db = new TempDb();
        var log = new GatewayLog(db.ConnectionString, new FakeClock(T0));
        for (var i = 0; i < 3; i++)
            log.Write(new GatewayEvent(50, "beta/Tess", T0, GatewayEventKinds.HostDenied, true, "example.com", "GET", $"http://example.com/{i}", 403, "host not served by this run's gateway"));
        var events = log.Read(50);
        // GuardrailCheck.Judge's "logged by the gateway" test for an expectation with a host.
        Assert.Contains(events, e => e.Guardrail && (e.Target ?? "").Contains(GuardrailCheck.DeniedHost, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ point 7: docs vs code

    [Fact]
    public void P7_FeaturesMd_NetworkText_AgainstTheCode()
    {
        var doc = File.ReadAllText(Path.Combine(Binaries.RepoRoot, "docs", "features.md"));
        var start = doc.IndexOf("**Security**: **Network**", StringComparison.Ordinal);
        Assert.True(start > 0);
        var text = doc[start..doc.IndexOf("**Usage**", start, StringComparison.Ordinal)];
        // The model hosts it names are exactly the code's.
        foreach (var vhost in Vhosts.ProductionUpstreams.Keys) Assert.Contains($"`{vhost.Split('.')[0]}`", text);
        Assert.Contains("`.yawble.internal`", text);
        Assert.Contains("counted in **Model usage**", text);
        // Host-denied and connect-denied fold into one row; the Times column and " (Nx)" are described.
        Assert.Contains("A plain-http request to a host that is not allowed (`host-denied`)", text);
        Assert.Contains("(`connect-denied`), refused again in the same run, are both counted on one row", text);
        Assert.Contains("A host's plain-http and HTTPS refusals stay two rows", text);
        Assert.Contains("**Times** column", text);
        Assert.Contains("` (Nx)`", text);
        Assert.Contains("` (5x)` for five", text);
        Assert.Contains("Refused paths are not folded into one row", text);
        Assert.Contains("the first after each credential problem or rate limit on that\n    host", text.Replace("\r\n", "\n"));
        Assert.DoesNotContain("repeated refusals of one host in a run are counted on one row", text);
        // The names the doc uses are the code's: kinds, the Times label, the repeat limit's window.
        Assert.Equal("host-denied", GatewayEventKinds.HostDenied);
        Assert.Equal("connect-denied", GatewayEventKinds.ConnectDenied);
        Assert.Equal("Times", Assert.Single(LogSources.Find("gateway")!.Columns, c => c.Key == "count").Label);
        Assert.Equal(TimeSpan.FromMinutes(1), GatewayLog.RepeatWindow);

        // The app's own description of the Network source no longer says every model request is a row.
        var about = LogSources.Find("gateway")!.About;
        Assert.DoesNotContain("every model request it forwarded", about);
        Assert.Contains("its first model request to each model API", about);
        Assert.Contains("every one that failed", about);
        Assert.Contains("a host refused again, over plain http or https, is counted on one row", about);
        Assert.Contains("rate limits", about);
        // Observed (nit, not asserted as a fault): it does not mention the success kept after each problem, which the doc does.
        Assert.DoesNotContain("after", about);
    }

    // ------------------------------------------------------------------ helpers

    private static (GuardrailResult Before, GuardrailResult After) NoModelCallBeforeAfter(IEnumerable<GatewayEvent> events)
    {
        var list = events.ToList();
        using var db = new TempDb();
        var log = new GatewayLog(db.ConnectionString, new FakeClock(T0));
        foreach (var e in list) log.Write(e);
        var method = typeof(GuardrailCheck).GetMethod("NoModelCall", BindingFlags.NonPublic | BindingFlags.Static)!;
        var before = (GuardrailResult)method.Invoke(null, [list])!;  // what the old log stored: every event
        var after = (GuardrailResult)method.Invoke(null, [log.Read(40)])!;
        return (before, after);
    }

    private static bool IsModel(string? vhost) => vhost is not null && Vhosts.ProductionUpstreams.ContainsKey(vhost);

    private static HttpRequestMessage Model(RunGateway gateway)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://{Vhosts.Model}/v1/messages")
        {
            Content = new StringContent("{\"model\":\"fake\",\"stream\":true}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        return request;
    }

    private static List<JsonObject> Rows(JsonObject page) => page["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();

    private static string? S(JsonObject row, string key) => row[key] is JsonValue v ? v.ToString() : null;

    private static string ReadMigration(string name)
    {
        using var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("Yawble.Core.Migrations." + name + ".sql")!;
        return new StreamReader(stream).ReadToEnd();
    }

    private static long Row(SqliteConnection c, long run, string kind, string? vhost, int? status)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gateway_events (run_id, member, at, kind, guardrail, vhost, method, target, status)
            VALUES ($r, 'beta/Tess', '2026-11-10T10:00:00.0000000+00:00', $k, 0, $v, 'POST', '/v1/messages', $s);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$r", run);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$v", (object?)vhost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", (object?)status ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void RowOf(SqliteConnection c, GatewayEvent e)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gateway_events (run_id, member, at, kind, guardrail, vhost, method, target, status)
            VALUES ($r, 'beta/Tess', '2026-11-10T10:00:00.0000000+00:00', $k, 0, $v, 'POST', '/v1/messages', $s);
            """;
        cmd.Parameters.AddWithValue("$r", (object?)e.RunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", e.Kind);
        cmd.Parameters.AddWithValue("$v", (object?)e.Vhost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", (object?)e.Status ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static List<long> Ids(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM gateway_events ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    private static long Scalar(string cs, string sql)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        return Scalar(c, sql);
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Exec(string cs, string sql)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, sql);
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
