using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// A ref members/&lt;slug&gt; (an old member branch named like a team's slug) blocks that team's
/// members/&lt;slug&gt;/&lt;name&gt; branches, since git cannot hold both. The member's start fails with a plain message (the
/// branch, where it is, what to do) and nothing changes; the Git dialog lists the blocker. Temporary repositories and bare
/// origins only, no network, no real system program but git. The cases of the second half reach their check without
/// git's local transport (no push, fetch, clone or ls-remote between local repositories): origin's side is a
/// remote-tracking ref written with update-ref, origin itself is a path that does not exist (a tried push fails at once
/// and plainly), the shared view is a copy of the team repository's .git, and a member's clone is a folder whose
/// .git/HEAD is read as a file. So they also run where local transport is not allowed.
/// </summary>
public class TeamBranchBlockingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Refs(string repo) => AgentGit.Host(repo, "for-each-ref", "--format=%(refname) %(objectname)");

    private static string Ok(string cwd, string home, params string[] args)
    {
        var r = AgentGit.Run(cwd, home, args);
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)}: {r.Stderr}");
        return r.Stdout;
    }

    private static string Commit(string clone, string home, string file)
    {
        File.WriteAllText(Path.Combine(clone, file), file);
        Ok(clone, home, "add", file);
        Ok(clone, home, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "-m", file);
        return Ok(clone, home, "rev-parse", "HEAD");
    }

    private static void AssertPlain(string message, string branch, string where)
    {
        Assert.Contains(branch, message);
        Assert.Contains(where, message);
        Assert.Contains("Nothing was changed", message);
        // There is no team rename, and the Git dialog only deletes: only actions that exist are named.
        Assert.DoesNotContain("rename", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cannot lock ref", message);
        Assert.DoesNotContain("fatal:", message);
    }

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

    [Fact]
    public async Task ABlockingBranchInTheTeamsRepository_FailsTheStartPlainly_AndChangesNothing()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Docs", Ct);
        AgentGit.Host(repo.Path, "branch", "members/docs", "main"); // an old member "Docs" of some team
        var before = Refs(repo.Path);

        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ada, Ct));

        AssertPlain(e.Message, "members/docs", "the team's repository");
        Assert.Contains("Git dialog", e.Message);
        Assert.Equal(before, Refs(repo.Path));
        Assert.False(Directory.Exists(f.Repos.CheckoutOf(ada, repo).ClonePath));
        Assert.Null(f.Repos.Row(ada.Id));
        var s = await f.Repos.GitStatusAsync(ada.TeamId, "Docs", Ct);
        Assert.Equal([new TeamGitBlockingBranch("members/docs", "main", "local")], s.BlockingBranches);
    }

    /// <summary>The shared view follows the repository. A members/docs it still has (deleted in the repository
    /// since) is no refusal the Git dialog could act on: the start goes on, and its refresh (fetch --prune) removes it.</summary>
    [Fact]
    public async Task AStaleBlockerInTheSharedView_DoesNotRefuseTheStart_AndGoesAtTheRefresh()
    {
        SandboxGit.RequireLocalTransport();
        using var f = new TeamsFixture();
        var (manager, ada, _) = f.Team("Docs");
        await f.Repos.PrepareAsync(manager, Ct); // the Manager's start makes the shared view (team/docs is not blocked)
        var shared = f.Repos.SharedRepoPath("Docs");
        AgentGit.Host(shared, "branch", "members/docs", "main");
        var repo = f.Repos.RepoOf(ada.TeamId)!;

        var c = await f.Repos.PrepareAsync(ada, Ct);

        Assert.Equal("members/docs/ada", c.Branch);
        Assert.DoesNotContain("refs/heads/members/docs ", Refs(shared));
        Assert.True(Directory.Exists(f.Repos.CheckoutOf(ada, repo).ClonePath));
    }

    [Fact]
    public async Task ABlockingBranchInTheMembersClone_FailsTheStartPlainly_AndTheCloneIsNotTouched()
    {
        SandboxGit.RequireLocalTransport();
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var c = await f.Repos.PrepareAsync(ada, Ct);
        Assert.Equal("members/docs/ada", c.Branch);
        // The agent renamed its branch away and made members/docs in its clone.
        Ok(c.ClonePath, c.Workspace, "branch", "-m", "members/docs/ada", "work");
        Ok(c.ClonePath, c.Workspace, "branch", "members/docs");
        var cloneBefore = Refs(c.ClonePath);
        var head = File.ReadAllText(Path.Combine(c.ClonePath, ".git", "HEAD"));

        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ada, Ct));

        AssertPlain(e.Message, "members/docs", "Ada's clone");
        Assert.Equal(cloneBefore, Refs(c.ClonePath));
        Assert.Equal(head, File.ReadAllText(Path.Combine(c.ClonePath, ".git", "HEAD")));
        Assert.Equal("members/docs/ada", f.Repos.Row(ada.Id)!.Branch);
    }

    [Fact]
    public async Task ABlockingBranchOnOrigin_FailsTheStartPlainly_IsListedAsRemote_AndBlocksThePush()
    {
        using var f = new TeamsFixture();
        var (_, ada, ben) = f.Team("Docs");
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Docs", picked, Ct);
        // Ada started before the blocker came; her branch is fetched back.
        var a = await f.Repos.PrepareAsync(ada, Ct);
        Commit(a.ClonePath, a.Workspace, "a.txt");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        var trunk = AgentGit.Host(picked, "rev-parse", "trunk");
        AgentGit.Host(picked, "push", "-q", "origin", trunk + ":refs/heads/members/docs");
        Assert.Equal("fetched", (await f.Repos.GitFetchAsync(ada.TeamId, "Docs", Ct)).Outcome);
        var before = Refs(picked);
        var originBefore = Refs(bare);

        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ben, Ct));
        AssertPlain(e.Message, "members/docs", "origin");
        Assert.Equal(before, Refs(picked));
        Assert.False(Directory.Exists(f.Repos.CheckoutOf(ben, f.Repos.RepoOf(ben.TeamId)!).ClonePath));

        var s = await f.Repos.GitStatusAsync(ada.TeamId, "Docs", Ct);
        Assert.Equal([new TeamGitBlockingBranch("members/docs", "main", "remote")], s.BlockingBranches);

        // Ada's branch, made before, is not pushed into the clash: said plainly, origin unchanged.
        var report = await f.Repos.PublishAsync(ada, Ct);
        Assert.Equal(TeamPublishResult.Rejected, report.Result);
        Assert.Contains("members/docs/ada", report.Branches);
        Assert.Contains("origin has a branch members/docs", report.Reason);
        Assert.DoesNotContain("cannot lock ref", report.Reason);
        Assert.Equal(originBefore, Refs(bare));
    }

    [Fact]
    public async Task ABlockingBranchInAnExtraRepository_FailsTheStartPlainly_BeforeAnythingIsMade()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var dir = Path.Combine(f.Db.Dir, "cloud");
        Directory.CreateDirectory(dir);
        var (cloud, _) = TeamReposTests.PersonsRepoWithOrigin(dir);
        await f.Repos.AddExtraAsync(ada.TeamId, "Docs", "cloud", cloud, null, writable: true, Ct);
        AgentGit.Host(cloud, "branch", "members/docs", "trunk");
        var before = Refs(cloud);

        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ada, Ct));

        AssertPlain(e.Message, "members/docs", "repository cloud");
        Assert.Equal(before, Refs(cloud));
        var repo = f.Repos.RepoOf(ada.TeamId)!;
        Assert.False(Directory.Exists(f.Repos.CheckoutOf(ada, repo).ClonePath)); // the main clone was not made either
        Assert.Null(f.Repos.ExtraRow(ada.Id, "cloud"));
        var s = await f.Repos.GitStatusAsync(ada.TeamId, "Docs", Ct, "cloud");
        Assert.Equal([new TeamGitBlockingBranch("members/docs", "cloud", "local")], s.BlockingBranches);
    }

    /// <summary>The reverse case: team "Ada" arrives where team Alpha's member Ada is still on members/ada. Team Ada's
    /// start is refused; Alpha's member keeps its branch and still moves to members/alpha/ada.</summary>
    [Fact]
    public async Task ATeamNamedLikeAnotherTeamsOldMemberBranch_IsRefused_AndThatTeamsBranchesAreUntouched()
    {
        using var f = new TeamsFixture();
        var (_, alphaAda, _) = f.Team("Alpha");
        var (_, _, adaBen) = f.Team("Ada");
        var (picked, _) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(alphaAda.TeamId, "Alpha", picked, Ct);
        await f.Repos.UseExistingRepoAsync(adaBen.TeamId, "Ada", picked, Ct);
        // Alpha's Ada, old-style: on members/ada, with fetched work.
        var c = await f.Repos.PrepareAsync(alphaAda, Ct);
        Ok(c.ClonePath, c.Workspace, "branch", "-m", "members/ada");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(f.Db.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE member_clones SET branch = 'members/ada' WHERE member_id = $m;";
            cmd.Parameters.AddWithValue("$m", alphaAda.Id);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        var tip = Commit(c.ClonePath, c.Workspace, "w.txt");
        Assert.True((await f.Repos.FetchBackAsync(alphaAda, Ct)).Fetched);
        Assert.Equal(tip, AgentGit.Host(picked, "rev-parse", "refs/heads/members/ada"));

        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(adaBen, Ct));
        AssertPlain(e.Message, "members/ada", "the team's repository");
        Assert.Equal(tip, AgentGit.Host(picked, "rev-parse", "refs/heads/members/ada"));

        var moved = await f.Repos.PrepareAsync(alphaAda, Ct);
        Assert.Equal("members/alpha/ada", moved.Branch);
        Assert.Equal(tip, AgentGit.Host(picked, "rev-parse", "refs/heads/members/alpha/ada"));
        Assert.Equal(tip, AgentGit.Host(picked, "rev-parse", "refs/heads/members/ada")); // kept, never deleted
    }

    /// <summary>The move to members/&lt;team&gt;/&lt;name&gt; into a name origin blocks is refused before anything moves: the member stays on its old
    /// branch (its start still works there), and the host repository gets no new branch.</summary>
    [Fact]
    public async Task TheOldStyleMove_IntoANameOriginBlocks_IsRefusedBeforeAnythingMoves()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var (picked, _) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Docs", picked, Ct);
        var c = await f.Repos.PrepareAsync(ada, Ct);
        Ok(c.ClonePath, c.Workspace, "branch", "-m", "members/ada");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(f.Db.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE member_clones SET branch = 'members/ada' WHERE member_id = $m;";
            cmd.Parameters.AddWithValue("$m", ada.Id);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        Commit(c.ClonePath, c.Workspace, "w.txt");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        var trunk = AgentGit.Host(picked, "rev-parse", "trunk");
        AgentGit.Host(picked, "push", "-q", "origin", trunk + ":refs/heads/members/docs");
        AgentGit.Host(picked, "fetch", "-q", "origin");
        var before = Refs(picked);

        var again = await f.Repos.PrepareAsync(ada, Ct);

        Assert.Equal("members/ada", again.Branch);
        Assert.Equal("members/ada", Ok(again.ClonePath, again.Workspace, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(before, Refs(picked));
        Assert.Contains(f.Log, l => l.Contains("origin already has members/docs", StringComparison.Ordinal));
    }

    /// <summary>
    /// Team "Docs" with a member "Docs" still on its old members/docs. Ben's start is refused and names
    /// Docs's next start; that start moves Docs through its own old branch (renamed here and in the clone, HEAD follows),
    /// the push after its run replaces origin's members/docs with members/docs/docs, and then Ben starts.
    /// </summary>
    [Fact]
    public async Task ASameTeamMemberNamedLikeTheSlug_MovesThroughItsOldBranch()
    {
        using var f = new TeamsFixture();
        var (_, _, ben) = f.Team("Docs");
        MemberRef docs;
        using (var store = f.Db.OpenStore())
        {
            docs = store.AddMember(ben.TeamId, "Docs", MemberRole.Member, "claude");
            store.Subscribe(docs.Qualified, MessageTypes.Instruction(docs));
            store.InitCursor(docs.Qualified);
        }
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ben.TeamId, "Docs", picked, Ct);
        // Docs, old-style: on members/docs, with work fetched back and pushed.
        var c = await f.Repos.PrepareAsync(docs, Ct);
        Ok(c.ClonePath, c.Workspace, "branch", "-m", "members/docs");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(f.Db.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE member_clones SET branch = 'members/docs' WHERE member_id = $m;";
            cmd.Parameters.AddWithValue("$m", docs.Id);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        var tip = Commit(c.ClonePath, c.Workspace, "w.txt");
        Assert.True((await f.Repos.FetchBackAsync(docs, Ct)).Fetched);
        await f.Repos.PublishAsync(docs, Ct);
        Assert.Contains($"refs/heads/members/docs {tip}", Refs(bare));

        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ben, Ct));
        Assert.Contains("Start Docs first", e.Message);

        var moved = await f.Repos.PrepareAsync(docs, Ct);
        Assert.Equal("members/docs/docs", moved.Branch);
        Assert.Equal("members/docs/docs", Ok(moved.ClonePath, moved.Workspace, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(tip, Ok(moved.ClonePath, moved.Workspace, "rev-parse", "HEAD"));
        Assert.Equal(tip, AgentGit.Host(picked, "rev-parse", "refs/heads/members/docs/docs"));
        Assert.DoesNotContain("refs/heads/members/docs ", Refs(picked));

        // Origin's members/docs is never deleted by the app (a name alone does not prove whose it is): the push waits and
        // says to delete it in the Git dialog. Once the person has deleted it there, the next publish goes through.
        var report = await f.Repos.PublishAsync(docs, Ct);
        Assert.Contains($"refs/heads/members/docs {tip}", Refs(bare));
        Assert.DoesNotContain("refs/heads/members/docs/docs ", Refs(bare));
        Assert.Contains("Delete members/docs on origin in the Git dialog", report.Reason ?? "");

        AgentGit.Host(bare, "branch", "-D", "members/docs");
        AgentGit.Host(picked, "fetch", "--prune", "origin");
        var again = await f.Repos.PublishAsync(docs, Ct);
        Assert.Equal(TeamPublishResult.Published, again.Result);
        Assert.Contains("members/docs/docs", again.Branches);
        Assert.Contains($"refs/heads/members/docs/docs {tip}", Refs(bare));

        Assert.Equal("members/docs/ben", (await f.Repos.PrepareAsync(ben, Ct)).Branch);
    }

    /// <summary>The blockers for a slug are listed by name; a slug nothing blocks has none.</summary>
    [Fact]
    public async Task BlockingBranches_AreListedForTheBlockedSlugOnly()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Alpha");
        var repo = await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Alpha", Ct);
        AgentGit.Host(repo.Path, "branch", "members/docs", "main");
        Assert.Equal([new TeamGitBlockingBranch("members/docs", "main", "local")], await f.Repos.BlockingBranchesAsync(ada.TeamId, "Docs", ct: Ct));
        Assert.Empty(await f.Repos.BlockingBranchesAsync(ada.TeamId, "Alpha", ct: Ct));
    }

    [Fact]
    public void TheBlockedMessage_NamesOnlyActionsThatExist()
    {
        var plain = TeamRepos.BlockedMessage("Docs", "members/docs", "the team's repository (x)", inDialog: true);
        Assert.Contains("delete members/docs in the Git dialog", plain);
        Assert.DoesNotContain("rename", plain, StringComparison.OrdinalIgnoreCase);
        var clone = TeamRepos.BlockedMessage("Docs", "members/docs", "Ada's clone", inDialog: false);
        Assert.DoesNotContain("Git dialog", clone);
        Assert.DoesNotContain("rename", clone, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("members/docs/ada", new[] { "members", "members/docs" })]
    [InlineData("members/ada", new[] { "members" })]
    public void ParentPaths_AreEachPrefixOfTheBranch(string branch, string[] expected) =>
        Assert.Equal(expected, TeamRepos.ParentPaths(branch));

    // ------------------------------------------------------------------ without git's local transport

    /// <summary>The remote-tracking side: origin/members/docs (as of the last fetch) refuses the start plainly,
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

    /// <summary>The check before pushing: a member branch that origin's members/docs blocks is not pushed: the report says so
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

    /// <summary>The shared view follows the repository, so a members/docs only it has (deleted in the repository
    /// since) does not refuse the start with a branch the Git dialog cannot delete: the refresh after the checks prunes it.
    /// (Here the start may still fail later on local transport in a sandbox; never with the blocked refusal.)</summary>
    [Fact]
    public async Task AStaleSharedViewBlocker_DoesNotRefuseTheStart()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Docs", Ct);
        var shared = f.Repos.SharedRepoPath("Docs");
        CopyDir(Path.Combine(repo.Path, ".git"), shared);
        AgentGit.Host(shared, "update-ref", "refs/heads/members/docs", AgentGit.Host(repo.Path, "rev-parse", "main"));

        var e = await Record.ExceptionAsync(() => f.Repos.PrepareAsync(ada, Ct));

        Assert.IsNotType<MemberBranchBlockedException>(e);
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
    /// Two teams on one repository: team "Ada" shares it with team Alpha, whose member Ada is still on its old
    /// members/ada (work not merged). A team whose slug equals another team's member branch does not break that team's
    /// branches: team Ada's start says whose branch it is and where it moves, its Git dialog marks members/ada as in use
    /// (no Delete), and a Delete sent anyway is refused, with a token too.
    /// </summary>
    [Fact]
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
        // The start says whose branch it is and the way forward that exists: Alpha's member's next start moves it.
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

    /// <summary>With another branch named too, the other team's one is refused and the rest is planned as usual;
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
    /// The same-team case: team "Docs" has a member named Docs still on its old members/docs, which blocks every other
    /// member, and whose Delete is refused because Docs's clone is on it. The way forward is Docs's own next start, whose
    /// move to members/docs/docs goes through its old branch (renamed, not kept: git cannot keep both). Ben's refusal
    /// names that and the dialog marks the branch (no Delete). The whole start with a clone and origin is
    /// <see cref="ASameTeamMemberNamedLikeTheSlug_MovesThroughItsOldBranch"/> (local transport).
    /// </summary>
    [Fact]
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

    /// <summary>The move's clone half, by file (no git in the clone): a loose old branch with its reflog, HEAD on it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the old branch only in packed-refs
    public void TheClonesOldBranch_IsRenamedUnderItself_ByFile(bool packed)
    {
        var dir = Path.Combine(Path.GetTempPath(), "yawble-old-branch-rename-" + Guid.NewGuid().ToString("N")[..8]);
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
        var dir = Path.Combine(Path.GetTempPath(), "yawble-old-branch-rename-" + Guid.NewGuid().ToString("N")[..8]);
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

    // ------------------------------------------------------------------ the push after a move through the old branch

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

    /// <summary>The push after the move: origin's old members/docs, even one the new branch holds, is never deleted by the
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

    /// <summary>The push after the move: origin's members/docs has a commit the new branch does not: it is not deleted (no push tried),
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

    /// <summary>The push after the move: another team's member named Docs is still on members/docs (same repository): origin's copy is
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
        var dir = Path.Combine(Path.GetTempPath(), "yawble-branch-blocking-" + Guid.NewGuid().ToString("N")[..8]);
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
