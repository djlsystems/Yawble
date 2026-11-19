namespace Yawble.Core.Teams;

/// <summary>
/// Where a repository's remote is hosted, read from its address. git itself works with any host, or none; only web links
/// and pull requests are host-specific (<see cref="RepoProviders"/>, <see cref="PullRequestProviders"/>): pull requests
/// are opened, listed and merged for GitHub (gh), GitLab (glab) and Azure DevOps (az repos), each through the person's
/// own CLI signed in on this computer.
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
    /// Null when the app can work with pull requests for a repository whose origin is <paramref name="origin"/> (its
    /// provider, or <paramref name="provider"/> when the person set one, has a pull request CLI); otherwise why not, for
    /// the person. Whether that CLI is installed and signed in is found when it runs.
    /// </summary>
    public static string? PullRequestsUnavailable(string repoName, string? origin, RepoProviderKind? provider = null)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return $"{repoName} has no remote (no origin), so it has no pull requests; its branches stay on this computer";
        var kind = provider ?? RepoProviders.Detect(origin);
        if (PullRequestProviders.Supports(kind)) return null;
        var host = HostOf(origin) ?? "a local folder";
        var what = kind == RepoProviderKind.None ? host : RepoProviders.Label(kind);
        return $"pull requests are not supported for {what} from here (only GitHub through gh, GitLab through glab and Azure DevOps through az repos); " +
               $"{repoName}'s origin is on {host}. Push the branch and open the pull request (or merge request) on {host}";
    }
}
