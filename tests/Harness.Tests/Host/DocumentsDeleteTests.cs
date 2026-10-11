using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// Deleting anything in the Documents dialog, through the real Host. A non-empty folder goes
/// only when the caller says `recursive`; a gone team's documents - a file, a folder, the whole
/// folder - can be cleared out but not added to; the whole folder only when its marker says the
/// platform made it; every delete is recorded first, and a live team's watches hear every file.
/// </summary>
public sealed class DocumentsDeleteTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TeamDocuments Docs => host.Services.GetRequiredService<TeamDocuments>();

    private string Write(string folder, string relative, string body = "x")
    {
        var file = Path.Combine(Docs.RootFor(folder), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, body);
        return file;
    }

    /// <summary>A team created, given documents, and deleted - its folder kept, its team gone.</summary>
    private async Task<string> GoneTeamAsync(string name, params string[] files)
    {
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent, ct: Ct)).Id;

        foreach (var file in files) Write(team, file);

        Assert.NotNull(await host.Services.GetRequiredService<TeamDeletion>().DeleteAsync(team, ct: Ct));
        Assert.Null(registry.ExistingName(team));
        Assert.True(Directory.Exists(Docs.RootFor(team)));
        return team;
    }

    [Fact]
    public async Task A_recursive_delete_removes_a_non_empty_folder_and_without_recursive_it_is_409()
    {
        using var client = await host.PersonAsync();
        Docs.EnsureFor(host.Alpha);
        Write(host.Alpha, "full/a.md");
        Write(host.Alpha, "full/deeper/b.md");

        var refused = await client.DeleteAsync($"/api/teams/{host.Alpha}/documents?path=full", Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.True(File.Exists(Path.Combine(Docs.RootFor(host.Alpha), "full", "deeper", "b.md")));

        var deleted = await client.DeleteAsync(
            $"/api/teams/{host.Alpha}/documents?path=full&recursive=true", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(Docs.RootFor(host.Alpha), "full")));
    }

    [Fact]
    public async Task A_live_teams_documents_folder_itself_still_cannot_be_deleted()
    {
        using var client = await host.PersonAsync();
        Docs.EnsureFor(host.Alpha);

        var refused = await client.DeleteAsync($"/api/teams/{host.Alpha}/documents?recursive=true", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.True(Directory.Exists(Docs.RootFor(host.Alpha)));
    }

    [Fact]
    public async Task A_gone_teams_file_folder_and_whole_folder_can_be_deleted_but_not_added_to()
    {
        using var client = await host.PersonAsync();
        var team = await GoneTeamAsync("Gone", "one.md", "notes/two.md", "notes/three.md", "keep/four.md");
        var root = Docs.RootFor(team);

        // Not added to: upload and new folder still refuse a team that is gone.
        using var form = new MultipartFormDataContent { { new ByteArrayContent("x"u8.ToArray()), "file", "new.md" } };
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync($"/api/teams/{team}/documents/upload", form, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"/api/teams/{team}/documents/folders", new { path = "made" }, Ct)).StatusCode);
        Assert.False(File.Exists(Path.Combine(root, "new.md")));
        Assert.False(Directory.Exists(Path.Combine(root, "made")));

        // A file.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/teams/{team}/documents?path=one.md", Ct)).StatusCode);
        Assert.False(File.Exists(Path.Combine(root, "one.md")));

        // A folder.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/teams/{team}/documents?path=notes&recursive=true", Ct)).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(root, "notes")));

        // The whole folder, which then leaves the picker.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/teams/{team}/documents?recursive=true", Ct)).StatusCode);
        Assert.False(Directory.Exists(root));

        using var listed = JsonDocument.Parse(await client.GetStringAsync("/api/documents", Ct));
        Assert.DoesNotContain(
            listed.RootElement.GetProperty("folders").EnumerateArray(),
            f => f.GetProperty("folder").GetString() == team);
    }

    [Fact]
    public async Task A_retired_folder_can_be_deleted_whole()
    {
        using var client = await host.PersonAsync();
        var team = await GoneTeamAsync("Retiring", "old.md");

        // A successor of the same id retires the predecessor's folder.
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;
        await registry.CreateAsync(team, agent, memberAgent: agent, ct: Ct);

        var retired = Assert.Single(Docs.Folders(), f => f.Retired && f.Team == team).Folder;

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/teams/{retired}/documents?recursive=true", Ct)).StatusCode);
        Assert.False(Directory.Exists(Docs.RootFor(retired)));

        // The successor's own folder is untouched and still refuses a whole-folder delete.
        Assert.True(Directory.Exists(Docs.RootFor(team)));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.DeleteAsync($"/api/teams/{team}/documents?recursive=true", Ct)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Someone")]
    public async Task The_whole_folder_delete_is_refused_when_the_marker_is_missing_or_names_another_owner(
        string? owner)
    {
        using var client = await host.PersonAsync();
        var folder = owner is null ? "Unmarked" : "Misowned";
        Write(folder, "theirs.md");

        if (owner is not null)
        {
            File.WriteAllText(TeamPaths.MarkerIn(Docs.RootFor(folder)), $"{owner}\n{DateTimeOffset.UtcNow:O}\n");
        }

        var refused = await client.DeleteAsync($"/api/teams/{folder}/documents?recursive=true", Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(TeamPaths.MarkerFileName, await refused.Content.ReadAsStringAsync(Ct));
        Assert.True(File.Exists(Path.Combine(Docs.RootFor(folder), "theirs.md")));
    }

    [Fact]
    public async Task Every_delete_appends_a_documents_deleted_row_with_the_file_count()
    {
        using var client = await host.PersonAsync();
        var log = host.Services.GetRequiredService<ITenantLog>();
        var team = await GoneTeamAsync("Counted", "a.md", "set/b.md", "set/c.md", "set/inner/d.md");

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/teams/{team}/documents?path=a.md", Ct)).StatusCode);
        AssertRow(await log.FindLatestAsync(TenantActions.DocumentsDeleted, team, Ct), team, "a.md", false, 1);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/teams/{team}/documents?path=set&recursive=true", Ct)).StatusCode);
        AssertRow(await log.FindLatestAsync(TenantActions.DocumentsDeleted, team, Ct), team, "set", true, 3);

        Write(team, "last.md");
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/teams/{team}/documents?recursive=true", Ct)).StatusCode);
        AssertRow(await log.FindLatestAsync(TenantActions.DocumentsDeleted, team, Ct), team, "", true, 1);
    }

    private static void AssertRow(TenantEvent? row, string folder, string path, bool isFolder, int files)
    {
        Assert.NotNull(row);
        Assert.Equal("person@example.test", row.ActorEmail);
        Assert.False(string.IsNullOrEmpty(row.ActorId));

        using var detail = JsonDocument.Parse(row.Detail!);
        Assert.Equal(folder, detail.RootElement.GetProperty("folder").GetString());
        Assert.Equal(path, detail.RootElement.GetProperty("path").GetString());
        Assert.Equal(isFolder, detail.RootElement.GetProperty("isFolder").GetBoolean());
        Assert.Equal(files, detail.RootElement.GetProperty("files").GetInt32());
    }

    [Fact]
    public async Task A_recursive_delete_in_a_live_teams_folder_announces_each_removed_file_once_per_folder()
    {
        using var client = await host.PersonAsync();
        Docs.EnsureFor(host.Alpha);
        Write(host.Alpha, "tree/top.md");
        Write(host.Alpha, "tree/top2.md");
        Write(host.Alpha, "tree/sub/low.md");

        var log = host.Services.GetRequiredService<IMessageLog>();
        var before = (await log.ReadAfterAsync(0, [MessageTypes.FileChanged], int.MaxValue, Ct))
            .Select(m => m.Seq).DefaultIfEmpty(0).Max();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/teams/{host.Alpha}/documents?path=tree&recursive=true", Ct)).StatusCode);

        var announced = (await log.ReadAfterAsync(before, [MessageTypes.FileChanged], int.MaxValue, Ct))
            .Select(m => JsonDocument.Parse(m.Payload).RootElement)
            .Where(p => p.GetProperty(PayloadFields.Team).GetString() == host.Alpha)
            .ToDictionary(
                p => p.GetProperty(PayloadFields.Path).GetString()!,
                p => p.GetProperty(PayloadFields.Changed).EnumerateArray().Select(c => c.GetString()).Order().ToList());

        Assert.Equal(2, announced.Count);
        Assert.Equal(["tree/top.md", "tree/top2.md"], announced["tree"]);
        Assert.Equal(["tree/sub/low.md"], announced["tree/sub"]);
    }
}

/// <summary>
/// The delete does not happen when its `documents.deleted` row cannot be written. A Host of its own,
/// because the only honest way to make the row unwritable is to take the table away, as the tenant
/// settings tests do.
/// </summary>
public sealed class DocumentsDeleteUnrecordedTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-docs-unrecorded-{Guid.NewGuid():N}");

    private WebApplicationFactory<Program> _factory = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning"));

        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        var agent = _factory.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;
        await registry.CreateAsync("Alpha", agent, memberAgent: agent);
        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", HostFixture.Password);
    }

    [Fact]
    public async Task Nothing_is_deleted_when_the_documents_deleted_row_cannot_be_written()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.test", password = HostFixture.Password }, Ct))
            .EnsureSuccessStatusCode();

        var docs = _factory.Services.GetRequiredService<TeamDocuments>();
        var file = Path.Combine(docs.EnsureFor("Alpha"), "kept", "a.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=false"))
        {
            await connection.OpenAsync(Ct);
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE tenant_events";
            await drop.ExecuteNonQueryAsync(Ct);
        }

        var single = await client.DeleteAsync("/api/teams/Alpha/documents?path=kept/a.md", Ct);
        var tree = await client.DeleteAsync("/api/teams/Alpha/documents?path=kept&recursive=true", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, single.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, tree.StatusCode);
        Assert.True(File.Exists(file));
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
