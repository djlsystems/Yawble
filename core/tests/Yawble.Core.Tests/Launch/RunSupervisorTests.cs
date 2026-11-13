using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// The wall-clock timeout, the idle watchdog, Stop and shutdown: each ends the whole
/// tree within 5 s. The tree tests run a real process tree in a PID namespace (the stand-in for the sandbox's bwrap, see
/// <see cref="PidNamespaceBackend"/>), including a grandchild that left the process group with setsid, which a
/// process-group kill misses. They skip where unprivileged user namespaces are refused.
/// </summary>
public class RunSupervisorTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    /// <summary>A tree: the agent, a background child, and a setsid grandchild, all sleeping on the marker.</summary>
    private static string Tree(string marker) =>
        $"sleep {marker} & setsid sh -c 'sleep {marker}; true' & sh -c 'sleep {marker}; true' & echo started; wait";

    private static ISandboxProcess SpawnTree(string marker)
    {
        if (!PidNamespaceBackend.Available) Assert.Skip("unprivileged user + PID namespaces are not available on this host");
        return new PidNamespaceBackend(() => Tree(marker)).Spawn("{}");
    }

    private static async Task WaitForTree(string marker)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Procs.WithMarker(marker).Count < 3)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("the test tree did not start");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static async Task AssertTreeGone(string marker)
    {
        // The supervisor already waited for the sandbox; the kernel tears the namespace down right behind it.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Procs.WithMarker(marker).Count > 0)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"left behind: {string.Join(", ", Procs.WithMarker(marker))}");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task WallClockTimeoutKillsTheWholeTreeWithinFiveSeconds()
    {
        var marker = Procs.NewMarker();
        using var p = SpawnTree(marker);
        var output = new RunOutputBuffer();
        var s = new RunSupervisor(p, wallTimeoutSec: 2, idleTimeoutSec: 0, new RunActivity(TimeProvider.System), output);
        var run = s.RunAsync(TestContext.Current.CancellationToken);
        await WaitForTree(marker);
        var exit = await run;
        Assert.Equal(RunEnd.Timeout, exit.End);
        Assert.False(exit.TreeSurvived);
        Assert.True(exit.KillLatency < Bound, $"kill took {exit.KillLatency}");
        Assert.InRange(exit.Duration.TotalSeconds, 1.9, 2 + Bound.TotalSeconds);
        await AssertTreeGone(marker);
        Assert.Contains("started", System.Text.Encoding.UTF8.GetString(output.Snapshot()));
    }

    [Fact]
    public async Task IdleWatchdogKillsTheWholeTreeWhenNothingCallsProgress()
    {
        var marker = Procs.NewMarker();
        using var p = SpawnTree(marker);
        var s = new RunSupervisor(p, wallTimeoutSec: 60, idleTimeoutSec: 1, new RunActivity(TimeProvider.System), null);
        var exit = await s.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RunEnd.Idle, exit.End);
        Assert.False(exit.TreeSurvived);
        Assert.True(exit.KillLatency < Bound);
        await AssertTreeGone(marker);
    }

    [Fact]
    public async Task ProgressKeepsTheWatchdogAwayAndStopKillsTheWholeTree()
    {
        var marker = Procs.NewMarker();
        using var p = SpawnTree(marker);
        var activity = new RunActivity(TimeProvider.System);
        var s = new RunSupervisor(p, wallTimeoutSec: 60, idleTimeoutSec: 1, activity, null);
        var run = s.RunAsync(TestContext.Current.CancellationToken);
        await WaitForTree(marker);
        // 2.5 s with a progress call every 200 ms: longer than the 1 s idle limit, and still running.
        for (var i = 0; i < 12; i++)
        {
            activity.Touch();
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
        Assert.False(run.IsCompleted);
        s.RequestStop();
        var exit = await run;
        Assert.Equal(RunEnd.Stopped, exit.End);
        Assert.True(exit.KillLatency < Bound);
        await AssertTreeGone(marker);
    }

    [Fact]
    public async Task CoreShutdownKillsTheTreeAndReportsInterrupted()
    {
        var marker = Procs.NewMarker();
        using var p = SpawnTree(marker);
        using var shutdown = new CancellationTokenSource();
        var s = new RunSupervisor(p, wallTimeoutSec: 60, idleTimeoutSec: 0, new RunActivity(TimeProvider.System), null);
        var run = s.RunAsync(shutdown.Token);
        await WaitForTree(marker);
        await shutdown.CancelAsync();
        var exit = await run;
        Assert.Equal(RunEnd.Interrupted, exit.End);
        await AssertTreeGone(marker);
    }

    [Fact]
    public async Task ExitCodesAreReported()
    {
        var ok = new FakeProcess(RunKind.Headless);
        ok.Exit(0);
        Assert.Equal(RunEnd.Completed, (await new RunSupervisor(ok, 60, 60, new RunActivity(TimeProvider.System), null).RunAsync(TestContext.Current.CancellationToken)).End);
        var bad = new FakeProcess(RunKind.Headless);
        bad.Exit(3);
        var exit = await new RunSupervisor(bad, 60, 60, new RunActivity(TimeProvider.System), null).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RunEnd.Failed, exit.End);
        Assert.Equal(3, exit.ExitCode);
    }

    [Fact]
    public async Task SandboxReportedTimeoutIsATimeout()
    {
        var p = new FakeProcess(RunKind.Headless);
        p.KillForTimeout(); // as the sandbox's own process.timeout finalises it
        var exit = await new RunSupervisor(p, 60, 60, new RunActivity(TimeProvider.System), null).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RunEnd.Timeout, exit.End);
        Assert.True(exit.SandboxTimedOut);
        Assert.Null(exit.ExitCode);
    }

    [Fact]
    public async Task TimeoutUsesSandboxKillForTimeoutAndStopUsesKill()
    {
        var p = new FakeProcess(RunKind.Headless);
        var exit = await new RunSupervisor(p, 1, 0, new RunActivity(TimeProvider.System), null).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RunEnd.Timeout, exit.End);
        Assert.Equal(1, p.TimeoutKillCalls);
        Assert.Equal(0, p.KillCalls);

        var q = new FakeProcess(RunKind.Headless);
        var s = new RunSupervisor(q, 60, 0, new RunActivity(TimeProvider.System), null);
        s.RequestStop();
        Assert.Equal(RunEnd.Stopped, (await s.RunAsync(TestContext.Current.CancellationToken)).End);
        Assert.Equal(1, q.KillCalls);
    }

    [Fact]
    public async Task ATreeThatSurvivesTheBoundIsRecordedAndItsHandleFreed()
    {
        var p = new FakeProcess(RunKind.Headless) { IgnoreKill = true };
        var s = new RunSupervisor(p, 60, 0, new RunActivity(TimeProvider.System), null,
            options: new SupervisorOptions { KillBound = TimeSpan.FromMilliseconds(300) });
        s.RequestStop();
        var exit = await s.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RunEnd.Stopped, exit.End);
        Assert.True(exit.TreeSurvived);
        Assert.InRange(exit.KillLatency!.Value.TotalMilliseconds, 300, 3000);
    }
}
