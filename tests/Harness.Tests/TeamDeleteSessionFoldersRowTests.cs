using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE TEAM DELETE'S TENANT ROW COUNTS THE SESSION FOLDERS IT REMOVED AND NAMES ANY IT LEFT. The
/// route's <c>team.deleted</c> row carries <c>sessionFolders</c> and <c>sessionFoldersRemaining</c>,
/// the same as its result, so the one record left of the team says what went from the agent home.
/// The Host's agent home is its HOME, pointed here at the test's own folder.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class TeamDeleteSessionFoldersRowTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-session-folders-row-").FullName;
    private readonly string _dataRoot;
    private readonly string _home;
    private readonly EnvironmentScope _environment;
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TeamDeleteSessionFoldersRowTests()
    {
        _dataRoot = Path.Combine(_root, "data");
        _home = Path.Combine(_root, "agent-home");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_home);
        _environment = new EnvironmentScope([new("HOME", _home)]);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));
    }

    [Fact]
    public async Task The_team_deleted_row_counts_the_session_folders_removed_and_names_those_left()
    {
        var person = await PersonAsync();
        var agent = _factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var created = await person.PostAsJsonAsync("/api/teams", new { name = "Row Team", agent, memberAgents = new[] { agent } }, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        var team = (await JsonAsync(created)).GetProperty("id").GetString()!;

        var paths = _factory.Services.GetRequiredService<TeamPaths>();
        var workspace = paths.WorkspaceFor(Assert.Single(_factory.Services.GetRequiredService<TeamRegistry>().ContainerIdsOf(team)));

        // Claude's projects folder goes; its cache folder, reached through a linked `~/.cache`, is left.
        var projects = Path.Combine(_home, ".claude", "projects", LiveView.Dashed(workspace));
        Directory.CreateDirectory(projects);
        File.WriteAllText(Path.Combine(projects, "session.jsonl"), "x");
        var outside = Path.Combine(_root, "outside-cache");
        var kept = Path.Combine(outside, "claude-cli-nodejs", LiveView.Dashed(workspace), "log.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllText(kept, "x");
        Directory.CreateSymbolicLink(Path.Combine(_home, ".cache"), outside);

        var deleted = await person.DeleteAsync($"/api/teams/{team}", Ct);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.False(Directory.Exists(projects));
        Assert.True(File.Exists(kept));

        var body = await JsonAsync(deleted);
        Assert.Equal(1, body.GetProperty("sessionFolders").GetInt32());
        var bodyLeft = Assert.Single(body.GetProperty("sessionFoldersRemaining").EnumerateArray()).GetString()!;

        var rows = (await _factory.Services.GetRequiredService<ITenantLog>().ReadAsync(null, ITenantLog.MaxTake, Ct)).Events;
        using var row = JsonDocument.Parse(Assert.Single(rows, e => e.Action == TenantActions.TeamDeleted).Detail!);
        Assert.Equal(1, row.RootElement.GetProperty("sessionFolders").GetInt32());
        var rowLeft = Assert.Single(row.RootElement.GetProperty("sessionFoldersRemaining").EnumerateArray()).GetString()!;
        Assert.Equal(bodyLeft, rowLeft);
        Assert.Contains(LiveView.Dashed(workspace), rowLeft, StringComparison.Ordinal);
        Assert.Contains("no symbolic link on the way", rowLeft, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private async Task<HttpClient> PersonAsync()
    {
        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", Password);
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = Password }, Ct);
        login.EnsureSuccessStatusCode();
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _environment.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
