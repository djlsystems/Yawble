using System.Text;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;

namespace Harness.Tests;

/// <summary>
/// DOCUMENTS OUTLIVE THEIR TEAM, AND A SUCCESSOR DOES NOT INHERIT THEM.
///
/// A team is deleted as soon as its work is merged, which is exactly when its reports become the
/// only record of how that work was checked - so deletion keeps them, and they stay listed and
/// readable. Team ids are reusable, so a team recreated under the same name would meet them; the
/// claim retires the old folder instead, and the new team starts empty.
///
/// The real <see cref="TeamRegistry"/> and <see cref="TeamDeletion"/> over a real database and a
/// real data root, because both halves are about what those two do to the disk.
/// </summary>
public sealed class DocumentsRetentionTests : IAsyncDisposable
{
    private const string Report = "report.md";
    private const string Body = "how the work was checked";

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-docs-{Guid.NewGuid():N}");

    private readonly ContainerHost _host;
    private readonly TeamRegistry _teams;
    private readonly TeamDeletion _deletion;
    private readonly TeamDocuments _documents;

    public DocumentsRetentionTests()
    {
        Directory.CreateDirectory(_dataRoot);

        var database = Path.Combine(_dataRoot, "messages.db");
        new SchemaMigrator(database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();

        var store = new SqliteMessageStore(database);
        var pending = new SqlitePendingDeliveries(database);
        var triggers = new SqliteTriggerStore(database);
        var teamStore = new SqliteTeamStore(database);
        var principals = new SqlitePrincipalStore(database);
        var paths = new TeamPaths(_dataRoot);
        var git = new GitRunner();

        _documents = new TeamDocuments(paths);
        _host = new ContainerHost(
            store, store, store,
            new InMemoryTranscriptStore(), pending, triggers);

        var catalog = new AgentCatalog(
            [new AgentDefinition("fake", AgentMode.Headless, new AgentLaunch("true", []))]);
        var effective = new EffectiveSubscriptions(teamStore, triggers, store, _host);

        _teams = new TeamRegistry(
            _host, catalog, new AgentMemberRunner(new FakeAgent()), paths, teamStore,
            new AgentEnvironment(principals, catalog, "http://127.0.0.1:1", _documents),
            new FileBrowserPolicy([new FileBrowserRoot("data", _dataRoot, AllowCreate: true)]),
            store, effective, new RepoClone(git, enabled: false));

        _deletion = new TeamDeletion(
            _teams, _host, teamStore, store, store, effective, pending, triggers, principals, paths, git);
    }

    [Fact]
    public async Task Documents_survive_team_deletion_and_are_listed_and_readable_afterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAlphaAsync(ct);
        await _documents.SaveAsync("Alpha", null, Report, new MemoryStream(Encoding.UTF8.GetBytes(Body)), ct);

        var deleted = await _deletion.DeleteAsync("Alpha", ct: ct);

        Assert.NotNull(deleted);
        Assert.Empty(deleted.Failures);
        Assert.Null(_teams.ExistingName("Alpha"));

        var folder = Assert.Single(_documents.Folders());
        Assert.Equal("Alpha", folder.Team);
        Assert.False(folder.Retired);

        Assert.Equal(Report, Assert.Single(_documents.List(folder.Folder)).Name);
        Assert.Equal(Body, await File.ReadAllTextAsync(_documents.Resolve(folder.Folder, Report), ct));
    }

    [Fact]
    public async Task A_team_that_wrote_no_documents_leaves_no_documents_folder()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAlphaAsync(ct);

        var deleted = await _deletion.DeleteAsync("Alpha", ct: ct);

        // Nothing was kept, so nothing is listed: a folder holding only the platform's marker is
        // not a team's documents, and keeping it put a "documents kept" row in the dialog for
        // every team that ever existed.
        Assert.NotNull(deleted);
        Assert.Empty(deleted.Failures);
        Assert.Null(deleted.DocumentsKept);
        Assert.Empty(_documents.Folders());
    }

    [Fact]
    public async Task A_same_name_team_recreation_retires_the_old_folder()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAlphaAsync(ct);
        await _documents.SaveAsync("Alpha", null, Report, new MemoryStream(Encoding.UTF8.GetBytes(Body)), ct);
        Assert.NotNull(await _deletion.DeleteAsync("Alpha", ct: ct));

        await CreateAlphaAsync(ct);

        // The successor starts empty.
        Assert.Empty(_documents.List("Alpha"));

        // The predecessor's documents are aside, under a name no team can ever claim, still saying
        // whose they were, and still readable.
        var retired = Assert.Single(_documents.Folders(), f => f.Retired);
        Assert.Equal("Alpha", retired.Team);
        Assert.Equal(_documents.RootFor(retired.Folder),_teams.RetiredDocumentsFor("Alpha"));
        Assert.Equal(Body, await File.ReadAllTextAsync(_documents.Resolve(retired.Folder, Report), ct));
    }

    private Task CreateAlphaAsync(CancellationToken ct) =>
        _teams.CreateAsync(
            "Alpha", "fake",
            memberAgents: ["fake"], ct: ct);

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
