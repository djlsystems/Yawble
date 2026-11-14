using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// B020 / card 92: a ref members/&lt;slug&gt; (an old member branch named like a team's slug) blocks that team's
/// members/&lt;slug&gt;/&lt;name&gt; branches, since git cannot hold both. The member's start fails with a plain message (the
/// branch, where it is, what to do) and nothing changes; the Git dialog lists the blocker. Temporary repositories and bare
/// origins only, no network, no real system program.
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
        // Card 104: there is no team rename, and the Git dialog only deletes: only actions that exist are named.
        Assert.DoesNotContain("rename", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cannot lock ref", message);
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

    [Fact]
    public async Task ABlockingBranchInTheSharedView_FailsTheStartPlainly()
    {
        using var f = new TeamsFixture();
        var (manager, ada, _) = f.Team("Docs");
        await f.Repos.PrepareAsync(manager, Ct); // the Manager's start makes the shared view (team/docs is not blocked)
        var shared = f.Repos.SharedRepoPath("Docs");
        AgentGit.Host(shared, "branch", "members/docs", "main");
        var repo = f.Repos.RepoOf(ada.TeamId)!;
        var before = Refs(repo.Path);

        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ada, Ct));

        AssertPlain(e.Message, "members/docs", "shared view");
        Assert.Equal(before, Refs(repo.Path));
        Assert.Contains("refs/heads/members/docs ", Refs(shared));
        Assert.False(Directory.Exists(f.Repos.CheckoutOf(ada, repo).ClonePath));
    }

    [Fact]
    public async Task ABlockingBranchInTheMembersClone_FailsTheStartPlainly_AndTheCloneIsNotTouched()
    {
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
        // Alpha's Ada, pre-B016: on members/ada, with fetched work.
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

    /// <summary>The B016 move into a name origin blocks is refused before anything moves: the member stays on its old
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
    /// Card 104 (F2): team "Docs" with a member "Docs" still on its old members/docs. Ben's start is refused and names
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
        // Docs, pre-B016: on members/docs, with work fetched back and pushed.
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

        var report = await f.Repos.PublishAsync(docs, Ct);
        Assert.Contains("members/docs/docs", report.Published!);
        Assert.Contains($"refs/heads/members/docs/docs {tip}", Refs(bare));
        Assert.DoesNotContain("refs/heads/members/docs ", Refs(bare));

        Assert.Equal("members/docs/ben", (await f.Repos.PrepareAsync(ben, Ct)).Branch);
    }

    /// <summary>The blockers for a slug are listed by name; a slug nothing blocks has none (card 104: there is no team
    /// rename, so the old rename check is gone and only the listing is left).</summary>
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
}
