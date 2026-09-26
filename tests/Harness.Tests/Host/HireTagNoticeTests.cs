using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// When no agent this team may hire on carries the requested tag, the hire still
/// falls back to the first allowed agent - but the caller is told so in the reply it reads (the
/// <c>member</c> tool result), not only in the <c>X-Harness-Hiring-Notice</c> header nobody sees;
/// and the hiring view names the roles no allowed agent covers.
/// </summary>
public sealed class HireTagNoticeTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_hire_whose_tag_no_allowed_agent_carries_tells_the_caller_in_the_tool_result()
    {
        await host.Services.GetRequiredService<TeamRegistry>()
            .SetMemberAgentsAsync(host.Alpha, ["grok-headless"], Ct);

        var result = await Tools().Hire("Developer Rowan", team: host.Alpha, @for: "developer", cancellationToken: Ct);

        Assert.StartsWith("HTTP 200", result);
        var body = JsonDocument.Parse(result[result.IndexOf('{')..]).RootElement;

        // The fallback itself is unchanged: the first allowed agent.
        Assert.Equal("grok-headless", body.GetProperty("agent").GetString());
        Assert.Equal(
            "no agent on this team is tagged developer; hired grok-headless instead",
            body.GetProperty("hiringNotice").GetString());
    }

    [Fact]
    public async Task A_hire_whose_tag_is_carried_says_nothing_about_a_substitution()
    {
        await host.Services.GetRequiredService<TeamRegistry>()
            .SetMemberAgentsAsync(host.Alpha, ["grok-headless"], Ct);

        var result = await Tools().Hire("Researcher Ada", team: host.Alpha, @for: "researcher", cancellationToken: Ct);

        Assert.StartsWith("HTTP 200", result);
        var body = JsonDocument.Parse(result[result.IndexOf('{')..]).RootElement;
        Assert.False(body.TryGetProperty("hiringNotice", out var notice) && notice.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task The_hiring_view_names_the_roles_no_allowed_agent_covers()
    {
        await host.Services.GetRequiredService<TeamRegistry>()
            .SetMemberAgentsAsync(host.Alpha, ["grok-headless"], Ct);
        using var person = await host.PersonAsync();

        var hiring = await person.GetFromJsonAsync<JsonElement>($"/api/teams/{host.Alpha}/hiring", Ct);

        Assert.Equal(
            ["developer", "tester"],
            hiring.GetProperty("uncoveredTags").EnumerateArray().Select(e => e.GetString()!).ToArray());

        // Through the hiring tool too, which is where the Manager reads it.
        var viaTool = await Tools().Hiring(team: host.Alpha, cancellationToken: Ct);
        Assert.Contains("\"uncoveredTags\":[\"developer\",\"tester\"]", viaTool, StringComparison.Ordinal);
    }

    private PlatformMcpTools Tools()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = host.AlphaContainerKey;
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal(new ContainerId(host.Alpha, "Worker").ToString(), PrincipalKind.Container, new HashSet<string>()),
            "test");

        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context },
            host.Services.GetRequiredService<IPrincipalStore>(),
            host.Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(host.ServerHandler()));
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
