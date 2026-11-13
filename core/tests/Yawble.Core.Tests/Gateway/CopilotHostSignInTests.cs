using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Yawble.Core.Gateway;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// GitHub Copilot's sign-in held by the gateway: the CLI presents its run token
/// GitHub-shaped (gho_&lt;run token&gt;) to copilot.yawble.internal; the gateway reads the person's own Copilot token from
/// the OS credential store at every request and puts it on the request to api.githubcopilot.com.
/// </summary>
public class CopilotHostSignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlySet<string> CopilotOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Vhosts.Copilot };

    private static HttpRequestMessage Responses(string presented) => new(HttpMethod.Post, $"http://{Vhosts.Copilot}/responses")
    {
        Content = new StringContent("{\"model\":\"fake\"}", Encoding.UTF8, "application/json"),
        Headers = { Authorization = new AuthenticationHeaderValue("Bearer", presented) },
    };

    [Fact]
    public async Task TheGitHubShapedRunTokenIsSwappedForThePersonsCopilotToken_AndOnlyThatReachesGitHub()
    {
        await using var f = await GatewayFixture.StartAsync(copilotToken: () => FakeSecrets.CopilotToken);
        f.Upstream.ReleaseStream();
        await using var gateway = f.StartRun(1, vhosts: CopilotOnly, copilotFromHost: true);
        using var http = ProxyClient.For(gateway);

        using var response = await http.SendAsync(Responses(AgentPresets.CopilotPlaceholder(gateway.Token.Value)), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("copilot-host", response.Headers.GetValues("x-fake-accepted").Single());
        var seen = Assert.Single(f.Upstream.Requests);
        Assert.Equal("Bearer " + FakeSecrets.CopilotToken, seen.Authorization);
        Assert.DoesNotContain(gateway.Token.Value, string.Join("\n", seen.Headers.Values));
    }

    [Fact]
    public async Task WithoutACopilotSignIn_TheRequestIsRefusedWithTheReason_AndNothingIsForwarded()
    {
        await using var f = await GatewayFixture.StartAsync(copilotToken: () => null);
        await using var gateway = f.StartRun(2, vhosts: CopilotOnly, copilotFromHost: true);
        using var http = ProxyClient.For(gateway);
        using var response = await http.SendAsync(Responses(AgentPresets.CopilotPlaceholder(gateway.Token.Value)), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("not signed in on this computer", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(f.Upstream.Requests);
    }

    [Fact]
    public async Task AnotherRunsTokenOrAnyOtherGitHubToken_IsRefused()
    {
        await using var f = await GatewayFixture.StartAsync(copilotToken: () => FakeSecrets.CopilotToken);
        await using var a = f.StartRun(3, vhosts: CopilotOnly, copilotFromHost: true);
        await using var b = f.StartRun(4, vhosts: CopilotOnly, copilotFromHost: true);
        using var http = ProxyClient.For(a);
        foreach (var presented in new[] { AgentPresets.CopilotPlaceholder(b.Token.Value), FakeSecrets.CopilotToken, "gho_" + a.Token.Value[..^2] })
        {
            using var response = await http.SendAsync(Responses(presented), Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Empty(f.Upstream.Requests);
    }

    [Fact]
    public async Task OnlyCopilotsOwnPathsAreServed()
    {
        await using var f = await GatewayFixture.StartAsync(copilotToken: () => FakeSecrets.CopilotToken);
        await using var gateway = f.StartRun(5, vhosts: CopilotOnly, copilotFromHost: true);
        using var http = ProxyClient.For(gateway);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{Vhosts.Copilot}/user/repos");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AgentPresets.CopilotPlaceholder(gateway.Token.Value));
        using var response = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(f.Upstream.Requests);
    }

    [Fact]
    public void ACopilotRunGetsThePlaceholderAndTheGatewayUrl_AndNoCopilotHostByConnect()
    {
        Assert.Equal(CredentialMode.CopilotGatewaySignIn, AgentPresets.HostSignInMode(AgentCli.Copilot));
        Assert.True(AgentPresets.NeedsHostSignIn(CredentialMode.CopilotGatewaySignIn));
        Assert.False(AgentPresets.IsHostSignIn(CredentialMode.CopilotGatewaySignIn));
        Assert.Empty(AgentPresets.PassthroughHosts(CredentialMode.CopilotGatewaySignIn));
        Assert.Contains("api.githubcopilot.com", AgentPresets.PassthroughHosts(CredentialMode.CopilotHostSignIn));
        var vhost = Assert.Single(AgentPresets.Vhosts(CredentialMode.CopilotGatewaySignIn), v => v.Kind == "model");
        Assert.Equal(AgentPresets.CopilotHost, vhost.Host);
        Assert.Equal("https://api.githubcopilot.com", vhost.Upstream);
    }
}
