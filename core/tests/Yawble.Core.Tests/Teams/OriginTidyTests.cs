using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// A team's own branches on origin once their work is in origin's base branch: deleted there after the team branch is
/// merged, and at a run's end when the merge happened somewhere else; never pushed back while they add nothing; pushed
/// again when the team has new work. Another team's branch and a branch with unmerged work stay. A person's repository
/// with a bare origin, in temp dirs.
/// </summary>
public class OriginTidyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TeamsFixture F, MemberRef Manager, MemberRef Ada, string Picked, string Bare)> PickedTeam()
    {
        var f = new TeamsFixture();
        var (manager, ada, _) = f.Team();
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        return (f, manager, ada, picked, bare);
    }

    /// <summary>A commit on the member's branch, fetched back into the team repository as a run's end does.</summary>
    private static async Task Commit(TeamsFixture f, MemberRef member, string file)
    {
        SandboxGit.RequireLocalTransport();
        var checkout = await f.Repos.PrepareAsync(member, Ct);
        File.WriteAllText(Path.Combine(checkout.ClonePath, file), file + "\n");
        AgentGit.Ok(checkout, "add", file);
        AgentGit.Ok(checkout, "commit", "-q", "-m", file);
        Assert.True((await f.Repos.FetchBackAsync(member, Ct)).Fetched);
    }

    private static string Rev(string repo, string reference) => AgentGit.Host(repo, "rev-parse", reference);

    private static string[] OnOrigin(string bare) =>
        AgentGit.Host(bare, "for-each-ref", "--format=%(refname:short)", "refs/heads/").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The Manager's work on team/alpha and Ada's on members/alpha/ada, both pushed to origin as a run's end does.</summary>
    private static async Task<(TeamsFixture F, MemberRef Manager, MemberRef Ada, string Picked, string Bare)> TeamWithPublishedWork()
    {
        var (f, manager, ada, picked, bare) = await PickedTeam();
        await Commit(f, manager, "m.txt");
        await Commit(f, ada, "a.txt");
        Assert.Equal(TeamPublishResult.Published, (await f.Repos.PublishAsync(manager, Ct)).Result);
        Assert.Contains("team/alpha", OnOrigin(bare));
        Assert.Contains("members/alpha/ada", OnOrigin(bare));
        return (f, manager, ada, picked, bare);
    }

    [Fact]
    public async Task MergingTheTeamBranch_DeletesItOnOrigin_KeepsItHere_AndLeavesUnmergedAndOtherTeamsBranches()
    {
        var (f, manager, _, picked, bare) = await TeamWithPublishedWork();
        using (f)
        {
            // Another team's branch on the same origin, with nothing the base lacks.
            AgentGit.Host(picked, "push", "-q", "origin", "trunk:refs/heads/team/beta");
            AgentGit.Host(picked, "fetch", "-q", "origin");
            var team = Rev(picked, "refs/heads/team/alpha");

            var r = await f.Repos.GitMergeTeamToBaseAsync(manager.TeamId, "Alpha", Ct);

            Assert.True(r.Changed);
            Assert.Equal(team, Rev(bare, "refs/heads/trunk"));
            Assert.Contains("Deleted on origin, now merged: team/alpha.", r.Message);
            var origin = OnOrigin(bare);
            Assert.DoesNotContain("team/alpha", origin);
            Assert.Contains("members/alpha/ada", origin); // Ada's work is not in the base: kept
            Assert.Contains("team/beta", origin);         // not this team's
            Assert.Equal(team, Rev(picked, "refs/heads/team/alpha")); // the team repository keeps its branch
            lock (f.Log) Assert.Contains(f.Log, line => line.Contains("deleted team/alpha on origin") && line.Contains(team));
        }
    }

    [Fact]
    public async Task AMergedBranch_IsNotPushedBack_EvenWithASyncMergeOnTop()
    {
        var (f, manager, _, picked, bare) = await TeamWithPublishedWork();
        using (f)
        {
            // Origin's trunk moved on, so the merge is a merge commit and the team branch is behind the new trunk.
            var other = Path.Combine(f.Db.Dir, "elsewhere-" + Guid.NewGuid().ToString("N")[..6]);
            AgentGit.Host(f.Db.Dir, "clone", "-q", "-b", "trunk", bare, other);
            File.WriteAllText(Path.Combine(other, "other.txt"), "other");
            AgentGit.Host(other, "add", "other.txt");
            AgentGit.Host(other, "-c", "user.name=Other", "-c", "user.email=o@example.invalid", "commit", "-q", "-m", "elsewhere");
            AgentGit.Host(other, "push", "-q", "origin", "trunk");
            Assert.Equal("merged", (await f.Repos.GitMergeTeamToBaseAsync(manager.TeamId, "Alpha", Ct)).Outcome);
            Assert.DoesNotContain("team/alpha", OnOrigin(bare));
            // The app's sync before new work: a merge commit on the team branch that changes no file of the base.
            var sync = AgentGit.Host(picked, "-c", "user.name=Yawble", "-c", "user.email=y@example.invalid",
                "commit-tree", "trunk^{tree}", "-p", "team/alpha", "-p", "trunk", "-m", "Sync trunk into team/alpha");
            AgentGit.Host(picked, "update-ref", "refs/heads/team/alpha", sync);

            var report = await f.Repos.PublishAsync(manager, Ct);

            Assert.DoesNotContain("team/alpha", report.Branches);
            Assert.DoesNotContain("team/alpha", OnOrigin(bare));
        }
    }

    [Fact]
    public async Task ABranchMergedSomewhereElse_IsDeletedOnOriginAtTheNextRunsEnd()
    {
        var (f, manager, _, picked, bare) = await TeamWithPublishedWork();
        using (f)
        {
            // Someone merges the team branch into trunk on origin, outside the app.
            var other = Path.Combine(f.Db.Dir, "elsewhere-" + Guid.NewGuid().ToString("N")[..6]);
            AgentGit.Host(f.Db.Dir, "clone", "-q", "-b", "trunk", bare, other);
            AgentGit.Host(other, "-c", "user.name=Other", "-c", "user.email=o@example.invalid", "merge", "-q", "--no-ff", "--no-edit", "origin/team/alpha");
            AgentGit.Host(other, "push", "-q", "origin", "trunk");
            AgentGit.Host(picked, "fetch", "-q", "origin");

            await f.Repos.PublishAsync(manager, Ct);

            var origin = OnOrigin(bare);
            Assert.DoesNotContain("team/alpha", origin);
            Assert.Contains("members/alpha/ada", origin);
        }
    }

    [Fact]
    public async Task NewWorkAfterTheMerge_IsPushedAgain()
    {
        var (f, manager, _, picked, bare) = await TeamWithPublishedWork();
        using (f)
        {
            Assert.True((await f.Repos.GitMergeTeamToBaseAsync(manager.TeamId, "Alpha", Ct)).Changed);
            Assert.DoesNotContain("team/alpha", OnOrigin(bare));

            await Commit(f, manager, "next.txt");
            var report = await f.Repos.PublishAsync(manager, Ct);

            Assert.Contains("team/alpha", report.Branches);
            Assert.Equal(Rev(picked, "refs/heads/team/alpha"), Rev(bare, "refs/heads/team/alpha"));
        }
    }

    [Fact]
    public async Task ABranchWithWorkTheBaseLacks_StaysOnOrigin()
    {
        var (f, manager, _, picked, bare) = await TeamWithPublishedWork();
        using (f)
        {
            var before = AgentGit.Host(bare, "for-each-ref");

            Assert.Equal(TeamPublishResult.NothingToPublish, (await f.Repos.PublishAsync(manager, Ct)).Result);

            Assert.Equal(before, AgentGit.Host(bare, "for-each-ref"));
            Assert.Equal(Rev(picked, "refs/heads/team/alpha"), Rev(bare, "refs/heads/team/alpha"));
        }
    }
}
