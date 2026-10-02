using System.Globalization;

namespace Harness.Host.Capacity;

/// <summary>
/// The container's own limits, usage and pressure, read from its cgroup files and nothing else.
///
/// <para>
/// <b>FROM INSIDE THE CONTAINER ONLY.</b> Every container engine mounts the container's own cgroup
/// at <c>/sys/fs/cgroup</c>, so the same files answer under any engine, rootful or rootless, and the
/// Host never asks the engine. cgroup v2 is recognised by <c>cgroup.controllers</c> at the root;
/// otherwise the v1 controllers (<c>memory/</c>, <c>cpu/</c> or <c>cpu,cpuacct/</c>,
/// <c>cpuacct/</c>, <c>pids/</c>) are read where they are mounted.
/// </para>
///
/// <para>
/// <b>NOTHING IS ESTIMATED.</b> A figure whose file is missing, unreadable or unparseable is null and
/// named in <see cref="CgroupFigures.NotMeasured"/>; it is never 0 and never derived from another
/// figure. A limit the cgroup says is unbounded (<c>max</c>, v1's <c>-1</c> or its page-rounded
/// maximum) is measured: it is <see cref="CgroupFigures.MemoryUnlimited"/> or
/// <see cref="CgroupFigures.CpuUnlimited"/> with no value.
/// </para>
/// </summary>
/// <param name="root">The cgroup mount; tests hand a fixture tree.</param>
public sealed class CgroupReader(string root = CgroupReader.DefaultRoot)
{
    public const string DefaultRoot = "/sys/fs/cgroup";

    /// <summary>A v1 limit at or above this is the kernel's "no limit" (LONG_MAX rounded to a page).</summary>
    private const long V1Unlimited = 1L << 62;

    public string Root { get; } = root;

    public CgroupFigures Read()
    {
        var notMeasured = new List<string>();

        if (File.Exists(Path.Combine(Root, "cgroup.controllers"))) return ReadV2(notMeasured);
        if (Directory.Exists(Path.Combine(Root, "memory")) || V1Dir("cpu.cfs_quota_us") is not null) return ReadV1(notMeasured);

        notMeasured.AddRange(AllFigures);
        return new CgroupFigures(null, null, false, null, null, null, null, false, null, null, null, null, null, null, null, null, false, notMeasured);
    }

    /// <summary>Every figure's name, as <see cref="CgroupFigures.NotMeasured"/> spells it.</summary>
    public static readonly IReadOnlyList<string> AllFigures =
    [
        "cpu.limit", "cpu.usage", "cpu.throttled", "memory.limit", "memory.current", "memory.anon",
        "memory.file", "memory.shmem", "cpu.pressure", "memory.pressure", "pids.current", "pids.limit",
    ];

    private CgroupFigures ReadV2(List<string> notMeasured)
    {
        // cpu.max: "<quota> <period>", quota "max" for none.
        double? cpus = null;
        var cpuUnlimited = false;
        if (Text("cpu.max") is { } cpuMax && cpuMax.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var quota, var period, ..])
        {
            if (quota == "max") cpuUnlimited = true;
            else if (Long(quota) is > 0 and var q && Long(period) is > 0 and var p) cpus = (double)q / p;
        }
        if (cpus is null && !cpuUnlimited) notMeasured.Add("cpu.limit");

        var cpuStat = Keyed("cpu.stat");
        var usage = Need(cpuStat, "usage_usec", "cpu.usage", notMeasured);
        var throttledUsec = cpuStat?.GetValueOrDefault("throttled_usec");
        var nrThrottled = cpuStat?.GetValueOrDefault("nr_throttled");
        if (throttledUsec is null || nrThrottled is null) notMeasured.Add("cpu.throttled");

        long? memoryLimit = null;
        var memoryUnlimited = false;
        if (Text("memory.max") is { } memMax)
        {
            if (memMax == "max") memoryUnlimited = true;
            else memoryLimit = Long(memMax);
        }
        if (memoryLimit is null && !memoryUnlimited) notMeasured.Add("memory.limit");

        var current = Long(Text("memory.current"));
        if (current is null) notMeasured.Add("memory.current");

        var stat = Keyed("memory.stat");
        var anon = Need(stat, "anon", "memory.anon", notMeasured);
        var file = Need(stat, "file", "memory.file", notMeasured);
        var shmem = Need(stat, "shmem", "memory.shmem", notMeasured);

        var cpuPressure = Pressure("cpu.pressure");
        if (cpuPressure is null) notMeasured.Add("cpu.pressure");
        var memoryPressure = Pressure("memory.pressure");
        if (memoryPressure is null) notMeasured.Add("memory.pressure");

        var (pids, pidsMax, pidsUnlimited) = Pids("pids.current", "pids.max", notMeasured);

        return new CgroupFigures(
            "v2", cpus, cpuUnlimited, usage, nrThrottled, throttledUsec,
            memoryLimit, memoryUnlimited, current, anon, file, shmem,
            cpuPressure, memoryPressure, pids, pidsMax, pidsUnlimited, notMeasured);
    }

    private CgroupFigures ReadV1(List<string> notMeasured)
    {
        // cpu.cfs_quota_us is -1 for none; cpuacct.usage is nanoseconds; cpu.stat's throttled_time too.
        double? cpus = null;
        var cpuUnlimited = false;
        var cpuDir = V1Dir("cpu.cfs_quota_us");
        if (cpuDir is not null && Long(Text(Path.Combine(cpuDir, "cpu.cfs_quota_us"))) is { } quota)
        {
            if (quota < 0) cpuUnlimited = true;
            else if (quota > 0 && Long(Text(Path.Combine(cpuDir, "cpu.cfs_period_us"))) is > 0 and var period)
            {
                cpus = (double)quota / period;
            }
        }
        if (cpus is null && !cpuUnlimited) notMeasured.Add("cpu.limit");

        var acctDir = V1Dir("cpuacct.usage");
        long? usage = acctDir is null ? null : Long(Text(Path.Combine(acctDir, "cpuacct.usage"))) / 1000;
        if (usage is null) notMeasured.Add("cpu.usage");

        var cpuStat = cpuDir is null ? null : Keyed(Path.Combine(cpuDir, "cpu.stat"));
        var nrThrottled = cpuStat?.GetValueOrDefault("nr_throttled");
        var throttledUsec = cpuStat?.GetValueOrDefault("throttled_time") / 1000;
        if (nrThrottled is null || throttledUsec is null) notMeasured.Add("cpu.throttled");

        long? memoryLimit = null;
        var memoryUnlimited = false;
        if (Long(Text(Path.Combine("memory", "memory.limit_in_bytes"))) is { } limit)
        {
            if (limit >= V1Unlimited) memoryUnlimited = true;
            else memoryLimit = limit;
        }
        if (memoryLimit is null && !memoryUnlimited) notMeasured.Add("memory.limit");

        var current = Long(Text(Path.Combine("memory", "memory.usage_in_bytes")));
        if (current is null) notMeasured.Add("memory.current");

        // v1 names the hierarchy's totals total_*; a leaf without children has only the bare names.
        var stat = Keyed(Path.Combine("memory", "memory.stat"));
        var anon = Either(stat, "total_rss", "rss", "memory.anon", notMeasured);
        var file = Either(stat, "total_cache", "cache", "memory.file", notMeasured);
        var shmem = Either(stat, "total_shmem", "shmem", "memory.shmem", notMeasured);

        // v1 has no pressure files for a cgroup. /proc/pressure is the whole machine's, not this
        // container's, so it is not read: pressure is not measured on v1.
        notMeasured.Add("cpu.pressure");
        notMeasured.Add("memory.pressure");

        var (pids, pidsMax, pidsUnlimited) = Pids(
            Path.Combine("pids", "pids.current"), Path.Combine("pids", "pids.max"), notMeasured);

        return new CgroupFigures(
            "v1", cpus, cpuUnlimited, usage, nrThrottled, throttledUsec,
            memoryLimit, memoryUnlimited, current, anon, file, shmem,
            null, null, pids, pidsMax, pidsUnlimited, notMeasured);
    }

    private (long? Current, long? Max, bool Unlimited) Pids(string currentFile, string maxFile, List<string> notMeasured)
    {
        var current = Long(Text(currentFile));
        if (current is null) notMeasured.Add("pids.current");

        long? max = null;
        var unlimited = false;
        if (Text(maxFile) is { } text)
        {
            if (text == "max") unlimited = true;
            else max = Long(text);
        }
        if (max is null && !unlimited) notMeasured.Add("pids.limit");

        return (current, max, unlimited);
    }

    /// <summary>The v1 directory holding <paramref name="file"/>: <c>cpu,cpuacct</c>, <c>cpu</c> or <c>cpuacct</c>.</summary>
    private string? V1Dir(string file) =>
        new[] { "cpu,cpuacct", "cpu", "cpuacct", "cpuacct,cpu" }
            .FirstOrDefault(dir => File.Exists(Path.Combine(Root, dir, file)));

    /// <summary>A PSI file: <c>some avg10=.. avg60=.. avg300=.. total=..</c> and, for memory, a <c>full</c> line.</summary>
    private PressureFigures? Pressure(string file)
    {
        if (Text(file) is not { } text) return null;

        PressureLine? some = null, full = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var values = parts.Skip(1)
                .Select(part => part.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

            if (!values.TryGetValue("avg10", out var a10) || !values.TryGetValue("avg60", out var a60)
                || !values.TryGetValue("avg300", out var a300) || !values.TryGetValue("total", out var total)
                || Double(a10) is not { } avg10 || Double(a60) is not { } avg60 || Double(a300) is not { } avg300
                || Long(total) is not { } totalUsec)
            {
                continue;
            }

            var parsed = new PressureLine(avg10, avg60, avg300, totalUsec);
            if (parts[0] == "some") some = parsed;
            else if (parts[0] == "full") full = parsed;
        }

        return some is null ? null : new PressureFigures(some, full);
    }

    private static long? Need(Dictionary<string, long>? keyed, string key, string figure, List<string> notMeasured)
    {
        if (keyed is not null && keyed.TryGetValue(key, out var value)) return value;
        notMeasured.Add(figure);
        return null;
    }

    private static long? Either(Dictionary<string, long>? keyed, string total, string bare, string figure, List<string> notMeasured)
    {
        if (keyed is not null && (keyed.TryGetValue(total, out var value) || keyed.TryGetValue(bare, out value))) return value;
        notMeasured.Add(figure);
        return null;
    }

    /// <summary>A flat-keyed file (<c>name value</c> per line), or null when it cannot be read.</summary>
    private Dictionary<string, long>? Keyed(string file)
    {
        if (Text(file) is not { } text) return null;

        var keyed = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && Long(parts[1]) is { } value) keyed[parts[0]] = value;
        }

        return keyed;
    }

    private string? Text(string file)
    {
        try
        {
            var path = Path.IsPathRooted(file) ? file : Path.Combine(Root, file);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? Long(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? Double(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>One PSI line: the share of wall time (percent) some or all work stalled, and the total stall in µs.</summary>
public sealed record PressureLine(double Avg10, double Avg60, double Avg300, long TotalUsec);

/// <summary>A PSI file. <c>Full</c> is null where the file has no such line (cpu.pressure on older kernels).</summary>
public sealed record PressureFigures(PressureLine Some, PressureLine? Full);

/// <summary>
/// What the container's cgroup said, at one moment. Null is NOT MEASURED, and the figure's name is
/// in <see cref="NotMeasured"/>; an unbounded limit is <c>…Unlimited</c> with a null value.
/// <see cref="Version"/> is <c>v2</c>, <c>v1</c>, or null when no cgroup could be read at all.
/// CPU times are microseconds on both versions.
/// </summary>
public sealed record CgroupFigures(
    string? Version,
    double? CpuLimit,
    bool CpuUnlimited,
    long? CpuUsageUsec,
    long? CpuThrottledPeriods,
    long? CpuThrottledUsec,
    long? MemoryLimitBytes,
    bool MemoryUnlimited,
    long? MemoryCurrentBytes,
    long? MemoryAnonBytes,
    long? MemoryFileBytes,
    long? MemoryShmemBytes,
    PressureFigures? CpuPressure,
    PressureFigures? MemoryPressure,
    long? PidsCurrent,
    long? PidsLimit,
    bool PidsUnlimited,
    IReadOnlyList<string> NotMeasured)
{
    /// <summary>
    /// Memory the container cannot give back by dropping cache: anonymous memory plus shmem (tmpfs
    /// files among it). Null when either is not measured - never <c>memory.current</c>, which counts
    /// file cache the kernel reclaims on demand.
    /// </summary>
    public long? MemoryInUseBytes => MemoryAnonBytes + MemoryShmemBytes;
}
