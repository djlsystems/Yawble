using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Capacity;

namespace Harness.Host;

/// <summary>
/// THE WORKERS CONTROL PLACES RUNS ON, each with the headroom gate its own capacity samples feed.
/// Admission (<see cref="WipLedger"/>) asks it which worker has room; the runner asks it for the
/// worker a run was placed on.
/// </summary>
/// <remarks>
/// <para>
/// In one process (<c>all</c>) the pool is fixed: the Host's own worker, always there, with no cap of
/// its own - its bound is the run limit. In <c>control</c> it starts empty and workers join as they
/// connect (<see cref="Join"/>), drop while their connection is down (<see cref="Drop"/>), come back
/// (<see cref="Back"/>) and leave (<see cref="Leave"/>).
/// </para>
/// <para>
/// <see cref="Workers"/> lists the workers a run may be placed on now, in the order they are tried:
/// most projected memory headroom first - the measured headroom less <c>wip.memoryPerRunMb</c> for each
/// run placed on the worker since its last sample, so a burst inside one sample spreads instead of
/// piling onto the worker that led it; then workers with no headroom figure (no sample yet, or no
/// memory limit), which hold nothing back and are never taken for 0 or unlimited, by runs placed
/// against their own bound; ties to the worker with fewer runs placed, then to the one that connected
/// first. A dropped worker is never listed, nor one that is draining (its operator is about to stop
/// it): its runs go on, and nothing new goes to it.
/// </para>
/// <para>
/// The projection only orders the workers. Whether a run waits is the gate's, which reads the measured
/// figure alone: nothing estimated holds a run. The count against a worker resets when its next sample
/// lands, whose figures carry those runs by then.
/// </para>
/// </remarks>
public sealed class WorkerPool : IRunPlacement
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly bool _fixed;
    private readonly Func<WorkerId, HeadroomGate>? _newGate;
    private readonly Func<WorkerInfo, int?>? _bound;
    private readonly Func<int>? _memoryPerRunMb;
    private readonly TimeProvider _clock;
    private long _joined;
    private TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A fixed pool: these workers, always placeable once connected, with no bound of their own.</summary>
    public WorkerPool(IReadOnlyList<(WorkerId Id, HeadroomGate Gate)> workers)
    {
        _fixed = true;
        _clock = TimeProvider.System;
        var since = DateTimeOffset.UtcNow;
        foreach (var (id, gate) in workers) _entries.Add(new Entry(id, gate, new WorkerInfo(id, null, null, null, since), _joined++));
    }

    /// <summary>
    /// A pool workers join as they connect, each with a gate from <paramref name="gate"/>, and each
    /// bounded by <paramref name="bound"/> (null: no cap of its own). <paramref name="memoryPerRunMb"/>
    /// is what each run placed since a worker's last sample is counted at against its headroom; absent,
    /// nothing is.
    /// </summary>
    public WorkerPool(
        Func<WorkerId, HeadroomGate> gate, Func<WorkerInfo, int?>? bound = null, TimeProvider? clock = null, Func<int>? memoryPerRunMb = null)
    {
        _newGate = gate;
        _bound = bound;
        _clock = clock ?? TimeProvider.System;
        _memoryPerRunMb = memoryPerRunMb;
    }

    /// <summary>How many runs are placed on a worker now; the ledger's answer, for ties.</summary>
    public Func<WorkerId, int>? Placed { get; set; }

    public IReadOnlyList<WorkerId> Workers
    {
        get
        {
            (Entry Entry, int SinceSample)[] placeable;
            lock (_gate)
            {
                if (_fixed) return [.. _entries.Select(e => e.Id)];
                placeable = [.. _entries.Where(e => e.Worker is not null && e.DroppedAt is null && !e.Draining)
                    .Select(e => (e, e.PlacedSinceSample()))];
            }

            // Outside the pool's lock: the ledger asks this under its own, and the count is the ledger's.
            var placed = Placed;
            var allowance = (_memoryPerRunMb?.Invoke() ?? 0) * 1024L * 1024L;
            return [.. placeable
                .Select(e => (
                    e.Entry,
                    Projected: e.Entry.Gate.Headroom - e.SinceSample * allowance,
                    Placed: placed?.Invoke(e.Entry.Id) ?? 0,
                    Bound: _bound?.Invoke(e.Entry.Info)))
                .OrderBy(e => e.Projected is null ? 1 : 0)
                .ThenByDescending(e => e.Projected ?? 0)
                // Unmeasured: by how full each is against its own bound. A set run limit leaves every
                // worker unbounded, so bounded and unbounded workers never compare on these two scales.
                .ThenBy(e => e.Projected is null ? Load(e.Placed, e.Bound) : 0)
                .ThenBy(e => e.Placed)
                .ThenBy(e => e.Entry.Order)
                .Select(e => e.Entry.Id)];
        }
    }

    public string? HeadroomReason(WorkerId worker) => Gate(worker).Reason();

    /// <summary>
    /// A run was placed on <paramref name="worker"/>: it counts against that worker's headroom until
    /// the worker's next sample. A fixed pool is never ranked, and counts nothing.
    /// </summary>
    public void RunPlaced(WorkerId worker)
    {
        lock (_gate)
        {
            if (_fixed || _entries.FirstOrDefault(e => e.Id == worker) is not { } entry) return;
            var samples = entry.Gate.Samples;
            if (entry.CountedAgainst != samples)
            {
                entry.CountedAgainst = samples;
                entry.Counted = 0;
            }

            entry.Counted++;
        }
    }

    /// <summary>How full a worker is against its own bound; with no bound, its runs placed.</summary>
    private static double Load(int placed, int? bound) => bound is { } b ? placed / (double)Math.Max(b, 1) : placed;

    public int? Bound(WorkerId worker)
    {
        if (_bound is null) return null;
        lock (_gate) return _entries.FirstOrDefault(e => e.Id == worker) is { } entry ? _bound(entry.Info) : null;
    }

    /// <summary>The headroom gate <paramref name="worker"/>'s capacity samples feed.</summary>
    public HeadroomGate Gate(WorkerId worker)
    {
        lock (_gate) return Find(worker).Gate;
    }

    /// <summary>The connection to one of a fixed pool's workers, once it has one.</summary>
    public void Connect(IRunWorker worker)
    {
        lock (_gate) Find(worker.Id).Worker = worker;
    }

    /// <summary>A worker connected: it is placeable from now, with a gate of its own. Returns that gate.</summary>
    public HeadroomGate Join(IRunWorker worker, WorkerInfo info)
    {
        lock (_gate)
        {
            if (_fixed) throw new InvalidOperationException("A fixed pool takes no workers.");
            _entries.RemoveAll(e => e.Id == worker.Id);
            var entry = new Entry(worker.Id, _newGate!(worker.Id), info, _joined++) { Worker = worker };
            _entries.Add(entry);
            ConnectedLocked();
            return entry.Gate;
        }
    }

    /// <summary>The worker's connection is down: it is not placeable until it is back.</summary>
    public void Drop(WorkerId worker)
    {
        lock (_gate)
        {
            if (_entries.FirstOrDefault(e => e.Id == worker) is { } entry) entry.DroppedAt ??= _clock.GetUtcNow();
        }
    }

    /// <summary>The worker's connection is back.</summary>
    /// <summary>Takes a worker out of placement while it drains, or back in after.</summary>
    public void Drain(WorkerId worker, bool draining)
    {
        lock (_gate)
        {
            if (_entries.FirstOrDefault(e => e.Id == worker) is { } entry) entry.Draining = draining;
        }
    }

    /// <summary>Whether a worker is draining.</summary>
    public bool IsDraining(WorkerId worker)
    {
        lock (_gate) return _entries.FirstOrDefault(e => e.Id == worker)?.Draining ?? false;
    }

    public void Back(WorkerId worker)
    {
        lock (_gate)
        {
            if (_entries.FirstOrDefault(e => e.Id == worker) is { } entry) entry.DroppedAt = null;
            ConnectedLocked();
        }
    }

    /// <summary>
    /// Completes when a worker is placeable: at once when one is, otherwise when one next joins or comes
    /// back. What a person waiting for a worker is told on. In a fixed pool, at once.
    /// </summary>
    public Task ConnectedAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return _fixed || _entries.Any(e => e.Worker is not null && e.DroppedAt is null)
                ? Task.CompletedTask
                : _connected.Task.WaitAsync(ct);
        }
    }

    private void ConnectedLocked()
    {
        var waiting = _connected;
        _connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting.TrySetResult();
    }

    /// <summary>The worker is gone.</summary>
    public void Leave(IRunWorker worker)
    {
        lock (_gate) _entries.RemoveAll(e => ReferenceEquals(e.Worker, worker));
    }

    /// <summary>Every worker joined, dropped ones included, with what it said of itself and its gate.</summary>
    public IReadOnlyList<(WorkerInfo Info, HeadroomGate Gate, IRunWorker? Worker, DateTimeOffset? DroppedAt)> Entries()
    {
        lock (_gate) return [.. _entries.OrderBy(e => e.Order).Select(e => (e.Info, e.Gate, e.Worker, e.DroppedAt))];
    }

    /// <summary>Every connected worker, dropped ones included: what a message for all of them goes to.</summary>
    public IReadOnlyList<IRunWorker> Connected()
    {
        lock (_gate) return [.. _entries.OrderBy(e => e.Order).Select(e => e.Worker).OfType<IRunWorker>()];
    }

    /// <summary>
    /// The worker <paramref name="placed"/> names, or, when a run was placed nowhere, the first a run
    /// would be placed on now. Throws when there is none.
    /// </summary>
    public IRunWorker Worker(WorkerId? placed)
    {
        lock (_gate)
        {
            if (placed is not null && _entries.FirstOrDefault(e => e.Id == placed)?.Worker is { } worker) return worker;
        }

        foreach (var id in Workers)
        {
            lock (_gate)
            {
                if (_entries.FirstOrDefault(e => e.Id == id)?.Worker is { } first) return first;
            }
        }

        throw new InvalidOperationException("No worker is connected.");
    }

    /// <summary>The worker a member's messages go to: where it was placed, or null when it holds no slot.</summary>
    public IRunWorker? For(WorkerId? placed)
    {
        if (placed is null) return null;
        lock (_gate) return _entries.FirstOrDefault(e => e.Id == placed)?.Worker;
    }

    private Entry Find(WorkerId worker) =>
        _entries.FirstOrDefault(e => e.Id == worker) ?? throw new InvalidOperationException($"No worker {worker} is in the pool.");

    private sealed class Entry(WorkerId id, HeadroomGate gate, WorkerInfo info, long order)
    {
        public WorkerId Id => id;

        public HeadroomGate Gate => gate;

        public WorkerInfo Info => info;

        public long Order => order;

        public IRunWorker? Worker { get; set; }

        public DateTimeOffset? DroppedAt { get; set; }

        public bool Draining { get; set; }

        /// <summary>Runs placed since the sample <see cref="CountedAgainst"/> names.</summary>
        public int Counted { get; set; }

        /// <summary>The gate's <see cref="HeadroomGate.Samples"/> when <see cref="Counted"/> was last reset.</summary>
        public long CountedAgainst { get; set; }

        /// <summary>Runs placed since the last sample: none once a new one has landed.</summary>
        public int PlacedSinceSample() => CountedAgainst == gate.Samples ? Counted : 0;
    }
}

/// <summary>
/// What a worker said of itself when it connected: its build version, its CPUs and memory limit as it
/// measured them (null: not measured), and when it connected.
/// </summary>
public sealed record WorkerInfo(WorkerId Id, string? Version, double? Cpus, long? MemoryLimitBytes, DateTimeOffset ConnectedSince);
