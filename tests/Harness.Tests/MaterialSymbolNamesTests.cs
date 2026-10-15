using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE HOST'S MATERIAL SYMBOLS NAMES ARE THE WEB FONT PACKAGE'S. A plugin's <c>monitor.icon</c> is
/// checked against <see cref="MaterialSymbols"/>, embedded from
/// <c>cli/internal/plugin/material-symbols.txt</c>; the Monitor draws it with the font
/// <c>@quasar/extras</c> ships. An upgrade that adds or drops a name fails here until
/// <c>npm run symbols:names</c> (web/) writes the file again.
/// </summary>
public sealed class MaterialSymbolNamesTests
{
    private static readonly string Root = SolutionSamples.RepoRoot();

    private static readonly string NamesFile = Path.Combine(Root, "cli", "internal", "plugin", "material-symbols.txt");

    private static readonly string Extras = Path.Combine(Root, "web", "node_modules", "@quasar", "extras");

    /// <summary>`18_up_rating` is exported as `symOutlined18UpRating`, as web/scripts/icon-font.mjs maps it.</summary>
    private static string ExportName(string name) =>
        "symOutlined" + string.Concat(name.Split('_').Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    [Fact]
    public void The_Host_embeds_the_checked_in_list()
    {
        Assert.Equal(MaterialSymbols.Parse(File.ReadAllText(NamesFile)), MaterialSymbols.Names);
        Assert.True(MaterialSymbols.Names.Count > 3000, $"{MaterialSymbols.Names.Count} names is not the whole font.");
    }

    [Fact]
    public void The_list_is_sorted_unique_lower_case_ligature_names()
    {
        var names = MaterialSymbols.Names;

        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.Matches("^[a-z0-9]+(_[a-z0-9]+)*$", name));
    }

    /// <summary>Runs wherever the repository is, node_modules or not: the lock file moves with an upgrade.</summary>
    [Fact]
    public void The_list_was_generated_from_the_font_package_version_the_web_app_locks()
    {
        using var lockFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "web", "package-lock.json")));
        var locked = lockFile.RootElement.GetProperty("packages").GetProperty("node_modules/@quasar/extras").GetProperty("version").GetString();

        var header = File.ReadLines(NamesFile).First();
        var generated = Regex.Match(header, @"@quasar/extras (\S+):").Groups[1].Value;

        Assert.True(locked == generated,
            $"material-symbols.txt was generated from @quasar/extras {generated}, but web/ locks {locked}. Run `npm run symbols:names` in web/.");
    }

    [Fact]
    public void The_list_is_the_web_font_packages_list()
    {
        var icons = Path.Combine(Extras, "exports", "material-symbols-outlined", "icons.json");
        Assert.SkipUnless(File.Exists(icons), "web/node_modules is not installed; `npm ci` in web/ to compare against the font package.");

        var exported = JsonSerializer.Deserialize<string[]>(File.ReadAllText(icons))!.ToHashSet(StringComparer.Ordinal);
        var ours = MaterialSymbols.Names.Select(ExportName).ToList();

        var missing = exported.Except(ours, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var extra = MaterialSymbols.Names.Where(name => !exported.Contains(ExportName(name))).ToList();

        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"material-symbols.txt is not the font package's list: {missing.Count} exported icon(s) have no name "
            + $"({string.Join(", ", missing.Take(10))}), {extra.Count} name(s) are not in the package "
            + $"({string.Join(", ", extra.Take(10))}). Run `npm run symbols:names` in web/.");

        // One name per icon: no two names stand for the same glyph.
        Assert.Equal(exported.Count, ours.Count);
    }
}
