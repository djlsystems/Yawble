using Harness.Contracts;

namespace Harness.Host;

/// <summary>A connected worker as the run limit and the run memory figures read it, and the worker connection's timings as configured.</summary>
public static class WorkerBounds
{
    /// <summary>What a worker said of itself, in the units its bound is computed in: whole CPUs, rounded up, and megabytes.</summary>
    public static WorkerBoundSource Of(WorkerInfo info) => new(
        info.Id,
        info.Cpus is > 0 and var cpus ? (int)Math.Ceiling(cpus) : null,
        info.MemoryLimitBytes is > 0 and var bytes ? bytes / (1024 * 1024) : null);

    /// <summary><c>Workers:GraceSeconds</c> (30), <c>Workers:KeepAliveSeconds</c> (10) and <c>Workers:HelloSeconds</c> (10).</summary>
    public static WorkerTimings Timings(IConfiguration configuration)
    {
        TimeSpan Seconds(string name, TimeSpan fallback) =>
            double.TryParse(configuration[name], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0
                ? TimeSpan.FromSeconds(s)
                : fallback;

        var defaults = WorkerTimings.Default;
        return new WorkerTimings(
            Seconds("Workers:GraceSeconds", defaults.Grace),
            Seconds("Workers:KeepAliveSeconds", defaults.KeepAlive),
            Seconds("Workers:HelloSeconds", defaults.Hello));
    }
}
