using Harness.Contracts;

namespace Harness.Containers;

/// <summary>One run holding or waiting on an instance-wide slot.</summary>
public sealed record WipHold(string Team, string Member, DateTimeOffset Since);

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

    private readonly object _gate = new();
    private readonly Dictionary<string, WipHold> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WipHold> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _queue = [];
    private int _max;
    private TaskCompletionSource _changed = NewSignal();

    public WipLedger(int maxRunning) => _max = maxRunning;

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
                if (!_waiting.ContainsKey(key))
                {
                    _waiting[key] = new WipHold(id.Team, id.Name, DateTimeOffset.UtcNow);
                    _queue.Add(key);
                }

                return null;
            }

            RemoveWaiterLocked(key);
            _running[key] = new WipHold(id.Team, id.Name, DateTimeOffset.UtcNow);
            return new Release(this, key);
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

    private bool RemoveWaiterLocked(string key)
    {
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
