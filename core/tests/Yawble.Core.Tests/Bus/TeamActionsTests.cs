using Yawble.Core.Bus;
using Yawble.Core.Credentials;
using Yawble.Core.Sandboxing;
using TargetOs = Yawble.Core.Policy.TargetOs;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Policy;
using Yawble.Core.Triggers;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>Pause and resume, archive and unarchive, delete, and the team's environment variables (Yawble's team actions).</summary>
public class TeamActionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TempDb Db, BusEngine Engine, MemberRef Ada)> Team(ScriptedRunner runner)
    {
        var db = new TempDb();
        var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        var ada = await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        return (db, engine, ada);
    }

    [Fact]
    public async Task APausedTeamsWorkWaitsInPlace_AndRunsWhenItIsResumed()
    {
        var runner = new ScriptedRunner();
        var (db, engine, _) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            await engine.PauseTeamAsync("Alpha", Ct);
            await engine.PersonTellAsync("Alpha", "Ada", "when you can", ct: Ct);
            await Task.Delay(500, Ct);
            Assert.Empty(runner.Admitted);
            var summary = Assert.Single(await engine.TeamsAsync(Ct));
            Assert.True(summary.Paused);

            await engine.ResumeTeamAsync("Alpha", Ct);
            await Wait.UntilAsync(() => runner.Admitted.Count >= 1, "Ada runs after the resume");
            Assert.Equal("Alpha/Ada", runner.AdmittedNames()[0]);
        }
    }

    [Fact]
    public async Task APausedTeamsTriggersSkip_SayingWhy()
    {
        var (db, engine, ada) = await Team(new ScriptedRunner());
        using (db)
        await using (engine)
        {
            var t = await engine.SaveTriggerAsync(new TriggerDraft(null, ada.Id, "nightly", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "Check.", false, true), Ct);
            await engine.PauseTeamAsync("Alpha", Ct);
            await engine.SweepTriggersAsync(t.NextDueAt!.Value.AddSeconds(1), Ct);
            using var store = db.OpenStore();
            var row = store.Trigger(t.Id)!;
            Assert.Equal(("skipped", "the team is paused"), (row.LastOutcome, row.LastReason));
            Assert.Null(row.LastSeq);
        }
    }

    [Fact]
    public async Task ArchiveNeedsAQuietTeam_RefusesNewWork_AndUnarchiveBringsItBackPaused()
    {
        var release = new TaskCompletionSource<RunResult>();
        var runner = new ScriptedRunner().On("Alpha/Ada", (_, _) => release.Task);
        var (db, engine, _) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            await engine.PersonTellAsync("Alpha", "Ada", "long job", ct: Ct);
            await Wait.UntilAsync(() => runner.Admitted.Count == 1, "Ada running");
            var quiet = await engine.TeamQuietAsync("Alpha", Ct);
            Assert.False(quiet.Quiet);
            Assert.Contains("Ada is running", quiet.Working);
            var refused = await Assert.ThrowsAsync<BusRefusal>(() => engine.ArchiveTeamAsync("Alpha", Ct));
            Assert.Equal("not_quiet", refused.Code);
            Assert.Contains("Nothing was changed", refused.Message);

            release.SetResult(RunResult.Ok);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            await engine.ArchiveTeamAsync("Alpha", Ct);
            var archived = Assert.Single(await engine.TeamsAsync(Ct));
            Assert.True(archived.Archived && archived.Paused);
            var told = await Assert.ThrowsAsync<BusRefusal>(() => engine.PersonTellAsync("Alpha", "Ada", "more", ct: Ct));
            Assert.Equal("archived", told.Code);
            Assert.Equal("archived", (await Assert.ThrowsAsync<BusRefusal>(() => engine.ResumeTeamAsync("Alpha", Ct))).Code);

            await engine.UnarchiveTeamAsync("Alpha", Ct);
            var back = Assert.Single(await engine.TeamsAsync(Ct));
            Assert.False(back.Archived);
            Assert.True(back.Paused);
        }
    }

    [Fact]
    public async Task DeletingATeam_RemovesItFromTheBus_AndItsFolder_ButKeepsTheMessageLog()
    {
        var db = new TempDb();
        using (db)
        {
            var repos = new TeamRepos(db.Dir, db.ConnectionString);
            await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { Repos = repos, IdleOffer = false });
            await engine.CreateTeamAsync("Alpha", ct: Ct);
            await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            var folder = repos.TeamRoot("Alpha");
            Directory.CreateDirectory(Path.Combine(folder, "members", "Ada"));
            var readOnly = Path.Combine(folder, "members", "Ada", "object");
            File.WriteAllText(readOnly, "x");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly); // like git's object files
            var told = await engine.PersonTellAsync("Alpha", "Ada", "never mind", ct: Ct);

            // Ada has an instruction waiting: not quiet.
            Assert.Equal("not_quiet", (await Assert.ThrowsAsync<BusRefusal>(() => engine.DeleteTeamAsync("Alpha", Ct))).Code);
            // Let Ada's run take it, and the team is quiet.
            await engine.StartAsync(Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            var left = await engine.DeleteTeamAsync("Alpha", Ct);
            Assert.Empty(left);
            Assert.Empty(await engine.TeamsAsync(Ct));
            Assert.False(Directory.Exists(folder));
            using var after = db.OpenStore();
            Assert.NotNull(after.Find(told.Seq)); // the history stays
        }
    }

    [Theory]
    [InlineData("1BAD", "not a variable name")]
    [InlineData("PATH", "set by the app")]
    [InlineData("HOME", "set by the app")]
    [InlineData("YAWBLE_TEAM_REPO", "set by the app")]
    [InlineData("ANTHROPIC_API_KEY", "set by the app")]
    public void TeamVariablesThatCannotBeGivenAreNamed(string name, string why) =>
        Assert.Contains(why, Assert.Single(TeamEnv.Problems([new(name, "x")])));

    [Fact]
    public async Task TeamVariablesAreSavedWhole_AndABadSetSavesNothing()
    {
        var (db, engine, _) = await Team(new ScriptedRunner());
        using (db)
        await using (engine)
        {
            await engine.SetTeamEnvAsync("Alpha", [new("TEST_ADMIN_EMAIL", "admin@alpha.test"), new("API_BASE", "http://localhost:5000")], Ct);
            var bad = await Assert.ThrowsAsync<BusRefusal>(() => engine.SetTeamEnvAsync("Alpha", [new("OK_ONE", "1"), new("PATH", "/evil"), new("OK_ONE", "2")], Ct));
            Assert.Contains("'PATH'", bad.Message);
            Assert.Contains("given twice", bad.Message);
            var env = await engine.TeamEnvAsync("Alpha", Ct);
            Assert.Equal(2, env.Count);
            Assert.Equal("admin@alpha.test", env["TEST_ADMIN_EMAIL"]);
        }
    }

    [Fact]
    public void ARunGetsTheTeamsVariables_UnderTheAppsOwn()
    {
        var run = PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeSubscription) with
        {
            TeamEnv = new Dictionary<string, string> { ["API_BASE"] = "http://localhost:5000", ["HOME"] = "/tmp/evil", ["PATH"] = "/evil" },
        };
        var compiled = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, run);
        var env = compiled.Request["process"]!["env"]!.AsArray().Select(e => e!.GetValue<string>()).ToArray();
        Assert.Contains("API_BASE=http://localhost:5000", env);
        Assert.DoesNotContain(env, e => e == "HOME=/tmp/evil" || e == "PATH=/evil");
        Assert.Empty(SandboxRequestInvariants.Check(compiled, run));
    }
}
