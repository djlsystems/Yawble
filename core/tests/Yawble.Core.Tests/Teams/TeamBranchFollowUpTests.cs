using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// Follow-ups to old member branches and blocked team branches. Temporary repositories only; no git local transport (no push, fetch, clone
/// or ls-remote between local repositories), so these also run in a sandbox that does not allow it. Item 7's tests are
/// TeamBranchBlockingTests.AStaleBlockerInTheSharedView_DoesNotRefuseTheStart_AndGoesAtTheRefresh (local transport) and
/// TeamBranchBlockingGapTests.AStaleSharedViewBlocker_DoesNotRefuseTheStart.
/// </summary>
public class TeamBranchFollowUpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Refs(string repo) => AgentGit.Host(repo, "for-each-ref", "--format=%(refname) %(objectname)");

    /// <summary>A member's recorded clone row only (no folder on disk): the branch the app has it on.</summary>
    private static void RecordClone(TeamsFixture f, MemberRef member, string team, string branch)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(f.Db.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO member_clones (member_id, path, branch, created_at) VALUES ($m, $p, $b, '2026-11-14T00:00:00Z');";
        cmd.Parameters.AddWithValue("$m", member.Id);
        cmd.Parameters.AddWithValue("$p", Path.Combine(f.Repos.MemberRoot(team, member.Name), "repo"));
        cmd.Parameters.AddWithValue("$b", branch);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    // ------------------------------------------------------------------ item 6: blockers ignore case

    /// <summary>Members/Docs blocks members/docs/&lt;name&gt; where loose refs are files (Windows, macOS): the start gets the
    /// plain message naming it as git has it, never "cannot lock ref", and the Git dialog lists it for its Delete.</summary>
    [Fact]
    public async Task ABlockerInAnotherCase_FailsTheStartPlainly_AndIsListed()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Docs", Ct);
        AgentGit.Host(repo.Path, "branch", "Members/Docs", "main");
        var before = Refs(repo.Path);

        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ada, Ct));

        Assert.Contains("The branch Members/Docs in the team's repository", e.Message);
        Assert.Contains("Nothing was changed", e.Message);
        Assert.DoesNotContain("cannot lock ref", e.Message);
        Assert.Equal(before, Refs(repo.Path));
        Assert.Null(f.Repos.Row(ada.Id));
        var s = await f.Repos.GitStatusAsync(ada.TeamId, "Docs", Ct);
        Assert.Equal([new TeamGitBlockingBranch("Members/Docs", "main", "local")], s.BlockingBranches);
    }

    /// <summary>Origin's side too: a remote-tracking MEMBERS (as of the last fetch) is listed as remote and refuses the start.</summary>
    [Fact]
    public async Task ARemoteBlockerInAnotherCase_IsListedAsRemote_AndRefusesTheStart()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Docs", Ct);
        AgentGit.Host(repo.Path, "remote", "add", "origin", Path.Combine(f.Db.Dir, "nowhere.git"));
        AgentGit.Host(repo.Path, "update-ref", "refs/remotes/origin/MEMBERS", "main");

        var s = await f.Repos.GitStatusAsync(ada.TeamId, "Docs", Ct);
        Assert.Equal([new TeamGitBlockingBranch("MEMBERS", "main", "remote")], s.BlockingBranches);
        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ada, Ct));
        Assert.Contains("origin/MEMBERS", e.Message);
        Assert.DoesNotContain("cannot lock ref", e.Message);
    }

    /// <summary>The clone's side (what ls-remote of the clone gives): matched ignoring case, named as the clone has it, in
    /// the order of the names looked for; other branches and non-branch refs are not blockers.</summary>
    [Fact]
    public void TheClonesBlockers_AreMatchedIgnoringCase()
    {
        const string sha = "0123456789012345678901234567890123456789";
        var lsRemote = string.Join('\n',
            $"{sha}\trefs/heads/Members/Docs",
            $"{sha}\trefs/heads/members/docs/ada",
            $"{sha}\trefs/heads/MEMBERS",
            $"{sha}\trefs/tags/members/docs",
            $"{sha}\trefs/heads/work",
            "");

        Assert.Equal(["MEMBERS", "Members/Docs"], TeamRepos.BlockingHeads(lsRemote, TeamRepos.BlockingNames("Docs")));
        Assert.Empty(TeamRepos.BlockingHeads($"{sha}\trefs/heads/members/docs/ada\n", TeamRepos.BlockingNames("Docs")));
    }

    // ------------------------------------------------------------------ item 8: a refusal after a move says what moved

    /// <summary>The move to members/&lt;team&gt;/&lt;name&gt; runs before the checks. When a check then refuses, the message does not say "Nothing was
    /// changed": it says which branch moved, and the move stands (the member is recorded on its new branch).</summary>
    [Fact]
    public async Task ARefusalAfterTheOldStyleMove_SaysWhatMoved()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Docs", Ct);
        RecordClone(f, ada, "Docs", "members/ada"); // an old-style member, its clone not on disk (so the move needs no transport)
        AgentGit.Host(repo.Path, "branch", "members/docs", "main");

        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ada, Ct));

        Assert.Contains("The branch members/docs in the team's repository", e.Message);
        Assert.Contains("Only this was changed before it was found: Ada's branch in the team's repository moved from members/ada to members/docs/ada (members/ada is kept)", e.Message);
        Assert.Contains("Nothing else was changed", e.Message);
        Assert.DoesNotContain("Nothing was changed", e.Message);
        Assert.Equal("members/docs/ada", f.Repos.Row(ada.Id)!.Branch);
    }

    /// <summary>With no move the message is the usual one; a move through the old branch says it was renamed.</summary>
    [Fact]
    public void WithMoves_ChangesTheMessageOnlyWhenSomethingMoved()
    {
        var e = new MemberBranchBlockedException(TeamRepos.BlockedMessage("Docs", "members/docs", "here", inDialog: true));
        Assert.Same(e, TeamRepos.WithMoves(e, []));
        var through = TeamRepos.MoveLine("Docs", "repository cloud", "members/docs", "members/docs/docs");
        Assert.Equal("Docs's branch in repository cloud moved from members/docs to members/docs/docs (members/docs was renamed, git cannot keep both)", through);
        var moved = TeamRepos.WithMoves(e, [through]);
        Assert.Contains(through, moved.Message);
        Assert.DoesNotContain("Nothing was changed", moved.Message);
        Assert.Contains("delete members/docs in the Git dialog", moved.Message);
    }

    // ------------------------------------------------------------------ item 9: "every merged" keeps another team's branch

    /// <summary>The Git dialog's Delete with no names plans every merged branch. A merged branch another team's member is
    /// recorded on (its clone not on disk, so not "checked out") is refused there too, naming the team and member, and the
    /// confirmed delete leaves it; the rest is deleted as usual.</summary>
    [Fact]
    public async Task DeleteEveryMerged_RefusesAnotherTeamsInUseBranch()
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
        AgentGit.Host(picked, "branch", "members/alpha/ada", "main"); // merged (at main), Alpha's Ada is on it
        AgentGit.Host(picked, "branch", "members/zed", "main");       // merged, nobody is on it
        RecordClone(f, alphaAda, "Alpha", "members/alpha/ada");

        var teamAda = new[] { adaManager.Name, adaAda.Name, adaBen.Name };
        var plan = await f.Repos.GitDeleteBranchesAsync(adaBen.TeamId, "Ada", teamAda, null, [], false, [], null, Ct);

        Assert.Contains("members/alpha/ada: REFUSED, team Alpha's member Ada is still on it", plan.Plan);
        Assert.DoesNotContain("delete members/alpha/ada", plan.Plan);
        Assert.Contains("delete members/zed", plan.Plan);
        var done = await f.Repos.GitDeleteBranchesAsync(adaBen.TeamId, "Ada", teamAda, null, [], false, [], plan.Token, Ct);
        Assert.Contains("deleted members/zed", done.Result);
        Assert.Contains("refs/heads/members/alpha/ada ", Refs(picked));
        Assert.DoesNotContain("refs/heads/members/zed ", Refs(picked));
    }
}
