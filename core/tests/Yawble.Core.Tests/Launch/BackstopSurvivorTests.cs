using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// Run 109's shape, but the sleep in group 25720 outlives SIGKILL in the backstop's second reap.
/// The teardown line names it as found through the group and still running; the record counts it as left behind.
/// </summary>
public class BackstopSurvivorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RunTreeOptions Fast = new()
    {
        ObserveEvery = TimeSpan.Zero,
        Grace = TimeSpan.FromMilliseconds(200),
        KillWait = TimeSpan.FromMilliseconds(200),
        Poll = TimeSpan.FromMilliseconds(10),
    };

    private const string RunFolder = "/private/var/folders/xy/T/yr-283eebe3";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGroupMemberStillAliveAfterTheSecondReapsSigkillIsLoggedAsGroupStillRunning(bool stop)
    {
        var table = new FakeProcessTable();
        var p = new FakeProcess(RunKind.Headless);
        var root = table.Add(p.Id, table.Self);
        var shell = table.Add(25720, root, group: 25720);
        var build = table.Add(25723, shell, group: 25720, ignoresTerm: true);
        var wrapper = table.Add(25724, build, group: 25720, ignoresTerm: true);
        var exec = table.Add(25734, wrapper, group: 25720);
        var sleep = table.Add(25735, exec, group: 25720, ignoresTerm: true, unkillable: true);
        table.End(shell);

        var tree = new RunTree(table, root, Fast);
        var holders = new FakeRunFolderHolders(table, RunFolder, exec);
        var log = new List<string>();
        var s = new RunSupervisor(p, 60, 0, new RunActivity(TimeProvider.System), null, tree: tree,
            holders: holders, runFolder: RunFolder, log: log.Add);
        var running = s.RunAsync(Ct);
        await Wait.UntilAsync(() => tree.Tracked.ContainsKey(root), "the root to be recorded");

        if (stop) s.RequestStop();
        else
        {
            table.End(root);
            p.Exit(0);
        }
        var exit = await running;
        table.End(root);

        Assert.True(table.IsAlive(sleep));
        Assert.All(new[] { build, wrapper, exec }, pid => Assert.False(table.IsAlive(pid), $"{pid} outlived the run"));
        Assert.True(exit.TreeSurvived);
        Assert.Equal(1, exit.LeftBehind);
        var line = Assert.Single(log);
        Assert.Equal($"run teardown: tree survived: {build} (group, reaped), {wrapper} (group, reaped), "
            + $"{exec} (run-folder, reaped), {sleep} (group, still running after SIGKILL)", line);
        TestContext.Current.SendDiagnosticMessage($"stop={stop}: {line}; signalled {exit.TreeSignalled}");
    }
}
