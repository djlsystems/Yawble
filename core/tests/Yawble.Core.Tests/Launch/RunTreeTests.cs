using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// B122: on macOS (no PID namespace) the core ends a run's whole tree itself: SIGTERM, a grace, SIGKILL, then the run's
/// temp folder, then the run is done. All against <see cref="FakeProcessTable"/>: no real process, any OS.
/// </summary>
public class RunTreeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RunTreeOptions Fast = new()
    {
        ObserveEvery = TimeSpan.Zero,
        Grace = TimeSpan.FromMilliseconds(200),
        KillWait = TimeSpan.FromMilliseconds(200),
        Poll = TimeSpan.FromMilliseconds(10),
    };

    [Fact]
    public void AChildThatIgnoresSigtermIsSigkilledAfterTheGrace()
    {
        var table = new FakeProcessTable();
        var root = table.Add(500, table.Self);
        var shell = table.Add(501, root, ignoresTerm: true);
        var tree = new RunTree(table, root, Fast);
        tree.Track();
        table.End(root);

        var result = tree.Reap();

        Assert.Empty(result.Survivors);
        Assert.False(table.IsAlive(shell));
        var term = table.Log.Single(e => e.Pid == shell && e.Signal == RunTree.SIGTERM && !e.Group);
        var kill = table.Log.Single(e => e.Pid == shell && e.Signal == RunTree.SIGKILL && !e.Group);
        Assert.True(kill.At - term.At >= Fast.Grace - TimeSpan.FromMilliseconds(15), $"SIGKILL {(kill.At - term.At).TotalMilliseconds} ms after SIGTERM");
    }

    [Fact]
    public void AChildThatEndsOnSigtermIsNotSigkilled()
    {
        var table = new FakeProcessTable();
        var root = table.Add(500, table.Self);
        var child = table.Add(501, root);
        var tree = new RunTree(table, root, Fast);
        tree.Track();
        table.End(root);

        var result = tree.Reap();

        Assert.Empty(result.Survivors);
        Assert.Equal(1, result.Signalled);
        Assert.DoesNotContain(table.Log, e => e.Signal == RunTree.SIGKILL);
        Assert.False(table.IsAlive(child));
    }

    [Fact]
    public void AGrandchildInItsOwnGroupIsKilledEvenAfterItsParentEnded()
    {
        var table = new FakeProcessTable();
        var root = table.Add(500, table.Self);
        var cli = table.Add(501, root);
        // The shell wrapper the CLI starts in a session of its own, and the build under it (in the shell's group).
        var shell = table.Add(502, cli, group: 502, ignoresTerm: true);
        var build = table.Add(503, shell, group: 502, ignoresTerm: true);
        var tree = new RunTree(table, root, Fast);
        tree.Track();
        // The CLI and the sandbox end: launchd (pid 1) adopts the shell.
        table.End(cli);
        table.End(root);
        Assert.Equal(1, table.Get(shell).ParentPid);

        var result = tree.Reap();

        Assert.Empty(result.Survivors);
        Assert.False(table.IsAlive(shell));
        Assert.False(table.IsAlive(build));
        Assert.Contains(table.Log, e => e.Group && e.Pid == 502 && e.Signal == RunTree.SIGTERM);
        Assert.Contains(table.Log, e => e.Group && e.Pid == 502 && e.Signal == RunTree.SIGKILL);
    }

    [Fact]
    public void AProcessInARecordedGroupIsKilledThoughItsParentWasNeverSeen()
    {
        var table = new FakeProcessTable();
        var root = table.Add(500, table.Self);
        var shell = table.Add(502, root, group: 502);
        var tree = new RunTree(table, root, Fast);
        tree.Track();
        // Started and orphaned between two reads of the list: only its group says it is the run's.
        var orphan = table.Add(510, 1, group: 502, ignoresTerm: true);

        var result = tree.Reap();

        Assert.Empty(result.Survivors);
        Assert.False(table.IsAlive(orphan));
        Assert.False(table.IsAlive(shell));
    }

    [Fact]
    public void AnUnkillableChildIsReportedAsASurvivor()
    {
        var table = new FakeProcessTable();
        var root = table.Add(500, table.Self);
        var stuck = table.Add(501, root, ignoresTerm: true, unkillable: true);
        var tree = new RunTree(table, root, Fast);
        tree.Track();
        table.End(root);

        var result = tree.Reap();

        Assert.Equal([stuck], result.Survivors);
    }

    [Fact]
    public void AReusedPidTheCoresGroupAndUnrelatedProcessesAreNeverSignalled()
    {
        var table = new FakeProcessTable();
        var root = table.Add(500, table.Self);
        var child = table.Add(501, root);
        // A child left in the core's own process group: it is signalled alone, never the core's group.
        var inCoreGroup = table.Add(502, root, group: table.SelfGroup);
        var stranger = table.Add(600, 1);
        var tree = new RunTree(table, root, Fast);
        tree.Track();
        table.End(child);
        table.End(root);
        // The pid comes back as another program.
        table.Add(501, 1);

        tree.Reap();

        Assert.True(table.IsAlive(501));
        Assert.True(table.IsAlive(stranger));
        Assert.False(table.IsAlive(inCoreGroup));
        Assert.DoesNotContain(table.Log, e => e.Pid == 501 || e.Pid == stranger || e.Group && e.Pid == table.SelfGroup);
    }

    [Fact]
    public void ARootThatIsNotTheCoresChildIsNotFollowed()
    {
        var table = new FakeProcessTable();
        var notOurs = table.Add(500, 1);
        table.Add(501, notOurs);
        var tree = new RunTree(table, notOurs, Fast);
        tree.Track();

        var result = tree.Reap();

        Assert.Equal(0, result.Signalled);
        Assert.Empty(table.Log);
    }

    [Fact]
    public async Task TheRunsTempFolderGoesOnlyAfterTheTreeIsGoneAndTheRunEndsAfterThat()
    {
        var table = new FakeProcessTable();
        var p = new FakeProcess(RunKind.Headless);
        var root = table.Add(p.Id, table.Self);
        var shell = table.Add(p.Id + 1, root, group: p.Id + 1, ignoresTerm: true);
        var build = table.Add(p.Id + 2, shell, group: p.Id + 1, ignoresTerm: true);
        var tree = new RunTree(table, root, Fast);
        int? aliveAtRemoval = null;
        var removedAfterLog = -1;
        var s = new RunSupervisor(p, 60, 0, new RunActivity(TimeProvider.System), null, tree: tree, treeGone: () =>
        {
            aliveAtRemoval = table.AliveCount;
            removedAfterLog = table.Log.Count;
        });
        var run = s.RunAsync(Ct);
        await Wait.UntilAsync(() => tree.Tracked.Count == 3, "the tree to be recorded");
        Assert.Null(aliveAtRemoval);
        // The CLI exits 0 on its own; its shell and the hung build live on (as on the Mac mini).
        table.End(root);
        p.Exit(0);

        var exit = await run;

        Assert.Equal(RunEnd.Completed, exit.End);
        Assert.Equal(0, aliveAtRemoval);
        Assert.Equal(table.Log.Count, removedAfterLog);
        Assert.Contains(table.Log, e => e.Pid == build && e.Signal == RunTree.SIGKILL);
        Assert.False(exit.TreeSurvived);
        Assert.Equal(0, exit.LeftBehind);
        Assert.Equal(2, exit.TreeSignalled);
    }

    [Fact]
    public async Task ASupervisedRunWhoseChildCannotBeKilledRecordsTreeSurvived()
    {
        var table = new FakeProcessTable();
        var p = new FakeProcess(RunKind.Headless);
        var root = table.Add(p.Id, table.Self);
        table.Add(p.Id + 1, root, ignoresTerm: true, unkillable: true);
        var tree = new RunTree(table, root, Fast);
        var removed = false;
        var s = new RunSupervisor(p, 60, 0, new RunActivity(TimeProvider.System), null, tree: tree, treeGone: () => removed = true);
        var run = s.RunAsync(Ct);
        await Wait.UntilAsync(() => tree.Tracked.Count == 2, "the tree to be recorded");
        table.End(root);
        p.Exit(0);

        var exit = await run;

        Assert.True(exit.TreeSurvived);
        Assert.Equal(1, exit.LeftBehind);
        // The folder is removed anyway, after the kill: what is left is reported, not waited on forever.
        Assert.True(removed);
    }

    [Fact]
    public async Task TheRunRecordSaysTreeSurvivedWhenAProcessOfTheRunOutlivedSigkill()
    {
        var table = new FakeProcessTable();
        RunTree? tree = null;
        await using var kit = await LaunchKit.StartAsync(runTrees: pid =>
        {
            var root = table.Add(pid, table.Self);
            table.Add(pid + 100000, root, ignoresTerm: true, unkillable: true);
            return tree = new RunTree(table, root, Fast);
        });
        kit.Backend.Script = async (req, p) =>
        {
            if (!LaunchKit.Env(req).GetValueOrDefault("CLAUDE_CONFIG_DIR", "").Contains("dev1")) { p.Exit(0); return; }
            await Wait.UntilAsync(() => tree is { Tracked.Count: 2 }, "dev1's tree to be recorded");
            table.End(p.Id);
            p.Exit(0);
        };
        await kit.Bus.PersonTellAsync("alpha", "dev1", "build", ct: Ct);
        var outcome = await kit.OutcomeAsync("dev1");

        var record = kit.Records.All().First(r => r.Member == "dev1");
        Assert.True(record.TreeSurvived);
        Assert.Contains("still running after SIGTERM and SIGKILL", record.Reason);
        Assert.Contains("still running after SIGTERM and SIGKILL", Payload.Field(outcome, "reason"));
    }

    [Fact]
    public void MacProcessTableListsThisProcess()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Skip("libproc is macOS's");
        var table = MacProcessTable.Instance;
        var self = table.List().Single(p => p.Pid == table.Self);
        Assert.Equal(table.SelfGroup, self.GroupId);
        Assert.True(self.StartedAt > 0);
    }
}

/// <summary>
/// A process table with no OS behind it. SIGTERM ends a process unless it ignores it; SIGKILL ends it unless it is
/// unkillable; an ended parent's children go to pid 1, as launchd adopts them.
/// </summary>
internal sealed class FakeProcessTable : IProcessTable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Proc> _procs = [];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _starts;

    public sealed record Entry(int Pid, int Signal, bool Group, TimeSpan At);

    private sealed class Proc
    {
        public required ProcessEntry Info { get; set; }
        public bool IgnoresTerm { get; init; }
        public bool Unkillable { get; init; }
    }

    public int Self => 1000;
    public int SelfGroup => 1000;
    public List<Entry> Log { get; } = [];

    public int Add(int pid, int parent, int? group = null, bool ignoresTerm = false, bool unkillable = false)
    {
        lock (_lock)
        {
            var g = group ?? (parent is 1 or 1000 ? pid : _procs.TryGetValue(parent, out var pp) ? pp.Info.GroupId : pid);
            _procs[pid] = new Proc { Info = new ProcessEntry(pid, parent, g, ++_starts), IgnoresTerm = ignoresTerm, Unkillable = unkillable };
            return pid;
        }
    }

    public ProcessEntry Get(int pid)
    {
        lock (_lock) return _procs[pid].Info;
    }

    public bool IsAlive(int pid)
    {
        lock (_lock) return _procs.ContainsKey(pid);
    }

    public int AliveCount
    {
        get { lock (_lock) return _procs.Count; }
    }

    public void End(int pid)
    {
        lock (_lock)
        {
            if (!_procs.Remove(pid)) return;
            foreach (var child in _procs.Values.Where(c => c.Info.ParentPid == pid)) child.Info = child.Info with { ParentPid = 1 };
        }
    }

    public IReadOnlyList<ProcessEntry> List()
    {
        lock (_lock) return [.. _procs.Values.Select(p => p.Info)];
    }

    public void Signal(int pid, int signal)
    {
        lock (_lock)
        {
            Log.Add(new Entry(pid, signal, false, _clock.Elapsed));
            Deliver(pid, signal);
        }
    }

    public void SignalGroup(int pgid, int signal)
    {
        lock (_lock)
        {
            Log.Add(new Entry(pgid, signal, true, _clock.Elapsed));
            foreach (var pid in _procs.Values.Where(p => p.Info.GroupId == pgid).Select(p => p.Info.Pid).ToList()) Deliver(pid, signal);
        }
    }

    private void Deliver(int pid, int signal)
    {
        if (!_procs.TryGetValue(pid, out var p)) return;
        if (signal == RunTree.SIGTERM && p.IgnoresTerm) return;
        if (signal == RunTree.SIGKILL && p.Unkillable) return;
        End(pid);
    }
}
