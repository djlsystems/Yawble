using Harness.Contracts;

namespace Harness.Host.Capacity;

/// <summary>
/// A worker's cgroup reading as the run protocol carries it, and back. Field for field both ways:
/// <c>RunProtocolTests</c> pins that nothing is lost.
/// </summary>
public static class WorkerCapacity
{
    /// <summary>What a worker with no cgroup reader says: nothing measured.</summary>
    public static CgroupFigures NotMeasured { get; } = new(
        null, null, false, null, null, null, null, false, null, null, null, null, null, null, null, null, false, []);

    public static CapacityFigures ToFigures(CgroupFigures figures) => new(
        figures.Version,
        figures.CpuLimit,
        figures.CpuUnlimited,
        figures.CpuUsageUsec,
        figures.CpuThrottledPeriods,
        figures.CpuThrottledUsec,
        figures.MemoryLimitBytes,
        figures.MemoryUnlimited,
        figures.MemoryCurrentBytes,
        figures.MemoryAnonBytes,
        figures.MemoryFileBytes,
        figures.MemoryShmemBytes,
        ToReading(figures.CpuPressure),
        ToReading(figures.MemoryPressure),
        figures.PidsCurrent,
        figures.PidsLimit,
        figures.PidsUnlimited,
        figures.NotMeasured);

    public static CgroupFigures ToCgroup(CapacityFigures figures) => new(
        figures.Version,
        figures.CpuLimit,
        figures.CpuUnlimited,
        figures.CpuUsageUsec,
        figures.CpuThrottledPeriods,
        figures.CpuThrottledUsec,
        figures.MemoryLimitBytes,
        figures.MemoryUnlimited,
        figures.MemoryCurrentBytes,
        figures.MemoryAnonBytes,
        figures.MemoryFileBytes,
        figures.MemoryShmemBytes,
        ToPressure(figures.CpuPressure),
        ToPressure(figures.MemoryPressure),
        figures.PidsCurrent,
        figures.PidsLimit,
        figures.PidsUnlimited,
        figures.NotMeasured);

    private static PressureReading? ToReading(PressureFigures? figures) =>
        figures is null ? null : new(ToLines(figures.Some), figures.Full is { } full ? ToLines(full) : null);

    private static PressureLines ToLines(PressureLine line) => new(line.Avg10, line.Avg60, line.Avg300, line.TotalUsec);

    private static PressureFigures? ToPressure(PressureReading? reading) =>
        reading is null ? null : new(ToLine(reading.Some), reading.Full is { } full ? ToLine(full) : null);

    private static PressureLine ToLine(PressureLines lines) => new(lines.Avg10, lines.Avg60, lines.Avg300, lines.TotalUsec);
}
