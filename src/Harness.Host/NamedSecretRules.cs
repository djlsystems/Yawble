namespace Harness.Host;

/// <summary>A named secret a member holds: its name, who set it and when. Never the value.</summary>
public sealed record NamedSecretState(string Name, string SetBy, DateTimeOffset SetAt);

/// <summary>
/// WHAT A PERSON MAY NAME A PLUGIN MEMBER'S SECRET, AND HOLD UNDER IT, for a plugin whose manifest
/// declares a <c>namedSecrets</c> block (<see cref="PluginNamedSecrets"/>). A name is a letter, then
/// letters, digits and underscores, at most 64 characters; it is unique per member ignoring case and
/// never one of the manifest's <c>secrets</c> field names; a member holds at most the block's
/// <c>max</c>. A value is held to a masked field's rules (<see cref="PluginMemberSecretStore.ValueRefusal"/>)
/// and is never one of the member's names, which a run hands on stdin unredacted. Every refusal is one
/// sentence naming the name and never repeating the value.
/// </summary>
public static class NamedSecretRules
{
    /// <summary>The longest a refused name is shown: a name is at most 64, and a refusal stays one line.</summary>
    private const int ShownLength = 80;

    /// <summary>
    /// Why <paramref name="changes"/> (a value sets or replaces, null removes) cannot be kept for a member
    /// of <paramref name="manifest"/> holding <paramref name="held"/>, or null.
    /// </summary>
    public static string? ChangesRefusal(
        PluginManifest manifest, IReadOnlyCollection<string> held, IReadOnlyDictionary<string, string?> changes)
    {
        if (manifest.NamedSecrets is not { } block)
        {
            return $"Plugin '{manifest.Id}' declares no named secrets; its secrets are the fields its manifest names.";
        }

        var after = new HashSet<string>(held, StringComparer.OrdinalIgnoreCase);
        var given = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? lastAdded = null;

        foreach (var (name, value) in changes.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (!PluginNamedSecrets.IsName(name))
            {
                return $"`{Shown(name)}` is not a usable secret name: start with a letter, then use letters, digits "
                    + "and underscores, at most 64 characters.";
            }

            if (given.TryGetValue(name, out var twin))
            {
                return $"`{name}` is given twice (also as `{twin}`); a member's secret names are unique ignoring case.";
            }

            given[name] = name;

            if (manifest.Secrets.Keys.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)) is { } field)
            {
                return $"`{name}` is the name of the plugin's secret field `{field}`; give the secret another name.";
            }

            if (value is null)
            {
                after.Remove(name);
                continue;
            }

            if (held.FirstOrDefault(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(h, name, StringComparison.Ordinal)) is { } spelled)
            {
                return $"`{name}`: this member already holds a secret named `{spelled}`, and names are unique ignoring case. "
                    + $"Replace `{spelled}` or give the secret another name.";
            }

            if (PluginMemberSecretStore.ValueRefusal(name, value, masked: true) is { } refused) return refused;

            if (after.Add(name)) lastAdded = name;
        }

        if (after.Count > block.Max && lastAdded is not null)
        {
            return $"`{lastAdded}` would be this member's secret number {after.Count}; plugin '{manifest.Id}' holds at most "
                + $"{block.Max} named secrets per member. Remove one first.";
        }

        // A VALUE THAT IS A NAME would ride on stdin unredacted, beside the names a run is handed.
        foreach (var (name, value) in changes.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (value is not null && (after.Any(n => string.Equals(n, value, StringComparison.Ordinal)) || manifest.Secrets.ContainsKey(value)))
            {
                return $"`{name}`: the value is the name of a secret this member's plugin is told, and names are not secret. Type the real value.";
            }
        }

        return null;
    }

    private static string Shown(string name)
    {
        var line = new string([.. name.Select(c => char.IsControl(c) ? ' ' : c)]);
        return line.Length > ShownLength ? line[..ShownLength] + "…" : line;
    }
}
