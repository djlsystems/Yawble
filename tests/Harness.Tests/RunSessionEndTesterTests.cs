using System.Diagnostics;
using System.Globalization;
using Harness.Contracts;
using Harness.Host;
using Harness.Pty;

namespace Harness.Tests;

/// <summary>
/// An independent tester's checks of a run's session sweep, beyond the developer's own: a run that is
/// cancelled or fails takes its other-group job with it, the sweep refuses ids it must never touch, a
/// bystander in a session of its own survives, and a process that calls setsid itself is NOT caught
/// (the documented limit, so AGENTS.md does not claim more than the code does).
/// </summary>
public sealed class RunSessionEndTesterTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("harness-session-tester-").FullName;
    private readonly List<int> _started = [];

    public void Dispose()
    {
        foreach (var pid in _started)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        MemberTempCleanup.Remove(_workspace);
        Directory.Delete(_workspace, recursive: true);
    }

    private static string OtherGroupJob(string pidFile) =>
        $"set -m; sleep 120 </dev/null >/dev/null 2>&1 & echo $! > '{pidFile}'";

    private Task<AgentResult> RunAsync(string script, CancellationToken ct)
    {
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch("bash", ["-c", script], LanguageModel: false)),
        ]);

        var runner = new ProcessAgentRunner(
            catalog, new RunHeartbeat(), lookup: new LaunchLookup(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100)));

        return runner.RunAsync(new AgentInvocation(
            new ContainerId("alpha", "worker"), "You are a probe.", "hello", _workspace,
            new Dictionary<string, string>(), Agent: "probe"), ct);
    }

    [Fact]
    public async Task A_cancelled_run_takes_its_other_group_job_with_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var pidFile = Path.Combine(_workspace, "child.pid");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = RunAsync($"{OtherGroupJob(pidFile)}; sleep 120", cancel.Token);
        var child = await WaitForPid(pidFile);
        Assert.True(IsAlive(child));

        cancel.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        await GoneWithin(child, TimeSpan.FromSeconds(5));
        Assert.False(IsAlive(child), $"pid {child}, in another group of a cancelled run's session, outlived it");
    }

    [Fact]
    public async Task A_failing_run_takes_its_other_group_job_with_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var pidFile = Path.Combine(_workspace, "child.pid");

        var result = await RunAsync($"{OtherGroupJob(pidFile)}; exit 3", TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        var child = Learn(pidFile);
        await GoneWithin(child, TimeSpan.FromSeconds(5));
        Assert.False(IsAlive(child), $"pid {child}, in another group of a failed run's session, outlived it");
    }

    [Fact]
    public void The_sweep_refuses_the_workers_own_session_and_ids_zero_one_and_negative()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var own = int.Parse(File.ReadAllText("/proc/self/stat")[(File.ReadAllText("/proc/self/stat").LastIndexOf(')') + 2)..].Split(' ')[3], CultureInfo.InvariantCulture);

        Assert.Empty(RunSession.End(own));
        Assert.Empty(RunSession.End(1));
        Assert.Empty(RunSession.End(0));
        Assert.Empty(RunSession.End(-1));
        Assert.Empty(RunSession.End(-own));
    }

    [Fact]
    public async Task A_bystander_in_a_session_of_its_own_survives_the_runs_end()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var bystanderFile = Path.Combine(_workspace, "bystander.pid");
        using (var launcher = Process.Start(new ProcessStartInfo("bash", ["-c", $"setsid sleep 120 </dev/null >/dev/null 2>&1 & echo $! > '{bystanderFile}'"]) { UseShellExecute = false })!)
        {
            await launcher.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        var bystander = await WaitForPid(bystanderFile);
        Assert.Equal(bystander, Session(bystander));

        var pidFile = Path.Combine(_workspace, "child.pid");
        var result = await RunAsync(OtherGroupJob(pidFile), TestContext.Current.CancellationToken);
        var child = Learn(pidFile);

        await GoneWithin(child, TimeSpan.FromSeconds(5));
        Assert.False(IsAlive(child));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.True(IsAlive(bystander), $"bystander {bystander} in its own session was stopped by run {result.ProcessId}'s end");
    }

    /// <summary>The documented limit: a process that calls setsid itself leaves the session and is not swept (no cgroup here).</summary>
    [Fact]
    public async Task A_process_that_calls_setsid_itself_outlives_a_run_without_a_cgroup()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var pidFile = Path.Combine(_workspace, "escaped.pid");

        // The escaped process writes its own pid once it is in a session of its own, and the run waits for
        // that before it ends, so the run's end cannot race the setsid call.
        await RunAsync(
            $"setsid sh -c 'echo $$ > \"{pidFile}\"; exec sleep 120' </dev/null >/dev/null 2>&1 & "
            + $"while [ ! -s '{pidFile}' ]; do sleep 0.05; done",
            TestContext.Current.CancellationToken);
        var escaped = Learn(pidFile);
        Assert.Equal(escaped, Session(escaped));

        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.True(IsAlive(escaped), "a process that called setsid itself was expected to outlive the run, as AGENTS.md says");
    }

    /// <summary>
    /// REPRODUCTION, red at a6cfe07: the terminal's sweep runs only after its output has been delivered,
    /// so while the worker's connection is down a background job in another group outlives the terminal's
    /// end. Explicit, so it does not redden the suite; run it with <c>--explicit on</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task A_terminals_background_job_is_gone_when_it_ends_even_while_the_connection_is_down()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var ct = TestContext.Current.CancellationToken;
        var pidFile = Path.Combine(_workspace, "job.pid");
        var sink = new DownSink();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var terminals = new WorkerTerminals(sink, new PortaPtyEngine(), runAs: null, publish: _ => Task.CompletedTask);
        await terminals.StartAsync(new StartTerminal("terminal:down", new TerminalLaunch(
            ["bash", "-c", $"{OtherGroupJob(pidFile)}; echo output; sleep 0.3"], _workspace,
            new Dictionary<string, string>(), [], null, null, null), 80, 24), ct);

        try
        {
            var job = await WaitForPid(pidFile);

            // The CLI has long exited after 3 s; its session should have been swept.
            await GoneWithin(job, TimeSpan.FromSeconds(3));
            Assert.False(IsAlive(job), $"pid {job} outlived its terminal while the connection was down");
        }
        finally
        {
            sink.Up = true;
        }
    }

    private async Task<int> WaitForPid(string pidFile)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return Learn(pidFile);
    }

    private int Learn(string pidFile)
    {
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);
        _started.Add(pid);
        return pid;
    }

    private static async Task GoneWithin(int pid, TimeSpan bound)
    {
        var deadline = DateTime.UtcNow + bound;
        while (IsAlive(pid) && DateTime.UtcNow < deadline) await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    private static string[]? Stat(int pid)
    {
        try
        {
            var text = File.ReadAllText($"/proc/{pid}/stat");
            return text[(text.LastIndexOf(')') + 2)..].Split(' ');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsAlive(int pid) => Stat(pid) is { } fields && fields[0] != "Z";

    private static int? Session(int pid) => Stat(pid) is { } f ? int.Parse(f[3], CultureInfo.InvariantCulture) : null;

    private sealed class DownSink : IRunStreamSink
    {
        public volatile bool Up;

        public ValueTask<bool> StreamAsync(StreamChunk chunk, CancellationToken ct = default) => ValueTask.FromResult(Up);
    }
}
