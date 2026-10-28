using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests.Host;

/// <summary>
/// HOW A SIGN-IN ENDS, AS THE PAGE HEARS IT, on the real Host's socket - Kestrel's, whose socket a
/// cancelled read aborts, unlike the test server's - over a worker that runs
/// browsers in name only: a session replaced by the same person's next one, and one left idle for
/// 10 minutes, each tell the page why before the socket closes.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SignInPageEndTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-sign-in-end-{Guid.NewGuid():N}");
    private readonly ManualTime _clock = new(DateTimeOffset.Parse("2026-10-28T12:00:00Z"));
    private WebApplicationFactory<Program> _factory = null!;
    private string _team = "";
    private Uri _address = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rows : ISignInRows
    {
        public Task<SignInEntry?> EntryAsync(ContainerId member, string field, string row, CancellationToken ct = default) =>
            Task.FromResult<SignInEntry?>(new SignInEntry("https://site.example/", [], "session"));

        public Task SaveSessionAsync(
            ContainerId member, string field, string row, string column, SignInStorageState state, CredentialActor actor, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>Starts nothing; a stop ends the browser's stream, as a real worker's does.</summary>
    private sealed class NameOnlyWorker(Func<WorkerStreams> streams) : IRunWorker
    {
        private long _seq;

        public WorkerId Id { get; } = new("w-name-only");

        public Task Closed { get; } = new TaskCompletionSource().Task;

        public ConcurrentQueue<ControlMessage> Sent { get; } = new();

        public async Task SendAsync(ControlMessage message, CancellationToken ct = default)
        {
            Sent.Enqueue(message);
            if (message is StopSignInBrowser stop)
            {
                await streams().HandleAsync(new WorkerEnvelope(Id, Interlocked.Increment(ref _seq), new SignInBrowserEnded(stop.Session, "The browser closed.")));
            }
        }
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginInstall.Write(_dataRoot, "webcreds");

        WorkerStreams? streams = null;
        var worker = new NameOnlyWorker(() => streams!);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("urls", "http://127.0.0.1:0")
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<ISignInRows>(new Rows());
                services.AddSingleton(sp => new SignInSessions(
                    () => worker, streams = sp.GetRequiredService<WorkerStreams>(), sp.GetRequiredService<ISignInRows>(), _clock));
            }));
        _factory.UseKestrel();
        _factory.StartServer();
        _address = new Uri(_factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());

        _team = (await _factory.Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Sites", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);

        using var person = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = _address };
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        var hired = await person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Creds", agent = "plugin:webcreds" }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private async Task<WebSocket> OpenAsync()
    {
        using var login = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = _address };
        var answer = await login.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct);
        answer.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", answer.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", cookie);
        socket.Options.SetRequestHeader("Origin", _address.GetLeftPart(UriPartial.Authority));
        await socket.ConnectAsync(new Uri($"ws://{_address.Authority}/api/teams/{_team}/members/Creds/sign-in/entries/site-admin"), Ct);
        Assert.Equal("started", (await NextAsync(socket))?.GetProperty("kind").GetString());
        return socket;
    }

    /// <summary>The next text message, or null once the socket closes.</summary>
    private static async Task<JsonElement?> NextAsync(WebSocket socket)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bound.CancelAfter(TimeSpan.FromSeconds(20));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var length = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), bound.Token);
                length += result.Count;
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType == WebSocketMessageType.Text) return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, length)).RootElement.Clone();
        }
    }

    private static async Task<string?> EndedAsync(WebSocket socket)
    {
        while (await NextAsync(socket) is { } message)
        {
            if (message.GetProperty("kind").GetString() == "ended") return message.GetProperty("reason").GetString();
        }

        return null;
    }

    [Fact]
    public async Task A_replaced_session_tells_its_page_why_before_it_closes()
    {
        using var first = await OpenAsync();
        using var second = await OpenAsync();

        Assert.Equal(SignInSessions.ReplacedText, await EndedAsync(first));
        Assert.Equal(WebSocketState.Open, second.State);
    }

    [Fact]
    public async Task An_idle_session_tells_its_page_why_before_it_closes()
    {
        using var socket = await OpenAsync();

        _clock.Advance(SignInSessions.IdleTimeout + TimeSpan.FromSeconds(1));

        Assert.Equal(SignInSessions.IdleText, await EndedAsync(socket));
    }
}
