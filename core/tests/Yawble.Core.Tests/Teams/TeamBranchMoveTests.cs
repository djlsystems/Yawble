using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// B016: member branches carry the team (members/&lt;team&gt;/&lt;name&gt;), and a member still on an old-style
/// members/&lt;name&gt; moves to the new name at its next start, with its commits; the old branch is never deleted or
/// forced. Local repositories and bare origins in temp folders only, no network.
/// </summary>
public class TeamBranchMoveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SetRowBranch(TeamsFixture f, string table, long memberId, string branch)
    {
        using var c = new SqliteConnection(f.Db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"UPDATE {table} SET branch = $b WHERE member_id = $m;";
        cmd.Parameters.AddWithValue("$b", branch);
        cmd.Parameters.AddWithValue("$m", memberId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    private static string Commit(string clone, string home, string file)
    {
        File.WriteAllText(Path.Combine(clone, file), file);
        Ok(clone, home, "add", file);
        Ok(clone, home, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "-m", file);
        return Ok(clone, home, "rev-parse", "HEAD");
    }

    private static string Ok(string cwd, string home, params string[] args)
    {
        var r = AgentGit.Run(cwd, home, args);
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)}: {r.Stderr}");
        return r.Stdout;
    }

    private static string? Rev(string repo, string reference)
    {
        var r = AgentGit.Run(repo, Path.GetTempPath(), "rev-parse", "--verify", "-q", reference);
        return r.ExitCode == 0 ? r.Stdout : null;
    }

    /// <summary>Puts a freshly prepared member back as a pre-B016 member: its clone and its record on members/&lt;name&gt;.</summary>
    private static string MakeOldStyle(TeamsFixture f, MemberRef member, MemberCheckout checkout)
    {
        var old = TeamRepos.LegacyBranchFor(member.Name);
        Ok(checkout.ClonePath, checkout.Workspace, "branch", "-m", old);
        SetRowBranch(f, "member_clones", member.Id, old);
        return old;
    }

    [Fact]
    public async Task TwoTeamsOnOneOrigin_WithASameNamedMember_PushToDifferentBranches()
    {
        using var f = new TeamsFixture();
        var (_, alphaAda, _) = f.Team("Alpha");
        var (_, betaAda, _) = f.Team("Beta");
        var (alphaRepo, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        var betaRepo = Path.Combine(f.Db.Dir, "beta-repo");
        AgentGit.Host(f.Db.Dir, "clone", "-q", "-b", "trunk", bare, betaRepo);
        await f.Repos.UseExistingRepoAsync(alphaAda.TeamId, "Alpha", alphaRepo, Ct);
        await f.Repos.UseExistingRepoAsync(betaAda.TeamId, "Beta", betaRepo, Ct);

        var a = await f.Repos.PrepareAsync(alphaAda, Ct);
        var b = await f.Repos.PrepareAsync(betaAda, Ct);
        Assert.Equal("members/alpha/ada", a.Branch);
        Assert.Equal("members/beta/ada", b.Branch);
        var aTip = Commit(a.ClonePath, a.Workspace, "alpha.txt");
        var bTip = Commit(b.ClonePath, b.Workspace, "beta.txt");
        Assert.True((await f.Repos.FetchBackAsync(alphaAda, Ct)).Fetched);
        Assert.True((await f.Repos.FetchBackAsync(betaAda, Ct)).Fetched);

        var ra = await f.Repos.PublishAsync(alphaAda, Ct);
        var rb = await f.Repos.PublishAsync(betaAda, Ct);
        Assert.Equal(TeamPublishResult.Published, ra.Result);
        Assert.Equal(TeamPublishResult.Published, rb.Result);
        Assert.Equal(["members/alpha/ada"], ra.Branches);
        Assert.Equal(["members/beta/ada"], rb.Branches);
        // Both went, neither replaced the other.
        Assert.Equal(aTip, AgentGit.Host(bare, "rev-parse", "refs/heads/members/alpha/ada"));
        Assert.Equal(bTip, AgentGit.Host(bare, "rev-parse", "refs/heads/members/beta/ada"));
        Assert.Equal("alpha.txt", AgentGit.Host(bare, "show", "members/alpha/ada:alpha.txt"));
        Assert.Equal("beta.txt", AgentGit.Host(bare, "show", "members/beta/ada:beta.txt"));
    }

    [Fact]
    public async Task TheGitDialogAndTheSync_TakeOnlyTheirOwnTeamsMemberBranches()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Alpha");
        var (repo, _) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", repo, Ct);
        var a = await f.Repos.PrepareAsync(ada, Ct);
        Commit(a.ClonePath, a.Workspace, "a.txt");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        // Another team picked the same folder: its member's branch is in it too. And a branch an old-style member left.
        var start = AgentGit.Host(repo, "rev-parse", "trunk");
        AgentGit.Host(repo, "branch", "members/beta/ada", start);
        AgentGit.Host(repo, "branch", "members/ada", start);

        var s = await f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct);
        Assert.Equal(["members/alpha/ada"], s.Members.Select(m => m.Branch)); // members/ada: nobody is on it, so only in OldBranches
        Assert.Equal(["members/ada"], s.OldBranches.Select(o => o.Name));

        File.WriteAllText(Path.Combine(repo, "later.txt"), "x");
        AgentGit.Host(repo, "add", "later.txt");
        AgentGit.Host(repo, "-c", "user.name=p", "-c", "user.email=p@x", "commit", "-q", "-m", "later");
        var r = await f.Repos.SyncNowAsync(ada.TeamId, "Alpha", Ct);
        Assert.Contains("members/alpha/ada", r.Moved);
        Assert.Equal(start, AgentGit.Host(repo, "rev-parse", "members/beta/ada")); // another team's: not touched
        Assert.Equal(start, AgentGit.Host(repo, "rev-parse", "members/ada")); // left behind by nobody here: not touched
    }

    [Fact]
    public async Task AnOldStyleMember_MovesToTheNewName_WithItsCommits_AndTheOldBranchIsKept()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        var old = MakeOldStyle(f, ada, checkout);

        // Ada's pre-B016 life: work fetched back and pushed on members/ada; then one more commit in the clone.
        var fetched = Commit(checkout.ClonePath, checkout.Workspace, "old.txt");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        Assert.Equal(["members/ada"], (await f.Repos.PublishAsync(ada, Ct)).Branches);
        var cloneTip = Commit(checkout.ClonePath, checkout.Workspace, "later.txt");
        File.WriteAllText(Path.Combine(checkout.ClonePath, "wip.txt"), "uncommitted"); // the working tree is never touched

        var moved = await f.Repos.PrepareAsync(ada, Ct);

        Assert.Equal("members/alpha/ada", moved.Branch);
        Assert.Equal("members/alpha/ada", f.Repos.Row(ada.Id)!.Branch);
        Assert.Equal("members/alpha/ada", Ok(moved.ClonePath, moved.Workspace, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(cloneTip, Ok(moved.ClonePath, moved.Workspace, "rev-parse", "refs/heads/members/alpha/ada"));
        Assert.Equal(cloneTip, Ok(moved.ClonePath, moved.Workspace, "rev-parse", "refs/heads/" + old)); // kept in the clone
        Assert.Equal("?? wip.txt", Ok(moved.ClonePath, moved.Workspace, "status", "--porcelain"));
        Assert.Equal("uncommitted", File.ReadAllText(Path.Combine(moved.ClonePath, "wip.txt")));
        // The team repository: the new branch at the old one's commit; the old one kept.
        Assert.Equal(fetched, Rev(picked, "refs/heads/members/alpha/ada"));
        Assert.Equal(fetched, Rev(picked, "refs/heads/" + old));
        Assert.Contains("refs/heads/members/alpha/ada", AgentGit.Host(picked, "bundle", "list-heads",
            Path.Combine(f.Repos.SharedRepoPath("Alpha"), TeamRepos.SharedBundleFile)));
        Assert.Contains(f.Log, l => l.Contains("moved from members/ada to members/alpha/ada", StringComparison.Ordinal));

        // Pushed as usual although it has no commit origin lacks; origin's old branch is neither deleted nor moved.
        var oldOnOrigin = AgentGit.Host(bare, "rev-parse", "refs/heads/" + old);
        var report = await f.Repos.PublishAsync(ada, Ct);
        Assert.Equal(TeamPublishResult.Published, report.Result);
        Assert.Equal(["members/alpha/ada"], report.Branches);
        Assert.Equal(fetched, AgentGit.Host(bare, "rev-parse", "refs/heads/members/alpha/ada"));

        // The clone's later commit comes back on the new name; the old one stays where it was everywhere.
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        Assert.Equal(cloneTip, Rev(picked, "refs/heads/members/alpha/ada"));
        Assert.Equal(fetched, Rev(picked, "refs/heads/" + old));
        Assert.Equal(["members/alpha/ada"], (await f.Repos.PublishAsync(ada, Ct)).Branches);
        Assert.Equal(cloneTip, AgentGit.Host(bare, "rev-parse", "refs/heads/members/alpha/ada"));
        Assert.Equal(oldOnOrigin, AgentGit.Host(bare, "rev-parse", "refs/heads/" + old));

        // The Git dialog lists both, each once: the new one as a member branch, the old one as old (the person deletes
        // it); a sync no longer moves the one left behind.
        var s = await f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct);
        Assert.Equal(["members/alpha/ada"], s.Members.Select(m => m.Branch));
        Assert.Equal(["members/ada"], s.OldBranches.Select(o => o.Name));
        File.WriteAllText(Path.Combine(picked, "base.txt"), "x");
        AgentGit.Host(picked, "add", "base.txt");
        AgentGit.Host(picked, "-c", "user.name=p", "-c", "user.email=p@x", "commit", "-q", "-m", "base");
        var sync = await f.Repos.SyncNowAsync(ada.TeamId, "Alpha", Ct);
        Assert.Contains("members/alpha/ada", sync.Moved);
        Assert.DoesNotContain(old, sync.Moved);
        Assert.Equal(fetched, Rev(picked, "refs/heads/" + old));

        // A second start changes nothing more.
        Assert.Equal("members/alpha/ada", (await f.Repos.PrepareAsync(ada, Ct)).Branch);
    }

    [Fact]
    public async Task AnOldStyleMemberWithNoFetchedWork_MovesItsClone_AndTheNextFetchBackMakesTheBranch()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        var old = MakeOldStyle(f, ada, checkout);
        var tip = Commit(checkout.ClonePath, checkout.Workspace, "w.txt");
        var repo = f.Repos.RepoOf(ada.TeamId)!.Path;
        Assert.Null(Rev(repo, "refs/heads/" + old)); // never fetched back

        var moved = await f.Repos.PrepareAsync(ada, Ct);
        Assert.Equal("members/alpha/ada", Ok(moved.ClonePath, moved.Workspace, "symbolic-ref", "--short", "HEAD"));
        Assert.Null(Rev(repo, "refs/heads/members/alpha/ada"));
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        Assert.Equal(tip, Rev(repo, "refs/heads/members/alpha/ada"));
        Assert.Null(Rev(repo, "refs/heads/" + old));
    }

    [Fact]
    public async Task AnOldStyleExtraClone_MovesToo()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var dir = Path.Combine(f.Db.Dir, "cloud");
        Directory.CreateDirectory(dir);
        var (cloud, cloudOrigin) = TeamReposTests.PersonsRepoWithOrigin(dir);
        await f.Repos.AddExtraAsync(ada.TeamId, "Alpha", "cloud", cloud, null, writable: true, Ct);
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        var extra = Assert.Single(checkout.Extras);
        MakeOldStyle(f, ada, checkout);
        Ok(extra.Path, checkout.Workspace, "branch", "-m", "members/ada");
        SetRowBranch(f, "member_extra_clones", ada.Id, "members/ada");
        var tip = Commit(extra.Path, checkout.Workspace, "api.txt");
        Assert.Null((await f.Repos.FetchBackAsync(ada, Ct)).Error);
        Assert.Equal(tip, Rev(cloud, "refs/heads/members/ada"));

        var moved = await f.Repos.PrepareAsync(ada, Ct);
        var movedExtra = Assert.Single(moved.Extras);
        Assert.Equal("members/alpha/ada", movedExtra.Branch);
        Assert.Equal("members/alpha/ada", f.Repos.ExtraRow(ada.Id, "cloud")!.Branch);
        Assert.Equal("members/alpha/ada", Ok(extra.Path, checkout.Workspace, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(tip, Rev(cloud, "refs/heads/members/alpha/ada"));
        Assert.Equal(tip, Rev(cloud, "refs/heads/members/ada"));
        var report = await f.Repos.PublishAsync(ada, Ct);
        Assert.Contains("cloud: members/alpha/ada", report.Branches);
        Assert.Equal(tip, AgentGit.Host(cloudOrigin, "rev-parse", "refs/heads/members/alpha/ada"));
    }

    [Fact]
    public async Task AMemberNamedLikeItsTeam_MovesThroughItsOwnOldBranch()
    {
        // Team "Ada" with a member "Ada": members/ada/ada cannot exist beside members/ada, so (B020) the member's own old
        // branch is moved through: members/ada becomes members/ada/ada, its work and HEAD with it.
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Ada");
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        MakeOldStyle(f, ada, checkout);
        var tip = Commit(checkout.ClonePath, checkout.Workspace, "w.txt");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);

        var again = await f.Repos.PrepareAsync(ada, Ct);
        Assert.Equal("members/ada/ada", again.Branch);
        Assert.Equal("members/ada/ada", Ok(again.ClonePath, again.Workspace, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(tip, Ok(again.ClonePath, again.Workspace, "rev-parse", "HEAD"));
    }
}
