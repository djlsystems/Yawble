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

        // The Team picture's reserved site, as the platform makes it for a team that has turned it on.
        Assert.True(await _store.CreateAsync(
            new SiteRow("alpha", TeamPicture.SiteName, null, 1, DateTimeOffset.UtcNow, TeamPicture.PlatformLabel),
            new TriggerAudit(null, null, "site.created", $"alpha/{TeamPicture.SiteName}", TeamPicture.SiteName, null), Ct));
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
    public async Task A_document_named_by_a_services_record_id_is_stored_with_its_id_and_a_random_looking_run_in_it_is_still_redacted()
    {
        // Twilio names a text SM and 32 hex digits; the plugin stores it under that id and keeps the
        // ids it has seen as the keys of a map. Neither is a secret, and neither may be dropped.
        const string sid = "SM3f9c2b7a1e4d8f6a0b5c9e2d7f1a4b8c";
        await using var bed = await RunAsync("""
            cat >/dev/null
            echo '{"t":"site.put","site":"board","collection":"messages","id":"SID","doc":{"sid":"SID","note":"key Qm7vX2pL9tRw4kZs8Hn3Yb6dJ1fA5gC0eT7uI2oP4sW"}}'
            echo '{"t":"site.put","site":"board","collection":"state","id":"poll","doc":{"inboundSeen":{"SID":"2026-10-10T23:42:26Z"}}}'
            echo '{"t":"result","ok":true,"output":"synced"}'
            """.Replace("SID", sid, StringComparison.Ordinal));

        Assert.Empty(await bed.OfTypeAsync(MessageTypes.Progress));

        var message = Assert.Single(await _store.ListDocumentsAsync("alpha", "board", "messages", Ct));
        Assert.Equal(sid, message.Id);
        Assert.Equal(sid, (string?)JsonNode.Parse(message.Json)!["sid"]);
        Assert.Equal("key [redacted]", (string?)JsonNode.Parse(message.Json)!["note"]);

        var state = Assert.Single(await _store.ListDocumentsAsync("alpha", "board", "state", Ct));
        Assert.Equal("2026-10-10T23:42:26Z", (string?)JsonNode.Parse(state.Json)!["inboundSeen"]![sid]);
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

    /// <summary>
    /// An operational team's own figures, written by the plugin that already has them in the one
    /// documented shape (docs/sites.md, <c>tp-ops</c>), are what the page reads, with no agent run.
    /// The plugin cannot write the platform's collections, nor a shape the page cannot draw.
    /// </summary>
    [Fact]
    public async Task A_plugin_writes_the_team_pictures_operational_figures_in_the_documented_shape_and_the_page_reads_them()
    {
        await using var bed = await RunAsync("""
            cat >/dev/null
            echo '{"t":"site.put","site":"team-picture","collection":"tp-ops","id":"today","doc":{"date":"2026-11-04","counts":{"support":3,"inquiry":5,"alert":1,"spam":12},"lastMessage":{"to":"+440000","sentAt":"2026-11-04T09:55:00Z","text":"3 support, 1 alert"},"runs":[{"at":"2026-11-04T09:50:00Z","kind":null},{"at":"2026-11-04T09:55:00Z","kind":"alert"}],"items":[{"at":"2026-11-04T09:55:00Z","kind":"alert","title":"Disk full"}],"notes":"kept as written"}}'
            echo '{"t":"site.put","site":"team-picture","collection":"tp-now","id":"plug","doc":{"member":"plug"}}'
            echo '{"t":"site.put","site":"team-picture","collection":"tp-ops","id":"bad","doc":{"date":"today","counts":{"spam":-1}}}'
            echo '{"t":"site.put","site":"team-picture","collection":"tp-picture","id":"ongoing","doc":{"kind":"operational"}}'
            echo '{"t":"result","ok":true,"output":"triaged"}'
            """);

        var sites = new SiteService(
            _store, team => string.Equals(team, "alpha", StringComparison.OrdinalIgnoreCase) ? "alpha" : null,
            new TeamPaths(_dataRoot), bed.Store);

        // What `_api/data/tp-ops` answers the page.
        var page = await sites.ListDocumentsAsync("alpha", TeamPicture.SiteName, TeamPictureRules.Ops, null, Ct);
        var ops = Assert.Single(page.Value!);
        Assert.Equal("today", ops.Id);
        Assert.Equal("alpha/plug", ops.UpdatedBy);
        var doc = JsonNode.Parse(ops.Json)!;
        Assert.Equal(12, (int)doc["counts"]!["spam"]!);
        Assert.Equal("3 support, 1 alert", (string?)doc["lastMessage"]!["text"]);
        Assert.Equal(2, doc["runs"]!.AsArray().Count);
        Assert.Equal("kept as written", (string?)doc["notes"]);

        // Nor the platform's collections, nor the Manager's picture, nor a wrong shape.
        Assert.Empty(await _store.ListDocumentsAsync("alpha", TeamPicture.SiteName, TeamPictureRules.Now, Ct));
        Assert.Empty(await _store.ListDocumentsAsync("alpha", TeamPicture.SiteName, TeamPictureRules.Picture, Ct));
        var warning = Assert.Single(await bed.OfTypeAsync(MessageTypes.Progress));
        Assert.Contains("A `site.put` record was dropped: `tp-now` is kept by the platform", Status(warning), StringComparison.Ordinal);
    }
}
