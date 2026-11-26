using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The Git dialog's Delete and branch names: each name is resolved to its exact ref, ignoring case, before every guard
/// (base/protected, checked out, unmerged, another team's member on it), on the local side and on origin's. None needs
/// git's local transport: origin's side is a remote-tracking ref written with update-ref, origin itself is a path that
/// does not exist (a tried push fails at once), and clones are folders whose .git/HEAD is a file.
/// </summary>
public class TeamBranchDeleteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Refs(string repo) => AgentGit.Host(repo, "for-each-ref", "--format=%(refname) %(objectname)");

    private static string NewCommit(string repo, string parent, string message) =>
        AgentGit.Host(repo, "-c", "user.name=a", "-c", "user.email=a@x", "commit-tree", parent + "^{tree}", "-p", parent, "-m", message);

    /// <summary>A member's clone as the host sees it: a folder whose .git/HEAD names the branch. No recorded row.</summary>
    private static string CloneFolder(TeamsFixture f, MemberRef member, string team, string branch)
    {
        var clone = Path.Combine(f.Repos.MemberRoot(team, member.Name), "repo");
        Directory.CreateDirectory(Path.Combine(clone, ".git"));
        File.WriteAllText(Path.Combine(clone, ".git", "HEAD"), $"ref: refs/heads/{branch}\n");
        return clone;
    }

    /// <summary>The same folder, plus the member's recorded clone row.</summary>
    private static void FakeClone(TeamsFixture f, MemberRef member, string team, string branch)
    {
        var clone = CloneFolder(f, member, team, branch);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(f.Db.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO member_clones (member_id, path, branch, created_at) VALUES ($m, $p, $b, '2026-11-14T00:00:00Z');";
        cmd.Parameters.AddWithValue("$m", member.Id);
        cmd.Parameters.AddWithValue("$p", clone);
        cmd.Parameters.AddWithValue("$b", branch);
        Assert.Equal(1, cmd.ExecuteNonQuery());
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
        CloneFolder(f, ada, "Ada", "members/ada/ada");

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

        CloneFolder(f, ada, "Ada", "old/w");
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

    /// <summary>Teams Alpha and Ada on one repository the person picked (with an origin that does not exist).</summary>
    private static async Task<string> SharedRepoAsync(TeamsFixture f, long alphaId, long adaId)
    {
        var picked = Path.Combine(f.Db.Dir, "picked");
        Directory.CreateDirectory(picked);
        AgentGit.Host(picked, "init", "-q", "-b", "main");
        AgentGit.Host(picked, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "--allow-empty", "-m", "base");
        AgentGit.Host(picked, "remote", "add", "origin", Path.Combine(f.Db.Dir, "nowhere.git"));
        AgentGit.Host(picked, "update-ref", "refs/remotes/origin/main", "main");
        await f.Repos.UseExistingRepoAsync(alphaId, "Alpha", picked, Ct);
        await f.Repos.UseExistingRepoAsync(adaId, "Ada", picked, Ct);
        return picked;
    }

    /// <summary>
    /// Git on Windows (and macOS) finds a loose ref whatever its case: asked for Members/Ada, it reads and deletes
    /// members/ada. So the refusal of another team's in-use branch holds for a name in another case too:
    /// git.deleteBranches with "Members/Ada" (and unmergedOk "Members/Ada") from team Ada's dialog leaves team Alpha's
    /// in-use, unmerged members/ada. On a case-sensitive file system git finds no such branch either.
    /// </summary>
    [Fact]
    public async Task AnotherTeamsInUseOldBranch_IsNotDeletable_WhenNamedInAnotherCase()
    {
        using var f = new TeamsFixture();
        var (_, alphaAda, _) = f.Team("Alpha");
        var (adaManager, adaAda, adaBen) = f.Team("Ada");
        var picked = await SharedRepoAsync(f, alphaAda.TeamId, adaBen.TeamId);
        var work = NewCommit(picked, AgentGit.Host(picked, "rev-parse", "main"), "alpha ada's unmerged work");
        AgentGit.Host(picked, "update-ref", "refs/heads/members/ada", work);
        FakeClone(f, alphaAda, "Alpha", "members/ada");

        var teamAda = new[] { adaManager.Name, adaAda.Name, adaBen.Name };
        var plan = await f.Repos.GitDeleteBranchesAsync(adaBen.TeamId, "Ada", teamAda, null, ["Members/Ada"], false, ["Members/Ada"], null, Ct);
        if (!plan.Empty)
            await f.Repos.GitDeleteBranchesAsync(adaBen.TeamId, "Ada", teamAda, null, ["Members/Ada"], false, ["Members/Ada"], plan.Token, Ct);

        Assert.Contains($"refs/heads/members/ada {work}", Refs(picked));
        Assert.Contains("members/ada: REFUSED, team Alpha's member Ada is still on it", plan.Plan);
    }

    /// <summary>Two branches whose names differ only in case (one packed, one loose): a name matching both is
    /// refused as unclear and neither is deleted.</summary>
    [Fact]
    public async Task ANameMatchingTwoBranchesInAnotherCase_IsRefused()
    {
        using var f = new TeamsFixture();
        var (_, _, ben) = f.Team("Ada");
        var repo = await f.Repos.EnsureTeamRepoAsync(ben.TeamId, "Ada", Ct);
        var main = AgentGit.Host(repo.Path, "rev-parse", "main");
        AgentGit.Host(repo.Path, "update-ref", "refs/heads/old/x", main);
        AgentGit.Host(repo.Path, "pack-refs", "--all");
        AgentGit.Host(repo.Path, "update-ref", "refs/heads/Old/X", main);
        var before = Refs(repo.Path);
        Assert.Contains("refs/heads/old/x ", before);
        Assert.Contains("refs/heads/Old/X ", before);

        var plan = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Ada", [ben.Name], null, ["OLD/x"], false, [], null, Ct);

        Assert.Contains("OLD/x: REFUSED, more than one branch has this name in another case", plan.Plan);
        Assert.True(plan.Empty);
        Assert.Equal(before, Refs(repo.Path));
    }

    /// <summary>A same-team member still on its old members/ada, which only origin has
    /// (as of the last fetch): Delete with origin refuses it as checked out, and nothing is pushed.</summary>
    [Fact]
    public async Task ASameTeamMembersInUseOldBranch_OnOriginOnly_IsRefused()
    {
        using var f = new TeamsFixture();
        var (manager, ada, ben) = f.Team("Ada");
        var repo = await f.Repos.EnsureTeamRepoAsync(ben.TeamId, "Ada", Ct);
        AgentGit.Host(repo.Path, "remote", "add", "origin", Path.Combine(f.Db.Dir, "nowhere.git"));
        var main = AgentGit.Host(repo.Path, "rev-parse", "main");
        AgentGit.Host(repo.Path, "update-ref", "refs/remotes/origin/main", main);
        AgentGit.Host(repo.Path, "update-ref", "refs/remotes/origin/members/ada", main);
        FakeClone(f, ada, "Ada", "members/ada");

        var names = new[] { manager.Name, ada.Name, ben.Name };
        var plan = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Ada", names, null, ["Members/Ada"], true, [], null, Ct);

        Assert.Contains("members/ada", plan.Plan);
        Assert.Contains("REFUSED, checked out in", plan.Plan);
        Assert.DoesNotContain("delete origin/members/ada", plan.Plan);
        Assert.True(plan.Empty);
        Assert.Contains($"refs/remotes/origin/members/ada {main}", Refs(repo.Path));
    }

    /// <summary>Origin's side: another team's member's branch known only as origin's (as of the last fetch) is refused
    /// too when the Delete asks for origin, and nothing is pushed (a tried push would fail on the missing origin instead).</summary>
    [Fact]
    public async Task AnotherTeamsInUseBranch_OnOriginOnly_IsRefused()
    {
        using var f = new TeamsFixture();
        var (_, alphaAda, _) = f.Team("Alpha");
        var (adaManager, adaAda, adaBen) = f.Team("Ada");
        var picked = await SharedRepoAsync(f, alphaAda.TeamId, adaBen.TeamId);
        var main = AgentGit.Host(picked, "rev-parse", "main");
        AgentGit.Host(picked, "update-ref", "refs/remotes/origin/members/ada", main); // merged, so nothing else would keep it
        FakeClone(f, alphaAda, "Alpha", "members/ada");

        var teamAda = new[] { adaManager.Name, adaAda.Name, adaBen.Name };
        var plan = await f.Repos.GitDeleteBranchesAsync(adaBen.TeamId, "Ada", teamAda, null, ["members/ada"], true, ["members/ada"], null, Ct);

        Assert.Contains("members/ada: REFUSED, team Alpha's member Ada is still on it", plan.Plan);
        Assert.DoesNotContain("delete origin/members/ada", plan.Plan);
        Assert.True(plan.Empty);
        var run = await f.Repos.GitDeleteBranchesAsync(adaBen.TeamId, "Ada", teamAda, null, ["members/ada"], true, ["members/ada"], plan.Token, Ct);
        Assert.Equal("Nothing to delete.", run.Result);
        Assert.Contains($"refs/remotes/origin/members/ada {main}", Refs(picked));
    }
}
