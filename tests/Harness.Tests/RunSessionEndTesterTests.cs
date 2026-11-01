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
    /// Red at a6cfe07, where the terminal's sweep ran only after its output had been delivered: while the
    /// worker's connection is down, a background job in another group must not outlive the terminal's
    /// CLI, and the held output and the terminal's end still go out once the connection is back.
    /// </summary>
    [Fact]
    public async Task A_terminals_background_job_is_gone_when_it_ends_even_while_the_connection_is_down()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var ct = TestContext.Current.CancellationToken;
        var pidFile = Path.Combine(_workspace, "job.pid");
        var sink = new DownSink();
        var ended = new TaskCompletionSource<TerminalEnded>(TaskCreationOptions.RunContinuationsAsynchronously);

        var terminals = new WorkerTerminals(sink, new PortaPtyEngine(), runAs: null, publish: message =>
        {
            if (message is TerminalEnded end) ended.TrySetResult(end);
            return Task.CompletedTask;
        });
        await terminals.StartAsync(new StartTerminal("terminal:down", new TerminalLaunch(
            ["bash", "-c", $"{OtherGroupJob(pidFile)}; echo output; sleep 0.3"], _workspace,
            new Dictionary<string, string>(), [], null, null, null), 80, 24), ct);

        try
        {
            var job = await WaitForPid(pidFile);

            // The CLI has long exited after 3 s; its session should have been swept.
            await GoneWithin(job, TimeSpan.FromSeconds(3));
            Assert.False(IsAlive(job), $"pid {job} outlived its terminal while the connection was down");
            Assert.False(ended.Task.IsCompleted, "the terminal's end is said only after its held output");

            // Output delivery after the CLI's exit works as before: the held output, then the end.
            sink.Up = true;
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Contains("output", sink.Text);
        }
        finally
        {
            sink.Up = true;
        }
    }

    /// <summary>
    /// Red at a6cfe07: a worker that stops while its connection is down stopped each terminal's CLI only,
    /// and the sweep waited on output that was never delivered. Stopping every terminal sweeps each live
    /// terminal's session before it returns, whatever the connection's state.
    /// </summary>
    [Fact]
    public async Task A_worker_stop_with_the_connection_down_takes_a_terminals_other_group_job()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var ct = TestContext.Current.CancellationToken;
        var pidFile = Path.Combine(_workspace, "job.pid");
        var sink = new DownSink();

        var terminals = new WorkerTerminals(sink, new PortaPtyEngine(), runAs: null, publish: _ => Task.CompletedTask);
        await terminals.StartAsync(new StartTerminal("terminal:stop", new TerminalLaunch(
            ["bash", "-c", $"{OtherGroupJob(pidFile)}; echo output; sleep 120"], _workspace,
            new Dictionary<string, string>(), [], null, null, null), 80, 24), ct);

        try
        {
            var job = await WaitForPid(pidFile);
            Assert.True(IsAlive(job));

            await terminals.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);

            // No grace period: the stop has returned, and the worker may exit now.
            Assert.False(IsAlive(job), $"pid {job} outlived a worker stop while the connection was down");
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
        private readonly System.Text.StringBuilder _text = new();

        public volatile bool Up;

        public string Text
        {
            get { lock (_text) return _text.ToString(); }
        }

        public ValueTask<bool> StreamAsync(StreamChunk chunk, CancellationToken ct = default)
        {
            if (!Up) return ValueTask.FromResult(false);
            lock (_text) _text.Append(System.Text.Encoding.UTF8.GetString(chunk.Bytes ?? []));
            return ValueTask.FromResult(true);
        }
    }
}
