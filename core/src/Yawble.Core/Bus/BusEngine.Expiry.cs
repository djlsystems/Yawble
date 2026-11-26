using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Bus;

/// <summary>The one warning before a deliverable that waits on the person's answer expires.</summary>
/// <param name="Text">"#&lt;id&gt; will expire tomorrow; it waits on your answer to question &lt;n&gt;".</param>
public sealed record DeliverableExpiryWarning(long TeamId, string Team, long Deliverable, long Question, DateTimeOffset ExpiresAt, string Text);

/// <summary>
/// Deliverable expiry: an open deliverable with no activity for the team's deliverableExpiryDays (default 3) is closed by the
/// app as "expired", so nothing stays open forever. Activity is any message in it (an instruction, a run's report, a
/// hand-back, a status change, the person's answer) and the person's Extend. Never idle, so never expired: a run working it
/// or queued for it, a wake (wake_me) or an automatic resume due in it. One that waits on the person's answer to a question
/// is warned first: at N-1 days an Update and a Needs-you line say it will expire tomorrow, and it expires no sooner than a
/// day after that warning. The expiry is a message on the log (deliverable.expired): new activity reopens it like any end.
/// </summary>
public sealed partial class BusEngine
{
    public const int DefaultDeliverableExpiryDays = 3, MinDeliverableExpiryDays = 1, MaxDeliverableExpiryDays = 365;

    /// <summary>How often the pump looks for deliverables to warn about or expire.</summary>
    public static readonly TimeSpan ExpirySweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>The least notice a deliverable waiting on the person gets between its warning and its expiry.</summary>
    public static readonly TimeSpan ExpiryNotice = TimeSpan.FromDays(1);

    /// <summary>The warning's message type: on the log for the record and so open views refresh; not activity.</summary>
    public const string DeliverableExpiring = "deliverable.expiring";

    /// <summary>
    /// Called (under the gate: it must not call back into the bus) once per warning, after it is recorded: the core posts
    /// it as a Concierge Update.
    /// </summary>
    public Action<DeliverableExpiryWarning>? ExpiryWarned { get; set; }

    private DateTimeOffset _nextExpirySweep = DateTimeOffset.MinValue;

    /// <summary>
    /// An open deliverable nothing works or is due to work, and when it expires. <paramref name="Question"/>: the question it
    /// waits on the person's answer to (warned first); <paramref name="Warned"/>: that warning was given for this idle stretch.
    /// </summary>
    private sealed record IdleDeliverable(DateTimeOffset LastActivity, DateTimeOffset IdleSince, int Days, DateTimeOffset ExpiresAt,
        Message? Question, DateTimeOffset? WarnAt, bool Warned);

    /// <summary>The team's deliverableExpiryDays, within 1-365.</summary>
    private int ExpiryDaysOf(long teamId) =>
        Math.Clamp(_store.DeliverableExpiryDays(teamId) ?? DefaultDeliverableExpiryDays, MinDeliverableExpiryDays, MaxDeliverableExpiryDays);

    /// <summary>The team's deliverableExpiryDays now in force (the default 3 when never set).</summary>
    public Task<int> DeliverableExpiryDaysAsync(string team, CancellationToken ct = default) =>
        WithGate(() => ExpiryDaysOf(TeamId(team)), ct);

    /// <summary>Sets the team's deliverableExpiryDays (1-365); null goes back to the default. Returns the value now in force.</summary>
    public Task<int> SetDeliverableExpiryDaysAsync(string team, int? days, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var teamId = TeamId(team);
            if (days is { } d && d is < MinDeliverableExpiryDays or > MaxDeliverableExpiryDays)
                throw new BusRefusal("bad_days", $"deliverableExpiryDays must be {MinDeliverableExpiryDays} to {MaxDeliverableExpiryDays} days");
            _store.SetDeliverableExpiryDays(teamId, days);
            Log($"team {team}: deliverables expire after {ExpiryDaysOf(teamId)} day(s) without activity");
            return ExpiryDaysOf(teamId);
        }, ct);

    /// <summary>
    /// Extend: the person resets an open deliverable's idle clock (it expires deliverableExpiryDays from now, and a deliverable
    /// waiting on their answer is warned again a day before). An ended one is refused: telling its team something reopens it.
    /// </summary>
    public Task<DeliverableSummary> ExtendDeliverableAsync(string team, long correlation, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var teamId = TeamId(team);
            EnsureTeamDeliverable(teamId, correlation);
            if (_store.DeliverableEnded(correlation))
                throw new BusRefusal("already_ended", "this deliverable has ended; telling its team something in it reopens it");
            var now = _store.Clock.GetUtcNow();
            _store.SetDeliverableExtended(correlation, now);
            Log($"deliverable {correlation} of {team}: extended by the person");
            var row = _store.DeliverablesOf(team, -1).First(w => w.Id == correlation);
            return Summary(teamId, team, row, _store.Pending(), now);
        }, ct);

    /// <summary>The name of the team whose deliverable this is; null when there is no such deliverable.</summary>
    public Task<string?> DeliverableTeamAsync(long correlation, CancellationToken ct = default) =>
        WithGate(() => TeamOf(correlation) is { } id ? _store.Teams().FirstOrDefault(t => t.Id == id)?.Name : null, ct);

    /// <summary>The expiry warnings in force for a team's deliverables, by deliverable (the Needs-you line on the question).</summary>
    public Task<IReadOnlyDictionary<long, DeliverableExpiryWarning>> ExpiryWarningsAsync(string team, CancellationToken ct = default) =>
        WithGate<IReadOnlyDictionary<long, DeliverableExpiryWarning>>(() =>
        {
            var teamId = TeamId(team);
            var now = _store.Clock.GetUtcNow();
            var pending = _store.Pending();
            var result = new Dictionary<long, DeliverableExpiryWarning>();
            foreach (var w in _store.OpenDecisions(team).Select(d => d.CorrelationId).Distinct())
            {
                if (_store.DeliverableEnded(w)) continue;
                if (IdleOf(teamId, w, _store.Deliverable(w), pending, now) is { Warned: true, Question: { } q } idle)
                    result[w] = new DeliverableExpiryWarning(teamId, team, w, q.Seq, idle.ExpiresAt, WarningText(w, q.Seq));
            }
            return result;
        }, ct);

    public static string WarningText(long deliverable, long question) =>
        $"#{deliverable} will expire tomorrow; it waits on your answer to question {question}";

    /// <summary>Warns and expires as of <paramref name="now"/> (tests call it with their own clock). The number expired.</summary>
    public Task<int> SweepExpiryAsync(DateTimeOffset now, CancellationToken ct = default) => WithGate(() => SweepExpiry(now), ct);

    /// <summary>Called by the pump pass (under the gate), at most every <see cref="ExpirySweepInterval"/>.</summary>
    private void SweepExpiryIfDue()
    {
        var wall = DateTimeOffset.UtcNow;
        if (wall < _nextExpirySweep) return;
        _nextExpirySweep = wall + ExpirySweepInterval;
        try { SweepExpiry(_store.Clock.GetUtcNow()); }
        catch (Exception ex)
        {
            Logs.Diag.Warning("bus", $"deliverable expiry sweep failed: {ex.GetType().Name}: {ex.Message}",
                ("check", "deliverable expiry sweep"), ("exception", ex.GetType().Name));
        }
    }

    private int SweepExpiry(DateTimeOffset now)
    {
        var expired = 0;
        var pending = _store.Pending();
        foreach (var team in _store.Teams())
        {
            var days = ExpiryDaysOf(team.Id);
            // Nothing can be due in a deliverable whose newest message is newer than this: a warning comes at N-1 days of
            // quiet, and an expiry no sooner than a day after the warning (whose own message is the newest then).
            var quiet = TimeSpan.FromDays(Math.Min(days - 1, 1));
            foreach (var w in _store.DeliverablesOf(team.Name, -1))
            {
                if (now - w.Last < quiet) continue;
                if (_store.DeliverableEnded(w.Id)) continue;
                var thread = _store.Deliverable(w.Id);
                if (IdleOf(team.Id, w.Id, thread, pending, now) is not { } idle) continue;
                if (idle is { Question: { } q, Warned: false, WarnAt: { } warnAt } && now >= warnAt) Warn(team.Id, team.Name, w.Id, thread, idle, q, now);
                else if (now >= idle.ExpiresAt)
                {
                    Expire(team.Name, w.Id, thread, idle, now);
                    expired++;
                }
            }
        }
        return expired;
    }

    private void Warn(long teamId, string team, long correlation, IReadOnlyList<Message> thread, IdleDeliverable idle, Message question, DateTimeOffset now)
    {
        var expiresAt = Max(idle.IdleSince + TimeSpan.FromDays(idle.Days), now + ExpiryNotice);
        var text = WarningText(correlation, question.Seq);
        _store.InTransaction(() =>
        {
            _store.SetDeliverableWarned(correlation, idle.IdleSince, now, question.Seq);
            Publish(new NewMessage(DeliverableExpiring,
                JsonSerializer.Serialize(new { team, text, question = question.Seq, expiresAt = Iso(expiresAt) }),
                MessageTypes.HostSource, thread[^1].Seq));
        });
        Log($"deliverable {correlation} of {team}: warned that it expires at {Iso(expiresAt)} (waits on question {question.Seq})");
        try { ExpiryWarned?.Invoke(new DeliverableExpiryWarning(teamId, team, correlation, question.Seq, expiresAt, text)); }
        catch (Exception ex) { Log($"deliverable {correlation}: posting the expiry warning failed: {ex.Message}"); }
    }

    private void Expire(string team, long correlation, IReadOnlyList<Message> thread, IdleDeliverable idle, DateTimeOffset now)
    {
        _store.InTransaction(() =>
        {
            var cancelled = _store.CancelWakes(correlation);
            Publish(new NewMessage(MessageTypes.DeliverableExpired,
                JsonSerializer.Serialize(new
                {
                    team, reason = "expired", detail = $"no activity for {idle.Days} day{(idle.Days == 1 ? "" : "s")}",
                    // The recorded end is the last real activity, not the expiry: expiry adds no time to the deliverable.
                    endedAt = Iso(idle.LastActivity), expiredAt = Iso(now), expiryDays = idle.Days,
                    question = idle.Question?.Seq, wakesCancelled = cancelled,
                }),
                MessageTypes.HostSource, thread[^1].Seq));
        });
        Log($"deliverable {correlation} of {team}: expired (no activity since {Iso(idle.IdleSince)})");
    }

    /// <summary>
    /// The deliverable's idle state as of <paramref name="now"/>, or null when it is not idle: a run of it is active, a
    /// delivery in it is pending (queued or running), a wake is set in it, or an automatic resume is due in it.
    /// </summary>
    private IdleDeliverable? IdleOf(long teamId, long correlation, IReadOnlyList<Message> thread, IReadOnlyList<BusStore.PendingRow> pending, DateTimeOffset now) =>
        IdleOf(correlation, thread, pending, now, () => _store.OpenWakes(correlation), () => _store.DeliverableExpiry(correlation), () => ExpiryDaysOf(teamId));

    /// <summary><see cref="IdleOf(long, long, IReadOnlyList{Message}, IReadOnlyList{BusStore.PendingRow}, DateTimeOffset)"/> with its reads given (the Deliverables page reads them for every deliverable at once).</summary>
    private IdleDeliverable? IdleOf(long correlation, IReadOnlyList<Message> thread, IReadOnlyList<BusStore.PendingRow> pending, DateTimeOffset now,
        Func<int> openWakes, Func<DeliverableExpiryRow?> expiry, Func<int> expiryDays)
    {
        if (thread.Count == 0) return null;
        if (_active.Values.Any(a => a.Context.Correlation == correlation)) return null;
        if (pending.Any(p => p.Correlation == correlation)) return null;
        if (openWakes() != 0) return null;
        if (ResumeDue(thread)) return null;
        var activity = thread.Where(m => m.Type is not (MessageTypes.DeliverableExpired or DeliverableExpiring)).ToList();
        if (activity.Count == 0) return null;
        var last = activity.Max(m => m.OccurredAt);
        var row = expiry();
        var since = row?.ExtendedAt is { } ext && ext > last ? ext : last;
        var days = expiryDays();
        var expires = since + TimeSpan.FromDays(days);
        var question = OpenQuestion(thread);
        if (question is null) return new IdleDeliverable(last, since, days, expires, null, null, false);
        var warnAt = expires - ExpiryNotice;
        var warned = row is { WarnedFor: { } wf, WarnedAt: not null } && wf == since;
        // Never closed without a day's notice: an app that was not running at warning time warns first, then waits.
        expires = warned ? Max(expires, row!.WarnedAt!.Value + ExpiryNotice) : Max(expires, Max(now, warnAt) + ExpiryNotice);
        return new IdleDeliverable(last, since, days, expires, question, warnAt, warned);
    }

    /// <summary>The newest question in the thread that waits on the person's answer (as BusStore.OpenDecisions reads it).</summary>
    private static Message? OpenQuestion(IReadOnlyList<Message> thread) =>
        thread.LastOrDefault(q => q.Type == MessageTypes.NeedsDecision
            && !thread.Any(a => a.Source == MessageTypes.PersonSource
                && (a.CausationSeq == q.Seq || (a.Seq > q.Seq && a.Type == MessageTypes.InstructionPrefix + q.Source))));

    /// <summary>An automatic resume after a usage limit is still to come in this deliverable (BusEngine.Resume).</summary>
    private static bool ResumeDue(IReadOnlyList<Message> thread)
    {
        var attempts = thread.Count(m => m.Payload.Contains($"\"{AutomaticResumeMarker}\":true", StringComparison.Ordinal));
        if (attempts >= MaxAutomaticResumes) return false;
        foreach (var failed in thread.Where(m => m.Type == MessageTypes.Failed))
        {
            if (Payload(failed) is not { } p || p["failureClass"]?.GetValue<string>() != FailureClasses.Rate) continue;
            if (p["run"]?["retryAfter"] is not JsonValue rv || !rv.TryGetValue<string>(out var retryText)
                || !DateTimeOffset.TryParse(retryText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) continue;
            if (thread.Any(m => m.Seq > failed.Seq && string.Equals(m.Source, failed.Source, StringComparison.OrdinalIgnoreCase))) continue;
            if (thread.Any(m => m.Payload.Contains($"\"resumeOf\":{failed.Seq}", StringComparison.Ordinal))) continue;
            return true;
        }
        return false;
    }

    /// <summary>One deliverable as the list shows it (bus.deliverables, team_deliverables, the cloud dashboard).</summary>
    private DeliverableSummary Summary(long teamId, string team, (long Id, DateTimeOffset First, DateTimeOffset Last, int Count) w,
        IReadOnlyList<BusStore.PendingRow> pending, DateTimeOffset now, bool time = true)
    {
        var manager = _store.TeamMembers(teamId).Single(m => m.Role == MemberRole.Manager);
        var root = _store.Find(w.Id);
        var owner = OwnerOf(w.Id) ?? manager;
        var thread = _store.Deliverable(w.Id);
        var ending = thread.LastOrDefault(m => MessageTypes.DeliverableEnds.Contains(m.Type));
        var ended = _store.DeliverableEnded(w.Id);
        var state = !ended || ending is null ? "open" : ending.Type switch
        {
            MessageTypes.DeliverableClosed => "closed",
            MessageTypes.DeliverableExpired => "expired",
            _ => "completed",
        };
        var busy = Busy(owner, w.Id, watcher: null);
        if (_active.Values.Any(a => a.Context.Correlation == w.Id && a.Context.Member.Id == owner.Id))
            busy.Insert(0, $"{owner.Name} is running");
        var undeclared = !ended && busy.Count == 0 && _store.OpenWakes(w.Id) == 0;
        var idle = ended ? null : IdleOf(teamId, w.Id, thread, pending, now);
        DateTimeOffset? endedAt = null, expiredAt = null;
        if (ended && ending is not null)
        {
            endedAt = state == "expired" ? Time(ending, "endedAt") ?? ending.OccurredAt : ending.OccurredAt;
            expiredAt = state == "expired" ? Time(ending, "expiredAt") ?? ending.OccurredAt : null;
        }
        var endingText = ending is null || !ended ? null : ending.Type switch
        {
            MessageTypes.DeliverableClosed => Text(ending, "reason"),
            MessageTypes.DeliverableExpired => Text(ending, "detail") ?? Text(ending, "reason"),
            _ => Text(ending, "delivered"),
        };
        return new DeliverableSummary(w.Id, TitleOf(root), owner.Name, state, undeclared, busy, endingText, w.First, w.Last, w.Count,
            endedAt, expiredAt, idle?.ExpiresAt,
            idle is { Warned: true, Question: { } q } ? WarningText(w.Id, q.Seq) : null,
            ended ? null : (idle?.Question ?? OpenQuestion(thread))?.Seq,
            time ? TimeOf(w.Id, thread, now) : null);
    }

    /// <summary>The deliverable's time from the board (null without one, or when it cannot be read: the list still answers).</summary>
    private Board.TimeSummary? TimeOf(long deliverable, IReadOnlyList<Message> thread, DateTimeOffset now)
    {
        if (Board is not { } board) return null;
        try { return board.Timeline.OfDeliverable(deliverable, thread, LiveActivity(now), now)?.Time; }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or FormatException or ArgumentException)
        {
            Log($"deliverable {deliverable}: its time could not be read: {ex.Message}");
            return null;
        }
    }

    private static DateTimeOffset? Time(Message m, string field) =>
        Text(m, field) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
