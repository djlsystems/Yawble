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
/// A plugin's <c>site.put</c> and <c>site.delete</c> records, through the real member runtime and
/// the real site store: a /bin/sh fixture prints the records. A plugin reaches its own team's sites
/// only, and what it writes is redacted like every other record.
/// </summary>
public sealed class PluginSiteRecordsTests : IAsyncLifetime
{
    private static readonly ContainerId Plug = new("alpha", "plug");

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-plugin-site-").FullName;
    private readonly List<IDisposable> _registrations = [];

    private SqliteSiteStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var database = Path.Combine(_dataRoot, "harness.db");
        await new SchemaMigrator(database).ApplyAsync(AuthSchema.Steps, ct: Ct);
        _store = new SqliteSiteStore(database);

        foreach (var team in new[] { "alpha", "beta" })
        {
            Assert.True(await _store.CreateAsync(
                new SiteRow(team, "board", null, 1, DateTimeOffset.UtcNow, "person@example.test"),
                new TriggerAudit(null, null, "site.created", $"{team}/board", "board", null), Ct));
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var registration in _registrations) registration.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private sealed class Secrets(Dictionary<string, string> values) : ISecretStore
    {
        public string? TryGet(string logicalKey) => values.GetValueOrDefault(logicalKey);
    }

    private sealed class Settings(PluginMemberSettings settings) : IPluginMemberSettings
    {
        public Task<PluginMemberSettings> ForAsync(ContainerId member, CancellationToken ct = default) => Task.FromResult(settings);
    }

    private async Task<ContainerTestBed> RunAsync(string script)
    {
        PluginInstall.Write(_dataRoot, "fixture", script: script, manifest: PluginInstall.Manifest("fixture",
            edit: m => m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}""")));

        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        catalog.Rescan();
        _registrations.Add(EventCatalog.Register(catalog));

        var bed = new ContainerTestBed();
        var heartbeat = new RunHeartbeat();
        var sites = new SiteService(
            _store,
            team => new[] { "alpha", "beta" }.FirstOrDefault(t => string.Equals(t, team, StringComparison.OrdinalIgnoreCase)),
            new TeamPaths(_dataRoot),
            bed.Store);

        var plugins = new PluginMemberRunner(
            catalog, new MemberReports(bed.Host, bed.Store, heartbeat), heartbeat, null,
            new Settings(new PluginMemberSettings(
                new Dictionary<string, JsonElement>(), new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" })),
            new Secrets(new() { ["FIXTURE_TOKEN"] = "s3cr3tvalue" }),
            sites: sites);

        await bed.Host.AddAsync(
            ContainerTestBed.Definition(Plug) with { Agent = "plugin:fixture", WorkingDirectory = _dataRoot },
            new MemberRunnerRouter(bed.Runner, plugins), Ct);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Plug), JsonSerializer.Serialize(new { instruction = "sync" }), "alpha/manager"), Ct);

        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.Store.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], 10, Ct)).Count > 0, attempts: 400));

        return bed;
    }

    private static string Status(Message row) =>
        JsonDocument.Parse(row.Payload).RootElement.GetProperty("status").GetString()!;

    [Fact]
    public async Task A_plugin_puts_and_deletes_on_its_own_teams_site_only_and_its_documents_are_redacted()
    {
        await using var bed = await RunAsync("""
            cat >/dev/null
            echo '{"t":"site.put","site":"board","collection":"jobs","id":"j1","doc":{"title":"Engineer","note":"token s3cr3tvalue"}}'
            echo '{"t":"site.put","site":"board","collection":"jobs","id":"j2","doc":{"title":"Gone soon"}}'
            echo '{"t":"site.delete","site":"board","collection":"jobs","id":"j2"}'
            echo '{"t":"site.put","team":"beta","site":"board","collection":"jobs","id":"x","doc":{"title":"Not mine"}}'
            echo '{"t":"result","ok":true,"output":"synced"}'
            """);

        var own = Assert.Single(await _store.ListDocumentsAsync("alpha", "board", "jobs", Ct));
        Assert.Equal("j1", own.Id);
        Assert.Equal("alpha/plug", own.UpdatedBy);
        Assert.Equal("Engineer", (string?)JsonNode.Parse(own.Json)!["title"]);
        Assert.Equal("token [redacted]", (string?)JsonNode.Parse(own.Json)!["note"]);
        Assert.DoesNotContain("s3cr3tvalue", own.Json, StringComparison.Ordinal);

        // Another team's site of the same name is untouched, and the refusal is one warning row.
        Assert.Empty(await _store.ListDocumentsAsync("beta", "board", "jobs", Ct));
        var warning = Assert.Single(await bed.OfTypeAsync(MessageTypes.Progress));
        Assert.Contains("A `site.put` record was dropped: a plugin writes only its own team's sites.", Status(warning), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plugin_is_refused_a_site_its_team_does_not_have()
    {
        await using var bed = await RunAsync("""
            cat >/dev/null
            echo '{"t":"site.put","site":"nosuch","collection":"jobs","id":"j1","doc":{}}'
            echo '{"t":"result","ok":true,"output":"done"}'
            """);

        var warning = Assert.Single(await bed.OfTypeAsync(MessageTypes.Progress));
        Assert.Contains("A `site.put` record was dropped: No such site.", Status(warning), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plugins_document_over_the_limit_is_refused_with_a_sentence()
    {
        var big = new string('x', SiteRules.MaxDocumentBytes);

        await using var bed = await RunAsync($$"""
            cat >/dev/null
            echo '{"t":"site.put","site":"board","collection":"jobs","id":"big","doc":"{{big}}"}'
            echo '{"t":"result","ok":true,"output":"done"}'
            """);

        Assert.Empty(await _store.ListDocumentsAsync("alpha", "board", "jobs", Ct));
        var warning = Assert.Single(await bed.OfTypeAsync(MessageTypes.Progress));
        Assert.Contains("over the limit of 65536 bytes (64 KB) for one document", Status(warning), StringComparison.Ordinal);
    }
}
