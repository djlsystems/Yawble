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
/// A local repository delete that could not remove everything, through the real Host: it never
/// answers Deleted while files remain. The answer and a <c>local-repo.delete-incomplete</c> row name
/// the moved-aside <c>.deleting-&lt;guid&gt;</c> folder and what is left in it, and it is retried as an
/// unfinished removal until it is gone. A <c>.deleting-*</c> folder found with no row - left by a
/// delete from before this - is removed at start. The first Host's own deletes refuse the moved-aside
/// repository's <c>HEAD</c>.
/// </summary>
public sealed class LocalRepoDeleteIncompleteTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-local-delete-").FullName;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private bool _refusing;
    private bool _personMade;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Repos => Path.Combine(_dataRoot, "repos");

    [Fact]
    public async Task A_partial_local_repository_delete_names_what_it_left_writes_its_row_and_is_retried_to_completion()
    {
        var factory = Start(refusing: true);
        using var client = await PersonAsync(factory);
        (await client.PostAsJsonAsync("/api/local-repos", new { name = "widget" }, Ct)).EnsureSuccessStatusCode();

        var response = await client.DeleteAsync("/api/local-repos/widget", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var folder = body.GetProperty("folder").GetString()!;
        var stuck = Path.Combine(folder, "HEAD");
        Assert.Equal(Repos, Path.GetDirectoryName(folder));
        Assert.StartsWith(LocalRepos.DeletingPrefix, Path.GetFileName(folder), StringComparison.Ordinal);
        Assert.Equal([stuck], body.GetProperty("remaining").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains(stuck, body.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains("not fully deleted", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(stuck));
        Assert.False(Directory.Exists(Path.Combine(Repos, "widget.git")));

        var log = await factory.Services.GetRequiredService<ITenantLog>().ReadAsync(null, 50, Ct);
        var row = Assert.Single(log.Events, e => e.Action == TenantActions.LocalRepoDeleteIncomplete);
        Assert.Equal("widget", row.Subject);
        Assert.Contains(JsonSerializer.Serialize(stuck).Trim('"'), row.Detail);

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/removals", Ct);
        Assert.Equal(folder, listed[0].GetProperty("path").GetString());
        Assert.Equal(RemovalKinds.LocalRepo, listed[0].GetProperty("kind").GetString());

        // Still refused: named again, never reported finished.
        var stillStuck = await client.PostAsJsonAsync("/api/removals/retry", new { path = folder }, Ct);
        var failed = (await stillStuck.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("retried")[0];
        Assert.False(failed.GetProperty("finished").GetBoolean());
        Assert.Equal(stuck, failed.GetProperty("remaining")[0].GetString());

        _refusing = false;
        var retried = await client.PostAsJsonAsync("/api/removals/retry", new { path = folder }, Ct);

        Assert.True((await retried.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("retried")[0].GetProperty("finished").GetBoolean());
        Assert.False(Directory.Exists(folder));
        Assert.Empty(await factory.Services.GetRequiredService<IUnfinishedRemovals>().ListAsync(Ct));
    }

    [Fact]
    public async Task A_partial_local_repository_delete_is_finished_at_the_next_start()
    {
        var first = Start(refusing: true);
        using var client = await PersonAsync(first);
        (await client.PostAsJsonAsync("/api/local-repos", new { name = "widget" }, Ct)).EnsureSuccessStatusCode();
        var response = await client.DeleteAsync("/api/local-repos/widget", Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var folder = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("folder").GetString()!;
        await first.DisposeAsync();

        var second = Start(refusing: false);

        Assert.False(Directory.Exists(folder));
        Assert.Empty(await second.Services.GetRequiredService<IUnfinishedRemovals>().ListAsync(Ct));
    }

    [Fact]
    public async Task A_deleting_folder_left_with_no_row_is_found_at_start_and_retried_until_gone()
    {
        // What a delete from before this change left: a .deleting- folder and no row.
        var left = Path.Combine(Repos, LocalRepos.DeletingPrefix + Guid.NewGuid().ToString("N"));
        var stuck = Path.Combine(left, "HEAD");
        Directory.CreateDirectory(Path.Combine(left, "objects", "ab"));
        await File.WriteAllTextAsync(stuck, "ref: refs/heads/main\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(left, "objects", "ab", "cdef"), "x", Ct);

        // A start that still cannot remove all of it records what is left.
        var first = Start(refusing: true);
        var row = Assert.Single(await first.Services.GetRequiredService<IUnfinishedRemovals>().ListAsync(Ct));
        Assert.Equal(left, row.Path);
        Assert.Equal(RemovalKinds.LocalRepo, row.Kind);
        Assert.Equal([stuck], row.Remaining);
        Assert.False(Directory.Exists(Path.Combine(left, "objects")));
        await first.DisposeAsync();

        var second = Start(refusing: false);

        Assert.False(Directory.Exists(left));
        Assert.Empty(await second.Services.GetRequiredService<IUnfinishedRemovals>().ListAsync(Ct));
    }

    private WebApplicationFactory<Program> Start(bool refusing)
    {
        _refusing = refusing;

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());

                // The Host refuses a moved-aside repository's HEAD while _refusing, the way a
                // file it cannot remove would.
                services.AddSingleton(sp => new FolderRemoval(
                    sp.GetRequiredService<AgentLaunchUser>(),
                    sp.GetRequiredService<IUnfinishedRemovals>(),
                    new FolderRemoval.HostDeletes(
                        path =>
                        {
                            if (_refusing
                                && Path.GetFileName(path) == "HEAD"
                                && Path.GetFileName(Path.GetDirectoryName(path))!.StartsWith(LocalRepos.DeletingPrefix, StringComparison.Ordinal))
                            {
                                throw new UnauthorizedAccessException(path);
                            }

                            File.Delete(path);
                        },
                        path => Directory.Delete(path, recursive: false))));
            }));
        _factories.Add(factory);
        _ = factory.Server;
        return factory;
    }

    private async Task<HttpClient> PersonAsync(WebApplicationFactory<Program> factory)
    {
        if (!_personMade)
        {
            await factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", Password);
            _personMade = true;
        }

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
