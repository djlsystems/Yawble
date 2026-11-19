using System.Text.Json.Nodes;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// Card 470 (B074 core): git.commitsResolve on temp repositories. Short and full IDs resolve with repo, full ID, title and
/// pushed; a hex word that is no commit, a branch or tag named like hex and a tree's ID give no match; the same commit
/// in two repositories (main and a writable extra) gives two matches, a read-only reference is not looked at; a batch
/// of any size costs the same few git processes per repository.
/// </summary>
public class CommitsResolveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Commit(string repo, string message)
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Person", ["GIT_AUTHOR_EMAIL"] = "p@example.invalid",
            ["GIT_COMMITTER_NAME"] = "Person", ["GIT_COMMITTER_EMAIL"] = "p@example.invalid",
        };
        AgentGit.HostWithEnv(repo, env, "-c", "commit.gpgSign=false", "commit", "-q", "--allow-empty", "-m", message);
        return AgentGit.Host(repo, "rev-parse", "HEAD");
    }

    private sealed record Rig(TeamsFixture F, long TeamId, string Repo, string Root, string Second);

    /// <summary>Team Alpha on a repository with two commits on main; origin's main (a remote-tracking ref) at the first.</summary>
    private static async Task<Rig> RigAsync(TeamsFixture f)
    {
        var (_, ada, _) = f.Team();
        var repo = Path.Combine(f.Db.Dir, "person-repo");
        Directory.CreateDirectory(repo);
        AgentGit.Host(repo, "init", "-q", "-b", "main");
        var root = Commit(repo, "Start the project");
        var second = Commit(repo, "Second commit\n\nWith a body.");
        AgentGit.Host(repo, "update-ref", "refs/remotes/origin/main", root);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", repo, Ct);
        return new Rig(f, ada.TeamId, repo, root, second);
    }

    /// <summary>
    /// A second repository with the same commits, as a clone of the person's repository would have them: its objects
    /// reached through alternates, main and origin's main at the person's main. (git clone cannot run in the agent
    /// sandbox: spawning upload-pack fails.)
    /// </summary>
    private static string CopyOf(TeamsFixture f, Rig r, string name)
    {
        var copy = Path.Combine(f.Db.Dir, name);
        Directory.CreateDirectory(copy);
        AgentGit.Host(copy, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(copy, ".git", "objects", "info", "alternates"), Path.Combine(r.Repo, ".git", "objects").Replace('\\', '/') + "\n");
        var main = AgentGit.Host(r.Repo, "rev-parse", "refs/heads/main");
        AgentGit.Host(copy, "update-ref", "refs/heads/main", main);
        AgentGit.Host(copy, "update-ref", "refs/remotes/origin/main", main);
        AgentGit.Host(copy, "reset", "-q", "--hard", "main");
        return copy;
    }

    private static Task<CommitsResolved> Resolve(Rig r, params string[] ids) =>
        r.F.Repos.ResolveCommitIdsAsync([(r.TeamId, "Alpha")], ids, Ct);

    [Fact]
    public async Task ShortAndFullIds_ResolveWithRepoFullIdTitleAndPushed()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);

        var done = await Resolve(r, r.Root[..7], r.Second.ToUpperInvariant(), r.Root[..7]);
        Assert.Empty(done.Errors);
        Assert.Equal(2, done.Results.Count); // the repeated ID is answered once
        var first = Assert.Single(done.Results[0].Matches);
        Assert.Equal(r.Root[..7], done.Results[0].Id);
        Assert.True(done.Results[0].Valid);
        Assert.Equal(new CommitMatch("Alpha", r.TeamId, null, r.Root, "Start the project", true), first);
        var second = Assert.Single(done.Results[1].Matches);
        Assert.Equal(r.Second, done.Results[1].Id); // lower-cased
        Assert.Equal("Second commit", second.Title);
        Assert.False(second.Pushed); // origin's main is at the root only

        var json = done.ToJson();
        Assert.Equal(TeamRepos.MaxResolveIds, json["max"]!.GetValue<int>());
        Assert.False(json.ContainsKey("errors"));
        var m = json["results"]![0]!["matches"]![0]!.AsObject();
        Assert.Equal(["fullId", "pushed", "repo", "shortId", "team", "teamId", "title"], m.Select(p => p.Key).Order());
        Assert.Null(m["repo"]);
        Assert.Equal(r.Root[..12], m["shortId"]!.GetValue<string>());
    }

    [Fact]
    public async Task HexWordsThatAreNotCommits_NoMatch_EvenABranchOrTagNamedLikeHex()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "branch", "cafe1234", r.Second);
        AgentGit.Host(r.Repo, "tag", "beefcafe", r.Second);
        var tree = AgentGit.Host(r.Repo, "rev-parse", "HEAD^{tree}");

        var done = await Resolve(r, "cafe1234", "beefcafe", "0123456789abcdef", tree[..10], "nothex!", "abc");
        Assert.Empty(done.Errors);
        Assert.All(done.Results, x => Assert.Empty(x.Matches));
        Assert.Equal([true, true, true, true, false, false], done.Results.Select(x => x.Valid));
    }

    [Fact]
    public async Task TheSameShortIdInTwoRepositories_TwoMatches_AndAReadOnlyReferenceIsNotLookedAt()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var copy = CopyOf(f, r, "copy-repo");
        var reference = CopyOf(f, r, "reference-repo");
        await f.Repos.AddExtraAsync(r.TeamId, "Alpha", "copy", copy, null, writable: true, Ct);
        await f.Repos.AddExtraAsync(r.TeamId, "Alpha", "docs", reference, null, writable: false, Ct);

        var done = await Resolve(r, r.Second[..8]);
        var matches = done.Results.Single().Matches;
        Assert.Equal(2, matches.Count);
        Assert.Equal([null, "copy"], matches.Select(x => x.Repo));
        Assert.All(matches, x => Assert.Equal(r.Second, x.FullId));
        // In the clone, origin is the person's repository, which has it: pushed there; not in the main repository.
        Assert.Equal([false, true], matches.Select(x => x.Pushed));
    }

    [Fact]
    public async Task AnUnpushedCommit_IsNotPushed_AndARepositoryWithoutOrigin_PushesNothing()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var local = Commit(r.Repo, "Local only");
        var done = await Resolve(r, local, r.Root);
        Assert.False(done.Results[0].Matches.Single().Pushed);
        Assert.True(done.Results[1].Matches.Single().Pushed);

        AgentGit.Host(r.Repo, "update-ref", "-d", "refs/remotes/origin/main");
        done = await Resolve(r, r.Root);
        Assert.False(done.Results.Single().Matches.Single().Pushed);
    }

    [Fact]
    public async Task ABatch_CostsTheSameFewGitCallsPerRepository_NotOnePerId()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var commits = new List<string> { r.Root, r.Second };
        for (var i = 0; i < 30; i++) commits.Add(Commit(r.Repo, $"Commit {i}"));

        async Task<int> Calls(IReadOnlyList<string> ids)
        {
            var count = 0;
            f.Repos.Git.OnRun = (_, _) => Interlocked.Increment(ref count); // the team's only repository
            try
            {
                var done = await Resolve(r, [.. ids]);
                Assert.All(done.Results.Where(x => commits.Any(c => c.StartsWith(x.Id, StringComparison.Ordinal))), x => Assert.Single(x.Matches));
            }
            finally
            {
                f.Repos.Git.OnRun = null;
            }
            return count;
        }

        var one = await Calls([commits[5][..7]]);
        var many = await Calls([.. commits.Select(c => c[..9]), "deadbeef0", "cafe123"]);
        Assert.Equal(3, one); // rev-parse --disambiguate, log --no-walk, rev-list --not --remotes=origin
        Assert.Equal(one, many);
        Assert.Equal(1, await Calls(["deadbeef0"])); // nothing matched: only the rev-parse
    }

    [Fact]
    public async Task TheCap_IsTwoHundredDistinctIds()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var ids = Enumerable.Range(0, TeamRepos.MaxResolveIds).Select(i => i.ToString("x8")).ToList();
        Assert.Equal(TeamRepos.MaxResolveIds, (await Resolve(r, [.. ids, .. ids])).Results.Count);
        var e = await Assert.ThrowsAsync<ArgumentException>(() => Resolve(r, [.. ids, "abcdef12345"]));
        Assert.Contains("200", e.Message);
    }

    [Fact]
    public async Task EveryTeam_AndATeamWithoutARepository_IsSkippedNotCreated()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var (_, beta, _) = f.Team("Beta");
        var done = await f.Repos.ResolveCommitIdsAsync([(r.TeamId, "Alpha"), (beta.TeamId, "Beta")], [r.Root[..7]], Ct);
        Assert.Equal("Alpha", done.Results.Single().Matches.Single().Team);
        Assert.Empty(done.Errors);
        Assert.Null(f.Repos.RepoOf(beta.TeamId));
    }
}
