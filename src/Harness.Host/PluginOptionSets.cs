using System.Text.Json;

namespace Harness.Host;

/// <summary>One choice of a setting: the value stored and the words the settings form shows for it.</summary>
public sealed record PluginOption(string Value, string Label);

/// <summary>
/// THE NAMED OPTION SETS a plugin's <c>config</c> field may take its choices from, so a manifest need
/// not list hundreds of values: <c>"options": "locale"</c> is every language Chrome accepts
/// (<c>en-US</c>, labelled "English (United States)"), <c>"options": "timeZone"</c> UTC and every IANA
/// time zone (<c>Europe/London</c>). Read from <c>PluginOptionSets.json</c>, embedded in this assembly
/// and written only by <c>web/scripts/plugin-option-sets.mjs</c>. <c>PluginOptionSetsTests</c> pins it.
/// </summary>
public static class PluginOptionSets
{
    public const string ResourceName = "Harness.Host.PluginOptionSets.json";

    /// <summary>The set names a manifest may give, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> Names { get; } = ["locale", "timeZone"];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PluginOption>> Sets = Load();

    /// <summary>The set called <paramref name="name"/>, or null when there is none by that name.</summary>
    public static IReadOnlyList<PluginOption>? Named(string name) => Sets.GetValueOrDefault(name);

    private static Dictionary<string, IReadOnlyList<PluginOption>> Load()
    {
        using var stream = typeof(PluginOptionSets).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded in the Host.");
        using var document = JsonDocument.Parse(stream);

        var sets = new Dictionary<string, IReadOnlyList<PluginOption>>(StringComparer.Ordinal);
        foreach (var name in Names)
        {
            sets[name] = [.. document.RootElement.GetProperty(name).EnumerateArray()
                .Select(o => new PluginOption(o.GetProperty("value").GetString()!, o.GetProperty("label").GetString()!))];
        }

        return sets;
    }
}
