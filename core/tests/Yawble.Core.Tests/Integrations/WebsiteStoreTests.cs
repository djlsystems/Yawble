using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Website login Connections and Website integrations. A fake IWebsiteHost; no window, no network, no OS store.</summary>
public class WebsiteStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeHost : IWebsiteHost
    {
        public readonly List<WebsiteRequest> Requests = [];
        public Func<WebsiteRequest, WebsiteReply> Script { get; set; } = _ => new WebsiteReply(true, null, null, null, null, null, null);
        /// <summary>When set, every request waits for it (a window that is slow to answer).</summary>
        public TaskCompletionSource? Hold { get; set; }
        public bool Connected => true;
        public async Task<WebsiteReply> RunAsync(WebsiteRequest request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            if (Hold is { } hold) await hold.Task.WaitAsync(ct);
            return Script(request);
        }
        public WebsiteRequest[] Seen() { lock (Requests) return [.. Requests]; }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db = new();
        public BusEngine Engine = null!;
        public IntegrationService Svc = null!;
        public ConnectionService Conns = null!;
        public FakeHost Host = new();

        public static Task<Rig> Make()
        {
            var r = new Rig();
            r.Engine = new BusEngine(r.Db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
            r.Conns = new ConnectionService(new MemoryCredentialStore(), r.Db.ConnectionString);
            r.Svc = new IntegrationService(r.Db.ConnectionString, r.Engine, connections: r.Conns, websites: r.Host);
            r.Engine.Integrations = r.Svc;
            return Task.FromResult(r);
        }

        public async Task<JsonNode?> Rpc(string method, JsonObject? p = null) => await Svc.DispatchAsync(method, p, Ct);

        public async Task<JsonNode> Login(string site = "https://example.com/", string? name = null)
        {
            var p = new JsonObject { ["kind"] = "website", ["site"] = site };
            if (name is not null) p["name"] = name;
            return (await Rpc("integrations.connectionSave", p))!;
        }

        public async Task<JsonNode> Website(string name, long? connectionId, string url = "https://example.com/")
        {
            var p = new JsonObject { ["kind"] = "website", ["name"] = name, ["url"] = url };
            if (connectionId is { } c) p["connectionId"] = c;
            return (await Rpc("integrations.apiSave", p))!;
        }

        public async ValueTask DisposeAsync() { await Svc.DisposeAsync(); await Engine.DisposeAsync(); Db.Dispose(); }
    }

    [Fact]
    public async Task WebsiteLoginSavesASiteAndNoSecrets()
    {
        await using var r = await Rig.Make();
        var c = await r.Login("https://www.Example.com/app/?x=1#frag");
        Assert.Equal("website", c["kind"]!.GetValue<string>());
        Assert.Equal("www.example.com login", c["name"]!.GetValue<string>());
        var field = Assert.Single(((JsonArray)c["fields"]!).OfType<JsonObject>());
        Assert.Equal("site", field["key"]!.GetValue<string>());
        Assert.False(field["secret"]!.GetValue<bool>());
        Assert.Equal("https://www.example.com/app/", field["shown"]!.GetValue<string>());
        Assert.Empty(r.Conns.Secrets(c["id"]!.GetValue<long>()));

        foreach (var bad in new[] { "ftp://example.com/", "https://user:pw@example.com/", "example.com", "javascript:alert(1)" })
            await Assert.ThrowsAnyAsync<Exception>(() => r.Login(bad));
    }

    [Fact]
    public async Task TakenLoginNameGetsSuffix()
    {
        await using var r = await Rig.Make();
        var a = await r.Login("https://example.com/");
        var b = await r.Login("https://example.com/other");
        var c = await r.Login("https://example.com/third");
        Assert.Equal("example.com login", a["name"]!.GetValue<string>());
        Assert.Equal("example.com login 2", b["name"]!.GetValue<string>());
        Assert.Equal("example.com login 3", c["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task SignInOpensTheConnectionsOwnPartition()
    {
        await using var r = await Rig.Make();
        var id = (await r.Login())["id"]!.GetValue<long>();
        var res = await r.Rpc("integrations.websiteSignIn", new JsonObject { ["connectionId"] = id });
        Assert.True(res!["ok"]!.GetValue<bool>());
        var req = Assert.Single(r.Host.Seen());
        Assert.Equal("signIn", req.Op);
        Assert.Equal("persist:website-" + r.Conns.StoreKeyOf(id), req.Partition);
        Assert.Equal("https://example.com/", req.SiteUrl);
        Assert.Equal(id, req.Args["connectionId"]!.GetValue<long>()); // the window keys its sign-in window by it
        Assert.NotNull(req.Deadline);

        await r.Rpc("integrations.websiteSignInDone", new JsonObject { ["connectionId"] = id });
        Assert.Equal("signInClose", r.Host.Seen()[^1].Op);
        Assert.Equal(req.Partition, r.Host.Seen()[^1].Partition);
        Assert.Equal(id, r.Host.Seen()[^1].Args["connectionId"]!.GetValue<long>());
    }

    [Fact]
    public async Task SignInRefusesAConnectionThatIsNotAWebsiteLogin()
    {
        await using var r = await Rig.Make();
        var key = r.Conns.Save(null, "A key", [new ConnectionField("token", "Token", true, null)], new Dictionary<string, string> { ["token"] = "sk_test_1234567890" });
        await Assert.ThrowsAnyAsync<Exception>(() => r.Rpc("integrations.websiteSignIn", new JsonObject { ["connectionId"] = key.Id }));
        Assert.Empty(r.Host.Seen());
    }

    [Fact]
    public async Task SignedInCheckInsideTheSiteMarksWorks()
    {
        await using var r = await Rig.Make();
        var id = (await r.Login())["id"]!.GetValue<long>();
        r.Host.Script = _ => new WebsiteReply(true, null, "https://example.com/account", "Account", null, null, null);
        var changed = 0;
        r.Conns.Changed = () => Interlocked.Increment(ref changed);
        await r.Svc.WebsiteSignedIn(id);
        Assert.Equal("works", r.Conns.Brief(id)!.Status);
        Assert.True(changed > 0);
        var req = r.Host.Seen().Single(x => x.Op == "check");
        Assert.Equal("persist:website-" + r.Conns.StoreKeyOf(id), req.Partition);
    }

    [Fact]
    public async Task SignedInCheckOutsideMarksNeedsReconnect()
    {
        await using var r = await Rig.Make();
        var id = (await r.Login())["id"]!.GetValue<long>();
        r.Host.Script = _ => new WebsiteReply(true, null, "https://login.other.example/signin", "Sign in", null, null, null);
        var changed = 0;
        r.Conns.Changed = () => Interlocked.Increment(ref changed);
        await r.Svc.WebsiteSignedIn(id);
        var b = r.Conns.Brief(id)!;
        Assert.Equal("needs_reconnect", b.Status);
        Assert.StartsWith("the site sent the page to login.other.example, outside example.com, at ", b.StatusReason);
        Assert.EndsWith(": sign in again", b.StatusReason);
        Assert.True(changed > 0);
        // a website login needing reconnect is in Needs You like any other Connection
        var attention = Assert.Single((JsonArray)(await r.Rpc("integrations.connectionAttention"))!)!;
        Assert.Equal(id, attention["id"]!.GetValue<long>());
        Assert.Equal(b.StatusReason, attention["reason"]!.GetValue<string>());
        Assert.Equal(id, Assert.Single(r.Svc.ConnectionsNeedingReconnect()).Id);

        // a stop (a redirect the window held) says the same
        r.Host.Script = _ => new WebsiteReply(true, null, null, null, null, null, "https://idp.test/login");
        await r.Svc.WebsiteSignedIn(id);
        Assert.Contains("idp.test", r.Conns.Brief(id)!.StatusReason);
    }

    [Fact]
    public async Task ConnectionTestRunsTheSameCheck()
    {
        await using var r = await Rig.Make();
        var id = (await r.Login())["id"]!.GetValue<long>();
        r.Host.Script = _ => new WebsiteReply(true, null, "https://example.com/", "Home", null, null, null);
        var res = await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id });
        Assert.True(res!["checking"]!.GetValue<bool>());
        Assert.False(res["changed"]!.GetValue<bool>());
        await r.Svc.LastWebsiteCheck!;
        Assert.Equal("works", r.Conns.Brief(id)!.Status);
        Assert.Equal("check", r.Host.Seen().Single().Op);
    }

    [Fact]
    public async Task ConnectionTestDoesNotWaitForTheWindow()
    {
        await using var r = await Rig.Make();
        var id = (await r.Login())["id"]!.GetValue<long>();
        r.Host.Script = _ => new WebsiteReply(true, null, "https://example.com/", "Home", null, null, null);
        r.Host.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // the window has not answered, yet the RPC (on the window's main connection) is back
        var res = await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id }).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(res!["checking"]!.GetValue<bool>());
        Assert.Equal("untested", r.Conns.Brief(id)!.Status);
        var changed = 0;
        r.Conns.Changed = () => Interlocked.Increment(ref changed);
        r.Host.Hold.SetResult();
        await r.Svc.LastWebsiteCheck!;
        Assert.Equal("works", r.Conns.Brief(id)!.Status);
        Assert.True(changed > 0); // the screen showing "checking" hears of it
    }

    [Fact]
    public async Task NoAnswerKeepsANeedsReconnectAndSaysWhyOtherwise()
    {
        await using var r = await Rig.Make();
        var id = (await r.Login())["id"]!.GetValue<long>();
        r.Svc.WebsiteLog = _ => { };
        r.Host.Script = _ => new WebsiteReply(false, "the page took too long to load", null, null, null, null, null);
        await r.Svc.WebsiteSignedIn(id);
        Assert.Equal("untested", r.Conns.Brief(id)!.Status);
        Assert.Equal("could not check the site: the page took too long to load", r.Conns.Brief(id)!.StatusReason);

        r.Conns.MarkStatus(id, "needs_reconnect", "the site sent the page to idp.test");
        var changed = 0;
        r.Conns.Changed = () => Interlocked.Increment(ref changed);
        await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id });
        await r.Svc.LastWebsiteCheck!;
        Assert.Equal("needs_reconnect", r.Conns.Brief(id)!.Status); // never cleared without proof
        Assert.Equal("the site sent the page to idp.test", r.Conns.Brief(id)!.StatusReason);
        Assert.True(changed > 0);
    }

    [Fact]
    public async Task AFailingCheckIsLoggedAndNeverThrown()
    {
        await using var r = await Rig.Make();
        var id = (await r.Login())["id"]!.GetValue<long>();
        r.Svc.WebsiteLog = _ => { };
        r.Host.Script = _ => throw new WebsiteException("website actions need the app to be open on this computer");
        await r.Svc.WebsiteSignedIn(id); // must complete without throwing
        Assert.Equal("untested", r.Conns.Brief(id)!.Status);
        Assert.Equal("could not check the site: website actions need the app to be open on this computer", r.Conns.Brief(id)!.StatusReason);
        await r.Svc.WebsiteSignedIn(9999); // a connection that is gone
    }

    [Fact]
    public async Task EditingALoginWithNoNameKeepsItsNameAndAnEmptyReplyProvesNothing()
    {
        await using var r = await Rig.Make();
        var c = await r.Login("https://example.com/", "My shop");
        var id = c["id"]!.GetValue<long>();
        var edited = (await r.Rpc("integrations.connectionSave", new JsonObject { ["kind"] = "website", ["id"] = id, ["site"] = "https://shop.example.com/" }))!;
        Assert.Equal("My shop", edited["name"]!.GetValue<string>());
        Assert.Equal("forget", Assert.Single(r.Host.Seen()).Op); // another host: the old site's cookies are cleared
        r.Svc.WebsiteLog = _ => { };
        await r.Svc.WebsiteSignedIn(id); // the fake answers Ok with no Url: nothing proved
        Assert.Equal("untested", r.Conns.Brief(id)!.Status);
        Assert.Equal("could not check the site: the window did not say where the page ended", r.Conns.Brief(id)!.StatusReason);
    }

    [Fact]
    public async Task ChangingTheSiteHostForgetsTheOldSession()
    {
        await using var r = await Rig.Make();
        var id = (await r.Login("https://example.com/"))["id"]!.GetValue<long>();
        var partition = "persist:website-" + r.Conns.StoreKeyOf(id);
        await r.Rpc("integrations.connectionSave", new JsonObject { ["kind"] = "website", ["id"] = id, ["site"] = "https://example.com/app/" });
        Assert.Empty(r.Host.Seen()); // the same host: the session is kept
        await r.Rpc("integrations.connectionSave", new JsonObject { ["kind"] = "website", ["id"] = id, ["site"] = "https://other.test/" });
        var forget = Assert.Single(r.Host.Seen());
        Assert.Equal("forget", forget.Op);
        Assert.Equal(partition, forget.Partition);
        Assert.Equal("untested", r.Conns.Brief(id)!.Status);
    }

    [Fact]
    public async Task WebsiteIntegrationDiscoversTheFixedSix()
    {
        await using var r = await Rig.Make();
        var team = await r.Engine.CreateTeamAsync("Alpha", ct: Ct);
        var conn = (await r.Login())["id"]!.GetValue<long>();
        var saved = await r.Website("Example", conn);
        Assert.Equal("Example", saved["name"]!.GetValue<string>());

        var s = await r.Rpc("integrations.discover", new JsonObject { ["name"] = "Example", ["kind"] = "api" });
        Assert.True(s!["started"]!.GetValue<bool>());
        await Wait.UntilAsync(async () => (await r.Rpc("integrations.discoverStatus", new JsonObject { ["name"] = "Example", ["kind"] = "api" }))!["state"]!.GetValue<string>() != "running", "discovered");
        await r.Rpc("integrations.apiEnable", new JsonObject { ["name"] = "Example", ["teamId"] = team });

        var list = (JsonObject)((JsonArray)(await r.Rpc("integrations.apiList"))!).Single()!;
        Assert.Equal("website", list["kind"]!.GetValue<string>());
        Assert.Equal(conn, list["connection"]!["id"]!.GetValue<long>());
        Assert.Equal("untested", list["connection"]!["status"]!.GetValue<string>());
        Assert.Null(list["signIn"]);

        var nodes = ((JsonArray)(await r.Rpc("integrations.checklist", new JsonObject { ["name"] = "Example", ["kind"] = "api", ["teamId"] = team }))!["nodes"]!).OfType<JsonObject>().ToList();
        Assert.Equal(6, nodes.Count);
        Assert.Equal(["click", "fill", "open", "read", "screenshot", "submit"], nodes.Select(n => n["path"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        foreach (var n in nodes)
        {
            var path = n["path"]!.GetValue<string>();
            var destructive = path is "click" or "fill" or "submit";
            Assert.Equal(destructive ? "destructive" : "readonly", n["class"]!.GetValue<string>());
            Assert.Equal(destructive ? "off" : "allow", n["suggested"]!.GetValue<string>());
            Assert.Equal("BROWSER", n["method"]!.GetValue<string>());
            Assert.Equal(path, n["route"]!.GetValue<string>());
        }
        Assert.Equal("Open a page of the site and read it", nodes.Single(n => n["path"]!.GetValue<string>() == "open")["summary"]!.GetValue<string>());
        Assert.Equal("can change things on the site", nodes.Single(n => n["path"]!.GetValue<string>() == "click")["reason"]!.GetValue<string>());
        Assert.Equal(3, nodes.Count(n => n["ticked"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task WebsiteMustUseAWebsiteConnection()
    {
        await using var r = await Rig.Make();
        var key = r.Conns.Save(null, "A key", [new ConnectionField("token", "Token", true, null)], new Dictionary<string, string> { ["token"] = "sk_test_1234567890" });
        await Assert.ThrowsAnyAsync<Exception>(() => r.Website("Example", key.Id));
        // none = a public site
        await r.Website("Public", null);
        Assert.Null(((JsonObject)((JsonArray)(await r.Rpc("integrations.apiList"))!).Single()!)["connection"]);
        // and an http API cannot take a website login
        var login = (await r.Login())["id"]!.GetValue<long>();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "Svc", ["baseUrl"] = "https://api.example.com", ["connectionId"] = login }));
        Assert.Contains("website", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task McpServerRefusesAWebsiteLogin()
    {
        await using var r = await Rig.Make();
        var login = (await r.Login())["id"]!.GetValue<long>();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => r.Rpc("integrations.mcpSave", new JsonObject
        {
            ["name"] = "web", ["transport"] = "http", ["url"] = "https://mcp.example.com/mcp", ["connectionId"] = login,
        }));
        Assert.Contains("is a website login", ex.Message);
    }

    [Fact]
    public async Task RemovingAWebsiteLoginForgetsTheSession()
    {
        await using var r = await Rig.Make();
        var id = (await r.Login())["id"]!.GetValue<long>();
        var partition = "persist:website-" + r.Conns.StoreKeyOf(id);
        var res = await r.Rpc("integrations.connectionRemove", new JsonObject { ["id"] = id });
        Assert.True(res!["ok"]!.GetValue<bool>());
        var req = Assert.Single(r.Host.Seen());
        Assert.Equal("forget", req.Op);
        Assert.Equal(partition, req.Partition);
        Assert.Null(r.Conns.Get(id));
    }

    [Fact]
    public async Task ARemovalStandsEvenIfForgetFails()
    {
        await using var r = await Rig.Make();
        var id = (await r.Login())["id"]!.GetValue<long>();
        r.Svc.WebsiteLog = _ => { };
        r.Host.Script = _ => throw new WebsiteException("website actions need the app to be open on this computer");
        var res = await r.Rpc("integrations.connectionRemove", new JsonObject { ["id"] = id });
        Assert.True(res!["ok"]!.GetValue<bool>());
        Assert.Null(r.Conns.Get(id));
    }

    [Fact]
    public async Task RemovingTheWebsiteWithItsLoginForgetsTheSessionToo()
    {
        await using var r = await Rig.Make();
        var id = (await r.Login())["id"]!.GetValue<long>();
        await r.Website("Example", id);
        var partition = "persist:website-" + r.Conns.StoreKeyOf(id);
        var res = await r.Rpc("integrations.apiRemove", new JsonObject { ["name"] = "Example", ["removeConnection"] = true });
        Assert.True(res!["connectionRemoved"]!.GetValue<bool>());
        Assert.Contains(r.Host.Seen(), x => x.Op == "forget" && x.Partition == partition);
    }

    [Fact]
    public async Task RemovingAWebsiteForgetsItsPublicSessionAndPages()
    {
        await using var r = await Rig.Make();
        await r.Website("Public", null, "https://public.example/");
        var login = (await r.Login())["id"]!.GetValue<long>();
        await r.Website("Signed", login);
        await r.Rpc("integrations.apiRemove", new JsonObject { ["name"] = "Public" });
        var f = Assert.Single(r.Host.Seen(), x => x.Op == "forget");
        Assert.StartsWith("persist:website-public-", f.Partition);
        var apiId = f.Partition["persist:website-public-".Length..];
        Assert.Equal(apiId + ":", f.Args["pagesOf"]!.GetValue<string>());

        // a website with a login (kept): its pages close, the login's session stays
        await r.Rpc("integrations.apiRemove", new JsonObject { ["name"] = "Signed" });
        var g = r.Host.Seen().Where(x => x.Op == "forget").ToList();
        Assert.Equal(2, g.Count);
        Assert.StartsWith("persist:website-public-", g[1].Partition);
        Assert.NotEqual(f.Partition, g[1].Partition);
        Assert.NotNull(r.Conns.Get(login));
    }

    [Fact]
    public void WithinSiteRules()
    {
        static bool In(string url, string site) => IntegrationService.WithinSite(new Uri(url), new Uri(site));
        Assert.True(In("https://example.com/a", "https://example.com/"));
        Assert.True(In("https://www.example.com/a", "https://example.com/"));
        Assert.True(In("https://example.com/a", "https://www.example.com/"));
        Assert.True(In("https://app.example.com/", "https://example.com/"));
        Assert.True(In("https://a.b.example.com/", "https://www.example.com/"));
        Assert.True(In("https://EXAMPLE.com/", "https://example.com/"));
        Assert.False(In("https://evil-example.com/", "https://example.com/"));
        Assert.False(In("https://example.com.evil.net/", "https://example.com/"));
        Assert.False(In("https://other.com/", "https://example.com/"));
        Assert.False(In("https://user@example.com/", "https://example.com/"));
        Assert.False(In("https://user:pw@example.com/", "https://example.com/"));
        Assert.False(In("http://example.com/", "https://example.com/"));
        Assert.True(In("http://example.com/", "http://example.com/"));
        Assert.True(In("https://example.com/", "http://example.com/"));
        Assert.False(In("ftp://example.com/", "https://example.com/"));
        Assert.False(In("file:///c:/x", "https://example.com/"));
        Assert.False(In("https://anything.com/", "https://www.com/")); // www.com is the host itself, not a prefix of all .com
        Assert.True(In("https://www.com/", "https://www.com/"));
        Assert.True(In("https://example.com:8443/", "https://example.com/")); // port ignored
        Assert.True(In("https://bücher.example/", "https://xn--bcher-kva.example/")); // IDN vs punycode
        Assert.True(In("https://xn--bcher-kva.example/", "https://bücher.example/"));
        Assert.False(IntegrationService.WithinSite(new Uri("/relative", UriKind.Relative), new Uri("https://example.com/")));
    }
}
