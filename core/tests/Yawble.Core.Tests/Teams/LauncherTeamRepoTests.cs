using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Launch;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The launcher and the team repository (every OS; the sandbox is the scripted backend): the launcher's runner wrapped in
/// <see cref="TeamRepoRunner"/>, as BusHost does. The sandbox's cwd is the member's prepared clone, and the CLI is
/// started with the team prompt (who it is, its role text, its branch), not the bare batch.
/// </summary>
public class LauncherTeamRepoTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheLauncherRunsInTheMembersCloneWithTheTeamPrompt()
    {
        TeamRepos? repos = null;
        await using var kit = await LaunchKit.StartAsync(wrapRunner: (inner, host, connection) =>
        {
            repos = new TeamRepos(host.AppDataRoot, connection);
            return new TeamRepoRunner(inner, repos);
        });
        string? commandLine = null;
        var cloneHadGit = false;
        var layout = kit.Layout("dev1", Yawble.Core.Policy.AgentCli.Claude);
        kit.Backend.Script = (req, p) =>
        {
            if (LaunchKit.IsFor(req.ToJsonString(), "dev1"))
            {
                commandLine = req["process"]!["commandLine"]!.GetValue<string>();
                cloneHadGit = Directory.Exists(Path.Combine(layout.ClonePath, ".git"));
            }
            p.Exit(0);
            return Task.CompletedTask;
        };
        await kit.Bus.PersonTellAsync("alpha", "dev1", "write hello.txt", ct: Ct);
        var outcome = await kit.OutcomeAsync("dev1");

        Assert.Equal(MessageTypes.Completed, outcome.Type);
        Assert.NotNull(commandLine);
        Assert.True(cloneHadGit, "the sandbox started before the member's clone was made");
        Assert.Contains("You are dev1, a member of team alpha", commandLine);
        Assert.Contains("on branch members/dev1", commandLine);
        Assert.Contains("write hello.txt", commandLine);
        using var store = kit.Db.OpenStore();
        var dev1 = store.AllMembers().Single(m => m.Team == "alpha" && m.Name == "dev1");
        var row = repos!.Row(dev1.Id)!;
        Assert.Equal("members/dev1", row.Branch);
        Assert.Equal(Path.GetFullPath(layout.ClonePath), Path.GetFullPath(row.Path));
        Assert.Equal("[]", row.LastCheck);
    }
}
