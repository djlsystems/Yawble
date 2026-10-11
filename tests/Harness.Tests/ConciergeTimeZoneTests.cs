using System.Net.Http.Json;
using System.Net.WebSockets;
using Harness.Contracts;
using Harness.Host;
using Harness.Pty;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// ONE CLOCK: THE CONCIERGE STATES TIMES IN THE PERSON'S OWN TIME ZONE, the one the page shows. The
/// browser names its IANA zone on the socket that opens the session, and the launch carries it as
/// `HARNESS_TIME_ZONE` and `TZ`; the prompt says to state every time in it, or to name the zone with
/// the time when there is none.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ConciergeTimeZoneTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-time-zone-{Guid.NewGuid():N}");

    public ConciergeTimeZoneTests() => Directory.CreateDirectory(_dataRoot);

    [Fact]
    public async Task The_concierge_launch_carries_the_persons_time_zone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var restore = new EnvironmentScope([new("HOME", _dataRoot)]);
        var (factory, agent) = Factory();

        var spec = await factory.ForAsync(
            team: "", teamLabel: "this instance", user: "user-1", login: "person@example.com",
            agent, teamEnv: new Dictionary<string, string>(), timeZone: "America/New_York", ct: ct);

        Assert.Equal("America/New_York", spec.Env!["HARNESS_TIME_ZONE"]);
        Assert.Equal("America/New_York", spec.Env["TZ"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("../../etc/passwd")]
    [InlineData("America/New_York\nHARNESS_KEY=x")]
    public async Task A_zone_that_is_not_one_is_not_carried(string? given)
    {
        var ct = TestContext.Current.CancellationToken;
        using var restore = new EnvironmentScope([new("HOME", _dataRoot)]);
        var (factory, agent) = Factory();

        var spec = await factory.ForAsync(
            team: "", teamLabel: "this instance", user: "user-1", login: "person@example.com",
            agent, teamEnv: new Dictionary<string, string>(), timeZone: given, ct: ct);

        Assert.False(spec.Env!.ContainsKey("HARNESS_TIME_ZONE"));
        Assert.False(spec.Env.ContainsKey("TZ"));
    }

    [Fact]
    public void The_concierge_prompt_says_to_state_times_in_the_persons_zone()
    {
        var prompt = BuiltInPrompts.ConciergePromptText.ReplaceLineEndings(" ");

        Assert.Contains("HARNESS_TIME_ZONE", prompt, StringComparison.Ordinal);
        Assert.Contains("in their own time zone", prompt, StringComparison.Ordinal);
        Assert.Contains("name the zone with every time", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_socket_route_hands_the_launch_the_zone_the_browser_named()
    {
        var ct = TestContext.Current.CancellationToken;
        ConciergeBrowser? handed = null;
        var launched = new TaskCompletionSource();

        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton(new ConciergeSessionStore(
                new SilentEngine(),
                (key, team, browser, _) =>
                {
                    handed = browser;
                    launched.TrySetResult();
                    return Task.FromResult(new PtySpec("true", _dataRoot));
                },
                (_, _) => Task.CompletedTask))));

        await app.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", "correct horse battery", ct: ct);

        using var login = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var answer = await login.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.test", password = "correct horse battery" }, ct);
        answer.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", answer.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        var client = app.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Cookie = cookie;
        using var socket = await client.ConnectAsync(
            new Uri("ws://vm.example:9000/api/concierge/ws?cols=80&rows=24&tz=America%2FNew_York"), ct);

        await launched.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);

        Assert.Equal("America/New_York", handed?.TimeZone);
        Assert.Equal("http://vm.example:9000", handed?.PublicUrl);
    }

    private (ConciergeLaunchFactory Factory, string Agent) Factory()
    {
        var catalog = new AgentCatalog(AgentCatalogFile.BuiltIns());
        var agent = catalog.Definitions
            .First(d => d.Mode == AgentMode.Interactive && !d.Hidden && d.Launch.LanguageModel).Name;

        return (new ConciergeLaunchFactory(
            new TeamPaths(_dataRoot),
            "http://127.0.0.1:5000", new MintingPrincipals(), catalog), agent);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>A terminal that prints nothing and never exits.</summary>
    private sealed class SilentEngine : IPtyEngine
    {
        public Task<IPtySession> SpawnAsync(PtySpec spec, CancellationToken ct) =>
            Task.FromResult<IPtySession>(new SilentSession());
    }

    private sealed class SilentSession : IPtySession
    {
#pragma warning disable CS0067 // never raised: this terminal is silent
        public event Action<byte[]>? Output;
        public event Action<int>? Exited;
#pragma warning restore CS0067

        public void Write(ReadOnlySpan<byte> bytes) { }

        public void Resize(int cols, int rows) { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
