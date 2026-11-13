using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// Claude's sign-in held by the gateway: the CLI presents its run token, the gateway
/// reads the person's own host sign-in at every request and puts it on the request; the host's claude renews it.
/// </summary>
public class ClaudeHostSignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void WriteHostSignIn(string file, string accessToken, DateTimeOffset expiresAt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file,
            $"{{\"claudeAiOauth\":{{\"accessToken\":\"{accessToken}\",\"refreshToken\":\"sk-ant-ort01-FAKE\",\"expiresAt\":{expiresAt.ToUnixTimeMilliseconds()},\"scopes\":[\"user:inference\"]}}}}");
    }

    private static HttpRequestMessage Messages(RunGateway gateway)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://{Vhosts.Model}/v1/messages")
        {
            Content = new StringContent("{\"model\":\"fake\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20,claude-code-20250219");
        return request;
    }

    [Fact]
    public async Task TheRunTokenIsSwappedForThePersonsHostSignIn_AndOnlyThatReachesAnthropic()
    {
        using var dir = new TempDb();
        var file = Path.Combine(dir.Dir, "home", ".claude", ".credentials.json");
        WriteHostSignIn(file, FakeSecrets.SetupToken, DateTimeOffset.UtcNow.AddHours(4));
        var session = new ClaudeHostSession(file, () => null);
        await using var f = await GatewayFixture.StartAsync(claudeHost: session);
        f.Upstream.ReleaseStream();
        await using var gateway = f.StartRun(1, claudeFromHost: true);
        using var http = ProxyClient.For(gateway);

        using var response = await http.SendAsync(Messages(gateway), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("claude-subscription", response.Headers.GetValues("x-fake-accepted").Single());
        var seen = Assert.Single(f.Upstream.Requests);
        Assert.Equal("Bearer " + FakeSecrets.SetupToken, seen.Authorization);
        Assert.DoesNotContain(gateway.Token.Value, string.Join("\n", seen.Headers.Values));
        Assert.Contains("oauth-2025-04-20", seen.Headers["anthropic-beta"]);
    }

    [Fact]
    public async Task WithoutAHostSignIn_TheRequestIsRefusedWithTheReason_AndNothingIsForwarded()
    {
        using var dir = new TempDb();
        var session = new ClaudeHostSession(Path.Combine(dir.Dir, "missing", ".credentials.json"), () => null);
        await using var f = await GatewayFixture.StartAsync(claudeHost: session);
        await using var gateway = f.StartRun(2, claudeFromHost: true);
        using var http = ProxyClient.For(gateway);

        using var response = await http.SendAsync(Messages(gateway), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("not signed in on this computer", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(f.Upstream.Requests);
    }

    [Fact]
    public async Task AnotherRunsTokenIsRefused_EvenWithAHostSignIn()
    {
        using var dir = new TempDb();
        var file = Path.Combine(dir.Dir, "home", ".claude", ".credentials.json");
        WriteHostSignIn(file, FakeSecrets.SetupToken, DateTimeOffset.UtcNow.AddHours(4));
        await using var f = await GatewayFixture.StartAsync(claudeHost: new ClaudeHostSession(file, () => null));
        await using var a = f.StartRun(3, claudeFromHost: true);
        await using var b = f.StartRun(4, claudeFromHost: true);
        using var http = ProxyClient.For(a);
        using var request = Messages(a);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", b.Token.Value);
        using var response = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(f.Upstream.Requests);
    }

    [Fact]
    public async Task AnExpiringSignIn_IsRenewedByTheHostsClaude_StatusFirstThenOneSmallPrompt()
    {
        using var dir = new TempDb();
        var file = Path.Combine(dir.Dir, "home", ".claude", ".credentials.json");
        WriteHostSignIn(file, "sk-ant-oat01-OLD", DateTimeOffset.UtcNow.AddMinutes(2));
        var ran = new List<string>();
        var session = new ClaudeHostSession(file, () => "claude", runClaude: (_, args, _) =>
        {
            lock (ran) ran.Add(string.Join(' ', args.Take(2)));
            if (args[0] == "-p") WriteHostSignIn(file, FakeSecrets.SetupToken, DateTimeOffset.UtcNow.AddHours(8)); // what claude does on use
            return Task.CompletedTask;
        });

        var token = await session.CurrentAsync(Ct);
        Assert.Equal(FakeSecrets.SetupToken, token!.AccessToken);
        Assert.Equal(["auth status", "-p Reply with the single word ok."], ran);

        // Fresh now: no further renewal.
        Assert.Equal(FakeSecrets.SetupToken, (await session.CurrentAsync(Ct))!.AccessToken);
        Assert.Equal(2, ran.Count);
    }

    [Fact]
    public async Task ARenewalThatFails_LeavesNoToken_SoTheRunIsToldToSignIn()
    {
        using var dir = new TempDb();
        var file = Path.Combine(dir.Dir, "home", ".claude", ".credentials.json");
        WriteHostSignIn(file, "sk-ant-oat01-OLD", DateTimeOffset.UtcNow.AddMinutes(-1));
        var session = new ClaudeHostSession(file, () => "claude", runClaude: (_, _, _) => Task.CompletedTask);
        Assert.Null(await session.CurrentAsync(Ct));
    }

    [Fact]
    public void TheSignInIsReadAsTheHostsClaudeWritesIt_AndNothingElseCountsAsOne()
    {
        using var dir = new TempDb();
        var file = Path.Combine(dir.Dir, "c.json");
        var session = new ClaudeHostSession(file, () => null);
        Assert.Null(session.Read());
        File.WriteAllText(file, "{\"claudeAiOauth\":{}}");
        Assert.Null(session.Read());
        File.WriteAllText(file, "not json");
        Assert.Null(session.Read());
        WriteHostSignIn(file, "sk-ant-oat01-X", DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        var t = session.Read()!;
        Assert.Equal("sk-ant-oat01-X", t.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000), t.ExpiresAt);
    }
}
