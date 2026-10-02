using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// A plugin is told, on stdin as <c>siteFiles</c>, the files folder of each of its OWN team's sites,
/// so it writes a file there and stores the relative path in the site's data. Through the real member
/// runtime, the real plugin runner and the real site store: a /bin/sh fixture copies its stdin to a
/// temp path outside the data root.
/// </summary>
public sealed class PluginSiteFilesTests : IAsyncLifetime
{
    private const string Secret = "s3cr3tvalue";
    private const string Person = "person@example.test";

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-plugin-files-").FullName;

    // OUTSIDE the data root, so the plugin's copy of its stdin is not among what the run wrote.
    private readonly string _outside = Directory.CreateTempSubdirectory("plugin-files-stdin-").FullName;
    private readonly List<IDisposable> _registrations = [];
    private readonly List<ContainerTestBed> _beds = [];

    private SqliteSiteStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StdinCopy => Path.Combine(_outside, "request.json");

    public async ValueTask InitializeAsync()
    {
        var database = Path.Combine(_dataRoot, "harness.db");
        await new SchemaMigrator(database).ApplyAsync(AuthSchema.Steps, ct: Ct);
        _store = new SqliteSiteStore(database);

        // alpha has `board` and `ledger`; beta has `board` and `other`.
        foreach (var (team, site) in new[] { ("alpha", "ledger"), ("alpha", "board"), ("beta", "board"), ("beta", "other") })
        {
            Assert.True(await _store.CreateAsync(
                new SiteRow(team, site, null, 1, DateTimeOffset.UtcNow, Person),
                new TriggerAudit(null, null, "site.created", $"{team}/{site}", site, null), Ct));
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var bed in _beds) await bed.DisposeAsync();
        foreach (var registration in _registrations) registration.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var directory in new[] { _dataRoot, _outside })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class Secrets(Dictionary<string, string> values) : ISecretStore
    {
        public string? TryGet(string logicalKey) => values.GetValueOrDefault(logicalKey);
    }

    private sealed class Settings(PluginMemberSettings settings) : IPluginMemberSettings
    {
        public Task<PluginMemberSettings> ForAsync(ContainerId member, CancellationToken ct = default) => Task.FromResult(settings);
    }

    private static string[] Teams => ["alpha", "beta", "gamma"];

    private SiteService Service(ContainerTestBed bed) => new(
        _store,
        team => Teams.FirstOrDefault(t => string.Equals(t, team, StringComparison.OrdinalIgnoreCase)),
        new TeamPaths(_dataRoot),
        bed.Store);

    /// <summary>One run of the fixture plugin as <paramref name="member"/>, which copies its stdin
    /// outside the data root and then runs <paramref name="then"/>.</summary>
    private async Task<ContainerTestBed> RunAsync(
        ContainerId member, string then = """echo '{"t":"result","ok":true,"output":"done"}'""", bool serveSites = true,
        string? reads = null)
    {
        PluginInstall.Write(_dataRoot, "fixture", script: $"cat > '{StdinCopy}'\n{then}", manifest: PluginInstall.Manifest("fixture", edit: m =>
        {
            m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}""");
            if (reads is not null) m["reads"] = JsonNode.Parse(reads);
        }));

        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        catalog.Rescan();
        _registrations.Add(EventCatalog.Register(catalog));

        var bed = new ContainerTestBed();
        _beds.Add(bed);
        var heartbeat = new RunHeartbeat();

        var plugins = new PluginMemberRunner(
            catalog, new MemberReports(bed.Host, bed.Store, heartbeat), heartbeat, null,
            new Settings(new PluginMemberSettings(
                new Dictionary<string, JsonElement>(), new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" })),
            new Secrets(new() { ["FIXTURE_TOKEN"] = Secret }),
            sites: serveSites ? Service(bed) : null);

        await bed.Host.AddAsync(
            ContainerTestBed.Definition(member) with { Agent = "plugin:fixture", WorkingDirectory = _dataRoot },
            new MemberRunnerRouter(bed.Runner, plugins), Ct);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(member), JsonSerializer.Serialize(new { instruction = "sync" }), $"{member.Team}/manager"), Ct);

        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.Store.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], 10, Ct)).Count > 0, attempts: 400));

        return bed;
    }

    private JsonNode Request() => JsonNode.Parse(File.ReadAllText(StdinCopy))!;

    private string FolderOf(string team, string site) => Path.Combine(_dataRoot, "documents", team, "sites", site, "files");

    [Fact]
    public async Task A_plugin_is_told_each_of_its_teams_sites_files_folder()
    {
        await RunAsync(new ContainerId("alpha", "plug"));

        var told = Request()["siteFiles"]!.AsArray();

        Assert.Equal(
            [("board", FolderOf("alpha", "board")), ("ledger", FolderOf("alpha", "ledger"))],
            told.Select(e => ((string)e!["site"]!, (string)e["folder"]!)));
        Assert.All(told, e =>
        {
            Assert.True(Path.IsPathFullyQualified((string)e!["folder"]!));
            Assert.True(Directory.Exists((string)e["folder"]!));
        });

        // Another team's sites, and its documents, appear nowhere in the request.
        var stdin = File.ReadAllText(StdinCopy);
        Assert.DoesNotContain("\"other\"", stdin, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.Combine(_dataRoot, "documents", "beta"), stdin, StringComparison.Ordinal);
        Assert.False(Directory.Exists(FolderOf("beta", "other")));
    }

    [Fact]
    public async Task A_plugin_that_reads_site_data_is_told_its_sites_files_folders_too()
    {
        // The request is measured, then built again with the documents read: both carry the folders.
        var bed = await RunAsync(new ContainerId("alpha", "plug"), reads: """[{"site":"board","collection":"items"}]""");

        Assert.Single(await bed.OfTypeAsync(MessageTypes.Completed));
        Assert.Equal(["board", "ledger"], Request()["siteFiles"]!.AsArray().Select(e => (string)e!["site"]!));
        Assert.Equal("board", (string?)Request()["sites"]!.AsArray().Single()!["site"]);
    }

    [Fact]
    public async Task A_plugin_of_a_team_without_sites_gets_an_empty_list()
    {
        await RunAsync(new ContainerId("gamma", "plug"));
        Assert.Empty(Request()["siteFiles"]!.AsArray());

        await RunAsync(new ContainerId("alpha", "plug"), serveSites: false);
        Assert.Empty(Request()["siteFiles"]!.AsArray());
    }

    [Fact]
    public async Task The_files_block_holds_paths_only_and_no_secret()
    {
        await RunAsync(new ContainerId("alpha", "plug"));

        var request = Request();
        Assert.Equal(Secret, (string?)request["secrets"]!["token"]);

        foreach (var entry in request["siteFiles"]!.AsArray())
        {
            Assert.Equal(["folder", "site"], entry!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(Secret, entry.ToJsonString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_plugins_stored_path_is_redacted_like_any_record()
    {
        await RunAsync(new ContainerId("alpha", "plug"), """
            folder=$(grep -o '"folder":"[^"]*"' '__STDIN__' | head -1 | cut -d'"' -f4)
            mkdir -p "$folder/out" && printf 'made' > "$folder/out/a.txt"
            echo '{"t":"site.put","site":"board","collection":"items","id":"i1","doc":{"file":"out/a.txt","other":"out/s3cr3tvalue.txt"}}'
            echo '{"t":"result","ok":true,"output":"done"}'
            """.Replace("__STDIN__", StdinCopy));

        var stored = Assert.Single(await _store.ListDocumentsAsync("alpha", "board", "items", Ct));
        Assert.Equal("out/a.txt", (string?)JsonNode.Parse(stored.Json)!["file"]);
        Assert.Equal("out/[redacted].txt", (string?)JsonNode.Parse(stored.Json)!["other"]);
        Assert.DoesNotContain(Secret, stored.Json, StringComparison.Ordinal);

        // The file landed where the plugin was told: the site's files folder.
        Assert.Equal("made", File.ReadAllText(Path.Combine(FolderOf("alpha", "board"), "out", "a.txt")));
    }
}
