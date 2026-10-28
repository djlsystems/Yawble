using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests.Host;

/// <summary>
/// THE SIGN-IN PAGE'S SOCKET ON THE REAL HOST, with its own in-process worker running Playwright's
/// headless Chromium against a local site. Refused to anyone but a person; a person's Done keeps the
/// browser's session through the row seam and the socket never carries it; nothing under the data
/// root holds it.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SignInPageTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string Cookie = "site-session-4b1d9e";
    private const string Local = "local-token-0c77aa";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-sign-in-page-{Guid.NewGuid():N}");
    private readonly Rows _rows = new();
    private WebApplicationFactory<Program> _factory = null!;
    private WebApplication _site = null!;
    private HttpClient _person = null!;
    private string _team = "";
    private string _key = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Route(string member = "Creds", string row = "site-admin") => $"/api/teams/{_team}/members/{member}/sign-in/entries/{row}";

    /// <summary>The row seam's test double: one interactive row, <c>site-admin</c>, and what was kept.</summary>
    private sealed class Rows : ISignInRows
    {
        public string SiteUrl { get; set; } = "";

        public ConcurrentDictionary<(ContainerId, string, string, string), string> Kept { get; } = new();

        public Task<SignInEntry?> EntryAsync(ContainerId member, string field, string row, CancellationToken ct = default) =>
            Task.FromResult(field == "entries" && row == "site-admin" ? new SignInEntry(SiteUrl, [], "session") : null);

        public Task SaveSessionAsync(
            ContainerId member, string field, string row, string column, SignInStorageState state, CredentialActor actor, CancellationToken ct = default)
        {
            Kept[(member, field, row, column)] = state.Json;
            return Task.CompletedTask;
        }
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginInstall.Write(_dataRoot, "webcreds");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _site = builder.Build();
        _site.MapGet("/", (HttpContext context) =>
        {
            context.Response.Cookies.Append("sid", Cookie);
            return Results.Content($"<html><head><title>Site</title></head><body>hi<script>localStorage.setItem('t','{Local}')</script></body></html>", "text/html");
        });
        await _site.StartAsync(Ct);
        _rows.SiteUrl = _site.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<ISignInRows>(_rows)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Sites", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        _key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(_team, "Manager").ToString(), PrincipalKind.Container, _team, Permits.All);

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Creds", agent = "plugin:webcreds" }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        await _site.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Only_a_person_reaches_the_page_and_only_for_a_rows_interactive_entry()
    {
        using var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route(), Ct)).StatusCode);

        // A Manager's (any container's) key holding every permit is still not a person.
        using var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, _key);
        var refused = await manager.GetAsync(Route(), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(PermitGate.HumansOnlyMessage, (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await _person.GetAsync(Route(member: "Nobody"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _person.GetAsync(Route(row: "missing"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _person.GetAsync(Route(row: "bad.name"), Ct)).StatusCode);

        // A person passes every gate; only the transport is wrong.
        var plain = await _person.GetAsync(Route(), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Contains("WebSocket", await plain.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Done_keeps_the_session_through_the_row_and_the_socket_never_carries_it()
    {
        Assert.SkipWhen(
            ChromiumSignInBrowser.Find(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")) is null,
            "No headless Chromium is installed (PLAYWRIGHT_BROWSERS_PATH).");

        var cookie = await LoginCookieAsync();
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Cookie = cookie;
        using var socket = await client.ConnectAsync(new Uri(_factory.Server.BaseAddress, Route()), Ct);

        var texts = new List<string>();
        var frames = 0;
        var buffer = new byte[4 * 1024 * 1024];

        async Task<string?> Next()
        {
            var length = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), Ct);
                length += result.Count;
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                frames++;
                return "";
            }

            var text = Encoding.UTF8.GetString(buffer, 0, length);
            texts.Add(text);
            return text;
        }

        async Task<JsonElement> Until(string kind)
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            bound.CancelAfter(TimeSpan.FromSeconds(60));
            while (await Next().WaitAsync(bound.Token) is { } text)
            {
                if (text.Length == 0) continue;
                var message = JsonDocument.Parse(text).RootElement;
                if (message.GetProperty("kind").GetString() == kind) return message.Clone();
            }

            Assert.Fail($"The socket closed before '{kind}': {string.Join(" | ", texts)}");
            return default;
        }

        var started = await Until("started");
        Assert.Equal(_rows.SiteUrl, started.GetProperty("site").GetString());
        // The site's own title: the page has loaded, so its cookie and local storage are set.
        while ((await Until("page")).TryGetProperty("title", out var title) is false || title.GetString() != "Site") { }
        while (frames == 0) await Next();

        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"mouse","event":"mouseMoved","x":10,"y":10}"""), WebSocketMessageType.Text, true, Ct);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"done"}"""), WebSocketMessageType.Text, true, Ct);

        var saved = await Until("saved");
        Assert.Contains("127.0.0.1", saved.GetProperty("hosts").EnumerateArray().Select(h => h.GetString()));
        var ended = await Until("ended");
        Assert.Equal(SignInSessions.SavedText, ended.GetProperty("reason").GetString());

        var kept = _rows.Kept[(new ContainerId(_team, "Creds"), "entries", "site-admin", "session")];
        Assert.Contains(Cookie, kept, StringComparison.Ordinal);
        Assert.Contains(Local, kept, StringComparison.Ordinal);

        Assert.DoesNotContain(texts, t => t.Contains(Cookie, StringComparison.Ordinal) || t.Contains(Local, StringComparison.Ordinal));
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            // The host's own lock files are held exclusively while it runs: they hold no state.
            if (Path.GetFileName(file).StartsWith("host.lock", StringComparison.Ordinal)) continue;
            byte[] bytes;
            await using (var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                using var copy = new MemoryStream();
                await read.CopyToAsync(copy, Ct);
                bytes = copy.ToArray();
            }

            Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Cookie)) >= 0, $"{file} holds the session cookie.");
        }
    }

    private async Task<string> LoginCookieAsync()
    {
        using var login = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        var answer = await login.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct);
        answer.EnsureSuccessStatusCode();
        return string.Join("; ", answer.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
    }
}
