using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The manifest's own rules: a <c>list</c> setting (strings, default <c>[]</c>, an
/// optional <c>enum</c> limiting each item) and <c>requires</c> (the runtimes the image provides,
/// checked on the Host's PATH when the catalog loads or rescans). The older types' rules are unchanged.
/// </summary>
public sealed class PluginManifestListAndRequiresTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-list-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private static (PluginConfigField? Field, string? Refusal) Field(string json) =>
        PluginConfigField.Parse("allow", JsonDocument.Parse(json).RootElement);

    private static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_list_setting_parses_with_an_empty_default_when_the_manifest_gives_none()
    {
        var (field, refusal) = Field("""{"type":"list","description":"Who may be written to."}""");

        Assert.Null(refusal);
        Assert.Equal("list", field!.Type);
        Assert.Equal("[]", field.Default!.Value.GetRawText());
    }

    [Fact]
    public void A_list_takes_strings_only_and_its_enum_limits_each_item()
    {
        var (plain, _) = Field("""{"type":"list"}""");
        Assert.Null(plain!.Refusal("allow", Value("""["a@example.test","b@example.test"]""")));
        Assert.Null(plain.Refusal("allow", Value("[]")));
        Assert.Equal("`allow` must be a list of strings.", plain.Refusal("allow", Value("""["a",1]""")));
        Assert.Equal("`allow` must be a list of strings.", plain.Refusal("allow", Value("\"a\"")));

        var (limited, _) = Field("""{"type":"list","enum":["read","send"]}""");
        Assert.Null(limited!.Refusal("allow", Value("""["read","send"]""")));
        Assert.Contains("'delete'", limited.Refusal("allow", Value("""["read","delete"]""")));
        Assert.Contains("read, send", limited.Refusal("allow", Value("""["delete"]""")));
    }

    [Fact]
    public void A_list_default_outside_its_enum_is_refused_naming_the_field()
    {
        var (field, refusal) = Field("""{"type":"list","enum":["read"],"default":["send"]}""");

        Assert.Null(field);
        Assert.Contains("`allow`", refusal);
    }

    [Fact]
    public void The_older_types_keep_their_rules_and_an_unknown_type_is_still_refused_naming_the_field()
    {
        var (text, _) = PluginConfigField.Parse("mode", Value("""{"type":"string","enum":["upper","reverse"]}"""));
        Assert.Equal("`mode` must be one of: upper, reverse.", text!.Refusal("mode", Value("\"sideways\"")));
        Assert.Equal("`mode` must be a string.", text.Refusal("mode", Value("""["upper"]""")));

        var (number, _) = PluginConfigField.Parse("n", Value("""{"type":"number"}"""));
        Assert.Equal("`n` must be a number.", number!.Refusal("n", Value("\"1\"")));
        Assert.Null(number.Default);

        var (none, refusal) = PluginConfigField.Parse("shape", Value("""{"type":"object"}"""));
        Assert.Null(none);
        Assert.Contains("`config.shape.type`", refusal);
    }

    [Fact]
    public void A_manifest_with_a_list_setting_parses()
    {
        var (manifest, refusal) = PluginManifest.Parse(PluginInstall.Manifest("lists", edit: m =>
            m["config"] = JsonNode.Parse("""{"allow":{"type":"list"}}""")).ToJsonString());

        Assert.Null(refusal);
        Assert.Equal("list", manifest!.Config["allow"].Type);
    }

    [Fact]
    public void Requires_names_only_runtimes_the_image_provides_and_an_unknown_one_is_refused_naming_it()
    {
        var (good, none) = PluginManifest.Parse(PluginInstall.Manifest(edit: m => m["requires"] = new JsonArray("dotnet", "python3")).ToJsonString());
        Assert.Null(none);
        Assert.Equal(["dotnet", "python3"], good!.Requires);

        var (bad, refusal) = PluginManifest.Parse(PluginInstall.Manifest(edit: m => m["requires"] = new JsonArray("ruby")).ToJsonString());
        Assert.Null(bad);
        // The CLI's pre-check refuses in these same words (cli/internal/plugin).
        Assert.Equal("`requires` names 'ruby', which is not a runtime this Host knows (dotnet, node, python3).", refusal);

        var (notList, shapeRefusal) = PluginManifest.Parse(PluginInstall.Manifest(edit: m => m["requires"] = "dotnet").ToJsonString());
        Assert.Null(notList);
        Assert.Equal("`requires` must be an array of runtime names.", shapeRefusal);
    }

    [Fact]
    public void Reads_are_refused_in_the_words_the_CLI_uses()
    {
        // The CLI's pre-check refuses in these same words (cli/internal/plugin, TestReadsRefusals).
        foreach (var (reads, expected) in new[]
        {
            ("""{"site":"board","collection":"items"}""", "`reads` must be a list of { site, collection }."),
            ("""[{"site":"board"}]""", "`reads[0]` must be an object with `site` and `collection`."),
            ("""[{"site":"board","collection":"items","team":"beta"}]""", "`reads[0]` names a team; a plugin reads only its own team's sites."),
            ("""[{"site":"board","collection":"items"},{"site":"board","collection":"items"}]""", "`reads[1]` repeats board/items."),
        })
        {
            var (manifest, refusal) = PluginManifest.Parse(PluginInstall.Manifest(edit: m => m["reads"] = JsonNode.Parse(reads)).ToJsonString());
            Assert.Null(manifest);
            Assert.Equal(expected, refusal);
        }
    }

    [Fact]
    public void A_nested_config_type_is_refused_in_the_words_the_CLI_uses()
    {
        var (field, refusal) = PluginConfigField.Parse("x", Value("""{"type":"object"}"""));
        Assert.Null(field);
        Assert.Equal("`config.x.type` must be string, number, bool or list - v1 has no nested configuration.", refusal);
    }

    [Fact]
    public void The_catalog_refuses_a_plugin_whose_runtime_is_missing_naming_it()
    {
        PluginInstall.Write(_dataRoot, "needs-python", manifest: PluginInstall.Manifest("needs-python", edit: m =>
            m["requires"] = new JsonArray("dotnet", "python3")));

        var scan = PluginCatalog.Scan(PluginInstall.PluginsRoot(_dataRoot), runtime => runtime != "python3");

        Assert.Empty(scan.Plugins);
        var refused = Assert.Single(scan.Refused);
        Assert.Equal("it needs python3, which is not installed on this instance.", refused.Reason);
    }

    [Fact]
    public void The_catalog_refuses_an_unknown_runtime_naming_it()
    {
        PluginInstall.Write(_dataRoot, "needs-ruby", manifest: PluginInstall.Manifest("needs-ruby", edit: m =>
            m["requires"] = new JsonArray("ruby")));

        var refused = Assert.Single(PluginCatalog.Scan(PluginInstall.PluginsRoot(_dataRoot), _ => true).Refused);

        Assert.Contains("'ruby'", refused.Reason);
    }

    [Fact]
    public void The_catalog_accepts_a_plugin_whose_runtimes_are_present()
    {
        PluginInstall.Write(_dataRoot, "needs-both", manifest: PluginInstall.Manifest("needs-both", edit: m =>
            m["requires"] = new JsonArray("dotnet", "node")));

        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot), runtime => runtime is "dotnet" or "node");
        catalog.Rescan();

        Assert.Empty(catalog.Refused);
        Assert.Equal(["dotnet", "node"], catalog.For("needs-both")!.Manifest.Requires);
    }

    [Fact]
    public void Requires_is_checked_on_the_real_PATH_by_default()
    {
        // `sh` is not a runtime a manifest may name; `dotnet` runs this very test.
        PluginInstall.Write(_dataRoot, "needs-dotnet", manifest: PluginInstall.Manifest("needs-dotnet", edit: m =>
            m["requires"] = new JsonArray("dotnet")));

        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        catalog.Rescan();

        Assert.True(catalog.RuntimeFound("dotnet") == (Harness.Pty.PathSearch.Find("dotnet") is not null));
        Assert.Equal(catalog.RuntimeFound("dotnet"), catalog.For("needs-dotnet") is not null);
    }
}
