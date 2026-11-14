using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// B016 gap tests (card 77): branch names for a team slug with spaces, a rehired same-named member, extra repositories
/// shared by two teams, old branches only on origin, and stale confirms. Local repositories and bare origins in temp
/// folders only, no network.
/// </summary>
public class TeamBranchGapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? Rev(string repo, string reference)
    {
        var r = AgentGit.Run(repo, Path.GetTempPath(), "rev-parse", "--verify", "-q", reference);
        return r.ExitCode == 0 ? r.Stdout : null;
    }

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

    /// <summary>A person's repository on trunk with no origin (no transport: runs anywhere git does).</summary>
    private static string LocalRepo(TeamsFixture f, string name = "local-repo")
    {
        var repo = Path.Combine(f.Db.Dir, name);
        Directory.CreateDirectory(repo);
        AgentGit.Host(repo, "init", "-q", "-b", "trunk");
        File.WriteAllText(Path.Combine(repo, "README.md"), "# x\n");
        AgentGit.Host(repo, "add", "README.md");
        AgentGit.Host(repo, "-c", "user.name=P", "-c", "user.email=p@x", "commit", "-q", "-m", "initial");
        return repo;
    }

    private static string CommitOn(string repo, string parent, string message)
    {
        var tree = AgentGit.Host(repo, "rev-parse", parent + "^{tree}");
        return AgentGit.Host(repo, "-c", "user.name=b", "-c", "user.email=b@x", "commit-tree", tree, "-p", parent, "-m", message);
    }

    private static MemberRef Hire(TeamsFixture f, long teamId, string name)
    {
        using var store = f.Db.OpenStore();
        var m = store.AddMember(teamId, name, MemberRole.Member, "claude");
        store.Subscribe(m.Qualified, MessageTypes.Instruction(m));
        store.InitCursor(m.Qualified);
        return m;
    }

    private static void Fire(TeamsFixture f, MemberRef member)
    {
        using var store = f.Db.OpenStore();
        store.InTransaction(() => store.DeleteMember(member));
    }

    // ------------------------------------------------------------------ names (pure)

    [Theory]
    [InlineData("Yawble Engineering Team", "DeveloperMarlo", "members/yawble-engineering-team/developermarlo")]
    [InlineData("  Web  App ", "Ada", "members/web-app/ada")]
    [InlineData("ÃœnÃ¯code Team", "Ben Two", "members/n-code-team/ben-two")]
    [InlineData("!!!", "???", "members/member/member")]
    public void BranchFor_SlugsTheTeamAndTheMember_AsOneSegmentEach(string team, string member, string expected)
    {
        var branch = TeamRepos.BranchFor(team, member);
        Assert.Equal(expected, branch);
        Assert.StartsWith(TeamRepos.MemberBranchPrefix(team), branch);
        Assert.Equal(TeamRepos.TeamBranchFor(team)["team/".Length..], TeamRepos.MemberBranchPrefix(team)["members/".Length..^1]);
        Assert.False(TeamRepos.IsLegacyMemberBranch(branch));
        Assert.True(TeamRepos.IsLegacyMemberBranch(TeamRepos.LegacyBranchFor(member)));
    }

    [Theory]
    [InlineData("members/ada", true)]
    [InlineData("members/alpha/ada", false)]
    [InlineData("members/", false)]
    [InlineData("team/ada", false)]
    [InlineData("membersx/ada", false)]
    public void IsLegacyMemberBranch_IsOneLevelUnderMembers(string branch, bool expected) =>
        Assert.Equal(expected, TeamRepos.IsLegacyMemberBranch(branch));

    // ------------------------------------------------------------------ no transport needed

    [Fact]
    public async Task ATeamSlugWithSpaces_TheGitDialogListsOnlyItsOwnMemberBranches_AndOldOnesAsOld()
    {
        using var f = new TeamsFixture();
        const string team = "Yawble Engineering Team";
        var (_, ada, _) = f.Team(team);
        var repo = LocalRepo(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, team, repo, Ct);
        var trunk = AgentGit.Host(repo, "rev-parse", "trunk");
        var unmerged = CommitOn(repo, trunk, "old work");
        AgentGit.Host(repo, "branch", "members/yawble-engineering-team/ada", trunk);
        AgentGit.Host(repo, "branch", "members/other-team/ada", trunk); // another team on the same folder
        AgentGit.Host(repo, "branch", "members/ben", unmerged);         // old-style, work not in trunk
        AgentGit.Host(repo, "branch", "members/ada", trunk);            // old-style, merged

        var s = await f.Repos.GitStatusAsync(ada.TeamId, team, Ct);

        Assert.Equal("team/yawble-engineering-team", s.TeamBranch);
        // Nobody is on members/ada or members/ben: old ones, listed once, in OldBranches only (card 75).
        Assert.Equal(["members/yawble-engineering-team/ada"], s.Members.Select(m => m.Branch));
        Assert.Equal([new TeamGitOldBranch("members/ada", true, false, true), new TeamGitOldBranch("members/ben", true, false, false)], s.OldBranches);
    }

    /// <summary>
    /// Card 75: git.status lists each old-style branch once. One a member is still recorded on (not moved yet) is that
    /// member's branch, in Members only; one nobody is on is old, in OldBranches only. No member clone, no transport.
    /// </summary>
    [Fact]
    public async Task GitStatus_ListsAnOldStyleBranchOnce_AsAMemberBranchWhileAMemberIsOnIt_ElseAsOld()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var repo = LocalRepo(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", repo, Ct);
        var trunk = AgentGit.Host(repo, "rev-parse", "trunk");
        AgentGit.Host(repo, "branch", "members/ada", CommitOn(repo, trunk, "ada's work")); // Ada: recorded here, not moved yet
        AgentGit.Host(repo, "branch", "members/cy", trunk);                                 // nobody's
        AgentGit.Host(repo, "branch", "members/alpha/ben", trunk);
        RecordClone(f, ada.Id, "members/ada");

        var s = await f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct);

        Assert.Equal(["members/ada", "members/alpha/ben"], s.Members.Select(m => m.Branch).Order());
        Assert.Equal(1, Assert.Single(s.Members, m => m.Branch == "members/ada").AheadOfBase);
        Assert.Equal([new TeamGitOldBranch("members/cy", Local: true, Remote: false, Merged: true)], s.OldBranches);
        var all = s.Members.Select(m => m.Branch).Concat(s.OldBranches.Select(o => o.Name)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    private static void RecordClone(TeamsFixture f, long memberId, string branch)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(f.Db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO member_clones (member_id, path, branch, created_at) VALUES ($m, $p, $b, $c);";
        cmd.Parameters.AddWithValue("$m", memberId);
        cmd.Parameters.AddWithValue("$p", Path.Combine(f.Db.Dir, "no-clone"));
        cmd.Parameters.AddWithValue("$b", branch);
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    /// <summary>Card 75, the rehire decision without transport: a clone left on disk with no row is recorded on the branch
    /// it is on when that is the member's old-style one; anything else keeps the member's own new-style branch.</summary>
    [Theory]
    [InlineData(MemberRole.Member, "Ada", "members/ada", "members/ada")]
    [InlineData(MemberRole.Member, "Ben Two", "members/ben-two", "members/ben-two")]
    [InlineData(MemberRole.Member, "Ada", "members/alpha/ada", null)] // a post-B016 clone: nothing to adopt
    [InlineData(MemberRole.Member, "Ada", "members/ben", null)]       // another member's old branch
    [InlineData(MemberRole.Member, "Ada", "main", null)]
    [InlineData(MemberRole.Member, "Ada", null, null)]                // detached or unreadable HEAD
    [InlineData(MemberRole.Manager, "Manager", "members/manager", null)] // a manager is on the team branch
    public void ARehiredMembersReusedClone_IsRecordedOnItsOldStyleBranch_OnlyWhenItIsOnIt(MemberRole role, string name, string? head, string? expected) =>
        Assert.Equal(expected, TeamRepos.LegacyBranchToAdopt(role, name, head));

    /// <summary>Card 75: the half of the rehire fix that reads the clone left on disk (by file, as the move does: no git in
    /// the clone, no transport). The full rehire through PrepareAsync is ARehiredSameNamedMember_WithAPreB016CloneOnDisk_NoOrigin_...</summary>
    [Fact]
    public void ACloneLeftOnDisk_IsAdoptedOnTheBranchItsHeadIsOn_OnlyWhenThatIsTheMembersOldStyleBranch()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        string Clone(string name, string? branch)
        {
            var dir = Path.Combine(f.Db.Dir, name);
            Directory.CreateDirectory(dir);
            if (branch is not null) AgentGit.Host(dir, "init", "-q", "-b", branch);
            return dir;
        }

        Assert.Equal("members/ada", TeamRepos.AdoptLegacyClone(ada, Clone("pre-b016", "members/ada")));
        Assert.Null(TeamRepos.AdoptLegacyClone(ada, Clone("post-b016", "members/alpha/ada")));
        Assert.Null(TeamRepos.AdoptLegacyClone(ada, Clone("someone-else", "members/ben")));
        Assert.Null(TeamRepos.AdoptLegacyClone(ada, Clone("empty", null)));
        Assert.Null(TeamRepos.AdoptLegacyClone(ada, Path.Combine(f.Db.Dir, "missing")));
    }

    [Fact]
    public async Task AConfirmUsedTwice_IsStaleTheSecondTime_AndAMadeUpTokenDeletesNothing()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var repo = LocalRepo(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", repo, Ct);
        var trunk = AgentGit.Host(repo, "rev-parse", "trunk");
        AgentGit.Host(repo, "branch", "members/ben", trunk);
        AgentGit.Host(repo, "branch", "members/alpha/ben", trunk);
        Task<TeamGitDelete> Delete(string? confirm, params string[] branches) =>
            f.Repos.GitDeleteBranchesAsync(ada.TeamId, "Alpha", ["Manager", "Ada", "Ben"], null, branches, false, [], confirm, Ct);

        var forged = await Delete("not-the-token", "members/ben");
        Assert.True(forged.Stale);
        Assert.Null(forged.Result);
        Assert.Equal(trunk, Rev(repo, "refs/heads/members/ben"));

        var plan = await Delete(null, "members/ben");
        Assert.False(plan.Empty);
        Assert.False(plan.Stale);
        var done = await Delete(plan.Token, "members/ben");
        Assert.False(done.Stale);
        Assert.Contains($"deleted members/ben; was {trunk}", done.Result);
        Assert.Null(Rev(repo, "refs/heads/members/ben"));

        // A double confirm (the button pressed twice): the branch is gone, so the plan changed and nothing more happens.
        var twice = await Delete(plan.Token, "members/ben");
        Assert.True(twice.Stale);
        Assert.Null(twice.Result);
        Assert.Equal(trunk, Rev(repo, "refs/heads/members/alpha/ben"));
        Assert.Empty((await f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct)).OldBranches);
    }

    [Fact]
    public async Task AConfirmIsStale_WhenAnotherBranchTheSamePlanNamesAppears()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var repo = LocalRepo(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", repo, Ct);
        var trunk = AgentGit.Host(repo, "rev-parse", "trunk");
        AgentGit.Host(repo, "branch", "members/ben", trunk);
        var plan = await f.Repos.GitDeleteBranchesAsync(ada.TeamId, "Alpha", ["Ada", "Ben"], null, ["members/ben", "members/cy"], false, [], null, Ct);
        AgentGit.Host(repo, "branch", "members/cy", trunk); // appears between plan and confirm

        var again = await f.Repos.GitDeleteBranchesAsync(ada.TeamId, "Alpha", ["Ada", "Ben"], null, ["members/ben", "members/cy"], false, [], plan.Token, Ct);
        Assert.True(again.Stale);
        Assert.Equal(trunk, Rev(repo, "refs/heads/members/ben"));
        Assert.Equal(trunk, Rev(repo, "refs/heads/members/cy"));
    }

    // ------------------------------------------------------------------ need a local origin / member clone

    [Fact]
    public async Task ATeamSlugWithSpaces_PreparesAndPublishesOnItsSluggedBranch()
    {
        using var f = new TeamsFixture();
        const string team = "Yawble Engineering Team";
        var (_, ada, _) = f.Team(team);
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, team, picked, Ct);
        var c = await f.Repos.PrepareAsync(ada, Ct);
        Assert.Equal("members/yawble-engineering-team/ada", c.Branch);
        Assert.Equal("members/yawble-engineering-team/ada", Ok(c.ClonePath, c.Workspace, "symbolic-ref", "--short", "HEAD"));
        var tip = Commit(c.ClonePath, c.Workspace, "a.txt");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        var report = await f.Repos.PublishAsync(ada, Ct);
        Assert.Equal(TeamPublishResult.Published, report.Result);
        Assert.Equal(["members/yawble-engineering-team/ada"], report.Branches);
        Assert.Equal(tip, AgentGit.Host(bare, "rev-parse", "refs/heads/members/yawble-engineering-team/ada"));
    }

    [Fact]
    public async Task TwoTeamsSharingAnExtraRepository_WithASameNamedMember_PushToDifferentBranches()
    {
        using var f = new TeamsFixture();
        var (_, alphaAda, _) = f.Team("Alpha");
        var (_, betaAda, _) = f.Team("Beta");
        var dir = Path.Combine(f.Db.Dir, "cloud");
        Directory.CreateDirectory(dir);
        var (alphaCloud, cloudOrigin) = TeamReposTests.PersonsRepoWithOrigin(dir);
        var betaCloud = Path.Combine(f.Db.Dir, "beta-cloud");
        AgentGit.Host(f.Db.Dir, "clone", "-q", "-b", "trunk", cloudOrigin, betaCloud);
        await f.Repos.AddExtraAsync(alphaAda.TeamId, "Alpha", "cloud", alphaCloud, null, writable: true, Ct);
        await f.Repos.AddExtraAsync(betaAda.TeamId, "Beta", "cloud", betaCloud, null, writable: true, Ct);

        var a = Assert.Single((await f.Repos.PrepareAsync(alphaAda, Ct)).Extras);
        var b = Assert.Single((await f.Repos.PrepareAsync(betaAda, Ct)).Extras);
        Assert.Equal("members/alpha/ada", a.Branch);
        Assert.Equal("members/beta/ada", b.Branch);
        var aTip = Commit(a.Path, Path.GetTempPath(), "alpha.txt");
        var bTip = Commit(b.Path, Path.GetTempPath(), "beta.txt");
        Assert.Null((await f.Repos.FetchBackAsync(alphaAda, Ct)).Error);
        Assert.Null((await f.Repos.FetchBackAsync(betaAda, Ct)).Error);
        Assert.Contains("cloud: members/alpha/ada", (await f.Repos.PublishAsync(alphaAda, Ct)).Branches);
        Assert.Contains("cloud: members/beta/ada", (await f.Repos.PublishAsync(betaAda, Ct)).Branches);
        Assert.Equal(aTip, AgentGit.Host(cloudOrigin, "rev-parse", "refs/heads/members/alpha/ada"));
        Assert.Equal(bTip, AgentGit.Host(cloudOrigin, "rev-parse", "refs/heads/members/beta/ada"));
    }

    [Fact]
    public async Task ARehiredSameNamedMember_AfterB016_GetsTheSameBranch_AndItsEarlierWorkComesBack()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (picked, _) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        var first = await f.Repos.PrepareAsync(ada, Ct);
        var tip = Commit(first.ClonePath, first.Workspace, "first.txt");
        Fire(f, ada);
        Assert.Null(f.Repos.Row(ada.Id));

        var again = Hire(f, ada.TeamId, "Ada");
        var c = await f.Repos.PrepareAsync(again, Ct);
        Assert.Equal("members/alpha/ada", c.Branch);
        Assert.Equal(first.ClonePath, c.ClonePath); // the clone on disk is reused
        Assert.Equal(c.Branch, Ok(c.ClonePath, c.Workspace, "symbolic-ref", "--short", "HEAD"));
        Assert.True((await f.Repos.FetchBackAsync(again, Ct)).Fetched);
        Assert.Equal(tip, Rev(picked, "refs/heads/members/alpha/ada"));
    }

    /// <summary>
    /// A member hired before B016 is removed (its row goes, its clone stays on disk on members/&lt;name&gt;) and a member of
    /// the same name is hired. Its record must name the branch its clone is actually on, or the next fetch-back looks
    /// for a branch the clone does not have and the clone's work never comes back.
    /// </summary>
    [Fact(Skip = "Needs git local transport (clone, fetch into the shared view, a local origin), which cannot start in the agents' sandbox (MSYS2 sh.exe 0xC0000142). The fault is fixed (card 75): run it outside the sandbox.")]
    public async Task ARehiredSameNamedMember_WhoseOldCloneIsStillOnTheOldBranch_IsMovedOrKeptConsistently()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (picked, _) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        var first = await f.Repos.PrepareAsync(ada, Ct);
        Ok(first.ClonePath, first.Workspace, "branch", "-m", "members/ada"); // the pre-B016 clone
        var tip = Commit(first.ClonePath, first.Workspace, "unfetched.txt");
        Fire(f, ada);

        var again = Hire(f, ada.TeamId, "Ada");
        var c = await f.Repos.PrepareAsync(again, Ct);
        var head = Ok(c.ClonePath, c.Workspace, "symbolic-ref", "--short", "HEAD");
        Assert.Equal(f.Repos.Row(again.Id)!.Branch, head);
        Assert.Equal(c.Branch, head);
        Assert.True((await f.Repos.FetchBackAsync(again, Ct)).Fetched);
        Assert.Equal(tip, Rev(picked, "refs/heads/" + c.Branch));
    }

    /// <summary>
    /// The same rehire as above on a person's repository with no origin (no push needed; it still needs git's local
    /// transport for the first clone and the fetch-back, which cannot start in the agents' sandbox).
    /// Was a FAULT (core, card 77), fixed in card 75: RemoveMemberAsync deletes the member (its member_clones row goes) but leaves its clone on
    /// disk. The rehired member has no row, so MoveLegacyBranchLockedAsync does nothing, CheckoutOf names
    /// members/&lt;team&gt;/&lt;name&gt;, and PrepareAsync reuses the clone and records that branch while the clone's HEAD is
    /// still members/&lt;name&gt;. Fetch-back then looks for a branch the clone does not have, and later starts never move it
    /// (the row's branch is not old-style). The unfetched work on members/&lt;name&gt; never comes back.
    /// Now the move at start adopts a clone on disk with no row when it is on members/&lt;name&gt; (records it, then moves it).
    /// </summary>
    [Fact(Skip = "Needs git local transport (the first clone, the fetch into the shared view, the move's ls-remote of the clone, fetch-back), which cannot start in the agents' sandbox (MSYS2 sh.exe 0xC0000142). The fault is fixed (card 75); its no-transport tests are ACloneLeftOnDisk_... and ARehiredMembersReusedClone_...")]
    public async Task ARehiredSameNamedMember_WithAPreB016CloneOnDisk_NoOrigin_IsRecordedOnTheBranchItsCloneIsOn()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var repo = LocalRepo(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", repo, Ct);
        var first = await f.Repos.PrepareAsync(ada, Ct);
        Ok(first.ClonePath, first.Workspace, "branch", "-m", "members/ada"); // the pre-B016 clone
        var tip = Commit(first.ClonePath, first.Workspace, "unfetched.txt");
        Fire(f, ada);

        var again = Hire(f, ada.TeamId, "Ada");
        var c = await f.Repos.PrepareAsync(again, Ct);
        Assert.Equal(first.ClonePath, c.ClonePath);
        var head = Ok(c.ClonePath, c.Workspace, "symbolic-ref", "--short", "HEAD");
        Assert.Equal(head, f.Repos.Row(again.Id)!.Branch); // expected by reading the code: members/ada vs members/alpha/ada
        Assert.Equal(head, c.Branch);
        await f.Repos.FetchBackAsync(again, Ct);
        Assert.Equal(tip, Rev(repo, "refs/heads/" + c.Branch));
    }

    [Fact]
    public async Task AnOldStyleBranchOnlyOnOrigin_ThatIsMergedIntoOriginsBase_IsMerged_AndDeletesWithoutTheUnmergedTick()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        var trunk = AgentGit.Host(picked, "rev-parse", "trunk");
        AgentGit.Host(picked, "push", "-q", "origin", trunk + ":refs/heads/members/cy");
        Assert.Equal("fetched", (await f.Repos.GitFetchAsync(ada.TeamId, "Alpha", Ct)).Outcome);

        var old = Assert.Single((await f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct)).OldBranches);
        Assert.Equal(new TeamGitOldBranch("members/cy", Local: false, Remote: true, Merged: true), old);

        var plan = await f.Repos.GitDeleteBranchesAsync(ada.TeamId, "Alpha", ["Ada", "Ben"], null, ["members/cy"], true, [], null, Ct);
        Assert.False(plan.Empty);
        var done = await f.Repos.GitDeleteBranchesAsync(ada.TeamId, "Alpha", ["Ada", "Ben"], null, ["members/cy"], true, [], plan.Token, Ct);
        Assert.Contains($"deleted origin/members/cy; was {trunk}", done.Result);
        Assert.Null(Rev(bare, "refs/heads/members/cy"));
        Assert.Equal(trunk, Rev(bare, "refs/heads/trunk"));
    }
}
