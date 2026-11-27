using Yawble.Core.Triggers;

namespace Yawble.Core.Bus;

/// <summary>What one trigger fire did. <paramref name="WorkflowRunId"/>: the workflow run a workflow member's fire started, or recorded as skipped.</summary>
public sealed record TriggerFire(string Outcome, long? Seq, string? Reason, long? WorkflowRunId = null);

/// <summary>
/// Triggers and wakes. The pump loop sweeps them at most every
/// <see cref="TriggerSweepInterval"/>, inside the gate, so a fire is ordered with every other state change.
/// A folder trigger's folder is listed outside the gate (a large or remote folder takes seconds): the gate is held
/// only to say which folders are due and, once they are listed, to record and fire what the listing shows.
///
/// - A due trigger tells its member its instruction as a NEW deliverable (source "trigger:&lt;id&gt;"), unless the member
///   is busy and the trigger is "only when idle" (skipped, the next fire armed).
/// - Fires that passed while the app was not running are recorded as missed, never caught up: the trigger is armed
///   at its next occurrence from now. A once that passed fires late, once.
/// - A workflow member's fire starts its workflow run instead (no deliverable); while its run is going or waiting, a folder
///   change is queued by the runner and runs when that run ends (recorded fired, the reason saying queued), any other fire is skipped.
/// - A wake (the wake_me tool) tells the same member its note inside the deliverable it asked from.
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

    /// <summary>The folder looks being listed outside the gate; null when none is. Under the gate.</summary>
    private Task<IReadOnlyList<FolderLook>>? _folderLooks;

    /// <summary>The time the pump's sweeps go by (tests set their own).</summary>
    internal Func<DateTimeOffset> TriggerClock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Lists a watched folder: relative path to size (tests set a fake that waits).</summary>
    internal Func<string, string?, Dictionary<string, long>> ListFolder { get; set; } = FolderWatcher.Listing;

    /// <summary>Called by the pump pass (under the gate).</summary>
    private void SweepTriggersIfDue()
    {
        // Folders whose listing has finished are recorded at once: the listing signals the pump when it is done.
        if (_folderLooks is { IsCompleted: true } listed)
        {
            _folderLooks = null;
            ApplyFolderLooks(listed.Result);
        }
        var now = TriggerClock();
        if (now < _nextTriggerSweep) return;
        _nextTriggerSweep = now + TriggerSweepInterval;
        SweepTriggers(now);
        // One listing pass at a time: while a slow folder is being listed, the due folders wait for the next sweep.
        if (_folderLooks is not null) return;
        var plans = FolderPlans(now);
        if (plans.Count == 0) return;
        _folderLooks = Task.Run(() =>
        {
            var looks = LookAtFolders(plans);
            Signal();
            return looks;
        });
    }

    /// <summary>Fires every due trigger and wake as of <paramref name="now"/> (tests call it with their own clock).</summary>
    public async Task SweepTriggersAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var plans = await WithGate(() => FolderPlans(now), ct);
        var looks = plans.Count == 0 ? [] : await Task.Run(() => LookAtFolders(plans), ct);
        await WithGate(() =>
        {
            SweepTriggers(now);
            ApplyFolderLooks(looks);
        }, ct);
    }

    private void SweepTriggers(DateTimeOffset now)
    {
        try { ResumeRateLimited(now); }
        catch (Exception ex)
        {
            Logs.Diag.Warning("triggers", $"automatic resume sweep failed: {ex.GetType().Name}: {ex.Message}",
                ("check", "resuming rate-limited runs"), ("exception", ex.GetType().Name));
        }
        foreach (var row in _store.DueTriggers(now))
        {
            if (row.Schedule.Kind == TriggerKinds.Folder) continue; // looked at once its folder is listed (ApplyFolderLooks)
            try
            {
                FireDue(row, now);
            }
            catch (Exception ex)
            {
                WarnTriggerFailed(row, ex);
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
                var member = MemberOrNull(wake.MemberId);
                Logs.Diag.Warning("triggers", $"wake {wake.Id} failed: {ex.GetType().Name}: {ex.Message}",
                    ("team", member?.Team), ("member", member?.Qualified), ("wake", wake.Id), ("schedule", "wake"),
                    ("causation", wake.CausationSeq),("exception", ex.GetType().Name));
            }
        }
    }

    private void WarnTriggerFailed(TriggerRow row, Exception ex)
    {
        var member = MemberOrNull(row.MemberId);
        Logs.Diag.Warning("triggers", $"trigger {row.Id} ({row.Name}) failed: {ex.GetType().Name}: {ex.Message}",
            ("team", member?.Team ?? TeamNameOrNull(row.TeamId)), ("member", member?.Qualified), ("trigger", row.Id),
            ("schedule", row.Schedule.Kind), ("exception", ex.GetType().Name));
    }

    /// <summary>A member for a failure's log line; null when it is gone or cannot be read (the line is written anyway).</summary>
    private MemberRef? MemberOrNull(long memberId)
    {
        try { return _store.Member(memberId); }
        catch (Exception) { return null; }
    }

    private string? TeamNameOrNull(long teamId)
    {
        try { return _store.Teams().FirstOrDefault(t => t.Id == teamId)?.Name; }
        catch (Exception) { return null; }
    }

    private void FireDue(TriggerRow row, DateTimeOffset now)
    {
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

    /// <summary>A due folder trigger's look, as planned under the gate: what to list, and the time the look counts as.</summary>
    /// <param name="DocumentsRoot">The team's documents folder, for a watch inside it; null for a folder on this computer,
    /// or when the team's folders are not here.</param>
    private sealed record FolderPlan(TriggerRow Trigger, DateTimeOffset At, bool HasMember, string? DocumentsRoot);

    /// <summary>A planned look after the file system was read: the folder (null: nothing to watch), its listing (null:
    /// no such folder), or what went wrong.</summary>
    private sealed record FolderLook(FolderPlan Plan, string? Folder, Dictionary<string, long>? Listing, Exception? Error);

    /// <summary>Under the gate: the folder triggers due as of <paramref name="now"/>, with what their listing needs from the store.</summary>
    private List<FolderPlan> FolderPlans(DateTimeOffset now)
    {
        var plans = new List<FolderPlan>();
        foreach (var row in _store.DueTriggers(now))
        {
            if (row.Schedule.Kind != TriggerKinds.Folder) continue;
            try
            {
                var member = _store.Member(row.MemberId);
                var team = member is null || row.Watch is null || row.Watch.Root == FolderWatch.Host || Repos is null
                    ? null
                    : _store.Teams().FirstOrDefault(t => t.Id == member.TeamId)?.Name;
                plans.Add(new FolderPlan(row, now, member is not null, team is null ? null : Path.Combine(Repos!.TeamRoot(team), Teams.TeamDocuments.Folder)));
            }
            catch (Exception ex)
            {
                WarnTriggerFailed(row, ex);
            }
        }
        return plans;
    }

    /// <summary>Outside the gate: reads each planned folder. Never throws; a failure is kept with its look.</summary>
    private IReadOnlyList<FolderLook> LookAtFolders(IReadOnlyList<FolderPlan> plans)
    {
        var looks = new List<FolderLook>(plans.Count);
        foreach (var plan in plans)
        {
            try
            {
                var watch = plan.Trigger.Watch;
                var folder = watch is null || !plan.HasMember ? null : WatchedFolder(watch, plan.DocumentsRoot);
                looks.Add(new FolderLook(plan, folder, folder is not null && Directory.Exists(folder) ? ListFolder(folder, watch!.Glob) : null, null));
            }
            catch (Exception ex)
            {
                looks.Add(new FolderLook(plan, null, null, ex));
            }
        }
        return looks;
    }

    /// <summary>
    /// Under the gate: records and fires what the listings show. A trigger that was edited, re-armed, disabled or
    /// removed while its folder was being listed is left for its next look.
    /// </summary>
    private void ApplyFolderLooks(IReadOnlyList<FolderLook> looks)
    {
        foreach (var look in looks)
        {
            var planned = look.Plan.Trigger;
            try
            {
                var row = _store.Trigger(planned.Id);
                if (row is null || !row.Enabled || row.Schedule != planned.Schedule || row.Watch != planned.Watch
                    || row.NextDueAt is not { } due || due > look.Plan.At) continue;
                LookAtFolder(row, look);
            }
            catch (Exception ex)
            {
                WarnTriggerFailed(planned, ex);
            }
        }
    }

    /// <summary>
    /// One look at a folder trigger's folder, from its listing: remember it, wait for a change to settle, and tell the
    /// member what changed when one has (through the same checks as any fire: paused team, only when idle).
    /// </summary>
    private void LookAtFolder(TriggerRow row, FolderLook look)
    {
        if (look.Error is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        var now = look.Plan.At;
        var next = TriggerScheduling.NextOccurrence(row.Schedule, now);
        var folder = look.Folder;
        if (folder is null || look.Listing is null)
        {
            _store.RecordTriggerOutcome(row.Id, null, next, "skipped", folder is null ? "nothing to watch" : $"the folder {folder} is not there", null, row.MissedCount);
            return;
        }
        var (change, state) = FolderWatcher.Look(look.Listing, _store.WatchState(row.Id), now, row.Watch!.QuietSec, row.Watch.FireOn);
        if (change is null)
        {
            _store.SetWatch(row.Id, state, next);
            return;
        }
        var fire = Fire(row, now, change.Describe(row.Watch.Root == FolderWatch.Documents ? $"the team's documents{(row.Watch.Path.Length > 0 ? "/" + row.Watch.Path : "")}" : folder),
            Workflows.Running.WorkflowPayloads.FolderChange(folder, change.Added, change.Removed, change.Changed));
        // Only a change that was told is taken as the new baseline: a skipped one (busy, paused) fires at the next look,
        // with the same list (only the kinds the trigger fires on). A workflow member's change queued while its run goes on
        // (it runs when that run ends) counts as told; the trigger records it as fired, the reason saying it was queued.
        var told = fire.Outcome is "fired" or "queued";
        if (told) _store.SetWatchState(row.Id, state);
        _store.RecordTriggerOutcome(row.Id, told ? now : null, next, told ? "fired" : fire.Outcome, fire.Reason, fire.Seq, row.MissedCount);
    }

    /// <summary>The folder a watch means: inside the team's documents, or the absolute folder on this computer.</summary>
    private static string? WatchedFolder(FolderWatch w, string? documentsRoot)
    {
        if (w.Root == FolderWatch.Host) return Path.IsPathFullyQualified(w.Path) ? w.Path : null;
        if (documentsRoot is null) return null;
        Directory.CreateDirectory(documentsRoot);
        try { return Teams.TeamDocuments.Resolve(documentsRoot, w.Path, mustExist: false); }
        catch (Teams.DocumentsException) { return null; }
    }

    /// <param name="payload">What a workflow member gets as its run's input (FolderChange, EventItem); null: TriggerFired.</param>
    private TriggerFire Fire(TriggerRow row, DateTimeOffset now, string? what = null, System.Text.Json.Nodes.JsonObject? payload = null)
    {
        var member = _store.Member(row.MemberId);
        if (member is null) return new TriggerFire("member-missing", null, "the member no longer exists");
        if (_store.Teams().FirstOrDefault(t => t.Id == member.TeamId) is { } team && (team.Paused || team.Archived))
            return new TriggerFire("skipped", null, team.Archived ? "the team is archived" : "the team is paused");
        // A workflow member's fire starts its workflow run with the structured payload; no deliverable, no message.
        if (FireWorkflowMember(row, member, now, payload) is { } scripted) return scripted;
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
            // a workflow member runs its workflow with the fire's details as input: it is told nothing, so it needs no instruction
            var isWorkflow = WorkflowMembers?.IsWorkflowMember(member.Id) == true;
            if (isWorkflow) draft = draft with { Instruction = draft.Instruction?.Trim() ?? "" };
            else if (string.IsNullOrWhiteSpace(draft.Instruction)) throw new ArgumentException("a trigger needs an instruction");
            if (draft.Id is { } id && _store.Trigger(id) is null) throw new ArgumentException("no such trigger");
            if (!WakeManagerPolicy.IsValid(draft.WakeManager))
                throw new ArgumentException($"wakeManager must be one of {string.Join(", ", WakeManagerPolicy.All)}");
            var now = DateTimeOffset.UtcNow;
            if (TriggerScheduling.Validate(draft.Schedule, draft.Schedule.Kind == TriggerKinds.Once ? now : null) is { } why) throw new ArgumentException(why);
            if (draft.Schedule.Kind == TriggerKinds.Folder)
            {
                // No "Fire on" given (an empty value): an edit keeps the trigger's own, a new trigger fires on added files.
                if (draft.Watch is { FireOn: null or "" } w)
                    draft = draft with { Watch = w with { FireOn = draft.Id is { } eid && _store.Trigger(eid)?.Watch is { } old ? old.FireOn : FolderWatch.Added } };
                draft = draft with { Watch = CheckWatch(draft.Watch) };
            }
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
        if (!FolderWatch.FireOnValues.Contains(w.FireOn)) throw new ArgumentException($"fire on is one of {string.Join(", ", FolderWatch.FireOnValues)}");
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
