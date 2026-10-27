using System.Text.Json.Nodes;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A manifest's <c>namedSecrets</c> block: <see cref="PluginManifest"/> accepts a valid one (with
/// <c>max</c> 20 when omitted, at most 50), names a key it does not know as ignored, and refuses
/// anything else malformed naming the field - in the sentences the operator CLI uses
/// (cli/internal/plugin/manifest_test.go reads the same cases). A manifest without one has none.
/// </summary>
public sealed class PluginNamedSecretsManifestTests
{
    /// <summary>The cases the CLI's tests read too: tests/Fixtures/PluginNamedSecrets/cases.json.</summary>
    private static readonly JsonNode Cases = JsonNode.Parse(File.ReadAllText(
        Path.Combine(SolutionSamples.RepoRoot(), "tests", "Fixtures", "PluginNamedSecrets", "cases.json")))!;

    public static TheoryData<string, int, string> Accepted() =>
    [
        .. Cases["accepted"]!.AsArray().Select(c => (
            c!["block"]!.ToJsonString(), c["max"]!.GetValue<int>(),
            string.Join(",", c["ignored"]!.AsArray().Select(i => i!.GetValue<string>())))),
    ];

    public static TheoryData<string, string> Refused() =>
        [.. Cases["refused"]!.AsArray().Select(c => (c!["block"]!.ToJsonString(), c["says"]!.GetValue<string>()))];

    private static (PluginManifest? Manifest, string? Refusal) ParseBlock(string? block) =>
        PluginManifest.Parse(PluginInstall.Manifest("fx", edit: m =>
        {
            if (block is not null) m["namedSecrets"] = JsonNode.Parse(block);
        }).ToJsonString());

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Each_shared_valid_block_is_accepted_naming_what_it_ignores(string block, int max, string ignored)
    {
        var (manifest, refusal) = ParseBlock(block);

        Assert.Null(refusal);
        Assert.Equal(max, manifest!.NamedSecrets!.Max);
        Assert.Equal(ignored, string.Join(",", manifest.Ignored));
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Each_fault_is_refused_naming_the_field(string block, string says)
    {
        var (manifest, refusal) = ParseBlock(block);

        Assert.Null(manifest);
        Assert.Equal(says, refusal);
    }

    [Fact]
    public void The_label_and_description_are_read_and_a_manifest_without_the_block_has_none()
    {
        var (manifest, _) = ParseBlock("""{"label":" App secrets ","description":"How the team refers to it.","max":8}""");
        Assert.Equal(new PluginNamedSecrets("App secrets", "How the team refers to it.", 8), manifest!.NamedSecrets);

        Assert.Null(ParseBlock(null).Manifest!.NamedSecrets);
        Assert.Null(ParseBlock("null").Manifest!.NamedSecrets);
    }

    [Theory]
    [InlineData("ApiKey", true)]
    [InlineData("a", true)]
    [InlineData("Microsoft_Client_Secret2", true)]
    [InlineData("1Key", false)]
    [InlineData("_key", false)]
    [InlineData("api-key", false)]
    [InlineData("api key", false)]
    [InlineData("", false)]
    [InlineData("ApiKey\n", false)]
    public void A_name_is_a_letter_then_letters_digits_and_underscores(string name, bool usable)
    {
        Assert.Equal(usable, PluginNamedSecrets.IsName(name));
        Assert.True(PluginNamedSecrets.IsName("A" + new string('b', 63)));
        Assert.False(PluginNamedSecrets.IsName("A" + new string('b', 64)));
    }
}
