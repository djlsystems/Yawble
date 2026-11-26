using System.Collections.Concurrent;
using System.Globalization;

namespace Yawble.Core.Bus;

/// <summary>One holder of the heavy-work lease, or one run waiting for it (Since: when it took it, or when it queued).</summary>
public sealed record LeaseEntry(string Team, string Member, string What, string Since, int? Position = null);

/// <summary>The heavy-work lease as the Monitor, the member cards and system_status show it.</summary>
public sealed record LeaseSnapshot(int Limit, IReadOnlyList<LeaseEntry> Holders, IReadOnlyList<LeaseEntry> Queue);

/// <summary>
/// The heavy-work lease: agents on one computer share its CPU and memory, so a full build, a
/// whole test suite or an image build should run a few at a time (app.heavyWorkAtOnce, default 1). A member takes the
/// lease before such a step and gives it back after.
///
/// - Busy: the caller joins an ordered queue, one entry per run (asking again keeps its place), and is answered at once
///   with its position: the tool never blocks.
/// - The lease passes to the first queued run when a holder releases, when a holder's run ends, or when a holder has had
///   it for <see cref="LeaseLimit"/>. A queued run that ends leaves the queue (and so does one that calls release).
/// - The new holder is told in its run: the reply to its next bus tool call (any tool) starts with the notice, and
///   lease status / acquire say so. A run cannot be given a bus message mid-run (deliveries start the member's NEXT
///   run), and the lease belongs to the run, so a delivery would arrive after the lease was already gone.
/// - While a run waits in the queue its idle watchdog is held (<see cref="RunContext.IdleClock"/>): waiting for the
///   lease is not being stuck. It gets a full idle window back when it is given the lease or leaves the queue.
/// The state lives in memory: runs do not survive a restart either.
/// </summary>
public sealed partial class BusEngine
{
    private sealed record LeaseHold(RunContext Run, string What, DateTimeOffset Since, LeaseUse Use);

    /// <summary>
    /// One use of the lease, for deliverable_digest: asked, given, given back. Kept in memory like the lease itself
    /// (the newest <see cref="LeaseHistoryMax"/>), so a restart forgets them.
    /// </summary>
    internal sealed class LeaseUse(long runId, long deliverable, string member, string what, DateTimeOffset askedAt)
    {
        public long RunId { get; } = runId;
        public long Deliverable { get; } = deliverable;
        public string Member { get; } = member;
        public string What { get; } = what;
        public DateTimeOffset AskedAt { get; } = askedAt;
        public DateTimeOffset? GrantedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
    }

    internal const int LeaseHistoryMax = 2000;
    private readonly List<LeaseUse> _leaseHistory = [];

    private LeaseUse NewLeaseUse(RunContext run, string what, DateTimeOffset now)
    {
        var use = new LeaseUse(run.RunId, run.Correlation, run.Member.Name, what, now);
        _leaseHistory.Add(use);
        if (_leaseHistory.Count > LeaseHistoryMax) _leaseHistory.RemoveRange(0, _leaseHistory.Count - LeaseHistoryMax);
        return use;
    }

    /// <summary>The lease's uses in one deliverable (under the gate).</summary>
    private IReadOnlyList<Teams.DigestLease> LeaseUsesOf(long deliverable) =>
        [.. _leaseHistory.Where(u => u.Deliverable == deliverable).Select(u => new Teams.DigestLease(u.Member, u.What, u.AskedAt, u.GrantedAt, u.EndedAt))];

    /// <summary>
    /// A run's waits in the lease's queue as of <paramref name="now"/> (under the gate), from asking to
    /// being given it, leaving the queue or the run's end; one still waiting runs to now. A lease given at once is no wait.
    /// The lease itself is the record, so every run has them, whatever its idle clock keeps.
    /// </summary>
    private List<ActivitySpan> LeaseWaitsOf(long runId, DateTimeOffset now)
    {
        var waits = new List<ActivitySpan>();
        foreach (var u in _leaseHistory)
        {
            if (u.RunId != runId) continue;
            var end = u.GrantedAt ?? u.EndedAt ?? now;
            if (end > u.AskedAt) waits.Add(new ActivitySpan(u.AskedAt, end));
        }
        return waits;
    }

    /// <summary>
    /// A run's stalls and lease waits for the board's time: what its idle clock keeps, with its waits in the lease's
    /// queue (<see cref="LeaseWaitsOf"/>) added to the holds and overlapping holds joined into one.
    /// </summary>
    private RunActivitySpans ActivityOf(RunContext run, DateTimeOffset now)
    {
        var spans = run.IdleClock?.Spans(now) ?? RunActivitySpans.None;
        var waits = LeaseWaitsOf(run.RunId, now);
        return waits.Count == 0 ? spans : new RunActivitySpans(spans.Stalls, JoinSpans([.. spans.Holds, .. waits]));
    }

    /// <summary>Spans in start order, those that overlap or touch joined into one.</summary>
    internal static IReadOnlyList<ActivitySpan> JoinSpans(IEnumerable<ActivitySpan> spans)
    {
        var joined = new List<ActivitySpan>();
        foreach (var s in spans.OrderBy(s => s.Start))
        {
            if (joined.Count > 0 && s.Start <= joined[^1].End)
            {
                if (s.End > joined[^1].End) joined[^1] = joined[^1] with { End = s.End };
            }
            else joined.Add(s);
        }
        return joined;
    }

    private readonly List<LeaseHold> _leaseHolders = [];
    private readonly List<LeaseHold> _leaseQueue = [];

    /// <summary>What a run must be told on its next tool call (it was given the lease, or lost it to the time limit).</summary>
    private readonly ConcurrentDictionary<long, string> _leaseNotices = new();

    /// <summary>The longest a lease is honoured without being given back; then the next in line gets it.</summary>
    public static readonly TimeSpan LeaseLimit = TimeSpan.FromMinutes(30);

    /// <summary>The range of app.heavyWorkAtOnce.</summary>
    public const int MaxHeavyAtOnce = 8;

    /// <summary>Called (under the gate) when the holders or the queue changed: the core turns it into a "lease.changed" event.</summary>
    public Action? LeaseChanged { get; set; }

    public const string LeaseChangedEvent = "lease.changed";

    // app.heavyWorkAtOnce, cached: read again after a policy save (Reschedule marks it stale) or after a minute at most,
    // never on every pump pass, poll or lease call.
    private int _heavyAtOnce;
    private long _heavyReadAt = long.MinValue;
    private static readonly long HeavyCacheMs = 60_000;

    private int HeavyAtOnce
    {
        get
        {
            var tick = Environment.TickCount64;
            if (_heavyReadAt == long.MinValue || tick - _heavyReadAt >= HeavyCacheMs)
            {
                _heavyAtOnce = Math.Clamp(_options.HeavyWorkAtOnce(), 1, MaxHeavyAtOnce);
                _heavyReadAt = tick;
            }
            return _heavyAtOnce;
        }
    }

    /// <summary>The policy changed: read app.heavyWorkAtOnce again at the next lease pass.</summary>
    private void HeavySettingChanged() => _heavyReadAt = long.MinValue;

    /// <summary>The longest step name kept (agent-facing text and the UI).</summary>
    public const int MaxWhatLength = 80;

    /// <summary>A step's name on one line: trimmed, runs of whitespace (newlines too) as one space, at most 80 characters.</summary>
    internal static string NormalizeWhat(string? what)
    {
        var one = string.Join(' ', (what ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (one.Length == 0) return "a heavy step";
        return one.Length <= MaxWhatLength ? one : one[..(MaxWhatLength - 1)].TrimEnd() + "…";
    }

    internal Task<string> LeaseAsync(RunContext run, string action, string? what, CancellationToken ct) =>
        WithGate(() =>
        {
            EnsureActive(run);
            var now = _store.Clock.GetUtcNow();
            TidyLease(now);
            switch (action)
            {
                case "acquire":
                {
                    // Whatever the run was about to be told (a lapsed lease) is out of date: this answer is the news.
                    _leaseNotices.TryRemove(run.RunId, out _);
                    if (_leaseHolders.FirstOrDefault(h => h.Run.RunId == run.RunId) is { } mine) return Granted(mine);
                    var step = NormalizeWhat(what);
                    var place = _leaseQueue.FindIndex(q => q.Run.RunId == run.RunId);
                    if (place < 0)
                    {
                        if (_leaseQueue.Count == 0 && _leaseHolders.Count < HeavyAtOnce)
                        {
                            var hold = new LeaseHold(run, step, now, NewLeaseUse(run, step, now));
                            hold.Use.GrantedAt = now;
                            _leaseHolders.Add(hold);
                            Logs.Diag.Debug("lease", $"lease: {run.Member.Qualified} holds it for \"{step}\"",
                                ("team", run.Member.Team), ("member", run.Member.Qualified), ("run", run.RunId), ("step", step));
                            LeaseChanged?.Invoke();
                            return Granted(hold);
                        }
                        _leaseQueue.Add(new LeaseHold(run, step, now, NewLeaseUse(run, step, now)));
                        place = _leaseQueue.Count - 1;
                        run.IdleClock?.Hold();
                        Logs.Diag.Debug("lease", $"lease: {run.Member.Qualified} queued for \"{step}\" at position {place + 1}",
                            ("team", run.Member.Team), ("member", run.Member.Qualified), ("run", run.RunId), ("step", step));
                        LeaseChanged?.Invoke();
                    }
                    return $"queued, position {place + 1}; {HoldersText(now)}. Keep working on something else, or check with lease status every " +
                           "minute or two; you'll also be told on your next tool call when it's yours (while queued your run is not ended for being idle). " +
                           "Your place lasts while this run does: ending your turn (wake_me) gives it up.";
                }
                case "release":
                {
                    foreach (var gone in _leaseHolders.Concat(_leaseQueue).Where(h => h.Run.RunId == run.RunId)) gone.Use.EndedAt ??= now;
                    var held = _leaseHolders.RemoveAll(h => h.Run.RunId == run.RunId);
                    var queued = _leaseQueue.RemoveAll(q => q.Run.RunId == run.RunId);
                    _leaseNotices.TryRemove(run.RunId, out _);
                    if (queued > 0) run.IdleClock?.Resume();
                    if (held + queued > 0)
                    {
                        Logs.Diag.Debug("lease", $"lease: {run.Member.Qualified} {(held > 0 ? "released it" : "left the queue")}",
                            ("team", run.Member.Team), ("member", run.Member.Qualified), ("run", run.RunId));
                        TidyLease(now);
                        LeaseChanged?.Invoke();
                    }
                    return queued > 0 ? "ok: you left the queue." : "ok: released.";
                }
                case "status":
                {
                    // A pending notice is said once, here or on the next tool call, never both.
                    var pending = _leaseNotices.TryRemove(run.RunId, out var n) ? n : null;
                    if (_leaseHolders.FirstOrDefault(h => h.Run.RunId == run.RunId) is { } mine) return Granted(mine);
                    return pending is null ? LeaseStatusText(now, run.RunId) : $"{pending} Now: {LeaseStatusText(now, run.RunId)}";
                }
                default:
                    throw new BusRefusal("bad_params", "action is acquire, release or status");
            }
        }, ct);

    private static string Granted(LeaseHold hold) =>
        $"ok: you hold the heavy-work lease for \"{hold.What}\" (since {hold.Since:HH:mm} UTC, at most {LeaseLimit.TotalMinutes:0} minutes). " +
        "Run it, then call lease with action release as soon as it finishes.";

    /// <summary>
    /// Drops holders whose run ended or whose time is up, and queue entries whose run ended, then gives free places to
    /// the queue in order. Called under the gate: by every lease call, at a run's end, and by every pump pass.
    /// </summary>
    private void TidyLease(DateTimeOffset now)
    {
        foreach (var gone in _leaseQueue.Where(q => !_active.ContainsKey(q.Run.RunId))) gone.Use.EndedAt ??= now;
        var changed = _leaseQueue.RemoveAll(q => !_active.ContainsKey(q.Run.RunId)) > 0;
        foreach (var h in _leaseHolders.ToArray())
        {
            var ended = !_active.ContainsKey(h.Run.RunId);
            if (!ended && now - h.Since <= LeaseLimit) continue;
            _leaseHolders.Remove(h);
            h.Use.EndedAt ??= now;
            changed = true;
            if (!ended)
            {
                var after = _leaseQueue.Count > 0 ? "and went to the next in line" : "and was released";
                _leaseNotices[h.Run.RunId] = $"Your heavy-work lease for \"{h.What}\" passed its {LeaseLimit.TotalMinutes:0}-minute limit {after}. " +
                                             "Take it again with lease acquire before any more heavy work.";
                // Not routine: a holder that kept the lease past its limit is said, as a warning.
                Logs.Diag.Warning("lease", $"lease: {h.Run.Member.Qualified} passed the {LeaseLimit.TotalMinutes:0}-minute limit",
                    ("team", h.Run.Member.Team), ("member", h.Run.Member.Qualified), ("run", h.Run.RunId), ("step", h.What));
            }
        }
        var limit = HeavyAtOnce;
        while (_leaseHolders.Count < limit && _leaseQueue.Count > 0)
        {
            var next = _leaseQueue[0];
            _leaseQueue.RemoveAt(0);
            _leaseHolders.Add(next with { Since = now });
            next.Use.GrantedAt = now;
            next.Run.IdleClock?.Resume(); // a full idle window from now
            _leaseNotices[next.Run.RunId] = $"You now hold the heavy-work lease for \"{next.What}\"; run it, then release.";
            Logs.Diag.Debug("lease", $"lease: handed to {next.Run.Member.Qualified} for \"{next.What}\"",
                ("team", next.Run.Member.Team), ("member", next.Run.Member.Qualified), ("run", next.Run.RunId), ("step", next.What));
            changed = true;
        }
        if (changed) LeaseChanged?.Invoke();
    }

    /// <summary>The notice a run must read on its next tool call, taken once (no gate: the gateway calls it per tool call).</summary>
    internal string? TakeLeaseNotice(long runId) => _leaseNotices.TryRemove(runId, out var notice) ? notice : null;

    private string HoldersText(DateTimeOffset now) =>
        _leaseHolders.Count == 0
            ? "nobody holds it"
            : string.Join("; ", _leaseHolders.Select(h => $"{h.Run.Member.Qualified} holds it for \"{h.What}\" since {h.Since:HH:mm} UTC ({(int)(now - h.Since).TotalMinutes} min)"));

    private string LeaseStatusText(DateTimeOffset now, long? self = null)
    {
        var limit = HeavyAtOnce;
        var head = _leaseHolders.Count == 0 ? $"free ({limit} at once)" : $"{HoldersText(now)} ({_leaseHolders.Count} of {limit} at once)";
        if (_leaseQueue.Count == 0) return head + "; nobody waiting.";
        var queue = string.Join(", ", _leaseQueue.Select((q, i) =>
            $"{i + 1}. {q.Run.Member.Qualified}{(q.Run.RunId == self ? " (you)" : "")} for \"{q.What}\" since {q.Since:HH:mm} UTC"));
        return $"{head}; waiting: {queue}.";
    }

    /// <summary>A run that ends gives back the lease it held and leaves the queue; the next in line gets it.</summary>
    private void ReleaseLeaseOf(long runId)
    {
        _leaseNotices.TryRemove(runId, out _);
        if (_leaseHolders.Any(h => h.Run.RunId == runId) || _leaseQueue.Any(q => q.Run.RunId == runId)) TidyLease(_store.Clock.GetUtcNow());
    }

    /// <summary>The holders and the queue, for the Monitor, the member cards, system_status and the Cloud Link.</summary>
    public Task<LeaseSnapshot> LeaseSnapshotAsync(CancellationToken ct = default) =>
        WithGate(() =>
        {
            TidyLease(_store.Clock.GetUtcNow());
            return LeaseSnapshot();
        }, ct);

    /// <summary>One line for system_status: "free" or who holds it and how many wait.</summary>
    public Task<string> LeaseStatusTextAsync(CancellationToken ct = default) =>
        WithGate(() =>
        {
            var now = _store.Clock.GetUtcNow();
            TidyLease(now);
            return LeaseStatusText(now);
        }, ct);

    private LeaseSnapshot LeaseSnapshot()
    {
        static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);
        return new LeaseSnapshot(HeavyAtOnce,
            [.. _leaseHolders.Select(h => new LeaseEntry(h.Run.Member.Team, h.Run.Member.Name, h.What, Iso(h.Since)))],
            [.. _leaseQueue.Select((q, i) => new LeaseEntry(q.Run.Member.Team, q.Run.Member.Name, q.What, Iso(q.Since), i + 1))]);
    }

    /// <summary>"holding" / "waiting" for a member's card (null: neither), with what for and the queue position.</summary>
    private (string State, string What, int? Position)? LeaseOf(long memberId)
    {
        if (_leaseHolders.FirstOrDefault(h => h.Run.Member.Id == memberId) is { } h) return ("holding", h.What, null);
        var i = _leaseQueue.FindIndex(q => q.Run.Member.Id == memberId);
        return i < 0 ? null : ("waiting", _leaseQueue[i].What, i + 1);
    }
}
