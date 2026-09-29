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
/// THE CONCIERGE HANDS A PERSON LINKS, SO IT IS TOLD THE ADDRESS THE PERSON USES. `HARNESS_PUBLIC_URL`
/// is the address of the browser request that opened the session - a VM address, a tunnel - while
/// `HARNESS_URL` stays the internal one. Members keep the internal address and are never handed
/// the public one.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ConciergePublicUrlTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-public-url-{Guid.NewGuid():N}");

    public ConciergePublicUrlTests() => Directory.CreateDirectory(_dataRoot);

    [Fact]
    public async Task The_concierge_is_handed_the_browsers_address_and_keeps_the_internal_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var restore = new EnvironmentScope([new("HOME", _dataRoot)]);
        var (factory, agent) = Factory();

        var spec = await factory.ForAsync(
            team: "", teamLabel: "this instance", user: "user-1", login: "person@example.com",
            agent, teamEnv: new Dictionary<string, string>(),
            publicUrl: "https://tunnel.example.net", ct: ct);

        Assert.Equal("https://tunnel.example.net", spec.Env!["HARNESS_PUBLIC_URL"]);
        Assert.Equal("http://127.0.0.1:5000", spec.Env["HARNESS_URL"]);
    }

    [Fact]
    public async Task Without_a_browser_address_the_concierge_is_handed_the_internal_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var restore = new EnvironmentScope([new("HOME", _dataRoot)]);
        var (factory, agent) = Factory();

        var spec = await factory.ForAsync(
            team: "", teamLabel: "this instance", user: "user-1", login: "person@example.com",
            agent, teamEnv: new Dictionary<string, string>(), publicUrl: "javascript:alert(1)", ct: ct);

        Assert.Equal("http://127.0.0.1:5000", spec.Env!["HARNESS_PUBLIC_URL"]);
    }

    [Theory]
    [InlineData("http://10.0.0.5:8080", "http://10.0.0.5:8080")]
    [InlineData("https://tunnel.example.net/", "https://tunnel.example.net")]
    [InlineData("https://host.example/base/?q=1#x", "https://host.example/base")]
    [InlineData("ftp://host.example", null)]
    [InlineData("/relative", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_public_address_is_scheme_host_port_and_base_path_only(string? given, string? expected) =>
        Assert.Equal(expected, ConciergeLaunchFactory.PublicUrl(given));

    [Fact]
    public async Task A_member_keeps_the_internal_address_and_is_not_handed_the_public_one()
    {
        var catalog = new AgentCatalog(AgentCatalogFile.BuiltIns());
        var agent = catalog.Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var environment = new AgentEnvironment(
            new MintingPrincipals(), catalog, "http://127.0.0.1:5000",
            new TeamDocuments(new TeamPaths(_dataRoot)));

        var member = await environment.ForContainerAsync(
            new ContainerId("Alpha", "Worker"), agent, new HashSet<string> { Permits.Progress },
            new Dictionary<string, string> { ["HARNESS_PUBLIC_URL"] = "https://tunnel.example.net" }, [],
            TestContext.Current.CancellationToken);

        Assert.Equal("http://127.0.0.1:5000", member["HARNESS_URL"]);
        Assert.False(member.ContainsKey("HARNESS_PUBLIC_URL"));
    }

    [Fact]
    public async Task The_socket_route_hands_the_launch_the_address_the_browser_opened_it_on()
    {
        var ct = TestContext.Current.CancellationToken;
        string? handed = null;
        var launched = new TaskCompletionSource();

        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton(new ConciergeSessionStore(
                new SilentEngine(),
                (key, team, publicUrl, _) =>
                {
                    handed = publicUrl;
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
        using var socket = await client.ConnectAsync(new Uri("ws://vm.example:9000/api/concierge/ws"), ct);

        await launched.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);

        Assert.Equal("http://vm.example:9000", handed);
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
