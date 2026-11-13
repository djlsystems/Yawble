using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>A team's repository cloned from an address, with the person's own git: the origin stays, for the push back.</summary>
public class CloneForTeamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARepositoryIsClonedOutsideTheTeamsFolder_WithItsOrigin()
    {
        using var db = new TempDb();
        var (_, bare) = TeamReposTests.PersonsRepoWithOrigin(db.Dir);
        AgentGit.Host(db.Dir, "--git-dir", bare, "symbolic-ref", "HEAD", "refs/heads/trunk"); // the origin's default branch
        var repos = new TeamRepos(db.Dir, db.ConnectionString);
        var url = new Uri(bare).AbsoluteUri; // file:///... stands in for https://github.com/...
        var clone = await repos.CloneForTeamAsync("web", url, Ct);

        Assert.Equal(repos.ClonePathFor("web"), clone);
        Assert.False(clone.StartsWith(Path.Combine(db.Dir, "teams"), StringComparison.OrdinalIgnoreCase)); // deleting the team keeps it
        Assert.True(File.Exists(Path.Combine(clone, "README.md")));
        Assert.Equal(url.TrimEnd('/'), AgentGit.Host(clone, "remote", "get-url", "origin").Trim().TrimEnd('/'));

        var again = await Assert.ThrowsAsync<ArgumentException>(() => repos.CloneForTeamAsync("web", url, Ct));
        Assert.Contains("already there", again.Message);
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("C:\\some\\folder")]
    [InlineData("ftp://example.com/repo.git")]
    public async Task AnythingButAnAddressIsRefused(string url)
    {
        using var db = new TempDb();
        var repos = new TeamRepos(db.Dir, db.ConnectionString);
        var e = await Assert.ThrowsAsync<ArgumentException>(() => repos.CloneForTeamAsync("web", url, Ct));
        Assert.Contains("repository's address", e.Message);
    }

    [Fact]
    public async Task AFailedCloneSaysWhy_AndLeavesNothingBehind()
    {
        using var db = new TempDb();
        var repos = new TeamRepos(db.Dir, db.ConnectionString);
        var missing = new Uri(Path.Combine(db.Dir, "no-such-origin.git")).AbsoluteUri;
        var e = await Assert.ThrowsAsync<ArgumentException>(() => repos.CloneForTeamAsync("web", missing, Ct));
        Assert.Contains("git could not clone it", e.Message);
        Assert.False(Directory.Exists(repos.ClonePathFor("web")));
    }
}
