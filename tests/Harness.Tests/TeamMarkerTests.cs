using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;

namespace Harness.Tests;

/// <summary>
/// DELETION REMOVES A DIRECTORY ONLY WHEN THE `.harness-team` MARKER SAYS THIS PLATFORM MADE IT.
///
/// A team root is a path a person can type. Without the marker there is no evidence the platform
/// created the directory, so deletion leaves it and names it rather than removing somebody's only
/// copy of their work on the strength of a stored string.
/// </summary>
public sealed class TeamMarkerTests : IAsyncDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-marker-{Guid.NewGuid():N}");

    private readonly ContainerHost _host;
    private readonly TeamRegistry _teams;
    private readonly TeamDeletion _deletion;

    public TeamMarkerTests()
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
        var documents = new TeamDocuments(paths);

        _host = new ContainerHost(
            store, store, store, new LedgerContextBuilder(new SqliteLedger(database)),
            new InMemoryTranscriptStore(), pending, triggers);

        var catalog = new AgentCatalog(
            [new AgentDefinition("fake", AgentMode.Headless, new AgentLaunch("true", []))]);
        var effective = new EffectiveSubscriptions(teamStore, triggers, store, _host);

        _teams = new TeamRegistry(
            _host, catalog, new FakeAgent(), paths, teamStore,
            new AgentEnvironment(principals, catalog, "http://127.0.0.1:1", documents),
            new FileBrowserPolicy([new FileBrowserRoot("data", _dataRoot, AllowCreate: true)]),
            store, effective, new RepoClone(git, enabled: false));

        _deletion = new TeamDeletion(
            _teams, _host, teamStore, store, store, effective, pending, triggers, principals, paths, git);
    }

    private string AlphaRoot => TeamPaths.ContainerOf(_dataRoot, "Alpha");

    [Fact]
    public async Task A_team_root_carrying_the_marker_is_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAlphaAsync(ct);
        Assert.True(File.Exists(TeamPaths.MarkerIn(AlphaRoot)));

        var deleted = await _deletion.DeleteAsync("Alpha", ct: ct);

        Assert.NotNull(deleted);
        Assert.Empty(deleted.Failures);
        Assert.False(Directory.Exists(AlphaRoot));
    }

    [Fact]
    public async Task A_team_root_without_the_marker_is_left_and_named()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAlphaAsync(ct);
        File.Delete(TeamPaths.MarkerIn(AlphaRoot));
        var precious = Path.Combine(AlphaRoot, "precious.txt");
        await File.WriteAllTextAsync(precious, "somebody's only copy", ct);

        var deleted = await _deletion.DeleteAsync("Alpha", ct: ct);

        Assert.NotNull(deleted);
        Assert.True(File.Exists(precious));
        Assert.Contains(deleted.Failures, f => f.Contains(TeamPaths.MarkerFileName, StringComparison.Ordinal));
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
