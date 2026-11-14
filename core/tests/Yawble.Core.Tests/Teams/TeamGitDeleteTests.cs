using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// Card 75: the Git dialog's Delete (git.deleteBranches over ConciergeGit.PlanDeleteAsync, plan then confirm with the
/// plan's token) and git.status's oldBranches (old-style members/&lt;member&gt; branches here and/or on origin). Local
/// repositories and bare origins in temp folders only, no network.
/// </summary>
public class TeamGitDeleteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? Rev(string repo, string reference)
    {
        var r = AgentGit.Run(repo, Path.GetTempPath(), "rev-parse", "--verify", "-q", reference);
        return r.ExitCode == 0 ? r.Stdout : null;
    }

    private static string Commit(string clone, string home, string file)
    {
        File.WriteAllText(Path.Combine(clone, file), file);
        Assert.Equal(0, AgentGit.Run(clone, home, "add", file).ExitCode);
        Assert.Equal(0, AgentGit.Run(clone, home, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "-m", file).ExitCode);
        return AgentGit.Run(clone, home, "rev-parse", "HEAD").Stdout;
    }

    private static void SetRowBranch(TeamsFixture f, long memberId, string branch)
    {
        using var c = new SqliteConnection(f.Db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE member_clones SET branch = $b WHERE member_id = $m;";
        cmd.Parameters.AddWithValue("$b", branch);
        cmd.Parameters.AddWithValue("$m", memberId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    private static Task<TeamGitDelete> Delete(TeamsFixture f, MemberRef ada, string[] branches, bool remote, string? confirm = null, string[]? unmergedOk = null) =>
        f.Repos.GitDeleteBranchesAsync(ada.TeamId, "Alpha", ["Manager", "Ada", "Ben"], null, branches, remote, unmergedOk ?? [], confirm, Ct);

    [Fact]
    public async Task PlanThenConfirm_DeletesAMemberBranchLocallyAndOnOrigin_AndKeepsACheckedOutOne()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        await f.Repos.PrepareAsync(ada, Ct); // Ada's clone has members/alpha/ada checked out
        var trunk = AgentGit.Host(picked, "rev-parse", "trunk");
        AgentGit.Host(picked, "branch", "members/alpha/ben", trunk);
        AgentGit.Host(picked, "branch", "members/alpha/ada", trunk);
        AgentGit.Host(picked, "push", "-q", "origin", "members/alpha/ben");

        var plan = await Delete(f, ada, ["members/alpha/ben", "members/alpha/ada"], remote: true);
        Assert.False(plan.Empty);
        Assert.Null(plan.Result);
        Assert.Contains("delete members/alpha/ben at " + trunk, plan.Plan);
        Assert.Contains("delete origin/members/alpha/ben at " + trunk, plan.Plan);
        Assert.Contains("members/alpha/ada", plan.Plan);
        Assert.Contains("REFUSED, checked out", plan.Plan);
        // Planning changed nothing.
        Assert.Equal(trunk, Rev(picked, "refs/heads/members/alpha/ben"));
        Assert.Equal(trunk, Rev(bare, "refs/heads/members/alpha/ben"));

        var done = await Delete(f, ada, ["members/alpha/ben", "members/alpha/ada"], remote: true, confirm: plan.Token);
        Assert.False(done.Stale);
        Assert.Contains($"deleted members/alpha/ben; was {trunk}", done.Result);
        Assert.Contains($"deleted origin/members/alpha/ben; was {trunk}", done.Result);
        Assert.Null(Rev(picked, "refs/heads/members/alpha/ben"));
        Assert.Null(Rev(bare, "refs/heads/members/alpha/ben"));
        Assert.Equal(trunk, Rev(picked, "refs/heads/members/alpha/ada")); // checked out: never deleted
        var log = File.ReadAllText(Path.Combine(picked, ".git", Yawble.Core.Concierge.ConciergeGit.DeletedLog));
        Assert.Contains($"deleted members/alpha/ben {trunk}", log);
        Assert.Contains($"deleted origin/members/alpha/ben {trunk}", log);
    }

    [Fact]
    public async Task ABranchThatChangedSinceThePlan_IsStale_AndNothingIsDeleted()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (picked, _) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        var trunk = AgentGit.Host(picked, "rev-parse", "trunk");
        AgentGit.Host(picked, "branch", "members/alpha/ben", trunk);
        var plan = await Delete(f, ada, ["members/alpha/ben"], remote: false);

        // Ben's branch moves on (another commit fetched back, say) before the person confirms.
        var tree = AgentGit.Host(picked, "rev-parse", "trunk^{tree}");
        var next = AgentGit.Host(picked, "-c", "user.name=b", "-c", "user.email=b@x", "commit-tree", tree, "-p", trunk, "-m", "more");
        AgentGit.Host(picked, "update-ref", "refs/heads/members/alpha/ben", next);

        var again = await Delete(f, ada, ["members/alpha/ben"], remote: false, confirm: plan.Token);
        Assert.True(again.Stale);
        Assert.Null(again.Result);
        Assert.Equal(next, Rev(picked, "refs/heads/members/alpha/ben"));
        Assert.NotEqual(plan.Token, again.Token);
    }

    [Fact]
    public async Task AnOldStyleBranchOnlyOnOrigin_IsListedInOldBranches_AndDeletesWithRemote()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        Assert.Empty((await f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct)).OldBranches);

        // Ada before B016: on members/ada, her work fetched back and pushed there.
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        Assert.Equal(0, AgentGit.Run(checkout.ClonePath, checkout.Workspace, "branch", "-m", "members/ada").ExitCode);
        SetRowBranch(f, ada.Id, "members/ada");
        var tip = Commit(checkout.ClonePath, checkout.Workspace, "old.txt");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        Assert.Equal(["members/ada"], (await f.Repos.PublishAsync(ada, Ct)).Branches);

        // The migration moves her to members/alpha/ada; the old branch stays. The person deletes the local one first.
        Assert.Equal("members/alpha/ada", (await f.Repos.PrepareAsync(ada, Ct)).Branch);
        Assert.Equal(["members/alpha/ada"], (await f.Repos.PublishAsync(ada, Ct)).Branches);
        var both = Assert.Single((await f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct)).OldBranches);
        Assert.Equal(new TeamGitOldBranch("members/ada", true, true, false), both);
        AgentGit.Host(picked, "branch", "-D", "members/ada");
        Assert.Equal(tip, Rev(bare, "refs/heads/members/ada"));

        var status = await f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct);
        Assert.Equal(new TeamGitOldBranch("members/ada", Local: false, Remote: true, Merged: false), Assert.Single(status.OldBranches));
        Assert.DoesNotContain(status.OldBranches, b => b.Name == "members/alpha/ada");

        // Unmerged: kept unless named in unmergedOk.
        var kept = await Delete(f, ada, ["members/ada"], remote: true);
        Assert.True(kept.Empty);
        Assert.Contains("origin/members/ada", kept.Plan);
        Assert.Contains("KEPT", kept.Plan);

        var plan = await Delete(f, ada, ["members/ada"], remote: true, unmergedOk: ["members/ada"]);
        Assert.False(plan.Empty);
        var done = await Delete(f, ada, ["members/ada"], remote: true, confirm: plan.Token, unmergedOk: ["members/ada"]);
        Assert.Contains($"deleted origin/members/ada; was {tip}", done.Result);
        Assert.Null(Rev(bare, "refs/heads/members/ada"));
        Assert.Equal(tip, Rev(bare, "refs/heads/members/alpha/ada"));
        Assert.Empty((await f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct)).OldBranches);
    }
}
