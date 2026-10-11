using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A team deletion that could not finish, through the real Host: the route's answer and its tenant
/// log row name every remaining path, and the removal is finished at the next start or when a
/// person retries it. The first Host's own deletes refuse one path, the way an agent's owner-only
/// directory refuses the product's Host.
/// </summary>
public sealed class UnfinishedRemovalHostTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-unfinished-").FullName;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_removal_that_failed_is_named_in_the_result_and_the_log_and_finished_at_the_next_start()
    {
        var first = Start(refusing: true);
        using var client = await PersonAsync(first);
        var (root, stuck) = await TeamWithAStuckFileAsync(first);

        var response = await client.DeleteAsync("/api/teams/Alpha", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal([stuck], body.GetProperty("remaining").EnumerateArray().Select(p => p.GetString()));

        var log = await first.Services.GetRequiredService<ITenantLog>().ReadAsync(null, 50, Ct);
        var row = Assert.Single(log.Events, e => e.Action == TenantActions.TeamDeleted);
        Assert.Contains(JsonSerializer.Serialize(stuck).Trim('"'), row.Detail);
        Assert.True(File.Exists(TeamPaths.MarkerIn(root)));
        await first.DisposeAsync();

        // The next start, with a Host that can remove it.
        var second = Start(refusing: false);

        Assert.False(Directory.Exists(root));
        Assert.Empty(await second.Services.GetRequiredService<IUnfinishedRemovals>().ListAsync(Ct));
    }

    [Fact]
    public async Task A_person_can_retry_a_removal_on_request()
    {
        var factory = Start(refusing: true);
        using var client = await PersonAsync(factory);
        var (root, stuck) = await TeamWithAStuckFileAsync(factory);
        (await client.DeleteAsync("/api/teams/Alpha", Ct)).EnsureSuccessStatusCode();

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/removals", Ct);
        Assert.Equal(root, listed[0].GetProperty("path").GetString());

        var stillStuck = await client.PostAsJsonAsync("/api/removals/retry", new { path = root }, Ct);
        var failed = (await stillStuck.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("retried")[0];
        Assert.False(failed.GetProperty("finished").GetBoolean());
        Assert.Equal(stuck, failed.GetProperty("remaining")[0].GetString());

        _refused.Clear();
        var retried = await client.PostAsJsonAsync("/api/removals/retry", new { path = root }, Ct);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.True((await retried.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("retried")[0].GetProperty("finished").GetBoolean());
        Assert.False(Directory.Exists(root));

        var unknown = await client.PostAsJsonAsync("/api/removals/retry", new { path = root }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private async Task<(string Root, string Stuck)> TeamWithAStuckFileAsync(WebApplicationFactory<Program> factory)
    {
        var registry = factory.Services.GetRequiredService<TeamRegistry>();
        var agent = factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync("Alpha", agent, memberAgent: agent, ct: Ct)).Id;
        var root = factory.Services.GetRequiredService<TeamPaths>().RootFor(team);
        var stuck = Path.Combine(root, "workspaces", "Manager", "tmp", "stuck.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(stuck)!);
        await File.WriteAllTextAsync(stuck, "x", Ct);
        _refused.Add(stuck);
        return (root, stuck);
    }

    private WebApplicationFactory<Program> Start(bool refusing)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());

                if (refusing)
                {
                    services.AddSingleton(sp => new FolderRemoval(
                        sp.GetRequiredService<AgentLaunchUser>(),
                        sp.GetRequiredService<IUnfinishedRemovals>(),
                        new FolderRemoval.HostDeletes(
                            path =>
                            {
                                if (_refused.Contains(path)) throw new UnauthorizedAccessException(path);
                                File.Delete(path);
                            },
                            path => Directory.Delete(path, recursive: false))));
                }
            }));
        _factories.Add(factory);
        _ = factory.Server;
        return factory;
    }

    private static async Task<HttpClient> PersonAsync(WebApplicationFactory<Program> factory)
    {
        await factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", Password);
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = Password }, Ct))
            .EnsureSuccessStatusCode();
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories) await factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
