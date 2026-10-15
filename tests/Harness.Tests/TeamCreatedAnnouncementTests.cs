using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// EVERY WAY A TEAM IS CREATED TELLS EVERY OPEN WINDOW. A window learns of a team made somewhere
/// else only from the hub's <c>teamCreated</c> push to the person's group; a create that does not
/// announce leaves every board, Teams tab and Monitor already open without the team until a reload.
/// Listened for here on the real hub, as a browser does.
/// </summary>
public sealed class TeamCreatedAnnouncementTests : IAsyncDisposable
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("harness-team-announce-").FullName;
    private readonly WebApplicationFactory<Program> _factory;
    private HubConnection? _hub;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TeamCreatedAnnouncementTests()
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));
    }

    [Fact]
    public async Task A_team_created_on_the_teams_route_is_announced_to_the_persons_open_windows()
    {
        var (person, announced) = await ListeningPersonAsync();

        (await person.PostAsJsonAsync("/api/teams",
            new { name = "MadeHere", agent = Agent(), memberAgents = new[] { Agent() } }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal("MadeHere", await NextAnnouncedAsync(announced));
    }

    [Fact]
    public async Task A_team_created_by_a_backlog_dispatch_to_a_new_team_is_announced_to_the_persons_open_windows()
    {
        var (person, announced) = await ListeningPersonAsync();
        var created = await person.PostAsJsonAsync("/api/backlog", new { title = "An item", body = "A spec." }, Ct);
        created.EnsureSuccessStatusCode();
        var item = (await JsonAsync(created)).GetProperty("id").GetInt64();
        (await person.PatchAsJsonAsync($"/api/backlog/{item}", new { state = "ready" }, Ct)).EnsureSuccessStatusCode();

        var dispatched = await person.PostAsJsonAsync($"/api/backlog/{item}/dispatch-to-new",
            new { name = "MadeByDispatch", agent = Agent(), memberAgents = new[] { Agent() } }, Ct);
        Assert.True(dispatched.IsSuccessStatusCode, await dispatched.Content.ReadAsStringAsync(Ct));

        Assert.Equal("MadeByDispatch", await NextAnnouncedAsync(announced));
    }

    [Fact]
    public async Task A_cloned_team_is_announced_to_the_persons_open_windows()
    {
        var (person, announced) = await ListeningPersonAsync();
        (await person.PostAsJsonAsync("/api/teams",
            new { name = "Original", agent = Agent(), memberAgents = new[] { Agent() }, localRepository = false }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal("Original", await NextAnnouncedAsync(announced));

        var cloned = await person.PostAsJsonAsync("/api/teams/Original/clone", new { name = "Copied" }, Ct);
        Assert.True(cloned.IsSuccessStatusCode, await cloned.Content.ReadAsStringAsync(Ct));

        Assert.Equal("Copied", await NextAnnouncedAsync(announced));
    }

    /// <summary>A signed-in person with a window open: the hub joined to their own group, as the
    /// browser joins it, collecting each team it is told was created.</summary>
    private async Task<(HttpClient Person, ChannelReader<string> Announced)> ListeningPersonAsync()
    {
        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        var person = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct);
        login.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        person.DefaultRequestHeaders.Add("Cookie", cookie);

        var announced = Channel.CreateUnbounded<string>();
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hub/containers"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Headers["Cookie"] = cookie;
            })
            .Build();
        _hub.On<JsonElement>("teamCreated", change => announced.Writer.TryWrite(change.GetProperty("team").GetString()!));
        await _hub.StartAsync(Ct);
        await _hub.InvokeAsync("JoinMe", Ct);
        return (person, announced.Reader);
    }

    private static async Task<string> NextAnnouncedAsync(ChannelReader<string> announced)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(Patience);
        try
        {
            return await announced.ReadAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            Assert.Fail("no teamCreated reached the person's open window: a window already open never shows the team until it is reloaded");
            throw;
        }
    }

    private string Agent() =>
        _factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
        await _factory.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
