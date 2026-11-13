using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Triggers;

namespace Yawble.Core.Bus;

/// <summary>A workflow as the person sees it in a team's list (bus.workflows).</summary>
/// <param name="State">open, completed (declared done) or closed (by a person). New work after either reopens it.</param>
/// <param name="Undeclared">Open, with nothing running, queued or waiting to be woken: nobody is working it.</param>
/// <param name="Busy">What is still working it, in words ("Ada is running").</param>
public sealed record WorkflowSummary(
    long Id, string Title, string? Owner, string State, bool Undeclared, IReadOnlyList<string> Busy, string? Ending,
    DateTimeOffset StartedAt, DateTimeOffset LastAt, int Messages);

/// <summary>
/// The person's hold on a workflow: see each one's state, close one, stop what
/// runs in it, nudge it back to life. And the two ways the app keeps a workflow from being forgotten: a Manager whose
/// own run the app's shutdown cut off is woken once at the next start, and an owner that ends its run with the
/// workflow open and nobody else working it is told so once.
/// </summary>
public sealed partial class BusEngine
{
    /// <summary>Marks the one idle offer of a workflow (the idleWorkflowOffer payload marker).</summary>
    public const string IdleOfferMarker = "idleWorkflowOffer";

    /// <summary>Marks the wake a Manager gets after its run in the workflow was cut off by the app stopping.</summary>
    public const string RestartWakeMarker = "restartWake";

    public Task<IReadOnlyList<WorkflowSummary>> WorkflowsAsync(string team, int limit = 30, CancellationToken ct = default) =>
        WithGate<IReadOnlyList<WorkflowSummary>>(() =>
        {
            var teamId = TeamId(team);
            var manager = _store.TeamMembers(teamId).Single(m => m.Role == MemberRole.Manager);
            return _store.WorkflowsOf(team, Math.Clamp(limit, 1, 200)).Select(w =>
            {
                var root = _store.Find(w.Id);
                var owner = OwnerOf(w.Id);
                var ending = _store.Workflow(w.Id).LastOrDefault(m => m.Type is MessageTypes.WorkflowCompleted or MessageTypes.WorkflowClosed);
                var ended = _store.WorkflowEnded(w.Id);
                var state = !ended ? "open" : ending?.Type == MessageTypes.WorkflowClosed ? "closed" : "completed";
                var busy = Busy(owner ?? manager, w.Id, watcher: null);
                if (_active.Values.Any(a => a.Context.Correlation == w.Id && a.Context.Member.Id == (owner ?? manager).Id))
                    busy.Insert(0, $"{(owner ?? manager).Name} is running");
                var undeclared = !ended && busy.Count == 0 && _store.OpenWakes(w.Id) == 0;
                return new WorkflowSummary(w.Id, TitleOf(root), (owner ?? manager).Name, state, undeclared, busy,
                    ending is null ? null : Text(ending, ending.Type == MessageTypes.WorkflowClosed ? "reason" : "delivered"),
                    w.First, w.Last, w.Count);
            }).ToArray();
        }, ct);

    /// <summary>
    /// A person closes a workflow: it ends without being declared complete. Its pending wakes are cancelled; nothing
    /// running is stopped (Stop does that) and what is already queued still runs: close is a record.
    /// </summary>
    public Task<Message> CloseWorkflowAsync(string team, long correlation, string? reason, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var teamId = TeamId(team);
            EnsureTeamWorkflow(teamId, correlation);
            if (_store.WorkflowEnded(correlation)) throw new BusRefusal("already_ended", "this workflow has already ended");
            return _store.InTransaction(() =>
            {
                var cancelled = _store.CancelWakes(correlation);
                var m = Publish(new NewMessage(MessageTypes.WorkflowClosed,
                    JsonSerializer.Serialize(new { team, reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), wakesCancelled = cancelled }),
                    MessageTypes.PersonSource, correlation));
                Log($"workflow {correlation} of {team}: closed by the person");
                return m;
            });
        }, ct);

    /// <summary>Stops every run of the team working this workflow (each is recorded as stopped). The number stopped.</summary>
    public Task<int> StopWorkflowAsync(string team, long correlation, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var teamId = TeamId(team);
            EnsureTeamWorkflow(teamId, correlation);
            var stopped = 0;
            foreach (var a in _active.Values.Where(a => a.Context.Member.TeamId == teamId && a.Context.Correlation == correlation))
            {
                _stopRequested[a.Context.RunId] = true;
                a.Cancel.Cancel();
                stopped++;
            }
            Log($"workflow {correlation} of {team}: stop requested for {stopped} run(s)");
            return stopped;
        }, ct);

    /// <summary>A person nudges a quiet workflow: its Manager is told to pick it up (joins the thread; reopens it).</summary>
    public async Task<Message> NudgeWorkflowAsync(string team, long correlation, string? note = null, CancellationToken ct = default)
    {
        string managerName;
        await _gate.WaitAsync(ct);
        try
        {
            var teamId = TeamId(team);
            EnsureTeamWorkflow(teamId, correlation);
            managerName = _store.TeamMembers(teamId).Single(m => m.Role == MemberRole.Manager).Name;
        }
        finally
        {
            _gate.Release();
        }
        var text = "A person nudged this workflow back to life. Look at everything under this thread (workflow_show) and either " +
            "continue the work, declare it complete with workflow_complete, or say why it should stop (blocked or needs_decision)." +
            (string.IsNullOrWhiteSpace(note) ? "" : $"\n\nThey added: {note.Trim()}");
        return await PersonTellAsync(team, managerName, text, correlation, ct, new JsonObject { ["nudge"] = true });
    }

    /// <summary>workflow_show: one workflow's messages, oldest first, when it is the caller's team's.</summary>
    internal Task<IReadOnlyList<Message>> WorkflowShowAsync(RunContext run, long correlation, CancellationToken ct) =>
        WithGate<IReadOnlyList<Message>>(() =>
        {
            if (TeamOf(correlation) != run.Member.TeamId) return [];
            return _store.Workflow(correlation);
        }, ct);

    // ------------------------------------------------------------------ keeping workflows from being forgotten

    /// <summary>
    /// After restart recovery: a Manager whose own run was cut off has nothing that would wake it again (it is never
    /// woken by its own rows). Woken once, when the workflow is still open and nothing is already waiting for it there.
    /// </summary>
    private void WakeCutOffManagers(IReadOnlyList<(MemberRef Member, Message Failed)> interrupted)
    {
        foreach (var (member, failed) in interrupted)
        {
            if (member.Role != MemberRole.Manager || _store.WorkflowEnded(failed.CorrelationId)) continue;
            if (_store.Pending(member.Qualified).Any(p => p.Correlation == failed.CorrelationId)) continue;
            var text = "Your last run in this workflow was cut off: the app stopped while it was running. Nothing else is queued " +
                "for you here, so nothing would wake you again. Look at everything under this thread (workflow_show) and carry on " +
                "from where it stopped.";
            _store.Append(new NewMessage(MessageTypes.Instruction(member), BatchText.InstructionPayload(text, new JsonObject { [RestartWakeMarker] = true }),
                MessageTypes.HostSource, failed.Seq));
            Log($"workflow {failed.CorrelationId}: woke {member.Qualified}, whose run the stop cut off");
        }
    }

    /// <summary>
    /// The idle offer: the workflow's owner or its team's Manager ended a run that went
    /// well, the workflow is still open, and nothing else works it: nothing running, nothing queued or waiting to be delivered in it, no wake asked
    /// for. Told once per message from the person in the workflow, to declare it complete or say what it waits on. A trigger's own workflow is
    /// left to the trigger's wake choice.
    /// </summary>
    private readonly List<RunContext> _endedRuns = [];

    /// <summary>The idle reminders of the runs that ended well since the last pass (true when one was sent).</summary>
    private bool OfferIdleReminders()
    {
        if (_endedRuns.Count == 0) return false;
        var sent = false;
        foreach (var run in _endedRuns.ToArray()) sent |= OfferIfIdle(run);
        _endedRuns.Clear();
        return sent;
    }

    private bool OfferIfIdle(RunContext run)
    {
        var correlation = run.Correlation;
        if (!_options.IdleOffer || WakeManagerPolicy.OfRun(run.Items) is not null) return false;
        // A workflow a trigger started is left to the trigger's wake choice, whoever's run ends in it.
        if (_store.Find(correlation) is { } root && root.Source.StartsWith(TriggerSourcePrefix, StringComparison.Ordinal)) return false;
        var manager = _store.TeamMembers(run.Member.TeamId).SingleOrDefault(m => m.Role == MemberRole.Manager);
        var owner = OwnerOf(correlation) ?? manager;
        // Told to whoever ended the last run, when that is the owner or the Manager (who may declare on its behalf):
        // a member's run that ends wakes the Manager, so the Manager is usually the last one working it.
        if (owner is null || (run.Member.Id != owner.Id && run.Member.Id != manager?.Id)) return false;
        if (_store.WorkflowEnded(correlation)) return false;
        var thread = _store.Workflow(correlation);
        // Once per word from the person: an offer already made since the person's last message in this workflow is not
        // made again (an owner that ignores it is not nagged), but a workflow the person picked up again (an answer, a
        // new instruction) gets its own chance, or it could stall silently after the first offer was spent.
        var lastPerson = thread.LastOrDefault(m => m.Source == MessageTypes.PersonSource)?.Seq ?? 0;
        if (thread.Any(m => m.Seq > lastPerson && m.Payload.Contains($"\"{IdleOfferMarker}\":true", StringComparison.Ordinal))) return false;
        // It already waits on the person: a question nobody has answered yet. Reminding the owner only makes it ask again.
        // (Answered: a reply to it, or anything the person told the asker later in the workflow; BusStore.OpenDecisions.)
        if (thread.Any(q => q.Type == MessageTypes.NeedsDecision
                && !thread.Any(a => a.Source == MessageTypes.PersonSource
                    && (a.CausationSeq == q.Seq || (a.Seq > q.Seq && a.Type == MessageTypes.InstructionPrefix + q.Source))))) return false;
        if (Busy(run.Member, correlation, watcher: null).Count != 0) return false;
        if (_store.Pending(run.Member.Qualified).Any(p => p.Correlation == correlation && !p.Started)) return false;
        if (_store.OpenWakes(correlation) != 0) return false;
        if (_options.DepthLimit > 0 && thread.Max(m => m.Depth) + 1 > _options.DepthLimit) return false;
        var text = "This workflow is open and nobody is working it. Every member has finished, nothing is queued, and no wake is " +
            "set: this was the last wake it would get. Do one of these now:\n" +
            "- if the work is done, call workflow_complete with what was delivered;\n" +
            "- if it waits on something (a person, an answer, an outside event), call blocked or needs_decision and say what.\n" +
            "Do not simply end your turn again. You are told this once per workflow.";
        _store.Append(new NewMessage(MessageTypes.Instruction(run.Member), BatchText.InstructionPayload(text, new JsonObject { [IdleOfferMarker] = true }),
            MessageTypes.HostSource, run.Items[^1].Seq));
        Log($"workflow {correlation}: idle offer to {run.Member.Qualified}");
        return true;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The team whose member the workflow's root addressed, or whose member sent its first message.</summary>
    private long? TeamOf(long correlation)
    {
        if (OwnerOf(correlation) is { } owner) return owner.TeamId;
        var first = _store.Workflow(correlation).FirstOrDefault(m => _store.MemberBySubscriber(m.Source) is not null);
        return first is null ? null : _store.MemberBySubscriber(first.Source)!.TeamId;
    }

    private void EnsureTeamWorkflow(long teamId, long correlation)
    {
        if (TeamOf(correlation) != teamId) throw new BusRefusal("no_such_workflow", $"workflow {correlation} is not this team's");
    }

    private static string TitleOf(Message? root)
    {
        if (root is null) return "(gone)";
        var text = Text(root, "text") ?? Text(root, "question") ?? Text(root, "status") ?? Text(root, "reason") ?? Text(root, "delivered") ?? root.Type;
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? text;
        return line.Length <= 100 ? line : line[..99] + "…";
    }

    private static string? Text(Message m, string field)
    {
        try
        {
            return JsonNode.Parse(m.Payload) is JsonObject o && o[field] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
