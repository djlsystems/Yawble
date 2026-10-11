using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Harness.Contracts;
using Harness.Host.Capacity;

namespace Harness.Host;

/// <summary>
/// THE HEAVY ALLOWANCE: a run holding the <c>heavy</c> lease gets the memory heavy work needs, and
/// goes back to its own limit when it stops holding it. <c>LeaseActions</c> calls
/// <see cref="ReconcileAsync"/> after every acquire, release and run end, so whichever path moved
/// the lease (the route, a run's end, any later release path) also moves the limit: each running run
/// is brought to the limit its holding says.
///
/// <para>
/// HOW MUCH is measured, not set (<c>TenantSettings.HeavyRunMemoryLimit</c>): the container's
/// limit less the Host reserve less the resident memory of every other running run, read from
/// <c>/proc</c> over <see cref="RunProcessGroups"/> at the moment the lease is granted, and never
/// below the run's own limit. It is not re-read as the others grow; admission still guards them.
/// </para>
/// <para>
/// HOW, per mechanism:
/// </para>
/// <list type="bullet">
/// <item><b>cgroup</b>: the run's <c>memory.max</c> is raised in place; it covers every process in
/// the run, running or started later.</item>
/// <item><b>rlimit</b>: <c>prlimit --pid</c> raises the SOFT data limit of every process in the
/// run's session - the agent and whatever it already started - so a process already running gets
/// the raise too, and everything started afterwards inherits it. Up to the HARD limit the run was
/// launched with (<see cref="RunMemoryLimits.Ceiling"/>): raising a hard limit needs a capability
/// no rootless engine gives, and the agent cannot raise it either. Run as the agent's own user,
/// which may set its own processes' soft limit.</item>
/// </list>
/// <para>
/// LOWERING NEVER KILLS A RUN INSIDE ITS OLD LIMIT. A cgroup is lowered only once what it cannot give
/// back fits the run's own limit; a process only once its data size does. Until then it keeps the
/// raised limit, and the lowering is tried again every few seconds until it is done or the run ends.
/// </para>
/// </summary>
public sealed class RunAllowances
{
    /// <summary>Sets a process's soft data limit to the given bytes; true when it was set.</summary>
    public delegate Task<bool> SoftLimitWriter(int pid, long bytes, CancellationToken ct);

    private readonly RunMemoryLimits _memory;
    private readonly Func<long, int, RunMemoryLimit> _heavy;
    private readonly Func<IReadOnlyCollection<string>> _holders;
    private readonly ProcessGroupReader _processes;
    private readonly RunProcessGroups _groups;
    private readonly string _procRoot;
    private readonly long _pageSize;
    private readonly SoftLimitWriter _writeSoft;
    private readonly ILogger? _log;
    private readonly TimeSpan _retry;
    private readonly ConcurrentDictionary<string, RunAllowance> _runs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _retrying;

    /// <param name="heavy">The heavy figure for the other runs' measured MB and count
    /// (<c>TenantSettings.HeavyRunMemoryLimit</c>).</param>
    /// <param name="heavyHolders">The lease owner keys holding <c>heavy</c> right now.</param>
    /// <param name="procRoot">The proc mount; tests hand a fixture tree.</param>
    /// <param name="writeSoft">How a process's soft limit is set; <c>prlimit --pid</c> as the agent's user when not given.</param>
    public RunAllowances(
        RunMemoryLimits memory,
        Func<long, int, RunMemoryLimit> heavy,
        Func<IReadOnlyCollection<string>> heavyHolders,
        ProcessGroupReader? processes = null,
        RunProcessGroups? groups = null,
        string procRoot = "/proc",
        long? pageSize = null,
        SoftLimitWriter? writeSoft = null,
        AgentLaunchUser? runAs = null,
        ILogger<RunAllowances>? log = null,
        TimeSpan? retry = null)
    {
        _memory = memory;
        _heavy = heavy;
        _holders = heavyHolders;
        _processes = processes ?? new ProcessGroupReader(procRoot, pageSize);
        _groups = groups ?? RunProcessGroups.Shared;
        _procRoot = procRoot;
        _pageSize = pageSize ?? Environment.SystemPageSize;
        _writeSoft = writeSoft ?? ((pid, bytes, ct) => PrlimitAsync(memory.PrlimitPath, runAs, pid, bytes, ct));
        _log = log;
        _retry = retry ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// A run whose process exists: <paramref name="leader"/> leads its session, <paramref name="normal"/>
    /// is the limit it started with and <paramref name="ceiling"/> its hard limit under rlimit. Null when
    /// nothing limits the run. Dispose it when the run ends.
    /// </summary>
    public RunAllowance? Begin(ContainerId run, int leader, RunMemoryLimit normal, RunMemoryLimit ceiling, RunCgroup? cgroup)
    {
        if (normal.Mb is null || _memory.Mechanism == RunMemoryMechanism.None) return null;
        if (_memory.Mechanism == RunMemoryMechanism.Cgroup && cgroup is null) return null;

        var allowance = new RunAllowance(this, run, leader, normal, ceiling, cgroup);
        _runs[allowance.Key] = allowance;

        // A lease granted between the run's start and this registration is not missed.
        if (_holders().Contains(allowance.Key)) _ = ReconcileQuietlyAsync();
        return allowance;
    }

    /// <summary>The allowance of the run <paramref name="key"/> (a lease owner's key), when it is running and limited.</summary>
    public RunAllowance? Find(string key) => _runs.GetValueOrDefault(key);

    /// <summary>
    /// Brings every running run to the limit its holding says: raised while it holds <c>heavy</c>,
    /// back to its own once it does not (and fits). Never throws for a run it cannot change; that run
    /// keeps what it has and the next call tries again.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        var pending = false;
        try
        {
            var holders = _holders().ToHashSet(StringComparer.Ordinal);
            foreach (var run in _runs.Values)
            {
                if (run.Ended) continue;

                if (holders.Contains(run.Key))
                {
                    if (!run.Raised) await RaiseAsync(run, ct);
                }
                else if (run.Raised)
                {
                    pending |= !await LowerAsync(run, ct);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (pending) ScheduleRetry();
    }

    private async Task ReconcileQuietlyAsync()
    {
        try
        {
            await ReconcileAsync();
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Moving a run's memory limit for the heavy lease failed: {Message}", ex.Message);
        }
    }

    private void ScheduleRetry()
    {
        if (Interlocked.Exchange(ref _retrying, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(_retry);
            Volatile.Write(ref _retrying, 0);
            await ReconcileQuietlyAsync();
        });
    }

    private async Task RaiseAsync(RunAllowance run, CancellationToken ct)
    {
        var (othersMb, others) = MeasureOthers(run.Run);
        var heavy = _heavy(othersMb, others);
        var normalMb = run.Normal.Mb!.Value;

        // Under rlimit the hard limit the run started with is as far as a soft limit goes.
        if (_memory.Mechanism == RunMemoryMechanism.Rlimit && heavy.Mb is { } wanted && run.Ceiling.Mb is { } hard && wanted > hard)
        {
            heavy = new RunMemoryLimit(hard, $"{heavy.Source}, held to the {hard} MB hard limit this run started with");
        }

        if (heavy.Mb is not { } mb || mb <= normalMb)
        {
            run.Raise(run.Normal with { Source = heavy.Source });
            return;
        }

        var bytes = RunMemoryLimits.Bytes(mb);
        if (_memory.Mechanism == RunMemoryMechanism.Cgroup)
        {
            if (run.Cgroup?.SetMax(bytes) != true)
            {
                _log?.LogWarning("Could not raise {Run}'s cgroup to {Mb} MB for the heavy lease.", run.Run, mb);
                return;
            }
        }
        else
        {
            // Twice: a process forked while the first pass ran took its parent's old limit.
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var pid in Session(run.Leader))
                {
                    if (SoftLimit(pid) != bytes) await _writeSoft(pid, bytes, ct);
                }
            }
        }

        run.Raise(heavy);
        _log?.LogInformation("{Run} holds the heavy lease: its memory limit is {Mb} MB, as {Source}.", run.Run, mb, heavy.Source);
    }

    /// <summary>True when the run is back at its own limit; false when part of it is still over and keeps the raise.</summary>
    private async Task<bool> LowerAsync(RunAllowance run, CancellationToken ct)
    {
        var normalBytes = RunMemoryLimits.Bytes(run.Normal.Mb!.Value);
        var done = true;

        if (_memory.Mechanism == RunMemoryMechanism.Cgroup)
        {
            done = run.Cgroup?.Unreclaimable() is { } used && used <= normalBytes && run.Cgroup.SetMax(normalBytes);
        }
        else
        {
            foreach (var pid in Session(run.Leader))
            {
                if (SoftLimit(pid) is { } soft && soft <= normalBytes) continue;

                // A process over the old limit keeps the raise until it fits: lowering it now would
                // fail its next allocation, which is what kills it.
                if (DataBytes(pid) is not { } data || data > normalBytes || !await _writeSoft(pid, normalBytes, ct))
                {
                    done = false;
                }
            }
        }

        if (done)
        {
            run.Lower();
            _log?.LogInformation("{Run} no longer holds the heavy lease: its memory limit is back to {Mb} MB.", run.Run, run.Normal.Mb);
        }

        return done;
    }

    /// <summary>Resident memory of every other running run, in MB (rounded up), and how many there are.</summary>
    private (long Mb, int Runs) MeasureOthers(ContainerId self)
    {
        var others = _groups.Snapshot().Where(g => g.Value != self).ToList();
        if (others.Count == 0) return (0, 0);

        var sums = _processes.Read(others.Select(g => g.Key).ToList());
        var bytes = sums.Values.Sum(f => f?.ResidentBytes ?? 0);
        return ((bytes + (1024 * 1024) - 1) / (1024 * 1024), others.Select(g => g.Value).Distinct().Count());
    }

    /// <summary>Every process whose session is <paramref name="leader"/>'s: the run started it in its own (setsid).</summary>
    private IReadOnlyList<int> Session(int leader)
    {
        List<int> pids = [];
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories(_procRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return pids;
        }

        foreach (var entry in entries)
        {
            if (!int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;

            var text = Read(Path.Combine(entry, "stat"));
            var close = text?.LastIndexOf(')') ?? -1;
            if (close < 0) continue;

            // After ")": state ppid pgrp session ...
            var fields = text![(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 3 && int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var session)
                && session == leader)
            {
                pids.Add(pid);
            }
        }

        return pids;
    }

    /// <summary>statm's sixth field (data and stack), in bytes: what the data limit is measured against, or more.</summary>
    private long? DataBytes(int pid)
    {
        var fields = Read(Path.Combine(_procRoot, pid.ToString(CultureInfo.InvariantCulture), "statm"))
            ?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields is { Length: >= 6 } && long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages)
            ? pages * _pageSize
            : null;
    }

    /// <summary>The process's soft data limit from <c>/proc/&lt;pid&gt;/limits</c>, or null (unread, or unlimited).</summary>
    private long? SoftLimit(int pid)
    {
        var text = Read(Path.Combine(_procRoot, pid.ToString(CultureInfo.InvariantCulture), "limits"));
        var line = text?.Split('\n').FirstOrDefault(l => l.StartsWith("Max data size", StringComparison.Ordinal));
        var fields = line?["Max data size".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields is { Length: >= 1 } && long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var soft)
            ? soft
            : null;
    }

    private static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><c>prlimit --pid &lt;pid&gt; --data=&lt;bytes&gt;:</c> (soft only), as the agent's user so it may.</summary>
    private static async Task<bool> PrlimitAsync(string? prlimit, AgentLaunchUser? runAs, int pid, long bytes, CancellationToken ct)
    {
        if (prlimit is null || runAs is { Refuses: true }) return false;

        IReadOnlyList<string> command = [prlimit, "--pid", pid.ToString(CultureInfo.InvariantCulture), $"--data={bytes}:"];
        if (runAs is not null) command = runAs.Wrap(command);

        var start = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var drained = Task.WhenAll(process.StandardOutput.ReadToEndAsync(timeout.Token), process.StandardError.ReadToEndAsync(timeout.Token));
            await process.WaitForExitAsync(timeout.Token);
            await drained;
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal void Remove(RunAllowance run) => _runs.TryRemove(new KeyValuePair<string, RunAllowance>(run.Key, run));

    /// <summary>
    /// WATCHES ONE RUN FOR A PROCESS THE LIMIT STOPPED while the agent carries on, and says so through
    /// <paramref name="report"/> with the limit in force (<paramref name="limitNow"/>) and the setting.
    /// With a cgroup it is the kernel's own count, <c>oom_kill</c> in <c>memory.events</c>, read every
    /// <paramref name="poll"/> while <paramref name="alive"/>; with an rlimit nothing in the kernel
    /// records it, so it is the process's own words in the run's transcript
    /// (<see cref="RunMemoryLimits.ChildOutOfMemory"/>). Ends with <paramref name="ended"/>; never throws.
    /// </summary>
    public static async Task WatchAsync(
        RunMemoryLimits memory,
        Func<RunMemoryLimit> limitNow,
        RunCgroup? cgroup,
        Func<IAsyncEnumerable<string>?>? transcript,
        Func<bool> alive,
        Func<string, Task> report,
        TimeSpan poll,
        CancellationToken ended)
    {
        try
        {
            if (memory.Mechanism == RunMemoryMechanism.Cgroup && cgroup is not null)
            {
                var seen = cgroup.OomKills();
                while (!ended.IsCancellationRequested)
                {
                    await Task.Delay(poll, ended);

                    var kills = cgroup.OomKills();
                    if (kills <= seen) continue;

                    var killed = kills - seen;
                    seen = kills;
                    if (alive())
                    {
                        await report(memory.ChildSentence(limitNow(),
                            $"the kernel OOM-killed {killed} process{(killed == 1 ? "" : "es")} in the run's cgroup"));
                    }
                }
            }
            else if (memory.Mechanism == RunMemoryMechanism.Rlimit && transcript?.Invoke() is { } lines)
            {
                await foreach (var line in lines.WithCancellation(CancellationToken.None))
                {
                    if (RunMemoryLimits.ChildOutOfMemory(line) is { } words)
                    {
                        await report(memory.ChildSentence(limitNow(), $"it printed \"{words}\""));
                    }
                }
            }
        }
        catch (Exception)
        {
            // An observer: the run goes on whether or not it could be watched.
        }
    }
}

/// <summary>One running run's memory limit: the one it started with, and the one in force now.</summary>
public sealed class RunAllowance : IDisposable
{
    private readonly RunAllowances _owner;
    private volatile RunMemoryLimit _current;

    internal RunAllowance(RunAllowances owner, ContainerId run, int leader, RunMemoryLimit normal, RunMemoryLimit ceiling, RunCgroup? cgroup)
    {
        _owner = owner;
        Run = run;
        Key = run.ToString();
        Leader = leader;
        Normal = normal;
        Ceiling = ceiling;
        Cgroup = cgroup;
        _current = normal;
    }

    public ContainerId Run { get; }

    /// <summary>The lease owner key this run holds leases under.</summary>
    public string Key { get; }

    /// <summary>The pid that leads the run's session.</summary>
    public int Leader { get; }

    /// <summary>The limit the run started with, and goes back to.</summary>
    public RunMemoryLimit Normal { get; }

    /// <summary>The hard limit it started with under rlimit.</summary>
    public RunMemoryLimit Ceiling { get; }

    public RunCgroup? Cgroup { get; }

    /// <summary>The limit in force: the heavy allowance from a grant until the lowering is done.</summary>
    public RunMemoryLimit Current => _current;

    /// <summary>Raised for the heavy lease and not yet back at <see cref="Normal"/>.</summary>
    public bool Raised { get; private set; }

    public bool Ended { get; private set; }

    internal void Raise(RunMemoryLimit heavy)
    {
        _current = heavy;
        Raised = true;
    }

    internal void Lower()
    {
        _current = Normal;
        Raised = false;
    }

    public void Dispose()
    {
        Ended = true;
        _owner.Remove(this);
    }
}
