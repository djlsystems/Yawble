using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// Member branches carry the team (members/&lt;team&gt;/&lt;name&gt;), and a member still on an old-style
/// members/&lt;name&gt; moves to the new name at its next start, with its commits; the old branch is never deleted or
/// forced. Also: branch names for a team slug with spaces, a rehired same-named member, extra repositories shared by
/// two teams, old branches only on origin, and stale confirms. Local repositories and bare origins in temp folders
/// only, no network.
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

    /// <summary>Puts a freshly prepared member back as an old-style member (without the team in its branch): its clone and its record on members/&lt;name&gt;.</summary>
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

        // Ada's old-style life: work fetched back and pushed on members/ada; then one more commit in the clone.
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
        // it); a sync does not move the one left behind.
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
        SandboxGit.RequireLocalTransport();
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
        SandboxGit.RequireLocalTransport();
        // Team "Ada" with a member "Ada": members/ada/ada cannot exist beside members/ada, so the member's own old
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

    // ------------------------------------------------------------------ names, rehired members, shared repositories, stale confirms

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

    /// <summary>
    /// The origin and clone tests here need nothing Windows-only and nothing Linux lacks, only git's local transport
    /// (clone of a folder, fetch and push to a folder, ls-remote of one). That starts git helper processes, which a
    /// sandbox may refuse (in the Windows agents' sandbox Git for Windows' MSYS2 runtime fails to start, 0xC0000142).
    /// So instead of a fixed skip they try one local clone first and run wherever it works (Linux, macOS, a Windows host).
    /// </summary>
    private static readonly Lazy<string?> LocalTransportFault = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "yawble-transport-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Path.Combine(dir, "src");
            Directory.CreateDirectory(src);
            AgentGit.Host(src, "init", "-q", "-b", "trunk");
            AgentGit.Host(src, "-c", "user.name=P", "-c", "user.email=p@x", "commit", "-q", "--allow-empty", "-m", "probe");
            var r = AgentGit.Run(dir, dir, "-c", "protocol.file.allow=always", "clone", "-q", "--no-local", src, Path.Combine(dir, "dst"));
            return r.ExitCode == 0 ? null : $"exit {r.ExitCode}: {r.Stderr}";
        }
        catch (Exception e) { return e.Message; }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    });

    private static void SkipUnlessLocalTransport(string needs)
    {
        if (TransportSkipReason(LocalTransportFault.Value, needs, OperatingSystem.IsWindows()) is { } reason) Assert.Skip(reason);
    }

    /// <summary>
    /// The skip reason for a failed transport probe, or null when the probe worked (the test runs). It quotes git's own
    /// error and names the likely cause for the OS it runs on: on Windows the agents' sandbox (MSYS2 failing to start);
    /// elsewhere (Linux, Fedora included, and macOS) no sandbox is assumed, so it says git's transport helpers cannot run.
    /// </summary>
    internal static string? TransportSkipReason(string? fault, string needs, bool windows)
    {
        if (fault is null) return null;
        var start = $"Needs git's local transport ({needs}), and the probe 'git clone --no-local' of a temp folder fails on this machine ({fault.Trim()}). ";
        var cause = windows
            ? "On Windows that is the agents' sandbox stopping Git for Windows' MSYS2 runtime from starting (0xC0000142); the test runs on a Windows host. "
            : "Nothing in the test is OS-specific, so on Linux or macOS this means git or its transport helpers (git-upload-pack, git-receive-pack) cannot run here: check that git is installed and on PATH. ";
        return start + cause;
    }

    /// <summary>The same, with a faked probe result: the reason names git's error and the cause for the OS it is on,
    /// and never blames the Windows sandbox on Linux.</summary>
    [Theory]
    [InlineData(false, "exit 128: fatal: Could not read from remote repository.")]
    [InlineData(false, "An error occurred trying to start process 'git' with working directory '/tmp/x'. No such file or directory")]
    [InlineData(true, "exit 128: error: cannot spawn sh: ... 0xC0000142")]
    public void TheTransportSkipReason_QuotesGitsError_AndNamesTheCauseForItsOS(bool windows, string fault)
    {
        Assert.Null(TransportSkipReason(null, "clone", windows));
        var reason = TransportSkipReason(fault, "clone, fetch-back", windows)!;
        Assert.Contains("(clone, fetch-back)", reason);
        Assert.Contains(fault, reason);
        if (windows) Assert.Contains("MSYS2", reason);
        else
        {
            Assert.DoesNotContain("MSYS2", reason);
            Assert.DoesNotContain("sandbox", reason);
            Assert.Contains("Linux", reason);
            Assert.Contains("git-upload-pack", reason);
        }
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
    [InlineData("Yawble Engineering Team", "SeniorDeveloper", "members/yawble-engineering-team/seniordeveloper")]
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
        // Nobody is on members/ada or members/ben: old ones, listed once, in OldBranches only.
        Assert.Equal(["members/yawble-engineering-team/ada"], s.Members.Select(m => m.Branch));
        Assert.Equal([new TeamGitOldBranch("members/ada", true, false, true), new TeamGitOldBranch("members/ben", true, false, false)], s.OldBranches);
    }

    /// <summary>
    /// git.status lists each old-style branch once. One a member is still recorded on (not moved yet) is that
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

    /// <summary>The rehire decision without transport: a clone left on disk with no row is recorded on the branch
    /// it is on when that is the member's old-style one; anything else keeps the member's own new-style branch.</summary>
    [Theory]
    [InlineData(MemberRole.Member, "Ada", "members/ada", "members/ada")]
    [InlineData(MemberRole.Member, "Ben Two", "members/ben-two", "members/ben-two")]
    [InlineData(MemberRole.Member, "Ada", "members/alpha/ada", null)] // a clone already on members/<team>/<name>: nothing to adopt
    [InlineData(MemberRole.Member, "Ada", "members/ben", null)]       // another member's old branch
    [InlineData(MemberRole.Member, "Ada", "main", null)]
    [InlineData(MemberRole.Member, "Ada", null, null)]                // detached or unreadable HEAD
    [InlineData(MemberRole.Manager, "Manager", "members/manager", null)] // a manager is on the team branch
    public void ARehiredMembersReusedClone_IsRecordedOnItsOldStyleBranch_OnlyWhenItIsOnIt(MemberRole role, string name, string? head, string? expected) =>
        Assert.Equal(expected, TeamRepos.LegacyBranchToAdopt(role, name, head));

    /// <summary>The half of a rehire that reads the clone left on disk (by file, as the move does: no git in
    /// the clone, no transport). The full rehire through PrepareAsync is
    /// <see cref="ARehiredSameNamedMember_WithAnOldStyleCloneOnDisk_NoOrigin_IsRecordedOnTheBranchItsCloneIsOn"/>.</summary>
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

        Assert.Equal("members/ada", TeamRepos.AdoptLegacyClone(ada, Clone("old-style", "members/ada")));
        Assert.Null(TeamRepos.AdoptLegacyClone(ada, Clone("team-style", "members/alpha/ada")));
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
        SkipUnlessLocalTransport("push to a local origin, the member clone, fetch-back, publish");
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
        SkipUnlessLocalTransport("push to a local origin, a clone of it, the member clones, fetch-back, publish");
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
    public async Task ARehiredSameNamedMember_WithTeamBranches_GetsTheSameBranch_AndItsEarlierWorkComesBack()
    {
        SkipUnlessLocalTransport("push to a local origin, the member clone, fetch-back");
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
    /// A member hired before branches carried the team is removed (its row goes, its clone stays on disk on members/&lt;name&gt;) and a member of
    /// the same name is hired. Its record must name the branch its clone is actually on, or the next fetch-back looks
    /// for a branch the clone does not have and the clone's work never comes back.
    /// </summary>
    [Fact]
    public async Task ARehiredSameNamedMember_WhoseOldCloneIsStillOnTheOldBranch_IsMovedOrKeptConsistently()
    {
        SkipUnlessLocalTransport("clone, fetch into the shared view, push to a local origin");
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (picked, _) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        var first = await f.Repos.PrepareAsync(ada, Ct);
        Ok(first.ClonePath, first.Workspace, "branch", "-m", "members/ada"); // the old-style clone (members/<name>, without the team)
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
    /// The same rehire on a person's repository with no origin (no push needed; it still needs git's local transport
    /// for the first clone and the fetch-back, so it skips only where that cannot start: SkipUnlessLocalTransport).
    /// RemoveMemberAsync deletes the member (its member_clones row goes) but leaves its clone on disk. The move at start
    /// adopts a clone on disk with no row when it is on members/&lt;name&gt; (records it, then moves it), so the record
    /// names the branch the clone's HEAD is on and the unfetched work on members/&lt;name&gt; comes back.
    /// </summary>
    [Fact]
    public async Task ARehiredSameNamedMember_WithAnOldStyleCloneOnDisk_NoOrigin_IsRecordedOnTheBranchItsCloneIsOn()
    {
        SkipUnlessLocalTransport("the first clone, the fetch into the shared view, the move's ls-remote of the clone, fetch-back");
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var repo = LocalRepo(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", repo, Ct);
        var first = await f.Repos.PrepareAsync(ada, Ct);
        Ok(first.ClonePath, first.Workspace, "branch", "-m", "members/ada"); // the old-style clone (members/<name>, without the team)
        var tip = Commit(first.ClonePath, first.Workspace, "unfetched.txt");
        Fire(f, ada);

        var again = Hire(f, ada.TeamId, "Ada");
        var c = await f.Repos.PrepareAsync(again, Ct);
        Assert.Equal(first.ClonePath, c.ClonePath);
        var head = Ok(c.ClonePath, c.Workspace, "symbolic-ref", "--short", "HEAD");
        Assert.Equal(head, f.Repos.Row(again.Id)!.Branch);
        Assert.Equal(head, c.Branch);
        await f.Repos.FetchBackAsync(again, Ct);
        Assert.Equal(tip, Rev(repo, "refs/heads/" + c.Branch));
    }

    [Fact]
    public async Task AnOldStyleBranchOnlyOnOrigin_ThatIsMergedIntoOriginsBase_IsMerged_AndDeletesWithoutTheUnmergedTick()
    {
        SkipUnlessLocalTransport("push to a local origin, fetch from it, delete a branch on it");
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
