using System.Text.Json.Nodes;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// CON 7's manifest words: a secret field's <c>movedTo</c> names an API-key connection slot the same
/// manifest declares, and a slot's <c>multiple</c> is true or false. <see cref="PluginManifest"/>
/// accepts each valid pair and refuses each fault naming the field, in the sentences the operator
/// CLI uses (cli/internal/plugin/manifest_test.go reads the same cases).
/// </summary>
public sealed class PluginConnectionMovesManifestTests
{
    /// <summary>The cases the CLI's tests read too: tests/Fixtures/PluginConnectionMoves/cases.json.</summary>
    private static readonly JsonNode Cases = JsonNode.Parse(File.ReadAllText(
        Path.Combine(SolutionSamples.RepoRoot(), "tests", "Fixtures", "PluginConnectionMoves", "cases.json")))!;

    public static TheoryData<int> Accepted() => [.. Enumerable.Range(0, Cases["accepted"]!.AsArray().Count)];

    public static TheoryData<int> Refused() => [.. Enumerable.Range(0, Cases["refused"]!.AsArray().Count)];

    private static (PluginManifest? Manifest, string? Refusal) Parse(JsonNode @case) =>
        PluginManifest.Parse(PluginInstall.Manifest("fx", edit: m =>
        {
            m["secrets"] = @case["secrets"]?.DeepClone();
            m["connections"] = @case["connections"]?.DeepClone();
        }).ToJsonString());

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Each_shared_valid_pair_is_accepted_reading_moved_to_and_multiple(int index)
    {
        var @case = Cases["accepted"]![index]!;

        var (manifest, refusal) = Parse(@case);

        Assert.Null(refusal);
        Assert.Equal(
            @case["moved"]!.AsObject().Select(m => (m.Key, (string)m.Value!)).OrderBy(m => m.Key),
            manifest!.Secrets.Where(s => s.Value.MovedTo is not null).Select(s => (s.Key, s.Value.MovedTo!)).OrderBy(m => m.Key));
        Assert.Equal(
            @case["multiple"]!.AsArray().Select(m => (string)m!).Order(),
            manifest.Connections.Where(c => c.Value.Multiple).Select(c => c.Key).Order());
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

    [Theory]
    [InlineData("keys", "keys", 1)]
    [InlineData("keys#2", "keys", 2)]
    [InlineData("keys#12", "keys", 12)]
    [InlineData("keys#1", null, 0)]
    [InlineData("keys#0", null, 0)]
    [InlineData("keys#02", null, 0)]
    [InlineData("keys#", null, 0)]
    [InlineData("keys#x", null, 0)]
    [InlineData("keys#1000", null, 0)]
    public void A_binding_key_is_the_slot_then_its_place_from_the_second(string key, string? slot, int index)
    {
        var bound = PluginConnectionSlot.BindingKey(key);

        Assert.Equal(slot is null ? null : (slot, index), bound);
    }
}
