using Harness.Containers;
using Harness.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Host.Capacity;

/// <summary>
/// MEASURES THE INSTANCE EVERY FEW SECONDS into a short in-memory history (about ten minutes), feeds
/// the admission gate, and pushes each sample to the people watching.
///
/// <para>
/// One sample is the worker's own: its cgroup and every registered run's process group, measured on
/// the worker when it is sent <see cref="SampleCapacity"/> and reported as
/// <see cref="RunMeasured"/> and <see cref="WorkerCapacitySampled"/>. To that this adds the run
/// limit's view (<see cref="WipLedger"/>) and the <c>heavy</c> lease (<see cref="IHeavyLeaseView"/>).
/// Rates (CPUs in use, a run's CPU share) are the difference between two measurements and are null
/// on the first. The history lives in memory only; a restart starts it again.
/// </para>
/// </summary>
public sealed class CapacitySampler : BackgroundService, IRunWorkerClient
{
    private readonly Func<IReadOnlyList<(IRunWorker Worker, HeadroomGate Gate)>> workers;
    private readonly WipLedger wip;
    private readonly IHeavyLeaseView lease;
    private readonly Func<int> memoryPercent;
    private readonly Func<int> pressurePercent;
    private readonly Func<CapacitySample, Task>? push;
    private readonly ILogger<CapacitySampler>? logger;
    private readonly int ticksPerSecond;

    /// <summary>
    /// The Host's: <paramref name="worker"/> measures, and <paramref name="gate"/> is that worker's
    /// headroom. The worker's events reach this through <see cref="HandleAsync"/>.
    /// </summary>
    public CapacitySampler(
        IRunWorker worker,
        WipLedger wip,
        HeadroomGate gate,
        IHeavyLeaseView lease,
        Func<int> memoryPercent,
        Func<int> pressurePercent,
        Func<CapacitySample, Task>? push = null,
        ILogger<CapacitySampler>? logger = null,
        TimeProvider? clock = null,
        TimeSpan? interval = null,
        int ticksPerSecond = 100)
        : this(() => [(worker, gate)], wip, lease, memoryPercent, pressurePercent, push, logger, clock, interval, ticksPerSecond)
    {
    }

    /// <summary>
    /// Over the workers that are connected at each sample, each with the headroom gate its own
    /// measurements feed: control's, when its runs go to workers in processes of their own.
    /// </summary>
    public CapacitySampler(
        Func<IReadOnlyList<(IRunWorker Worker, HeadroomGate Gate)>> workers,
        WipLedger wip,
        IHeavyLeaseView lease,
        Func<int> memoryPercent,
        Func<int> pressurePercent,
        Func<CapacitySample, Task>? push = null,
        ILogger<CapacitySampler>? logger = null,
        TimeProvider? clock = null,
        TimeSpan? interval = null,
        int ticksPerSecond = 100)
    {
        this.workers = workers;
        this.wip = wip;
        this.lease = lease;
        this.memoryPercent = memoryPercent;
        this.pressurePercent = pressurePercent;
        this.push = push;
        this.logger = logger;
        this.ticksPerSecond = ticksPerSecond;
        _clock = clock ?? TimeProvider.System;
        Interval = interval ?? DefaultInterval;
    }

    /// <summary>
    /// Over a worker of its own, in this process, that measures with <paramref name="cgroup"/> and
    /// <paramref name="processes"/> over <paramref name="groups"/>: the sample crosses the protocol
    /// all the same.
    /// </summary>
    public CapacitySampler(
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
        int ticksPerSecond = 100)
    {
        var heartbeat = new RunHeartbeat();
        var worker = InProcessWorker.Connect(
            WorkerId.Local,
            events => new WorkerHost(
                WorkerId.Local, events, new RunLauncher(heartbeat), heartbeat, cgroup: cgroup, processes: processes, groups: groups,
                clock: clock),
            HandleAsync).Worker;
        this.workers = () => [(worker, gate)];
        this.wip = wip;
        this.lease = lease;
        this.memoryPercent = memoryPercent;
        this.pressurePercent = pressurePercent;
        this.push = push;
        this.logger = logger;
        this.ticksPerSecond = ticksPerSecond;
        _clock = clock ?? TimeProvider.System;
        Interval = interval ?? DefaultInterval;
    }

    /// <summary>The worker this samples: the first, when there are several.</summary>
    public IRunWorker Worker => workers()[0].Worker;

    /// <summary>How often a sample is taken.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    /// <summary>How far back the history reaches.</summary>
    public static readonly TimeSpan HistorySpan = TimeSpan.FromMinutes(10);

    /// <summary>How many runs each top list names.</summary>
    public const int TopRuns = 5;

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sampling = new(1, 1);
    private readonly Dictionary<WorkerId, Round> _rounds = [];

    /// <summary>What a worker that did not answer in time reads as: every figure not measured, never 0.</summary>
    private static readonly WorkerCapacitySampled Unanswered =
        new(DateTimeOffset.MinValue, WorkerCapacity.ToFigures(WorkerCapacity.NotMeasured with { NotMeasured = CgroupReader.AllFigures }), []);
    private readonly Queue<CapacitySample> _history = new();
    private Previous? _previous;

    public TimeSpan Interval { get; }

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

    /// <summary>What a worker measured for the sample in progress.</summary>
    public Task HandleAsync(WorkerEnvelope envelope, CancellationToken ct = default)
    {
        lock (_rounds)
        {
            // A measurement that arrives after its round gave up on it is dropped, never charged to the next.
            if (!_rounds.TryGetValue(envelope.Worker, out var round)) return Task.CompletedTask;

            if (envelope.Event is RunMeasured measured) round.Measured.Add(measured);
            else if (envelope.Event is WorkerCapacitySampled sampled) round.Sampled.TrySetResult(sampled);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Takes one sample now and returns it. An entry point for callers in this process, which have
    /// always taken a sample synchronously; a worker in this process has answered by the time its
    /// send completes, so this never waits on a timer.
    /// </summary>
    public CapacitySample Sample() => SampleAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Takes one sample, keeps it, hands it to each gate and the ledger, and returns it. Every worker
    /// is asked at once; one that has not answered within <see cref="Interval"/> is not measured this
    /// round: its gate keeps the figures it last had, and nothing waits on it past the interval.
    /// </summary>
    public async Task<CapacitySample> SampleAsync(CancellationToken ct = default)
    {
        await _sampling.WaitAsync(ct);
        try
        {
            return await SampleLockedAsync(ct);
        }
        finally
        {
            _sampling.Release();
        }
    }

    private async Task<CapacitySample> SampleLockedAsync(CancellationToken ct)
    {
        var at = _clock.GetUtcNow();
        var asked = workers();

        var rounds = asked.Select(w => (w.Worker, w.Gate, Round: new Round(w.Worker.Id))).ToList();
        lock (_rounds)
        {
            _rounds.Clear();
            foreach (var (worker, _, round) in rounds) _rounds[worker.Id] = round;
        }

        foreach (var (worker, _, round) in rounds)
        {
            // A send that fails is a worker that will not answer this round.
            try
            {
                _ = worker.SendAsync(new SampleCapacity(), ct).ContinueWith(
                    send => round.Sampled.TrySetResult(null),
                    CancellationToken.None,
                    TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception)
            {
                round.Sampled.TrySetResult(null);
            }
        }

        var answers = new List<(IRunWorker Worker, HeadroomGate Gate, WorkerCapacitySampled? Answer, IReadOnlyList<RunMeasured> Measured)>();
        foreach (var (worker, gate, round) in rounds)
        {
            WorkerCapacitySampled? answer;
            try
            {
                answer = await round.Sampled.Task.WaitAsync(Interval - (_clock.GetUtcNow() - at) is { } left && left > TimeSpan.Zero ? left : TimeSpan.Zero, _clock, ct);
            }
            catch (TimeoutException)
            {
                answer = null;
            }

            if (answer is null)
            {
                logger?.LogWarning("Worker {Worker} did not answer the capacity sample within {Interval}; it is not measured this round.", worker.Id, Interval);
            }

            answers.Add((worker, gate, answer, answer is null ? [] : [.. round.Measured]));
        }

        lock (_rounds) _rounds.Clear();

        // Each gate answers from its own worker's measurement from here on. A worker that did not
        // answer leaves its gate with the figures it last had, at their own age.
        foreach (var (_, gate, answer, _) in answers)
        {
            if (answer is not null) gate.Update(WorkerCapacity.ToCgroup(answer.Figures), at);
        }

        var figures = answers.Count == 1
            ? WorkerCapacity.ToCgroup((answers[0].Answer ?? Unanswered).Figures)
            : Sum([.. answers.Where(a => a.Answer is not null).Select(a => WorkerCapacity.ToCgroup(a.Answer!.Figures))]);
        var measuredRuns = answers.SelectMany(a => a.Measured.Select(m => (a.Worker.Id, Measured: m))).ToList();
        var view = wip.View();

        // What a run asking now would wait for: the first worker's reason only when none has room.
        var holding = answers.Count == 1
            ? answers[0].Gate.Reason()
            : answers.Count == 0
                ? WipLedger.WorkerReason
                : answers.All(a => a.Gate.Reason() is not null) ? answers[0].Gate.Reason() : null;

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
            var ticks = new Dictionary<(WorkerId, int), long>();
            foreach (var (worker, measured) in measuredRuns)
            {
                var group = (worker, measured.Group);
                var run = measured.Run.Member;

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
                var sample = await SampleAsync(stoppingToken);
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


    /// <summary>Several workers' figures as one: each figure summed over the workers that measured it, not measured when none did.</summary>
    private static CgroupFigures Sum(IReadOnlyList<CgroupFigures> each)
    {
        if (each.Count == 0) return WorkerCapacity.NotMeasured with { NotMeasured = CgroupReader.AllFigures };
        if (each.Count == 1) return each[0];

        static long? Total(IEnumerable<long?> values)
        {
            long? sum = null;
            foreach (var value in values) if (value is { } v) sum = (sum ?? 0) + v;
            return sum;
        }

        static double? TotalOf(IEnumerable<double?> values)
        {
            double? sum = null;
            foreach (var value in values) if (value is { } v) sum = (sum ?? 0) + v;
            return sum;
        }

        // Pressure is a share of time, not an amount: it does not add up, so several workers' is not measured here; each worker's is its own.
        var notMeasured = CgroupReader.AllFigures.Where(name => each.All(f => f.NotMeasured.Contains(name)))
            .Concat(["cpu.pressure", "memory.pressure"]).Distinct().ToList();

        return new CgroupFigures(
            each.Select(f => f.Version).FirstOrDefault(v => v is not null),
            TotalOf(each.Select(f => f.CpuLimit)), each.Any(f => f.CpuUnlimited),
            Total(each.Select(f => f.CpuUsageUsec)), Total(each.Select(f => f.CpuThrottledPeriods)), Total(each.Select(f => f.CpuThrottledUsec)),
            Total(each.Select(f => f.MemoryLimitBytes)), each.Any(f => f.MemoryUnlimited),
            Total(each.Select(f => f.MemoryCurrentBytes)), Total(each.Select(f => f.MemoryAnonBytes)), Total(each.Select(f => f.MemoryFileBytes)),
            Total(each.Select(f => f.MemoryShmemBytes)), null, null,
            Total(each.Select(f => f.PidsCurrent)), Total(each.Select(f => f.PidsLimit)), each.Any(f => f.PidsUnlimited),
            notMeasured);
    }

    /// <summary>One worker's answer to the sample in progress, and what it measured of its runs.</summary>
    private sealed class Round(WorkerId worker)
    {
        public WorkerId Worker => worker;

        public TaskCompletionSource<WorkerCapacitySampled?> Sampled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<RunMeasured> Measured { get; } = [];
    }

    private sealed record Previous(DateTimeOffset At, long? CpuUsageUsec, IReadOnlyDictionary<(WorkerId, int), long> RunTicks);
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
