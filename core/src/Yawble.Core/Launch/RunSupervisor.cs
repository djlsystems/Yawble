using System.Diagnostics;

namespace Yawble.Core.Launch;

/// <summary>How a run ended, as the card shows it (timeout, idle, stopped).</summary>
public enum RunEnd
{
    Completed,
    Failed,
    Timeout,
    Idle,
    Stopped,
    Interrupted,
    StartFailed,
    Credential,
}

public static class RunEnds
{
    public static string Code(RunEnd end) => end switch
    {
        RunEnd.Completed => "completed",
        RunEnd.Failed => "failed",
        RunEnd.Timeout => "timeout",
        RunEnd.Idle => "idle",
        RunEnd.Stopped => "stopped",
        RunEnd.Interrupted => "interrupted",
        RunEnd.StartFailed => "start_failed",
        RunEnd.Credential => "credential",
        _ => throw new ArgumentOutOfRangeException(nameof(end)),
    };
}

/// <summary>What the supervisor observed. KillLatency: from the kill decision to the sandbox reported gone.</summary>
public sealed record SupervisedExit(
    RunEnd End,
    int? ExitCode,
    bool SandboxTimedOut,
    TimeSpan Duration,
    TimeSpan? KillLatency,
    bool TreeSurvived,
    bool EndedAfterAnswer = false,
    int LeftBehind = 0,
    int TreeSignalled = 0);

public sealed class SupervisorOptions
{
    /// <summary>How often the handle is polled (try_wait), and the watchdog and timers are checked.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The kill bound: after a kill, the tree must be gone within this.</summary>
    public TimeSpan KillBound { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to let the output pumps drain after exit.</summary>
    public TimeSpan Drain { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// After the CLI's final answer (<see cref="FinalAnswer"/>): how long it may take to exit on its own before the
    /// sandbox is ended and the run counts as completed.
    /// </summary>
    public TimeSpan AfterAnswer { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Supervises one live sandbox: the wall-clock timeout, the idle watchdog, Stop and core shutdown, each of which
/// kills through the sandbox handle (the sandbox's kill: on Linux the bwrap PID namespace, never a process-group
/// kill, which misses processes that left the group). The core's own wall-clock timer runs on every OS, so the
/// timeout holds where the sandbox does not report timedOut (the wxc-exec route on Windows); the sandbox's
/// process.timeout in the request is the second line.
/// All handle calls happen on the supervisor's thread, as the sandbox's handle contract requires.
/// </summary>
public sealed class RunSupervisor
{
    private readonly ISandboxProcess _process;
    private readonly TimeSpan _wall;
    private readonly TimeSpan _idle;
    private readonly RunActivity _activity;
    private readonly IRunOutput? _output;
    private readonly IRunOutput? _errorOutput;
    private readonly TimeProvider _clock;
    private readonly SupervisorOptions _options;
    private volatile bool _stopRequested;
    private readonly Func<DateTimeOffset?>? _answeredAt;
    private readonly RunTree? _tree;
    private readonly Action? _treeGone;
    private readonly IRunFolderHolders? _holders;
    private readonly string? _runFolder;
    private readonly Action<string>? _log;

    /// <param name="tree">macOS: the run's processes, followed while it runs and ended with it (<see cref="RunTree"/>).</param>
    /// <param name="treeGone">Called once the tree is ended (or the wait for it is up), before the run is reported done: the run's temp folder is removed there.</param>
    /// <param name="holders">macOS backstop (B127): after the tree is reaped, the processes still holding <paramref name="runFolder"/> are adopted into it and reaped too.</param>
    /// <param name="runFolder">The run's yr-* temp folder the backstop looks under.</param>
    /// <param name="log">The core log: one teardown line when a process outlived the reap or was found only by the backstop.</param>
    public RunSupervisor(ISandboxProcess process, int wallTimeoutSec, int idleTimeoutSec, RunActivity activity,
        IRunOutput? output, TimeProvider? clock = null, SupervisorOptions? options = null, IRunOutput? errorOutput = null,
        Func<DateTimeOffset?>? answeredAt = null, RunTree? tree = null, Action? treeGone = null,
        IRunFolderHolders? holders = null, string? runFolder = null, Action<string>? log = null)
    {
        _tree = tree;
        _treeGone = treeGone;
        _holders = holders;
        _runFolder = runFolder;
        _log = log;
        _answeredAt = answeredAt;
        _errorOutput = errorOutput;
        _process = process;
        _wall = TimeSpan.FromSeconds(Math.Max(1, wallTimeoutSec));
        // 0 turns the watchdog off (an interactive session waits on the person, not on progress calls).
        _idle = idleTimeoutSec <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(idleTimeoutSec);
        _activity = activity;
        _output = output;
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new SupervisorOptions();
    }

    /// <summary>The person's Stop. Takes effect at the next poll.</summary>
    public void RequestStop() => _stopRequested = true;

    public bool StopRequested => _stopRequested;

    /// <summary>Runs until the sandbox exits or is killed. <paramref name="shutdown"/> is the core stopping.</summary>
    public async Task<SupervisedExit> RunAsync(CancellationToken shutdown)
    {
        var started = _clock.GetTimestamp();
        var pumps = new List<Task>();
        if (_process.Output is { } o) pumps.Add(Pump(o, _output));
        // stderr joins stdout (one terminal) unless the caller keeps it apart (a probe's result is its stdout).
        if (_process.Error is { } e) pumps.Add(Pump(e, _errorOutput ?? _output));

        var exit = await Task.Factory.StartNew(() => EndTree(Supervise(shutdown, started)), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);

        try
        {
            await Task.WhenAll(pumps).WaitAsync(_options.Drain).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A grandchild that escaped the kill could hold a pipe open; the record already says TreeSurvived.
        }
        return exit;
    }

    /// <summary>
    /// macOS: whatever the run started is ended before the run is reported done, however the CLI ended (its own exit
    /// too: the sandbox there has no PID namespace that takes the rest down). B127: then the backstop - whatever still
    /// holds the run's folder (a build that left both the tree and its group) is adopted and reaped - and only then
    /// <c>treeGone</c>. Anything that outlived the reap, or was found only by the backstop, makes the run's record say
    /// TreeSurvived and gets one teardown line in the core log.
    /// </summary>
    private SupervisedExit EndTree(SupervisedExit exit)
    {
        try
        {
            if (_tree is { } tree)
            {
                var reaped = tree.Reap();
                var signalled = reaped.Signalled;
                var survivors = reaped.Survivors;
                var fromTree = reaped.Survivors.ToHashSet();
                List<int> fromFolder = [];
                List<int> fromGroup = [];
                if (_holders is not null && !string.IsNullOrEmpty(_runFolder))
                {
                    var before = tree.Tracked.ToHashSet();
                    IReadOnlyList<int> holding;
                    try { holding = _holders.Under(_runFolder); }
                    catch (Exception e) when (e is IOException or DllNotFoundException or EntryPointNotFoundException or UnauthorizedAccessException) { holding = []; }
                    if (holding.Count > 0 && tree.Adopt(holding) > 0)
                    {
                        // What the backstop took on: tracked now, not before (a pid tracked before with another start time
                        // is a new process).
                        var adopted = tree.Tracked.Where(t => !before.Contains(t)).ToHashSet();
                        fromFolder = [.. adopted.Select(t => t.Key).Order()];
                        var again = tree.Reap();
                        signalled += again.Signalled;
                        survivors = again.Survivors;
                        // B129: what the second reap found through the adopted ones' groups or ppids (tracked after the
                        // adoption) or signalled besides them; never one the first reap already reported.
                        var newlyTracked = tree.Tracked.Where(t => !before.Contains(t) && !adopted.Contains(t)).Select(t => t.Key);
                        fromGroup = [.. newlyTracked.Concat(again.SignalledPids)
                            .Where(p => !fromTree.Contains(p) && !fromFolder.Contains(p)).Distinct().Order()];
                    }
                }
                var survived = survivors.Count > 0 || fromFolder.Count > 0;
                exit = exit with
                {
                    TreeSurvived = exit.TreeSurvived || survived,
                    LeftBehind = survivors.Count,
                    TreeSignalled = signalled,
                };
                if (survived) _log?.Invoke(TeardownLine(fromTree, fromFolder, fromGroup, survivors));
            }
        }
        finally
        {
            _treeGone?.Invoke();
        }
        return exit;
    }

    /// <summary>
    /// The core log's teardown line: every process that outlived the tree's own reap (<c>tree</c>: found by its ppid or
    /// its group), was found only holding the run's folder (<c>run-folder</c>), or was found by the reap after that
    /// through the recorded group or ppid of what the backstop took on (<c>group</c>), and whether it is still running.
    /// </summary>
    internal static string TeardownLine(IReadOnlySet<int> fromTree, IReadOnlyList<int> fromFolder, IReadOnlyList<int> fromGroup,
        IReadOnlyList<int> survivors)
    {
        var alive = survivors.ToHashSet();
        var parts = fromTree.Select(p => (Pid: p, How: "tree"))
            .Concat(fromFolder.Where(p => !fromTree.Contains(p)).Select(p => (Pid: p, How: "run-folder")))
            .Concat(fromGroup.Where(p => !fromTree.Contains(p) && !fromFolder.Contains(p)).Select(p => (Pid: p, How: "group")))
            .Concat(survivors.Where(p => !fromTree.Contains(p) && !fromFolder.Contains(p) && !fromGroup.Contains(p)).Select(p => (Pid: p, How: "tree")))
            .OrderBy(x => x.Pid)
            .Select(x => $"{x.Pid} ({x.How}, {(alive.Contains(x.Pid) ? "still running after SIGKILL" : "reaped")})");
        return $"run teardown: tree survived: {string.Join(", ", parts)}";
    }

    private SupervisedExit Supervise(CancellationToken shutdown, long started)
    {
        var deadline = _clock.GetUtcNow() + _wall;
        while (true)
        {
            _tree?.Observe();
            var done = _process.TryWait();
            if (done is { } d)
            {
                var end = d.TimedOut ? RunEnd.Timeout : d.ExitCode == 0 ? RunEnd.Completed : RunEnd.Failed;
                return new SupervisedExit(end, d.TimedOut ? null : d.ExitCode, d.TimedOut, _clock.GetElapsedTime(started), null, false);
            }

            var now = _clock.GetUtcNow();
            RunEnd? kill = null;
            if (shutdown.IsCancellationRequested) kill = RunEnd.Interrupted;
            else if (_stopRequested) kill = RunEnd.Stopped;
            else if (now >= deadline) kill = RunEnd.Timeout;
            else if (_idle != Timeout.InfiniteTimeSpan && now - _activity.Last >= _idle) kill = RunEnd.Idle;
            if (kill is { } reason) return KillAndReap(reason, started);
            // The CLI gave its final answer but is still running (waiting on a leftover process): its work is done.
            if (_answeredAt?.Invoke() is { } answered && now - answered >= _options.AfterAnswer)
                return KillAndReap(RunEnd.Completed, started) with { End = RunEnd.Completed, EndedAfterAnswer = true };

            Thread.Sleep(_options.Poll);
        }
    }

    private SupervisedExit KillAndReap(RunEnd reason, long started)
    {
        var killAt = Stopwatch.GetTimestamp();
        try
        {
            if (reason == RunEnd.Timeout) _process.KillForTimeout();
            else _process.Kill();
        }
        catch (IOException)
        {
            // Already gone between try_wait and kill: the poll below sees the exit.
        }
        SandboxExit? exit = null;
        while (Stopwatch.GetElapsedTime(killAt) < _options.KillBound)
        {
            _tree?.Observe();
            exit = _process.TryWait();
            if (exit is not null) break;
            Thread.Sleep(_options.Poll);
        }
        var latency = Stopwatch.GetElapsedTime(killAt);
        // Past the bound: free the handle (which kills again) and record the gap rather than wait forever.
        if (exit is null) _process.Dispose();
        return new SupervisedExit(reason, exit?.TimedOut == true ? null : exit?.ExitCode, exit?.TimedOut == true,
            _clock.GetElapsedTime(started), latency, exit is null);
    }

    private static Task Pump(Stream stream, IRunOutput? sink) => Task.Factory.StartNew(() =>
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) sink?.Write(buffer.AsSpan(0, n));
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
