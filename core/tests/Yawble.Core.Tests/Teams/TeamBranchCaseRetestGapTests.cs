using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// B020 re-test / card 107 (tester's gap tests on card 106's F4 fix: Delete resolves each name to its exact ref, ignoring
/// case, before every guard). None needs git's local transport: origin's side is a remote-tracking ref written with
/// update-ref, origin itself is a path that does not exist (a tried push fails at once), and clones are folders whose
/// .git/HEAD is a file.
/// </summary>
public class TeamBranchCaseRetestGapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Refs(string repo) => AgentGit.Host(repo, "for-each-ref", "--format=%(refname) %(objectname)");

    private static string NewCommit(string repo, string parent, string message) =>
        AgentGit.Host(repo, "-c", "user.name=a", "-c", "user.email=a@x", "commit-tree", parent + "^{tree}", "-p", parent, "-m", message);

    private static void FakeClone(TeamsFixture f, MemberRef member, string team, string branch)
    {
        var clone = Path.Combine(f.Repos.MemberRoot(team, member.Name), "repo");
        Directory.CreateDirectory(Path.Combine(clone, ".git"));
        File.WriteAllText(Path.Combine(clone, ".git", "HEAD"), $"ref: refs/heads/{branch}\n");
    }

    private static async Task<(TeamRepo Repo, string Main, string[] Names, MemberRef Ada, MemberRef Ben)> RepoAsync(TeamsFixture f, bool origin = false)
    {
        var (manager, ada, ben) = f.Team("Ada");
        var repo = await f.Repos.EnsureTeamRepoAsync(ben.TeamId, "Ada", Ct);
        var main = AgentGit.Host(repo.Path, "rev-parse", "main");
        if (origin)
        {
            AgentGit.Host(repo.Path, "remote", "add", "origin", Path.Combine(f.Db.Dir, "nowhere.git"));
            AgentGit.Host(repo.Path, "update-ref", "refs/remotes/origin/main", main);
        }
        return (repo, main, [manager.Name, ada.Name, ben.Name], ada, ben);
    }

    /// <summary>The base/protected guard ignores case: "MAIN" and "Head" are refused as protected, nothing changes.</summary>
    [Fact]
    public async Task TheBaseBranch_NamedInAnotherCase_IsRefusedAsProtected()
    {
        using var f = new TeamsFixture();
        var (repo, _, names, _, ben) = await RepoAsync(f);
        var before = Refs(repo.Path);

        var plan = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Ada", names, null, ["MAIN"], false, ["MAIN"], null, Ct);

        Assert.Contains("main: REFUSED, it is the base/protected branch", plan.Plan);
        Assert.True(plan.Empty);
        Assert.Equal(before, Refs(repo.Path));
    }

    /// <summary>The checked-out guard ignores case for a same-team member's clone, locally: "Members/Ada/ADA" is refused while
    /// Ada's clone is on members/ada/ada.</summary>
    [Fact]
    public async Task ASameTeamMembersCheckedOutBranch_NamedInAnotherCase_IsRefused()
    {
        using var f = new TeamsFixture();
        var (repo, main, names, ada, ben) = await RepoAsync(f);
        var work = NewCommit(repo.Path, main, "ada's work");
        AgentGit.Host(repo.Path, "update-ref", "refs/heads/members/ada/ada", work);
        FakeClone(f, ada, "Ada", "members/ada/ada");

        var plan = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Ada", names, null, ["Members/Ada/ADA"], false, ["Members/Ada/ADA"], null, Ct);

        Assert.Contains("members/ada/ada", plan.Plan);
        Assert.Contains("REFUSED, checked out in", plan.Plan);
        Assert.True(plan.Empty);
        Assert.Contains($"refs/heads/members/ada/ada {work}", Refs(repo.Path));
    }

    /// <summary>unmergedOk ignores case, and the plan, its token and the delete use the exact ref name: "Old/Y" with
    /// unmergedOk "OLD/Y" deletes old/y and the result names old/y.</summary>
    [Fact]
    public async Task AnUnmergedBranch_TickedInAnotherCase_IsDeletedByItsExactName()
    {
        using var f = new TeamsFixture();
        var (repo, main, names, _, ben) = await RepoAsync(f);
        var work = NewCommit(repo.Path, main, "unmerged");
        AgentGit.Host(repo.Path, "update-ref", "refs/heads/old/y", work);

        var plan = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Ada", names, null, ["Old/Y"], false, ["OLD/Y"], null, Ct);
        Assert.Contains($"delete old/y at {work} (UNMERGED, you named it)", plan.Plan);
        Assert.Contains($"L old/y {work}", plan.Token);

        var done = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Ada", names, null, ["Old/Y"], false, ["OLD/Y"], plan.Token, Ct);
        Assert.False(done.Stale);
        Assert.Contains($"deleted old/y; was {work}", done.Result);
        Assert.DoesNotContain("refs/heads/old/y ", Refs(repo.Path));
    }

    /// <summary>Not ticked in any case: an unmerged branch named in another case is KEPT under its exact name.</summary>
    [Fact]
    public async Task AnUnmergedBranch_NamedInAnotherCase_WithoutTheTick_IsKept()
    {
        using var f = new TeamsFixture();
        var (repo, main, names, _, ben) = await RepoAsync(f);
        var work = NewCommit(repo.Path, main, "unmerged");
        AgentGit.Host(repo.Path, "update-ref", "refs/heads/old/y", work);

        var plan = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Ada", names, null, ["Old/Y"], false, [], null, Ct);

        Assert.Contains("old/y", plan.Plan);
        Assert.Contains("KEPT, not merged", plan.Plan);
        Assert.True(plan.Empty);
        Assert.Contains($"refs/heads/old/y {work}", Refs(repo.Path));
    }

    /// <summary>A name no branch has in any case keeps the "no such branch" refusal (locally and with origin).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnknownName_IsRefusedAsNoSuchBranch(bool remote)
    {
        using var f = new TeamsFixture();
        var (repo, _, names, _, ben) = await RepoAsync(f, origin: true);
        var before = Refs(repo.Path);

        var plan = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Ada", names, null, ["Gone/Z"], remote, [], null, Ct);

        Assert.Contains("Gone/Z: REFUSED, no such branch", plan.Plan);
        Assert.True(plan.Empty);
        Assert.Equal(before, Refs(repo.Path));
    }

    /// <summary>The origin-side checked-out refusal holds at the yes too: planned while nobody was on origin's old/w, a
    /// same-team clone checks it out before the confirm; the confirm is Stale and nothing is pushed or changed.</summary>
    [Fact]
    public async Task AnOriginOnlyBranch_CheckedOutBetweenThePlanAndTheYes_IsNotDeleted()
    {
        using var f = new TeamsFixture();
        var (repo, main, names, ada, ben) = await RepoAsync(f, origin: true);
        AgentGit.Host(repo.Path, "update-ref", "refs/remotes/origin/old/w", main);

        var plan = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Ada", names, null, ["Old/W"], true, [], null, Ct);
        Assert.Contains($"delete origin/old/w at {main}", plan.Plan);
        Assert.False(plan.Empty);
        var before = Refs(repo.Path);

        FakeClone(f, ada, "Ada", "old/w");
        var done = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Ada", names, null, ["Old/W"], true, [], plan.Token, Ct);

        Assert.True(done.Stale);
        Assert.Null(done.Result);
        Assert.Contains("REFUSED, checked out in", done.Plan);
        Assert.Equal(before, Refs(repo.Path));
    }

    /// <summary>Local feat/a and origin's Feat/A differ only in case: with origin the name is unclear and refused; without
    /// origin only the local one counts and it resolves to feat/a.</summary>
    [Fact]
    public async Task ALocalAndAnOriginBranchDifferingOnlyInCase_AreRefusedTogether_ButTheLocalOneResolvesAlone()
    {
        using var f = new TeamsFixture();
        var (repo, main, names, _, ben) = await RepoAsync(f, origin: true);
        AgentGit.Host(repo.Path, "update-ref", "refs/heads/feat/a", main);
        AgentGit.Host(repo.Path, "update-ref", "refs/remotes/origin/Feat/A", main);
        var before = Refs(repo.Path);

        var withOrigin = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Ada", names, null, ["feat/a"], true, [], null, Ct);
        Assert.Contains("feat/a: REFUSED, more than one branch has this name in another case", withOrigin.Plan);
        Assert.True(withOrigin.Empty);
        Assert.Equal(before, Refs(repo.Path));

        var local = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Ada", names, null, ["FEAT/A"], false, [], null, Ct);
        Assert.Contains($"delete feat/a at {main} (merged)", local.Plan);
        Assert.DoesNotContain("delete origin/", local.Plan);
    }
}
