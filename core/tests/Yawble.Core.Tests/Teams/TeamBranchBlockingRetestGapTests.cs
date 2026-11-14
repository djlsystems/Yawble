using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// B020 re-test / card 105 (tester's gap tests on card 104's fix). Like <see cref="TeamBranchBlockingGapTests"/>, none of
/// these needs git's local transport: origin's side is a remote-tracking ref written with update-ref and origin itself is
/// a path that does not exist, so a push that is tried fails at once and plainly; clones are folders whose .git/HEAD is a file.
/// </summary>
public class TeamBranchBlockingRetestGapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Refs(string repo) => AgentGit.Host(repo, "for-each-ref", "--format=%(refname) %(objectname)");

    private static string NewCommit(string repo, string parent, string message) =>
        AgentGit.Host(repo, "-c", "user.name=a", "-c", "user.email=a@x", "commit-tree", parent + "^{tree}", "-p", parent, "-m", message);

    private static MemberRef AddMember(TeamsFixture f, long teamId, string name)
    {
        using var store = f.Db.OpenStore();
        var m = store.AddMember(teamId, name, MemberRole.Member, "claude");
        store.Subscribe(m.Qualified, MessageTypes.Instruction(m));
        store.InitCursor(m.Qualified);
        return m;
    }

    private static string FakeClone(TeamsFixture f, MemberRef member, string team, string branch)
    {
        var clone = Path.Combine(f.Repos.MemberRoot(team, member.Name), "repo");
        Directory.CreateDirectory(Path.Combine(clone, ".git"));
        File.WriteAllText(Path.Combine(clone, ".git", "HEAD"), $"ref: refs/heads/{branch}\n");
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(f.Db.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO member_clones (member_id, path, branch, created_at) VALUES ($m, $p, $b, '2026-11-14T00:00:00Z');";
        cmd.Parameters.AddWithValue("$m", member.Id);
        cmd.Parameters.AddWithValue("$p", clone);
        cmd.Parameters.AddWithValue("$b", branch);
        Assert.Equal(1, cmd.ExecuteNonQuery());
        return clone;
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
    /// FAULT F4 (card 105). The F1 refusal compares branch names exactly, but git on Windows (and macOS) finds a loose ref
    /// whatever its case: asked for Members/Ada, it reads and deletes members/ada. The checked-out guard compares exactly
    /// too. So git.deleteBranches with "Members/Ada" (and unmergedOk "Members/Ada") deletes team Alpha's in-use, unmerged
    /// members/ada from team Ada's dialog. On a case-sensitive file system git finds no such branch and this passes.
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

    /// <summary>Card 106: two branches whose names differ only in case (one packed, one loose): a name matching both is
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

    /// <summary>Card 106 (Wren's note on card 105): a same-team member still on its old members/ada, which only origin has
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

    /// <summary>F1 on origin's side: another team's member's branch known only as origin's (as of the last fetch) is refused
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

    /// <summary>Team "Docs", member "Docs", already moved to members/docs/docs; the repository has an origin.</summary>
    private static async Task<(TeamsFixture F, MemberRef Docs, string Repo, string Work)> MovedDocsAsync()
    {
        var f = new TeamsFixture();
        var (_, _, ben) = f.Team("Docs");
        var docs = AddMember(f, ben.TeamId, "Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(ben.TeamId, "Docs", Ct);
        AgentGit.Host(repo.Path, "remote", "add", "origin", Path.Combine(f.Db.Dir, "nowhere.git"));
        var main = AgentGit.Host(repo.Path, "rev-parse", "main");
        AgentGit.Host(repo.Path, "update-ref", "refs/remotes/origin/main", main);
        var work = NewCommit(repo.Path, main, "docs's work");
        AgentGit.Host(repo.Path, "update-ref", "refs/heads/members/docs/docs", work);
        FakeClone(f, docs, "Docs", "members/docs/docs");
        return (f, docs, repo.Path, work);
    }

    /// <summary>F2's push, after review: origin's old members/docs, even one the new branch holds, is never deleted by the
    /// app (a name alone does not prove whose it is). No delete is tried; the report says plainly to delete it in the Git
    /// dialog, and the new branch is not pushed.</summary>
    [Fact]
    public async Task TheOwnOldBranchOnOrigin_IsNeverDeletedByTheApp_AndThePushWaitsForThePerson()
    {
        var (f, docs, repo, work) = await MovedDocsAsync();
        using var _ = f;
        AgentGit.Host(repo, "update-ref", "refs/remotes/origin/members/docs", work);

        var report = await f.Repos.PublishAsync(docs, Ct);

        Assert.Equal(TeamPublishResult.Rejected, report.Result);
        Assert.Contains("members/docs/docs", report.Branches);
        Assert.DoesNotContain("refused deleting", report.Reason); // no delete was tried
        Assert.Contains("Delete members/docs on origin in the Git dialog", report.Reason);
        Assert.DoesNotContain("cannot lock ref", report.Reason);
        Assert.Contains($"refs/remotes/origin/members/docs {work}", Refs(repo)); // nothing changed on our side
    }

    /// <summary>F2's push: origin's members/docs has a commit the new branch does not: it is not deleted (no push tried),
    /// and the report is the plain blocked one.</summary>
    [Fact]
    public async Task TheOwnOldBranchOnOrigin_IsNotReplaced_WhenItHasCommitsTheNewOneLacks()
    {
        var (f, docs, repo, work) = await MovedDocsAsync();
        using var _ = f;
        AgentGit.Host(repo, "update-ref", "refs/remotes/origin/members/docs", NewCommit(repo, work, "pushed from elsewhere"));

        var report = await f.Repos.PublishAsync(docs, Ct);

        Assert.Equal(TeamPublishResult.Rejected, report.Result);
        Assert.Contains("origin has a branch members/docs", report.Reason);
        Assert.DoesNotContain("refused deleting", report.Reason); // no delete was tried
    }

    /// <summary>F2's push: another team's member named Docs is still on members/docs (same repository): origin's copy is
    /// never deleted for team Docs, even when the new branch holds it, and the report names whose it is.</summary>
    [Fact]
    public async Task TheOwnOldBranchOnOrigin_IsNotReplaced_WhenAnotherTeamsMemberIsOnIt()
    {
        using var f = new TeamsFixture();
        var (_, alphaAda, _) = f.Team("Alpha");
        var (_, _, ben) = f.Team("Docs");
        var picked = Path.Combine(f.Db.Dir, "picked");
        Directory.CreateDirectory(picked);
        AgentGit.Host(picked, "init", "-q", "-b", "main");
        AgentGit.Host(picked, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "--allow-empty", "-m", "base");
        AgentGit.Host(picked, "remote", "add", "origin", Path.Combine(f.Db.Dir, "nowhere.git"));
        var main = AgentGit.Host(picked, "rev-parse", "main");
        AgentGit.Host(picked, "update-ref", "refs/remotes/origin/main", main);
        await f.Repos.UseExistingRepoAsync(alphaAda.TeamId, "Alpha", picked, Ct);
        await f.Repos.UseExistingRepoAsync(ben.TeamId, "Docs", picked, Ct);
        var alphaDocs = AddMember(f, alphaAda.TeamId, "Docs");
        FakeClone(f, alphaDocs, "Alpha", "members/docs");
        var docs = AddMember(f, ben.TeamId, "Docs");
        var work = NewCommit(picked, main, "docs's work");
        AgentGit.Host(picked, "update-ref", "refs/heads/members/docs/docs", work);
        AgentGit.Host(picked, "update-ref", "refs/remotes/origin/members/docs", work);
        FakeClone(f, docs, "Docs", "members/docs/docs");

        var report = await f.Repos.PublishAsync(docs, Ct);

        Assert.Equal(TeamPublishResult.Rejected, report.Result);
        Assert.Contains("team Alpha's member Docs", report.Reason);
        Assert.DoesNotContain("refused deleting", report.Reason); // no delete was tried
    }

    private static string NewRepo(string branch)
    {
        var dir = Path.Combine(Path.GetTempPath(), "yb105-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        AgentGit.Host(dir, "init", "-q", "-b", branch);
        AgentGit.Host(dir, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "--allow-empty", "-m", "base");
        return dir;
    }

    private static void Drop(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The clone's old branch both loose (newer) and in packed-refs (older): both go, so the packed line cannot
    /// bring members/docs back at the old commit (which would block the new branch again).</summary>
    [Fact]
    public void TheClonesOldBranch_LooseAndPacked_LeavesNoStalePackedLine()
    {
        var dir = NewRepo("members/docs");
        try
        {
            AgentGit.Host(dir, "pack-refs", "--all");
            AgentGit.Host(dir, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "--allow-empty", "-m", "newer");
            var tip = AgentGit.Host(dir, "rev-parse", "HEAD");
            var gitDir = Path.Combine(dir, ".git");
            Assert.True(File.Exists(Path.Combine(gitDir, "refs", "heads", "members", "docs")));
            Assert.Contains("refs/heads/members/docs", File.ReadAllText(Path.Combine(gitDir, "packed-refs")));

            Assert.Null(TeamRepos.MoveCloneRefThrough(gitDir, "members/docs", "members/docs/docs", tip));

            Assert.DoesNotContain("refs/heads/members/docs\n", File.ReadAllText(Path.Combine(gitDir, "packed-refs")).Replace("\r", "") + "\n");
            Assert.DoesNotContain("refs/heads/members/docs ", Refs(dir) + "\n");
            Assert.Equal(tip, AgentGit.Host(dir, "rev-parse", "refs/heads/members/docs/docs"));
            Assert.Equal("refs/heads/members/docs/docs", AgentGit.Host(dir, "symbolic-ref", "HEAD"));
        }
        finally { Drop(dir); }
    }

    /// <summary>A leftover of an earlier, cut-off move in the clone's refs: the move refuses and changes nothing.</summary>
    [Fact]
    public void TheClonesOldBranch_IsNotRenamed_WhenAnEarlierMoveLeftSomethingBehind()
    {
        var dir = NewRepo("members/docs");
        try
        {
            var gitDir = Path.Combine(dir, ".git");
            var tip = AgentGit.Host(dir, "rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(gitDir, "refs", "heads", "members", "docs.yawble-move.lock"), tip + "\n");
            var before = Refs(dir);
            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD"));

            var why = TeamRepos.MoveCloneRefThrough(gitDir, "members/docs", "members/docs/docs", tip);

            Assert.Contains("leftover", why);
            Assert.Equal(before, Refs(dir));
            Assert.Equal(head, File.ReadAllText(Path.Combine(gitDir, "HEAD")));
        }
        finally { Drop(dir); }
    }
}
