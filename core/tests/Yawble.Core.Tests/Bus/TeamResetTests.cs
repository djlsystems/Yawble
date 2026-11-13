using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>Team reset: the members stay; their waiting work, home, working copy and the team's documents are cleared.</summary>
public class TeamResetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AResetClearsWhatWasPicked_ForThePickedMembersOnly()
    {
        using var db = new TempDb();
        var repos = new TeamRepos(db.Dir, db.ConnectionString);
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { Repos = repos, IdleOffer = false });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ben", ct: Ct);
        foreach (var who in new[] { "Ada", "Ben" })
        {
            var root = repos.MemberRoot("Alpha", who);
            Directory.CreateDirectory(Path.Combine(root, "home", ".claude"));
            File.WriteAllText(Path.Combine(root, "home", ".claude", "history.jsonl"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
            File.WriteAllText(Path.Combine(root, "repo", ".git", "obj"), "x");
            File.SetAttributes(Path.Combine(root, "repo", ".git", "obj"), FileAttributes.ReadOnly);
        }
        var docs = Path.Combine(repos.TeamRoot("Alpha"), TeamDocuments.Folder);
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "plan.md"), "# plan");
        await engine.PersonTellAsync("Alpha", "Ada", "later", ct: Ct);

        // Ada has an instruction waiting: clearing her folders alone is refused, and nothing changes.
        var refused = await Assert.ThrowsAsync<BusRefusal>(() => engine.ResetTeamAsync("Alpha", ["Ada"], new TeamResetOptions(false, true, true, false), Ct));
        Assert.Contains("Nothing was changed", refused.Message);
        Assert.True(File.Exists(Path.Combine(repos.MemberRoot("Alpha", "Ada"), "home", ".claude", "history.jsonl")));

        var result = await engine.ResetTeamAsync("Alpha", ["Ada"], new TeamResetOptions(true, true, true, true), Ct);
        Assert.Empty(result.Left);
        Assert.Contains(result.Done, d => d.StartsWith("dropped 1 waiting instruction"));
        var ada = repos.MemberRoot("Alpha", "Ada");
        Assert.True(Directory.Exists(Path.Combine(ada, "home")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(ada, "home")));
        Assert.False(Directory.Exists(Path.Combine(ada, "repo")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(docs));
        // Ben was not picked: untouched.
        Assert.True(File.Exists(Path.Combine(repos.MemberRoot("Alpha", "Ben"), "home", ".claude", "history.jsonl")));
        Assert.True(Directory.Exists(Path.Combine(repos.MemberRoot("Alpha", "Ben"), "repo")));
        Assert.True(await engine.IsQuiescentAsync(Ct));
    }

    [Fact]
    public async Task AResetIsRefusedWhileAPickedMemberRuns()
    {
        var release = new TaskCompletionSource<RunResult>();
        var runner = new ScriptedRunner().On("Alpha/Ada", (_, _) => release.Task);
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Ada", "work", ct: Ct);
        await Wait.UntilAsync(() => runner.Admitted.Count == 1, "Ada running");
        var e = await Assert.ThrowsAsync<BusRefusal>(() => engine.ResetTeamAsync("Alpha", [], new TeamResetOptions(true, true, false, false), Ct));
        Assert.Contains("Ada is working", e.Message);
        release.SetResult(RunResult.Ok);
    }
}
