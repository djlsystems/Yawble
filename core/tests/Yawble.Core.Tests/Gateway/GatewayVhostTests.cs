using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// A request to each vhost carrying the run-token placeholder reaches a fake upstream with
/// the real (fake) secret swapped in, streaming intact and rate-limit headers passed back. And a path outside
/// the allowlist and a CONNECT to a host outside the allowlist are refused and appear in the gateway log.
/// </summary>
public class GatewayVhostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, string, string, string?> Vhosts3 => new()
    {
        // vhost, mode, path, the credential the upstream must see, the anthropic-beta header the CLI sends
        { Vhosts.Model, "subscription", "/v1/messages?beta=true", "claude-subscription", "claude-code-20250219,oauth-2025-04-20" },
        { Vhosts.Model, "apikey", "/v1/messages?beta=true", "claude-apikey", null },
        { Vhosts.Grok, "session", "/v1/responses", "grok-session-1", null },
        { Vhosts.Xai, "apikey", "/v1/chat/completions", "xai-apikey", null },
    };

    [Theory]
    [MemberData(nameof(Vhosts3))]
    public async Task PlaceholderIsSwappedForTheSecret_StreamingAndRateLimitHeadersPassThrough(
        string vhost, string mode, string path, string expectedCredential, string? beta)
    {
        await using var f = await GatewayFixture.StartAsync();
        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        f.Credentials.Set(CredentialSlot.AnthropicApiKey, FakeSecrets.AnthropicKey);
        f.Credentials.Set(CredentialSlot.XaiApiKey, FakeSecrets.XaiKey);
        f.Credentials.ClaudeMode = mode == "apikey" ? ClaudeMode.ApiKey : ClaudeMode.Subscription;
        FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, f.Clock.Now.AddHours(1));
        await using var gateway = f.StartRun(1);
        using var http = ProxyClient.For(gateway);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{vhost}{path}")
        {
            Content = new StringContent("{\"model\":\"fake\",\"stream\":true}", Encoding.UTF8, "application/json"),
        };
        // What each CLI sends: the run token as its credential (Bearer; Claude's API-key mode uses ANTHROPIC_AUTH_TOKEN → Bearer too).
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        if (beta is not null) request.Headers.TryAddWithoutValidation("anthropic-beta", beta);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Headers.TryAddWithoutValidation("x-xai-token-auth", "xai-grok-cli");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedCredential, response.Headers.GetValues("x-fake-accepted").Single());
        Assert.Equal("allowed", response.Headers.GetValues("anthropic-ratelimit-unified-status").Single());
        Assert.Equal("0.42", response.Headers.GetValues("anthropic-ratelimit-unified-5h-utilization").Single());
        Assert.Equal("99", response.Headers.GetValues("x-ratelimit-remaining-requests").Single());
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // Streaming: the first event arrives while the upstream is still holding the response open.
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream);
        Assert.Equal("event: message_start", await reader.ReadLineAsync(Ct));
        Assert.Equal("data: {\"n\":1}", await reader.ReadLineAsync(Ct));
        f.Upstream.ReleaseStream();
        var rest = await reader.ReadToEndAsync(Ct);
        Assert.Contains("event: message_stop", rest);

        // What the upstream saw: the real (fake) secret, never the run token; the CLI's own headers verbatim.
        var seen = Assert.Single(f.Upstream.Requests);
        Assert.Equal(path, seen.PathAndQuery);
        Assert.DoesNotContain(gateway.Token.Value, string.Join("\n", seen.Headers.Values) + seen.Body);
        if (expectedCredential == "claude-apikey")
        {
            Assert.Null(seen.Authorization);
            Assert.Equal(FakeSecrets.AnthropicKey, seen.ApiKey);
        }
        else
        {
            Assert.Null(seen.ApiKey);
        }
        if (beta is not null) Assert.Equal(beta, seen.Headers["anthropic-beta"]);
        Assert.Equal("2023-06-01", seen.Headers["anthropic-version"]);
        Assert.Equal("xai-grok-cli", seen.Headers["x-xai-token-auth"]);
        Assert.False(seen.Headers.ContainsKey("Proxy-Connection"));

        // The gateway log records the forward, and never a secret.
        var events = f.Log.Read(1);
        var forward = Assert.Single(events, e => e.Kind == GatewayEventKinds.Forward);
        Assert.Equal(vhost, forward.Vhost);
        Assert.Equal(200, forward.Status);
        AssertNoSecretIn(f, gateway.Token.Value);
    }

    [Fact]
    public async Task NonStreamingGetsAndKeepAliveWork_AcrossVhosts()
    {
        await using var f = await GatewayFixture.StartAsync();
        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, f.Clock.Now.AddHours(1));
        await using var gateway = f.StartRun(2);
        using var http = ProxyClient.For(gateway);
        foreach (var (vhost, path) in new[] { (Vhosts.Model, "/v1/models?limit=1000"), (Vhosts.Grok, "/v1/user?include=coding_data_team"), (Vhosts.Grok, "/v1/models") })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{vhost}{path}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
            using var resp = await http.SendAsync(req, Ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("\"ok\":true", await resp.Content.ReadAsStringAsync(Ct));
        }
        using var head = new HttpRequestMessage(HttpMethod.Head, $"http://{Vhosts.Model}/api/hello");
        head.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        using var headResp = await http.SendAsync(head, Ct);
        Assert.Equal(HttpStatusCode.OK, headResp.StatusCode);
    }

    [Fact]
    public async Task PathOutsideTheAllowlist_IsRefusedAndLogged()
    {
        await using var f = await GatewayFixture.StartAsync();
        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        await using var gateway = f.StartRun(3);
        using var http = ProxyClient.For(gateway);

        foreach (var (method, url) in new[] { (HttpMethod.Get, $"http://{Vhosts.Model}/v1/files"), (HttpMethod.Post, $"http://{Vhosts.Model}/v1/oauth/token"),
                     (HttpMethod.Delete, $"http://{Vhosts.Model}/v1/messages") })
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
            using var resp = await http.SendAsync(req, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        Assert.Empty(f.Upstream.Requests); // nothing reached the vendor

        var denied = f.Log.Read(3).Where(e => e.Kind == GatewayEventKinds.PathDenied).ToArray();
        Assert.Equal(3, denied.Length);
        Assert.All(denied, e => Assert.True(e.Guardrail));
        Assert.Contains(denied, e => e.Method == "GET" && e.Target == "/v1/files" && e.Status == 403);
        Assert.Contains(denied, e => e.Method == "POST" && e.Target == "/v1/oauth/token");
    }

    [Fact]
    public async Task ConnectOutsideTheAllowlist_IsRefusedAndLogged_VendorHostsNeverAllowed()
    {
        await using var f = await GatewayFixture.StartAsync();
        // Even a policy that lists the vendor's API host cannot open it: the floor wins.
        await using var gateway = f.StartRun(4, allowHosts: ["registry.npmjs.org", "api.anthropic.com", "*.x.ai"]);

        foreach (var target in new[] { "example.com:443", "api.anthropic.com:443", "auth.x.ai:443", "registry.npmjs.org:22", "169.254.169.254:443" })
        {
            var (status, stream, client) = await ProxyClient.RawAsync(gateway, $"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n");
            using (client)
            {
                Assert.StartsWith("HTTP/1.1 403", status);
            }
        }
        // Plain http to a host the gateway does not serve, and https absolute-form, are refused too.
        var (plain, _, c1) = await ProxyClient.RawAsync(gateway, "GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n");
        c1.Dispose();
        Assert.StartsWith("HTTP/1.1 403", plain);
        var (https, _, c2) = await ProxyClient.RawAsync(gateway, "GET https://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n");
        c2.Dispose();
        Assert.StartsWith("HTTP/1.1 400", https);

        var log = f.Log.Read(4);
        var connects = log.Where(e => e.Kind == GatewayEventKinds.ConnectDenied).ToArray();
        Assert.Equal(5, connects.Length);
        Assert.All(connects, e => Assert.True(e.Guardrail));
        Assert.Contains(connects, e => e.Target == "example.com:443" && e.Detail!.Contains("not on this agent's allowlist"));
        Assert.Contains(connects, e => e.Target == "api.anthropic.com:443" && e.Detail!.Contains("never reachable"));
        Assert.Contains(connects, e => e.Target == "auth.x.ai:443" && e.Detail!.Contains("never reachable"));
        Assert.Contains(connects, e => e.Target == "registry.npmjs.org:22" && e.Detail!.Contains("port 22"));
        Assert.Contains(log, e => e.Kind == GatewayEventKinds.HostDenied && e.Vhost == "example.com" && e.Guardrail);
    }

    [Fact]
    public async Task ConnectToAnAllowlistedHost_OpensATunnel_ButNotToAPrivateAddressInProduction()
    {
        // In memory: "localhost" resolves (from the fake dialer's table) to 127.0.0.1, and the dialer's server on the
        // port is an echo. The real-socket path (DNS + TCP) is the opt-in test below.
        const int port = 8443;
        static async Task Echo(Stream s)
        {
            var buf = new byte[64];
            var n = await s.ReadAsync(buf);
            await s.WriteAsync(buf.AsMemory(0, n));
        }

        // Test mode: loopback targets allowed, so a local echo server stands in for an allowlisted registry.
        await using (var f = await GatewayFixture.StartAsync(allowPrivateConnect: true, connectPorts: new HashSet<int> { port }))
        {
            f.Dialer!.Serve(port, Echo);
            await using var gateway = f.StartRun(5, allowHosts: ["localhost"]);
            var (status, stream, client) = await ProxyClient.RawAsync(gateway, $"CONNECT localhost:{port} HTTP/1.1\r\nHost: localhost:{port}\r\n\r\n");
            using (client)
            {
                Assert.StartsWith("HTTP/1.1 200", status);
                while (await ReadLineAsync(stream) != "") { }
                await stream.WriteAsync("ping"u8.ToArray(), Ct);
                var buf = new byte[4];
                await stream.ReadExactlyAsync(buf, Ct);
                Assert.Equal("ping", Encoding.ASCII.GetString(buf));
            }
            Assert.Contains(f.Log.Read(5), e => e.Kind == GatewayEventKinds.ConnectAllowed && e.Target == $"localhost:{port}");
            Assert.Equal(("127.0.0.1", port), Assert.Single(f.Dialer.Dialed)); // the name's resolved address was dialed
        }

        // Production mode: an allowlisted name that resolves to loopback cannot reach the host's own ports.
        await using (var f = await GatewayFixture.StartAsync(allowPrivateConnect: false, connectPorts: new HashSet<int> { port }))
        {
            f.Dialer!.Serve(port, Echo);
            await using var gateway = f.StartRun(6, allowHosts: ["localhost"]);
            var (status, _, client) = await ProxyClient.RawAsync(gateway, $"CONNECT localhost:{port} HTTP/1.1\r\n\r\n");
            client.Dispose();
            Assert.StartsWith("HTTP/1.1 403", status);
            Assert.Contains(f.Log.Read(6), e => e.Kind == GatewayEventKinds.ConnectDenied && e.Detail!.Contains("loopback"));
            Assert.Empty(f.Dialer.Dialed); // refused before any connection was opened
        }
    }

    /// <summary>
    /// The same tunnel over real sockets: the system resolver, a TCP connection to a real echo listener, and the gateway
    /// on a real loopback port. Opt-in (YAWBLE_REAL_LOOPBACK=1); the in-memory test above covers the gateway's logic.
    /// </summary>
    [Fact]
    public async Task ConnectToAnAllowlistedHost_OpensATunnel_ButNotToAPrivateAddressInProduction_OverRealLoopback()
    {
        GatewayFixture.SkipUnlessRealLoopback("a real CONNECT tunnel to a real echo listener");
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var port = ((IPEndPoint)echo.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var c = await echo.AcceptTcpClientAsync(Ct);
            var s = c.GetStream();
            var buf = new byte[64];
            var n = await s.ReadAsync(buf, Ct);
            await s.WriteAsync(buf.AsMemory(0, n), Ct);
        }, Ct);

        // Test mode: loopback targets allowed, so a local echo server stands in for an allowlisted registry.
        await using (var f = await GatewayFixture.StartAsync(allowPrivateConnect: true, connectPorts: new HashSet<int> { port }, realLoopback: true))
        {
            await using var gateway = f.StartRun(5, allowHosts: ["localhost"]);
            var (status, stream, client) = await ProxyClient.RawAsync(gateway, $"CONNECT localhost:{port} HTTP/1.1\r\nHost: localhost:{port}\r\n\r\n");
            using (client)
            {
                Assert.StartsWith("HTTP/1.1 200", status);
                while (await ReadLineAsync(stream) != "") { }
                await stream.WriteAsync("ping"u8.ToArray(), Ct);
                var buf = new byte[4];
                await stream.ReadExactlyAsync(buf, Ct);
                Assert.Equal("ping", Encoding.ASCII.GetString(buf));
            }
            Assert.Contains(f.Log.Read(5), e => e.Kind == GatewayEventKinds.ConnectAllowed && e.Target == $"localhost:{port}");
        }

        // Production mode: an allowlisted name that resolves to loopback cannot reach the host's own ports.
        await using (var f = await GatewayFixture.StartAsync(allowPrivateConnect: false, connectPorts: new HashSet<int> { port }, realLoopback: true))
        {
            await using var gateway = f.StartRun(6, allowHosts: ["localhost"]);
            var (status, _, client) = await ProxyClient.RawAsync(gateway, $"CONNECT localhost:{port} HTTP/1.1\r\n\r\n");
            client.Dispose();
            Assert.StartsWith("HTTP/1.1 403", status);
            Assert.Contains(f.Log.Read(6), e => e.Kind == GatewayEventKinds.ConnectDenied && e.Detail!.Contains("loopback"));
        }
    }

    [Fact]
    public async Task WrongMissingOrOtherRunsToken_Is401AndAGuardrailEvent_NothingForwarded()
    {
        await using var f = await GatewayFixture.StartAsync();
        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        await using var mine = f.StartRun(7);
        await using var other = f.StartRun(8);
        using var http = ProxyClient.For(mine);
        foreach (var presented in new[] { null, "yawrt_guessed", other.Token.Value })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://{Vhosts.Model}/v1/messages") { Content = new StringContent("{}") };
            if (presented is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", presented);
            using var resp = await http.SendAsync(req, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        Assert.Empty(f.Upstream.Requests);
        var unauthorized = f.Log.Read(7).Where(e => e.Kind == GatewayEventKinds.Unauthorized).ToArray();
        Assert.Equal(3, unauthorized.Length);
        Assert.All(unauthorized, e => Assert.True(e.Guardrail));
        Assert.Contains(unauthorized, e => e.Detail!.StartsWith("Missing"));
        Assert.Equal(2, unauthorized.Count(e => e.Detail!.StartsWith("Wrong")));
        AssertNoSecretIn(f, other.Token.Value);
    }

    [Fact]
    public async Task VhostsOutsideTheRunsPreset_AreNotServed()
    {
        await using var f = await GatewayFixture.StartAsync();
        f.Credentials.Set(CredentialSlot.XaiApiKey, FakeSecrets.XaiKey);
        // A Claude run: only model.yawble.internal.
        await using var gateway = f.StartRun(9, vhosts: RunGatewaySpec.VhostsFor("claude", GrokMode.Session));
        using var http = ProxyClient.For(gateway);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{Vhosts.Xai}/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        using var resp = await http.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Empty(f.Upstream.Requests);
        Assert.Contains(f.Log.Read(9), e => e.Kind == GatewayEventKinds.HostDenied && e.Vhost == Vhosts.Xai);
    }

    [Fact]
    public async Task UpstreamRefusingTheClaudeCredential_GivesTheCardReason_AndAReconnectReachesTheNextRequest()
    {
        await using var f = await GatewayFixture.StartAsync();
        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, "sk-ant-oat01-FAKE-revoked");
        await using var gateway = f.StartRun(10);
        using var http = ProxyClient.For(gateway);

        async Task<HttpStatusCode> Ask()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{Vhosts.Model}/v1/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
            using var resp = await http.SendAsync(req, Ct);
            return resp.StatusCode;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await Ask());
        Assert.Contains(f.Log.Read(10), e => e.Kind == GatewayEventKinds.CredentialProblem && e.Detail == "Claude credential expired or revoked: reconnect");
        // The person reconnects; the running gateway reads the credential per request, so no restart is needed.
        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        Assert.Equal(HttpStatusCode.OK, await Ask());
    }

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("registry.npmjs.org.", true)] // a trailing dot names the same host
    [InlineData("169.254.169.254", false)] // an address: never reachable, over https either
    [InlineData("127.0.0.1", false)]
    [InlineData("localhost", false)]
    [InlineData("printer.local", false)]
    [InlineData("api.anthropic.com", false)] // a model vendor's host
    [InlineData("bus.yawble.internal", false)] // a gateway virtual host
    [InlineData("bus.yawble.internal.", false)]
    [InlineData("cloud.yawble.ai", false)] // the app's own
    [InlineData("", false)]
    public void OnlyAHostThePersonCouldAllow_IsToldToUseHttps(string host, bool outside) =>
        Assert.Equal(outside, RunGateway.IsOutsideHost(host));

    [Fact]
    public async Task RefusedPlainHttpToAnOutsideHost_CarriesThePlainHttpNote()
    {
        await using var f = await GatewayFixture.StartAsync();
        await using var gateway = f.StartRun(11, allowHosts: ["registry.npmjs.org"]);

        // Off the allowlist, and on it: plain http is refused either way, and the agent is told to use https.
        foreach (var host in new[] { "example.com", "registry.npmjs.org" })
        {
            var (status, body) = await RawBodyAsync(gateway, $"GET http://{host}/ HTTP/1.1\r\nHost: {host}\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 403", status);
            Assert.Contains($"{host} is not reachable from this agent", body);
            Assert.Contains(RunGateway.PlainHttpNote, body);
        }
        Assert.Empty(f.Upstream.Requests);
    }

    [Fact]
    public async Task RefusedGatewayVhost_HasNoPlainHttpNote()
    {
        await using var f = await GatewayFixture.StartAsync();
        // A Claude run: xai.yawble.internal is not served, and an unknown *.yawble.internal never is.
        await using var gateway = f.StartRun(12, vhosts: RunGatewaySpec.VhostsFor("claude", GrokMode.Session));

        foreach (var host in new[] { Vhosts.Xai, "nothing.yawble.internal" })
        {
            var (status, body) = await RawBodyAsync(gateway, $"GET http://{host}/v1/models HTTP/1.1\r\nHost: {host}\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 403", status);
            Assert.Contains($"{host} is not reachable from this agent", body);
            Assert.DoesNotContain("Only https is forwarded", body);
            Assert.DoesNotContain("https://", body);
        }
        Assert.Empty(f.Upstream.Requests);
        Assert.Contains(f.Log.Read(12), e => e.Kind == GatewayEventKinds.HostDenied && e.Vhost == Vhosts.Xai);
    }

    [Fact]
    public async Task HttpsRefusals_HaveNoPlainHttpNote()
    {
        await using var f = await GatewayFixture.StartAsync();
        await using var gateway = f.StartRun(13, allowHosts: ["registry.npmjs.org"]);

        // CONNECT off the allowlist, CONNECT to a refused port, and https absolute-form: none is a plain-http refusal.
        foreach (var (request, expected) in new[]
                 {
                     ("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n", "HTTP/1.1 403"),
                     ("CONNECT registry.npmjs.org:22 HTTP/1.1\r\nHost: registry.npmjs.org:22\r\n\r\n", "HTTP/1.1 403"),
                     ("GET https://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n", "HTTP/1.1 400"),
                 })
        {
            var (status, body) = await RawBodyAsync(gateway, request);
            Assert.StartsWith(expected, status);
            Assert.DoesNotContain("Only https is forwarded", body);
        }
    }

    /// <summary>A raw request's status line and what follows it (headers and the JSON error body).</summary>
    private static async Task<(string Status, string Body)> RawBodyAsync(RunGateway gateway, string request)
    {
        var (status, stream, client) = await ProxyClient.RawAsync(gateway, request);
        using (client)
        {
            // Read the headers, then exactly Content-Length bytes: the connection may stay open, so never to the end.
            var bytes = new List<byte>();
            var one = new byte[1];
            int end;
            while ((end = Encoding.ASCII.GetString(bytes.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
            {
                if (await stream.ReadAsync(one, Ct) != 1) return (status, Encoding.UTF8.GetString(bytes.ToArray()));
                bytes.Add(one[0]);
            }
            var headers = Encoding.ASCII.GetString(bytes.ToArray(), 0, end);
            var lengthLine = headers.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = lengthLine is null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim());
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, Ct);
            return (status, Encoding.UTF8.GetString(body));
        }
    }

    private static async Task<string> ReadLineAsync(Stream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (await s.ReadAsync(one) == 1 && one[0] != '\n') if (one[0] != '\r') sb.Append((char)one[0]);
        return sb.ToString();
    }

    /// <summary>No fake secret (nor the run token) in the database file or the gateway log.</summary>
    internal static void AssertNoSecretIn(GatewayFixture f, params string[] extra)
    {
        var log = string.Join("\n", f.Log.Read().Select(e => e.ToString()));
        foreach (var secret in FakeSecrets.All.Concat(extra))
        {
            Assert.DoesNotContain(secret, log);
        }
        // The SQLite file and its WAL, as bytes: the secrets live only in the credential store and the Grok home.
        foreach (var path in Directory.GetFiles(f.Db.Dir, "yawble.db*"))
        {
            var bytes = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            foreach (var secret in FakeSecrets.All.Concat(extra)) Assert.DoesNotContain(secret, bytes);
        }
    }
}
