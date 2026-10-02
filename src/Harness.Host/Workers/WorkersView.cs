using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Capacity;

namespace Harness.Host;

/// <summary>
/// One worker as people see it: <c>GET /api/workers</c> and each capacity sample's <c>workers</c>. Its
/// build, when it connected, whether it is connected or dropped (and since when), what it measured -
/// CPUs, memory limit, memory in use, when, and what it could not measure - its own bound under the
/// default run limit, what a run asking it now would wait for, and the runs placed on it (a Manager
/// placed over its bound says so).
/// </summary>
public sealed record WorkerSample(
    string Id,
    string? Version,
    DateTimeOffset ConnectedSince,
    string State,
    DateTimeOffset? DroppedAt,
    WorkerCapacitySample Capacity,
    string? Holding,
    IReadOnlyList<RunHold> Runs);

/// <param name="Bound">How many runs it may hold at once under the default run limit; null when a set limit is the instance's total.</param>
/// <param name="NotMeasured">Every figure it did not measure; a null figure is never 0.</param>
public sealed record WorkerCapacitySample(
    double? Cpus,
    long? MemoryLimitBytes,
    int? Bound,
    DateTimeOffset? SampledAt,
    long? MemoryInUseBytes,
    double? MemoryPercent,
    IReadOnlyList<string> NotMeasured);

public static class WorkersView
{
    public const string Connected = "connected";
    public const string Dropped = "dropped";

    /// <summary>Every worker in <paramref name="pool"/>, in the order they connected.</summary>
    public static IReadOnlyList<WorkerSample> Of(WorkerPool pool, WipLedger wip, string version)
    {
        var running = wip.View().Running;
        return [.. pool.Entries().Select(entry =>
        {
            var id = entry.Info.Id;
            var figures = entry.Gate.Figures;
            var measured = figures is not null && entry.Gate.MeasuredAt is not null;
            var inUse = measured ? figures!.MemoryInUseBytes : null;
            var limit = measured ? figures!.MemoryLimitBytes ?? entry.Info.MemoryLimitBytes : entry.Info.MemoryLimitBytes;

            return new WorkerSample(
                id.Value,
                entry.Info.Version ?? version,
                entry.Info.ConnectedSince,
                entry.DroppedAt is null ? Connected : Dropped,
                entry.DroppedAt,
                new WorkerCapacitySample(
                    measured && figures!.CpuLimit is { } cpus ? cpus : entry.Info.Cpus,
                    limit,
                    pool.Bound(id),
                    entry.Gate.MeasuredAt,
                    inUse,
                    inUse is { } used && limit is > 0 and var bytes ? used * 100.0 / bytes : null,
                    measured ? figures!.NotMeasured : CgroupReader.AllFigures),
                entry.Gate.Reason(),
                [.. running.Where(hold => wip.PlacedOn(new ContainerId(hold.Team, hold.Member)) == id)
                    .Select(hold => new RunHold(hold.Team, hold.Member, hold.Since, hold.Reason))]);
        })];
    }
}
