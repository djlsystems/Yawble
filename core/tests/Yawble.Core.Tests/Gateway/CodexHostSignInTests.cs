using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// Codex's sign-in held by the gateway: the member's Codex home gets a placeholder
/// auth.json whose tokens are the run token; the gateway reads the person's own ~/.codex/auth.json at every request and
/// puts its access token and account on the request to chatgpt.com. The host's codex renews it near expiry.
/// </summary>
public class CodexHostSignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlySet<string> CodexOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Vhosts.Codex };

    private static string Jwt(JsonObject claims) =>
        "eyJhbGciOiJSUzI1NiJ9." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".SIGNATURE";

    private const string RealRefresh = "FAKE-real-refresh-token";
    private const string RealIdSignature = "SIGNATURE";

    private static void WriteHostAuth(string file, string access, DateTimeOffset expires)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var id = Jwt(new JsonObject
        {
            ["email"] = "person@example.com",
            ["sub"] = "secret-subject",
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "acct-1", ["chatgpt_plan_type"] = "pro" },
            ["exp"] = expires.ToUnixTimeSeconds(),
        });
        File.WriteAllText(file, new JsonObject
        {
            ["auth_mode"] = "chatgpt",
            ["OPENAI_API_KEY"] = null,
            ["tokens"] = new JsonObject
            {
                ["id_token"] = id,
                ["access_token"] = access,
                ["refresh_token"] = RealRefresh,
                ["account_id"] = "acct-1",
            },
            ["last_refresh"] = "2026-11-06T23:16:40Z",
        }.ToJsonString());
    }

    private static HttpRequestMessage Responses(string presented)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://{Vhosts.Codex}/backend-api/codex/responses")
        {
            Content = new StringContent("{\"model\":\"fake\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", presented);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", "placeholder"); // what the placeholder auth.json says
        return request;
    }

    [Fact]
    public async Task TheRunTokenIsSwappedForThePersonsCodexSignIn_WithTheirAccount_AndOnlyThatReachesOpenAI()
    {
        using var dir = new TempDb();
        var file = Path.Combine(dir.Dir, "home", ".codex", "auth.json");
        WriteHostAuth(file, FakeSecrets.CodexAccess, DateTimeOffset.UtcNow.AddDays(9));
        await using var f = await GatewayFixture.StartAsync(codexHost: new CodexHostSession(file, () => null));
        f.Upstream.ReleaseStream();
        await using var gateway = f.StartRun(1, vhosts: CodexOnly, codexFromHost: true);
        using var http = ProxyClient.For(gateway);

        using var response = await http.SendAsync(Responses(gateway.Token.Value), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("codex-host:acct-1", response.Headers.GetValues("x-fake-accepted").Single());
        var seen = Assert.Single(f.Upstream.Requests);
        Assert.Equal("Bearer " + FakeSecrets.CodexAccess, seen.Authorization);
        Assert.DoesNotContain(gateway.Token.Value, string.Join("\n", seen.Headers.Values));
        Assert.DoesNotContain("placeholder", string.Join("\n", seen.Headers.Values));
    }

    [Fact]
    public async Task WithoutACodexSignIn_TheRequestIsRefusedWithTheReason_AndNothingIsForwarded()
    {
        using var dir = new TempDb();
        await using var f = await GatewayFixture.StartAsync(codexHost: new CodexHostSession(Path.Combine(dir.Dir, "none", "auth.json"), () => null));
        await using var gateway = f.StartRun(2, vhosts: CodexOnly, codexFromHost: true);
        using var http = ProxyClient.For(gateway);
        using var response = await http.SendAsync(Responses(gateway.Token.Value), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("not signed in on this computer", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(f.Upstream.Requests);
    }

    [Fact]
    public async Task OnlyTheModelAndAccountPathsAreServed_NotPluginsOrAnalytics()
    {
        using var dir = new TempDb();
        var file = Path.Combine(dir.Dir, "home", ".codex", "auth.json");
        WriteHostAuth(file, FakeSecrets.CodexAccess, DateTimeOffset.UtcNow.AddDays(9));
        await using var f = await GatewayFixture.StartAsync(codexHost: new CodexHostSession(file, () => null));
        await using var gateway = f.StartRun(3, vhosts: CodexOnly, codexFromHost: true);
        using var http = ProxyClient.For(gateway);
        foreach (var (method, path) in new[] { ("POST", "/backend-api/codex/analytics-events/events"), ("GET", "/backend-api/ps/plugins/list"), ("POST", "/oauth/token") })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), $"http://{Vhosts.Codex}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
            using var response = await http.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Empty(f.Upstream.Requests);
    }

    [Fact]
    public void ThePlaceholderCarriesTheAccountButNoSecret_AndIsRecognisedAfterTheRun()
    {
        using var dir = new TempDb();
        var file = Path.Combine(dir.Dir, "home", ".codex", "auth.json");
        WriteHostAuth(file, FakeSecrets.CodexAccess, DateTimeOffset.UtcNow.AddDays(9));
        var json = new CodexHostSession(file, () => null).PlaceholderAuthJson("yawrt_RUN")!;
        Assert.DoesNotContain(FakeSecrets.CodexAccess, json);
        Assert.DoesNotContain(RealRefresh, json);
        Assert.DoesNotContain(RealIdSignature, json);
        Assert.DoesNotContain("secret-subject", json);
        var tokens = JsonNode.Parse(json)!["tokens"]!;
        Assert.Equal("yawrt_RUN", tokens["access_token"]!.GetValue<string>());
        Assert.Equal("yawrt_RUN", tokens["refresh_token"]!.GetValue<string>());
        Assert.Equal("acct-1", tokens["account_id"]!.GetValue<string>());
        var idPayload = tokens["id_token"]!.GetValue<string>().Split('.')[1].Replace('-', '+').Replace('_', '/');
        idPayload += new string('=', (4 - idPayload.Length % 4) % 4);
        var claims = JsonNode.Parse(Convert.FromBase64String(idPayload))!;
        Assert.Equal("person@example.com", claims["email"]!.GetValue<string>());
        Assert.Equal("pro", claims["https://api.openai.com/auth"]!["chatgpt_plan_type"]!.GetValue<string>());

        Assert.True(CodexHostSession.IsPlaceholderOnly(json, "yawrt_RUN"));
        Assert.False(CodexHostSession.IsPlaceholderOnly(json, "yawrt_OTHER"));
        Assert.False(CodexHostSession.IsPlaceholderOnly(File.ReadAllText(file), "yawrt_RUN"));
    }

    [Fact]
    public async Task ASignInCloseToExpiry_IsServedAndRenewedByTheHostsCodex_AnExpiredOneWaitsForTheRenewal()
    {
        using var dir = new TempDb();
        var file = Path.Combine(dir.Dir, "home", ".codex", "auth.json");
        string AccessExpiring(DateTimeOffset at) => Jwt(new JsonObject { ["exp"] = at.ToUnixTimeSeconds() });
        var old = AccessExpiring(DateTimeOffset.UtcNow.AddHours(-1));
        var renewed = AccessExpiring(DateTimeOffset.UtcNow.AddDays(10));
        WriteHostAuth(file, old, DateTimeOffset.UtcNow.AddDays(9));
        var ran = new List<string>();
        var session = new CodexHostSession(file, () => "codex", runCodex: (_, args, _) =>
        {
            lock (ran) ran.Add(string.Join(' ', args));
            WriteHostAuth(file, renewed, DateTimeOffset.UtcNow.AddDays(9)); // what codex does when it refreshes
            return Task.CompletedTask;
        });
        var token = await session.CurrentAsync(Ct);
        Assert.Equal(renewed, token!.AccessToken);
        Assert.Equal(["exec --skip-git-repo-check Reply with the single word ok."], ran);
        Assert.Equal(renewed, (await session.CurrentAsync(Ct))!.AccessToken);
        Assert.Single(ran);
    }

    [Fact]
    public void CodexRunsUseTheGatewayHeldSignIn_WithEverythingThroughTheGateway()
    {
        Assert.Equal(CredentialMode.CodexGatewaySignIn, AgentPresets.HostSignInMode(AgentCli.Codex));
        Assert.True(AgentPresets.NeedsHostSignIn(CredentialMode.CodexGatewaySignIn));
        Assert.False(AgentPresets.IsHostSignIn(CredentialMode.CodexGatewaySignIn));
        Assert.Empty(AgentPresets.PassthroughHosts(CredentialMode.CodexGatewaySignIn));
        var vhost = Assert.Single(AgentPresets.Vhosts(CredentialMode.CodexGatewaySignIn), v => v.Kind == "model");
        Assert.Equal(AgentPresets.CodexHost, vhost.Host);
        Assert.Equal("https://chatgpt.com", vhost.Upstream);
    }
}
