using System.Net;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The MCP tools call the platform's own routes back, on the address members are told
/// (<see cref="MemberBaseAddress"/>) through the client factory - never on the request's
/// <c>Host</c> header, which the caller writes, and never with a new <c>HttpClient</c> per call.
/// </summary>
public sealed class McpProxyTests
{
    [Fact]
    public async Task A_tool_calls_back_on_the_factory_client_and_ignores_the_Host_header()
    {
        var handler = new RecordingHandler();
        var clients = new FakeClients(handler, new Uri("http://127.0.0.1:8123"));
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("attacker.example", 9);
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = "the-callers-key";
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal("person-1", PrincipalKind.User, new HashSet<string>()), "test");

        var tools = new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context }, new MintingPrincipals(),
            new AgentCatalog([]), clients);

        var first = await tools.SkillsGet("member", cancellationToken: TestContext.Current.CancellationToken);
        await tools.SkillsGet("worktrees", cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith("HTTP 200", first);
        Assert.Equal(
            ["http://127.0.0.1:8123/api/me/skills/member", "http://127.0.0.1:8123/api/me/skills/worktrees"],
            handler.Requests.Select(r => r.Uri));
        Assert.All(handler.Requests, r => Assert.Equal("the-callers-key", r.Key));
        Assert.Equal([PlatformMcpTools.ClientName, PlatformMcpTools.ClientName], clients.Names);
    }

    [Fact]
    public async Task The_hosts_mcp_client_calls_the_member_base_address()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"harness-mcp-proxy-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(host => host
                    .UseSetting("DataRoot", dataRoot)
                    .UseSetting("MemberBaseAddress", "http://members.example:4321")
                    .UseSetting("Logging:LogLevel:Default", "Warning"));

            var client = factory.Services.GetRequiredService<IHttpClientFactory>()
                .CreateClient(PlatformMcpTools.ClientName);

            Assert.Equal(new Uri("http://members.example:4321"), client.BaseAddress);
        }
        finally
        {
            try { Directory.Delete(dataRoot, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class FakeClients(HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = baseAddress };
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Uri, string? Key)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues(ApiKeyAuthenticationHandler.Header, out var values) ? values.Single() : null));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
