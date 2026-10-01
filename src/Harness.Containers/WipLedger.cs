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
/// never refused. The gate covers a Manager too, but its reserved slot is unchanged. Whoever feeds the
/// gate calls <see cref="HeadroomChanged"/> after each new measurement, so a waiter held for headroom
/// asks again when it clears.
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

    private readonly object _gate = new();
    private readonly Dictionary<string, WipHold> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WipHold> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _queue = [];
    private readonly HashSet<string> _heldForHeadroom = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string?>? _headroom;
    private int _max;
    private TaskCompletionSource _changed = NewSignal();

    /// <param name="maxRunning">The run limit; 0 or less is unlimited.</param>
    /// <param name="headroom">Null when the instance has room for another run, otherwise the reason
    /// it waits. Read under the ledger's lock on every claim the limit would admit, so it must answer
    /// from figures already measured and never do I/O. Absent, only the run limit admits.</param>
    public WipLedger(int maxRunning, Func<string?>? headroom = null)
    {
        _max = maxRunning;
        _headroom = headroom;
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

            // The limit has room: measured headroom decides. Still a wait, never a refusal.
            if (_headroom?.Invoke() is { } reason)
            {
                WaitLocked(id, key, reason, headroom: true);
                return null;
            }

            RemoveWaiterLocked(key);
            _running[key] = new WipHold(id.Team, id.Name, DateTimeOffset.UtcNow);
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

            if (_headroom?.Invoke() is not { } reason)
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
            if (_running.Remove(key)) PulseLocked();
        }
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
