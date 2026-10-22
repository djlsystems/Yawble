using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A change carrying a valid session cookie and another origin's <c>Origin</c> - a preview port's
/// page, on the Host's own host name - is refused before any handler runs, and changes nothing; the
/// Host's own <c>Origin</c>, and none, behave as they always have. The in-memory Host is addressed
/// as <c>http://localhost</c>, so that is its own origin here.
/// </summary>
public sealed class OriginGateTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string PreviewOrigin = "http://localhost:8081";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_change_with_a_session_cookie_and_a_preview_ports_origin_is_refused_and_changes_nothing()
    {
        using var person = await host.PersonAsync();

        using var response = await CreateTeamAsync(person, "OriginPreview", PreviewOrigin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(OriginGate.Refusal, await ErrorAsync(response));
        Assert.Null(host.Services.GetRequiredService<TeamRegistry>().ExistingName("OriginPreview"));
    }

    [Fact]
    public async Task An_opaque_origin_is_refused_like_any_other()
    {
        using var person = await host.PersonAsync();

        using var response = await CreateTeamAsync(person, "OriginNull", "null");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(host.Services.GetRequiredService<TeamRegistry>().ExistingName("OriginNull"));
    }

    [Fact]
    public async Task The_hosts_own_origin_and_no_origin_behave_as_today()
    {
        using var person = await host.PersonAsync();

        using var own = await CreateTeamAsync(person, "OriginOwn", "http://localhost");
        using var none = await CreateTeamAsync(person, "OriginNone", origin: null);

        Assert.True(own.IsSuccessStatusCode, await own.Content.ReadAsStringAsync(Ct));
        Assert.True(none.IsSuccessStatusCode, await none.Content.ReadAsStringAsync(Ct));
        Assert.NotNull(host.Services.GetRequiredService<TeamRegistry>().ExistingName("OriginOwn"));
        Assert.NotNull(host.Services.GetRequiredService<TeamRegistry>().ExistingName("OriginNone"));
    }

    [Theory]
    [InlineData("/hub/containers")]
    [InlineData("/api/concierge/ws")]
    public async Task A_websocket_from_a_preview_ports_origin_is_refused(string path)
    {
        using var person = await host.PersonAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", PreviewOrigin);
        request.Headers.Add("Connection", "Upgrade");
        request.Headers.Add("Upgrade", "websocket");
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");

        using var response = await person.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(OriginGate.Refusal, await ErrorAsync(response));
    }

    [Fact]
    public async Task A_read_from_another_origin_is_not_the_gates_to_refuse()
    {
        using var person = await host.PersonAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/overview");
        request.Headers.Add("Origin", PreviewOrigin);

        using var response = await person.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_sites_capability_route_takes_its_opaque_origin()
    {
        using var anonymous = host.Anonymous();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sites/alpha/board/_c/not-a-capability/_api/actions/go")
        {
            Content = new StringContent("{}"),
        };
        request.Headers.Add("Origin", "null");

        using var response = await anonymous.SendAsync(request, Ct);

        Assert.DoesNotContain(OriginGate.Refusal, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost", "localhost", true)]
    [InlineData("https://localhost", "localhost", true)]
    [InlineData("http://LOCALHOST:8080", "localhost:8080", true)]
    [InlineData("https://board.example:443", "board.example", true)]
    [InlineData("http://[::1]:8080", "[::1]:8080", true)]
    [InlineData("http://localhost:8081", "localhost:8080", false)]
    [InlineData("http://localhost:8081", "localhost", false)]
    [InlineData("http://elsewhere:8080", "localhost:8080", false)]
    [InlineData("null", "localhost:8080", false)]
    [InlineData("file://", "localhost:8080", false)]
    public void Own_is_the_host_and_port_the_request_was_sent_to(string origin, string hostHeader, bool own) =>
        Assert.Equal(own, OriginGate.IsOwn(origin, new HostString(hostHeader)));

    private async Task<HttpResponseMessage> CreateTeamAsync(HttpClient person, string name, string? origin)
    {
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/teams")
        {
            Content = JsonContent.Create(new { name, agent, memberAgents = new[] { agent } }),
        };

        if (origin is not null) request.Headers.Add("Origin", origin);
        return await person.SendAsync(request, Ct);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("error").GetString();
    }
}
