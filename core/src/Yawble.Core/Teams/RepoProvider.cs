using System.Text;
using System.Text.RegularExpressions;

namespace Yawble.Core.Teams;

/// <summary>The hosting service of a repository's origin. None: plain git, no links, no pull requests.</summary>
public enum RepoProviderKind { None, GitHub, GitLab, Bitbucket, AzureDevOps, Gitea }

/// <summary>What a web link points at (git.webLink's kind).</summary>
public enum WebLinkKind { Commit, Branch, File, Compare, Pr }

/// <summary>
/// An origin as the web knows it: <see cref="Base"/> is the site (https://host[:port], never the ssh port) and
/// <see cref="Path"/> the repository's path on it, without a leading slash or .git (GitLab's nested groups kept;
/// Azure DevOps as org/project/_git/repo or, on visualstudio.com, [DefaultCollection/]project/_git/repo).
/// </summary>
public sealed record WebOrigin(RepoProviderKind Provider, string Host, string Base, string Path);

/// <summary>A repository's per-repository override (stored with the team's repository settings): wins over detection.</summary>
public sealed record RepoHostSetting(RepoProviderKind? Provider, string? LinkPattern);

/// <summary>What a link needs besides the origin; only the fields of its kind are read.</summary>
public sealed record WebLinkRequest(WebLinkKind Kind, string? Commit = null, string? Branch = null, string? File = null, string? From = null, string? To = null, long? Pr = null);

/// <summary>
/// The provider layer's plain part: which service an origin is on, read from its address, and the web links of
/// a commit, branch, file, compare or pull request there. Everything else in the app stays plain git.
/// </summary>
public static class RepoProviders
{
    /// <summary>The provider's wire name (teams.repos, teams.setRepo): github, gitlab, bitbucket, azuredevops, gitea or none.</summary>
    public static string WireName(RepoProviderKind k) => k switch
    {
        RepoProviderKind.GitHub => "github",
        RepoProviderKind.GitLab => "gitlab",
        RepoProviderKind.Bitbucket => "bitbucket",
        RepoProviderKind.AzureDevOps => "azuredevops",
        RepoProviderKind.Gitea => "gitea",
        _ => "none",
    };

    /// <summary>A wire name back (case-insensitive); null for anything else.</summary>
    public static RepoProviderKind? FromWire(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "github" => RepoProviderKind.GitHub,
        "gitlab" => RepoProviderKind.GitLab,
        "bitbucket" => RepoProviderKind.Bitbucket,
        "azuredevops" or "azure" or "azure-devops" => RepoProviderKind.AzureDevOps,
        "gitea" or "forgejo" => RepoProviderKind.Gitea,
        "none" => RepoProviderKind.None,
        _ => null,
    };

    /// <summary>The provider's name for the person ("Open on GitLab", "not supported for Bitbucket").</summary>
    public static string Label(RepoProviderKind k) => k switch
    {
        RepoProviderKind.GitHub => "GitHub",
        RepoProviderKind.GitLab => "GitLab",
        RepoProviderKind.Bitbucket => "Bitbucket",
        RepoProviderKind.AzureDevOps => "Azure DevOps",
        RepoProviderKind.Gitea => "Gitea",
        _ => "plain git",
    };

    /// <summary>The provider of an origin, from its host alone. A local folder, file:// or an unknown host: None.</summary>
    public static RepoProviderKind Detect(string? origin) => ProviderOfHost(RepoHost.HostOf(origin));

    private static RepoProviderKind ProviderOfHost(string? host)
    {
        if (host is null) return RepoProviderKind.None;
        if (host is "dev.azure.com" or "ssh.dev.azure.com" || host.EndsWith(".visualstudio.com", StringComparison.Ordinal)) return RepoProviderKind.AzureDevOps;
        if (host is "github.com" or "www.github.com" or "ssh.github.com") return RepoProviderKind.GitHub;
        if (host.Contains("gitlab", StringComparison.Ordinal)) return RepoProviderKind.GitLab;
        if (host is "bitbucket.org" or "www.bitbucket.org") return RepoProviderKind.Bitbucket;
        if (host.Contains("gitea", StringComparison.Ordinal) || host.Contains("forgejo", StringComparison.Ordinal) || host is "codeberg.org") return RepoProviderKind.Gitea;
        return RepoProviderKind.None;
    }

    /// <summary>
    /// The origin as the web knows it, for <paramref name="provider"/> (the detected one by default; an override reads the
    /// same address its own way). Null for a local folder, file:// or nothing.
    /// </summary>
    public static WebOrigin? WebOf(string? origin, RepoProviderKind? provider = null)
    {
        var a = origin?.Trim();
        if (string.IsNullOrEmpty(a) || RepoHost.HostOf(a) is not { } host) return null;
        string path;
        var site = "https://" + host;
        if (a.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(a, UriKind.Absolute, out var uri)) return null;
            path = Uri.UnescapeDataString(uri.AbsolutePath);
            // An http(s) origin keeps its own scheme and port; ssh:// and git:// are served on https with the default port.
            if (uri.Scheme is "http" or "https") site = $"{uri.Scheme}://{host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}";
        }
        else path = a[(a.IndexOf(':') + 1)..];
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        path = path.TrimEnd('/');
        var kind = provider ?? ProviderOfHost(host);
        // github.com's ssh and www names serve the same site.
        if (host is "ssh.github.com" or "www.github.com") site = "https://github.com";
        if (host is "www.bitbucket.org") site = "https://bitbucket.org";
        if (kind == RepoProviderKind.AzureDevOps)
        {
            if (host == "ssh.dev.azure.com" || host.StartsWith("vs-ssh.", StringComparison.Ordinal))
            {
                // ssh.dev.azure.com:v3/org/project/repo (and vs-ssh.visualstudio.com:v3/org/project/repo)
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4 && parts[0] == "v3")
                    return host == "ssh.dev.azure.com"
                        ? new(kind, host, "https://dev.azure.com", $"{parts[1]}/{parts[2]}/_git/{parts[3]}")
                        : new(kind, host, $"https://{parts[1]}.visualstudio.com", $"{parts[2]}/_git/{parts[3]}");
            }
            else if (host == "dev.azure.com")
            {
                // https://[org@]dev.azure.com/org/project/_git/repo
                site = "https://dev.azure.com";
            }
        }
        return new WebOrigin(kind, host, site, path);
    }

    private static readonly Regex Sha = new("^[0-9a-fA-F]{4,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new(@"\{(base|path|commit|branch|file|from|to|pr)\}", RegexOptions.CultureInvariant);

    /// <summary>
    /// The web address of <paramref name="req"/> on <paramref name="origin"/>, or null when there is none: no origin, an
    /// unknown host (None) without a pattern, or a field the kind needs is missing or malformed. The override's provider
    /// wins over detection; its link pattern wins over the provider's own layout for the kinds it covers.
    /// </summary>
    public static string? Link(string? origin, RepoHostSetting? setting, WebLinkRequest req)
    {
        if (!Valid(req)) return null;
        var web = WebOf(origin, setting?.Provider);
        if (!string.IsNullOrWhiteSpace(setting?.LinkPattern) && FromPattern(setting.LinkPattern, web, req) is { } patterned) return patterned;
        if (web is null) return null;
        var b = $"{web.Base}/{EscapePath(web.Path)}";
        string Seg(string s) => EscapePath(s);
        string Q(string s) => Uri.EscapeDataString(s);
        return (web.Provider, req.Kind) switch
        {
            (RepoProviderKind.GitHub, WebLinkKind.Commit) => $"{b}/commit/{req.Commit}",
            (RepoProviderKind.GitHub, WebLinkKind.Branch) => $"{b}/tree/{Seg(req.Branch!)}",
            (RepoProviderKind.GitHub, WebLinkKind.File) => $"{b}/blob/{req.Commit}/{Seg(req.File!)}",
            (RepoProviderKind.GitHub, WebLinkKind.Compare) => $"{b}/compare/{Seg(req.From!)}...{Seg(req.To!)}",
            (RepoProviderKind.GitHub, WebLinkKind.Pr) => $"{b}/pull/{req.Pr}",

            (RepoProviderKind.GitLab, WebLinkKind.Commit) => $"{b}/-/commit/{req.Commit}",
            (RepoProviderKind.GitLab, WebLinkKind.Branch) => $"{b}/-/tree/{Seg(req.Branch!)}",
            (RepoProviderKind.GitLab, WebLinkKind.File) => $"{b}/-/blob/{req.Commit}/{Seg(req.File!)}",
            (RepoProviderKind.GitLab, WebLinkKind.Compare) => $"{b}/-/compare/{Seg(req.From!)}...{Seg(req.To!)}",
            (RepoProviderKind.GitLab, WebLinkKind.Pr) => $"{b}/-/merge_requests/{req.Pr}",

            (RepoProviderKind.Bitbucket, WebLinkKind.Commit) => $"{b}/commits/{req.Commit}",
            (RepoProviderKind.Bitbucket, WebLinkKind.Branch) => $"{b}/src/{Seg(req.Branch!)}",
            (RepoProviderKind.Bitbucket, WebLinkKind.File) => $"{b}/src/{req.Commit}/{Seg(req.File!)}",
            // Bitbucket's compare names the newer side first: /branches/compare/<to>%0D<from>
            (RepoProviderKind.Bitbucket, WebLinkKind.Compare) => $"{b}/branches/compare/{Q(req.To!)}%0D{Q(req.From!)}",
            (RepoProviderKind.Bitbucket, WebLinkKind.Pr) => $"{b}/pull-requests/{req.Pr}",

            (RepoProviderKind.AzureDevOps, WebLinkKind.Commit) => $"{b}/commit/{req.Commit}",
            (RepoProviderKind.AzureDevOps, WebLinkKind.Branch) => $"{b}?version=GB{Q(req.Branch!)}",
            (RepoProviderKind.AzureDevOps, WebLinkKind.File) => $"{b}?path={Q("/" + req.File!.TrimStart('/'))}&version=GC{req.Commit}",
            (RepoProviderKind.AzureDevOps, WebLinkKind.Compare) => $"{b}/branchCompare?baseVersion={AzureVersion(req.From!)}&targetVersion={AzureVersion(req.To!)}",
            (RepoProviderKind.AzureDevOps, WebLinkKind.Pr) => $"{b}/pullrequest/{req.Pr}",

            (RepoProviderKind.Gitea, WebLinkKind.Commit) => $"{b}/commit/{req.Commit}",
            (RepoProviderKind.Gitea, WebLinkKind.Branch) => $"{b}/src/branch/{Seg(req.Branch!)}",
            (RepoProviderKind.Gitea, WebLinkKind.File) => $"{b}/src/commit/{req.Commit}/{Seg(req.File!)}",
            (RepoProviderKind.Gitea, WebLinkKind.Compare) => $"{b}/compare/{Seg(req.From!)}...{Seg(req.To!)}",
            (RepoProviderKind.Gitea, WebLinkKind.Pr) => $"{b}/pulls/{req.Pr}",
            _ => null,
        };
    }

    private static string AzureVersion(string v) => (Sha.IsMatch(v) && v.Length >= 7 ? "GC" : "GB") + Uri.EscapeDataString(v);

    /// <summary>The placeholders that tell the kinds apart; a template serves the kind whose set it holds exactly.</summary>
    private static readonly IReadOnlyDictionary<WebLinkKind, string[]> Keys = new Dictionary<WebLinkKind, string[]>
    {
        [WebLinkKind.Commit] = ["commit"],
        [WebLinkKind.Branch] = ["branch"],
        [WebLinkKind.File] = ["commit", "file"],
        [WebLinkKind.Compare] = ["from", "to"],
        [WebLinkKind.Pr] = ["pr"],
    };

    /// <summary>
    /// A link pattern: one template or several separated by whitespace. A template is used for the kind whose
    /// placeholders ({commit}, {branch}, {file}, {from}, {to}, {pr}) it holds exactly; {base} (the repository's web
    /// address, e.g. https://github.com/owner/name) and {path} (its owner/name path) may appear in any and need the origin. The first template that fits wins; none fits: null (the provider's own layout then).
    /// </summary>
    public static string? FromPattern(string pattern, WebOrigin? web, WebLinkRequest req)
    {
        var want = Keys[req.Kind];
        foreach (var template in pattern.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var used = Placeholder.Matches(template).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var keys = used.Where(u => u is not ("base" or "path")).ToHashSet(StringComparer.Ordinal);
            if (!keys.SetEquals(want)) continue;
            if ((used.Contains("base") || used.Contains("path")) && web is null) continue;
            var url = Placeholder.Replace(template, m => m.Groups[1].Value switch
            {
                // {base}: the repository's web address (https://github.com/owner/name); {path}: its owner/name path.
                "base" => $"{web!.Base}/{EscapePath(web.Path)}",
                "path" => EscapePath(web!.Path),
                "commit" => req.Commit!,
                "branch" => EscapePath(req.Branch!),
                "file" => EscapePath(req.File!),
                "from" => EscapePath(req.From!),
                "to" => EscapePath(req.To!),
                "pr" => req.Pr!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => m.Value,
            });
            // Only a web address is ever handed to the browser.
            return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? url : null;
        }
        return null;
    }

    /// <summary>Whether a pattern is usable: every template a web address with known placeholders that serves one kind.</summary>
    public static string? PatternProblem(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        foreach (var template in pattern.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!template.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !template.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !template.StartsWith("{base}", StringComparison.Ordinal))
                return $"'{template}' does not start with https:// or {{base}}";
            if (Regex.Matches(template, @"\{([^}]*)\}").Select(m => m.Groups[1].Value).FirstOrDefault(n => !Placeholder.IsMatch("{" + n + "}")) is { } unknown)
                return $"'{template}' has an unknown placeholder {{{unknown}}}; use {{base}} {{path}} {{commit}} {{branch}} {{file}} {{from}} {{to}} {{pr}}";
            var keys = Placeholder.Matches(template).Select(m => m.Groups[1].Value).Where(u => u is not ("base" or "path")).ToHashSet(StringComparer.Ordinal);
            if (!Keys.Values.Any(k => keys.SetEquals(k)))
                return $"'{template}' fits no link: use {{commit}}; {{branch}}; {{commit}} and {{file}}; {{from}} and {{to}}; or {{pr}}";
        }
        return null;
    }

    private static bool Valid(WebLinkRequest r) => r.Kind switch
    {
        WebLinkKind.Commit => IsSha(r.Commit),
        WebLinkKind.Branch => IsRef(r.Branch),
        WebLinkKind.File => IsSha(r.Commit) && IsFile(r.File),
        WebLinkKind.Compare => IsRef(r.From) && IsRef(r.To),
        WebLinkKind.Pr => r.Pr is > 0,
        _ => false,
    };

    public static bool IsSha(string? s) => s is not null && Sha.IsMatch(s);

    /// <summary>A branch or commit name as git allows it (no spaces, control characters, .., ~ ^ : ? * [ \).</summary>
    public static bool IsRef(string? s) =>
        !string.IsNullOrEmpty(s) && s.Length <= 255 && !s.Contains("..", StringComparison.Ordinal) && !s.StartsWith('-') && !s.StartsWith('/') && !s.EndsWith('/')
        && s.All(c => c > ' ' && c != 0x7f && c is not ('~' or '^' or ':' or '?' or '*' or '[' or '\\'));

    private static bool IsFile(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Length <= 1024 && !s.Contains('\\') && s.Split('/').All(p => p is not ("." or "..")) && s.All(c => c >= ' ' && c != 0x7f);

    /// <summary>Each path segment escaped, the slashes kept.</summary>
    private static string EscapePath(string path)
    {
        var sb = new StringBuilder();
        foreach (var part in path.Trim('/').Split('/'))
        {
            if (sb.Length > 0) sb.Append('/');
            sb.Append(Uri.EscapeDataString(part));
        }
        return sb.ToString();
    }
}
