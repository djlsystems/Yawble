using Harness.Containers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Host.Capacity;

/// <summary>
/// MEASURES THE INSTANCE EVERY FEW SECONDS into a short in-memory history (about ten minutes), feeds
/// the admission gate, and pushes each sample to the people watching.
///
/// <para>
/// One sample is the container's cgroup (<see cref="CgroupReader"/>), every registered run's process
/// group (<see cref="ProcessGroupReader"/> over <see cref="RunProcessGroups"/>), the run limit's view
/// (<see cref="WipLedger"/>) and the <c>heavy</c> lease (<see cref="IHeavyLeaseView"/>). Rates (CPUs in
/// use, a run's CPU share) are the difference between two measurements and are null on the first.
/// The history lives in memory only; a restart starts it again.
/// </para>
/// </summary>
public sealed class CapacitySampler(
    CgroupReader cgroup,
    ProcessGroupReader processes,
    RunProcessGroups groups,
    WipLedger wip,
    HeadroomGate gate,
    IHeavyLeaseView lease,
    Func<int> memoryPercent,
    Func<int> pressurePercent,
    Func<CapacitySample, Task>? push = null,
    ILogger<CapacitySampler>? logger = null,
    TimeProvider? clock = null,
    TimeSpan? interval = null,
    int ticksPerSecond = 100) : BackgroundService
{
    /// <summary>How often a sample is taken.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    /// <summary>How far back the history reaches.</summary>
    public static readonly TimeSpan HistorySpan = TimeSpan.FromMinutes(10);

    /// <summary>How many runs each top list names.</summary>
    public const int TopRuns = 5;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Queue<CapacitySample> _history = new();
    private Previous? _previous;

    public TimeSpan Interval { get; } = interval ?? DefaultInterval;

    /// <summary>The newest sample, or null before the first.</summary>
    public CapacitySample? Latest
    {
        get
        {
            lock (_gate) return _history.LastOrDefault();
        }
    }

    /// <summary>Oldest first, about <see cref="HistorySpan"/> of it.</summary>
    public IReadOnlyList<CapacitySample> History()
    {
        lock (_gate) return _history.ToArray();
    }

    /// <summary>Takes one sample now, keeps it, hands it to the gate and the ledger, and returns it.</summary>
    public CapacitySample Sample()
    {
        var at = _clock.GetUtcNow();
        var figures = cgroup.Read();
        var registered = groups.Snapshot();
        var runFigures = processes.Read(registered.Keys.ToArray());
        var view = wip.View();

        // The gate answers from this measurement from here on; the sample says what it answers.
        gate.Update(figures, at);
        var holding = gate.Reason();

        CapacitySample sample;
        lock (_gate)
        {
            var previous = _previous;
            var seconds = previous is null ? 0 : (at - previous.At).TotalSeconds;

            double? cpusInUse = previous is not null && seconds > 0
                && figures.CpuUsageUsec is { } usage && previous.CpuUsageUsec is { } before && usage >= before
                    ? (usage - before) / 1e6 / seconds
                    : null;

            var runs = new List<RunFigures>();
            var ticks = new Dictionary<int, long>();
            foreach (var (group, run) in registered)
            {
                if (runFigures.GetValueOrDefault(group) is not { } measured) continue;

                ticks[group] = measured.CpuTicks;
                double? cpuPercent = previous is not null && seconds > 0
                    && previous.RunTicks.TryGetValue(group, out var was) && measured.CpuTicks >= was
                        ? (measured.CpuTicks - was) / (double)ticksPerSecond / seconds * 100
                        : null;

                runs.Add(new RunFigures(run.Team, run.Name, measured.Processes, measured.ResidentBytes, cpuPercent));
            }

            _previous = new Previous(at, figures.CpuUsageUsec, ticks);

            sample = new CapacitySample(
                at,
                figures.Version,
                new CpuSample(
                    figures.CpuLimit, figures.CpuUnlimited, figures.CpuUsageUsec, cpusInUse,
                    cpusInUse is { } inUse && figures.CpuLimit is > 0 and var cpus ? inUse / cpus * 100 : null,
                    figures.CpuThrottledPeriods, figures.CpuThrottledUsec, figures.CpuPressure),
                new MemorySample(
                    figures.MemoryLimitBytes, figures.MemoryUnlimited, figures.MemoryCurrentBytes,
                    figures.MemoryAnonBytes, figures.MemoryFileBytes, figures.MemoryShmemBytes,
                    figures.MemoryInUseBytes,
                    figures.MemoryInUseBytes is { } used && figures.MemoryLimitBytes is > 0 and var limit
                        ? used * 100.0 / limit
                        : null,
                    figures.MemoryPressure),
                new PidsSample(figures.PidsCurrent, figures.PidsLimit, figures.PidsUnlimited),
                figures.NotMeasured,
                new RunsSample(
                    view.Max,
                    view.Max > 0 ? 1 : 0,
                    view.Running.Count,
                    view.Waiting.Count,
                    view.Running.Select(hold => new RunHold(hold.Team, hold.Member, hold.Since, null)).ToArray(),
                    view.Waiting.Select(hold => new RunHold(hold.Team, hold.Member, hold.Since, hold.Reason)).ToArray()),
                new AdmissionSample(memoryPercent(), pressurePercent(), holding),
                runs.OrderByDescending(run => run.ResidentBytes).Take(TopRuns).ToArray(),
                runs.Where(run => run.CpuPercent is not null)
                    .OrderByDescending(run => run.CpuPercent).Take(TopRuns).ToArray(),
                lease.Read());

            _history.Enqueue(sample);
            while (_history.Count > 0 && at - _history.Peek().At > HistorySpan) _history.Dequeue();
        }

        // Outside this lock: a waiter held for headroom asks again when it has cleared.
        wip.HeadroomChanged();

        return sample;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _clock);

        do
        {
            try
            {
                var sample = Sample();
                if (push is not null) await push(sample);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A missed sample is a gap in the history; it must not stop the loop.
                logger?.LogWarning(exception, "Capacity sample failed; trying again at the next one.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private sealed record Previous(DateTimeOffset At, long? CpuUsageUsec, IReadOnlyDictionary<int, long> RunTicks);
}

/// <summary>
/// ONE MEASUREMENT OF THE INSTANCE, as <c>GET /api/capacity</c> answers it and <c>capacityChanged</c>
/// pushes it. Every nullable figure is NOT MEASURED when null and is named in <c>NotMeasured</c>;
/// rates are null on the first sample.
/// </summary>
public sealed record CapacitySample(
    DateTimeOffset At,
    string? Cgroup,
    CpuSample Cpu,
    MemorySample Memory,
    PidsSample Pids,
    IReadOnlyList<string> NotMeasured,
    RunsSample Runs,
    AdmissionSample Admission,
    IReadOnlyList<RunFigures> TopByMemory,
    IReadOnlyList<RunFigures> TopByCpu,
    HeavyLeaseSnapshot? HeavyLease);

/// <param name="CpusInUse">CPUs' worth of time used since the last sample.</param>
/// <param name="PercentOfLimit">That against <c>LimitCpus</c>; null when there is no limit.</param>
public sealed record CpuSample(
    double? LimitCpus, bool Unlimited, long? UsageUsec, double? CpusInUse, double? PercentOfLimit,
    long? ThrottledPeriods, long? ThrottledUsec, PressureFigures? Pressure);

/// <param name="InUseBytes">Anonymous + shmem: what admission compares with the limit.</param>
public sealed record MemorySample(
    long? LimitBytes, bool Unlimited, long? CurrentBytes, long? AnonBytes, long? FileBytes, long? ShmemBytes,
    long? InUseBytes, double? PercentOfLimit, PressureFigures? Pressure);

public sealed record PidsSample(long? Current, long? Limit, bool Unlimited);

/// <param name="Limit"><c>wip.maxRunning</c> in force; 0 is unlimited.</param>
/// <param name="ManagerReserved">Slots above the limit only a Manager may use (1, or 0 when unlimited).</param>
public sealed record RunsSample(
    int Limit, int ManagerReserved, int RunningCount, int WaitingCount,
    IReadOnlyList<RunHold> Running, IReadOnlyList<RunHold> Waiting);

/// <param name="Reason">What a waiting run waits for ("waiting for a slot", "waiting for memory: …").</param>
public sealed record RunHold(string Team, string Member, DateTimeOffset Since, string? Reason);

/// <param name="Holding">Null when a run asking now would be admitted on headroom; otherwise its reason.</param>
public sealed record AdmissionSample(int MemoryPercent, int MemoryPressurePercent, string? Holding);

/// <param name="CpuPercent">Percent of one CPU since the last sample; null on a run's first.</param>
public sealed record RunFigures(string Team, string Member, int Processes, long ResidentBytes, double? CpuPercent);
