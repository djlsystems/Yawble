using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Connections;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Xunit;
using static Yawble.Core.Tests.Integrations.McpOAuthTests;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// The generic OAuth flow (a known client and known endpoints) and provider discovery (RFC 8414 / OpenID).
/// Every endpoint is a FakeHttp route; the browser and the redirect are fakes. No real browser, no port, no network, no OS store.
/// </summary>
public class OAuthFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Idp = "https://idp.example.test";
    private const string Authorize = "https://idp.example.test/oauth/authorize";
    private const string TokenUrl = "https://idp.example.test/oauth/token";
    private const string At1 = "at_ONE_generic_supersecret_0123";
    private const string At2 = "at_TWO_generic_supersecret_0123";
    private const string Rt1 = "rt_ONE_generic_supersecret_0123";
    private const string Rt2 = "rt_TWO_generic_supersecret_0123";
    private const string Secret = "cs_generic_supersecret_0123456789";
    private const string CodeValue = "code_supersecret_abcdef"; // what McpOAuthTests.FakeReceiver hands back

    private sealed class Rig : IDisposable
    {
        public TempDb Db = new();
        public MemoryCredentialStore Store = new();
        public ConnectionService Conns = null!;
        public FakeHttp Http = new();
        public FakeBrowser Browser = new();
        public ManualClock Clock = new();
        public FakeReceiver? Receiver;
        public List<Dictionary<string, string>> TokenForms = [];
        public string? SignInScope;   // the token response's scope on sign-in (null: none said)
        public string? RefreshScope;  // the token response's scope on refresh (null: none said)
        public OAuthFlow Flow = null!;

        public static Rig Make()
        {
            var r = new Rig();
            r.Conns = new ConnectionService(r.Store, r.Db.ConnectionString);
            r.Http.Add(TokenUrl, req =>
            {
                var form = Form(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                lock (r.TokenForms) r.TokenForms.Add(form);
                var refresh = form["grant_type"] == "refresh_token";
                var o = new JsonObject
                {
                    ["access_token"] = refresh ? At2 : At1, ["refresh_token"] = refresh ? Rt2 : Rt1, ["expires_in"] = 3600, ["token_type"] = "Bearer",
                };
                if ((refresh ? r.RefreshScope : r.SignInScope) is { } s) o["scope"] = s;
                return Json(o.ToJsonString());
            });
            r.Flow = new OAuthFlow(r.Http, r.Conns, r.Browser, () => r.Receiver = new FakeReceiver(r.Browser, McpOAuthTests.Answer.Code), r.Clock);
            return r;
        }

        public static OAuthClient Client(string? secret = null, string? resource = null, params string[] scopes) =>
            new("my-app-id", secret, new Uri(Authorize), new Uri(TokenUrl), scopes, resource, Idp, false);

        public Task<long> SignIn(OAuthClient client, long? existing = null, IReadOnlyList<ConnectionField>? extra = null) =>
            Flow.SignInAsync("GitHub", client, existing, extra ?? [], Ct);

        public void Dispose() => Db.Dispose();
    }

    /// <summary>The Connection's recorded grant (empty when it records none).</summary>
    private static IReadOnlySet<string> Granted(Rig r, long id) => OAuthFlow.GrantedOf(r.Conns.Get(id)!) ?? [];

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task GenericSignInUsesTheGivenClientAndPkce()
    {
        using var r = Rig.Make();
        var id = await r.SignIn(Rig.Client(scopes: ["repo", "read:user"]), extra: [new("api", "API", false, "github")]);

        var url = Assert.Single(r.Browser.Opened);
        Assert.StartsWith(Authorize + "?", url.AbsoluteUri);
        var q = Query(url);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("my-app-id", q["client_id"]);
        Assert.Equal("http://127.0.0.1:50123/callback", q["redirect_uri"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("repo read:user", q["scope"]);
        Assert.Equal(43, q["state"].Length);
        Assert.Equal(43, q["code_challenge"].Length);

        var form = Assert.Single(r.TokenForms);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(CodeValue, form["code"]);
        Assert.Equal("my-app-id", form["client_id"]);
        Assert.Equal("http://127.0.0.1:50123/callback", form["redirect_uri"]);
        Assert.Equal(q["code_challenge"], B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))));
        Assert.False(form.ContainsKey("client_secret"));
        Assert.True(r.Receiver!.Disposed);

        var view = Assert.Single(r.Conns.List());
        Assert.Equal(id, view.Id);
        Assert.Equal("oauth", view.Kind);
        Assert.Equal("GitHub", view.Name);
        Assert.Equal(["access_token", "refresh_token", "client_id", "token_endpoint", "authorize_endpoint", "expires_at", "granted_scopes", "api"],
            view.Fields.Select(f => f.Key));
        var f = view.Fields.ToDictionary(x => x.Key);
        Assert.True(f["access_token"].Secret);
        Assert.True(f["refresh_token"].Secret);
        Assert.Equal("my-app-id", f["client_id"].Shown);
        Assert.Equal(TokenUrl, f["token_endpoint"].Shown);
        Assert.Equal(Authorize, f["authorize_endpoint"].Shown);
        Assert.Equal("repo read:user", f["granted_scopes"].Shown); // the token response said nothing: what was asked
        Assert.Equal("github", f["api"].Shown);
        Assert.Equal(r.Clock.Now.AddSeconds(3600), DateTimeOffset.Parse(f["expires_at"].Shown!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(At1, await r.Flow.AccessTokenAsync(id, Ct));

        // the name is taken: the next one is "GitHub 2"; signing in again into the existing one keeps its name
        var second = await r.SignIn(Rig.Client(scopes: ["repo"]));
        Assert.Equal("GitHub 2", r.Conns.Get(second)!.Name);
        Assert.Equal(id, await r.SignIn(Rig.Client(scopes: ["repo"]), existing: id));
        Assert.Equal("GitHub", r.Conns.Get(id)!.Name);
        Assert.Equal(["repo"], Granted(r, id));
    }

    [Fact]
    public async Task ClientSecretIsSentAndStoredMasked()
    {
        using var r = Rig.Make();
        var id = await r.SignIn(Rig.Client(secret: Secret, scopes: ["read"]));

        Assert.DoesNotContain(Secret, r.Browser.Opened[0].AbsoluteUri);
        Assert.Equal(Secret, Assert.Single(r.TokenForms)["client_secret"]);
        var f = Assert.Single(r.Conns.List()).Fields.Single(x => x.Key == "client_secret");
        Assert.True(f.Secret);
        Assert.Equal(Secret, r.Conns.Secrets(id)["client_secret"]);
        var shown = System.Text.Json.JsonSerializer.Serialize(r.Conns.List());
        Assert.DoesNotContain(Secret, shown);
        Assert.DoesNotContain(At1, shown);
        Assert.DoesNotContain(Rt1, shown);
        Assert.DoesNotContain(Secret, SecretScrubber.Scrub("secret " + Secret));

        // the refresh sends it too
        r.Clock.Now = r.Clock.Now.AddHours(2);
        Assert.Equal(At2, await r.Flow.AccessTokenAsync(id, Ct));
        Assert.Equal(Secret, r.TokenForms[^1]["client_secret"]);
    }

    [Fact]
    public async Task GrantedScopesComeFromTheTokenResponse()
    {
        using var r = Rig.Make();
        r.SignInScope = "read  write"; // narrower than asked, and spaced oddly
        var id = await r.SignIn(Rig.Client(scopes: ["read", "write", "admin"]));
        Assert.Equal("read write", r.Conns.Get(id)!.Fields.Single(f => f.Key == "granted_scopes").Shown);
        var granted = Granted(r, id);
        Assert.True(granted.SetEquals(["read", "write"]));
        Assert.DoesNotContain("admin", granted);
        
        // GitHub separates with commas
        r.SignInScope = "repo,gist";
        var gh = await r.SignIn(Rig.Client(scopes: ["repo", "gist", "admin:org"]));
        Assert.Equal("repo gist", r.Conns.Get(gh)!.Fields.Single(f => f.Key == "granted_scopes").Shown);
        Assert.True(Granted(r, gh).SetEquals(["repo", "gist"]));

        // a scope that is there but lists nothing usable grants nothing (not "everything asked")
        r.SignInScope = " ,\u0001 ";
        var none = await r.SignIn(Rig.Client(scopes: ["read", "write"]));
        Assert.Empty(Granted(r, none));
        Assert.Equal("", r.Conns.Get(none)!.Fields.Single(f => f.Key == "granted_scopes").Shown);
    }

    [Fact]
    public async Task NothingAskedAndNothingNamedSavesNoGrant()
    {
        using var r = Rig.Make();
        var id = await r.SignIn(Rig.Client());
        Assert.DoesNotContain(r.Conns.Get(id)!.Fields, f => f.Key == "granted_scopes");
        Assert.Null(OAuthFlow.GrantedOf(r.Conns.Get(id)!)); // unknown, never "nothing"
    }

    [Fact]
    public async Task NoResourceWhenNotSet()
    {
        using var r = Rig.Make();
        var id = await r.SignIn(Rig.Client(scopes: ["read"]));
        Assert.False(Query(r.Browser.Opened[0]).ContainsKey("resource"));
        Assert.False(r.TokenForms[0].ContainsKey("resource"));
        Assert.DoesNotContain(r.Conns.Get(id)!.Fields, f => f.Key == "resource");
        r.Clock.Now = r.Clock.Now.AddHours(2);
        await r.Flow.AccessTokenAsync(id, Ct);
        Assert.False(r.TokenForms[^1].ContainsKey("resource"));

        // and when it is set, it is sent and kept
        var other = await r.SignIn(Rig.Client(resource: "https://api.example.test/", scopes: ["read"]));
        Assert.Equal("https://api.example.test/", Query(r.Browser.Opened[^1])["resource"]);
        Assert.Equal("https://api.example.test/", r.TokenForms[^1]["resource"]);
        Assert.Equal("https://api.example.test/", r.Conns.Get(other)!.Fields.Single(f => f.Key == "resource").Shown);
    }

    [Fact]
    public async Task ANonJsonTokenAnswerIsRefused()
    {
        using var r = Rig.Make();
        r.Http.Add(TokenUrl, "<html>ok</html>", "text/html");
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn(Rig.Client(scopes: ["read"])));
        Assert.Equal("the server's sign-in answer could not be read; sign in again", e.Message);
        Assert.Empty(r.Conns.List());
    }

    [Fact]
    public async Task ExtraFieldsAndTheNameAreCheckedBeforeTheBrowserOpens()
    {
        using var r = Rig.Make();
        await Assert.ThrowsAsync<ArgumentException>(() => r.SignIn(Rig.Client(scopes: ["read"]), extra: [new("pat", "Token", true, null)]));
        await Assert.ThrowsAsync<ArgumentException>(() => r.SignIn(Rig.Client(scopes: ["read"]), extra: [new("client_id", "Client", false, "other")]));
        await Assert.ThrowsAsync<ArgumentException>(() => r.Flow.SignInAsync("  ", Rig.Client(scopes: ["read"]), null, [], Ct));
        Assert.Empty(r.Browser.Opened);
        Assert.Null(r.Receiver);
        Assert.Empty(r.Conns.List());
    }

    [Fact]
    public async Task RefreshAddsGrantedScopesToAnOlderConnection()
    {
        using var r = Rig.Make();
        // a sign-in saved with no granted_scopes, no authorize_endpoint, expired
        var id = r.Conns.Save(null, "old sign-in", [
            new("access_token", "Access token", true, null), new("refresh_token", "Refresh token", true, null),
            new("client_id", "Client id", false, "my-app-id"), new("token_endpoint", "Token address", false, TokenUrl),
            new("expires_at", "Expires at", false, r.Clock.Now.AddMinutes(-5).ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
        ], new Dictionary<string, string> { ["access_token"] = At1, ["refresh_token"] = Rt1 }, kind: "oauth").Id;
        Assert.Null(OAuthFlow.GrantedOf(r.Conns.Get(id)!)); // unknown
        r.RefreshScope = "read write";
        Assert.Equal(At2, await r.Flow.AccessTokenAsync(id, Ct));
        Assert.True(Granted(r, id).SetEquals(["read", "write"]));
        Assert.Single(r.Conns.Get(id)!.Fields, f => f.Key == "granted_scopes");
    }

    [Fact]
    public async Task RefreshUpdatesGrantedScopes()
    {
        using var r = Rig.Make();
        r.SignInScope = "read write";
        var id = await r.SignIn(Rig.Client(scopes: ["read", "write"]));

        r.RefreshScope = null; // a refresh that says nothing keeps what was granted
        r.Clock.Now = r.Clock.Now.AddHours(2);
        Assert.Equal(At2, await r.Flow.AccessTokenAsync(id, Ct));
        Assert.False(r.TokenForms[^1].ContainsKey("scope")); // scope is never re-sent
        Assert.True(Granted(r, id).SetEquals(["read", "write"]));

        r.RefreshScope = "read";
        Assert.Equal(At2, await r.Flow.RefreshAfterRefusalAsync(id, At2, Ct));
        Assert.False(r.TokenForms[^1].ContainsKey("scope"));
        Assert.True(Granted(r, id).SetEquals(["read"]));
        var f = r.Conns.Get(id)!.Fields;
        Assert.Single(f, x => x.Key == "granted_scopes");
        Assert.Equal(Authorize, f.Single(x => x.Key == "authorize_endpoint").Shown); // the rest kept
    }

    // ------------------------------------------------------------------ discovery

    private static string Meta(string issuer, string? token = null, string? authorize = null, JsonArray? methods = null, JsonArray? scopes = null, bool iss = false)
    {
        var o = new JsonObject
        {
            ["issuer"] = issuer, ["authorization_endpoint"] = authorize ?? issuer + "/authorize", ["token_endpoint"] = token ?? issuer + "/token",
            ["authorization_response_iss_parameter_supported"] = iss,
        };
        if (methods is not null) o["code_challenge_methods_supported"] = methods;
        if (scopes is not null) o["scopes_supported"] = scopes;
        return o.ToJsonString();
    }

    [Fact]
    public async Task DiscoveryFindsOAuthMetadataWithPathInsertion()
    {
        var http = new FakeHttp();
        const string provider = Idp + "/tenant1";
        http.Add(Idp + "/.well-known/oauth-authorization-server/tenant1",
            Meta(provider, methods: ["plain", "S256"], scopes: ["openid", "repo", "read:user"], iss: true));
        var p = await OAuthDiscovery.DiscoverAsync(http, new Uri(provider + "/"), Ct); // a trailing slash is the same address
        Assert.NotNull(p);
        Assert.Equal([Idp + "/.well-known/oauth-authorization-server/tenant1"], http.Urls);
        Assert.Equal(provider, p.Issuer.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(provider + "/authorize", p.AuthorizeEndpoint.AbsoluteUri);
        Assert.Equal(provider + "/token", p.TokenEndpoint.AbsoluteUri);
        Assert.Equal(["openid", "repo", "read:user"], p.Scopes);
        Assert.True(p.Pkce);
        Assert.True(p.IssRequired);
    }

    [Fact]
    public async Task DiscoveryFallsBackToOpenIdConfiguration()
    {
        var http = new FakeHttp();
        const string provider = Idp + "/tenant1";
        var tooLong = new string('x', 201);
        var many = new JsonArray(Enumerable.Range(0, 300).Select(i => (JsonNode?)JsonValue.Create($"s{i}")).ToArray());
        many.Insert(0, "has space");
        many.Insert(0, tooLong);
        http.Add(provider + "/.well-known/openid-configuration", Meta(provider, methods: ["plain"], scopes: many));
        var p = await OAuthDiscovery.DiscoverAsync(http, new Uri(provider), Ct);
        Assert.NotNull(p);
        Assert.Equal([
            Idp + "/.well-known/oauth-authorization-server/tenant1", Idp + "/.well-known/openid-configuration/tenant1", provider + "/.well-known/openid-configuration",
        ], http.Urls);
        Assert.False(p.Pkce); // no S256 offered
        Assert.False(p.IssRequired);
        Assert.Equal(200, p.Scopes.Count);
        Assert.Equal("s0", p.Scopes[0]);
        Assert.DoesNotContain(p.Scopes, s => s.Contains(' ') || s.Length > 200);

        // no path: the two root documents, OAuth first
        var root = new FakeHttp();
        root.Add(Idp + "/.well-known/openid-configuration", Meta(Idp, methods: ["S256"]));
        var q = await OAuthDiscovery.DiscoverAsync(root, new Uri(Idp), Ct);
        Assert.NotNull(q);
        Assert.Equal([Idp + "/.well-known/oauth-authorization-server", Idp + "/.well-known/openid-configuration"], root.Urls);
        Assert.True(q.Pkce);
        Assert.Empty(q.Scopes);
    }

    [Fact]
    public async Task NoMetadataReturnsNull()
    {
        var http = new FakeHttp(); // everything 404
        Assert.Null(await OAuthDiscovery.DiscoverAsync(http, new Uri("https://github.example.test"), Ct));
        Assert.Equal(2, http.Urls.Count());
        http.Add("https://github.example.test/.well-known/oauth-authorization-server", "gone", "text/plain", HttpStatusCode.Gone);
        Assert.Null(await OAuthDiscovery.DiscoverAsync(http, new Uri("https://github.example.test"), Ct));
        // a server error is not "none": it says so
        http.Add("https://github.example.test/.well-known/oauth-authorization-server", "down", "text/plain", HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<McpException>(() => OAuthDiscovery.DiscoverAsync(http, new Uri("https://github.example.test"), Ct));
    }

    [Fact]
    public async Task IssuerMismatchIsRefused()
    {
        var http = new FakeHttp();
        http.Add(Idp + "/.well-known/oauth-authorization-server", Meta("https://evil.example.test"));
        var e = await Assert.ThrowsAsync<McpException>(() => OAuthDiscovery.DiscoverAsync(http, new Uri(Idp), Ct));
        Assert.Contains("issuer", e.Message);
        // a document with no issuer at all is refused the same way
        http.Add(Idp + "/.well-known/oauth-authorization-server", """{"authorization_endpoint":"https://idp.example.test/a","token_endpoint":"https://idp.example.test/t"}""");
        e = await Assert.ThrowsAsync<McpException>(() => OAuthDiscovery.DiscoverAsync(http, new Uri(Idp), Ct));
        Assert.Contains("issuer", e.Message);
    }

    [Fact]
    public async Task HttpEndpointIsRefused()
    {
        var http = new FakeHttp();
        http.Add(Idp + "/.well-known/oauth-authorization-server", Meta(Idp, token: "http://idp.example.test/token"));
        var e = await Assert.ThrowsAsync<McpException>(() => OAuthDiscovery.DiscoverAsync(http, new Uri(Idp), Ct));
        Assert.Contains("https://", e.Message);
        http.Add(Idp + "/.well-known/oauth-authorization-server", Meta(Idp, authorize: "http://idp.example.test/authorize"));
        e = await Assert.ThrowsAsync<McpException>(() => OAuthDiscovery.DiscoverAsync(http, new Uri(Idp), Ct));
        Assert.Contains("https://", e.Message);

        // the provider address itself: nothing is fetched over plain http
        var plain = new FakeHttp();
        e = await Assert.ThrowsAsync<McpException>(() => OAuthDiscovery.DiscoverAsync(plain, new Uri("http://idp.example.test"), Ct));
        Assert.Contains("https://", e.Message);
        Assert.Empty(plain.Urls);

        // plain http to this computer is fine
        var local = new FakeHttp();
        const string loop = "http://127.0.0.1:8080";
        local.Add(loop + "/.well-known/oauth-authorization-server", Meta(loop));
        var p = await OAuthDiscovery.DiscoverAsync(local, new Uri(loop), Ct);
        Assert.Equal(loop + "/token", p!.TokenEndpoint.AbsoluteUri);

        // and the flow refuses an http token address before opening the browser
        using var r = Rig.Make();
        var bad = Rig.Client(scopes: ["read"]) with { TokenEndpoint = new Uri("http://idp.example.test/token") };
        e = await Assert.ThrowsAsync<McpException>(() => r.SignIn(bad));
        Assert.Contains("https://", e.Message);
        Assert.Empty(r.Browser.Opened);
        Assert.Empty(r.Conns.List());
    }
}
