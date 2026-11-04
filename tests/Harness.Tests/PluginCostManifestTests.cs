using System.Text.Json.Nodes;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A manifest's <c>cost</c>: the cost type its runs are priced by. <see cref="PluginManifest"/> accepts
/// <c>{ "type": "duration" }</c>, an empty object, null or no field at all - each is duration, a plugin's
/// time at its member's own hourly rate - and refuses every other value, naming the field in the
/// sentences the operator CLI uses (cli/internal/plugin/manifest_test.go reads the same cases). Nothing
/// is priced from the field.
/// </summary>
public sealed class PluginCostManifestTests
{
    /// <summary>The cases the CLI's tests read too: tests/Fixtures/PluginCost/cases.json.</summary>
    private static readonly JsonNode Cases = JsonNode.Parse(File.ReadAllText(
        Path.Combine(SolutionSamples.RepoRoot(), "tests", "Fixtures", "PluginCost", "cases.json")))!;

    public static TheoryData<string, string> Accepted() =>
    [
        .. Cases["accepted"]!.AsArray().Select(c => (
            c!["cost"]?.ToJsonString() ?? "absent",
            string.Join(",", c["ignored"]!.AsArray().Select(i => i!.GetValue<string>())))),
    ];

    public static TheoryData<string, string> Refused() =>
        [.. Cases["refused"]!.AsArray().Select(c => (c!["cost"]?.ToJsonString() ?? "null", c["says"]!.GetValue<string>()))];

    private static (PluginManifest? Manifest, string? Refusal) ParseCost(string cost) =>
        PluginManifest.Parse(PluginInstall.Manifest("fixture-cost", edit: m =>
        {
            if (cost != "absent") m["cost"] = JsonNode.Parse(cost);
        }).ToJsonString());

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Each_shared_cost_is_accepted_as_duration(string cost, string ignored)
    {
        var (manifest, refusal) = ParseCost(cost);

        Assert.Null(refusal);
        Assert.Equal(PluginManifest.DurationCost, manifest!.CostType);
        Assert.Equal(ignored, string.Join(",", manifest.Ignored.Where(k => k.StartsWith("cost", StringComparison.Ordinal))));
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Each_other_cost_is_refused_naming_the_field(string cost, string says)
    {
        var (manifest, refusal) = ParseCost(cost);

        Assert.Null(manifest);
        Assert.Equal(says, refusal);
    }

    [Fact]
    public void A_manifest_with_no_cost_is_duration_and_an_explicit_null_is_too()
    {
        Assert.Equal(PluginManifest.DurationCost, ParseCost("absent").Manifest!.CostType);
        Assert.Equal(PluginManifest.DurationCost, ParseCost("null").Manifest!.CostType);
    }

    [Fact]
    public void Cost_is_a_known_key_so_it_is_never_named_as_ignored()
    {
        var (manifest, _) = ParseCost("""{ "type": "duration" }""");

        Assert.DoesNotContain("cost", manifest!.Ignored);
    }

    [Fact]
    public void Every_shipped_plugin_manifest_still_parses_with_the_field_known()
    {
        // REFUSING OTHER TYPES BREAKS NOTHING SHIPPED: every built-in plugin's plugin.json still loads.
        var manifests = Directory.GetFiles(Path.Combine(SolutionSamples.RepoRoot(), "plugins"), PluginManifest.FileName, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(manifests);
        foreach (var path in manifests)
        {
            var (manifest, refusal) = PluginManifest.Parse(File.ReadAllText(path));
            Assert.True(refusal is null, $"{path}: {refusal}");
            Assert.Equal(PluginManifest.DurationCost, manifest!.CostType);
        }
    }
}
