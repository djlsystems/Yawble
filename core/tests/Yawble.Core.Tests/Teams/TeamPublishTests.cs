using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// Publishing a team's work to the team repository's origin: every branch not yet on origin,
/// by refspec, never forced, never main/master/HEAD or the base branch, from the host (never from a sandbox).
/// </summary>
public class TeamPublishTests
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

    private static async Task Commit(TeamsFixture f, MemberRef member, string file)
    {
        var checkout = await f.Repos.PrepareAsync(member, Ct);
        File.WriteAllText(Path.Combine(checkout.ClonePath, file), file);
        AgentGit.Ok(checkout, "add", file);
        AgentGit.Ok(checkout, "commit", "-q", "-m", file);
        Assert.True((await f.Repos.FetchBackAsync(member, Ct)).Fetched);
    }

    [Fact]
    public async Task TheManagersBranchIsTheTeamBranch()
    {
        var (f, manager, ada, _, _) = await PickedTeam();
        using (f)
        {
            Assert.Equal("team/alpha", (await f.Repos.PrepareAsync(manager, Ct)).Branch);
            Assert.Equal("members/alpha/ada", (await f.Repos.PrepareAsync(ada, Ct)).Branch);
        }
    }

    [Fact]
    public async Task WorkNotOnOriginIsPushed_OnceAndUnforced_AndTheBaseBranchNever()
    {
        var (f, manager, ada, picked, bare) = await PickedTeam();
        using (f)
        {
            var trunkBefore = AgentGit.Host(bare, "rev-parse", "refs/heads/trunk");
            var mcheck = await f.Repos.PrepareAsync(manager, Ct); // cloned from trunk as it was
            await Commit(f, ada, "a.txt");
            // A commit on the person's base branch in the team repository must never be published by the app.
            File.WriteAllText(Path.Combine(picked, "local.txt"), "x");
            AgentGit.Host(picked, "add", "local.txt");
            AgentGit.Host(picked, "-c", "user.name=p", "-c", "user.email=p@x", "commit", "-q", "-m", "local");

            var report = await f.Repos.PublishAsync(ada, Ct);
            Assert.Equal(TeamPublishResult.Published, report.Result);
            Assert.Equal(["members/alpha/ada"], report.Branches);
            Assert.Equal(AgentGit.Host(picked, "rev-parse", "refs/heads/members/alpha/ada"), AgentGit.Host(bare, "rev-parse", "refs/heads/members/alpha/ada"));
            Assert.Equal(trunkBefore, AgentGit.Host(bare, "rev-parse", "refs/heads/trunk"));

            Assert.Equal(TeamPublishResult.NothingToPublish, (await f.Repos.PublishAsync(ada, Ct)).Result);

            // The Manager fast-forwards the team branch to Ada's tip: no new commit, but origin's team branch moves.
            AgentGit.Ok(mcheck, "fetch", "-q", Path.Combine(f.Repos.SharedRepoPath("Alpha"), TeamRepos.SharedBundleFile), "members/alpha/ada");
            AgentGit.Ok(mcheck, "merge", "-q", "--ff-only", "FETCH_HEAD");
            Assert.True((await f.Repos.FetchBackAsync(manager, Ct)).Fetched);
            var team = await f.Repos.PublishAsync(manager, Ct);
            Assert.Equal(TeamPublishResult.Published, team.Result);
            Assert.Equal(["team/alpha"], team.Branches);
            Assert.Equal(AgentGit.Host(bare, "rev-parse", "refs/heads/members/alpha/ada"), AgentGit.Host(bare, "rev-parse", "refs/heads/team/alpha"));
        }
    }

    [Fact]
    public async Task ATeamBranchWithNoWorkOfItsOwn_IsNeitherBroughtBackNorPushed()
    {
        var (f, manager, _, picked, bare) = await PickedTeam();
        using (f)
        {
            // The Manager's clone is made from trunk as it is; then trunk moves on (on origin too).
            await f.Repos.PrepareAsync(manager, Ct);
            var old = AgentGit.Host(picked, "rev-parse", "refs/heads/trunk");
            File.WriteAllText(Path.Combine(picked, "later.txt"), "x");
            AgentGit.Host(picked, "add", "later.txt");
            AgentGit.Host(picked, "-c", "user.name=p", "-c", "user.email=p@x", "commit", "-q", "-m", "later");
            AgentGit.Host(picked, "push", "-q", "origin", "trunk");
            AgentGit.Host(picked, "fetch", "-q", "origin");

            // The Manager never committed: its branch is an older point of trunk, nothing to bring back...
            Assert.Equal(FetchResult.Nothing, await f.Repos.FetchBackAsync(manager, Ct));
            // ...and a team branch that is only that older point is not pushed either.
            AgentGit.Host(picked, "branch", "team/alpha", old);
            Assert.Equal(TeamPublishResult.NothingToPublish, (await f.Repos.PublishAsync(manager, Ct)).Result);
            Assert.DoesNotContain("team/alpha", AgentGit.Host(bare, "for-each-ref", "--format=%(refname)"));
        }
    }

    [Fact]
    public async Task ARewrittenCloneBranchIsMergedNeverForced_AndARefusedPushDoesNotStopTheOthers()
    {
        var (f, manager, ada, picked, bare) = await PickedTeam();
        using (f)
        {
            await Commit(f, ada, "a.txt");
            Assert.Equal(TeamPublishResult.Published, (await f.Repos.PublishAsync(ada, Ct)).Result);
            var pushed = AgentGit.Host(bare, "rev-parse", "refs/heads/members/alpha/ada");
            // Ada rewrites her pushed commit: the team repository keeps the pushed one (the rewrite is merged with it,
            // the branch is never moved backwards), so nothing on origin would need a force.
            var checkout = await f.Repos.PrepareAsync(ada, Ct);
            AgentGit.Ok(checkout, "commit", "-q", "--amend", "-m", "rewritten");
            Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
            Assert.Equal(0, AgentGit.Run(picked, Path.GetTempPath(), "merge-base", "--is-ancestor", pushed, "refs/heads/members/alpha/ada").ExitCode);
            // Someone else moves origin's members/alpha/ada meanwhile: Ada's push is refused (never forced); the Manager's goes.
            var elsewhere = Path.Combine(f.Db.Dir, "elsewhere");
            AgentGit.Host(f.Db.Dir, "clone", "-q", "-b", "members/alpha/ada", bare, elsewhere);
            TeamSyncTests.Commit(elsewhere, "other.txt", "o\n", "someone else's commit");
            AgentGit.Host(elsewhere, "push", "-q", "origin", "members/alpha/ada");
            await Commit(f, manager, "m.txt");

            var report = await f.Repos.PublishAsync(ada, Ct);
            Assert.Equal(TeamPublishResult.Rejected, report.Result);
            Assert.Equal(["members/alpha/ada"], report.Branches);
            Assert.Equal(["team/alpha"], report.Published);
            Assert.NotEqual("", AgentGit.Host(bare, "rev-parse", "refs/heads/team/alpha"));
        }
    }

    [Fact]
    public async Task AnUnreachableOriginIsAPushFailure_WithNoCredentialInTheReason()
    {
        var (f, _, ada, picked, _) = await PickedTeam();
        using (f)
        {
            AgentGit.Host(picked, "remote", "set-url", "origin", "https://user:s3cret@127.0.0.1:9/nowhere.git");
            await Commit(f, ada, "a.txt");
            var report = await f.Repos.PublishAsync(ada, Ct);
            Assert.Equal(TeamPublishResult.PushFailed, report.Result);
            Assert.Equal(["members/alpha/ada"], report.Branches);
            Assert.DoesNotContain("s3cret", report.Reason);
        }
    }

    [Fact]
    public async Task ATeamRepositoryWithoutOriginHasNothingToPublish()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        await Commit(f, ada, "a.txt"); // the managed repository: no origin
        Assert.Equal(TeamPublishResult.NoRepository, (await f.Repos.PublishAsync(ada, Ct)).Result);
    }
}
