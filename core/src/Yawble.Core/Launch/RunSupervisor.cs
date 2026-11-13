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
    bool EndedAfterAnswer = false);

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

    public RunSupervisor(ISandboxProcess process, int wallTimeoutSec, int idleTimeoutSec, RunActivity activity,
        IRunOutput? output, TimeProvider? clock = null, SupervisorOptions? options = null, IRunOutput? errorOutput = null,
        Func<DateTimeOffset?>? answeredAt = null)
    {
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

        var exit = await Task.Factory.StartNew(() => Supervise(shutdown, started), CancellationToken.None,
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

    private SupervisedExit Supervise(CancellationToken shutdown, long started)
    {
        var deadline = _clock.GetUtcNow() + _wall;
        while (true)
        {
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
