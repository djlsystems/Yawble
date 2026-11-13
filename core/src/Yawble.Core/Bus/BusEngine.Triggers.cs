using Yawble.Core.Triggers;

namespace Yawble.Core.Bus;

/// <summary>What one trigger fire did.</summary>
public sealed record TriggerFire(string Outcome, long? Seq, string? Reason);

/// <summary>
/// Triggers and wakes. The pump loop sweeps them at most every
/// <see cref="TriggerSweepInterval"/>, inside the gate, so a fire is ordered with every other state change.
///
/// - A due trigger tells its member its instruction as a NEW workflow (source "trigger:&lt;id&gt;"), unless the member
///   is busy and the trigger is "only when idle" (skipped, the next fire armed).
/// - Fires that passed while the app was not running are recorded as missed, never caught up: the trigger is armed
///   at its next occurrence from now. A once that passed fires late, once.
/// - A wake (the wake_me tool) tells the same member its note inside the workflow it asked from.
/// Event and folder triggers exist too (<c>Triggers/</c>, including <c>FolderWatch</c>).
/// </summary>
public sealed partial class BusEngine
{
    public static readonly TimeSpan TriggerSweepInterval = TimeSpan.FromSeconds(10);

    /// <summary>The longest a wake may be asked for: a week.</summary>
    public static readonly TimeSpan MaxWakeDelay = TimeSpan.FromDays(7);

    public const string TriggerSourcePrefix = "trigger:";
    public const string WakeSource = "wake";

    private DateTimeOffset _nextTriggerSweep = DateTimeOffset.MinValue;

    /// <summary>Called by the pump pass (under the gate).</summary>
    private void SweepTriggersIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextTriggerSweep) return;
        _nextTriggerSweep = now + TriggerSweepInterval;
        SweepTriggers(now);
    }

    /// <summary>Fires every due trigger and wake as of <paramref name="now"/> (tests call it with their own clock).</summary>
    public Task SweepTriggersAsync(DateTimeOffset now, CancellationToken ct = default) => WithGate(() => SweepTriggers(now), ct);

    private void SweepTriggers(DateTimeOffset now)
    {
        try { ResumeRateLimited(now); }
        catch (Exception ex) { Log($"automatic resume sweep failed: {ex.Message}"); }
        foreach (var row in _store.DueTriggers(now))
        {
            try
            {
                FireDue(row, now);
            }
            catch (Exception ex)
            {
                Log($"trigger {row.Id} ({row.Name}) failed: {ex.Message}");
            }
        }
        foreach (var wake in _store.DueWakes(now))
        {
            try
            {
                var member = _store.Member(wake.MemberId);
                if (member is null || _store.Find(wake.CausationSeq) is null) continue;
                var text = $"Wake-up you asked for earlier (wake_me): {wake.Note}";
                var seq = _store.InTransaction(() =>
                {
                    var m = Publish(new NewMessage(MessageTypes.Instruction(member), BatchText.InstructionPayload(text), WakeSource, wake.CausationSeq));
                    _store.MarkWakeFired(wake.Id, m.Seq);
                    return m.Seq;
                });
                Log($"{member.Qualified}: woken (wake {wake.Id}) as message {seq}");
            }
            catch (Exception ex)
            {
                Log($"wake {wake.Id} failed: {ex.Message}");
            }
        }
    }

    private void FireDue(TriggerRow row, DateTimeOffset now)
    {
        if (row.Schedule.Kind == TriggerKinds.Folder)
        {
            LookAtFolder(row, now);
            return;
        }
        var due = row.NextDueAt!.Value;
        var next = TriggerScheduling.NextOccurrence(row.Schedule, due);
        // More than one occurrence passed (the app was not running): record them as missed, never catch up.
        if (row.Schedule.Kind != TriggerKinds.Once && next is { } n && n <= now)
        {
            var missed = 0;
            for (var t = due; t <= now && missed < 100_000; t = TriggerScheduling.NextOccurrence(row.Schedule, t) ?? DateTimeOffset.MaxValue) missed++;
            var future = TriggerScheduling.NextOccurrence(row.Schedule, now);
            _store.RecordTriggerOutcome(row.Id, null, future, "missed", $"{missed} fire(s) passed while the app was not running", null, row.MissedCount + missed);
            Log($"trigger {row.Id} ({row.Name}): {missed} missed fire(s); next {future:O}");
            return;
        }
        var fire = Fire(row, now);
        _store.RecordTriggerOutcome(row.Id, fire.Outcome == "fired" ? now : null, next, fire.Outcome, fire.Reason, fire.Seq, row.MissedCount);
    }

    /// <summary>
    /// One look at a folder trigger's folder: remember it, wait for a change to settle, and tell the member what changed
    /// when one has (through the same checks as any fire: paused team, only when idle).
    /// </summary>
    private void LookAtFolder(TriggerRow row, DateTimeOffset now)
    {
        var next = TriggerScheduling.NextOccurrence(row.Schedule, now);
        var member = _store.Member(row.MemberId);
        var folder = member is null || row.Watch is null ? null : WatchedFolder(row.Watch, member);
        if (folder is null || !Directory.Exists(folder))
        {
            _store.RecordTriggerOutcome(row.Id, null, next, "skipped", folder is null ? "nothing to watch" : $"the folder {folder} is not there", null, row.MissedCount);
            return;
        }
        var (change, state) = FolderWatcher.Look(FolderWatcher.Listing(folder, row.Watch!.Glob), _store.WatchState(row.Id), now, row.Watch.QuietSec);
        if (change is null)
        {
            _store.SetWatch(row.Id, state, next);
            return;
        }
        var fire = Fire(row, now, change.Describe(row.Watch.Root == FolderWatch.Documents ? $"the team's documents{(row.Watch.Path.Length > 0 ? "/" + row.Watch.Path : "")}" : folder));
        // Only a change that was told is taken as the new baseline: a skipped one (busy, paused) fires at the next look.
        if (fire.Outcome == "fired") _store.SetWatchState(row.Id, state);
        _store.RecordTriggerOutcome(row.Id, fire.Outcome == "fired" ? now : null, next, fire.Outcome, fire.Reason, fire.Seq, row.MissedCount);
    }

    /// <summary>The folder a watch means: inside the team's documents, or the absolute folder on this computer.</summary>
    private string? WatchedFolder(FolderWatch w, MemberRef member)
    {
        if (w.Root == FolderWatch.Host) return Path.IsPathFullyQualified(w.Path) ? w.Path : null;
        if (Repos is null) return null;
        var team = _store.Teams().FirstOrDefault(t => t.Id == member.TeamId)?.Name;
        if (team is null) return null;
        var root = Path.Combine(Repos.TeamRoot(team), Teams.TeamDocuments.Folder);
        Directory.CreateDirectory(root);
        try { return Teams.TeamDocuments.Resolve(root, w.Path, mustExist: false); }
        catch (Teams.DocumentsException) { return null; }
    }

    private TriggerFire Fire(TriggerRow row, DateTimeOffset now, string? what = null)
    {
        var member = _store.Member(row.MemberId);
        if (member is null) return new TriggerFire("member-missing", null, "the member no longer exists");
        if (_store.Teams().FirstOrDefault(t => t.Id == member.TeamId) is { } team && (team.Paused || team.Archived))
            return new TriggerFire("skipped", null, team.Archived ? "the team is archived" : "the team is paused");
        if (row.OnlyWhenIdle && IsBusy(member)) return new TriggerFire("skipped", null, $"{member.Name} was busy (only when idle)");
        var text = what is null
            ? $"Scheduled work (trigger \"{row.Name}\", {now:yyyy-MM-dd HH:mm} UTC): {row.Instruction}"
            : row.Schedule.Kind == TriggerKinds.Event
                ? $"Event (trigger \"{row.Name}\", {now:yyyy-MM-dd HH:mm} UTC): {row.Instruction}\n\n{what}"
                : $"Folder change (trigger \"{row.Name}\", {now:yyyy-MM-dd HH:mm} UTC): {row.Instruction}\n\n{what}";
        var message = Publish(new NewMessage(MessageTypes.Instruction(member), WakeManagerPolicy.InstructionPayload(text, row.WakeManager),
            TriggerSourcePrefix + row.Id, null));
        Log($"trigger {row.Id} ({row.Name}) told {member.Qualified} as message {message.Seq}");
        return new TriggerFire("fired", message.Seq, null);
    }

    /// <summary>Running, in a terminal session, queued for a slot, or with undelivered work.</summary>
    private bool IsBusy(MemberRef m) =>
        _active.Values.Any(a => a.Context.Member.Id == m.Id) || _sessions.ContainsKey(m.Id)
        || _store.Queue().Any(q => q.MemberId == m.Id)
        || _store.Pending().Any(p => string.Equals(p.Subscriber, m.Qualified, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ the person's operations (the Triggers dialog)

    public Task<IReadOnlyList<TriggerRow>> TriggersAsync(long teamId, CancellationToken ct = default) =>
        WithGate(() => _store.Triggers(teamId), ct);

    /// <summary>Creates or edits a trigger; any edit re-arms it from now. Throws ArgumentException with the reason.</summary>
    public Task<TriggerRow> SaveTriggerAsync(TriggerDraft draft, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var member = _store.Member(draft.MemberId) ?? throw new ArgumentException("no such member");
            if (string.IsNullOrWhiteSpace(draft.Name)) throw new ArgumentException("a trigger needs a name");
            if (string.IsNullOrWhiteSpace(draft.Instruction)) throw new ArgumentException("a trigger needs an instruction");
            if (draft.Id is { } id && _store.Trigger(id) is null) throw new ArgumentException("no such trigger");
            if (!WakeManagerPolicy.IsValid(draft.WakeManager))
                throw new ArgumentException($"wakeManager must be one of {string.Join(", ", WakeManagerPolicy.All)}");
            var now = DateTimeOffset.UtcNow;
            if (TriggerScheduling.Validate(draft.Schedule, draft.Schedule.Kind == TriggerKinds.Once ? now : null) is { } why) throw new ArgumentException(why);
            if (draft.Schedule.Kind == TriggerKinds.Folder) draft = draft with { Watch = CheckWatch(draft.Watch) };
            else draft = draft with { Watch = null };
            if (draft.Schedule.Kind == TriggerKinds.Event)
            {
                var ev = (draft.EventName ?? "").Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(ev, @"^[A-Za-z0-9._*-]{1,100}$"))
                    throw new ArgumentException("an event trigger needs the event's name (letters, digits, . _ - and * for any), e.g. web.pr");
                draft = draft with { EventName = ev };
            }
            else draft = draft with { EventName = null };
            var next = draft.Enabled ? TriggerScheduling.NextOccurrence(draft.Schedule, now) : null;
            var saved = _store.SaveTrigger(draft with { Name = draft.Name.Trim(), Instruction = draft.Instruction.Trim() }, member.TeamId, next);
            return _store.Trigger(saved)!;
        }, ct);

    private static FolderWatch CheckWatch(FolderWatch? w)
    {
        if (w is null) throw new ArgumentException("a folder trigger needs a folder to watch");
        var path = (w.Path ?? "").Trim();
        if (w.Root == FolderWatch.Host)
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("a folder on this computer needs its full path");
            if (!Directory.Exists(path)) throw new ArgumentException($"the folder {path} is not there");
        }
        else if (w.Root == FolderWatch.Documents)
        {
            path = path.Replace('\\', '/').Trim('/');
            if (path.Split('/').Any(p => p is ".." or ".") || path.Contains(':')) throw new ArgumentException("the folder must be inside the team's documents");
        }
        else
        {
            throw new ArgumentException("watch root is documents or host");
        }
        if (w.QuietSec is < 0 or > 3600) throw new ArgumentException("the quiet period is 0 to 3600 seconds");
        return w with { Path = path };
    }

    public Task<bool> DeleteTriggerAsync(long id, CancellationToken ct = default) => WithGate(() => _store.DeleteTrigger(id), ct);

    /// <summary>Fires a trigger once, now, through the same checks; its next scheduled fire does not move (Run now).</summary>
    public Task<TriggerFire> RunTriggerNowAsync(long id, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var row = _store.Trigger(id) ?? throw new ArgumentException("no such trigger");
            var now = DateTimeOffset.UtcNow;
            var fire = Fire(row, now);
            _store.RecordTriggerOutcome(row.Id, fire.Outcome == "fired" ? now : null, row.NextDueAt, fire.Outcome, fire.Reason, fire.Seq, row.MissedCount);
            return fire;
        }, ct);

    // ------------------------------------------------------------------ wake_me (a member's tool)

    internal Task<DateTimeOffset> WakeMeAsync(RunContext run, int minutes, string note, CancellationToken ct) =>
        WithGate(() =>
        {
            EnsureActive(run);
            if (string.IsNullOrWhiteSpace(note)) throw new BusRefusal("empty", "say what to look at when you are woken");
            if (minutes < 1 || TimeSpan.FromMinutes(minutes) > MaxWakeDelay)
                throw new BusRefusal("out_of_range", $"minutes must be between 1 and {(int)MaxWakeDelay.TotalMinutes}");
            var due = DateTimeOffset.UtcNow.AddMinutes(minutes);
            _store.AddWake(run.Member.Id, run.Causation, due, note.Trim());
            return due;
        }, ct);
}
