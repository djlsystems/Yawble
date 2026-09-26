namespace Harness.Host;

/// <summary>Validates repository URLs before they become directory names below a team root.
/// Public so its tests can reach it: there is no <c>InternalsVisibleTo</c> from this assembly.</summary>
public static class RepoUrls
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<string>? urls)
    {
        var validated = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var supplied in urls ?? [])
        {
            var url = supplied?.Trim() ?? "";

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException($"'{supplied}' is not an absolute http or https repository URL.");
            }

            var name = DeriveName(url);

            if (!IsLegalFolderName(name))
            {
                throw new ArgumentException($"'{supplied}' derives unsafe repository folder name '{name}'.");
            }

            if (!names.Add(name))
            {
                throw new ArgumentException($"'{supplied}' derives repository folder name '{name}', which another URL already uses.");
            }

            validated.Add(url);
        }

        return validated;
    }

    public static string DeriveName(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var escapedPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var escapedLeaf = escapedPath.Split('/').LastOrDefault() ?? "";
        var leaf = Uri.UnescapeDataString(escapedLeaf);
        return leaf.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? leaf[..^4] : leaf;
    }

    /// <summary>
    /// What Linux refuses in one directory name - empty, `/`, NUL, `.` or `..` - and what git
    /// refuses as a path component, `.git` in any case. The host runs only on Linux, so Windows
    /// rules (`\`, `:`, a leading or trailing dot, a trailing space) are not applied.
    /// </summary>
    private static bool IsLegalFolderName(string name) =>
        !string.IsNullOrEmpty(name)
        && !name.Contains('/') && !name.Contains('\0')
        && name is not ("." or "..")
        && !name.Equals(".git", StringComparison.OrdinalIgnoreCase);
}
