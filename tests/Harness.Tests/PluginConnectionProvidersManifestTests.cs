using System.Text.Json.Nodes;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A connection slot's <c>providers</c>: every provider word the Host knows - <c>website</c> beside
/// <c>login</c> and <c>apikey</c> - is accepted, and an unknown one refused naming the field, in the
/// sentences the operator CLI uses (cli/internal/plugin/manifest_test.go reads the same cases). Scopes
/// do not apply to a website, as to the other credentials; <c>movedTo</c> still needs exactly
/// <c>["apikey"]</c>.
/// </summary>
public sealed class PluginConnectionProvidersManifestTests
{
    /// <summary>The cases the CLI's tests read too: tests/Fixtures/PluginConnectionProviders/cases.json.</summary>
    private static readonly JsonNode Cases = JsonNode.Parse(File.ReadAllText(
        Path.Combine(SolutionSamples.RepoRoot(), "tests", "Fixtures", "PluginConnectionProviders", "cases.json")))!;

    public static TheoryData<int> Accepted() => [.. Enumerable.Range(0, Cases["accepted"]!.AsArray().Count)];

    public static TheoryData<int> Refused() => [.. Enumerable.Range(0, Cases["refused"]!.AsArray().Count)];

    private static (PluginManifest? Manifest, string? Refusal) Parse(JsonNode @case) =>
        PluginManifest.Parse(PluginInstall.Manifest("fx", edit: m =>
        {
            if (@case["secrets"] is { } secrets) m["secrets"] = secrets.DeepClone();
            m["connections"] = @case["connections"]?.DeepClone();
        }).ToJsonString());

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Each_shared_slot_is_accepted_with_its_providers(int index)
    {
        var @case = Cases["accepted"]![index]!;

        var (manifest, refusal) = Parse(@case);

        Assert.Null(refusal);
        Assert.Equal(
            @case["providers"]!.AsObject().Select(p => $"{p.Key}={string.Join(',', p.Value!.AsArray().Select(v => (string)v!))}").Order(),
            manifest!.Connections.Select(c => $"{c.Key}={string.Join(',', c.Value.Providers)}").Order());
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Each_shared_fault_is_refused_naming_the_field(int index)
    {
        var @case = Cases["refused"]![index]!;

        var (manifest, refusal) = Parse(@case);

        Assert.Null(manifest);
        Assert.Equal((string)@case["says"]!, refusal);
    }

    [Fact]
    public void A_website_is_a_provider_a_slot_may_name_with_no_scopes_said_in_words()
    {
        Assert.Contains(ConnectionKinds.Website, ConnectionProviders.ManifestProviders);
        Assert.True(ConnectionProviders.HasNoScopes(ConnectionKinds.Website));
        Assert.True(ConnectionProviders.Admits(ConnectionKinds.Website, ConnectionKinds.Website));
        Assert.False(ConnectionProviders.Admits(ConnectionKinds.Website, ConnectionKinds.Login));
        Assert.Equal("a website", ConnectionProviders.Spoken(ConnectionKinds.Website));

        // A list of scopes is every OAuth provider's: a website takes none of it.
        var (manifest, _) = Parse(Cases["accepted"]![3]!);
        Assert.Empty(manifest!.Connections["any"].ScopesFor(ConnectionKinds.Website));
        Assert.Equal(["openid"], manifest.Connections["any"].ScopesFor(ConnectionProviders.Google));
    }
}
