using System.Diagnostics;
using Harness.Contracts;
using Harness.Host;
using Harness.Pty;

namespace Harness.Tests;

/// <summary>
/// The launcher every member's child shares. <see cref="ProcessAgentRunnerLaunchTests"/> pins it
/// through the agent runner; these pin it directly, as the plugin runner uses it.
/// </summary>
public sealed class ChildProcessTests
{
    private static readonly ContainerId Who = new("alpha", "plugin");

    private static ProcessStartInfo Bash(string script)
    {
        var start = ChildProcess.StartInfo(PathSearch.Find("bash")!, Path.GetTempPath(), runAs: null)!;
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        return start;
    }

    [Fact]
    public async Task Stdin_is_written_and_closed_and_both_streams_are_captured()
    {
        var outcome = await ChildProcess.RunAsync(
            Bash("cat; echo oops >&2; exit 3"), "hello", TestContext.Current.CancellationToken);

        Assert.False(outcome.Killed);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal("hello", outcome.Stdout);
        Assert.Equal("oops\n", outcome.Stderr);
    }

    [Fact]
    public async Task Stdout_lines_are_handed_over_as_they_arrive()
    {
        var lines = new List<string>();

        var outcome = await ChildProcess.RunAsync(
            Bash("echo one; echo two"), null, TestContext.Current.CancellationToken,
            onStdoutLine: line => { lines.Add(line); return Task.CompletedTask; });

        Assert.Equal(["one", "two"], lines);
        Assert.Equal("one\ntwo\n", outcome.Stdout);
    }

    [Fact]
    public async Task A_child_that_has_already_exited_is_not_a_failure_to_write_stdin()
    {
        var outcome = await ChildProcess.RunAsync(
            Bash("exit 0"), new string('x', 1 << 20), TestContext.Current.CancellationToken);

        Assert.False(outcome.Killed);
        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public async Task The_idle_clock_kills_a_silent_child_and_says_it_was_the_clock()
    {
        using var clock = ChildProcess.Clock(new RunHeartbeat(), Who, 1, TestContext.Current.CancellationToken);
        var watch = Stopwatch.StartNew();

        var outcome = await ChildProcess.RunAsync(Bash("sleep 30"), null, clock.Stopping);

        Assert.True(outcome.Killed);
        Assert.True(clock.Expired);
        Assert.Equal(-1, outcome.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_heartbeat_pushes_the_idle_clock_out()
    {
        var heartbeat = new RunHeartbeat();
        using var clock = ChildProcess.Clock(heartbeat, Who, 1, TestContext.Current.CancellationToken);

        using var ticking = new Timer(_ => heartbeat.Touch(Who), null, 300, 300);
        var outcome = await ChildProcess.RunAsync(Bash("sleep 2; echo alive"), null, clock.Stopping);

        Assert.False(outcome.Killed);
        Assert.Equal("alive\n", outcome.Stdout);
    }

    [Fact]
    public async Task A_caller_stop_is_not_the_clock()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        using var clock = ChildProcess.Clock(new RunHeartbeat(), Who, null, stop.Token);

        var outcome = await ChildProcess.RunAsync(Bash("sleep 30"), null, clock.Stopping);

        Assert.True(outcome.Killed);
        Assert.False(clock.Expired);
    }

    [Fact]
    public async Task A_grandchild_holding_the_pipe_is_drained_for_a_bounded_grace()
    {
        var watch = Stopwatch.StartNew();

        var outcome = await ChildProcess.RunAsync(
            Bash("echo before; (sleep 30; echo late) & exit 0"), null, TestContext.Current.CancellationToken);

        Assert.True(outcome.HeldOpen);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("before\n", outcome.Stdout);
        Assert.True(watch.Elapsed < ChildProcess.DrainGrace + TimeSpan.FromSeconds(5));
    }
}
