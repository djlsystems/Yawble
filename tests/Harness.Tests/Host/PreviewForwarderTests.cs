using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The real Host on Kestrel with one published preview port, and <see cref="EchoHeadersServer"/>
/// behind it where an app the Concierge started would listen. Every request here goes over a socket
/// to the preview port, as a browser's would.
/// </summary>
public sealed class PreviewForwarderTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-preview-test-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;

    /// <summary>The published port with the echo app behind it.</summary>
    private int _preview;

    /// <summary>A published port with nothing behind it.</summary>
    private int _empty;

    private string _hostAddress = "";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _preview = FreePair();
        _empty = FreePair(except: _preview);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host
                .UseSetting("DataRoot", _dataRoot)
                .UseSetting("urls", "http://127.0.0.1:0")
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .UseSetting(PreviewPorts.Variable, $"{_preview},{_empty}"));
        _factory.UseKestrel();
        _factory.StartServer();

        _hostAddress = _factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Select(a => new Uri(a)).First(a => a.Port != _preview && a.Port != _empty)
            .GetLeftPart(UriPartial.Authority);

        await _factory.Services.GetRequiredService<Harness.Contracts.IUserStore>()
            .CreateAsync("person@example.test", HostFixture.Password);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_request_to_a_preview_port_reaches_the_app_behind_it_with_no_cookie_the_host_sets()
    {
        await using var app = await EchoHeadersServer.StartAsync(PreviewPorts.BehindPort(_preview));
        var session = await SessionCookieAsync();
        var name = InstanceIdentity.CookieNameFor(_dataRoot);

        using var client = Plain();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_preview}/hello?x=1");
        request.Headers.Add("Cookie", $"{session}; app-session=keep-me; {name}C1=chunk; harness-another-instance=theirs; theme=dark");
        request.Headers.Add("X-Custom", "passes");

        using var response = await client.SendAsync(request, Ct);
        var echoed = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("GET /hello?x=1\n", echoed, StringComparison.Ordinal);
        Assert.Contains("Cookie: app-session=keep-me; theme=dark", echoed, StringComparison.Ordinal);
        Assert.DoesNotContain(name, echoed, StringComparison.Ordinal);
        Assert.DoesNotContain("harness-", echoed, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Split('=', 2)[1], echoed, StringComparison.Ordinal);
        Assert.Contains("X-Custom: passes", echoed, StringComparison.Ordinal);
        Assert.Contains($"Host: 127.0.0.1:{_preview}", echoed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_carrying_only_host_cookies_reaches_the_app_with_no_cookie_header()
    {
        await using var app = await EchoHeadersServer.StartAsync(PreviewPorts.BehindPort(_preview));
        var session = await SessionCookieAsync();

        using var client = Plain();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_preview}/");
        request.Headers.Add("Cookie", session);

        var echoed = await (await client.SendAsync(request, Ct)).Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("Cookie:", echoed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_apps_answer_passes_through_with_nothing_of_the_hosts_added()
    {
        await using var app = await EchoHeadersServer.StartAsync(PreviewPorts.BehindPort(_preview));

        using var client = Plain();
        using var response = await client.GetAsync($"http://127.0.0.1:{_preview}/page", Ct);

        Assert.Equal("echo", response.Headers.GetValues("X-From-App").Single());
        Assert.Equal(EchoHeadersServer.AppPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("app-set=1; Path=/", response.Headers.GetValues("Set-Cookie").Single());
        Assert.False(response.Headers.Contains("X-Frame-Options"));
        Assert.False(response.Headers.Contains("Referrer-Policy"));
        Assert.False(response.Headers.Contains("X-Content-Type-Options"));

        // And the Host's own responses keep theirs.
        using var own = await client.GetAsync($"{_hostAddress}/healthz", Ct);
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, own.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", own.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task A_post_body_reaches_the_app_and_a_host_route_on_a_preview_port_is_the_apps()
    {
        await using var app = await EchoHeadersServer.StartAsync(PreviewPorts.BehindPort(_preview));

        using var client = Plain();
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{_preview}/api/teams", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);
        var echoed = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("POST /api/teams\n", echoed, StringComparison.Ordinal);
        Assert.Equal("echo", response.Headers.GetValues("X-From-App").Single());
    }

    [Fact]
    public async Task A_websocket_on_a_preview_port_reaches_the_app()
    {
        await using var app = await EchoHeadersServer.StartAsync(PreviewPorts.BehindPort(_preview));

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_preview}/ws"), Ct);
        await socket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, Ct);

        var buffer = new byte[64];
        var received = await socket.ReceiveAsync(buffer, Ct);

        Assert.Equal("ping", Encoding.UTF8.GetString(buffer, 0, received.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
    }

    [Fact]
    public async Task A_preview_port_with_nothing_behind_it_answers_the_no_preview_page()
    {
        using var client = Plain();
        using var response = await client.GetAsync($"http://127.0.0.1:{_empty}/", Ct);
        var page = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(PreviewForwarder.NoPreviewSentence, page, StringComparison.Ordinal);
        Assert.Contains("Concierge", page, StringComparison.Ordinal);

        // The page is the Host's own, so it carries the Host's headers.
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
    }

    /// <summary>A real session cookie from a real sign-in, as <c>name=value</c>.</summary>
    private async Task<string> SessionCookieAsync()
    {
        using var client = Plain();
        using var login = await client.PostAsJsonAsync(
            $"{_hostAddress}/api/auth/login", new { email = "person@example.test", password = HostFixture.Password }, Ct);
        login.EnsureSuccessStatusCode();

        var name = InstanceIdentity.CookieNameFor(_dataRoot);
        var set = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(name + "=", StringComparison.Ordinal));
        return set.Split(';', 2)[0];
    }

    private static HttpClient Plain() =>
        new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false });

    /// <summary>A port free on every interface whose behind port is free on loopback too.</summary>
    private static int FreePair(int except = 0)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var port = Random.Shared.Next(20000, 45000);
            if (port == except || port == PreviewPorts.BehindPort(except)) continue;
            if (PreviewPorts.IsFree(port) && PreviewPorts.IsFree(PreviewPorts.BehindPort(port))) return port;
        }

        throw new InvalidOperationException("No free port pair found.");
    }
}
