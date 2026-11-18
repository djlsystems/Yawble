using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Bus;

/// <summary>The one warning before a workflow that waits on the person's answer expires (B085).</summary>
/// <param name="Text">"#&lt;id&gt; will expire tomorrow; it waits on your answer to question &lt;n&gt;".</param>
public sealed record WorkflowExpiryWarning(long TeamId, string Team, long Workflow, long Question, DateTimeOffset ExpiresAt, string Text);

/// <summary>
/// Workflow expiry (B085): an open workflow with no activity for the team's workflowExpiryDays (default 3) is closed by the
/// app as "expired", so nothing stays open forever. Activity is any message in it (an instruction, a run's report, a
/// hand-back, a status change, the person's answer) and the person's Extend. Never idle, so never expired: a run working it
/// or queued for it, a wake (wake_me) or an automatic resume due in it. One that waits on the person's answer to a question
/// is warned first: at N-1 days an Update and a Needs-you line say it will expire tomorrow, and it expires no sooner than a
/// day after that warning. The expiry is a message on the log (workflow.expired): new activity reopens it like any end.
/// </summary>
public sealed partial class BusEngine
{
    public const int DefaultWorkflowExpiryDays = 3, MinWorkflowExpiryDays = 1, MaxWorkflowExpiryDays = 365;

    /// <summary>How often the pump looks for workflows to warn about or expire.</summary>
    public static readonly TimeSpan ExpirySweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>The least notice a workflow waiting on the person gets between its warning and its expiry.</summary>
    public static readonly TimeSpan ExpiryNotice = TimeSpan.FromDays(1);

    /// <summary>The warning's message type: on the log for the record and so open views refresh; not activity.</summary>
    public const string WorkflowExpiring = "workflow.expiring";

    /// <summary>
    /// Called (under the gate: it must not call back into the bus) once per warning, after it is recorded: the core posts
    /// it as a Concierge Update.
    /// </summary>
    public Action<WorkflowExpiryWarning>? ExpiryWarned { get; set; }

    private DateTimeOffset _nextExpirySweep = DateTimeOffset.MinValue;

    /// <summary>
    /// An open workflow nothing works or is due to work, and when it expires. <paramref name="Question"/>: the question it
    /// waits on the person's answer to (warned first); <paramref name="Warned"/>: that warning was given for this idle stretch.
    /// </summary>
    private sealed record IdleWorkflow(DateTimeOffset LastActivity, DateTimeOffset IdleSince, int Days, DateTimeOffset ExpiresAt,
        Message? Question, DateTimeOffset? WarnAt, bool Warned);

    /// <summary>The team's workflowExpiryDays, within 1-365.</summary>
    private int ExpiryDaysOf(long teamId) =>
        Math.Clamp(_store.WorkflowExpiryDays(teamId) ?? DefaultWorkflowExpiryDays, MinWorkflowExpiryDays, MaxWorkflowExpiryDays);

    /// <summary>The team's workflowExpiryDays now in force (the default 3 when never set).</summary>
    public Task<int> WorkflowExpiryDaysAsync(string team, CancellationToken ct = default) =>
        WithGate(() => ExpiryDaysOf(TeamId(team)), ct);

    /// <summary>Sets the team's workflowExpiryDays (1-365); null goes back to the default. Returns the value now in force.</summary>
    public Task<int> SetWorkflowExpiryDaysAsync(string team, int? days, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var teamId = TeamId(team);
            if (days is { } d && d is < MinWorkflowExpiryDays or > MaxWorkflowExpiryDays)
                throw new BusRefusal("bad_days", $"workflowExpiryDays must be {MinWorkflowExpiryDays} to {MaxWorkflowExpiryDays} days");
            _store.SetWorkflowExpiryDays(teamId, days);
            Log($"team {team}: workflows expire after {ExpiryDaysOf(teamId)} day(s) without activity");
            return ExpiryDaysOf(teamId);
        }, ct);

    /// <summary>
    /// Extend: the person resets an open workflow's idle clock (it expires workflowExpiryDays from now, and a workflow
    /// waiting on their answer is warned again a day before). An ended one is refused: telling its team something reopens it.
    /// </summary>
    public Task<WorkflowSummary> ExtendWorkflowAsync(string team, long correlation, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var teamId = TeamId(team);
            EnsureTeamWorkflow(teamId, correlation);
            if (_store.WorkflowEnded(correlation))
                throw new BusRefusal("already_ended", "this workflow has ended; telling its team something in it reopens it");
            var now = _store.Clock.GetUtcNow();
            _store.SetWorkflowExtended(correlation, now);
            Log($"workflow {correlation} of {team}: extended by the person");
            var row = _store.WorkflowsOf(team, -1).First(w => w.Id == correlation);
            return Summary(teamId, team, row, _store.Pending(), now);
        }, ct);

    /// <summary>The name of the team whose workflow this is; null when there is no such workflow.</summary>
    public Task<string?> WorkflowTeamAsync(long correlation, CancellationToken ct = default) =>
        WithGate(() => TeamOf(correlation) is { } id ? _store.Teams().FirstOrDefault(t => t.Id == id)?.Name : null, ct);

    /// <summary>The expiry warnings in force for a team's workflows, by workflow (the Needs-you line on the question).</summary>
    public Task<IReadOnlyDictionary<long, WorkflowExpiryWarning>> ExpiryWarningsAsync(string team, CancellationToken ct = default) =>
        WithGate<IReadOnlyDictionary<long, WorkflowExpiryWarning>>(() =>
        {
            var teamId = TeamId(team);
            var now = _store.Clock.GetUtcNow();
            var pending = _store.Pending();
            var result = new Dictionary<long, WorkflowExpiryWarning>();
            foreach (var w in _store.OpenDecisions(team).Select(d => d.CorrelationId).Distinct())
            {
                if (_store.WorkflowEnded(w)) continue;
                if (IdleOf(teamId, w, _store.Workflow(w), pending, now) is { Warned: true, Question: { } q } idle)
                    result[w] = new WorkflowExpiryWarning(teamId, team, w, q.Seq, idle.ExpiresAt, WarningText(w, q.Seq));
            }
            return result;
        }, ct);

    public static string WarningText(long workflow, long question) =>
        $"#{workflow} will expire tomorrow; it waits on your answer to question {question}";

    /// <summary>Warns and expires as of <paramref name="now"/> (tests call it with their own clock). The number expired.</summary>
    public Task<int> SweepExpiryAsync(DateTimeOffset now, CancellationToken ct = default) => WithGate(() => SweepExpiry(now), ct);

    /// <summary>Called by the pump pass (under the gate), at most every <see cref="ExpirySweepInterval"/>.</summary>
    private void SweepExpiryIfDue()
    {
        var wall = DateTimeOffset.UtcNow;
        if (wall < _nextExpirySweep) return;
        _nextExpirySweep = wall + ExpirySweepInterval;
        try { SweepExpiry(_store.Clock.GetUtcNow()); }
        catch (Exception ex) { Log($"workflow expiry sweep failed: {ex.Message}"); }
    }

    private int SweepExpiry(DateTimeOffset now)
    {
        var expired = 0;
        var pending = _store.Pending();
        foreach (var team in _store.Teams())
        {
            var days = ExpiryDaysOf(team.Id);
            // Nothing can be due in a workflow whose newest message is newer than this: a warning comes at N-1 days of
            // quiet, and an expiry no sooner than a day after the warning (whose own message is the newest then).
            var quiet = TimeSpan.FromDays(Math.Min(days - 1, 1));
            foreach (var w in _store.WorkflowsOf(team.Name, -1))
            {
                if (now - w.Last < quiet) continue;
                if (_store.WorkflowEnded(w.Id)) continue;
                var thread = _store.Workflow(w.Id);
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

    private void Warn(long teamId, string team, long correlation, IReadOnlyList<Message> thread, IdleWorkflow idle, Message question, DateTimeOffset now)
    {
        var expiresAt = Max(idle.IdleSince + TimeSpan.FromDays(idle.Days), now + ExpiryNotice);
        var text = WarningText(correlation, question.Seq);
        _store.InTransaction(() =>
        {
            _store.SetWorkflowWarned(correlation, idle.IdleSince, now, question.Seq);
            Publish(new NewMessage(WorkflowExpiring,
                JsonSerializer.Serialize(new { team, text, question = question.Seq, expiresAt = Iso(expiresAt) }),
                MessageTypes.HostSource, thread[^1].Seq));
        });
        Log($"workflow {correlation} of {team}: warned that it expires at {Iso(expiresAt)} (waits on question {question.Seq})");
        try { ExpiryWarned?.Invoke(new WorkflowExpiryWarning(teamId, team, correlation, question.Seq, expiresAt, text)); }
        catch (Exception ex) { Log($"workflow {correlation}: posting the expiry warning failed: {ex.Message}"); }
    }

    private void Expire(string team, long correlation, IReadOnlyList<Message> thread, IdleWorkflow idle, DateTimeOffset now)
    {
        _store.InTransaction(() =>
        {
            var cancelled = _store.CancelWakes(correlation);
            Publish(new NewMessage(MessageTypes.WorkflowExpired,
                JsonSerializer.Serialize(new
                {
                    team, reason = "expired", detail = $"no activity for {idle.Days} day{(idle.Days == 1 ? "" : "s")}",
                    // The recorded end is the last real activity, not the expiry: expiry adds no time to the workflow.
                    endedAt = Iso(idle.LastActivity), expiredAt = Iso(now), expiryDays = idle.Days,
                    question = idle.Question?.Seq, wakesCancelled = cancelled,
                }),
                MessageTypes.HostSource, thread[^1].Seq));
        });
        Log($"workflow {correlation} of {team}: expired (no activity since {Iso(idle.IdleSince)})");
    }

    /// <summary>
    /// The workflow's idle state as of <paramref name="now"/>, or null when it is not idle: a run of it is active, a
    /// delivery in it is pending (queued or running), a wake is set in it, or an automatic resume is due in it.
    /// </summary>
    private IdleWorkflow? IdleOf(long teamId, long correlation, IReadOnlyList<Message> thread, IReadOnlyList<BusStore.PendingRow> pending, DateTimeOffset now)
    {
        if (thread.Count == 0) return null;
        if (_active.Values.Any(a => a.Context.Correlation == correlation)) return null;
        if (pending.Any(p => p.Correlation == correlation)) return null;
        if (_store.OpenWakes(correlation) != 0) return null;
        if (ResumeDue(thread)) return null;
        var activity = thread.Where(m => m.Type is not (MessageTypes.WorkflowExpired or WorkflowExpiring)).ToList();
        if (activity.Count == 0) return null;
        var last = activity.Max(m => m.OccurredAt);
        var row = _store.WorkflowExpiry(correlation);
        var since = row?.ExtendedAt is { } ext && ext > last ? ext : last;
        var days = ExpiryDaysOf(teamId);
        var expires = since + TimeSpan.FromDays(days);
        var question = OpenQuestion(thread);
        if (question is null) return new IdleWorkflow(last, since, days, expires, null, null, false);
        var warnAt = expires - ExpiryNotice;
        var warned = row is { WarnedFor: { } wf, WarnedAt: not null } && wf == since;
        // Never closed without a day's notice: an app that was not running at warning time warns first, then waits.
        expires = warned ? Max(expires, row!.WarnedAt!.Value + ExpiryNotice) : Max(expires, Max(now, warnAt) + ExpiryNotice);
        return new IdleWorkflow(last, since, days, expires, question, warnAt, warned);
    }

    /// <summary>The newest question in the thread that waits on the person's answer (as BusStore.OpenDecisions reads it).</summary>
    private static Message? OpenQuestion(IReadOnlyList<Message> thread) =>
        thread.LastOrDefault(q => q.Type == MessageTypes.NeedsDecision
            && !thread.Any(a => a.Source == MessageTypes.PersonSource
                && (a.CausationSeq == q.Seq || (a.Seq > q.Seq && a.Type == MessageTypes.InstructionPrefix + q.Source))));

    /// <summary>An automatic resume after a usage limit is still to come in this workflow (BusEngine.Resume).</summary>
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

    /// <summary>One workflow as the list shows it (bus.workflows, team_workflows, the cloud dashboard).</summary>
    private WorkflowSummary Summary(long teamId, string team, (long Id, DateTimeOffset First, DateTimeOffset Last, int Count) w,
        IReadOnlyList<BusStore.PendingRow> pending, DateTimeOffset now, bool time = true)
    {
        var manager = _store.TeamMembers(teamId).Single(m => m.Role == MemberRole.Manager);
        var root = _store.Find(w.Id);
        var owner = OwnerOf(w.Id) ?? manager;
        var thread = _store.Workflow(w.Id);
        var ending = thread.LastOrDefault(m => MessageTypes.WorkflowEnds.Contains(m.Type));
        var ended = _store.WorkflowEnded(w.Id);
        var state = !ended || ending is null ? "open" : ending.Type switch
        {
            MessageTypes.WorkflowClosed => "closed",
            MessageTypes.WorkflowExpired => "expired",
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
            MessageTypes.WorkflowClosed => Text(ending, "reason"),
            MessageTypes.WorkflowExpired => Text(ending, "detail") ?? Text(ending, "reason"),
            _ => Text(ending, "delivered"),
        };
        return new WorkflowSummary(w.Id, TitleOf(root), owner.Name, state, undeclared, busy, endingText, w.First, w.Last, w.Count,
            endedAt, expiredAt, idle?.ExpiresAt,
            idle is { Warned: true, Question: { } q } ? WarningText(w.Id, q.Seq) : null,
            ended ? null : (idle?.Question ?? OpenQuestion(thread))?.Seq,
            time ? TimeOf(w.Id, thread, now) : null);
    }

    /// <summary>B086: the workflow's time from the board (null without one, or when it cannot be read: the list still answers).</summary>
    private Board.TimeSummary? TimeOf(long workflow, IReadOnlyList<Message> thread, DateTimeOffset now)
    {
        if (Board is not { } board) return null;
        try { return board.Timeline.OfWorkflow(workflow, thread, LiveActivity(now), now)?.Time; }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or FormatException or ArgumentException)
        {
            Log($"workflow {workflow}: its time could not be read: {ex.Message}");
            return null;
        }
    }

    private static DateTimeOffset? Time(Message m, string field) =>
        Text(m, field) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
