namespace Harness.Host;

/// <summary>
/// Every Material Symbols Outlined name, as the font's ligature spells it: what a plugin's
/// <c>monitor.icon</c> may be. Read from <c>cli/internal/plugin/material-symbols.txt</c>, embedded
/// into this assembly from the same file the operator CLI embeds, and written only by
/// <c>web/scripts/material-symbol-names.mjs</c> from the font package the web app serves.
/// <c>MaterialSymbolNamesTests</c> pins it to that package.
/// </summary>
public static class MaterialSymbols
{
    public const string ResourceName = "Harness.Host.material-symbols.txt";

    /// <summary>The names, sorted as the file holds them.</summary>
    public static IReadOnlyList<string> Names { get; } = Load();

    private static readonly HashSet<string> Set = new(Names, StringComparer.Ordinal);

    public static bool IsName(string name) => Set.Contains(name);

    /// <summary>The names in a list's text: one per line, a <c>#</c> line a comment.</summary>
    public static IReadOnlyList<string> Parse(string text) =>
        [.. text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))];

    private static IReadOnlyList<string> Load()
    {
        using var stream = typeof(MaterialSymbols).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded in the Host.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
