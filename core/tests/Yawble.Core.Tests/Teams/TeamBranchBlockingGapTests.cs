using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// B020 / card 94 (tester's gap tests). Each case reaches its check without git's local transport (no push, fetch,
/// clone or ls-remote between local repositories): origin's side is a remote-tracking ref written with update-ref, the
/// shared view is a copy of the team repository's .git, a member's clone is a folder whose .git/HEAD is read as a file.
/// So they also run where local transport is not allowed. Temporary repositories only, no real system program but git.
/// </summary>
public class TeamBranchBlockingGapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Refs(string repo) => AgentGit.Host(repo, "for-each-ref", "--format=%(refname) %(objectname)");

    private static string NewCommit(string repo, string parent, string message) =>
        AgentGit.Host(repo, "-c", "user.name=a", "-c", "user.email=a@x", "commit-tree", parent + "^{tree}", "-p", parent, "-m", message);

    private static void AssertPlain(string message, string branch, string where)
    {
        Assert.Contains(branch, message);
        Assert.Contains(where, message);
        Assert.Contains("Nothing was changed", message);
        Assert.DoesNotContain("cannot lock ref", message);
        Assert.DoesNotContain("fatal:", message);
    }

    private static MemberRef AddMember(TeamsFixture f, long teamId, string name)
    {
        using var store = f.Db.OpenStore();
        var m = store.AddMember(teamId, name, MemberRole.Member, "claude");
        store.Subscribe(m.Qualified, MessageTypes.Instruction(m));
        store.InitCursor(m.Qualified);
        return m;
    }

    /// <summary>A member's clone as the host sees it: a folder whose .git/HEAD names the branch, plus its recorded row.</summary>
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

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(from, to));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(from, to), true);
    }

    /// <summary>Spec: the remote-tracking side. origin/members/docs (as of the last fetch) refuses the start plainly,
    /// changes nothing, and git.status lists it as remote.</summary>
    [Fact]
    public async Task ARemoteTrackingBlocker_FailsTheStartPlainly_AndChangesNothing()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Docs", Ct);
        AgentGit.Host(repo.Path, "remote", "add", "origin", Path.Combine(f.Db.Dir, "nowhere.git"));
        AgentGit.Host(repo.Path, "update-ref", "refs/remotes/origin/members/docs", "main");
        var before = Refs(repo.Path);

        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ada, Ct));

        AssertPlain(e.Message, "members/docs", "origin");
        Assert.Contains("Git dialog", e.Message);
        Assert.Equal(before, Refs(repo.Path));
        Assert.False(Directory.Exists(f.Repos.CheckoutOf(ada, repo).ClonePath));
        Assert.Null(f.Repos.Row(ada.Id));
        var s = await f.Repos.GitStatusAsync(ada.TeamId, "Docs", Ct);
        Assert.Equal([new TeamGitBlockingBranch("members/docs", "main", "remote")], s.BlockingBranches);
    }

    /// <summary>A local members/docs and origin's bare "members": both block, each listed with where it is.</summary>
    [Fact]
    public async Task BlockersHereAndOnOrigin_AreEachListed()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Docs", Ct);
        AgentGit.Host(repo.Path, "remote", "add", "origin", Path.Combine(f.Db.Dir, "nowhere.git"));
        AgentGit.Host(repo.Path, "branch", "members/docs", "main");
        AgentGit.Host(repo.Path, "update-ref", "refs/remotes/origin/members", "main"); // the bare "members" blocks too

        var s = await f.Repos.GitStatusAsync(ada.TeamId, "Docs", Ct);

        Assert.Equal([
            new TeamGitBlockingBranch("members", "main", "remote"),
            new TeamGitBlockingBranch("members/docs", "main", "local"),
        ], s.BlockingBranches);
    }

    /// <summary>Spec: "before pushing". A member branch that origin's members/docs blocks is not pushed: the report says so
    /// plainly, and no push is tried (here origin is unreachable, so a tried push would show a transport error instead).</summary>
    [Fact]
    public async Task APushIntoARemoteBlocker_IsNotTried_AndIsReportedPlainly()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Docs", Ct);
        AgentGit.Host(repo.Path, "remote", "add", "origin", Path.Combine(f.Db.Dir, "nowhere.git"));
        var main = AgentGit.Host(repo.Path, "rev-parse", "main");
        AgentGit.Host(repo.Path, "update-ref", "refs/remotes/origin/main", main);
        AgentGit.Host(repo.Path, "update-ref", "refs/remotes/origin/members/docs", main);
        AgentGit.Host(repo.Path, "update-ref", "refs/heads/members/docs/ada", NewCommit(repo.Path, main, "ada's work"));

        var report = await f.Repos.PublishAsync(ada, Ct);

        Assert.Equal(TeamPublishResult.Rejected, report.Result);
        Assert.Contains("members/docs/ada", report.Branches);
        Assert.Contains("origin has a branch members/docs", report.Reason);
        Assert.DoesNotContain("cannot lock ref", report.Reason);
        Assert.DoesNotContain("Could not read", report.Reason);
    }

    /// <summary>Spec: the shared repo (the team's shared view). Its members/docs refuses the start before the view is
    /// refreshed (no transport is reached), and the team repository is unchanged.</summary>
    [Fact]
    public async Task ASharedViewBlocker_FailsTheStartPlainly_BeforeAnyTransport()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Docs", Ct);
        var shared = f.Repos.SharedRepoPath("Docs");
        CopyDir(Path.Combine(repo.Path, ".git"), shared);
        AgentGit.Host(shared, "update-ref", "refs/heads/members/docs", AgentGit.Host(repo.Path, "rev-parse", "main"));
        var before = Refs(repo.Path);

        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ada, Ct));

        AssertPlain(e.Message, "members/docs", "shared view");
        Assert.Equal(before, Refs(repo.Path));
        Assert.Contains("refs/heads/members/docs ", Refs(shared));
        Assert.Null(f.Repos.Row(ada.Id));
    }

    /// <summary>A Manager (team/docs) is never blocked by members/docs: its start does not run the check.</summary>
    [Fact]
    public async Task TheManagersStart_IsNotRefusedByAMembersBlocker()
    {
        using var f = new TeamsFixture();
        var (manager, _, _) = f.Team("Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(manager.TeamId, "Docs", Ct);
        AgentGit.Host(repo.Path, "branch", "members/docs", "main");
        var e = await Record.ExceptionAsync(() => f.Repos.PrepareAsync(manager, Ct));
        Assert.IsNotType<MemberBranchBlockedException>(e); // (it may still fail later on local transport in a sandbox)
    }

    /// <summary>
    /// FAULT (card 94, spec 1 and 2). Team "Ada" shares a repository with team Alpha, whose member Ada is still on its old
    /// members/ada (work not merged). Team Ada's Git dialog lists members/ada as blocking with Delete, and the Delete's plan
    /// checks only team Ada's own clones, so it plans (and with the token runs) deleting a branch another team's member is
    /// on. Spec 2: a team whose slug equals another team's member branch must not break that team's branches.
    /// </summary>
    [Fact] // fixed by card 104
    public async Task AnotherTeamsInUseOldBranch_IsNotDeletableFromTheBlockedTeamsDialog()
    {
        using var f = new TeamsFixture();
        var (_, alphaAda, _) = f.Team("Alpha");
        var (adaManager, adaAda, adaBen) = f.Team("Ada");
        var picked = Path.Combine(f.Db.Dir, "picked");
        Directory.CreateDirectory(picked);
        AgentGit.Host(picked, "init", "-q", "-b", "main");
        AgentGit.Host(picked, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "--allow-empty", "-m", "base");
        await f.Repos.UseExistingRepoAsync(alphaAda.TeamId, "Alpha", picked, Ct);
        await f.Repos.UseExistingRepoAsync(adaBen.TeamId, "Ada", picked, Ct);
        var main = AgentGit.Host(picked, "rev-parse", "main");
        var work = NewCommit(picked, main, "alpha ada's unmerged work");
        AgentGit.Host(picked, "update-ref", "refs/heads/members/ada", work);
        FakeClone(f, alphaAda, "Alpha", "members/ada");

        var refused = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(adaBen, Ct));
        AssertPlain(refused.Message, "members/ada", "the team's repository");
        // Card 104: the start says whose branch it is and the way forward that exists: Alpha's member's next start moves it.
        Assert.Contains("team Alpha's member Ada", refused.Message);
        Assert.Contains("members/alpha/ada", refused.Message);
        Assert.DoesNotContain("delete members/ada", refused.Message, StringComparison.OrdinalIgnoreCase);
        var s = await f.Repos.GitStatusAsync(adaBen.TeamId, "Ada", Ct);
        // Marked, so the dialog shows no Delete for it.
        Assert.Contains(new TeamGitBlockingBranch("members/ada", "main", "local") { InUseBy = "team Alpha's member Ada", MovesTo = "members/alpha/ada" },
            s.BlockingBranches);

        // What the dialog's Delete sends (the dialog always ticks unmergedOk for a blocking branch, as merge is unknown).
        var teamAda = new[] { adaManager.Name, adaAda.Name, adaBen.Name };
        var plan = await f.Repos.GitDeleteBranchesAsync(adaBen.TeamId, "Ada", teamAda, null, ["members/ada"], false, ["members/ada"], null, Ct);
        Assert.True(plan.Plan.Contains("REFUSED"), plan.Plan);
        Assert.Contains("team Alpha's member Ada", plan.Plan);
        Assert.True(plan.Empty);
        // Even sent with a token, nothing is deleted.
        var run = await f.Repos.GitDeleteBranchesAsync(adaBen.TeamId, "Ada", teamAda, null, ["members/ada"], false, ["members/ada"], plan.Token, Ct);
        Assert.Equal("Nothing to delete.", run.Result);
        Assert.Equal(work, AgentGit.Host(picked, "rev-parse", "refs/heads/members/ada"));
    }

    /// <summary>Card 104: with another branch named too, the other team's one is refused and the rest is planned as usual;
    /// another team's clone counts for what is checked out.</summary>
    [Fact]
    public async Task AnotherTeamsBranch_IsRefusedBesideADeletableOne()
    {
        using var f = new TeamsFixture();
        var (_, alphaAda, _) = f.Team("Alpha");
        var (adaManager, adaAda, adaBen) = f.Team("Ada");
        var picked = Path.Combine(f.Db.Dir, "picked");
        Directory.CreateDirectory(picked);
        AgentGit.Host(picked, "init", "-q", "-b", "main");
        AgentGit.Host(picked, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "--allow-empty", "-m", "base");
        await f.Repos.UseExistingRepoAsync(alphaAda.TeamId, "Alpha", picked, Ct);
        await f.Repos.UseExistingRepoAsync(adaBen.TeamId, "Ada", picked, Ct);
        AgentGit.Host(picked, "branch", "members/alpha/ada", "main");
        AgentGit.Host(picked, "branch", "members/zed", "main"); // an old branch nobody is on
        FakeClone(f, alphaAda, "Alpha", "members/alpha/ada");

        var teamAda = new[] { adaManager.Name, adaAda.Name, adaBen.Name };
        var plan = await f.Repos.GitDeleteBranchesAsync(adaBen.TeamId, "Ada", teamAda, null, ["members/alpha/ada", "members/zed"], false, [], null, Ct);

        Assert.Contains("members/alpha/ada: REFUSED, team Alpha's member Ada is still on it", plan.Plan);
        Assert.Contains("delete members/zed", plan.Plan);
        Assert.False(plan.Empty);
        var done = await f.Repos.GitDeleteBranchesAsync(adaBen.TeamId, "Ada", teamAda, null, ["members/alpha/ada", "members/zed"], false, [], plan.Token, Ct);
        Assert.Contains("deleted members/zed", done.Result);
        Assert.Contains("refs/heads/members/alpha/ada ", Refs(picked));
    }

    /// <summary>
    /// FAULT (card 94, spec 1, same-team case). Team "Docs" has a member named Docs still on its old members/docs. Its
    /// B016 move to members/docs/docs can never happen while members/docs exists (the old branch is kept), and that
    /// branch blocks every other member: Ben's start is refused and told to delete members/docs in the Git dialog, but
    /// that Delete is refused because Docs's clone is on it. No way forward the message names works.
    /// </summary>
    /// <para>Card 104: the way forward is Docs's own next start, whose B016 move now goes through its old branch (renamed,
    /// not kept: git cannot keep both). Ben's refusal names that, the dialog marks the branch (no Delete), and the move's
    /// clone side (by file, the part with no transport) is checked here on a real repository; the whole start with a
    /// clone and origin is TeamBranchBlockingTests.ASameTeamMemberNamedLikeTheSlug_MovesThroughItsOldBranch (local transport).</para>
    [Fact] // fixed by card 104
    public async Task ASameTeamMemberNamedLikeTheSlug_DoesNotLeaveTheTeamWithNoWayForward()
    {
        using var f = new TeamsFixture();
        var (manager, ada, ben) = f.Team("Docs");
        var docs = AddMember(f, ben.TeamId, "Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(ben.TeamId, "Docs", Ct);
        var main = AgentGit.Host(repo.Path, "rev-parse", "main");
        var work = NewCommit(repo.Path, main, "docs's old-style work");
        AgentGit.Host(repo.Path, "update-ref", "refs/heads/members/docs", work);
        var clone = FakeClone(f, docs, "Docs", "members/docs");

        var refused = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ben, Ct));
        AssertPlain(refused.Message, "members/docs", "the team's repository");
        // The way forward the message names is one that works: Docs's next start moves its branch to members/docs/docs.
        Assert.Contains("member Docs of this team", refused.Message);
        Assert.Contains("members/docs/docs at that member's next start", refused.Message);
        Assert.Contains("Start Docs first", refused.Message);
        Assert.DoesNotContain("delete members/docs", refused.Message, StringComparison.OrdinalIgnoreCase);
        var s = await f.Repos.GitStatusAsync(ben.TeamId, "Docs", Ct);
        Assert.Equal([new TeamGitBlockingBranch("members/docs", "main", "local") { InUseBy = "member Docs of this team", MovesTo = "members/docs/docs" }],
            s.BlockingBranches);

        // The Delete stays refused (Docs's clone is on it), which is right: the dialog offers none for a marked branch.
        var team = new[] { manager.Name, ada.Name, ben.Name, docs.Name };
        var plan = await f.Repos.GitDeleteBranchesAsync(ben.TeamId, "Docs", team, null, ["members/docs"], false, ["members/docs"], null, Ct);
        Assert.Contains("REFUSED, checked out", plan.Plan);
        Assert.Equal(work, AgentGit.Host(repo.Path, "rev-parse", "refs/heads/members/docs"));
        Assert.Equal("ref: refs/heads/members/docs", File.ReadAllText(Path.Combine(clone, ".git", "HEAD")).Trim());
    }

    /// <summary>Card 104: the move's clone half, by file (no git in the clone): a loose old branch with its reflog, HEAD on it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the old branch only in packed-refs
    public void TheClonesOldBranch_IsRenamedUnderItself_ByFile(bool packed)
    {
        var dir = Path.Combine(Path.GetTempPath(), "yb104-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            AgentGit.Host(dir, "init", "-q", "-b", "main");
            AgentGit.Host(dir, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "--allow-empty", "-m", "base");
            AgentGit.Host(dir, "checkout", "-q", "-b", "members/docs");
            AgentGit.Host(dir, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "--allow-empty", "-m", "work");
            var tip = AgentGit.Host(dir, "rev-parse", "HEAD");
            if (packed) AgentGit.Host(dir, "pack-refs", "--all");
            var gitDir = Path.Combine(dir, ".git");

            Assert.Null(TeamRepos.MoveCloneRefThrough(gitDir, "members/docs", "members/docs/docs", tip));

            Assert.Equal("refs/heads/members/docs/docs", AgentGit.Host(dir, "symbolic-ref", "HEAD"));
            Assert.Equal(tip, AgentGit.Host(dir, "rev-parse", "refs/heads/members/docs/docs"));
            Assert.DoesNotContain("refs/heads/members/docs ", Refs(dir) + "\n");
            Assert.DoesNotContain("members/docs.", Refs(dir)); // nothing set aside is left as a ref
            Assert.Contains("work", AgentGit.Host(dir, "reflog", "show", "refs/heads/members/docs/docs")); // the reflog moved too
            // git itself goes on working on the new branch.
            AgentGit.Host(dir, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "--allow-empty", "-m", "more");
            Assert.NotEqual(tip, AgentGit.Host(dir, "rev-parse", "refs/heads/members/docs/docs"));
            Assert.Empty(Directory.GetFiles(gitDir, "*.yawble-move*", SearchOption.AllDirectories));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A move the clone's state does not allow changes nothing (the old branch moved since it was read).</summary>
    [Fact]
    public void TheClonesOldBranch_IsNotRenamed_WhenItMovedSinceItWasRead()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yb104-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            AgentGit.Host(dir, "init", "-q", "-b", "members/docs");
            AgentGit.Host(dir, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "--allow-empty", "-m", "base");
            var before = Refs(dir);
            var head = File.ReadAllText(Path.Combine(dir, ".git", "HEAD"));

            var why = TeamRepos.MoveCloneRefThrough(Path.Combine(dir, ".git"), "members/docs", "members/docs/docs", new string('a', 40));

            Assert.Contains("moved", why);
            Assert.Equal(before, Refs(dir));
            Assert.Equal(head, File.ReadAllText(Path.Combine(dir, ".git", "HEAD")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
