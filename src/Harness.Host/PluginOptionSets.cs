using System.Text.Json;

namespace Harness.Host;

/// <summary>One choice of a setting: the value stored and the words the settings form shows for it.</summary>
public sealed record PluginOption(string Value, string Label)
{
    /// <summary>Other words the form's filter finds it by, besides its label and value: a language's
    /// other English name ("British English"), a time zone's older names (Asia/Calcutta). Null for none.</summary>
    public string? Keywords { get; init; }
}

/// <summary>
/// THE NAMED OPTION SETS a plugin's <c>config</c> field may take its choices from, so a manifest need
/// not list hundreds of values: <c>"options": "locale"</c> is every language Chrome accepts
/// (<c>en-US</c>, labelled "English (United States)"), <c>"options": "timeZone"</c> UTC and every IANA
/// time zone (<c>Europe/London</c>). Read from <c>PluginOptionSets.json</c>, embedded in this assembly
/// and written only by <c>web/scripts/plugin-option-sets.mjs</c>. <c>PluginOptionSetsTests</c> pins it.
/// A set's values match WITHOUT REGARD TO CASE (<c>en-gb</c> is <c>en-GB</c>), and <c>timeZone</c> also
/// takes the tz database's older names for an entry (<c>Asia/Calcutta</c> is <c>Asia/Kolkata</c>,
/// <c>Etc/UTC</c> and <c>GMT</c> are <c>UTC</c>): see <see cref="Canonical"/>.
/// </summary>
public static class PluginOptionSets
{
    public const string ResourceName = "Harness.Host.PluginOptionSets.json";

    /// <summary>The set names a manifest may give, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> Names { get; } = ["locale", "timeZone"];

    private static readonly (IReadOnlyDictionary<string, IReadOnlyList<PluginOption>> Sets,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Canonicals) Loaded = Load();

    /// <summary>The set called <paramref name="name"/>, or null when there is none by that name.</summary>
    public static IReadOnlyList<PluginOption>? Named(string name) => Loaded.Sets.GetValueOrDefault(name);

    /// <summary>
    /// The entry of the set called <paramref name="name"/> that <paramref name="given"/> stands for: the
    /// entry itself in any case, or an older name the set keeps for it (a <c>timeZone</c> alias). Null
    /// when it stands for none, or there is no such set.
    /// </summary>
    public static string? Canonical(string name, string given) =>
        Loaded.Canonicals.TryGetValue(name, out var canonical) ? canonical.GetValueOrDefault(given) : null;

    private static (IReadOnlyDictionary<string, IReadOnlyList<PluginOption>>, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>) Load()
    {
        using var stream = typeof(PluginOptionSets).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded in the Host.");
        using var document = JsonDocument.Parse(stream);

        var sets = new Dictionary<string, IReadOnlyList<PluginOption>>(StringComparer.Ordinal);
        var canonicals = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var aliases = document.RootElement.TryGetProperty("aliases", out var a) ? a : default;
        foreach (var name in Names)
        {
            IReadOnlyList<PluginOption> set = [.. document.RootElement.GetProperty(name).EnumerateArray()
                .Select(o => new PluginOption(o.GetProperty("value").GetString()!, o.GetProperty("label").GetString()!)
                {
                    Keywords = o.TryGetProperty("keywords", out var k) ? k.GetString() : null,
                })];
            sets[name] = set;

            // AN ENTRY BEFORE ITS ALIASES, so no alias can shadow one; the file's pinned test keeps them apart.
            var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in set) canonical.TryAdd(option.Value, option.Value);
            if (aliases.ValueKind == JsonValueKind.Object && aliases.TryGetProperty(name, out var named))
            {
                foreach (var alias in named.EnumerateObject()) canonical.TryAdd(alias.Name, alias.Value.GetString()!);
            }

            canonicals[name] = canonical;
        }

        return (sets, canonicals);
    }
}
