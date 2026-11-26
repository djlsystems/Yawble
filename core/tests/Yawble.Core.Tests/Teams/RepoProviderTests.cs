using System.Diagnostics;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The provider layer. Which service an origin is on (https, ssh:// with a port, scp-like, with and
/// without .git; GitLab's nested groups; every Azure DevOps form; a local folder is plain git), the web links of each
/// kind per provider, the person's override and link pattern, and pull requests routed to the right CLI (fakes only).
/// </summary>
public sealed class RepoProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- detection

    [Theory]
    [InlineData("https://github.com/acme/app.git", RepoProviderKind.GitHub)]
    [InlineData("https://github.com/acme/app", RepoProviderKind.GitHub)]
    [InlineData("git@github.com:acme/app.git", RepoProviderKind.GitHub)]
    [InlineData("ssh://git@ssh.github.com:443/acme/app.git", RepoProviderKind.GitHub)]
    [InlineData("https://gitlab.com/acme/web/app.git", RepoProviderKind.GitLab)]
    [InlineData("git@gitlab.example.com:acme/web/app.git", RepoProviderKind.GitLab)]
    [InlineData("ssh://git@gitlab.internal:2222/acme/app.git", RepoProviderKind.GitLab)]
    [InlineData("https://bitbucket.org/acme/app.git", RepoProviderKind.Bitbucket)]
    [InlineData("git@bitbucket.org:acme/app.git", RepoProviderKind.Bitbucket)]
    [InlineData("https://dev.azure.com/acme/Proj/_git/app", RepoProviderKind.AzureDevOps)]
    [InlineData("https://acme@dev.azure.com/acme/Proj/_git/app", RepoProviderKind.AzureDevOps)]
    [InlineData("https://acme.visualstudio.com/DefaultCollection/Proj/_git/app", RepoProviderKind.AzureDevOps)]
    [InlineData("https://acme.visualstudio.com/Proj/_git/app", RepoProviderKind.AzureDevOps)]
    [InlineData("git@ssh.dev.azure.com:v3/acme/Proj/app", RepoProviderKind.AzureDevOps)]
    [InlineData("acme@vs-ssh.visualstudio.com:v3/acme/Proj/app", RepoProviderKind.AzureDevOps)]
    [InlineData("https://gitea.example.com/acme/app.git", RepoProviderKind.Gitea)]
    [InlineData("ssh://git@forgejo.example.com:2222/acme/app.git", RepoProviderKind.Gitea)]
    [InlineData("https://codeberg.org/acme/app.git", RepoProviderKind.Gitea)]
    [InlineData("https://git.example.com/acme/app.git", RepoProviderKind.None)]
    [InlineData("https://github.com.evil.example/acme/app.git", RepoProviderKind.None)]
    [InlineData(@"C:\repos\app", RepoProviderKind.None)]
    [InlineData("C:/repos/app", RepoProviderKind.None)]
    [InlineData(@"\\server\share\app.git", RepoProviderKind.None)]
    [InlineData("/srv/git/app.git", RepoProviderKind.None)]
    [InlineData("../app.git", RepoProviderKind.None)]
    [InlineData("file:///srv/git/app.git", RepoProviderKind.None)]
    [InlineData(null, RepoProviderKind.None)]
    public void DetectsTheProviderFromTheOrigin(string? origin, RepoProviderKind kind) => Assert.Equal(kind, RepoProviders.Detect(origin));

    [Theory]
    [InlineData("https://github.com/acme/app.git", "https://github.com", "acme/app")]
    [InlineData("ssh://git@ssh.github.com:443/acme/app.git", "https://github.com", "acme/app")]
    [InlineData("git@gitlab.com:acme/web/sub/app.git", "https://gitlab.com", "acme/web/sub/app")]
    [InlineData("ssh://git@gitlab.internal:2222/acme/app.git", "https://gitlab.internal", "acme/app")]
    [InlineData("https://gitlab.internal:8443/acme/app.git/", "https://gitlab.internal:8443", "acme/app")]
    [InlineData("http://gitea.lan:3000/acme/app", "http://gitea.lan:3000", "acme/app")]
    [InlineData("https://acme@dev.azure.com/acme/Proj/_git/app", "https://dev.azure.com", "acme/Proj/_git/app")]
    [InlineData("git@ssh.dev.azure.com:v3/acme/Proj/app", "https://dev.azure.com", "acme/Proj/_git/app")]
    [InlineData("https://acme.visualstudio.com/DefaultCollection/Proj/_git/app", "https://acme.visualstudio.com", "DefaultCollection/Proj/_git/app")]
    [InlineData("acme@vs-ssh.visualstudio.com:v3/acme/Proj/app", "https://acme.visualstudio.com", "Proj/_git/app")]
    public void ReadsTheSiteAndPath(string origin, string site, string path)
    {
        var web = RepoProviders.WebOf(origin);
        Assert.NotNull(web);
        Assert.Equal((site, path), (web.Base, web.Path));
    }

    // ---------------------------------------------------------------- links

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("https://github.com/acme/app.git", "commit", "https://github.com/acme/app/commit/" + Sha)]
    [InlineData("https://github.com/acme/app.git", "branch", "https://github.com/acme/app/tree/members/team/ada")]
    [InlineData("https://github.com/acme/app.git", "file", "https://github.com/acme/app/blob/" + Sha + "/src/a%20b.cs")]
    [InlineData("https://github.com/acme/app.git", "compare", "https://github.com/acme/app/compare/main...members/team/ada")]
    [InlineData("https://github.com/acme/app.git", "pr", "https://github.com/acme/app/pull/7")]
    [InlineData("git@gitlab.com:acme/web/app.git", "commit", "https://gitlab.com/acme/web/app/-/commit/" + Sha)]
    [InlineData("git@gitlab.com:acme/web/app.git", "branch", "https://gitlab.com/acme/web/app/-/tree/members/team/ada")]
    [InlineData("git@gitlab.com:acme/web/app.git", "file", "https://gitlab.com/acme/web/app/-/blob/" + Sha + "/src/a%20b.cs")]
    [InlineData("git@gitlab.com:acme/web/app.git", "compare", "https://gitlab.com/acme/web/app/-/compare/main...members/team/ada")]
    [InlineData("git@gitlab.com:acme/web/app.git", "pr", "https://gitlab.com/acme/web/app/-/merge_requests/7")]
    [InlineData("https://bitbucket.org/acme/app.git", "commit", "https://bitbucket.org/acme/app/commits/" + Sha)]
    [InlineData("https://bitbucket.org/acme/app.git", "branch", "https://bitbucket.org/acme/app/src/members/team/ada")]
    [InlineData("https://bitbucket.org/acme/app.git", "file", "https://bitbucket.org/acme/app/src/" + Sha + "/src/a%20b.cs")]
    [InlineData("https://bitbucket.org/acme/app.git", "compare", "https://bitbucket.org/acme/app/branches/compare/members%2Fteam%2Fada%0Dmain")]
    [InlineData("https://bitbucket.org/acme/app.git", "pr", "https://bitbucket.org/acme/app/pull-requests/7")]
    [InlineData("https://dev.azure.com/acme/Proj/_git/app", "commit", "https://dev.azure.com/acme/Proj/_git/app/commit/" + Sha)]
    [InlineData("https://dev.azure.com/acme/Proj/_git/app", "branch", "https://dev.azure.com/acme/Proj/_git/app?version=GBmembers%2Fteam%2Fada")]
    [InlineData("https://dev.azure.com/acme/Proj/_git/app", "file", "https://dev.azure.com/acme/Proj/_git/app?path=%2Fsrc%2Fa%20b.cs&version=GC" + Sha)]
    [InlineData("https://dev.azure.com/acme/Proj/_git/app", "compare", "https://dev.azure.com/acme/Proj/_git/app/branchCompare?baseVersion=GBmain&targetVersion=GBmembers%2Fteam%2Fada")]
    [InlineData("https://dev.azure.com/acme/Proj/_git/app", "pr", "https://dev.azure.com/acme/Proj/_git/app/pullrequest/7")]
    [InlineData("https://codeberg.org/acme/app.git", "commit", "https://codeberg.org/acme/app/commit/" + Sha)]
    [InlineData("https://codeberg.org/acme/app.git", "branch", "https://codeberg.org/acme/app/src/branch/members/team/ada")]
    [InlineData("https://codeberg.org/acme/app.git", "file", "https://codeberg.org/acme/app/src/commit/" + Sha + "/src/a%20b.cs")]
    [InlineData("https://codeberg.org/acme/app.git", "compare", "https://codeberg.org/acme/app/compare/main...members/team/ada")]
    [InlineData("https://codeberg.org/acme/app.git", "pr", "https://codeberg.org/acme/app/pulls/7")]
    public void BuildsEachKindOfLinkPerProvider(string origin, string kind, string url) =>
        Assert.Equal(url, RepoProviders.Link(origin, null, Request(kind)));

    private static WebLinkRequest Request(string kind) => kind switch
    {
        "commit" => new(WebLinkKind.Commit, Commit: Sha),
        "branch" => new(WebLinkKind.Branch, Branch: "members/team/ada"),
        "file" => new(WebLinkKind.File, Commit: Sha, File: "src/a b.cs"),
        "compare" => new(WebLinkKind.Compare, From: "main", To: "members/team/ada"),
        _ => new(WebLinkKind.Pr, Pr: 7),
    };

    [Theory]
    [InlineData("https://git.example.com/acme/app.git")]
    [InlineData(@"C:\repos\app")]
    [InlineData("/srv/git/app.git")]
    [InlineData(null)]
    public void AnUnknownHostOrALocalFolderHasNoLink(string? origin)
    {
        foreach (var kind in new[] { "commit", "branch", "file", "compare", "pr" })
            Assert.Null(RepoProviders.Link(origin, null, Request(kind)));
    }

    [Fact]
    public void AMissingOrMalformedFieldHasNoLink()
    {
        const string gh = "https://github.com/acme/app.git";
        Assert.Null(RepoProviders.Link(gh, null, new WebLinkRequest(WebLinkKind.Commit)));
        Assert.Null(RepoProviders.Link(gh, null, new WebLinkRequest(WebLinkKind.Commit, Commit: "HEAD; rm -rf")));
        Assert.Null(RepoProviders.Link(gh, null, new WebLinkRequest(WebLinkKind.Branch, Branch: "a..b")));
        Assert.Null(RepoProviders.Link(gh, null, new WebLinkRequest(WebLinkKind.Branch, Branch: "has space")));
        Assert.Null(RepoProviders.Link(gh, null, new WebLinkRequest(WebLinkKind.File, Commit: Sha, File: "../secret")));
        Assert.Null(RepoProviders.Link(gh, null, new WebLinkRequest(WebLinkKind.File, Commit: Sha)));
        Assert.Null(RepoProviders.Link(gh, null, new WebLinkRequest(WebLinkKind.Compare, From: "main")));
        Assert.Null(RepoProviders.Link(gh, null, new WebLinkRequest(WebLinkKind.Pr, Pr: 0)));
    }

    private static string? L(string? origin, WebLinkRequest req, RepoHostSetting? s = null) => RepoProviders.Link(origin, s, req);

    [Fact]
    public void SelfHostedGitLab_WithAPortAndNestedGroups()
    {
        const string https = "https://gitlab.corp.example:8443/platform/payments/backend/api.git";
        Assert.Equal(RepoProviderKind.GitLab, RepoProviders.Detect(https));
        Assert.Equal("https://gitlab.corp.example:8443/platform/payments/backend/api/-/commit/" + Sha, L(https, new(WebLinkKind.Commit, Commit: Sha)));
        Assert.Equal("https://gitlab.corp.example:8443/platform/payments/backend/api/-/merge_requests/12", L(https, new(WebLinkKind.Pr, Pr: 12)));
        // ssh with its own port: the site is https on the default port; the groups are kept.
        const string ssh = "ssh://git@gitlab.corp.example:2222/platform/payments/backend/api.git";
        Assert.Equal("https://gitlab.corp.example/platform/payments/backend/api/-/tree/members/t/ada", L(ssh, new(WebLinkKind.Branch, Branch: "members/t/ada")));
        // scp form, nested groups
        Assert.Equal("https://gitlab.corp.example/platform/payments/api/-/blob/" + Sha + "/src/x.cs",
            L("git@gitlab.corp.example:platform/payments/api.git", new(WebLinkKind.File, Commit: Sha, File: "src/x.cs")));
    }

    [Fact]
    public void AzureDevOps_VisualStudioCom_AndTheSshForms()
    {
        Assert.Equal("https://acme.visualstudio.com/DefaultCollection/Proj/_git/app/commit/" + Sha,
            L("https://acme.visualstudio.com/DefaultCollection/Proj/_git/app", new(WebLinkKind.Commit, Commit: Sha)));
        Assert.Equal("https://acme.visualstudio.com/Proj/_git/app/pullrequest/7",
            L("acme@vs-ssh.visualstudio.com:v3/acme/Proj/app", new(WebLinkKind.Pr, Pr: 7)));
        Assert.Equal("https://dev.azure.com/acme/Proj/_git/app?version=GBmain",
            L("git@ssh.dev.azure.com:v3/acme/Proj/app", new(WebLinkKind.Branch, Branch: "main")));
        // compare by commit: GC, by branch: GB
        Assert.Equal($"https://dev.azure.com/acme/Proj/_git/app/branchCompare?baseVersion=GC{Sha}&targetVersion=GBteam%2Fx",
            L("https://dev.azure.com/acme/Proj/_git/app", new(WebLinkKind.Compare, From: Sha, To: "team/x")));
    }

    [Fact]
    public void ACredentialInTheOrigin_NeverReachesALink()
    {
        foreach (var origin in new[] { "https://pat:s3cr3tTOKEN@github.com/acme/app.git", "https://oauth2:s3cr3tTOKEN@gitlab.com/acme/app.git", "https://acme:s3cr3tTOKEN@dev.azure.com/acme/P/_git/app" })
        {
            var url = L(origin, new(WebLinkKind.Commit, Commit: Sha));
            Assert.NotNull(url);
            Assert.DoesNotContain("s3cr3tTOKEN", url);
            Assert.DoesNotContain("@", url);
            var patterned = L(origin, new(WebLinkKind.Commit, Commit: Sha), new RepoHostSetting(null, "{base}/c/{commit}"));
            Assert.DoesNotContain("s3cr3tTOKEN", patterned);
        }
    }

    // ---------------------------------------------------------------- the override

    [Fact]
    public void TheOverridesProviderWinsOverDetection()
    {
        // A self-hosted GitLab on a name that says nothing: the person says GitLab.
        var gitlab = new RepoHostSetting(RepoProviderKind.GitLab, null);
        Assert.Equal("https://code.example.com/acme/web/app/-/commit/" + Sha,
            RepoProviders.Link("git@code.example.com:acme/web/app.git", gitlab, Request("commit")));
        // And "none" turns links off for a known host.
        Assert.Null(RepoProviders.Link("https://github.com/acme/app.git", new RepoHostSetting(RepoProviderKind.None, null), Request("commit")));
    }

    [Fact]
    public void ALinkPatternServesTheKindWhosePlaceholdersItHoldsExactly()
    {
        var pattern = new RepoHostSetting(null, "{base}/-/c/{commit} https://browse.example.com/{path}/tree/{commit}/{file}");
        const string origin = "ssh://git@git.example.com:29418/acme/app.git";
        // {base} is the repository's web address (https://git.example.com/acme/app), {path} its owner/name.
        Assert.Equal("https://git.example.com/acme/app/-/c/" + Sha, RepoProviders.Link(origin, pattern, Request("commit")));
        Assert.Equal("https://browse.example.com/acme/app/tree/" + Sha + "/src/a%20b.cs", RepoProviders.Link(origin, pattern, Request("file")));
        // No template for these kinds and no provider: no link.
        Assert.Null(RepoProviders.Link(origin, pattern, Request("branch")));
        Assert.Null(RepoProviders.Link(origin, pattern, Request("pr")));
        // A pattern on a known provider covers its kinds; the provider's own layout serves the rest.
        var gh = new RepoHostSetting(null, "https://mirror.example.com/{path}/c/{commit}");
        Assert.Equal("https://mirror.example.com/acme/app/c/" + Sha, RepoProviders.Link("https://github.com/acme/app.git", gh, Request("commit")));
        Assert.Equal("https://github.com/acme/app/pull/7", RepoProviders.Link("https://github.com/acme/app.git", gh, Request("pr")));
        // The shell's docs: {base} = https://github.com/owner/name, {path} = owner/name.
        var docs = new RepoHostSetting(null, "{base}/commit/{commit}?from={path}");
        Assert.Equal($"https://github.com/owner/name/commit/{Sha}?from=owner/name", RepoProviders.Link("git@github.com:owner/name.git", docs, Request("commit")));
        // A pattern without {base}/{path} works with no origin host at all; one that needs them does not.
        Assert.Equal("https://review.example.com/c/7", RepoProviders.Link(@"C:\repos\app", new RepoHostSetting(null, "https://review.example.com/c/{pr}"), Request("pr")));
        Assert.Null(RepoProviders.Link(@"C:\repos\app", new RepoHostSetting(null, "{base}/c/{pr}"), Request("pr")));
    }

    [Fact]
    public void LinkPattern_BaseIsTheRepositorysWebAddress_PathIsOwnerName_EachTemplateByItsExactPlaceholders()
    {
        const string origin = "git@git.example.com:acme/app.git"; // no provider
        var p = new RepoHostSetting(null,
            "{base}/c/{commit}  https://git.example.com/{path}/b/{branch}\nhttps://x.example/{path}/f/{commit}/{file} {base}/cmp/{from}..{to} {base}/pr/{pr}");
        Assert.Equal("https://git.example.com/acme/app/c/" + Sha, L(origin, new(WebLinkKind.Commit, Commit: Sha), p));
        Assert.Equal("https://git.example.com/acme/app/b/members/t/ada", L(origin, new(WebLinkKind.Branch, Branch: "members/t/ada"), p));
        Assert.Equal($"https://x.example/acme/app/f/{Sha}/src/a%20b.cs", L(origin, new(WebLinkKind.File, Commit: Sha, File: "src/a b.cs"), p));
        Assert.Equal("https://git.example.com/acme/app/cmp/main..team/x", L(origin, new(WebLinkKind.Compare, From: "main", To: "team/x"), p));
        Assert.Equal("https://git.example.com/acme/app/pr/9", L(origin, new(WebLinkKind.Pr, Pr: 9), p));

        // Commit and branch templates only: the other kinds get none (no provider).
        var doc = new RepoHostSetting(null, "https://git.example.com/{path}/commit/{commit} https://git.example.com/{path}/src/{branch}");
        Assert.Equal("https://git.example.com/acme/app/commit/" + Sha, L(origin, new(WebLinkKind.Commit, Commit: Sha), doc));
        Assert.Equal("https://git.example.com/acme/app/src/main", L(origin, new(WebLinkKind.Branch, Branch: "main"), doc));
        Assert.Null(L(origin, new(WebLinkKind.File, Commit: Sha, File: "a"), doc));
        Assert.Null(L(origin, new(WebLinkKind.Compare, From: "main", To: "x"), doc));
        Assert.Null(L(origin, new(WebLinkKind.Pr, Pr: 1), doc));

        // A pattern on a provider's host: kinds it does not cover keep the provider's own layout.
        var gh = new RepoHostSetting(null, "https://mirror.example/{path}/c/{commit}");
        Assert.Equal("https://mirror.example/acme/app/c/" + Sha, L("https://github.com/acme/app.git", new(WebLinkKind.Commit, Commit: Sha), gh));
        Assert.Equal("https://github.com/acme/app/pull/3", L("https://github.com/acme/app.git", new(WebLinkKind.Pr, Pr: 3), gh));

        // {commit} alone is commit; {commit}+{file} is file, never commit.
        var fileOnly = new RepoHostSetting(null, "{base}/f/{commit}/{file}");
        Assert.Null(L(origin, new(WebLinkKind.Commit, Commit: Sha), fileOnly));
    }

    [Fact]
    public void AChosenProvider_DecidesTheKindsAPatternDoesNotCover()
    {
        const string plain = "git@git.example.com:acme/app.git";
        // Chosen as none on GitHub: the pattern's kind links, the other kinds get none.
        var chosenNone = new RepoHostSetting(RepoProviderKind.None, "https://mirror.example/{path}/c/{commit}");
        Assert.Equal("https://mirror.example/acme/app/c/" + Sha, L("https://github.com/acme/app.git", new(WebLinkKind.Commit, Commit: Sha), chosenNone));
        Assert.Null(L("https://github.com/acme/app.git", new(WebLinkKind.Pr, Pr: 3), chosenNone));
        // Chosen on an unknown host: the other kinds keep the chosen provider's links.
        var chosenGitLab = new RepoHostSetting(RepoProviderKind.GitLab, "https://mirror.example/{path}/c/{commit}");
        Assert.Equal("https://mirror.example/acme/app/c/" + Sha, L(plain, new(WebLinkKind.Commit, Commit: Sha), chosenGitLab));
        Assert.Equal("https://git.example.com/acme/app/-/merge_requests/4", L(plain, new(WebLinkKind.Pr, Pr: 4), chosenGitLab));
    }

    [Theory]
    [InlineData("{base}/commit/{commit}", null)]
    [InlineData("https://x.example/{path}/c/{commit} https://x.example/{path}/b/{branch}", null)]
    [InlineData("javascript:alert({commit})", "does not start with")]
    [InlineData("https://x.example/{sha}", "unknown placeholder {sha}")]
    [InlineData("https://x.example/{commit}/{branch}", "fits no link")]
    [InlineData("https://x.example/{path}", "fits no link")]
    [InlineData("https://h.example/{path}/{branch}", null)]
    [InlineData("{base}/{path}/f/{commit}/{file}", null)]
    [InlineData("{base}/compare/{from}...{to}", null)]
    [InlineData("{base}/pr/{pr}", null)]
    [InlineData("{base}/c/{from}", "fits no link")]          // {from} without {to}
    [InlineData("{base}/{file}", "fits no link")]            // {file} without {commit}
    [InlineData("{base}/pr/{pr}/{commit}", "fits no link")]
    [InlineData("ftp://h.example/{commit}", "does not start with")]
    [InlineData("{base}/{Commit}", "unknown placeholder")]
    public void APatternIsCheckedBeforeItIsSaved(string pattern, string? problem)
    {
        var said = RepoProviders.PatternProblem(pattern);
        if (problem is null) Assert.Null(said);
        else Assert.Contains(problem, said);
    }

    // ---------------------------------------------------------------- pull requests (fake CLIs)

    private sealed class FakeCli
    {
        public List<(string Cli, string[] Args)> Calls { get; } = [];
        public Func<string, IReadOnlyList<string>, GitResult> Answer = (_, _) => new GitResult(0, "", "");
        public Task<GitResult> Run(string cli, string dir, IReadOnlyList<string> args, CancellationToken ct)
        {
            Calls.Add((cli, [.. args]));
            return Task.FromResult(Answer(cli, args));
        }
    }

    [Theory]
    [InlineData(RepoProviderKind.GitHub, "gh", "pr create --head members/a --base main --title T --body B")]
    [InlineData(RepoProviderKind.GitLab, "glab", "mr create --source-branch members/a --target-branch main --title T --description B --yes")]
    [InlineData(RepoProviderKind.AzureDevOps, "az", "repos pr create --source-branch members/a --target-branch main --title T --description B --detect true --output json")]
    public async Task EachProviderOpensThroughItsOwnCli(RepoProviderKind kind, string cli, string line)
    {
        var fake = new FakeCli();
        var prs = PullRequestProviders.For(kind, fake.Run)!;
        Assert.Equal(cli, prs.Cli);
        await prs.OpenAsync("C:/repo", "members/a", "main", "T", "B", Ct);
        Assert.Equal((cli, line), (fake.Calls.Single().Cli, string.Join(' ', fake.Calls.Single().Args)));
    }

    [Fact]
    public async Task EachProviderReadsItsOwnAnswers()
    {
        var fake = new FakeCli();
        fake.Answer = (cli, a) => new GitResult(0, (cli, a.Contains("list")) switch
        {
            ("gh", false) => """{"number":3,"title":"T","headRefName":"h","baseRefName":"main","state":"MERGED"}""",
            ("glab", false) => """{"iid":3,"title":"T","source_branch":"h","target_branch":"main","state":"opened"}""",
            ("az", false) => """{"pullRequestId":3,"title":"T","sourceRefName":"refs/heads/h","targetRefName":"refs/heads/main","status":"active"}""",
            ("gh", true) => """[{"number":3,"title":"T","headRefName":"h"}]""",
            ("glab", true) => """[{"iid":3,"title":"T","source_branch":"h","state":"opened"},{"iid":4,"title":"Old","source_branch":"o","state":"merged"}]""",
            _ => """[{"pullRequestId":3,"title":"T","sourceRefName":"refs/heads/h"}]""",
        }, "");
        foreach (var kind in new[] { RepoProviderKind.GitHub, RepoProviderKind.GitLab, RepoProviderKind.AzureDevOps })
        {
            var prs = PullRequestProviders.For(kind, fake.Run)!;
            var v = await prs.ViewAsync("C:/repo", 3, Ct);
            Assert.Equal((3L, "T", "h", "main"), (v.Number, v.Title, v.Head, v.Base));
            Assert.Equal(kind != RepoProviderKind.GitHub, v.Open); // the gh answer is a merged one
            var list = await prs.OpenListAsync("C:/repo", Ct);
            Assert.Equal(new Dictionary<string, string> { ["h"] = "#3 T" }, list);
        }
    }

    [Fact]
    public async Task ARefusalSaysHowToSignIn_AndAMissingCliSaysSo()
    {
        var fake = new FakeCli { Answer = (_, _) => new GitResult(1, "", "az: not logged in") };
        var az = PullRequestProviders.For(RepoProviderKind.AzureDevOps, fake.Run)!;
        var e = await Assert.ThrowsAsync<ArgumentException>(() => az.MergeAsync("C:/repo", 3, Ct));
        Assert.Contains("az refused: az: not logged in", e.Message);
        Assert.Contains("az login", e.Message);
        fake.Answer = (cli, _) => new GitResult(-1, "", $"{cli} is not installed on this computer");
        var glab = PullRequestProviders.For(RepoProviderKind.GitLab, fake.Run)!;
        Assert.Contains("glab is not available on this computer", (await Assert.ThrowsAsync<ArgumentException>(() => glab.OpenAsync("C:/repo", "h", "main", "T", "", Ct))).Message);
        Assert.Null(await glab.OpenListAsync("C:/repo", Ct));
    }

    [Theory]
    [InlineData(RepoProviderKind.Bitbucket, "https://bitbucket.org/acme/app.git", "not supported for Bitbucket")]
    [InlineData(RepoProviderKind.Gitea, "https://codeberg.org/acme/app.git", "not supported for Gitea")]
    [InlineData(RepoProviderKind.None, "https://git.example.com/acme/app.git", "not supported for git.example.com")]
    public void ProvidersWithoutPullRequestsHaveNone_AndSayWhy(RepoProviderKind kind, string origin, string why)
    {
        Assert.Null(PullRequestProviders.For(kind, new FakeCli().Run));
        Assert.Contains(why, RepoHost.PullRequestsUnavailable("main", origin));
        // The person's override counts: GitLab on a host that does not say so has pull requests.
        Assert.Null(RepoHost.PullRequestsUnavailable("main", origin, RepoProviderKind.GitLab));
    }

    [Fact]
    public void TheRepositoryViewSaysWhetherPullRequestsWork()
    {
        var gh = TeamRepos.HostView("main", "https://github.com/acme/app.git", null, _ => true);
        Assert.Equal((RepoProviderKind.GitHub, RepoProviderKind.GitHub, true, (string?)null), (gh.Provider, gh.Detected, gh.PrSupported, gh.PrUnavailable));
        var noGlab = TeamRepos.HostView("main", "https://gitlab.com/acme/app.git", null, _ => false);
        Assert.False(noGlab.PrSupported);
        Assert.Contains("glab, which is not installed", noGlab.PrUnavailable);
        var overridden = TeamRepos.HostView("main", "https://git.example.com/acme/app.git", new RepoHostSetting(RepoProviderKind.GitLab, "{base}/{path}/c/{commit}"), _ => true);
        Assert.Equal((RepoProviderKind.GitLab, RepoProviderKind.None, (RepoProviderKind?)RepoProviderKind.GitLab, "{base}/{path}/c/{commit}", true),
            (overridden.Provider, overridden.Detected, overridden.Override, overridden.LinkPattern, overridden.PrSupported));
        Assert.Contains("not supported for Bitbucket", TeamRepos.HostView("docs", "https://bitbucket.org/a/b.git", null, _ => true).PrUnavailable);
    }

    [Fact]
    public void TheRepositoryView_SaysWhyPullRequestsAreUnavailable_NoOrigin_NoSupport_OrAMissingCli()
    {
        bool Installed(string _) => true;
        bool Missing(string _) => false;
        // Supported with the CLI installed (whether it is signed in is not checked).
        foreach (var o in new[] { "https://github.com/a/b.git", "https://gitlab.com/a/b.git", "https://dev.azure.com/a/P/_git/b" })
            Assert.True(TeamRepos.HostView("main", o, null, Installed).PrSupported, o);
        var none = TeamRepos.HostView("main", null, null, Installed);
        Assert.False(none.PrSupported);
        Assert.Contains("no origin", none.PrUnavailable);
        foreach (var o in new[] { "git@bitbucket.org:a/b.git", "https://gitea.com/a/b.git", "https://git.example.org/a/b.git" })
        {
            var v = TeamRepos.HostView("main", o, null, Installed);
            Assert.False(v.PrSupported, o);
            Assert.Contains("not supported for", v.PrUnavailable);
        }
        // The CLI is not installed on this computer: the reason names the provider's CLI.
        foreach (var (o, cli) in new[] { ("https://github.com/a/b.git", "gh"), ("https://gitlab.com/a/b.git", "glab"), ("https://dev.azure.com/a/P/_git/b", "az") })
        {
            var v = TeamRepos.HostView("main", o, null, Missing);
            Assert.False(v.PrSupported, o);
            Assert.Contains($"made from here with {cli}, which is not installed on this computer", v.PrUnavailable);
        }
    }

    [Fact]
    public void PullRequestsUnavailable_NamesTheProviderOrTheHost_AndARepositoryWithNoOrigin()
    {
        Assert.StartsWith("pull requests are not supported for Bitbucket from here", RepoHost.PullRequestsUnavailable("main", "git@bitbucket.org:a/b.git", RepoProviderKind.Bitbucket));
        Assert.StartsWith("pull requests are not supported for Gitea from here", RepoHost.PullRequestsUnavailable("main", "https://gitea.com/a/b.git", RepoProviderKind.Gitea));
        Assert.StartsWith("pull requests are not supported for git.example.org from here", RepoHost.PullRequestsUnavailable("main", "https://git.example.org/a/b.git", RepoProviderKind.None));
        Assert.Contains("has no remote (no origin)", RepoHost.PullRequestsUnavailable("main", null, RepoProviderKind.None));
        Assert.Null(RepoHost.PullRequestsUnavailable("main", "https://gitlab.com/a/b.git", RepoProviderKind.GitLab));
    }

    /// <summary>The docs name the CLI each provider's pull requests go through and the agents' two pull-request tools.</summary>
    [Fact]
    public void TheDocsNameEachProvidersCli_AndThePullRequestTools()
    {
        var features = File.ReadAllText(Path.Combine(Gateway.Binaries.RepoRoot, "docs", "features.md"));
        var integrations = File.ReadAllText(Path.Combine(Gateway.Binaries.RepoRoot, "docs", "connections-and-integrations.md"));
        foreach (var cli in new[] { "`gh`", "`glab`", "`az repos`" })
        {
            Assert.Contains(cli, features);
            Assert.Contains(cli, integrations);
        }
        Assert.Contains("`pr_open`", integrations);
        Assert.Contains("`pr_status`", integrations);
    }

    // ---------------------------------------------------------------- the Concierge's git_pr_open / git_pr_merge / team_git count

    [Fact]
    public async Task TheConciergesPullRequestsAreRoutedByProvider_TheOverrideIncluded()
    {
        using var db = new TempDb();
        var repos = new TeamRepos(db.Dir, db.ConnectionString);
        var bus = new BusEngine(db.OpenStore(), new UnavailableRunner(), new BusOptions { Repos = repos, IdleOffer = false });
        await using var _ = bus;
        await bus.StartAsync(Ct);
        var teamId = await bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
        var cfg = Path.Combine(db.Dir, "empty.gitconfig");
        File.WriteAllText(cfg, "");
        var work = Path.Combine(Path.GetTempPath(), "yrepo" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(work);
        try
        {
            Git(work, cfg, "init", "-q", "-b", "main");
            Git(work, cfg, "remote", "add", "origin", "https://github.com/acme/app.git");
            var gh = new List<string[]>();
            var fake = new FakeCli();
            var book = new ConfirmationBook();
            ConciergeActions Actions() => new(bus, (_, _, _) => Task.FromResult<System.Text.Json.Nodes.JsonNode?>(null), (_, _, _) => Task.FromResult<System.Text.Json.Nodes.JsonNode?>(null),
                new ConciergeGit(new HostGit(cfg), new PersonGitPush(globalConfig: cfg)),
                (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([new ActionRepo("main", work, "main", [])]), book,
                (_, a, _) => { gh.Add([.. a]); return Task.FromResult(new GitResult(0, """[{"number":1,"title":"G","headRefName":"g"}]""", "")); },
                turn: new ConciergeTurn("c1", byPerson: true, remote: false), cli: fake.Run);
            async Task<string> Call(string tool, System.Text.Json.Nodes.JsonObject args)
            {
                try { return await Actions().TryCallAsync(tool, args, Ct) ?? "none"; }
                catch (ArgumentException e) { return "REFUSED: " + e.Message; }
            }
            var target = new GitRepoTarget("alpha", "main", work, "main", []);

            // GitHub: gh.
            Assert.StartsWith("ok: opened a pull request on GitHub", await Call("git_pr_open", new() { ["team"] = "alpha", ["branch"] = "b", ["title"] = "T" }));
            Assert.Equal("pr", gh.Single()[0]);
            Assert.Equal("#1 G", (await Actions().OpenPullRequestsAsync(target, Ct))!["g"]);
            Assert.Empty(fake.Calls);

            // An unknown host: refused, nothing runs; the person says it is a GitLab: glab.
            Git(work, cfg, "remote", "set-url", "origin", "git@code.example.com:acme/web/app.git");
            Assert.Contains("not supported for code.example.com", await Call("git_pr_open", new() { ["team"] = "alpha", ["branch"] = "b", ["title"] = "T" }));
            Assert.Null(await Actions().OpenPullRequestsAsync(target, Ct));
            Assert.Empty(fake.Calls);
            repos.SetHostSetting(teamId, "main", RepoProviderKind.GitLab, null);
            fake.Answer = (_, a) => new GitResult(0, a[1] switch
            {
                "view" => """{"iid":5,"title":"T","source_branch":"b","target_branch":"main","state":"opened"}""",
                "list" => """[{"iid":5,"title":"T","source_branch":"b"}]""",
                _ => "https://code.example.com/acme/web/app/-/merge_requests/5",
            }, "");
            Assert.StartsWith("ok: opened a pull request on GitLab", await Call("git_pr_open", new() { ["team"] = "alpha", ["branch"] = "b", ["title"] = "T" }));
            Assert.Equal("glab", fake.Calls.Last().Cli);
            var shown = await Call("git_pr_merge", new() { ["team"] = "alpha", ["pr"] = 5 });
            Assert.StartsWith("NOT DONE", shown);
            var token = System.Text.RegularExpressions.Regex.Match(shown, "confirm=\"([0-9a-f]+)\"").Groups[1].Value;
            Assert.StartsWith("ok: merged", await Call("git_pr_merge", new() { ["team"] = "alpha", ["pr"] = 5, ["confirm"] = token }));
            Assert.Equal(("glab", "mr merge 5 --yes"), (fake.Calls.Last().Cli, string.Join(' ', fake.Calls.Last().Args)));
            Assert.Equal("#5 T", (await Actions().OpenPullRequestsAsync(target, Ct))!["b"]);
            Assert.Equal(2, gh.Count); // gh's pr create and pr list only

            // And "none" turns them off for a host that has them.
            Git(work, cfg, "remote", "set-url", "origin", "https://github.com/acme/app.git");
            repos.SetHostSetting(teamId, "main", RepoProviderKind.None, null);
            Assert.Contains("not supported for github.com", await Call("git_pr_open", new() { ["team"] = "alpha", ["branch"] = "b", ["title"] = "T" }));
            Assert.Equal(2, gh.Count); // gh's pr create and pr list only
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- stored with the team's repositories; links only for what origin has

    [Fact]
    public async Task TheOverrideIsKept_AndALinkIsGivenOnlyForWhatIsOnOrigin()
    {
        using var db = new TempDb();
        var repos = new TeamRepos(db.Dir, db.ConnectionString);
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { Repos = repos, IdleOffer = false });
        // The team's repository is a folder the person picked, kept short (git on Windows refuses paths over 260 characters).
        var cfg = Path.Combine(db.Dir, "empty.gitconfig");
        File.WriteAllText(cfg, "");
        var picked = Path.Combine(Path.GetTempPath(), "yrepo" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(picked);
        try
        {
            Git(picked, cfg, "init", "-q", "-b", "main");
            Git(picked, cfg, "commit", "-q", "--allow-empty", "-m", "first");
            await LinksAsync(repos, engine, picked, cfg);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(picked, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            try { Directory.Delete(picked, true); } catch (IOException) { }
        }
    }

    private static async Task LinksAsync(TeamRepos repos, BusEngine engine, string picked, string cfg)
    {
        var teamId = await engine.CreateTeamAsync("Alpha", "Manager", "claude", picked, Ct);
        var repo = await repos.EnsureTeamRepoAsync(teamId, "Alpha", Ct);

        // The override: saved, read back (by id and by team name), checked, cleared.
        Assert.Null(repos.HostSettingOf(teamId, "main"));
        repos.SetHostSetting(teamId, null, RepoProviderKind.GitLab, " {base}/{path}/c/{commit} ");
        Assert.Equal(new RepoHostSetting(RepoProviderKind.GitLab, "{base}/{path}/c/{commit}"), repos.HostSettingOf(teamId, "MAIN"));
        Assert.Equal(RepoProviderKind.GitLab, repos.HostSettingOf("alpha", "main")!.Provider);
        Assert.Throws<ArgumentException>(() => repos.SetHostSetting(teamId, "main", null, "https://x.example/{nope}"));
        Assert.Throws<ArgumentException>(() => repos.SetHostSetting(teamId, "nosuch", RepoProviderKind.GitHub, null));
        repos.SetHostSetting(teamId, "main", null, null);
        Assert.Null(repos.HostSettingOf(teamId, "main"));

        // The team repository with a GitHub origin; origin has the first commit (as of a fetch), not the second.
        string G(params string[] args) => Git(repo.Path, cfg, args);
        G("commit", "--allow-empty", "-m", "pushed");
        var pushed = G("rev-parse", "HEAD");
        G("remote", "add", "origin", "https://github.com/acme/app.git");
        G("update-ref", "refs/remotes/origin/" + repo.BaseBranch, pushed);
        G("commit", "--allow-empty", "-m", "local only");
        var local = G("rev-parse", "HEAD");
        G("branch", "members/alpha/ada");

        Assert.Equal("https://github.com/acme/app/commit/" + pushed,
            await repos.GitWebLinkAsync(teamId, null, new WebLinkRequest(WebLinkKind.Commit, Commit: pushed), Ct));
        Assert.Equal($"https://github.com/acme/app/blob/{pushed}/README.md",
            await repos.GitWebLinkAsync(teamId, "main", new WebLinkRequest(WebLinkKind.File, Commit: pushed, File: "README.md"), Ct));
        Assert.Null(await repos.GitWebLinkAsync(teamId, null, new WebLinkRequest(WebLinkKind.Commit, Commit: local), Ct));
        Assert.Equal($"https://github.com/acme/app/tree/{repo.BaseBranch}",
            await repos.GitWebLinkAsync(teamId, null, new WebLinkRequest(WebLinkKind.Branch, Branch: repo.BaseBranch), Ct));
        Assert.Null(await repos.GitWebLinkAsync(teamId, null, new WebLinkRequest(WebLinkKind.Branch, Branch: "members/alpha/ada"), Ct));
        Assert.Null(await repos.GitWebLinkAsync(teamId, null, new WebLinkRequest(WebLinkKind.Compare, From: repo.BaseBranch, To: "members/alpha/ada"), Ct));
        Assert.Equal($"https://github.com/acme/app/compare/{pushed}...{repo.BaseBranch}",
            await repos.GitWebLinkAsync(teamId, null, new WebLinkRequest(WebLinkKind.Compare, From: pushed, To: repo.BaseBranch), Ct));
        Assert.Equal("https://github.com/acme/app/pull/4", await repos.GitWebLinkAsync(teamId, null, new WebLinkRequest(WebLinkKind.Pr, Pr: 4), Ct));

        // The override's provider and pattern apply to the team's links.
        repos.SetHostSetting(teamId, "main", RepoProviderKind.None, "https://mirror.example.com/{path}/c/{commit}");
        Assert.Equal("https://mirror.example.com/acme/app/c/" + pushed,
            await repos.GitWebLinkAsync(teamId, null, new WebLinkRequest(WebLinkKind.Commit, Commit: pushed), Ct));
        Assert.Null(await repos.GitWebLinkAsync(teamId, null, new WebLinkRequest(WebLinkKind.Pr, Pr: 4), Ct));

        var view = await repos.HostViewAsync(teamId, null, Ct);
        Assert.Equal((RepoProviderKind.None, RepoProviderKind.GitHub, false), (view.Provider, view.Detected, view.PrSupported));
        await Assert.ThrowsAsync<ArgumentException>(() => repos.GitWebLinkAsync(teamId, "nosuch", new WebLinkRequest(WebLinkKind.Pr, Pr: 4), Ct));
    }

    private static string Git(string dir, string globalConfig, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir };
        psi.Environment["GIT_CONFIG_GLOBAL"] = globalConfig;
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        foreach (var a in new[] { "-c", "user.name=T", "-c", "user.email=t@example.test", "-c", "commit.gpgsign=false" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {e}");
        return o.Trim();
    }
}
