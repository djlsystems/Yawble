using System.Text.Json.Nodes;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A manifest's <c>replaces</c>: the plugin ids it succeeds, said only by the package's own manifest.
/// <see cref="PluginManifest"/> accepts a list of plugin ids (one not installed anywhere too), drops a
/// duplicate, and refuses anything else - its own id, a malformed id, more than
/// <see cref="PluginManifest.MaxReplaces"/> - naming the field, in the sentences the operator CLI uses
/// (cli/internal/plugin/manifest_test.go reads the same cases). A manifest without it replaces nothing.
/// </summary>
public sealed class PluginReplacesManifestTests
{
    /// <summary>The cases the CLI's tests read too: tests/Fixtures/PluginReplaces/cases.json.</summary>
    private static readonly JsonNode Cases = JsonNode.Parse(File.ReadAllText(
        Path.Combine(SolutionSamples.RepoRoot(), "tests", "Fixtures", "PluginReplaces", "cases.json")))!;

    private static string Id => Cases["id"]!.GetValue<string>();

    public static TheoryData<string, string> Accepted() =>
    [
        .. Cases["accepted"]!.AsArray().Select(c => (
            c!["replaces"]?.ToJsonString() ?? "null",
            string.Join(",", c["ids"]!.AsArray().Select(i => i!.GetValue<string>())))),
    ];

    public static TheoryData<string, string> Refused() =>
        [.. Cases["refused"]!.AsArray().Select(c => (c!["replaces"]!.ToJsonString(), c["says"]!.GetValue<string>()))];

    private static (PluginManifest? Manifest, string? Refusal) ParseList(string? list) =>
        PluginManifest.Parse(PluginInstall.Manifest(Id, edit: m =>
        {
            if (list is not null) m["replaces"] = JsonNode.Parse(list);
        }).ToJsonString());

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Each_shared_list_is_accepted_as_its_ids(string list, string ids)
    {
        var (manifest, refusal) = ParseList(list);

        Assert.Null(refusal);
        Assert.Equal(ids, string.Join(",", manifest!.Replaces));
        Assert.DoesNotContain("replaces", manifest.Ignored);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Each_fault_is_refused_naming_the_field(string list, string says)
    {
        var (manifest, refusal) = ParseList(list);

        Assert.Null(manifest);
        Assert.Equal(says, refusal);
    }

    [Fact]
    public void A_manifest_without_the_field_replaces_nothing_and_a_name_alone_says_nothing()
    {
        // NEVER A GUESS FROM THE NAME: "fixture-new" is not said to replace "fixture" or "fixture-old".
        var (manifest, _) = ParseList(null);

        Assert.Empty(manifest!.Replaces);
    }
}
