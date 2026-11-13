using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// "The server signs me in" (MCP OAuth). Every endpoint is a FakeHttp route; the browser and the redirect are fakes.
/// No real browser, no port, no network, no OS store.
/// </summary>
public class McpOAuthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string McpUrl = "https://mcp.example.test/mcp";
    private const string Prm = "https://mcp.example.test/.well-known/oauth-protected-resource/mcp";
    private const string PrmBare = "https://mcp.example.test/.well-known/oauth-protected-resource";
    private const string As = "https://auth.example.test";
    private const string AsMeta = "https://auth.example.test/.well-known/oauth-authorization-server";
    private const string OidcMeta = "https://auth.example.test/.well-known/openid-configuration";
    private const string Register = "https://auth.example.test/register";
    private const string TokenUrl = "https://auth.example.test/token";
    private const string Authorize = "https://auth.example.test/authorize";
    private const string At1 = "at_ONE_supersecret_0123456789";
    private const string At2 = "at_TWO_supersecret_0123456789";
    private const string Rt1 = "rt_ONE_supersecret_0123456789";
    private const string Rt2 = "rt_TWO_supersecret_0123456789";
    private const string Code = "code_supersecret_abcdef";

    internal sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class FakeBrowser : IBrowser
    {
        public List<Uri> Opened { get; } = [];
        public Exception? Fail;
        public void Open(Uri url)
        {
            if (Fail is not null) throw Fail;
            lock (Opened) Opened.Add(url);
        }
    }

    internal enum Answer { Code, WrongState, Denied, Never }

    /// <summary>Answers as the browser would come back, with the state the caller expects (it must also be the one in the URL the browser was sent to).</summary>
    internal sealed class FakeReceiver(FakeBrowser browser, Answer answer, string? iss = null) : IRedirectReceiver
    {
        public Uri RedirectUri { get; } = new("http://127.0.0.1:50123/callback");
        public bool Disposed;

        public async Task<IReadOnlyDictionary<string, string>> WaitAsync(string expectedState, CancellationToken ct)
        {
            if (answer == Answer.Never) await Task.Delay(Timeout.Infinite, ct);
            Assert.Equal(expectedState, Query(browser.Opened[^1])["state"]);
            var a = answer switch
            {
                // a receiver that hands back another state anyway: the caller still refuses it
                Answer.WrongState => new Dictionary<string, string> { ["code"] = Code, ["state"] = "not-the-state" },
                Answer.Denied => new Dictionary<string, string> { ["error"] = "access_denied", ["state"] = expectedState },
                _ => new Dictionary<string, string> { ["code"] = Code, ["state"] = expectedState },
            };
            if (iss is not null) a["iss"] = iss;
            return a;
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    internal static Dictionary<string, string> Query(Uri u) =>
        u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : ""));

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal sealed class Rig : IDisposable
    {
        public TempDb Db = new();
        public MemoryCredentialStore Store = new();
        public ConnectionService Conns = null!;
        public FakeHttp Http = new();
        public FakeBrowser Browser = new();
        public ManualClock Clock = new();
        public Answer Answer = Answer.Code;
        public FakeReceiver? Receiver;
        public List<Dictionary<string, string>> TokenForms = [];
        public List<JsonObject> Registrations = [];
        public int Refreshes;
        public (HttpStatusCode Status, string Body)? RefreshAnswer;
        public bool RefuseRefresh { set => RefreshAnswer = value ? (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""") : null; }
        public bool IssSupported;
        public string? Iss;
        public string? ClientSecret;
        public string TokenEndpoint = TokenUrl;
        public McpOAuth OAuth = null!;

        public static Rig Make(bool asMeta = true, bool oidc = false, bool prm = true)
        {
            var r = new Rig();
            r.Conns = new ConnectionService(r.Store, r.Db.ConnectionString);
            if (prm) r.Http.Add(Prm, $$"""{"resource":"{{McpUrl}}","authorization_servers":["{{As}}"],"scopes_supported":["mcp.read","mcp.write"]}""");
            if (asMeta) r.Http.Add(AsMeta, _ => Json(r.AsMetadata()));
            if (oidc) r.Http.Add(OidcMeta, _ => Json(r.AsMetadata()));
            r.Http.Add(Register, req =>
            {
                r.Registrations.Add((JsonObject)JsonNode.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!);
                var body = new JsonObject { ["client_id"] = "client-123" };
                if (r.ClientSecret is not null) body["client_secret"] = r.ClientSecret;
                return Json(body.ToJsonString(), HttpStatusCode.Created);
            });
            r.Http.Add(TokenUrl, req =>
            {
                var form = Form(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                lock (r.TokenForms) r.TokenForms.Add(form);
                if (form["grant_type"] == "refresh_token")
                {
                    Interlocked.Increment(ref r.Refreshes);
                    Thread.Sleep(150); // concurrent callers arrive while this one is in flight
                    if (r.RefreshAnswer is { } ra) return Json(ra.Body, ra.Status);
                    return Json($$"""{"access_token":"{{At2}}","refresh_token":"{{Rt2}}","expires_in":3600,"token_type":"Bearer"}""");
                }
                return Json($$"""{"access_token":"{{At1}}","refresh_token":"{{Rt1}}","expires_in":3600,"token_type":"bearer"}""");
            });
            r.OAuth = new McpOAuth(r.Http, r.Conns, r.Browser, () => r.Receiver = new FakeReceiver(r.Browser, r.Answer, r.Iss), r.Clock);
            return r;
        }

        public string AsMetadata() => new JsonObject
        {
            ["issuer"] = As, ["authorization_endpoint"] = Authorize, ["token_endpoint"] = TokenEndpoint, ["registration_endpoint"] = Register,
            ["code_challenge_methods_supported"] = new JsonArray("S256"), ["response_types_supported"] = new JsonArray("code"),
            ["authorization_response_iss_parameter_supported"] = IssSupported,
        }.ToJsonString();

        public Task<long> SignIn(string? www = null) => OAuth.SignInAsync("remote", new Uri(McpUrl), www, null, Ct);

        public void Dispose() => Db.Dispose();
    }

    internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal static Dictionary<string, string> Form(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : ""));

    [Fact]
    public async Task SignInRegistersUsesPkceAndSavesTokens()
    {
        using var r = Rig.Make();
        var id = await r.SignIn();

        var reg = Assert.Single(r.Registrations);
        Assert.Equal("Yawble", reg["client_name"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:50123/callback", reg["redirect_uris"]![0]!.GetValue<string>());
        Assert.Equal(["authorization_code", "refresh_token"], reg["grant_types"]!.AsArray().Select(g => g!.GetValue<string>()));
        Assert.Equal("none", reg["token_endpoint_auth_method"]!.GetValue<string>());

        var url = Assert.Single(r.Browser.Opened);
        Assert.StartsWith(Authorize + "?", url.AbsoluteUri);
        var q = Query(url);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("client-123", q["client_id"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal(McpUrl, q["resource"]);
        Assert.Equal("http://127.0.0.1:50123/callback", q["redirect_uri"]);
        Assert.Equal("mcp.read mcp.write", q["scope"]);
        Assert.Equal(43, q["state"].Length); // 32 random bytes, base64url

        var form = Assert.Single(r.TokenForms);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(Code, form["code"]);
        Assert.Equal(McpUrl, form["resource"]);
        Assert.Equal("http://127.0.0.1:50123/callback", form["redirect_uri"]);
        Assert.Equal(q["code_challenge"], B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))));
        Assert.True(r.Receiver!.Disposed);

        var view = Assert.Single(r.Conns.List());
        Assert.Equal(id, view.Id);
        Assert.Equal("oauth", view.Kind);
        Assert.Equal("remote sign-in", view.Name);
        var f = view.Fields.ToDictionary(x => x.Key);
        Assert.True(f["access_token"].Secret);
        Assert.True(f["refresh_token"].Secret);
        Assert.Equal("client-123", f["client_id"].Shown);
        Assert.Equal(TokenUrl, f["token_endpoint"].Shown);
        Assert.Equal(McpUrl, f["resource"].Shown);
        Assert.Equal(r.Clock.Now.AddSeconds(3600), DateTimeOffset.Parse(f["expires_at"].Shown!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(f.ContainsKey("client_secret"));
        var shown = System.Text.Json.JsonSerializer.Serialize(r.Conns.List());
        Assert.DoesNotContain(At1, shown);
        Assert.DoesNotContain(Rt1, shown);
        Assert.Equal(At1, r.Conns.Secrets(id)["access_token"]);
        Assert.DoesNotContain(At1, SecretScrubber.Scrub("token " + At1));
        Assert.Equal(At1, await r.OAuth.AccessTokenAsync(id, Ct));
    }

    [Fact]
    public async Task AClientSecretFromRegistrationIsKeptAsASecret()
    {
        using var r = Rig.Make();
        r.ClientSecret = "cs_supersecret_0123456789";
        var id = await r.SignIn();
        var f = Assert.Single(r.Conns.List()).Fields.Single(x => x.Key == "client_secret");
        Assert.True(f.Secret);
        Assert.Equal("cs_supersecret_0123456789", r.Conns.Secrets(id)["client_secret"]);
    }

    [Fact]
    public async Task ResourceMetadataFromWwwAuthenticateWins()
    {
        using var r = Rig.Make();
        const string custom = "https://mcp.example.test/custom/prm";
        const string other = "https://other-auth.example.test";
        r.Http.Add(custom, $$"""{"resource":"{{McpUrl}}","authorization_servers":["{{other}}"]}""");
        r.Http.Add(other + "/.well-known/oauth-authorization-server", _ => Json(new JsonObject
        {
            ["issuer"] = other, ["authorization_endpoint"] = other + "/authorize", ["token_endpoint"] = TokenUrl, ["registration_endpoint"] = Register,
        }.ToJsonString()));
        await r.SignIn($"Bearer realm=\"x\", resource_metadata=\"{custom}\"");
        Assert.DoesNotContain(Prm, r.Http.Urls);
        Assert.StartsWith(other + "/authorize?", r.Browser.Opened[0].AbsoluteUri);
        Assert.False(Query(r.Browser.Opened[0]).ContainsKey("scope")); // no scopes_supported: none asked
    }

    [Fact]
    public async Task BarePrmIsTriedAfterThePathOne()
    {
        using var r = Rig.Make();
        r.Http.Add(Prm, "nope", "text/plain", HttpStatusCode.NotFound);
        r.Http.Add(PrmBare, $$"""{"resource":"https://mcp.example.test","authorization_servers":["{{As}}"]}""");
        await r.SignIn();
        Assert.Equal([Prm, PrmBare], r.Http.Urls.Take(2));
    }

    [Fact]
    public async Task OpenIdConfigurationIsTheFallback()
    {
        using var r = Rig.Make(asMeta: false, oidc: true);
        await r.SignIn();
        Assert.Contains(AsMeta, r.Http.Urls);
        Assert.Contains(OidcMeta, r.Http.Urls);
        Assert.Single(r.Conns.List());
    }

    [Fact]
    public async Task AWrongIssuerIsRefused()
    {
        using var r = Rig.Make(asMeta: false);
        r.Http.Add(AsMeta, _ => Json(r.AsMetadata().Replace(As, "https://evil.example.test")));
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn());
        Assert.Contains("issuer", e.Message);
        Assert.Empty(r.Browser.Opened);
    }

    [Fact]
    public async Task WrongStateIsRefused()
    {
        using var r = Rig.Make();
        r.Answer = Answer.WrongState;
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn());
        Assert.Equal("the sign-in answer did not match the one Yawble started; sign in again", e.Message);
        Assert.Empty(r.TokenForms);
        Assert.Empty(r.Conns.List());
    }

    [Fact]
    public async Task DeniedSaysSo()
    {
        using var r = Rig.Make();
        r.Answer = Answer.Denied;
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn());
        Assert.Equal("the sign-in was cancelled", e.Message);
        Assert.Empty(r.TokenForms);
    }

    [Fact]
    public async Task NoRedirectTimesOut()
    {
        using var r = Rig.Make();
        r.Answer = Answer.Never;
        r.OAuth.SignInTimeout = TimeSpan.FromMilliseconds(200);
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn());
        Assert.Equal("the sign-in did not finish in time; sign in again", e.Message);
        Assert.True(r.Receiver!.Disposed);
        Assert.Empty(r.Conns.List());
    }

    [Fact]
    public async Task HttpTokenEndpointIsRefused()
    {
        using var r = Rig.Make();
        r.TokenEndpoint = "http://auth.example.test/token";
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn());
        Assert.Contains("https://", e.Message);
        Assert.Empty(r.Browser.Opened);
        Assert.Empty(r.Registrations);
    }

    [Fact]
    public async Task ARedirectIsNotFollowed()
    {
        using var r = Rig.Make();
        r.Http.Add(Prm, _ =>
        {
            var m = new HttpResponseMessage(HttpStatusCode.Found);
            m.Headers.Location = new Uri("https://evil.example.test/prm");
            return m;
        });
        await Assert.ThrowsAsync<McpException>(() => r.SignIn());
        Assert.DoesNotContain("https://evil.example.test/prm", r.Http.Urls);
    }

    [Fact]
    public async Task AHugeMetadataReplyIsRefused()
    {
        using var r = Rig.Make();
        r.Http.AddGenerated(Prm, 5L * 1024 * 1024);
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn());
        Assert.Contains("1 MB", e.Message);
        Assert.True(r.Http.BytesServed <= 1024 * 1024 + 64 * 1024);
    }

    [Fact]
    public async Task ExpiringTokenIsRefreshed()
    {
        using var r = Rig.Make();
        var id = await r.SignIn();
        r.Clock.Now = r.Clock.Now.AddSeconds(3000);
        Assert.Equal(At1, await r.OAuth.AccessTokenAsync(id, Ct)); // ten minutes left: no refresh
        Assert.Equal(0, r.Refreshes);
        r.Clock.Now = r.Clock.Now.AddSeconds(560); // 40 s left
        Assert.Equal(At2, await r.OAuth.AccessTokenAsync(id, Ct));
        var form = r.TokenForms[^1];
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal(Rt1, form["refresh_token"]);
        Assert.Equal("client-123", form["client_id"]);
        Assert.Equal(McpUrl, form["resource"]);
        var secrets = r.Conns.Secrets(id);
        Assert.Equal(At2, secrets["access_token"]);
        Assert.Equal(Rt2, secrets["refresh_token"]);
        Assert.Equal(At2, await r.OAuth.AccessTokenAsync(id, Ct));
        Assert.Equal(1, r.Refreshes);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneRefresh()
    {
        using var r = Rig.Make();
        var id = await r.SignIn();
        r.Clock.Now = r.Clock.Now.AddHours(2);
        var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => r.OAuth.AccessTokenAsync(id, Ct), Ct)));
        Assert.All(all, t => Assert.Equal(At2, t));
        Assert.Equal(1, r.Refreshes);
    }

    [Fact]
    public async Task RefusedRefreshMarksNeedsReconnect()
    {
        using var r = Rig.Make();
        var id = await r.SignIn();
        r.RefuseRefresh = true;
        r.Clock.Now = r.Clock.Now.AddHours(2);
        await Assert.ThrowsAnyAsync<McpUnauthorizedException>(() => r.OAuth.AccessTokenAsync(id, Ct));
        var b = r.Conns.Brief(id)!;
        Assert.Equal("needs_reconnect", b.Status);
        Assert.Equal("the sign-in expired; sign in again", b.StatusReason);
    }

    [Fact]
    public async Task PrmWithoutResourceIsRefused()
    {
        using var r = Rig.Make();
        r.Http.Add(Prm, $$"""{"authorization_servers":["{{As}}"]}""");
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn());
        Assert.Contains("not about this address", e.Message);
        Assert.Empty(r.Browser.Opened);
    }

    [Fact]
    public async Task PathPrmAboutTheOriginIsRefused()
    {
        using var r = Rig.Make();
        r.Http.Add(Prm, $$"""{"resource":"https://mcp.example.test","authorization_servers":["{{As}}"]}""");
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn());
        Assert.Contains("not about this address", e.Message);
    }

    [Fact]
    public async Task OriginIsTheAuthorizationServerWhenNoResourceMetadata()
    {
        using var r = Rig.Make(asMeta: false, prm: false);
        const string origin = "https://mcp.example.test";
        r.Http.Add(origin + "/.well-known/oauth-authorization-server", _ => Json(new JsonObject
        {
            ["issuer"] = origin, ["authorization_endpoint"] = origin + "/authorize", ["token_endpoint"] = TokenUrl, ["registration_endpoint"] = Register,
        }.ToJsonString()));
        await r.SignIn();
        Assert.Equal([Prm, PrmBare, origin + "/.well-known/oauth-authorization-server"], r.Http.Urls.Take(3));
        Assert.StartsWith(origin + "/authorize?", r.Browser.Opened[0].AbsoluteUri);
        Assert.Single(r.Conns.List());
    }

    [Fact]
    public async Task NoMetadataAnywhereSaysSo()
    {
        using var r = Rig.Make(asMeta: false, prm: false);
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn());
        Assert.Equal("the server did not say where to sign in (no sign-in information at mcp.example.test)", e.Message);
        Assert.DoesNotContain(r.Http.Urls, u => u.EndsWith("/authorize", StringComparison.Ordinal) || u.EndsWith("/token", StringComparison.Ordinal) || u.EndsWith("/register", StringComparison.Ordinal));
        Assert.Empty(r.Browser.Opened);
    }

    [Fact]
    public async Task ANamedPrmThatIsMissingIsNotGuessedAround()
    {
        using var r = Rig.Make();
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn("Bearer resource_metadata=\"https://mcp.example.test/gone\""));
        Assert.Contains("could not be found", e.Message);
        Assert.DoesNotContain(Prm, r.Http.Urls);
    }

    [Fact]
    public async Task AMissingIssIsRefusedWhenTheServerPromisesIt()
    {
        using var r = Rig.Make();
        r.IssSupported = true;
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn());
        Assert.Contains("another server", e.Message);
        Assert.Empty(r.TokenForms);
    }

    [Fact]
    public async Task AMatchingIssIsAccepted()
    {
        using var r = Rig.Make();
        r.IssSupported = true;
        r.Iss = As;
        await r.SignIn();
        Assert.Single(r.Conns.List());
    }

    [Fact]
    public async Task ABrowserThatCannotOpenSaysSo()
    {
        using var r = Rig.Make();
        r.Browser.Fail = new InvalidOperationException("no shell");
        var e = await Assert.ThrowsAsync<McpException>(() => r.SignIn());
        Assert.StartsWith("could not open your browser", e.Message);
        Assert.Contains("auth.example.test", e.Message);
        Assert.True(r.Receiver!.Disposed);
    }

    [Theory]
    [InlineData(400, """{"error":"invalid_grant"}""")]
    [InlineData(400, """{"error":"invalid_client"}""")]
    [InlineData(400, """{"error":"unauthorized_client"}""")]
    [InlineData(401, """{"error":"invalid_client"}""")]
    public async Task ARefreshTheServerRejectsMarksNeedsReconnect(int status, string body)
    {
        using var r = Rig.Make();
        var id = await r.SignIn();
        r.RefreshAnswer = ((HttpStatusCode)status, body);
        r.Clock.Now = r.Clock.Now.AddHours(2);
        await Assert.ThrowsAnyAsync<McpUnauthorizedException>(() => r.OAuth.AccessTokenAsync(id, Ct));
        Assert.Equal("needs_reconnect", r.Conns.Brief(id)!.Status);
    }

    [Theory]
    [InlineData(429, "{}")]
    [InlineData(408, "{}")]
    [InlineData(503, "down")]
    [InlineData(400, """{"error":"invalid_request"}""")]
    public async Task ARefreshThatFailsForNowDoesNotMarkNeedsReconnect(int status, string body)
    {
        using var r = Rig.Make();
        var id = await r.SignIn();
        var before = r.Conns.Brief(id)!.Status;
        r.RefreshAnswer = ((HttpStatusCode)status, body);
        r.Clock.Now = r.Clock.Now.AddHours(2);
        var e = await Assert.ThrowsAsync<McpException>(() => r.OAuth.AccessTokenAsync(id, Ct));
        Assert.Equal("could not refresh the sign-in right now; try again", e.Message);
        Assert.Equal(before, r.Conns.Brief(id)!.Status);
    }

    [Fact]
    public async Task ARefreshThatCannotReachTheServerDoesNotMarkNeedsReconnect()
    {
        using var r = Rig.Make();
        var id = await r.SignIn();
        r.Http.AddDropped(TokenUrl);
        r.Clock.Now = r.Clock.Now.AddHours(2);
        var e = await Assert.ThrowsAsync<McpException>(() => r.OAuth.AccessTokenAsync(id, Ct));
        Assert.Equal("could not refresh the sign-in right now; try again", e.Message);
        Assert.NotEqual("needs_reconnect", r.Conns.Brief(id)!.Status);
    }

    [Fact]
    public void TheLoopbackReceiverTakesOnlyTheExpectedState()
    {
        Assert.Equal(200, LoopbackRedirectReceiver.Accept("/callback?code=c1&state=abc", "abc", out var q));
        Assert.Equal("c1", q!["code"]);
        Assert.Equal(200, LoopbackRedirectReceiver.Accept("/callback?error=access_denied&state=abc", "abc", out q));
        Assert.Equal("access_denied", q!["error"]);
        Assert.Equal(400, LoopbackRedirectReceiver.Accept("/callback?code=c1&state=abd", "abc", out q));
        Assert.Null(q);
        Assert.Equal(400, LoopbackRedirectReceiver.Accept("/callback?code=c1", "abc", out _));
        Assert.Equal(404, LoopbackRedirectReceiver.Accept("/favicon.ico", "abc", out _));
        Assert.Equal(404, LoopbackRedirectReceiver.Accept("/callbackx?state=abc", "abc", out _));
        Assert.Equal(404, LoopbackRedirectReceiver.Accept(null, "abc", out _));
    }

    // ------------------------------------------------------------------ through the service

    private sealed class ServiceRig : IAsyncDisposable
    {
        public Rig R = null!;
        public BusEngine Engine = null!;
        public IntegrationService Svc = null!;
        public string Accept = At1; // the access token the MCP server takes
        public List<string> Auth = [];

        public static ServiceRig Make()
        {
            var s = new ServiceRig { R = Rig.Make() };
            s.Engine = new BusEngine(s.R.Db.OpenStore(), new ScriptedRunner(), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            s.Svc = new IntegrationService(s.R.Db.ConnectionString, s.Engine, clock: s.R.Clock, connections: s.R.Conns, mcpHandler: () => s.R.Http,
                browser: s.R.Browser, redirect: () => s.R.Receiver = new FakeReceiver(s.R.Browser, s.R.Answer, s.R.Iss));
            s.R.Http.Add(McpUrl, s.Serve);
            return s;
        }

        private HttpResponseMessage Serve(HttpRequestMessage req)
        {
            if (req.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);
            var auth = req.Headers.TryGetValues("Authorization", out var a) ? a.First() : "";
            lock (Auth) Auth.Add(auth);
            if (auth != "Bearer " + Accept)
            {
                var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("sign in") };
                denied.Headers.TryAddWithoutValidation("WWW-Authenticate", $"Bearer resource_metadata=\"{Prm}\"");
                return denied;
            }
            var b = (JsonObject)JsonNode.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            string Reply(string result) => $"{{\"jsonrpc\":\"2.0\",\"id\":{b["id"]},\"result\":{result}}}";
            return b["method"]!.GetValue<string>() switch
            {
                "initialize" => Json(Reply("{\"serverInfo\":{\"name\":\"fake\"}}")),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                "tools/list" => Json(Reply("{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\"}}]}")),
                _ => Json(Reply("{}")),
            };
        }

        public Task<JsonNode?> Rpc(string method, JsonObject? p = null) => Svc.DispatchAsync(method, p, Ct);

        public async ValueTask DisposeAsync() { await Svc.DisposeAsync(); await Engine.DisposeAsync(); R.Dispose(); }
    }

    [Fact]
    public async Task ADiscoverRefusedWithNoSignInOffersTheServerSignsMeIn()
    {
        await using var s = ServiceRig.Make();
        await s.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "remote", ["transport"] = "http", ["url"] = McpUrl, ["auth"] = new JsonObject { ["type"] = "none" } });
        await s.Rpc("integrations.discover", new JsonObject { ["name"] = "remote", ["kind"] = "mcp" });
        JsonNode? status = null;
        await Wait.UntilAsync(async () => (status = await s.Rpc("integrations.discoverStatus", new JsonObject { ["name"] = "remote", ["kind"] = "mcp" }))!["state"]!.GetValue<string>() is not ("running" or "queued"), "discover");
        Assert.True(status!["needsSignIn"]!.GetValue<bool>());
        Assert.Contains("The server signs me in", status["error"]!.GetValue<string>());
        Assert.Empty(s.R.Conns.List()); // nothing marked: there is no sign-in yet
    }

    [Fact]
    public async Task SignInRpcSetsOauthAndTheConnection_AndCallsSendTheToken()
    {
        await using var s = ServiceRig.Make();
        await s.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "remote", ["transport"] = "http", ["url"] = McpUrl, ["auth"] = new JsonObject { ["type"] = "none" } });
        await Assert.ThrowsAnyAsync<Exception>(() => s.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "remote" })); // the 401 carries the metadata address
        var res = await s.Rpc("integrations.mcpSignIn", new JsonObject { ["name"] = "remote" });
        Assert.True(res!["ok"]!.GetValue<bool>());
        Assert.Equal("remote sign-in", res["connection"]!["name"]!.GetValue<string>());
        Assert.Equal("works", res["connection"]!["status"]!.GetValue<string>());
        Assert.DoesNotContain(At1, res.ToJsonString());
        var list = (JsonArray)(await s.Rpc("integrations.mcpList"))!;
        Assert.Equal("oauth", list[0]!["auth"]!["type"]!.GetValue<string>());
        Assert.Equal("remote sign-in", list[0]!["connection"]!["name"]!.GetValue<string>());
        await s.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "remote" });
        Assert.Equal("Bearer " + At1, s.Auth[^1]);
        Assert.DoesNotContain(At1, list.ToJsonString());
    }

    [Fact]
    public async Task ARefusedCallRefreshesOnceAndRetries()
    {
        await using var s = ServiceRig.Make();
        await s.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "remote", ["transport"] = "http", ["url"] = McpUrl, ["auth"] = new JsonObject { ["type"] = "none" } });
        await s.Rpc("integrations.mcpSignIn", new JsonObject { ["name"] = "remote" });
        s.Accept = At2; // the server revoked the first token early
        await s.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "remote" });
        Assert.Equal(1, s.R.Refreshes);
        Assert.Equal("Bearer " + At2, s.Auth[^1]);
        Assert.Equal("works", Assert.Single(s.R.Conns.List()).Status);
    }

    [Fact]
    public async Task ARefusedCallAfterARefreshNeedsReconnect()
    {
        await using var s = ServiceRig.Make();
        await s.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "remote", ["transport"] = "http", ["url"] = McpUrl, ["auth"] = new JsonObject { ["type"] = "none" } });
        await s.Rpc("integrations.mcpSignIn", new JsonObject { ["name"] = "remote" });
        s.Accept = "nothing";
        var e = await Assert.ThrowsAnyAsync<Exception>(() => s.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "remote" }));
        Assert.Contains("sign in again", e.Message);
        Assert.Equal(1, s.R.Refreshes);
        Assert.Equal("needs_reconnect", Assert.Single(s.R.Conns.List()).Status);
    }

    [Fact]
    public async Task AnEditedAddressGetsNoToken()
    {
        await using var s = ServiceRig.Make();
        await s.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "remote", ["transport"] = "http", ["url"] = McpUrl, ["auth"] = new JsonObject { ["type"] = "none" } });
        await s.Rpc("integrations.mcpSignIn", new JsonObject { ["name"] = "remote" });
        const string moved = "https://elsewhere.example.test/mcp";
        await s.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "remote", ["transport"] = "http", ["url"] = moved });
        var e = await Assert.ThrowsAnyAsync<Exception>(() => s.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "remote" }));
        Assert.Contains("remote needs you to sign in again", e.Message);
        Assert.DoesNotContain(moved, s.R.Http.Urls); // nothing was sent there at all
        Assert.Equal(0, s.R.Refreshes);
        Assert.NotEqual("needs_reconnect", Assert.Single(s.R.Conns.List()).Status);
        // a trailing slash is the same address
        await s.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "remote", ["transport"] = "http", ["url"] = McpUrl + "/" });
        Assert.True(McpOAuth.SameResource(McpUrl, McpUrl + "/"));
    }

    [Fact]
    public async Task SwitchingToATokenDropsTheSignInConnection()
    {
        await using var s = ServiceRig.Make();
        await s.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "remote", ["transport"] = "http", ["url"] = McpUrl, ["auth"] = new JsonObject { ["type"] = "none" } });
        await s.Rpc("integrations.mcpSignIn", new JsonObject { ["name"] = "remote" });
        await s.Rpc("integrations.mcpSave", new JsonObject
        {
            ["name"] = "remote", ["transport"] = "http", ["url"] = McpUrl, ["auth"] = new JsonObject { ["type"] = "bearer" },
            ["secrets"] = new JsonObject { ["token"] = "tok_plain_0123456789" },
        });
        Assert.Equal("remote token", Assert.Single(s.R.Conns.List()).Name);
    }

    [Fact]
    public async Task SigningInAgainReusesTheConnection()
    {
        await using var s = ServiceRig.Make();
        await s.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "remote", ["transport"] = "http", ["url"] = McpUrl, ["auth"] = new JsonObject { ["type"] = "none" } });
        var first = await s.Rpc("integrations.mcpSignIn", new JsonObject { ["name"] = "remote" });
        var again = await s.Rpc("integrations.mcpSignIn", new JsonObject { ["name"] = "remote" });
        Assert.Equal(first!["connection"]!["id"]!.GetValue<long>(), again!["connection"]!["id"]!.GetValue<long>());
        Assert.Single(s.R.Conns.List());
    }
}
