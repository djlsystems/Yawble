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
/// A documents delete the Host cannot complete, through the real Host: never an unhandled
/// exception or an empty body, but 409 with a sentence naming each path left and why; the log
/// says `documents.deleted` and then `documents.delete-incomplete` naming them; and deleting again
/// once they are removable finishes it. The Host's own deletes refuse the named paths, the way an
/// agent's owner-only directory refuses the product's Host.
/// </summary>
public sealed class DocumentsDeleteIncompleteTests : IAsyncLifetime
{
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-docs-incomplete-").FullName;
    private readonly Dictionary<string, Exception> _refused = new(StringComparer.Ordinal);

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TeamDocuments Docs => _factory.Services.GetRequiredService<TeamDocuments>();

    public async ValueTask InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton(sp => new FolderRemoval(
                    sp.GetRequiredService<AgentLaunchUser>(),
                    sp.GetRequiredService<IUnfinishedRemovals>(),
                    new FolderRemoval.HostDeletes(
                        path =>
                        {
                            if (_refused.TryGetValue(path, out var refusal)) throw refusal;
                            File.Delete(path);
                        },
                        path =>
                        {
                            if (_refused.TryGetValue(path, out var refusal)) throw refusal;
                            Directory.Delete(path, recursive: false);
                        })));
            }));

        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        var agent = _factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        await registry.CreateAsync("Alpha", agent, memberAgent: agent, ct: Ct);
        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", Password);

        _client = _factory.CreateClient();
        (await _client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = Password }, Ct))
            .EnsureSuccessStatusCode();
    }

    private string Write(string relative)
    {
        var file = Path.Combine(Docs.EnsureFor("Alpha"), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        return file;
    }

    [Fact]
    public async Task A_folder_holding_a_path_the_host_cannot_remove_answers_409_naming_it_logs_deleted_then_incomplete_and_a_retry_finishes_it()
    {
        var stuck = Write("reports/locked/keep.md");
        var gone = Write("reports/free.md");
        _refused[stuck] = new UnauthorizedAccessException(stuck);

        var response = await _client.DeleteAsync("/api/teams/Alpha/documents?path=reports&recursive=true", Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var sentence = body.GetProperty("error").GetString()!;
        Assert.Contains("1 of 2 file(s) were removed", sentence);
        Assert.Contains("reports/locked/keep.md (permission denied)", sentence);
        var left = Assert.Single(body.GetProperty("remaining").EnumerateArray());
        Assert.Equal("reports/locked/keep.md", left.GetProperty("path").GetString());
        Assert.Equal("permission denied", left.GetProperty("reason").GetString());
        Assert.False(File.Exists(gone));
        Assert.True(File.Exists(stuck));

        // `documents.deleted` first, then the row saying what it left - nothing between them.
        var rows = await RowsAsync();
        Assert.Equal([TenantActions.DocumentsDeleted, TenantActions.DocumentsDeleteIncomplete], rows.Select(r => r.Action));
        using (var detail = JsonDocument.Parse(rows[1].Detail!))
        {
            Assert.Equal("reports", detail.RootElement.GetProperty("path").GetString());
            Assert.Equal(1, detail.RootElement.GetProperty("removed").GetInt32());
            var named = Assert.Single(detail.RootElement.GetProperty("remaining").EnumerateArray());
            Assert.Equal("reports/locked/keep.md", named.GetProperty("path").GetString());
            Assert.Equal("permission denied", named.GetProperty("reason").GetString());
        }

        // Removable now: deleting again finishes it, with its own row and no incomplete one.
        _refused.Clear();
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync("/api/teams/Alpha/documents?path=reports&recursive=true", Ct)).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(Docs.RootFor("Alpha"), "reports")));
        Assert.Equal(
            [TenantActions.DocumentsDeleted, TenantActions.DocumentsDeleteIncomplete, TenantActions.DocumentsDeleted],
            (await RowsAsync()).Select(r => r.Action));
    }

    [Fact]
    public async Task A_delete_that_removed_nothing_says_so_and_names_a_path_in_use()
    {
        var stuck = Write("busy.md");
        _refused[stuck] = new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));

        var response = await _client.DeleteAsync("/api/teams/Alpha/documents?path=busy.md", Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var sentence = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString()!;
        Assert.StartsWith("Nothing was removed.", sentence);
        Assert.Contains("busy.md (in use)", sentence);
        Assert.True(File.Exists(stuck));
        Assert.Equal(
            [TenantActions.DocumentsDeleted, TenantActions.DocumentsDeleteIncomplete],
            (await RowsAsync()).Select(r => r.Action));
    }

    [Fact]
    public async Task A_folder_holding_a_subfolder_the_host_cannot_list_answers_409_naming_it_not_an_unhandled_500()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions.");
        var locked = Path.GetDirectoryName(Write("reports/locked/keep.md"))!;
        var gone = Write("reports/free.md");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        _unlock.Add(locked);
        if (CanList(locked)) Assert.Skip("This process lists a mode-000 folder anyway (it runs as root).");

        var response = await _client.DeleteAsync("/api/teams/Alpha/documents?path=reports&recursive=true", Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Contains("reports/locked (permission denied)", body.GetProperty("error").GetString()!);
        Assert.Contains(body.GetProperty("remaining").EnumerateArray(), left => left.GetProperty("path").GetString() == "reports/locked");
        Assert.False(File.Exists(gone));

        var rows = await RowsAsync();
        Assert.Equal([TenantActions.DocumentsDeleted, TenantActions.DocumentsDeleteIncomplete], rows.Select(r => r.Action));
        Assert.Contains("reports/locked", rows[1].Detail!);

        // Listable again: deleting again finishes it.
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync("/api/teams/Alpha/documents?path=reports&recursive=true", Ct)).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(Docs.RootFor("Alpha"), "reports")));
    }

    private readonly List<string> _unlock = [];

    private static bool CanList(string folder)
    {
        try { _ = Directory.EnumerateFileSystemEntries(folder).Any(); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>This test's documents rows, oldest first.</summary>
    private async Task<List<TenantEvent>> RowsAsync() =>
        [.. (await _factory.Services.GetRequiredService<ITenantLog>().ReadAsync(null, 200, Ct)).Events
            .Where(e => e.Action.StartsWith("documents.", StringComparison.Ordinal))
            .OrderBy(e => e.Seq)];

    public async ValueTask DisposeAsync()
    {
        foreach (var folder in _unlock.Where(Directory.Exists))
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _client.Dispose();
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
