using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>Where a remote is hosted, read from its address: pull requests are GitHub's alone, everything else works with any host.</summary>
public class RepoHostTests
{
    [Theory]
    [InlineData("https://github.com/acme/app.git", "github.com")]
    [InlineData("https://user:token@GitHub.com/acme/app", "github.com")]
    [InlineData("git@github.com:acme/app.git", "github.com")]
    [InlineData("ssh://git@ssh.github.com:443/acme/app.git", "ssh.github.com")]
    [InlineData("https://gitlab.com/acme/app.git", "gitlab.com")]
    [InlineData("git@bitbucket.org:acme/app.git", "bitbucket.org")]
    [InlineData("https://dev.azure.com/acme/proj/_git/app", "dev.azure.com")]
    [InlineData("ssh://git@git.example.com:2222/team/app.git", "git.example.com")]
    [InlineData("git.example.com:team/app.git", "git.example.com")]
    public void ReadsTheHost(string address, string host) => Assert.Equal(host, RepoHost.HostOf(address));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\repos\app")]
    [InlineData("C:/repos/app")]
    [InlineData("/srv/git/app.git")]
    [InlineData("../app.git")]
    [InlineData("file:///srv/git/app.git")]
    public void ALocalFolderHasNoHost(string? address) => Assert.Null(RepoHost.HostOf(address));

    [Theory]
    [InlineData("https://github.com/acme/app.git", true)]
    [InlineData("git@github.com:acme/app.git", true)]
    [InlineData("https://gitlab.com/acme/app.git", false)]
    [InlineData("https://github.com.evil.example/acme/app.git", false)]
    [InlineData(@"C:\repos\app", false)]
    public void OnlyGitHubIsGitHub(string address, bool github) => Assert.Equal(github, RepoHost.IsGitHub(address));

    [Fact]
    public void PullRequestsSayWhyNot()
    {
        Assert.Null(RepoHost.PullRequestsUnavailable("main", "https://github.com/acme/app.git"));
        Assert.Contains("no remote", RepoHost.PullRequestsUnavailable("main", null));
        var gitlab = RepoHost.PullRequestsUnavailable("main", "https://gitlab.com/acme/app.git");
        Assert.Contains("only for repositories hosted on GitHub", gitlab);
        Assert.Contains("on gitlab.com", gitlab);
        Assert.Contains("a local folder", RepoHost.PullRequestsUnavailable("main", "/srv/git/app.git"));
    }
}
