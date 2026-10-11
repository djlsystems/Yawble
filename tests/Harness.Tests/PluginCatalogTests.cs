using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Host;
using Harness.Tests.Host;

namespace Harness.Tests;

/// <summary>Manifest v1 and the catalog that reads it. See docs/plugins.md.</summary>
public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-plugins-").FullName;

    public void Dispose() => Directory.Delete(_dataRoot, recursive: true);

    private PluginScan Scan() => PluginCatalog.Scan(PluginInstall.PluginsRoot(_dataRoot));

    private string RefusalFor(JsonObject manifest)
    {
        PluginInstall.Write(_dataRoot, manifest: manifest);
        var refused = Assert.Single(Scan().Refused);
        return refused.Reason;
    }

    [Fact]
    public void A_valid_manifest_is_installed_with_its_executable_resolved()
    {
        var directory = PluginInstall.Write(_dataRoot);

        var plugin = Assert.Single(Scan().Plugins);

        Assert.Equal("sample-echo", plugin.Manifest.Id);
        Assert.Equal(PluginManifest.DefaultTimeoutSeconds, plugin.Manifest.TimeoutSeconds);
        Assert.Equal(Path.Combine(directory, "bin", "run"), plugin.Executable);
    }

    [Theory]
    [InlineData("schemaVersion", "`schemaVersion` is required")]
    [InlineData("id", "`id` is required")]
    [InlineData("name", "`name` is required")]
    [InlineData("description", "`description` is required")]
    [InlineData("version", "`version` is required")]
    [InlineData("protocol", "`protocol` is required")]
    [InlineData("executable", "`executable.path` is required")]
    public void Each_required_field_is_refused_by_name_when_missing(string field, string expected)
    {
        Assert.Contains(expected, RefusalFor(PluginInstall.Manifest(edit: m => m.Remove(field))));
    }

    [Fact]
    public void A_plugin_whose_reads_are_malformed_is_listed_as_refused_on_rescan()
    {
        Assert.Equal("`reads[0]` names a team; a plugin reads only its own team's sites.",
            RefusalFor(PluginInstall.Manifest(edit: m => m["reads"] = JsonNode.Parse("""[{"site":"board","collection":"items","team":"beta"}]"""))));
    }

    [Fact]
    public void A_plugin_with_well_formed_reads_is_loaded_with_them()
    {
        PluginInstall.Write(_dataRoot, manifest: PluginInstall.Manifest(edit: m =>
            m["reads"] = JsonNode.Parse("""[{"site":"board","collection":"items"},{"site":"board","collection":"notes"}]""")));

        var plugin = Assert.Single(Scan().Plugins);
        Assert.Equal([new PluginSiteRead("board", "items"), new PluginSiteRead("board", "notes")], plugin.Manifest.Reads);
    }

    [Fact]
    public void A_later_schema_version_is_refused_rather_than_misread()
    {
        Assert.Contains("`schemaVersion` 2 is not one this Host reads",
            RefusalFor(PluginInstall.Manifest(edit: m => m["schemaVersion"] = 2)));
    }

    [Fact]
    public void An_unknown_protocol_is_refused()
    {
        Assert.Contains("is not one this Host speaks",
            RefusalFor(PluginInstall.Manifest(edit: m => m["protocol"] = "other/9")));
    }

    [Fact]
    public void An_id_that_disagrees_with_its_directory_is_refused()
    {
        PluginInstall.Write(_dataRoot, id: "sample-echo", manifest: PluginInstall.Manifest(id: "something-else"));

        Assert.Contains("its directory is 'sample-echo'", Assert.Single(Scan().Refused).Reason);
    }

    [Fact]
    public void A_bad_id_is_refused()
    {
        Assert.Contains("must be lowercase letters",
            RefusalFor(PluginInstall.Manifest(edit: m => m["id"] = "Sample_Echo")));
    }

    [Fact]
    public void An_executable_path_escaping_the_directory_is_refused()
    {
        Assert.Contains("must be a relative path inside",
            RefusalFor(PluginInstall.Manifest(edit: m => m["executable"] = new JsonObject { ["path"] = "../../bin/sh" })));
        Assert.Contains("must be a relative path inside",
            RefusalFor(PluginInstall.Manifest(edit: m => m["executable"] = new JsonObject { ["path"] = "/bin/sh" })));
    }

    [Fact]
    public void A_symlink_escaping_the_directory_is_refused()
    {
        var directory = PluginInstall.Write(_dataRoot, manifest: PluginInstall.Manifest(
            edit: m => m["executable"] = new JsonObject { ["path"] = "bin/escape" }));
        File.CreateSymbolicLink(Path.Combine(directory, "bin", "escape"), "/bin/sh");

        Assert.Contains("resolves outside", Assert.Single(Scan().Refused).Reason);
    }

    [Fact]
    public void An_executable_that_is_not_executable_is_refused()
    {
        var directory = PluginInstall.Write(_dataRoot);
        File.SetUnixFileMode(Path.Combine(directory, "bin", "run"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.Contains("is not executable", Assert.Single(Scan().Refused).Reason);
    }

    [Fact]
    public void A_secret_with_a_value_is_refused()
    {
        Assert.Contains("carries a value",
            RefusalFor(PluginInstall.Manifest(edit: m => m["secrets"] = new JsonObject
            {
                ["token"] = new JsonObject { ["value"] = "hunter2" },
            })));
    }

    [Fact]
    public void Unknown_keys_are_ignored_and_reserved_ones_are_kept()
    {
        PluginInstall.Write(_dataRoot, manifest: PluginInstall.Manifest(edit: m =>
        {
            m["fromTheFuture"] = true;
            m["signature"] = "abc";
            m["platforms"] = new JsonObject { [PluginManifest.ThisPlatform] = "bin/run" };
        }));

        var plugin = Assert.Single(Scan().Plugins);

        Assert.Equal(["fromTheFuture"], plugin.Manifest.Ignored);
        Assert.True(plugin.Manifest.Reserved.ContainsKey("signature"));
        Assert.Equal("bin/run", plugin.Manifest.ExecutableForThisPlatform);
    }

    [Fact]
    public void Config_fields_are_typed_and_their_defaults_checked()
    {
        Assert.Contains("must be one of: upper, reverse",
            RefusalFor(PluginInstall.Manifest(edit: m => m["config"] = JsonNode.Parse(
                """{"mode":{"type":"string","enum":["upper","reverse"],"default":"sideways"}}"""))));
        Assert.Contains("v1 has no nested configuration",
            RefusalFor(PluginInstall.Manifest(edit: m => m["config"] = JsonNode.Parse("""{"nested":{"type":"object"}}"""))));
    }

    [Fact]
    public void Several_versions_need_an_active_file_and_it_picks_one()
    {
        PluginInstall.Write(_dataRoot, version: "0.1.0", active: false);
        PluginInstall.Write(_dataRoot, version: "0.2.0", active: false);

        Assert.Contains("no `active` file", Assert.Single(Scan().Refused).Reason);

        File.WriteAllText(Path.Combine(PluginInstall.PluginsRoot(_dataRoot), "sample-echo", "active"), "0.2.0");
        Assert.Equal("0.2.0", Assert.Single(Scan().Plugins).Manifest.Version);
    }

    [Fact]
    public void A_rescan_registers_a_plugin_installed_after_start()
    {
        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        catalog.Rescan();
        Assert.Null(catalog.For("sample-echo"));
        Assert.Contains("is not installed", catalog.RefusalFor("sample-echo"));

        PluginInstall.Write(_dataRoot);
        catalog.Rescan();

        Assert.NotNull(catalog.For("sample-echo"));
        Assert.Null(catalog.RefusalFor("sample-echo"));
    }
}

public sealed class PluginEndpointTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_person_rescans_and_the_catalog_lists_the_new_plugin_without_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync();

        PluginInstall.Write(host.DataRoot, id: "listed-echo", manifest: PluginInstall.Manifest("listed-echo", edit: m =>
            m["secrets"] = JsonNode.Parse("""{"token":{"description":"A demo credential.","required":false}}""")));

        var rescanned = await person.PostAsync("/api/plugins/rescan", null, ct);
        Assert.Equal(HttpStatusCode.OK, rescanned.StatusCode);

        using var listed = JsonDocument.Parse(await person.GetStringAsync("/api/plugins", ct));
        var plugin = listed.RootElement.GetProperty("plugins").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "listed-echo");

        Assert.Equal("plugin:listed-echo", plugin.GetProperty("reference").GetString());
        Assert.True(plugin.GetProperty("secrets").TryGetProperty("token", out _));
        Assert.DoesNotContain(host.DataRoot, listed.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_cannot_rescan()
    {
        var member = host.Container(host.AlphaContainerKey);

        var refused = await member.PostAsync("/api/plugins/rescan", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
