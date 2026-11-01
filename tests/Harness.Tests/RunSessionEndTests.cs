using System.Diagnostics;
using System.Globalization;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;
using Harness.Pty;

namespace Harness.Tests;

/// <summary>
/// A RUN'S END TAKES ITS WHOLE SESSION WITH IT. A run starts in its own session (<c>setsid</c>); a
/// process it starts may move itself into another process group of that session (<c>setpgid</c>, a
/// <c>set -m</c> background job), which a kill of the run's own group never reaches. These start such
/// a process on the real <c>/proc</c> and check it is gone when the run ends, that it is counted
/// against the run while the run lives, that the same holds for the Concierge's terminal, and that a
/// process outside the run's session is never touched.
/// </summary>
public sealed class RunSessionEndTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("harness-session-").FullName;

    /// <summary>Every pid a test learned, stopped by that pid if it is still there so a failing run leaks nothing.</summary>
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

    /// <summary>A shell line that starts a background job in a process group of its own and writes its pid.</summary>
    private static string OtherGroupJob(string pidFile) =>
        $"set -m; sleep 120 </dev/null >/dev/null 2>&1 & echo $! > '{pidFile}'";

    private Task<AgentResult> RunAsync(string script)
    {
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch("bash", ["-c", script], LanguageModel: false)),
        ]);

        var runner = new ProcessAgentRunner(
            catalog, new RunHeartbeat(), lookup: new LaunchLookup(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100)));

        return runner.RunAsync(new AgentInvocation(
            new ContainerId("alpha", "worker"), "You are a probe.", "hello", _workspace,
            new Dictionary<string, string>(), Agent: "probe"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_child_that_moves_into_its_own_process_group_does_not_outlive_the_run()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var pidFile = Path.Combine(_workspace, "child.pid");

        var result = await RunAsync(OtherGroupJob(pidFile));

        Assert.Equal(0, result.ExitCode);
        var child = Learn(pidFile);

        await GoneWithin(child, TimeSpan.FromSeconds(5));
        Assert.False(IsAlive(child), $"pid {child}, in another process group of the run's session, outlived the run");
    }

    [Fact]
    public async Task A_grandchild_in_another_process_group_does_not_outlive_the_run()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var pidFile = Path.Combine(_workspace, "grandchild.pid");

        // The run's shell starts a shell, which starts the job: two levels down, in a group of its own.
        var inner = OtherGroupJob(pidFile).Replace("'", "'\\''", StringComparison.Ordinal);
        var result = await RunAsync($"bash -c '{inner}'");

        Assert.Equal(0, result.ExitCode);
        var grandchild = Learn(pidFile);

        await GoneWithin(grandchild, TimeSpan.FromSeconds(5));
        Assert.False(IsAlive(grandchild), $"pid {grandchild}, two levels down in another process group, outlived the run");
    }

    [Fact]
    public async Task A_process_outside_the_runs_session_survives_the_runs_end()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var pidFile = Path.Combine(_workspace, "child.pid");

        // A bystander in the test's own session, started before the run and still running after it.
        using var bystander = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        _started.Add(bystander.Id);
        Assert.NotNull(Session(bystander.Id));

        var result = await RunAsync(OtherGroupJob(pidFile));

        Assert.Equal(0, result.ExitCode);
        var child = Learn(pidFile);
        Assert.NotEqual(Session(bystander.Id), result.ProcessId);

        // The run's session is swept: its other-group job goes, the bystander stays.
        await GoneWithin(child, TimeSpan.FromSeconds(5));
        Assert.False(IsAlive(child), $"pid {child}, in the run's session, outlived the run");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.True(IsAlive(bystander.Id), $"the bystander {bystander.Id}, outside the run's session, was stopped");
    }

    [Fact]
    public async Task While_the_run_lives_its_measured_process_count_and_memory_include_the_other_group_process()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var ct = TestContext.Current.CancellationToken;
        var pidFile = Path.Combine(_workspace, "child.pid");

        var start = ChildProcess.StartInfo("/bin/bash", _workspace, runAs: null);
        Assert.NotNull(start);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"{OtherGroupJob(pidFile)}; exec sleep 120");

        var groups = new RunProcessGroups();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var leaderPid = 0;
        var run = ChildProcess.RunAsync(
            start, null, stop.Token, onStarted: process => leaderPid = process.Id,
            run: new ContainerId("alpha", "worker"), groups: groups);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0) && DateTime.UtcNow < deadline) await Task.Delay(50, ct);
            var child = Learn(pidFile);

            // Wait for the leader to be the sleep it execs into, so the figures settle.
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (Command(leaderPid) != "sleep" && DateTime.UtcNow < deadline) await Task.Delay(50, ct);

            Assert.Equal(leaderPid, Session(child));
            Assert.NotEqual(leaderPid, Group(child));

            var registered = Assert.Single(groups.Snapshot().Keys);
            var figures = new ProcessGroupReader().Read([registered])[registered];

            Assert.NotNull(figures);
            Assert.Equal(2, figures.Processes);
            Assert.True(
                figures.ResidentBytes >= Resident(leaderPid) + Resident(child),
                $"measured {figures.ResidentBytes} bytes; the leader holds {Resident(leaderPid)} and the other-group process {Resident(child)}");
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task A_background_job_in_another_group_is_gone_when_the_concierge_terminal_ends()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux sessions and /proc.");
        var ct = TestContext.Current.CancellationToken;
        var pidFile = Path.Combine(_workspace, "job.pid");
        var ended = new TaskCompletionSource<TerminalEnded>(TaskCreationOptions.RunContinuationsAsynchronously);

        var terminals = new WorkerTerminals(new Sink(), new PortaPtyEngine(), runAs: null, publish: e =>
        {
            if (e is TerminalEnded end) ended.TrySetResult(end);
            return Task.CompletedTask;
        });

        await terminals.StartAsync(new StartTerminal("terminal:sweep", new TerminalLaunch(
            ["bash", "-c", $"{OtherGroupJob(pidFile)}; sleep 0.5"], _workspace,
            new Dictionary<string, string>(), [], null, null, null), 80, 24), ct);

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        var job = Learn(pidFile);

        await GoneWithin(job, TimeSpan.FromSeconds(5));
        Assert.False(IsAlive(job), $"pid {job}, a background job in another group of the terminal's session, outlived it");
    }

    /// <summary>
    /// WITH A CGROUP OF ITS OWN, the run's end kills that too (<c>cgroup.kill</c>), which also reaches a
    /// process that left the session with <c>setsid</c>. Over a fixture tree: a kernel without
    /// <c>cgroup.kill</c> gets none written.
    /// </summary>
    [Fact]
    public void A_runs_own_cgroup_is_killed_through_cgroup_kill_when_the_run_ends()
    {
        var parent = Directory.CreateDirectory(Path.Combine(_workspace, "cgroup")).FullName;

        var cgroup = RunCgroup.Create(parent, 64L * 1024 * 1024);
        Assert.NotNull(cgroup);
        File.WriteAllText(Path.Combine(cgroup.Directory, "cgroup.kill"), "");
        File.WriteAllText(Path.Combine(cgroup.Directory, "cgroup.procs"), "");

        cgroup.Dispose();

        Assert.Equal("1", File.ReadAllText(Path.Combine(cgroup.Directory, "cgroup.kill")));

        var older = RunCgroup.Create(parent, 64L * 1024 * 1024);
        Assert.NotNull(older);
        older.Dispose();
        Assert.False(File.Exists(Path.Combine(older.Directory, "cgroup.kill")));
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

    /// <summary>The fields after the command name of <c>/proc/pid/stat</c>, or null when it is gone.</summary>
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

    /// <summary>Running, not a zombie: a killed process waiting to be reaped counts as gone.</summary>
    private static bool IsAlive(int pid) => Stat(pid) is { } fields && fields[0] != "Z";

    private static int? Group(int pid) => Stat(pid) is { } f ? int.Parse(f[2], CultureInfo.InvariantCulture) : null;

    private static int? Session(int pid) => Stat(pid) is { } f ? int.Parse(f[3], CultureInfo.InvariantCulture) : null;

    private static string? Command(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/comm").Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long Resident(int pid)
    {
        var fields = File.ReadAllText($"/proc/{pid}/statm").Split(' ');
        return long.Parse(fields[1], CultureInfo.InvariantCulture) * Environment.SystemPageSize;
    }

    private sealed class Sink : IRunStreamSink
    {
        public ValueTask<bool> StreamAsync(StreamChunk chunk, CancellationToken ct = default) => ValueTask.FromResult(true);
    }
}
