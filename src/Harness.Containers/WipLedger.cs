using Harness.Contracts;

namespace Harness.Containers;

/// <summary>One run holding or waiting on an instance-wide slot. A waiter's <c>Reason</c> says what it
/// waits for - <see cref="WipLedger.SlotReason"/>, or the headroom gate's sentence ("waiting for
/// memory: 11.2 of 12.9 GB in use"); a running hold has none.</summary>
public sealed record WipHold(string Team, string Member, DateTimeOffset Since, string? Reason = null);

/// <summary>What <c>GET /api/wip</c> returns. Held work is visible, and attributed to a team and member.
/// <c>Waiting</c> is in queue order: the head is the next to start.</summary>
public sealed record WipView(int Max, IReadOnlyList<WipHold> Running, IReadOnlyList<WipHold> Waiting);

/// <summary>
/// Instance-wide admission. A cgroup limit keeps the desktop usable; this is what stops the
/// container from filling that cgroup with runs nobody can see.
/// </summary>
/// <remarks>
/// <para>
/// <c>maxRunning</c> of 0 or less is unlimited. A claim is per container: one member cannot take
/// two slots, and releasing twice cannot free someone else's.
/// </para>
///
/// <para>
/// <b>WAITERS ARE A FIFO QUEUE.</b> A refused claim joins the tail; a slot freed by a release
/// goes to the head, whoever asks first. The claim loop waits on <see cref="Changed"/> rather than
/// polling, so the order is the queue's and never a 20ms race.
/// </para>
///
/// <para>
/// <b>ONE RESERVED SLOT FOR MANAGERS.</b> <c>max</c> counts every run, Managers included; a Manager
/// may additionally use slot <c>max + 1</c>. A pool full of members therefore never starves the
/// container that would free them. Nothing is refused: work over the limit waits.
/// </para>
///
/// <para>
/// <b>AND MEASURED HEADROOM.</b> A run the limit has room for still waits while the headroom gate
/// (handed in, read on every claim) answers a reason - memory in use or memory pressure over its
/// threshold. It waits exactly as for a slot, in the same queue, with that reason on its hold; it is
/// never refused. A Manager is never held by the gate: when memory is high the Manager is the run that
/// stops or redirects the work holding it, so it is admitted by the run limit alone, reserved slot
/// included, exactly as without a gate. Whoever feeds the
/// gate calls <see cref="HeadroomChanged"/> after each new measurement, so a waiter held for headroom
/// asks again when it clears.
/// </para>
///
/// <para>
/// <b>AND A WORKER.</b> An admitted run is placed on the first worker (<see cref="IRunPlacement"/>)
/// whose headroom has no reason to hold it and which has not reached its own bound
/// (<see cref="IRunPlacement.Bound"/>), and <see cref="PlacedOn"/> says which until its slot is
/// released. The gate above is per worker: a run waits with the first worker's reason only when none
/// has room. A Manager goes on the first worker unchecked. With no worker connected, every run the
/// limit admits - a Manager's too - waits in the same queue with <see cref="WorkerReason"/>, and is
/// never refused; <see cref="WorkersChanged"/> wakes the waiters when one connects. The run limit,
/// the reserved slot, the queue and the reasons stay instance-wide here.
/// </para>
///
/// <para>
/// <b>THE LIMIT IS SETTABLE AT RUNTIME</b> (<see cref="SetMax"/>, from <c>wip.maxRunning</c>). Lowering
/// it below the running count evicts nothing: running work finishes, and the next start waits until
/// the count is back under the new limit.
/// </para>
/// </remarks>
public sealed class WipLedger
{
    /// <summary>The container name every team's Manager has. A container NAME, never a label -
    /// the same identifier <c>TeamRegistry.DefaultManagerName</c> is, which nobody can rename.</summary>
    public const string ManagerName = "Manager";

    /// <summary>What a run waiting for the run limit is waiting for.</summary>
    public const string SlotReason = "waiting for a slot";

    /// <summary>What a run the limit has room for waits for while no worker is connected to run it on.</summary>
    public const string WorkerReason = "waiting for a worker";

    private readonly object _gate = new();
    private readonly Dictionary<string, WipHold> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WipHold> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _queue = [];
    private readonly HashSet<string> _heldForHeadroom = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorkerId> _placed = new(StringComparer.OrdinalIgnoreCase);
    private readonly IRunPlacement _placement;
    private int _max;
    private TaskCompletionSource _changed = NewSignal();

    /// <param name="maxRunning">The run limit; 0 or less is unlimited.</param>
    /// <param name="headroom">Null when the instance has room for another run, otherwise the reason
    /// it waits. Read under the ledger's lock on every claim the limit would admit, so it must answer
    /// from figures already measured and never do I/O. Absent, only the run limit admits.</param>
    public WipLedger(int maxRunning, Func<string?>? headroom = null)
        : this(maxRunning, new OneWorker(headroom))
    {
    }

    /// <param name="maxRunning">The run limit; 0 or less is unlimited.</param>
    /// <param name="placement">The workers a run may go on and each one's headroom, read under the
    /// ledger's lock on every claim the limit would admit: answered from figures already measured,
    /// never by I/O.</param>
    public WipLedger(int maxRunning, IRunPlacement placement)
    {
        _max = maxRunning;
        _placement = placement;
    }

    public int Max
    {
        get
        {
            lock (_gate) return _max;
        }
    }

    /// <summary>
    /// Completes the next time a slot is released, a waiter withdraws, or the limit changes - the
    /// moments at which a refused claim might now succeed. Read it BEFORE asking
    /// <see cref="TryEnter"/>, so a release between the refusal and the wait is not missed.
    /// </summary>
    public Task Changed
    {
        get
        {
            lock (_gate) return _changed.Task;
        }
    }

    public static bool IsManager(ContainerId id) => IsManager(id.Name);

    private static bool IsManager(string member) =>
        string.Equals(member, ManagerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Changes the limit under the ledger's lock. Raising it wakes the waiters; lowering it evicts
    /// nothing.
    /// </summary>
    public void SetMax(int maxRunning)
    {
        lock (_gate)
        {
            if (_max == maxRunning) return;
            _max = maxRunning;
            PulseLocked();
        }
    }

    /// <summary>
    /// Claims a slot, or returns null when this container must wait. A null answer records the
    /// container as waiting, at the tail of the queue the first time, so a colleague can see who is
    /// blocked and who holds the slots.
    /// </summary>
    public IDisposable? TryEnter(ContainerId id)
    {
        var key = id.ToString();

        lock (_gate)
        {
            if (_running.ContainsKey(key))
            {
                RemoveWaiterLocked(key);
                return Nop.Instance;
            }

            if (!AdmitsLocked(key, IsManager(id)))
            {
                WaitLocked(id, key, SlotReason, headroom: false);
                return null;
            }

            // The limit has room: a worker's measured headroom decides where, for members. Still a
            // wait, never a refusal. A Manager skips it - it is the run that would free the memory.
            var (worker, reason) = PlaceLocked(IsManager(id));
            if (reason is not null)
            {
                WaitLocked(id, key, reason, headroom: true);
                return null;
            }

            RemoveWaiterLocked(key);
            _running[key] = new WipHold(id.Team, id.Name, DateTimeOffset.UtcNow);
            if (worker is not null) _placed[key] = worker;
            return new Release(this, key);
        }
    }

    /// <summary>
    /// A new measurement: when the gate now has room and a waiter was held for headroom, the waiters
    /// ask again; otherwise each such waiter's reason is brought up to date (the figure in it moved).
    /// </summary>
    public void HeadroomChanged()
    {
        lock (_gate)
        {
            if (_heldForHeadroom.Count == 0) return;

            if (PlaceLocked(manager: false).Reason is not { } reason)
            {
                _heldForHeadroom.Clear();
                PulseLocked();
                return;
            }

            foreach (var key in _heldForHeadroom)
            {
                if (_waiting.TryGetValue(key, out var hold)) _waiting[key] = hold with { Reason = reason };
            }
        }
    }

    /// <summary>
    /// A worker connected or went: every waiter asks again, so a run waiting for a worker starts on
    /// the one that came.
    /// </summary>
    public void WorkersChanged()
    {
        lock (_gate)
        {
            _heldForHeadroom.Clear();
            PulseLocked();
        }
    }

    /// <summary>How many running members are placed on <paramref name="worker"/>.</summary>
    public int PlacedCount(WorkerId worker)
    {
        lock (_gate) return _placed.Values.Count(placed => placed == worker);
    }

    /// <summary>
    /// Takes a waiter out of the queue without starting it - its team was paused, or its container
    /// stopped. A waiter that will never ask again must not hold the head of the queue.
    /// </summary>
    public void Withdraw(ContainerId id)
    {
        lock (_gate)
        {
            if (RemoveWaiterLocked(id.ToString())) PulseLocked();
        }
    }

    public WipView View()
    {
        lock (_gate)
        {
            return new WipView(
                _max,
                _running.Values.OrderBy(hold => hold.Since).ToArray(),
                _queue.Select(key => _waiting[key]).ToArray());
        }
    }

    /// <summary>
    /// Whether <paramref name="key"/> starts now. Walks the queue ahead of it, letting each earlier
    /// waiter take a slot first if one would be open to it, so a slot freed by a release belongs to
    /// the head and a later waiter asking first is still refused. A Manager's limit is one higher.
    /// </summary>
    /// <summary>The worker a running member was placed on, or null when it holds no slot.</summary>
    public WorkerId? PlacedOn(ContainerId id)
    {
        lock (_gate) return _placed.GetValueOrDefault(id.ToString());
    }

    /// <summary>
    /// The first worker with room and under its own bound, or, when none has, the first worker's
    /// reason. A Manager takes the first worker without asking. No worker at all is
    /// <see cref="WorkerReason"/>.
    /// </summary>
    private (WorkerId? Worker, string? Reason) PlaceLocked(bool manager)
    {
        string? first = null;
        var workers = _placement.Workers;
        if (workers.Count == 0) return (null, WorkerReason);

        foreach (var worker in workers)
        {
            if (manager) return (worker, null);
            if (_placement.HeadroomReason(worker) is { } reason)
            {
                first ??= reason;
                continue;
            }

            // At its own bound: as full as the run limit would be.
            if (_placement.Bound(worker) is { } bound && _placed.Values.Count(placed => placed == worker) >= bound)
            {
                first ??= SlotReason;
                continue;
            }

            return (worker, null);
        }

        return (null, first);
    }

    private bool AdmitsLocked(string key, bool manager)
    {
        if (_max <= 0) return true;

        var taken = _running.Count;

        foreach (var ahead in _queue)
        {
            if (string.Equals(ahead, key, StringComparison.OrdinalIgnoreCase)) break;

            var limitAhead = IsManager(_waiting[ahead].Member) ? _max + 1 : _max;
            if (taken < limitAhead) taken++;
        }

        return taken < (manager ? _max + 1 : _max);
    }

    /// <summary>Records <paramref name="key"/> as waiting - at the tail the first time, keeping its
    /// place and its <c>Since</c> after that - with what it waits for.</summary>
    private void WaitLocked(ContainerId id, string key, string reason, bool headroom)
    {
        if (_waiting.TryGetValue(key, out var hold))
        {
            _waiting[key] = hold with { Reason = reason };
        }
        else
        {
            _waiting[key] = new WipHold(id.Team, id.Name, DateTimeOffset.UtcNow, reason);
            _queue.Add(key);
        }

        if (headroom) _heldForHeadroom.Add(key);
        else _heldForHeadroom.Remove(key);
    }

    private bool RemoveWaiterLocked(string key)
    {
        _heldForHeadroom.Remove(key);
        if (!_waiting.Remove(key)) return false;
        _queue.RemoveAll(queued => string.Equals(queued, key, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private void PulseLocked()
    {
        var fired = _changed;
        _changed = NewSignal();
        fired.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ReleaseOne(string key)
    {
        lock (_gate)
        {
            _placed.Remove(key);
            if (_running.Remove(key)) PulseLocked();
        }
    }

    /// <summary>The Host's one worker, its headroom read through a delegate.</summary>
    private sealed class OneWorker(Func<string?>? headroom) : IRunPlacement
    {
        public IReadOnlyList<WorkerId> Workers { get; } = [WorkerId.Local];

        public string? HeadroomReason(WorkerId worker) => headroom?.Invoke();
    }

    private sealed class Release(WipLedger ledger, string key) : IDisposable
    {
        private int _once;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _once, 1) == 0) ledger.ReleaseOne(key);
        }
    }

    private sealed class Nop : IDisposable
    {
        public static readonly Nop Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// The workers a run may be placed on, in the order they are tried, and why each would hold a run
/// now (null: it has room). Admission asks it; its answers come from each worker's capacity samples.
/// </summary>
public interface IRunPlacement
{
    IReadOnlyList<WorkerId> Workers { get; }

    string? HeadroomReason(WorkerId worker);

    /// <summary>How many runs <paramref name="worker"/> may hold at once; null for no cap of its own.</summary>
    int? Bound(WorkerId worker) => null;
}
