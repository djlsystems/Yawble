using System.Globalization;
using Yawble.Core.Scheduling;

namespace Yawble.Core.Bus;

/// <summary>
/// Teams under one global run limit.
///
/// Every sandbox holds one of app.maxConcurrentAgents slots: member runs, Manager runs and interactive terminal
/// sessions. They wait in ONE queue, the run_queue table, whose id order is admission order: strict first-come,
/// first-served across all teams, with no jump and no reserved slot. An interactive session is a
/// queue entry of kind 'interactive'; while a member's session waits or runs, its instructions wait, so one member
/// never has two sandboxes writing its clone.
///
/// A slot is given back when the sandbox ends for any reason: completed, failed, crashed (the runner threw),
/// timeout or idle (the launcher's supervisor), or Stop. Stop cancels the run's token and records "stopped".
/// The limit is read at every admission; <see cref="Reschedule"/> makes a changed limit take effect at once, and
/// a lowered limit never stops a running sandbox.
/// </summary>
public sealed partial class BusEngine
{
    public const string StoppedReason = "stopped by the person";

    // Interactive sessions waiting for a slot (by run_queue id) and holding one (by member id). The queue row is
    // the order; the waiter is the body to run once admitted. A restart drops waiters and their rows.
    private readonly Dictionary<long, SessionWaiter> _sessionWaiters = [];
    private readonly Dictionary<long, SessionRun> _sessions = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, bool> _stopRequested = new(); // read outside the gate

    private sealed record SessionWaiter(MemberRef Member, long QueueId, Func<CancellationToken, Task> Body, SessionTicket Ticket);

    private sealed class SessionRun(MemberRef member, long sessionId, string queuedAt, string startedAt, CancellationTokenSource cancel)
    {
        public MemberRef Member => member;
        public long SessionId => sessionId;
        public string QueuedAt => queuedAt;
        public string StartedAt => startedAt;
        public CancellationTokenSource Cancel => cancel;
        public bool StopRequested { get; set; }
        public Task Task { get; set; } = Task.CompletedTask;
    }

    /// <summary>Slots in use now: every live sandbox, of every team and kind.</summary>
    private int SlotsInUse => _active.Count + _sessions.Count;

    private bool HasSession(long memberId) =>
        _sessions.ContainsKey(memberId) || _sessionWaiters.Values.Any(w => w.Member.Id == memberId);

    private bool StopWasRequested(long runId) => _stopRequested.ContainsKey(runId);

    // ------------------------------------------------------------------ interactive sessions

    /// <summary>
    /// The person opens a member's interactive CLI. Offered only while the member is idle: refused while it runs,
    /// is queued, or already has a session. The session joins the back of the one global queue; <paramref name="body"/>
    /// runs once admitted and holds the slot until it returns. Its token is cancelled by Stop and when the core stops.
    /// </summary>
    public Task<SessionTicket> RequestSessionAsync(string team, string member, Func<CancellationToken, Task> body, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var target = _store.Member(team, member) ?? throw new BusRefusal("no_such_member", $"'{team}' has no member named '{member}'");
            if (_active.Values.Any(a => a.Context.Member.Id == target.Id))
                throw new BusRefusal("busy", $"{target.Name} is running; open the terminal to watch the run");
            if (HasSession(target.Id))
                throw new BusRefusal("busy", $"{target.Name} already has an interactive session");
            var row = _store.EnqueueSession(target.Id)
                ?? throw new BusRefusal("busy", $"{target.Name} is queued for a run");
            var ticket = new SessionTicket(this, target);
            _sessionWaiters[row.Id] = new SessionWaiter(target, row.Id, body, ticket);
            Log($"interactive session queued: {target.Qualified}, queue entry {row.Id}");
            Schedule();
            return ticket;
        }, ct);

    internal Task<int?> SessionPositionAsync(SessionTicket ticket) =>
        WithGate(() =>
        {
            var waiter = _sessionWaiters.Values.FirstOrDefault(w => w.Ticket == ticket);
            return waiter is null ? null : RunScheduler.PositionOf(_store.Queue(), q => q.Id == waiter.QueueId);
        }, CancellationToken.None);

    /// <summary>The person closes the window before the session was admitted: its queue entry is removed.</summary>
    internal Task WithdrawSessionAsync(SessionTicket ticket) =>
        WithGate(() =>
        {
            var waiter = _sessionWaiters.Values.FirstOrDefault(w => w.Ticket == ticket);
            if (waiter is null) return;
            Withdraw(waiter);
            Signal();
        }, CancellationToken.None);

    private void Withdraw(SessionWaiter waiter)
    {
        _sessionWaiters.Remove(waiter.QueueId);
        _store.Dequeue(waiter.QueueId);
        waiter.Ticket.Withdrawn();
    }

    private void AdmitSession(BusStore.QueueRow entry)
    {
        if (!_sessionWaiters.Remove(entry.Id, out var waiter))
        {
            // A row with no waiter in this process (cannot outlive recovery, but never let it hold the head).
            _store.Dequeue(entry.Id);
            return;
        }
        var sessionId = _store.InTransaction(() =>
        {
            _store.Dequeue(entry.Id);
            return _store.InsertSession(waiter.Member.Id, entry.Id, entry.EnqueuedAt);
        });
        var session = new SessionRun(waiter.Member, sessionId, entry.EnqueuedAt, _store.Now(), CancellationTokenSource.CreateLinkedTokenSource(_stop.Token));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Task = Task.Run(async () =>
        {
            await started.Task;
            await RunSessionAsync(session, waiter);
        });
        _sessions[waiter.Member.Id] = session;
        waiter.Ticket.Admitted();
        started.SetResult();
        Log($"interactive session {sessionId} admitted: {waiter.Member.Qualified}");
    }

    private async Task RunSessionAsync(SessionRun session, SessionWaiter waiter)
    {
        string? state;
        string? reason = null;
        try
        {
            await waiter.Body(session.Cancel.Token);
            state = session.StopRequested ? "stopped" : "ended";
            if (session.StopRequested) reason = StoppedReason;
        }
        catch (OperationCanceledException) when (_stopping)
        {
            state = null;
        }
        catch (OperationCanceledException) when (session.StopRequested)
        {
            (state, reason) = ("stopped", StoppedReason);
        }
        catch (Exception ex)
        {
            (state, reason) = ("failed", "the session failed: " + ex.Message);
        }
        await _gate.WaitAsync();
        try
        {
            _sessions.Remove(session.Member.Id);
            // Core stopping: left 'running'; the next start reports it interrupted.
            if (!_stopping && state is not null) _store.EndSession(session.SessionId, state, reason);
            Signal();
        }
        finally
        {
            session.Cancel.Dispose();
            _gate.Release();
            waiter.Ticket.Ended();
        }
    }

    /// <summary>Restart: queued sessions are dropped (their window is gone); open sessions are interrupted.</summary>
    private void RecoverSessions()
    {
        var dropped = _store.DropQueuedSessions();
        if (dropped > 0) Log($"{dropped} queued interactive session(s) dropped: the core restarted");
        foreach (var session in _store.Sessions("running"))
        {
            _store.EndSession(session.Id, "interrupted", "interrupted: the core stopped while this session was open");
            Log($"interactive session {session.Id} marked interrupted");
        }
    }

    private Task[] StopSessionsForShutdown()
    {
        foreach (var session in _sessions.Values) session.Cancel.Cancel();
        foreach (var waiter in _sessionWaiters.Values.ToArray())
        {
            _sessionWaiters.Remove(waiter.QueueId);
            waiter.Ticket.Withdrawn(); // the row stays; recovery drops it
        }
        return [.. _sessions.Values.Select(s => s.Task)];
    }

    // ------------------------------------------------------------------ Stop, and limit changes

    /// <summary>
    /// The card's Stop: ends the member's sandbox, headless run or interactive session, and gives its slot to
    /// the next queued entry. The run is recorded failed with failure class "stopped". A session still waiting for a
    /// slot is withdrawn. False when the member holds no slot and no queued session.
    /// </summary>
    public Task<bool> StopMemberAsync(long memberId, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var run = _active.Values.FirstOrDefault(a => a.Context.Member.Id == memberId);
            if (run is not null)
            {
                _stopRequested[run.Context.RunId] = true;
                run.Cancel.Cancel();
                Log($"run {run.Context.RunId} of {run.Context.Member.Qualified}: stop requested");
                return true;
            }
            if (_sessions.TryGetValue(memberId, out var session))
            {
                session.StopRequested = true;
                session.Cancel.Cancel();
                Log($"interactive session {session.SessionId} of {session.Member.Qualified}: stop requested");
                return true;
            }
            if (_sessionWaiters.Values.FirstOrDefault(w => w.Member.Id == memberId) is { } waiter)
            {
                Withdraw(waiter);
                Signal();
                return true;
            }
            return false;
        }, ct);

    public async Task<bool> StopMemberAsync(string team, string member, CancellationToken ct = default)
    {
        var target = await WithGate(() => _store.Member(team, member), ct)
            ?? throw new BusRefusal("no_such_member", $"'{team}' has no member named '{member}'");
        return await StopMemberAsync(target.Id, ct);
    }

    /// <summary>
    /// Re-runs admission now. Call it after app.maxConcurrentAgents changes: a raised limit admits the next queued
    /// entries at once; a lowered one only stops further admissions.
    /// </summary>
    public void Reschedule()
    {
        HeavySettingChanged(); // a saved policy may change app.heavyWorkAtOnce too
        Signal();
    }

    // ------------------------------------------------------------------ what the UI shows

    /// <summary>
    /// The global scheduler state: the header's running / limit, every live sandbox with how long it waited, the
    /// queue with each entry's "Queued · position N of M" and wait so far, and per-team running and queued counts
    /// with the wait each team suffered in its run history.
    /// </summary>
    public Task<SchedulerSnapshot> SchedulerSnapshotAsync(CancellationToken ct = default) =>
        WithGate(() =>
        {
            var now = Parse(_store.Now());
            var limit = _options.MaxConcurrentAgents();
            var queue = _store.Queue();
            var members = _store.AllMembers().ToDictionary(m => m.Id);

            var live = new List<SlotHolder>();
            var runRows = _store.Runs().ToDictionary(r => r.Id);
            foreach (var a in _active.Values.OrderBy(a => a.Context.RunId))
            {
                var row = runRows.GetValueOrDefault(a.Context.RunId);
                var queuedAt = row?.QueuedAt ?? "";
                var startedAt = row?.StartedAt ?? "";
                live.Add(new SlotHolder(a.Context.Member.Team, a.Context.Member.Name, QueueKinds.Run, a.Context.RunId, null,
                    queuedAt, startedAt, Ms(queuedAt, startedAt)));
            }
            foreach (var s in _sessions.Values.OrderBy(s => s.SessionId))
            {
                live.Add(new SlotHolder(s.Member.Team, s.Member.Name, QueueKinds.Interactive, null, s.SessionId,
                    s.QueuedAt, s.StartedAt, Ms(s.QueuedAt, s.StartedAt)));
            }

            var queued = queue.Select((q, i) =>
            {
                var m = members.GetValueOrDefault(q.MemberId);
                return new QueuedEntry(i + 1, queue.Count, m?.Team ?? "", m?.Name ?? "", q.Kind, q.EnqueuedAt,
                    (long)Math.Max(0, (now - Parse(q.EnqueuedAt)).TotalMilliseconds),
                    SchedulerSnapshot.QueuedLabel(q.Kind, i + 1, queue.Count));
            }).ToArray();

            var waits = runRows.Values.Select(r => (MemberId: r.MemberId, Wait: Ms(r.QueuedAt, r.StartedAt)))
                .Concat(_store.Sessions().Select(s => (MemberId: s.MemberId, Wait: Ms(s.QueuedAt, s.StartedAt))))
                .Where(w => members.ContainsKey(w.MemberId))
                .GroupBy(w => members[w.MemberId].Team, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(w => w.Wait).ToArray(), StringComparer.OrdinalIgnoreCase);
            var teams = members.Values.Select(m => m.Team).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(team =>
                {
                    var w = waits.GetValueOrDefault(team) ?? [];
                    return new TeamLoad(team,
                        live.Count(l => string.Equals(l.Team, team, StringComparison.OrdinalIgnoreCase)),
                        queued.Count(q => string.Equals(q.Team, team, StringComparison.OrdinalIgnoreCase)),
                        w.Length, w.Sum(), w.Length == 0 ? 0 : w.Max());
                }).ToArray();

            return new SchedulerSnapshot(live.Count, limit, queued.Length, live, queued, teams);
        }, ct);

    private static DateTimeOffset Parse(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static long Ms(string from, string to) =>
        from.Length == 0 || to.Length == 0 ? 0 : (long)Math.Max(0, (Parse(to) - Parse(from)).TotalMilliseconds);
}
