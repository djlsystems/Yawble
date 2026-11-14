using System.Collections.Concurrent;
using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// Before a workflow starts the host brings the team branch and the member branches up to the base branch, in the
/// team repository, with fakes only: local repositories in temp dirs, no network, no clone ever touched by git.
/// </summary>
public class TeamSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static string Commit(string repo, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(repo, file), content);
        AgentGit.Host(repo, "add", file);
        AgentGit.Host(repo, "-c", "user.name=Person", "-c", "user.email=p@example.invalid", "commit", "-q", "-m", message);
        return AgentGit.Host(repo, "rev-parse", "HEAD");
    }

    /// <summary>A branch made in the team repository (as a fetched-back branch is) with one commit of its own, without moving HEAD.</summary>
    internal static string BranchWithWork(string repo, string branch, string file, string content)
    {
        var tree = AgentGit.Host(repo, "rev-parse", "HEAD^{tree}");
        var parent = AgentGit.Host(repo, "rev-parse", "HEAD");
        var blob = HashObject(repo, content);
        var index = Path.Combine(Path.GetTempPath(), "idx-" + Guid.NewGuid().ToString("N"));
        try
        {
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
            // A temporary index keeps the person's index and files alone.
            AgentGit.HostWithEnv(repo, env, "read-tree", tree);
            AgentGit.HostWithEnv(repo, env, "update-index", "--add", "--cacheinfo", $"100644,{blob},{file}");
            var newTree = AgentGit.HostWithEnv(repo, env, "write-tree");
            var sha = AgentGit.Host(repo, "-c", "user.name=Ada", "-c", "user.email=a@example.invalid", "commit-tree", newTree, "-p", parent, "-m", "work on " + file);
            AgentGit.Host(repo, "update-ref", "refs/heads/" + branch, sha);
            return sha;
        }
        finally
        {
            File.Delete(index);
        }
    }

    private static string HashObject(string repo, string content)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "blob-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tmp, content);
        try { return AgentGit.Host(repo, "hash-object", "-w", tmp); }
        finally { File.Delete(tmp); }
    }

    private static (TeamsFixture F, MemberRef Manager, MemberRef Ada, string Repo) Ready()
    {
        var f = new TeamsFixture();
        var (m, ada, _) = f.Team();
        var repo = f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Alpha", Ct).GetAwaiter().GetResult().Path;
        return (f, m, ada, repo);
    }

    [Fact]
    public async Task ATeamBranchBehindItsBase_IsFastForwarded_AndTheBundleCarriesIt()
    {
        var (f, _, ada, repo) = Ready();
        using var _f = f;
        AgentGit.Host(repo, "branch", "team/alpha");
        AgentGit.Host(repo, "branch", "members/alpha/ada");
        var main = Commit(repo, "new.txt", "new\n", "base moves on");

        var r = await f.Repos.SyncBranchesAsync(ada.TeamId, "Alpha", "7", Ct);

        Assert.Equal("synced", r.Outcome);
        Assert.Equal(main, r.BaseSha);
        Assert.Equal(main, AgentGit.Host(repo, "rev-parse", "refs/heads/team/alpha"));
        Assert.Equal(main, AgentGit.Host(repo, "rev-parse", "refs/heads/members/alpha/ada"));
        Assert.Contains("team/alpha", r.Moved);
        Assert.Null(f.Repos.SyncNote(ada.TeamId));
        // The shared view and its bundle were refreshed, so members fetch the synced branches.
        var bundle = Path.Combine(f.Repos.SharedRepoPath("Alpha"), TeamRepos.SharedBundleFile);
        Assert.Contains(main + " refs/heads/team/alpha", AgentGit.Host(repo, "bundle", "list-heads", bundle));
        // Nothing to do the second time.
        Assert.Equal("in-step", (await f.Repos.SyncBranchesAsync(ada.TeamId, "Alpha", "8", Ct)).Outcome);
    }

    [Fact]
    public async Task ATeamBranchWithItsOwnWork_GetsAMergeCommit_NamedForTheWorkflow_AndBaseNeverMoves()
    {
        var (f, _, ada, repo) = Ready();
        using var _f = f;
        var own = BranchWithWork(repo, "team/alpha", "team.txt", "team\n");
        var main = Commit(repo, "new.txt", "new\n", "base moves on");

        var r = await f.Repos.SyncBranchesAsync(ada.TeamId, "Alpha", "12", Ct);

        Assert.Equal("synced", r.Outcome);
        var tip = AgentGit.Host(repo, "rev-parse", "refs/heads/team/alpha");
        Assert.Equal(own + " " + main, AgentGit.Host(repo, "rev-list", "--parents", "-n1", tip)[(tip.Length + 1)..]);
        Assert.Equal("Sync main into team/alpha before workflow #12", AgentGit.Host(repo, "log", "-1", "--format=%s", tip));
        Assert.Equal(main, AgentGit.Host(repo, "rev-parse", "refs/heads/main")); // the base is never moved or rewritten
        Assert.Equal("team", AgentGit.Host(repo, "show", "team/alpha:team.txt"));
        Assert.Equal("new", AgentGit.Host(repo, "show", "team/alpha:new.txt"));
    }

    [Fact]
    public async Task AConflictingSync_IsAbortedCleanly_LeavesTheBranchAsItWas_AndCarriesTheNoteWithTheFiles()
    {
        var (f, _, ada, repo) = Ready();
        using var _f = f;
        Commit(repo, "shared.txt", "one\n", "base has shared.txt");
        var ownBase = BranchWithWork(repo, "team/alpha", "shared.txt", "team version\n");
        Commit(repo, "shared.txt", "main version\n", "base changes shared.txt");
        Commit(repo, "other.txt", "o\n", "and more");

        var r = await f.Repos.SyncBranchesAsync(ada.TeamId, "Alpha", "3", Ct);

        Assert.Equal("conflict", r.Outcome);
        Assert.Equal(["shared.txt"], r.Conflicts);
        Assert.Equal(ownBase, AgentGit.Host(repo, "rev-parse", "refs/heads/team/alpha")); // unchanged
        Assert.Equal("", AgentGit.Host(repo, "status", "--porcelain", "--untracked-files=no")); // the person's tree untouched
        Assert.False(File.Exists(Path.Combine(repo, ".git", "MERGE_HEAD")));
        const string expected = "team/alpha is 2 commits behind main and could not be synced automatically (conflicts in: shared.txt). "
            + "Merge main into it first and resolve the conflicts before other work.";
        Assert.Equal(expected, r.Note);
        Assert.Equal(expected, f.Repos.SyncNote(ada.TeamId));
        // Needs You: the team shows up until a later sync finds nothing wrong.
        Assert.Equal("Alpha", Assert.Single(f.Repos.SyncNotes()).Team);
    }

    [Fact]
    public async Task MemberBranches_WithoutWorkAreFastForwarded_WithWorkMergedOnlyWhenClean_AndAConflictingOneIsLeftAlone()
    {
        var (f, _, ada, repo) = Ready();
        using var _f = f;
        Commit(repo, "shared.txt", "one\n", "base has shared.txt");
        AgentGit.Host(repo, "branch", "members/alpha/idle");
        var clean = BranchWithWork(repo, "members/alpha/clean", "clean.txt", "c\n");
        var clash = BranchWithWork(repo, "members/alpha/clash", "shared.txt", "clash\n");
        var main = Commit(repo, "shared.txt", "main\n", "base changes shared.txt");

        var r = await f.Repos.SyncBranchesAsync(ada.TeamId, "Alpha", "5", Ct);

        Assert.Equal("synced", r.Outcome); // no team branch yet, and a member's conflict is not the team's
        Assert.Equal(main, AgentGit.Host(repo, "rev-parse", "members/alpha/idle"));
        var merged = AgentGit.Host(repo, "rev-parse", "members/alpha/clean");
        Assert.Equal(0, AgentGit.Host(repo, "rev-list", "--count", main, "^" + merged) == "0" ? 0 : 1);
        Assert.Equal("c", AgentGit.Host(repo, "show", "members/alpha/clean:clean.txt"));
        Assert.Equal(clash, AgentGit.Host(repo, "rev-parse", "members/alpha/clash"));
        Assert.DoesNotContain("members/alpha/clash", r.Moved);
        Assert.Null(r.Note);
        Assert.NotEqual(clean, merged);
    }

    [Fact]
    public async Task WithTheSettingOff_NothingIsSynced_UntilSyncNowIsPressed()
    {
        var (f, _, ada, repo) = Ready();
        using var _f = f;
        Assert.True(f.Repos.SyncsToBase(ada.TeamId)); // on by default
        AgentGit.Host(repo, "branch", "team/alpha");
        var before = AgentGit.Host(repo, "rev-parse", "team/alpha");
        Commit(repo, "new.txt", "n\n", "base moves on");
        f.Repos.SetSyncsToBase(ada.TeamId, false);

        var off = await f.Repos.SyncBranchesAsync(ada.TeamId, "Alpha", "9", Ct);
        Assert.Equal("off", off.Outcome);
        Assert.Equal(before, AgentGit.Host(repo, "rev-parse", "team/alpha"));

        var now = await f.Repos.SyncNowAsync(ada.TeamId, "Alpha", Ct);
        Assert.Equal("synced", now.Outcome);
        Assert.NotEqual(before, AgentGit.Host(repo, "rev-parse", "team/alpha"));
    }

    [Fact]
    public async Task AnOriginsNewBaseCommit_IsFetchedAndMergedIn_WithoutMovingThePersonsBranchOrPushingAnything()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        // The person's checkout is on another branch, so the base branch is free to be compared but is never moved by the sync.
        AgentGit.Host(picked, "branch", "team/alpha");
        var elsewhere = Path.Combine(f.Db.Dir, "elsewhere");
        AgentGit.Host(f.Db.Dir, "clone", "-q", "-b", "trunk", bare, elsewhere);
        var upstream = Commit(elsewhere, "up.txt", "up\n", "someone pushed to origin");
        AgentGit.Host(elsewhere, "push", "-q", "origin", "trunk");
        var originBefore = AgentGit.Host(bare, "for-each-ref");
        var localBase = AgentGit.Host(picked, "rev-parse", "trunk");

        var r = await f.Repos.SyncBranchesAsync(ada.TeamId, "Alpha", "4", Ct);

        Assert.Equal("synced", r.Outcome);
        Assert.Equal(upstream, AgentGit.Host(picked, "rev-parse", "team/alpha"));
        Assert.Equal(localBase, AgentGit.Host(picked, "rev-parse", "trunk"));
        Assert.Equal(originBefore, AgentGit.Host(bare, "for-each-ref")); // nothing pushed, nothing forced
    }

    [Fact]
    public async Task TheManagersFirstRunOfAWorkflow_SyncsFirst_AndAConflictIsAtTheTopOfItsInstruction()
    {
        using var f = new TeamsFixture();
        var runner = new CaptureRunner();
        await using var engine = new BusEngine(f.Db.OpenStore(), new TeamRepoRunner(runner, f.Repos),
            new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200) });
        var teamId = await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", "claude", Ct);
        var repo = (await f.Repos.EnsureTeamRepoAsync(teamId, "Alpha", Ct)).Path;
        Commit(repo, "shared.txt", "one\n", "base has shared.txt");
        BranchWithWork(repo, "team/alpha", "shared.txt", "team version\n");
        Commit(repo, "shared.txt", "main version\n", "base changes shared.txt");
        await engine.StartAsync(Ct);

        var root = await engine.PersonTellAsync("Alpha", "Manager", "Do the thing.", ct: Ct);
        var prompt = await runner.NextAsync(Ct);

        Assert.Contains("team/alpha is 1 commit behind main and could not be synced automatically (conflicts in: shared.txt)", prompt);
        Assert.True(prompt.IndexOf("could not be synced", StringComparison.Ordinal) < prompt.IndexOf("Do the thing.", StringComparison.Ordinal));
        Assert.Contains("Your branch was built on main at", prompt);
        Assert.Single(f.Repos.SyncNotes());
        Assert.True(root.Seq > 0);

        // A clean team is not nagged: once the person resolves it (merges main into team/alpha in the team repository),
        // the next workflow's sync clears the note. The Manager's clone still has the old team/alpha: its fetch-back
        // (whenever it lands) must not put the branch back.
        var unresolved = AgentGit.Host(repo, "rev-parse", "refs/heads/team/alpha");
        var resolved = AgentGit.Host(repo, "-c", "user.name=Person", "-c", "user.email=p@example.invalid",
            "commit-tree", "main^{tree}", "-p", unresolved, "-p", "main", "-m", "merge main, take main's shared.txt");
        AgentGit.Host(repo, "update-ref", "refs/heads/team/alpha", resolved, unresolved);
        await engine.PersonTellAsync("Alpha", "Manager", "Another thing.", ct: Ct);
        var second = await runner.NextAsync(Ct);
        Assert.DoesNotContain("could not be synced", second);
        Assert.Empty(f.Repos.SyncNotes());
    }

    [Fact]
    public async Task TheRunRecordKeepsTheBaseCommitTheCloneWasBuiltOn()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        var main = AgentGit.Host(f.Repos.RepoOf(ada.TeamId)!.Path, "rev-parse", "main");
        Assert.Equal(main, checkout.CloneBaseCommit);

        var records = new RunRecordStore(f.Db.ConnectionString);
        records.Started("r-1", null, "Alpha", "Ada", "claude", "headless", "fake");
        records.BaseCommit("r-1", checkout.CloneBaseCommit);
        Assert.Equal(main, records.Get("r-1")!.BaseCommit);

        // The base moves on and a sync brings members/alpha/ada up in the team repository, but Ada's clone has not merged it
        // yet: the next run's record names the base commit the CLONE is on, not the synced branch's.
        var repo = f.Repos.RepoOf(ada.TeamId)!.Path;
        AgentGit.Ok(checkout, "commit", "-q", "--allow-empty", "-m", "ada's work");
        await f.Repos.FetchBackAsync(ada, Ct);
        var newer = Commit(repo, "new.txt", "n\n", "base moves on");
        Assert.Equal("synced", (await f.Repos.SyncBranchesAsync(ada.TeamId, "Alpha", "2", Ct)).Outcome);
        Assert.Equal(newer, AgentGit.Host(repo, "merge-base", "members/alpha/ada", "main"));
        var again = await f.Repos.PrepareAsync(ada, Ct);
        Assert.Equal(main, again.CloneBaseCommit);
    }

    /// <summary>A member clone made, given one commit of the agent's own and fetched back; then the base moves on and a
    /// sync merges it into members/alpha/ada in the team repository. The clone itself is not updated.</summary>
    private static async Task<(TeamsFixture F, MemberRef Ada, MemberCheckout Clone, string Repo, string Synced)> SyncedWhileTheCloneWasNot(
        string? baseChange = null)
    {
        var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var repo = (await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Alpha", Ct)).Path;
        Commit(repo, "shared.txt", "one\n", "base has shared.txt");
        var clone = await f.Repos.PrepareAsync(ada, Ct);
        File.WriteAllText(Path.Combine(clone.ClonePath, "ada.txt"), "ada\n");
        AgentGit.Ok(clone, "add", "ada.txt");
        AgentGit.Ok(clone, "commit", "-q", "-m", "ada's first work");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        Commit(repo, baseChange ?? "new.txt", "main\n", "base moves on");
        var r = await f.Repos.SyncBranchesAsync(ada.TeamId, "Alpha", "6", Ct);
        Assert.Contains("members/alpha/ada", r.Moved);
        return (f, ada, clone, repo, AgentGit.Host(repo, "rev-parse", "refs/heads/members/alpha/ada"));
    }

    [Fact]
    public async Task AFetchBackOfACloneThatHasNotPulledTheSync_LeavesTheSyncedBranchWhereItIs()
    {
        var (f, ada, _, repo, synced) = await SyncedWhileTheCloneWasNot();
        using var _f = f;

        var fetched = await f.Repos.FetchBackAsync(ada, Ct); // the run made no new commit

        Assert.Null(fetched.Error);
        Assert.Equal(synced, AgentGit.Host(repo, "rev-parse", "refs/heads/members/alpha/ada")); // never moved backwards
        Assert.Equal("main", AgentGit.Host(repo, "show", "members/alpha/ada:new.txt"));
        var bundle = Path.Combine(f.Repos.SharedRepoPath("Alpha"), TeamRepos.SharedBundleFile);
        var heads = AgentGit.Host(repo, "bundle", "list-heads", bundle);
        Assert.Contains(synced + " refs/heads/members/alpha/ada", heads);
        Assert.DoesNotContain("refs/sandbox/", heads); // the staging ref stays the host's
        Assert.Empty(f.Repos.SyncNotes());
    }

    [Fact]
    public async Task ANewCommitOnTheOldBase_IsMergedWithTheSyncedBranch_WhenClean()
    {
        var (f, ada, clone, repo, synced) = await SyncedWhileTheCloneWasNot();
        using var _f = f;
        File.WriteAllText(Path.Combine(clone.ClonePath, "more.txt"), "more\n");
        AgentGit.Ok(clone, "add", "more.txt");
        AgentGit.Ok(clone, "commit", "-q", "-m", "more work on the old base");
        var work = AgentGit.Ok(clone, "rev-parse", "HEAD");

        var fetched = await f.Repos.FetchBackAsync(ada, Ct);

        Assert.Null(fetched.Error);
        var tip = AgentGit.Host(repo, "rev-parse", "refs/heads/members/alpha/ada");
        Assert.Equal(synced + " " + work, AgentGit.Host(repo, "rev-list", "--parents", "-n1", tip)[(tip.Length + 1)..]);
        Assert.Equal("main", AgentGit.Host(repo, "show", "members/alpha/ada:new.txt"));
        Assert.Equal("more", AgentGit.Host(repo, "show", "members/alpha/ada:more.txt"));
        Assert.Equal("", AgentGit.Host(repo, "status", "--porcelain", "--untracked-files=no"));
        // A later fetch-back of the same clone (still behind the merge) changes nothing.
        await f.Repos.FetchBackAsync(ada, Ct);
        Assert.Equal(tip, AgentGit.Host(repo, "rev-parse", "refs/heads/members/alpha/ada"));
    }

    [Fact]
    public async Task ANewCommitThatConflictsWithTheSync_LeavesTheBranch_ParksTheWork_AndRaisesTheNote()
    {
        var (f, ada, clone, repo, synced) = await SyncedWhileTheCloneWasNot(baseChange: "shared.txt");
        using var _f = f;
        File.WriteAllText(Path.Combine(clone.ClonePath, "shared.txt"), "ada's version\n");
        AgentGit.Ok(clone, "commit", "-q", "-am", "ada changes shared.txt");
        var work = AgentGit.Ok(clone, "rev-parse", "HEAD");

        var fetched = await f.Repos.FetchBackAsync(ada, Ct);

        Assert.Equal(synced, AgentGit.Host(repo, "rev-parse", "refs/heads/members/alpha/ada")); // unchanged
        Assert.Equal(work, AgentGit.Host(repo, "rev-parse", "refs/sandbox/fetch/members/alpha/ada")); // the work is kept, not lost
        Assert.False(File.Exists(Path.Combine(repo, ".git", "MERGE_HEAD")));
        Assert.Contains("shared.txt", fetched.Error);
        var note = Assert.Single(f.Repos.SyncNotes()).Note;
        Assert.Contains("members/alpha/ada", note);
        Assert.Contains("conflicts in: shared.txt", note);
        // The next workflow's sync keeps saying so while the work is still parked.
        var r = await f.Repos.SyncBranchesAsync(ada.TeamId, "Alpha", "7", Ct);
        Assert.Contains("members/alpha/ada", r.Note);
        Assert.Single(f.Repos.SyncNotes());
    }

    private sealed class CaptureRunner : IAgentRunner
    {
        private readonly System.Threading.Channels.Channel<string> _prompts = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public Task<RunResult> RunAsync(RunContext run, CancellationToken ct)
        {
            _prompts.Writer.TryWrite(TeamPrompt.For(run));
            return Task.FromResult(RunResult.Ok);
        }

        public async Task<string> NextAsync(CancellationToken ct) => await _prompts.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), ct);
    }
}
