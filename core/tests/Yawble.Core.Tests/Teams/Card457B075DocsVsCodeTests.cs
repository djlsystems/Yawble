using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// Card 457 (B075 re-check, tester): F2 / F4 / F5 and the docs-vs-code check. Each docs statement is paired with the code
/// that makes it true: features.md's Link pattern and Pull requests lines against RepoProviders.Link (RepoProvider.cs:136)
/// and TeamRepos.HostView (TeamRepos.Hosts.cs:96-99); connections-and-integrations.md's pull-requests bullet against
/// RepoHost.PullRequestsUnavailable; UiRpc.cs's WebLinkAsync comment names git.webLink.
/// </summary>
public class Card457B075DocsVsCodeTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "docs", "features.md")) && Directory.Exists(Path.Combine(d.FullName, "core")))
                return d.FullName;
        throw new InvalidOperationException("repository root (docs/features.md) not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The file's text with line breaks and indentation collapsed to single spaces, so wrapped sentences match.</summary>
    private static string Flat(params string[] rel) =>
        System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(Path.Combine([Root(), .. rel])), @"\s+", " ");

    private static string? L(string? origin, WebLinkRequest req, RepoHostSetting? s) => RepoProviders.Link(origin, s, req);

    // ---------------------------------------------------------------- F2: features.md Link pattern vs RepoProvider.cs:136

    [Fact]
    public void F2_LinkPattern_DocsSayWhatTheCodeDoes()
    {
        var doc = Flat("docs", "features.md");
        Assert.Contains("With no provider (none: detected or chosen), the other kinds get none.", doc);
        Assert.Contains("On a provider's host (detected or chosen) a pattern replaces only the kinds it covers and the other kinds keep the provider's own links", doc);
        Assert.Contains("with a GitHub origin and a commit-only pattern, a pull request still links to GitHub's `/pull/<n>`", doc);

        // no provider (detected): the pattern's kinds only
        const string plain = "git@git.example.com:acme/app.git";
        var commitOnly = new RepoHostSetting(null, "https://git.example.com/{path}/commit/{commit}");
        Assert.Equal("https://git.example.com/acme/app/commit/" + Sha, L(plain, new(WebLinkKind.Commit, Commit: Sha), commitOnly));
        Assert.Null(L(plain, new(WebLinkKind.Pr, Pr: 1), commitOnly));
        // provider chosen as none on GitHub: the other kinds get none too ("detected or chosen")
        var chosenNone = new RepoHostSetting(RepoProviderKind.None, "https://mirror.example/{path}/c/{commit}");
        Assert.Equal("https://mirror.example/acme/app/c/" + Sha, L("https://github.com/acme/app.git", new(WebLinkKind.Commit, Commit: Sha), chosenNone));
        Assert.Null(L("https://github.com/acme/app.git", new(WebLinkKind.Pr, Pr: 3), chosenNone));
        // provider's host (detected): GitHub commit-only pattern, the pull request keeps /pull/<n>
        var gh = new RepoHostSetting(null, "https://mirror.example/{path}/c/{commit}");
        Assert.Equal("https://mirror.example/acme/app/c/" + Sha, L("https://github.com/acme/app.git", new(WebLinkKind.Commit, Commit: Sha), gh));
        Assert.Equal("https://github.com/acme/app/pull/3", L("https://github.com/acme/app.git", new(WebLinkKind.Pr, Pr: 3), gh));
        // provider chosen on an unknown host: the other kinds keep the chosen provider's links
        var chosenGitLab = new RepoHostSetting(RepoProviderKind.GitLab, "https://mirror.example/{path}/c/{commit}");
        Assert.Equal("https://git.example.com/acme/app/-/merge_requests/4", L(plain, new(WebLinkKind.Pr, Pr: 4), chosenGitLab));
    }

    // ---------------------------------------------------------------- F4: features.md Pull requests vs TeamRepos.Hosts.cs:96-99

    [Fact]
    public void F4_PullRequestsLine_DocsReasonsAreTheCodesReasons()
    {
        var doc = Flat("docs", "features.md");
        Assert.Contains("GitHub through your `gh`, GitLab through your `glab` and Azure DevOps through your `az repos`", doc);
        Assert.Contains("Bitbucket, Gitea and plain git have none from here.", doc);
        Assert.Contains("the repository has no origin, its provider has no pull requests from here, or the provider's CLI is not installed on this computer", doc);
        Assert.Contains("Whether the CLI is signed in is not checked there", doc);

        bool Installed(string _) => true;
        bool Missing(string _) => false;
        // supported, CLI installed (sign-in not checked)
        foreach (var o in new[] { "https://github.com/a/b.git", "https://gitlab.com/a/b.git", "https://dev.azure.com/a/P/_git/b" })
            Assert.True(TeamRepos.HostView("main", o, null, Installed).PrSupported, o);
        // no origin
        var none = TeamRepos.HostView("main", null, null, Installed);
        Assert.False(none.PrSupported);
        Assert.Contains("no origin", none.PrUnavailable);
        // provider has no pull requests from here
        foreach (var o in new[] { "git@bitbucket.org:a/b.git", "https://gitea.com/a/b.git", "https://git.example.org/a/b.git" })
        {
            var v = TeamRepos.HostView("main", o, null, Installed);
            Assert.False(v.PrSupported, o);
            Assert.Contains("not supported for", v.PrUnavailable);
        }
        // CLI not installed on this computer, naming the CLI per provider
        foreach (var (o, cli) in new[] { ("https://github.com/a/b.git", "gh"), ("https://gitlab.com/a/b.git", "glab"), ("https://dev.azure.com/a/P/_git/b", "az") })
        {
            var v = TeamRepos.HostView("main", o, null, Missing);
            Assert.False(v.PrSupported, o);
            Assert.Contains($"made from here with {cli}, which is not installed on this computer", v.PrUnavailable);
        }
    }

    // ---------------------------------------------------------------- docs: connections-and-integrations.md pull-requests bullet

    [Fact]
    public void Integrations_PullRequestsBullet_DocsTextIsTheCodesText()
    {
        var doc = Flat("docs", "connections-and-integrations.md");
        Assert.Contains("`gh` for GitHub, `glab` for GitLab (merge requests), `az repos` for Azure DevOps", doc);
        Assert.Contains("(Bitbucket, Gitea, an unknown host, provider set to none)", doc);
        Assert.Contains("answer \"pull requests are not supported for <provider or host> from here", doc);
        Assert.Contains("a repository with no origin gets \"… has no remote (no origin) …\"", doc);
        Assert.Contains("with a GitLab origin and only `gh` enabled, an agent's `pr_open` says it needs `glab` and never runs `gh pr create`", doc);
        Assert.Contains("`pr_status` runs, `pr_open` asks first", doc);

        Assert.StartsWith("pull requests are not supported for Bitbucket from here", RepoHost.PullRequestsUnavailable("main", "git@bitbucket.org:a/b.git", RepoProviderKind.Bitbucket));
        Assert.StartsWith("pull requests are not supported for Gitea from here", RepoHost.PullRequestsUnavailable("main", "https://gitea.com/a/b.git", RepoProviderKind.Gitea));
        Assert.StartsWith("pull requests are not supported for git.example.org from here", RepoHost.PullRequestsUnavailable("main", "https://git.example.org/a/b.git", RepoProviderKind.None));
        Assert.Contains("has no remote (no origin)", RepoHost.PullRequestsUnavailable("main", null, RepoProviderKind.None));
        Assert.Null(RepoHost.PullRequestsUnavailable("main", "https://gitlab.com/a/b.git", RepoProviderKind.GitLab));
    }

    // ---------------------------------------------------------------- F5: UiRpc.cs comment names git.webLink

    [Fact]
    public void F5_UiRpcWebLinkComment_NamesGitWebLink()
    {
        var src = File.ReadAllLines(Path.Combine(Root(), "core", "src", "Yawble.Core", "Ui", "UiRpc.cs"));
        var at = Array.FindIndex(src, l => l.Contains("Task<JsonNode?> WebLinkAsync(", StringComparison.Ordinal));
        Assert.True(at > 0, "WebLinkAsync not found");
        Assert.Contains("git.webLink (B075)", src[at - 1]);
        Assert.Equal("git.webLink", Yawble.Core.Ui.UiRpc.WebLinkMethod);
    }
}
