using System.Diagnostics;
using System.Runtime.InteropServices;
using Harness.Contracts;
using Harness.Pty;

namespace Harness.Host;

/// <summary>
/// HOW ANY MEMBER'S CHILD PROCESS IS LAUNCHED, whatever it is: in its own session through
/// `setsid`, as the agent user when the Host can switch to it, with both pipes pumped before the
/// wait, stdin written and closed, the whole process group killed when the run ends, and a bounded
/// drain for a grandchild still holding the pipe.
///
/// EXTRACTED FROM <see cref="ProcessAgentRunner"/> so a plugin member runs under exactly the
/// launch an agent does rather than a second copy of it. Nothing here knows what it is launching:
/// prompts, presets, usage and live views stay with the agent runner, and the plugin protocol stays
/// with <c>PluginMemberRunner</c>. Pinned by <c>ProcessAgentRunnerLaunchTests</c> (process group,
/// grandchild kill, PATH miss) and <c>ChildProcessTests</c>.
/// </summary>
public static class ChildProcess
{
    /// <summary>
    /// How long to keep draining a child's output AFTER the child itself has exited.
    ///
    /// Short on purpose. A process that exits on its own closes the last write handle as it goes, so
    /// the pumps are already finished and this is never spent; it is paid only when something the
    /// child started is still holding the pipe, and there the answer is not "wait longer" at any
    /// value - it is "stop waiting", which is what a bound gives.
    /// </summary>
    public static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// <paramref name="command"/> resolved with <see cref="PathSearch.Find"/>, LOOKING AGAIN after a
    /// pause while <paramref name="lookup"/>'s window lasts, or null when it never appeared.
    ///
    /// A MISS IS NOT FINAL AT ONCE, because the install every member launches from is shared and is
    /// replaced in place while it updates: a launch that lands in that gap of a few seconds finds
    /// no program, and a second look finds the new one. <paramref name="onFirstMiss"/> is called
    /// once, before the first pause, so the run can say what it is waiting for; a program that
    /// appears is returned and nothing else is recorded. A cancelled wait answers null.
    /// </summary>
    public static async Task<string?> FindAsync(
        string command, LaunchLookup lookup, Func<Task>? onFirstMiss, CancellationToken ct)
    {
        if (PathSearch.Find(command) is { } found) return found;
        if (lookup.Window <= TimeSpan.Zero) return null;

        if (onFirstMiss is not null) await onFirstMiss();

        var waited = System.Diagnostics.Stopwatch.StartNew();

        while (waited.Elapsed < lookup.Window)
        {
            try
            {
                await Task.Delay(lookup.Pause, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (PathSearch.Find(command) is { } appeared) return appeared;
        }

        return null;
    }

    /// <summary>
    /// The start info for <paramref name="resolvedFileName"/>, or null when `setsid` is not in a
    /// root-owned system directory - which the caller refuses in its own words.
    ///
    /// From a system directory, never PATH: setsid runs before the agent-user prefix, so it still
    /// holds the Host's capabilities. The arguments and environment are the caller's to add.
    /// </summary>
    /// <param name="limits">What goes between the agent-user prefix and the program: the run's
    /// memory limit (<see cref="RunMemoryLimits.Prefix"/>), applied after the switch so the agent,
    /// which keeps no capability, cannot raise it again.</param>
    public static ProcessStartInfo? StartInfo(
        string resolvedFileName, string workingDirectory, AgentLaunchUser? runAs, IReadOnlyList<string>? limits = null)
    {
        if (SystemCommand.Find("setsid") is not { } setsid) return null;

        var start = new ProcessStartInfo
        {
            // IN ITS OWN SESSION, AND SO ITS OWN PROCESS GROUP. `setsid` execs in place, so the
            // child keeps this pid and the group id is the pid. Everything the child starts joins
            // that group unless it asks for a session of its own, which is what lets the group
            // kill reach a grandchild that has been re-parented to init.
            FileName = setsid,
            WorkingDirectory = Directory.Exists(workingDirectory)
                ? workingDirectory
                : Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // As `agent` when the Host can switch, AFTER setsid so the session is still the
        // child's own: setpriv execs in place, and the group kill below reaches it as before.
        foreach (var part in runAs?.Prefix ?? []) start.ArgumentList.Add(part);
        foreach (var part in limits ?? []) start.ArgumentList.Add(part);
        start.ArgumentList.Add(resolvedFileName);

        return start;
    }

    /// <summary>
    /// Starts <paramref name="start"/>, writes <paramref name="stdin"/> (or nothing) and closes it,
    /// and waits for exit or for <paramref name="stopping"/>. A cancelled wait kills the process
    /// tree and reports <see cref="ChildOutcome.Killed"/>; it never throws
    /// <see cref="OperationCanceledException"/>. A process that cannot start throws, and the
    /// caller reports that as a launch failure.
    /// </summary>
    /// <param name="onStarted">Called once the process exists, before anything is written to it.</param>
    /// <param name="onStdoutLine">
    /// When given, stdout is read LINE BY LINE and each line handed over as it arrives - the plugin
    /// protocol. Without it stdout is pumped in chunks, which is what an agent's JSON envelope needs.
    /// Either way the text is captured into <see cref="ChildOutcome.Stdout"/>.
    /// </param>
    /// <param name="run">The run this child is, registered in <paramref name="groups"/> (the Host's
    /// <see cref="Capacity.RunProcessGroups.Shared"/> when not given) from its start to its end, so
    /// the capacity sampler can sum its process group under its team and member.</param>
    public static async Task<ChildOutcome> RunAsync(
        ProcessStartInfo start,
        string? stdin,
        CancellationToken stopping,
        Action<Process>? onStarted = null,
        Func<string, Task>? onStdoutLine = null,
        ContainerId? run = null,
        Capacity.RunProcessGroups? groups = null)
    {
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The process did not start.");

        // THE BACKSTOP FOR WHAT THE TREE WALK CANNOT REACH. Declared after `process` so
        // it disposes first: every exit from this method, including a run that completes
        // normally, takes whatever is left in the child's process group with it.
        using var group = new ProcessGroup(process.Id);

        // Named for the capacity sampler until the run ends, however it ends.
        using var registered = run is { } owner
            ? (groups ?? Capacity.RunProcessGroups.Shared).Register(process.Id, owner)
            : null;

        onStarted?.Invoke(process);

        // Both streams are read BEFORE waiting for exit. Waiting first deadlocks as soon as a
        // child fills a pipe buffer, which is a hang rather than a failure and therefore much
        // harder to diagnose than any error would have been.
        //
        // PUMPED INTO A BUILDER rather than ReadToEndAsync, and that is what makes the drain
        // below possible: a ReadToEnd task that never completes hands back NOTHING, so bounding
        // the wait on one would trade a hang for the silent loss of a whole transcript. A pump
        // has written everything it has seen by the time we stop waiting for it.
        //
        // Uncancellable, deliberately: a run stopped by either clock takes the kill path below and
        // reports what happened, where a torn read would surface first and lose the reason.
        // Bounded: head and tail of each stream, never the whole of a large file a child printed.
        var stdoutText = new BoundedCapture();
        var stderrText = new BoundedCapture();
        var stdout = onStdoutLine is null
            ? PumpAsync(process.StandardOutput, stdoutText)
            : PumpLinesAsync(process.StandardOutput, stdoutText, onStdoutLine);
        var stderr = PumpAsync(process.StandardError, stderrText);

        // INSIDE THE TRY THAT KILLS, and on `stopping`. Outside it, a cancellation arriving here
        // would throw straight past the kill path: the run would end, the member go Idle, and the
        // child keep running - Stop reporting success while leaving an orphan.
        //
        // It is a real hang site rather than a theoretical one: a CLI that never reads stdin and
        // an input larger than the pipe buffer blocks here forever, which is why it takes the
        // clock as well as the caller's token.
        try
        {
            // A CHILD THAT HAS ALREADY EXITED IS NOT A FAILED RUN.
            //
            // A fast child - one that answers from cache, refuses immediately, or is a one-liner -
            // can be gone before this write lands, and writing to its closed pipe throws
            // `IOException: The pipe is being closed`. It is timing, so it is invisible on an idle
            // machine and reproducible under load. Swallowed for the same reason `Kill` swallows
            // `InvalidOperationException`: a process that finished between two of our own calls
            // must not turn a successful run into a failure. Its output is still drained below.
            try
            {
                if (stdin is not null)
                {
                    await process.StandardInput.WriteAsync(stdin.AsMemory(), stopping);
                }

                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            await process.WaitForExitAsync(stopping);
        }
        catch (OperationCanceledException)
        {
            // KILLED, tree and all. `using var process` disposes a HANDLE rather than a process,
            // so returning without this would leave the child running - parented to nothing,
            // with no member and no queue to come back to. A CLI that spawns its own helpers is
            // the normal case, so killing only the parent leaves the same orphan one level down.
            Kill(process);

            return new ChildOutcome(-1, stdoutText.ToString(), stderrText.ToString(), HeldOpen: false, Killed: true, process.Id);
        }

        // THE PROCESS WE OWN HAS GONE. Anything still holding the pipe is a grandchild we did
        // not launch and do not manage - a server, a watcher, a tunnel - and waiting on it is
        // waiting on a handle nobody is going to close.
        //
        // Costs nothing on an ordinary run: a child that exits on its own closes the last write
        // handle as it goes, so both pumps are already finished and the grace is never spent.
        var pumps = Task.WhenAll(stdout, stderr);

        var heldOpen = await Task.WhenAny(pumps, Task.Delay(DrainGrace)) != pumps;

        // Abandoned, not awaited. The pumps stay parked on a read that a cancellation token
        // would not interrupt anyway, so they are left to finish whenever the grandchild exits.
        return new ChildOutcome(
            process.ExitCode, stdoutText.ToString(), stderrText.ToString(), heldOpen, Killed: false, process.Id);
    }

    /// <summary>
    /// Reads a stream into <paramref name="into"/> as it arrives, rather than at end-of-file.
    ///
    /// The point is that what has been read SURVIVES abandoning the read. `ReadToEndAsync` hands
    /// back a string only on EOF, so a task still waiting on a pipe a grandchild is holding yields
    /// nothing at all - bounding the wait on one would trade a hang for a lost transcript.
    ///
    /// Takes no CancellationToken, and that is not an omission: a pending pipe read on Windows does
    /// not unblock on cancel, so a token here would buy the illusion of control. The caller stops
    /// WAITING instead, and this is left to end on its own whenever the last writer closes.
    ///
    /// <see cref="BoundedCapture"/> locks, because the caller reads it while an abandoned pump may
    /// still be appending.
    /// </summary>
    private static async Task PumpAsync(TextReader reader, BoundedCapture into)
    {
        var buffer = new char[4096];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);

            if (read <= 0) return;

            into.Append(buffer, 0, read);
        }
    }

    /// <summary>
    /// <see cref="PumpAsync"/> for a line protocol: the same capture, and each line handed to
    /// <paramref name="onLine"/> as it arrives. A handler that throws loses its line, never the
    /// rest of the stream.
    /// </summary>
    private static async Task PumpLinesAsync(TextReader reader, BoundedCapture into, Func<string, Task> onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var text = line + "\n";
            into.Append(text.ToCharArray(), 0, text.Length);

            try
            {
                await onLine(line).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// Ends the child and everything it started, tolerating the race with a normal exit.
    ///
    /// `entireProcessTree` because a CLI that spawns helpers is the ordinary case and killing only
    /// the parent leaves the same orphan one level down. Wrapped because a process that exited
    /// between the wait ending and this call throws `InvalidOperationException` - and a run that
    /// finished on its own must not be reported as killed.
    /// </summary>
    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    /// <summary>
    /// AN IDLE CLOCK, NOT A WALL CLOCK, joined with the caller's token into
    /// <see cref="IdleClock.Stopping"/>. `CancelAfter` RESETS a pending countdown, so every
    /// progress report the member files through <see cref="RunHeartbeat"/> pushes its own deadline
    /// out again: <paramref name="seconds"/> means "this long with nothing to say", not "this long
    /// in total". NOT a clock that resets on OUTPUT: a stuck child still printing would hold it
    /// open forever. Null or non-positive seconds is unbounded.
    /// </summary>
    public static IdleClock Clock(RunHeartbeat heartbeat, ContainerId who, int? seconds, CancellationToken ct) =>
        new(heartbeat, who, seconds, ct);

    public sealed class IdleClock : IDisposable
    {
        private readonly CancellationTokenSource _expiry;
        private readonly IDisposable? _narrating;
        private readonly CancellationTokenSource _stopping;
        private readonly CancellationToken _caller;
        private readonly Lock _gate = new();
        private bool _paused;

        internal IdleClock(RunHeartbeat heartbeat, ContainerId who, int? seconds, CancellationToken ct)
        {
            _caller = ct;
            _expiry = seconds is { } s and > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(s))
                : new CancellationTokenSource();
            _narrating = seconds is { } window and > 0
                ? heartbeat.WhileRunning(who, () => Restart(window), paused => Hold(paused, window))
                : null;
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, _expiry.Token);
        }

        /// <summary>A progress report: a full window again, unless the clock is paused.</summary>
        private void Restart(int window)
        {
            lock (_gate)
            {
                if (!_paused) _expiry.CancelAfter(TimeSpan.FromSeconds(window));
            }
        }

        /// <summary>
        /// PAUSED WHILE THE MEMBER WAITS IN A LEASE'S QUEUE: an infinite countdown, which a progress
        /// report does not shorten. Resumed with a full window, so the wait itself is never
        /// counted as silence.
        /// </summary>
        private void Hold(bool paused, int window)
        {
            lock (_gate)
            {
                _paused = paused;
                _expiry.CancelAfter(paused ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(window));
            }
        }

        /// <summary>Fires for the caller's token or the clock, whichever is first.</summary>
        public CancellationToken Stopping => _stopping.Token;

        /// <summary>Whether it was THIS clock that ran out, rather than the caller stopping the run.
        /// The two are reported in different words because they are fixed in different places.</summary>
        public bool Expired => _expiry.IsCancellationRequested && !_caller.IsCancellationRequested;

        public void Dispose()
        {
            _stopping.Dispose();
            _narrating?.Dispose();
            _expiry.Dispose();
        }
    }

    /// <summary>
    /// Kills the child's process group when disposed. The group is the child's pid because the
    /// child was started through `setsid`. A group that is already empty answers ESRCH, which is
    /// the ordinary case and is ignored.
    /// </summary>
    private sealed class ProcessGroup(int id) : IDisposable
    {
        public void Dispose() => _ = kill(-id, SigKill);
    }

    private const int SigKill = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}

/// <summary>
/// How long a launch keeps looking for a program that is not on PATH, and how long it pauses
/// between looks. <see cref="Default"/> is the Host's; a test passes a short one.
/// </summary>
public sealed record LaunchLookup(TimeSpan Window, TimeSpan Pause)
{
    /// <summary>About 30 seconds, looking every 3: longer than an in-place CLI update takes.</summary>
    public static readonly LaunchLookup Default = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3));

    /// <summary>One look and no wait.</summary>
    public static readonly LaunchLookup Once = new(TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>The window in words: "30 seconds".</summary>
    public string WindowText => $"{(int)Math.Round(Window.TotalSeconds)} seconds";
}

/// <summary>What became of one child process.</summary>
/// <param name="Killed">True when the wait was cancelled and the tree was killed; the exit code is
/// then -1 and the captured text is whatever arrived before.</param>
/// <param name="HeldOpen">True when something the child started still held its output after
/// <see cref="ChildProcess.DrainGrace"/>.</param>
public sealed record ChildOutcome(int ExitCode, string Stdout, string Stderr, bool HeldOpen, bool Killed, int ProcessId);
