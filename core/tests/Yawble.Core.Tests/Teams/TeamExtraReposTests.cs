using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// A team's other repositories (0022): a writable one works like the main one (a clone per member, fetched back,
/// pushed to its origin); a read-only reference is one host checkout, kept current, from which nothing comes back.
/// </summary>
public class TeamExtraReposTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string Repo, string Bare) SecondRepo(TeamsFixture f, string name)
    {
        var dir = Path.Combine(f.Db.Dir, name);
        Directory.CreateDirectory(dir);
        return TeamReposTests.PersonsRepoWithOrigin(dir);
    }

    private static string Git(string cwd, string home, params string[] args)
    {
        var r = AgentGit.Run(cwd, home, args);
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)}: {r.Stderr}");
        return r.Stdout;
    }

    [Fact]
    public async Task AWritableRepository_IsClonedPerMember_BroughtBack_AndPushed()
    {
        using var f = new TeamsFixture();
        var (_, ada, ben) = f.Team();
        var (cloud, cloudOrigin) = SecondRepo(f, "cloud");
        var added = await f.Repos.AddExtraAsync(ada.TeamId, "Alpha", "cloud", cloud, null, writable: true, Ct);
        Assert.Equal("trunk", added.BaseBranch);

        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        var extra = Assert.Single(checkout.Extras);
        Assert.True(extra.Writable);
        Assert.Equal(f.Repos.ExtraClonePath("Alpha", "Ada", "cloud"), extra.Path);
        Assert.Equal("members/alpha/ada", extra.Branch);
        Assert.NotNull(extra.SharedBundle);
        Assert.True(File.Exists(Path.Combine(extra.Path, "README.md")));
        Assert.Equal("", Git(extra.Path, checkout.Workspace, "remote")); // no remote, as the main clone
        Assert.Equal("members/alpha/ada", Git(extra.Path, checkout.Workspace, "branch", "--show-current"));

        File.WriteAllText(Path.Combine(extra.Path, "api.txt"), "api");
        Git(extra.Path, checkout.Workspace, "add", "api.txt");
        Git(extra.Path, checkout.Workspace, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "-m", "api");
        // An agent adding a remote to its extra clone is scrubbed like one in its main clone.
        Git(extra.Path, checkout.Workspace, "remote", "add", "sneaky", "https://example.invalid/x.git");
        var findings = await f.Repos.CheckAndScrubAsync(ada, Ct);
        Assert.Contains(findings, x => x.Where.StartsWith("clone of cloud", StringComparison.Ordinal) && x.What.Contains("remote", StringComparison.Ordinal));

        Assert.Null((await f.Repos.FetchBackAsync(ada, Ct)).Error);
        Assert.Contains("members/alpha/ada", AgentGit.Host(cloud, "branch", "--list"));

        var report = await f.Repos.PublishAsync(ada, Ct);
        Assert.Equal(TeamPublishResult.Published, report.Result);
        Assert.Contains("cloud: members/alpha/ada", report.Branches);
        Assert.Contains("refs/heads/members/alpha/ada", AgentGit.Host(cloudOrigin, "for-each-ref", "--format=%(refname)"));

        // A teammate's clone starts from the base branch, and the bundle carries Ada's branch for it to fetch.
        var benCheckout = await f.Repos.PrepareAsync(ben, Ct);
        var benExtra = Assert.Single(benCheckout.Extras);
        Assert.False(File.Exists(Path.Combine(benExtra.Path, "api.txt")));
        Git(benExtra.Path, benCheckout.Workspace, "fetch", "-q", Path.Combine(benExtra.SharedBundle!, TeamRepos.SharedBundleFile), "members/alpha/ada");
        Assert.Contains("api.txt", Git(benExtra.Path, benCheckout.Workspace, "show", "--stat", "FETCH_HEAD"));
    }

    [Fact]
    public async Task AReference_IsOneReadOnlyCheckout_KeptCurrent_AndNothingComesBack()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (old, _) = SecondRepo(f, "old");
        var branchesBefore = AgentGit.Host(old, "for-each-ref", "--format=%(refname)");
        await f.Repos.AddExtraAsync(ada.TeamId, "Alpha", "old-yawble", old, null, writable: false, Ct);

        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        var reference = Assert.Single(checkout.Extras);
        Assert.False(reference.Writable);
        Assert.Null(reference.Branch);
        Assert.Equal(f.Repos.ReferencePath("Alpha", "old-yawble"), reference.Path);
        Assert.True(File.Exists(Path.Combine(reference.Path, "README.md")));
        Assert.False(Directory.Exists(f.Repos.ExtraClonePath("Alpha", "Ada", "old-yawble")));

        // The person moves the reference on: the next run sees it.
        File.WriteAllText(Path.Combine(old, "new.txt"), "new");
        AgentGit.Host(old, "add", "new.txt");
        AgentGit.Host(old, "-c", "user.name=p", "-c", "user.email=p@x", "commit", "-q", "-m", "new");
        await f.Repos.PrepareAsync(ada, Ct);
        Assert.True(File.Exists(Path.Combine(reference.Path, "new.txt")));

        Assert.Null((await f.Repos.FetchBackAsync(ada, Ct)).Error);
        Assert.Equal(TeamPublishResult.NoRepository, (await f.Repos.PublishAsync(ada, Ct)).Result);
        Assert.Equal(branchesBefore, AgentGit.Host(old, "for-each-ref", "--format=%(refname)"));
    }

    [Fact]
    public async Task NamesAreChecked_AndTheMainRepositoryCannotBeAddedAgain()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (cloud, _) = SecondRepo(f, "cloud");
        var (main, _) = SecondRepo(f, "main");
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", main, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => f.Repos.AddExtraAsync(ada.TeamId, "Alpha", "../up", cloud, null, true, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Repos.AddExtraAsync(ada.TeamId, "Alpha", null, cloud, "https://example.invalid/x.git", true, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Repos.AddExtraAsync(ada.TeamId, "Alpha", "again", main, null, true, Ct));
        var added = await f.Repos.AddExtraAsync(ada.TeamId, "Alpha", null, cloud, null, true, Ct);
        Assert.Equal("persons-repo", added.Name); // the folder's own name
        await Assert.ThrowsAsync<ArgumentException>(() => f.Repos.AddExtraAsync(ada.TeamId, "Alpha", "Persons-Repo", cloud, null, true, Ct));

        Assert.Equal("yawble-cloud", TeamRepos.SuggestRepoName("https://github.com/djlsystems/Yawble-cloud.git"));
        Assert.Equal("repo", TeamRepos.SuggestRepoName("git@github.com:o/repo.git"));
    }

    [Fact]
    public async Task Removing_TakesTheClonesAndCheckouts_AndKeepsTheRepository()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (cloud, _) = SecondRepo(f, "cloud");
        var (old, _) = SecondRepo(f, "old");
        await f.Repos.AddExtraAsync(ada.TeamId, "Alpha", "cloud", cloud, null, true, Ct);
        await f.Repos.AddExtraAsync(ada.TeamId, "Alpha", "old", old, null, false, Ct);
        await f.Repos.PrepareAsync(ada, Ct);

        Assert.Empty(f.Repos.RemoveExtra(ada.TeamId, "Alpha", ["Manager", "Ada", "Ben"], "cloud"));
        Assert.Empty(f.Repos.RemoveExtra(ada.TeamId, "Alpha", ["Manager", "Ada", "Ben"], "old"));
        Assert.Empty(f.Repos.ExtrasOf(ada.TeamId));
        Assert.False(Directory.Exists(f.Repos.ExtraClonePath("Alpha", "Ada", "cloud")));
        Assert.False(Directory.Exists(f.Repos.ExtraSharedPath("Alpha", "cloud")));
        Assert.False(Directory.Exists(f.Repos.ReferencePath("Alpha", "old")));
        Assert.True(File.Exists(Path.Combine(cloud, "README.md")));
        Assert.True(File.Exists(Path.Combine(old, "README.md")));
        Assert.Empty((await f.Repos.PrepareAsync(ada, Ct)).Extras);
    }

    [Fact]
    public async Task TheGitDialog_MergesAWritableRepositorysTeamBranch_AndRefusesAReference()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (cloud, cloudOrigin) = SecondRepo(f, "cloud");
        var (old, _) = SecondRepo(f, "old");
        await f.Repos.AddExtraAsync(ada.TeamId, "Alpha", "cloud", cloud, null, writable: true, Ct);
        await f.Repos.AddExtraAsync(ada.TeamId, "Alpha", "old", old, null, writable: false, Ct);

        // The team's delivery branch in the cloud repository, one commit ahead of trunk (as the Manager's merges leave it).
        AgentGit.Host(cloud, "checkout", "-q", "-b", TeamRepos.TeamBranchFor("Alpha"));
        File.WriteAllText(Path.Combine(cloud, "ci.yml"), "ci");
        AgentGit.Host(cloud, "add", "ci.yml");
        AgentGit.Host(cloud, "-c", "user.name=t", "-c", "user.email=t@x", "commit", "-q", "-m", "ci");
        var teamSha = AgentGit.Host(cloud, "rev-parse", "HEAD");
        AgentGit.Host(cloud, "checkout", "-q", "trunk");

        var status = await f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct, "cloud");
        Assert.Equal("team/alpha", status.TeamBranch);
        Assert.Equal(teamSha, status.TeamSha);

        var merged = await f.Repos.GitMergeTeamToBaseAsync(ada.TeamId, "Alpha", Ct, "cloud");
        Assert.True(merged.Changed, merged.Message);
        Assert.Equal(teamSha, AgentGit.Host(cloudOrigin, "rev-parse", "refs/heads/trunk")); // a fast-forward, pushed to origin
        Assert.True(File.Exists(Path.Combine(cloud, "ci.yml"))); // the person's checkout moved with it

        await Assert.ThrowsAsync<ArgumentException>(() => f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct, "old"));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct, "nope"));
    }

    [Fact]
    public async Task EachRun_IsToldTheCommitOfThePersonsCheckouts_WhereTheirCLIsRun()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (main, _) = SecondRepo(f, "main");
        var (cloud, _) = SecondRepo(f, "cloud");
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", main, Ct);
        await f.Repos.AddExtraAsync(ada.TeamId, "Alpha", "cloud", cloud, null, writable: true, Ct);

        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        var mainSha = AgentGit.Host(main, "rev-parse", "refs/heads/trunk");
        var cloudSha = AgentGit.Host(cloud, "rev-parse", "refs/heads/trunk");
        Assert.Equal(mainSha, checkout.BaseCommit);
        Assert.Equal(cloudSha, Assert.Single(checkout.Extras).BaseCommit);

        var line = Assert.Single(TeamPrompt.CheckoutCommitLines(checkout));
        Assert.Contains($"main repository: trunk at {mainSha}", line);
        Assert.Contains($"cloud: trunk at {cloudSha}", line);

        // The person merges something: the next run sees the new commit.
        File.WriteAllText(Path.Combine(cloud, "n.txt"), "n");
        AgentGit.Host(cloud, "add", "n.txt");
        AgentGit.Host(cloud, "-c", "user.name=p", "-c", "user.email=p@x", "commit", "-q", "-m", "n");
        Assert.Equal(AgentGit.Host(cloud, "rev-parse", "HEAD"), Assert.Single((await f.Repos.PrepareAsync(ada, Ct)).Extras).BaseCommit);
    }

    [Fact]
    public void ThePrompt_NamesEachRepository_AndWhatTheMemberMayDoWithIt()
    {
        var checkout = new MemberCheckout("/c/repo", "/c/home", "/c/tmp", "members/alpha/ada", "main", "/team")
        {
            Extras =
            [
                new ExtraCheckout("cloud", "/c/repos/cloud", true, "members/alpha/ada", "main", "/t/shared/cloud.git"),
                new ExtraCheckout("old", "/t/reference/old", false, null, "main", null),
            ],
        };
        var text = string.Join('\n', TeamPrompt.RepositoryLines(checkout));
        Assert.Contains("- cloud: your own clone at /c/repos/cloud, on branch members/alpha/ada", text);
        Assert.Contains("- old: READ-ONLY reference at /t/reference/old", text);
    }
}
