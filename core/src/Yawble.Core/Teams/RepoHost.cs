namespace Yawble.Core.Teams;

/// <summary>
/// Where a repository's remote is hosted, read from its address. git itself works with any host, or none; only pull
/// requests are host-specific, and the app opens, lists and merges them for GitHub (through the person's gh) alone.
/// </summary>
public static class RepoHost
{
    /// <summary>
    /// The host name of a remote address, lower case: https://host/..., ssh://[user@]host[:port]/..., git://host/...,
    /// or the scp-like user@host:path. Null for a local path, a file:// address or nothing.
    /// </summary>
    public static string? HostOf(string? address)
    {
        var a = address?.Trim();
        if (string.IsNullOrEmpty(a)) return null;
        if (a.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(a, UriKind.Absolute, out var uri) || uri.IsFile || uri.Host.Length == 0) return null;
            return uri.Host.ToLowerInvariant();
        }
        // scp-like: [user@]host:path, where host has no slash and is not a Windows drive letter (C:\repo, C:/repo).
        var colon = a.IndexOf(':');
        if (colon <= 1) return null;
        var left = a[..colon];
        if (left.Contains('/') || left.Contains('\\')) return null;
        var at = left.LastIndexOf('@');
        var host = at >= 0 ? left[(at + 1)..] : left;
        return host.Length == 0 ? null : host.ToLowerInvariant();
    }

    public static bool IsGitHub(string? address) => HostOf(address) is "github.com" or "www.github.com" or "ssh.github.com";

    /// <summary>
    /// Null when the app can work with pull requests for a repository whose origin is <paramref name="origin"/>; otherwise
    /// why not, for the person.
    /// </summary>
    public static string? PullRequestsUnavailable(string repoName, string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return $"{repoName} has no remote (no origin), so it has no pull requests; its branches stay on this computer";
        if (IsGitHub(origin)) return null;
        var host = HostOf(origin) ?? "a local folder";
        return $"pull requests are opened, listed and merged from here only for repositories hosted on GitHub; {repoName}'s origin is on {host}. " +
               $"Push the branch and open the pull request (or merge request) on {host}";
    }
}
