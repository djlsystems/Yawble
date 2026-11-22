using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Yawble.Core.Launch;

/// <summary>One live process as the OS lists it. <c>StartedAt</c> tells a reused pid from the process the run had.</summary>
public readonly record struct ProcessEntry(int Pid, int ParentPid, int GroupId, long StartedAt);

/// <summary>The OS's process list and kill(2), behind a seam so the ordering is tested with a fake on any OS.</summary>
public interface IProcessTable
{
    /// <summary>Every live process (zombies left out: they are gone and cannot be signalled to any effect).</summary>
    IReadOnlyList<ProcessEntry> List();

    /// <summary>kill(pid, signal); quiet when it is already gone.</summary>
    void Signal(int pid, int signal);

    /// <summary>kill(-pgid, signal); quiet when the group is already gone.</summary>
    void SignalGroup(int pgid, int signal);

    /// <summary>This (the core's) process id.</summary>
    int Self { get; }

    /// <summary>This (the core's) process group: never signalled.</summary>
    int SelfGroup { get; }
}

public sealed class RunTreeOptions
{
    /// <summary>How often, at most, the process list is read while the run is live.</summary>
    public TimeSpan ObserveEvery { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>After SIGTERM: how long the tree has to end before what is left is SIGKILLed.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>After SIGKILL: how long to wait for the kernel to take them down before they count as survivors.</summary>
    public TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>What the end of the run did to the tree: how many processes it signalled, and which outlived SIGKILL.</summary>
public sealed record RunTreeResult(int Signalled, IReadOnlyList<int> Survivors);

/// <summary>
/// macOS (B122): every process a run started, and their end. The sandbox there has no PID namespace (Linux's bwrap takes
/// its whole namespace down), so a process the agent CLI started - its shell wrapper, a build under it - is reparented to
/// launchd when the CLI ends, and lives on in a group or session of its own. While the run is live this reads the
/// process list (throttled) and records every descendant of the sandbox's process by ppid, with its start time, so one
/// that has since been reparented, or left the group, is still known; members of a group led by a recorded process are
/// recorded too. At the end <see cref="Reap"/> SIGTERMs them all (and their groups), waits a grace, SIGKILLs what is
/// left and reports whatever still runs. Never the core itself, its own group, pid 1 or a pid reused by another process.
/// </summary>
public sealed class RunTree
{
    public const int SIGTERM = 15;
    public const int SIGKILL = 9;

    private readonly IProcessTable _table;
    private readonly int _root;
    private readonly RunTreeOptions _options;
    private readonly Dictionary<int, long> _tracked = [];
    private readonly Lock _lock = new();
    private long _lastObserved;
    private bool _rootChecked;
    private bool _disabled;

    public RunTree(IProcessTable table, int rootPid, RunTreeOptions? options = null)
    {
        _table = table;
        _root = rootPid;
        _options = options ?? new RunTreeOptions();
    }

    /// <summary>The processes recorded so far (pid and start time), the sandbox's own process among them.</summary>
    public IReadOnlyDictionary<int, long> Tracked
    {
        get { lock (_lock) return new Dictionary<int, long>(_tracked); }
    }

    /// <summary>While the run is live (each supervisor poll): reads the list at most every <see cref="RunTreeOptions.ObserveEvery"/>.</summary>
    public void Observe()
    {
        lock (_lock)
        {
            if (_lastObserved != 0 && Stopwatch.GetElapsedTime(_lastObserved) < _options.ObserveEvery) return;
            _lastObserved = Stopwatch.GetTimestamp();
        }
        Track();
    }

    /// <summary>Reads the list now and records every new descendant. Returns the list it read.</summary>
    public IReadOnlyList<ProcessEntry> Track()
    {
        IReadOnlyList<ProcessEntry> list;
        try { list = _table.List(); }
        catch (Exception e) when (e is IOException or DllNotFoundException or EntryPointNotFoundException) { return []; }
        lock (_lock)
        {
            if (_disabled) return list;
            var byPid = list.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
            if (!_rootChecked)
            {
                _rootChecked = true;
                // The sandbox's process is the core's own child, alive at the first read (just after the spawn). Anything
                // else - gone already, or another process's - is not a pid this tree may follow: a later process that got
                // the same pid is never taken for it.
                if (!byPid.TryGetValue(_root, out var root) || root.ParentPid != _table.Self || !Allowed(root)) { _disabled = true; return list; }
                _tracked[_root] = root.StartedAt;
            }
            var children = list.ToLookup(p => p.ParentPid);
            var frontier = new Queue<int>(_tracked.Keys.Where(pid => IsSame(byPid, pid)));
            var leaders = new HashSet<int>(frontier);
            while (frontier.Count > 0)
            {
                foreach (var child in children[frontier.Dequeue()])
                {
                    if (!Allowed(child) || _tracked.TryGetValue(child.Pid, out var s) && s == child.StartedAt) continue;
                    _tracked[child.Pid] = child.StartedAt;
                    leaders.Add(child.Pid);
                    frontier.Enqueue(child.Pid);
                }
            }
            // A process in a group a recorded process leads, whose parent ended before it was seen.
            foreach (var p in list)
            {
                if (p.GroupId == _table.SelfGroup || !leaders.Contains(p.GroupId) || !Allowed(p)) continue;
                if (_tracked.TryGetValue(p.Pid, out var s) && s == p.StartedAt) continue;
                _tracked[p.Pid] = p.StartedAt;
            }
            return list;
        }
    }

    /// <summary>
    /// The run is over: SIGTERM every recorded process still alive (and each group one of them leads), wait up to the
    /// grace, SIGKILL what is left, wait for it to go. Blocking; returns once the tree is gone or the wait is up.
    /// </summary>
    public RunTreeResult Reap()
    {
        var alive = Alive(Track());
        if (alive.Count == 0) return new RunTreeResult(0, []);
        var signalled = new HashSet<int>(alive.Select(p => p.Pid));
        SignalAll(alive, SIGTERM);

        var termAt = Stopwatch.GetTimestamp();
        while (alive.Count > 0 && Stopwatch.GetElapsedTime(termAt) < _options.Grace)
        {
            Thread.Sleep(_options.Poll);
            alive = Alive(Track());
            // One that started during the grace gets its SIGTERM too.
            var fresh = alive.Where(p => signalled.Add(p.Pid)).ToList();
            if (fresh.Count > 0) SignalAll(fresh, SIGTERM);
        }
        if (alive.Count == 0) return new RunTreeResult(signalled.Count, []);

        SignalAll(alive, SIGKILL);
        var killAt = Stopwatch.GetTimestamp();
        while (true)
        {
            Thread.Sleep(_options.Poll);
            alive = Alive(Track());
            foreach (var p in alive) signalled.Add(p.Pid);
            if (alive.Count == 0 || Stopwatch.GetElapsedTime(killAt) >= _options.KillWait) break;
            // Started between the list and the kill: it goes too.
            SignalAll(alive, SIGKILL);
        }
        return new RunTreeResult(signalled.Count, [.. alive.Select(p => p.Pid).Order()]);
    }

    private void SignalAll(IReadOnlyList<ProcessEntry> procs, int signal)
    {
        foreach (var p in procs)
        {
            // Its group first (a whole build under the shell), unless it is the core's own.
            if (p.GroupId == p.Pid && p.GroupId != _table.SelfGroup) _table.SignalGroup(p.GroupId, signal);
            _table.Signal(p.Pid, signal);
        }
    }

    private List<ProcessEntry> Alive(IReadOnlyList<ProcessEntry> list)
    {
        lock (_lock) return [.. list.Where(p => _tracked.TryGetValue(p.Pid, out var s) && s == p.StartedAt && Allowed(p))];
    }

    private bool IsSame(Dictionary<int, ProcessEntry> byPid, int pid) =>
        byPid.TryGetValue(pid, out var p) && _tracked.TryGetValue(pid, out var s) && s == p.StartedAt;

    private bool Allowed(ProcessEntry p) => p.Pid > 1 && p.Pid != _table.Self;
}

/// <summary>macOS's process list (libproc: proc_listallpids, proc_pidinfo PROC_PIDTBSDINFO) and kill(2).</summary>
internal sealed class MacProcessTable : IProcessTable
{
    public static MacProcessTable Instance { get; } = new();

    private const int PROC_PIDTBSDINFO = 3;
    // struct proc_bsdinfo (sys/proc_info.h): pbi_status at 4, pbi_pid at 12, pbi_ppid at 16, pbi_pgid at 100,
    // pbi_start_tvsec at 120, pbi_start_tvusec at 128; 136 bytes.
    private const int BsdInfoSize = 136;
    private const int SZOMB = 5;

    public int Self { get; } = Environment.ProcessId;
    public int SelfGroup { get; } = getpgrp();

    public IReadOnlyList<ProcessEntry> List()
    {
        var capacity = Math.Max(1024, proc_listallpids(null, 0) + 256);
        var pids = new int[capacity];
        var n = proc_listallpids(pids, capacity * sizeof(int));
        if (n < 0) throw new IOException($"proc_listallpids failed (errno {Marshal.GetLastPInvokeError()})");
        var list = new List<ProcessEntry>(n);
        var info = new byte[BsdInfoSize];
        for (var i = 0; i < Math.Min(n, pids.Length); i++)
        {
            if (pids[i] <= 0) continue;
            if (proc_pidinfo(pids[i], PROC_PIDTBSDINFO, 0, info, BsdInfoSize) != BsdInfoSize) continue;
            if (BitConverter.ToUInt32(info, 4) == SZOMB) continue;
            var started = (long)BitConverter.ToUInt64(info, 120) * 1_000_000 + (long)BitConverter.ToUInt64(info, 128);
            list.Add(new ProcessEntry((int)BitConverter.ToUInt32(info, 12), (int)BitConverter.ToUInt32(info, 16),
                (int)BitConverter.ToUInt32(info, 100), started));
        }
        return list;
    }

    public void Signal(int pid, int signal)
    {
        if (pid > 1) _ = kill(pid, signal);
    }

    public void SignalGroup(int pgid, int signal)
    {
        if (pgid > 1) _ = kill(-pgid, signal);
    }

    [DllImport("libproc", SetLastError = true)]
    private static extern int proc_listallpids(int[]? buffer, int bufferSize);

    [DllImport("libproc", SetLastError = true)]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[] buffer, int bufferSize);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc")]
    private static extern int getpgrp();
}
