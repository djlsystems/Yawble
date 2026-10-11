namespace Harness.Host;

/// <summary>Validates repository URLs before they become directory names below a team root.
/// Public so its tests can reach it: there is no <c>InternalsVisibleTo</c> from this assembly.</summary>
public static class RepoUrls
{
    /// <summary>
    /// Every entry checked, trimmed, in order. An entry is an absolute http or https URL, or
    /// <c>local:&lt;name&gt;</c> for one of the instance's local repositories (<see cref="LocalRepos"/>):
    /// its name must be legal and <paramref name="localExists"/> must say it is there - with no
    /// such check given, every <c>local:</c> entry is refused. Each refusal names the entry.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<string>? urls, Func<string, bool>? localExists = null)
    {
        var validated = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var supplied in urls ?? [])
        {
            var url = supplied?.Trim() ?? "";

            if (LocalRepos.IsLocal(url))
            {
                var local = LocalRepos.NameOf(url);
                if (!LocalRepos.IsLegalName(local))
                {
                    throw new ArgumentException($"'{supplied}': {LocalRepos.IllegalName(local)}");
                }

                if (localExists is null || !localExists(local))
                {
                    throw new ArgumentException(
                        $"'{supplied}' names no local repository on this instance. Create '{local}' first, or pick one that exists.");
                }

                url = LocalRepos.ReferenceFor(local);
            }
            else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
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
        if (LocalRepos.IsLocal(url)) return LocalRepos.NameOf(url);

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
