using Harness.Host.Auth;

namespace Harness.Host.Capacity;

/// <summary>The instance's measured capacity, for people: one route, and the same sample pushed live.</summary>
public static class CapacityEndpoints
{
    /// <summary>The live connection's event carrying each new sample, sent to people only.</summary>
    public const string PushName = "capacityChanged";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/capacity", (CapacitySampler sampler) => Results.Ok(new
            {
                intervalSeconds = sampler.Interval.TotalSeconds,
                latest = sampler.Latest,
                history = sampler.History(),
            }))
            .HumansOnly()
            .WithTags("Admission")
            .WithSummary("The container's measured limits, usage and pressure, its runs, and the last ten minutes of it")
            .WithDescription(
                "Read from the container's own cgroup (v2, or v1's equivalents) and each run's process "
                + "group in /proc, every `intervalSeconds`, kept in memory for about ten minutes. "
                + "`latest` is the newest sample (null before the first), `history` every sample oldest "
                + "first. The same sample is pushed as `capacityChanged` on the live connection.\n\n"
                + "A null figure is NOT MEASURED and is named in `notMeasured`; it is never 0. A limit "
                + "the cgroup leaves unbounded is `unlimited: true` with a null value. Rates (`cpusInUse`, "
                + "a run's `cpuPercent`) are null on the first sample.\n\n"
                + "`runs` is the run limit's view: `limit` (`wip.maxRunning`, 0 unlimited), "
                + "`managerReserved`, who is running and who is waiting with the `reason` each waits for. "
                + "`admission.holding` is what a run asking now would wait for on headroom, or null. "
                + "`topByMemory` and `topByCpu` name each run's team and member. `heavyLease` is the "
                + "`heavy` lease's holders and queue, or null when no lease is available.");
    }
}
