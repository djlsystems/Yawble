using Yawble.Core.Bus;
using Yawble.Core.Ops;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The behind-count, Sync now, the doctor check, and the merge-clean flag on completion. Fakes only: local repositories
/// in temp dirs, no network, no port. These tests never ask for the shared view (it needs git to fetch between local repositories).
/// </summary>
public class TeamBehindTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Who = ["-c", "user.name=Person", "-c", "user.email=p@example.invalid"];

    private static string CommitAt(string repo, string file, string when)
    {
        File.WriteAllText(Path.Combine(repo, file), file + "\n");
        AgentGit.Host(repo, "add", file);
        var env = new Dictionary<string, string> { ["GIT_COMMITTER_DATE"] = when, ["GIT_AUTHOR_DATE"] = when };
        AgentGit.HostWithEnv(repo, env, [.. Who, "commit", "-q", "-m", file]);
        return AgentGit.Host(repo, "rev-parse", "HEAD");
    }

    private static (TeamsFixture F, long TeamId, string Repo) Ready()
    {
        var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var repo = f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Alpha", Ct).GetAwaiter().GetResult().Path;
        return (f, ada.TeamId, repo);
    }

    [Fact]
    public async Task TheBehindCount_NamesTheBranchTheBaseAndHowOldTheOldestMissingCommitIs()
    {
        var (f, id, repo) = Ready();
        using var _f = f;
        Assert.Null(await f.Repos.BehindBaseAsync(id, "Alpha", DateTimeOffset.UtcNow, Ct)); // no team branch yet: nothing to be behind
        AgentGit.Host(repo, "branch", "team/alpha");
        var now = DateTimeOffset.UtcNow;
        CommitAt(repo, "a.txt", now.AddDays(-5).ToString("o"));
        CommitAt(repo, "b.txt", now.AddDays(-1).ToString("o"));

        var b = await f.Repos.BehindBaseAsync(id, "Alpha", now, Ct);

        Assert.NotNull(b);
        Assert.Equal(2, b.Behind);
        Assert.Equal("team/alpha is 2 commits behind main", b.Text);
        Assert.InRange(b.Age!.Value.TotalDays, 4.9, 5.1);
        Assert.Contains("team/alpha is 2 commits behind main", await f.Repos.BehindLineAsync(id, "Alpha", Ct));

        await f.Repos.SyncNowAsync(id, "Alpha", Ct);
        Assert.Equal(0, (await f.Repos.BehindBaseAsync(id, "Alpha", now, Ct))!.Behind);
        Assert.Null(await f.Repos.BehindLineAsync(id, "Alpha", Ct)); // in step: nothing to say
        Assert.Equal("team/alpha is 1 commit behind main", (new TeamBehind("team/alpha", "main", 1, null)).Text);
    }

    [Fact]
    public async Task SyncNow_WorksWithTheSettingOff_WhichOnlyWarns_AndNeverRewritesOrMovesTheBase()
    {
        var (f, id, repo) = Ready();
        using var _f = f;
        var own = TeamSyncTests.BranchWithWork(repo, "team/alpha", "team.txt", "team\n");
        var main = TeamSyncTests.Commit(repo, "new.txt", "new\n", "base moves on");
        f.Repos.SetSyncsToBase(id, false);

        // Off: a new workflow's sync does nothing, the warning is all there is.
        Assert.Equal("off", (await f.Repos.SyncBranchesAsync(id, "Alpha", "1", Ct)).Outcome);
        Assert.Equal(own, AgentGit.Host(repo, "rev-parse", "team/alpha"));
        var line = await f.Repos.BehindLineAsync(id, "Alpha", Ct);
        Assert.Contains("team/alpha is 1 commit behind main", line);
        Assert.Contains("off for this team", line);

        // Sync now still works, as a merge commit on top of the branch's own tip: never a rebase, never a force.
        var r = await f.Repos.SyncNowAsync(id, "Alpha", Ct);
        Assert.Equal("synced", r.Outcome);
        var tip = AgentGit.Host(repo, "rev-parse", "team/alpha");
        AgentGit.Host(repo, "merge-base", "--is-ancestor", own, tip); // the old tip is still in its history
        AgentGit.Host(repo, "merge-base", "--is-ancestor", main, tip);
        Assert.Equal(own + " " + main, AgentGit.Host(repo, "rev-list", "--parents", "-n1", tip)[(tip.Length + 1)..]);
        Assert.Equal(main, AgentGit.Host(repo, "rev-parse", "main"));
        Assert.Null(await f.Repos.BehindLineAsync(id, "Alpha", Ct));
    }

    [Fact]
    public async Task SyncNowWithAConflict_ChangesNothing_AndSaysWhichFiles()
    {
        var (f, id, repo) = Ready();
        using var _f = f;
        TeamSyncTests.Commit(repo, "shared.txt", "one\n", "base has shared.txt");
        var own = TeamSyncTests.BranchWithWork(repo, "team/alpha", "shared.txt", "team\n");
        TeamSyncTests.Commit(repo, "shared.txt", "main\n", "base changes it");

        var r = await f.Repos.SyncNowAsync(id, "Alpha", Ct);

        Assert.Equal("conflict", r.Outcome);
        Assert.Equal(["shared.txt"], r.Conflicts);
        Assert.Equal(own, AgentGit.Host(repo, "rev-parse", "team/alpha"));
        Assert.Equal(1, (await f.Repos.BehindBaseAsync(id, "Alpha", DateTimeOffset.UtcNow, Ct))!.Behind);
    }

    [Fact]
    public async Task TheMergeCheck_IsCleanWhenItMerges_AndNamesTheFilesWhenItDoesNot()
    {
        var (f, id, repo) = Ready();
        using var _f = f;
        Assert.Null(await f.Repos.MergeCheckAsync(id, "Alpha", Ct)); // no team branch
        TeamSyncTests.Commit(repo, "shared.txt", "one\n", "base has shared.txt");
        TeamSyncTests.BranchWithWork(repo, "team/alpha", "other.txt", "t\n");
        TeamSyncTests.Commit(repo, "shared.txt", "main\n", "base changes it");
        Assert.True((await f.Repos.MergeCheckAsync(id, "Alpha", Ct))!.Clean);

        AgentGit.Host(repo, "update-ref", "-d", "refs/heads/team/alpha");
        TeamSyncTests.BranchWithWork(repo, "team/alpha", "shared.txt", "team\n");
        TeamSyncTests.Commit(repo, "shared.txt", "main again\n", "base changes it again");
        var check = (await f.Repos.MergeCheckAsync(id, "Alpha", Ct))!;
        Assert.False(check.Clean);
        Assert.Equal("team/alpha does not merge cleanly into main (shared.txt)", check.Text);
    }

    [Fact]
    public async Task TheDoctor_WarnsPastTwentyCommitsOrThreeDays_AndSyncNowIsItsFix()
    {
        using var f = new TeamsFixture();
        await using var engine = new BusEngine(f.Db.OpenStore(), new ScriptedRunner(), new BusOptions { Repos = f.Repos });
        var id = await engine.CreateTeamAsync("Alpha", ct: Ct);
        var repo = (await f.Repos.EnsureTeamRepoAsync(id, "Alpha", Ct)).Path;
        AgentGit.Host(repo, "branch", "team/alpha");
        var hostTeams = new DoctorHostTeams(engine, f.Db.ConnectionString);
        var doctor = new DoctorService(new QuietSystem(), null, null, null, null, hostTeams);
        async Task<DoctorCheck> Teams() => (await doctor.RunAsync(Ct)).Single(c => c.Id == "teams");

        Assert.Equal("ok", (await Teams()).Status); // in step

        for (var i = 0; i < 3; i++) CommitAt(repo, $"f{i}.txt", DateTimeOffset.UtcNow.ToString("o"));
        Assert.Equal("ok", (await Teams()).Status); // 3 recent commits: not worth a warning

        for (var i = 3; i < 21; i++) CommitAt(repo, $"f{i}.txt", DateTimeOffset.UtcNow.ToString("o"));
        var warn = await Teams();
        Assert.Equal("warn", warn.Status);
        Assert.Contains("team/alpha is 21 commit(s) behind main", warn.Detail);
        Assert.Equal("sync-team:Alpha", warn.Fix!.Id);
        Assert.False(warn.Fix.Confirm);

        var fixedIt = await doctor.RunFixAsync(warn.Fix.Id, confirmed: false, Ct);
        Assert.True(fixedIt.Done);
        Assert.Equal("ok", (await Teams()).Status);
        AgentGit.Host(repo, "merge-base", "--is-ancestor", "main", "team/alpha");

        // Age alone warns too: one commit, four days old.
        CommitAt(repo, "old.txt", DateTimeOffset.UtcNow.AddDays(-4).ToString("o"));
        var old = await Teams();
        Assert.Equal("warn", old.Status);
        Assert.Contains("1 commit(s) behind main", old.Detail);
    }

    [Fact]
    public async Task TheDoctorFix_ReportsAConflictAndLeavesTheBranchAlone()
    {
        using var f = new TeamsFixture();
        await using var engine = new BusEngine(f.Db.OpenStore(), new ScriptedRunner(), new BusOptions { Repos = f.Repos });
        var id = await engine.CreateTeamAsync("Alpha", ct: Ct);
        var repo = (await f.Repos.EnsureTeamRepoAsync(id, "Alpha", Ct)).Path;
        TeamSyncTests.Commit(repo, "shared.txt", "one\n", "base has shared.txt");
        var own = TeamSyncTests.BranchWithWork(repo, "team/alpha", "shared.txt", "team\n");
        TeamSyncTests.Commit(repo, "shared.txt", "main\n", "base changes it");
        var doctor = new DoctorService(new QuietSystem(), null, null, null, null, new DoctorHostTeams(engine, f.Db.ConnectionString));

        var r = await doctor.RunFixAsync("sync-team:Alpha", confirmed: false, Ct);

        Assert.False(r.Done);
        Assert.Contains("conflicts in: shared.txt", r.Message);
        Assert.Equal(own, AgentGit.Host(repo, "rev-parse", "team/alpha"));
    }

    [Fact]
    public async Task ACompletion_CarriesTheMergeConflict_WhenTheTeamBranchDoesNotMergeIntoTheBase_AndIsPlainWhenItDoes()
    {
        using var f = new TeamsFixture();
        var done = new List<Message>();
        var runner = new ScriptedRunner().On("Alpha/Manager", async (run, ct) =>
        {
            var m = await run.WorkflowCompleteAsync("all delivered", ct);
            lock (done) done.Add(m);
            return RunResult.Ok;
        });
        await using var engine = new BusEngine(f.Db.OpenStore(), runner, new BusOptions { Repos = f.Repos });
        var id = await engine.CreateTeamAsync("Alpha", ct: Ct);
        var repo = (await f.Repos.EnsureTeamRepoAsync(id, "Alpha", Ct)).Path;
        TeamSyncTests.Commit(repo, "shared.txt", "one\n", "base has shared.txt");
        TeamSyncTests.BranchWithWork(repo, "team/alpha", "shared.txt", "team\n");
        TeamSyncTests.Commit(repo, "shared.txt", "main\n", "base changes it");
        f.Repos.SetSyncsToBase(id, false); // the check is a warning: it runs whatever the setting says
        await engine.StartAsync(Ct);

        await engine.PersonTellAsync("Alpha", "Manager", "finish", ct: Ct);
        await Wait.UntilAsync(() => Task.FromResult(done.Count == 1), "first completion");

        var delivered = Payload.Field(done[0], "delivered")!;
        Assert.StartsWith("all delivered", delivered);
        Assert.Contains("team/alpha does not merge cleanly into main (shared.txt)", delivered);
        Assert.Contains("\"mergeConflict\"", done[0].Payload);

        // Resolved (the team branch now contains main): the next completion carries nothing extra.
        AgentGit.Host(repo, "update-ref", "-d", "refs/heads/team/alpha");
        AgentGit.Host(repo, "branch", "team/alpha");
        await engine.PersonTellAsync("Alpha", "Manager", "finish again", ct: Ct);
        await Wait.UntilAsync(() => Task.FromResult(done.Count == 2), "second completion");
        Assert.Equal("all delivered", Payload.Field(done[1], "delivered"));
        Assert.Contains("\"mergeConflict\":null", done[1].Payload);
    }

    private sealed class QuietSystem : IDoctorSystem
    {
        public string QuickCheck() => "ok";
        public IReadOnlyList<string> AppliedMigrations() => [];
        public IReadOnlyList<string> ShippedMigrations() => [];
        public long FreeBytesOnDataDrive() => 100L << 30;
        public IReadOnlyDictionary<string, long> TeamFolderSizes() => new Dictionary<string, long>();
        public long LogBytes() => 0;
        public SandboxState Sandbox() => new(null, "done", null, null);
        public LinkState Link() => new("off", null);
    }
}
