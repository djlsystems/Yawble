using System.Collections.Concurrent;
using System.Globalization;
using Harness.Contracts;

namespace Harness.Host.Capacity;

/// <summary>
/// One run's figures, summed over its SESSION: resident memory from <c>/proc/&lt;pid&gt;/statm</c>,
/// CPU time and thread count from <c>/proc/&lt;pid&gt;/stat</c>, read as the Host's own user.
///
/// <para>
/// Every run starts in its own session (<c>setsid</c> in <see cref="ChildProcess"/>), so its session
/// id (field 6) is its leader's pid - the id a run is registered under - and every process it starts
/// carries that session unless it calls <c>setsid</c> itself. Summed by session rather than by process
/// group, so a process that moved itself into another group of the session (<c>setpgid</c>, a
/// <c>set -m</c> background job) is counted against its run too. A session with no process the Host
/// can read is NOT MEASURED (null), never zero.
/// </para>
/// <para>
/// Processes are the session's members; threads are summed from each member's own count. The cgroup's
/// <c>pids.current</c> counts tasks (both), so neither figure here is that one. A member whose thread
/// count cannot be read leaves the run's threads not measured (null), never a part-sum.
/// </para>
/// </summary>
/// <param name="procRoot">The proc mount; tests hand a fixture tree.</param>
/// <param name="pageSize">Bytes per page, which statm counts in.</param>
public sealed class ProcessGroupReader(string procRoot = "/proc", long? pageSize = null)
{
    private readonly long _pageSize = pageSize ?? Environment.SystemPageSize;

    /// <summary>
    /// Each requested run's sum over every process whose session is that id, or null for one no
    /// readable process belongs to. The ids are the leaders' pids <see cref="RunProcessGroups"/> holds.
    /// </summary>
    public IReadOnlyDictionary<int, ProcessGroupFigures?> Read(IReadOnlyCollection<int> groups)
    {
        var sums = new Dictionary<int, ProcessGroupFigures?>();
        foreach (var group in groups) sums[group] = null;
        if (groups.Count == 0) return sums;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories(procRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return sums;
        }

        foreach (var entry in entries)
        {
            if (!int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
            if (Stat(entry) is not { } stat || !sums.ContainsKey(stat.Session)) continue;
            if (ResidentPages(entry) is not { } pages) continue;

            var before = sums[stat.Session];
            sums[stat.Session] = new ProcessGroupFigures(
                (before?.Processes ?? 0) + 1,
                (before?.ResidentBytes ?? 0) + pages * _pageSize,
                (before?.CpuTicks ?? 0) + stat.Ticks,
                before is null ? stat.Threads : before.Threads + stat.Threads);
        }

        return sums;
    }

    /// <summary>
    /// The session (field 6), utime + stime (fields 14 and 15) and num_threads (field 20, null when it
    /// cannot be read) of a <c>stat</c> line. The command name (field 2) is in parentheses and may hold
    /// spaces or parentheses itself, so fields are counted from the LAST closing parenthesis.
    /// </summary>
    private static (int Session, long Ticks, int? Threads)? Stat(string entry)
    {
        var text = ReadText(Path.Combine(entry, "stat"));
        if (text is null) return null;

        var close = text.LastIndexOf(')');
        if (close < 0) return null;

        // After ")": state(3) ppid(4) pgrp(5) session(6) tty(7) tpgid(8) flags(9) minflt(10)
        // cminflt(11) majflt(12) cmajflt(13) utime(14) stime(15) cutime(16) cstime(17) priority(18)
        // nice(19) num_threads(20).
        var fields = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 13) return null;

        if (!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var session)
            || !long.TryParse(fields[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out var utime)
            || !long.TryParse(fields[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stime))
        {
            return null;
        }

        int? threads = fields.Length > 17
            && int.TryParse(fields[17], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0
                ? count
                : null;

        return (session, utime + stime, threads);
    }

    /// <summary>statm's second field: resident pages.</summary>
    private static long? ResidentPages(string entry)
    {
        var fields = ReadText(Path.Combine(entry, "statm"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return fields is { Length: >= 2 }
            && long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages)
                ? pages
                : null;
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A process that exited between the listing and the read, or one the Host may not read.
            return null;
        }
    }
}

/// <summary>
/// A run's session (every process group of it): how many processes, their resident memory, their CPU time in clock ticks,
/// and how many threads they run between them (null when not measured).
/// </summary>
public sealed record ProcessGroupFigures(int Processes, long ResidentBytes, long CpuTicks, int? Threads);

/// <summary>
/// Which run leads which session - keyed by the leader's pid, which is the session id and its own
/// group's id - so the sampler can name a session's team and member.
/// <see cref="ChildProcess.RunAsync"/> registers the child as it starts and removes it when the run
/// ends, however it ends.
/// </summary>
public sealed class RunProcessGroups
{
    /// <summary>The Host's one registry. Tests make their own.</summary>
    public static RunProcessGroups Shared { get; } = new();

    private readonly ConcurrentDictionary<int, ContainerId> _groups = new();

    public IDisposable Register(int group, ContainerId run)
    {
        _groups[group] = run;
        return new Removal(this, group);
    }

    public IReadOnlyDictionary<int, ContainerId> Snapshot() => new Dictionary<int, ContainerId>(_groups);

    private sealed class Removal(RunProcessGroups owner, int group) : IDisposable
    {
        public void Dispose() => owner._groups.TryRemove(group, out _);
    }
}
