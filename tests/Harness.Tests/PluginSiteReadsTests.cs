using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A plugin READS the collections of its own team's sites that its manifest declares in
/// <c>reads</c>: delivered on stdin at run start as <c>sites</c>, newest first, within a per-run
/// budget whose cut is stated, never silent. Through the real member runtime, the real plugin runner
/// and the real site store: a /bin/sh fixture copies its stdin to a temp path outside the data root.
/// </summary>
public sealed class PluginSiteReadsTests
{
    // ---- the manifest ----------------------------------------------------------------------------

    private static string? Refusal(string reads) =>
        PluginManifest.Parse(PluginInstall.Manifest("fixture", edit: m => m["reads"] = JsonNode.Parse(reads)).ToJsonString()).Refusal;

    private static string ThirtyThree() =>
        "[" + string.Join(",", Enumerable.Range(0, 33).Select(i => $$"""{"site":"board","collection":"c{{i}}"}""")) + "]";

    [Theory]
    [InlineData("""{"site":"board","collection":"items"}""", "`reads` must be a list of { site, collection }.")]
    [InlineData("""["board/items"]""", "`reads[0]` must be an object with `site` and `collection`.")]
    [InlineData("""[{"site":"board"}]""", "`reads[0]` must be an object with `site` and `collection`.")]
    [InlineData("""[{"site":"Board","collection":"items"}]""", "`reads[0].site`: \"Board\" is not a valid site name. Use 1-63 lower-case letters, digits and hyphens, starting with a letter or digit.")]
    [InlineData("""[{"site":"board","collection":"items-"}]""", "`reads[0].collection`: \"items-\" is not a valid collection name. Use 1-63 lower-case letters, digits and hyphens, starting with a letter or digit.")]
    [InlineData("""[{"site":"board","collection":"items","team":"beta"}]""", "`reads[0]` names a team; a plugin reads only its own team's sites.")]
    [InlineData("""[{"site":"board","collection":"items"},{"site":"board","collection":"items"}]""", "`reads[1]` repeats board/items.")]
    public void A_reads_declaration_that_is_not_a_site_and_collection_list_is_refused_with_a_sentence(string reads, string expected)
    {
        Assert.Equal(expected, Refusal(reads));
    }

    [Fact]
    public void A_reads_declaration_of_more_than_32_collections_is_refused_with_a_sentence()
    {
        Assert.Equal("`reads` declares 33 collections; a plugin reads at most 32.", Refusal(ThirtyThree()));
        Assert.Null(Refusal("[" + string.Join(",", Enumerable.Range(0, 32).Select(i => $$"""{"site":"board","collection":"c{{i}}"}""")) + "]"));
    }

    [Fact]
    public void A_reads_declaration_with_two_faults_is_refused_for_the_first()
    {
        Assert.Equal(
            "`reads[0].site`: \"Bad\" is not a valid site name. Use 1-63 lower-case letters, digits and hyphens, starting with a letter or digit.",
            Refusal("""[{"site":"Bad","collection":"items"},{"site":"board","collection":"items","team":"beta"}]"""));
    }

    [Fact]
    public void A_manifest_without_reads_or_with_null_reads_reads_nothing()
    {
        var (absent, none) = PluginManifest.Parse(PluginInstall.Manifest("fixture").ToJsonString());
        Assert.Null(none);
        Assert.Empty(absent!.Reads);

        var (nulled, refusal) = PluginManifest.Parse(PluginInstall.Manifest("fixture", edit: m => m["reads"] = null).ToJsonString());
        Assert.Null(refusal);
        Assert.Empty(nulled!.Reads);
        Assert.DoesNotContain("reads", nulled.Ignored);
    }
}
