using System.Collections.Concurrent;
using System.Globalization;
using Harness.Contracts;

namespace Harness.Host.Capacity;

/// <summary>
/// One run's figures, summed over its process group: resident memory from <c>/proc/&lt;pid&gt;/statm</c>
/// and CPU time from <c>/proc/&lt;pid&gt;/stat</c>, read as the Host's own user.
///
/// <para>
/// Every run starts in its own session (<c>setsid</c> in <see cref="ChildProcess"/>), so its group id
/// is its leader's pid and every process it starts carries that group unless it asks for a session
/// of its own. A group with no process the Host can read is NOT MEASURED (null), never zero.
/// </para>
/// </summary>
/// <param name="procRoot">The proc mount; tests hand a fixture tree.</param>
/// <param name="pageSize">Bytes per page, which statm counts in.</param>
public sealed class ProcessGroupReader(string procRoot = "/proc", long? pageSize = null)
{
    private readonly long _pageSize = pageSize ?? Environment.SystemPageSize;

    /// <summary>Each requested group's sum, or null for a group no readable process belongs to.</summary>
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
            if (Stat(entry) is not { } stat || !sums.ContainsKey(stat.Group)) continue;
            if (ResidentPages(entry) is not { } pages) continue;

            var before = sums[stat.Group];
            sums[stat.Group] = new ProcessGroupFigures(
                (before?.Processes ?? 0) + 1,
                (before?.ResidentBytes ?? 0) + pages * _pageSize,
                (before?.CpuTicks ?? 0) + stat.Ticks);
        }

        return sums;
    }

    /// <summary>
    /// The group (field 5) and utime + stime (fields 14 and 15) of a <c>stat</c> line. The command name
    /// (field 2) is in parentheses and may hold spaces or parentheses itself, so fields are counted
    /// from the LAST closing parenthesis.
    /// </summary>
    private static (int Group, long Ticks)? Stat(string entry)
    {
        var text = ReadText(Path.Combine(entry, "stat"));
        if (text is null) return null;

        var close = text.LastIndexOf(')');
        if (close < 0) return null;

        // After ")": state(3) ppid(4) pgrp(5) session(6) tty(7) tpgid(8) flags(9) minflt(10)
        // cminflt(11) majflt(12) cmajflt(13) utime(14) stime(15).
        var fields = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 13) return null;

        if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var group)
            || !long.TryParse(fields[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out var utime)
            || !long.TryParse(fields[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stime))
        {
            return null;
        }

        return (group, utime + stime);
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

/// <summary>A run's process group: how many processes, their resident memory, and their CPU time in clock ticks.</summary>
public sealed record ProcessGroupFigures(int Processes, long ResidentBytes, long CpuTicks);

/// <summary>
/// Which run leads which process group, so the sampler can name a group's team and member.
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
