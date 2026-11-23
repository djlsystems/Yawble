using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// B127 verify (card 575): run_records 109 on the Mac mini, replayed with fakes. The agent's Bash shell 25720 (its own
/// group) ran `dotnet build -m:4 -nodeReuse:false &amp;` and exited within a second, before the tree's first read of
/// the process list. Left behind, all in group 25720: 25723 dotnet build (ppid 1), 25724 the wrapper's build -m:1,
/// 25734 /bin/sh running an exec.cmd with its cwd under yr-*/MSBuildTemp, 25735 sleep 600.
/// </summary>
public class Run109ReplayTests
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
    public async Task Run109TheBuildBackgroundedByAShellThatEndedBeforeTheFirstReadIsReapedAndReported(bool stop)
    {
        var table = new FakeProcessTable();
        var p = new FakeProcess(RunKind.Headless);
        var root = table.Add(p.Id, table.Self);
        // All before the first Observe: the shell starts the build in its group and exits; launchd adopts the build.
        var shell = table.Add(25720, root, group: 25720);
        var build = table.Add(25723, shell, group: 25720, ignoresTerm: true);
        var wrapper = table.Add(25724, build, group: 25720, ignoresTerm: true);
        var exec = table.Add(25734, wrapper, group: 25720);
        var sleep = table.Add(25735, exec, group: 25720);
        table.End(shell);
        Assert.Equal(1, table.Get(build).ParentPid);
        int[] run = [build, wrapper, exec, sleep];

        var tree = new RunTree(table, root, Fast);
        // The exec.cmd's cwd is under yr-283eebe3/MSBuildTemp.
        var holders = new FakeRunFolderHolders(table, RunFolder, exec);
        int[]? aliveAtTreeGone = null;
        var log = new List<string>();
        var s = new RunSupervisor(p, 60, 0, new RunActivity(TimeProvider.System), null, tree: tree,
            treeGone: () => aliveAtTreeGone = [.. run.Where(table.IsAlive)], holders: holders, runFolder: RunFolder, log: log.Add);
        var running = s.RunAsync(Ct);
        await Wait.UntilAsync(() => tree.Tracked.ContainsKey(root), "the root to be recorded");
        // The first read came after the shell had ended: neither ppid nor a recorded group leads to the build.
        Assert.DoesNotContain(tree.Tracked.Keys, run.Contains);
        Assert.True(run.All(table.IsAlive));

        if (stop) s.RequestStop();
        else
        {
            table.End(root);
            p.Exit(0);
        }
        var exit = await running;
        table.End(root);

        Assert.Equal(stop ? RunEnd.Stopped : RunEnd.Completed, exit.End);
        Assert.Equal([RunFolder], holders.Asked);
        Assert.All(run, pid => Assert.False(table.IsAlive(pid), $"{pid} outlived the run"));
        Assert.NotNull(aliveAtTreeGone);
        Assert.Empty(aliveAtTreeGone);
        Assert.True(exit.TreeSurvived);
        Assert.Equal(0, exit.LeftBehind);
        Assert.Contains(table.Log, e => e.Group && e.Pid == 25720 && e.Signal == RunTree.SIGKILL);
        var line = Assert.Single(log);
        Assert.StartsWith("run teardown: tree survived: ", line);
        Assert.Contains($"{exec} (run-folder, reaped)", line);
        TestContext.Current.SendDiagnosticMessage($"stop={stop}: {line}; signalled {exit.TreeSignalled}");
    }
}
