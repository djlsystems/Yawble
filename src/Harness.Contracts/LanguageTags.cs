using System.Text.RegularExpressions;

namespace Harness.Contracts;

/// <summary>
/// LANGUAGE TAGS IN THEIR CANONICAL CASE, as a browser and <c>Intl.getCanonicalLocales</c> spell them:
/// the language lower case, a script title case, a region upper case, the rest lower case - so
/// <c>en-gb</c> runs as <c>en-GB</c> and <c>ZH-hant-tw</c> as <c>zh-Hant-TW</c>, never dropped and never
/// sent as typed. A tag that is not well formed is null.
/// </summary>
public static partial class LanguageTags
{
    /// <summary>The longest tag kept.</summary>
    public const int MaximumLength = 35;

    /// <summary><paramref name="tag"/> in its canonical case, or null when it is not a language tag.</summary>
    public static string? Canonical(string? tag)
    {
        var given = tag?.Trim() ?? "";
        if (given.Length is 0 or > MaximumLength || !Tag().IsMatch(given)) return null;

        var parts = given.Split('-');
        var output = new List<string>(parts.Length) { parts[0].ToLowerInvariant() };
        var extension = false;
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];

            // After a singleton (u, x, t...) every subtag is lower case: it is an extension's, not a region.
            if (part.Length == 1) extension = true;
            output.Add(extension ? part.ToLowerInvariant()
                : part.Length == 4 && part.All(char.IsAsciiLetter) ? char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()
                : part.Length == 2 && part.All(char.IsAsciiLetter) ? part.ToUpperInvariant()
                : part.ToLowerInvariant());
        }

        return string.Join('-', output);
    }

    /// <summary>Each well-formed tag of <paramref name="tags"/> once, in its canonical case, at most 10.</summary>
    public static IReadOnlyList<string> CanonicalList(IEnumerable<string?> tags) =>
        [.. tags.Select(Canonical).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(10)];

    [GeneratedRegex("^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$")]
    private static partial Regex Tag();
}
