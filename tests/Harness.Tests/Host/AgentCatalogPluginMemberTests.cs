using System.Net;
using System.Net.Http.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A CATALOG SAVE NEVER TREATS A PLUGIN MEMBER AS AN AGENT. Saving the Agent catalog refuses a
/// catalog that leaves a member's Agent without a headless command; a plugin member runs
/// `plugin:&lt;id&gt;`, which is no Agent at all, so it can never be left without one. Seen live:
/// with a team holding the job-fetcher plugin, cloning `claude-headless` and saving was refused
/// naming `plugin:job-fetcher`, and no catalog could be saved on that instance.
/// </summary>
public sealed class AgentCatalogPluginMemberTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-catalog-plugin-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting("ScheduleRunnerEnabled", "false")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        _team = (await _factory.Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Plugged", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        var echo = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo",
            agent = "plugin:sample-echo",
            config = new { mode = "upper" },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private static AgentDefinition Clone(string name) =>
        AgentCatalogFile.BuiltIns().First(a => a.Name == "claude-headless") with { Name = name };

    [Fact]
    public async Task A_cloned_agent_saves_while_a_team_has_a_plugin_member()
    {
        var saved = await _person.PutAsJsonAsync("/api/agents", new { agents = new[] { Clone("claude-headless-sonnet") } }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
    }

    [Fact]
    public async Task An_agent_a_member_still_runs_is_still_refused_naming_that_member()
    {
        // The control: the check itself stays. A member on a custom Agent keeps that Agent in the catalog.
        Assert.Equal(HttpStatusCode.NoContent,
            (await _person.PutAsJsonAsync("/api/agents", new { agents = new[] { Clone("custom-sonnet") } }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Dev", agent = "custom-sonnet" }, Ct)).StatusCode);

        var removed = await _person.PutAsJsonAsync("/api/agents", new { agents = Array.Empty<AgentDefinition>() }, Ct);
        var error = (await removed.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct))!["error"];

        Assert.Equal(HttpStatusCode.BadRequest, removed.StatusCode);
        Assert.Contains("'custom-sonnet' is what Dev", error);
        Assert.DoesNotContain("plugin:", error);
    }
}
