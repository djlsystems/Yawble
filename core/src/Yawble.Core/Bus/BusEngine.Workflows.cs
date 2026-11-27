using System.Text.Json.Nodes;

namespace Yawble.Core.Bus;

/// <summary>
/// Workflow members as the bus sees them: a trigger aimed at one is handed to the workflow runner with its structured
/// payload instead of being told as a new deliverable (no deliverable per fire, no Manager woken); agents' triggers are unchanged.
/// </summary>
public interface IWorkflowMemberHost
{
    bool IsWorkflowMember(long memberId);

    /// <summary>
    /// Called inside the gate; starts the run in the background and returns at once (fired, or skipped with the reason). With
    /// <paramref name="queueWhileBusy"/> (a folder trigger's change) a fire while the member's run is going or waiting is "queued"
    /// instead: it runs, merged with the trigger's other queued changes, when that run ends.
    /// </summary>
    TriggerFire FireWorkflow(MemberRef member, string source, JsonObject payload, bool queueWhileBusy = false);
}

/// <summary>A workflow's tell as the bus sent it: the deliverable it roots, the instruction's seq, and who got it.</summary>
public sealed record WorkflowTellDelivery(long Deliverable, long Seq, string Team, string Member, bool Manager);

/// <summary>How a deliverable a workflow told ended: <see cref="Done"/>, <see cref="Failed"/> or <see cref="Blocked"/>, with the closing summary or reason.</summary>
public sealed record WorkflowTellOutcome(string Outcome, string? Text)
{
    public const string Done = "done", Failed = "failed", Blocked = "blocked";
}

/// <summary>A team a workflow may tell, with its agent members (the Manager first).</summary>
public sealed record WorkflowTellTeam(long TeamId, string Team, IReadOnlyList<string> Members);

public sealed partial class BusEngine
{
    /// <summary>The workflow runner (set by the app); null: every member is an agent.</summary>
    public IWorkflowMemberHost? WorkflowMembers { get; set; }

    /// <summary>
    /// A trigger's fire for a workflow member: the structured payload (TriggerFired, FolderChange or EventItem) goes to its
    /// workflow run; nothing is published. Null when the member is an agent (its fire is told to it as a message).
    /// </summary>
    private TriggerFire? FireWorkflowMember(TriggerRow row, MemberRef member, DateTimeOffset now, JsonObject? payload)
    {
        if (WorkflowMembers is not { } workflows || !workflows.IsWorkflowMember(member.Id)) return null;
        // A folder change is queued while the member's run goes on; every/cron/once (and Run now) are skipped.
        var queue = payload is not null && row.Schedule.Kind == Yawble.Core.Triggers.TriggerKinds.Folder;
        payload ??= new JsonObject
        {
            ["triggerId"] = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), // TriggerFired declares it a string
            ["kind"] = row.Schedule.Kind,
            ["firedAt"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        var fire = workflows.FireWorkflow(member, TriggerSourcePrefix + row.Id, payload, queue);
        // Routine, at debug, with the workflow run it started (or recorded as skipped).
        Logs.Diag.Debug("workflows", $"trigger {row.Id} ({row.Name}) {fire.Outcome} workflow member {member.Qualified}"
            + (fire.WorkflowRunId is { } id ? $" (workflow run {id})" : "") + (fire.Reason is { } r ? ": " + r : ""),
            ("team", member.Team), ("member", member.Qualified), ("trigger", row.Id), ("schedule", row.Schedule.Kind),
            ("workflowRun", fire.WorkflowRunId), ("status", fire.Outcome));
        return fire;
    }

    /// <summary>
    /// Adds a member that will be a workflow member (the caller sets its workflow columns next). No agent is logged for it; its
    /// cli column keeps the table's default ('claude'), which no workflow run reads (members.kind says what runs it).
    /// </summary>
    public Task<MemberRow> AddWorkflowMemberAsync(long teamId, string name, CancellationToken ct = default) =>
        AddMemberCoreAsync(teamId, name, "claude", ct);

    /// <summary>
    /// A workflow's tell step: work for an agent member of a team, sent as the person's tell is (bus.tellManager's path), so it roots
    /// a new deliverable: the Manager plans it; another member gets it as the deliverable's one card (the Manager sees the deliverable but does
    /// not plan it). A workflow member cannot be told, nor a name that is no member. <paramref name="extra"/> goes on the instruction's
    /// payload (where it came from).
    /// </summary>
    public async Task<WorkflowTellDelivery> WorkflowTellAsync(string team, string member, string text, System.Text.Json.Nodes.JsonObject? extra,
        CancellationToken ct = default)
    {
        var target = await WithGate(() =>
        {
            var m = _store.Member(team, member) ?? throw new BusRefusal("no_such_member", $"'{team}' has no agent member named '{member}'");
            if (WorkflowMembers?.IsWorkflowMember(m.Id) == true)
                throw new BusRefusal("not_an_agent", $"{m.Name} of {m.Team} is a workflow member: a workflow tells only the team's agent members");
            return m;
        }, ct);
        var message = await PersonTellAsync(target.Team, target.Name, text, null, ct, extra);
        return new WorkflowTellDelivery(message.CorrelationId, message.Seq, target.Team, target.Name, target.Role == MemberRole.Manager);
    }

    /// <summary>
    /// How the deliverable a workflow told <paramref name="member"/> (team/name) ended, read from its thread: done (the deliverable
    /// completed, or a member that is not the Manager handed it back), failed (closed, expired, or the told member's run failed
    /// in a way the bus does not retry), blocked (the told member is blocked); null while it is still going. Text: the closing
    /// summary or the reason.
    /// </summary>
    public Task<WorkflowTellOutcome?> WorkflowTellOutcomeAsync(long deliverable, string member, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var thread = _store.Threads([deliverable]).GetValueOrDefault(deliverable) ?? [];
            var told = _store.AllMembers().FirstOrDefault(m => string.Equals(m.Qualified, member, StringComparison.OrdinalIgnoreCase));
            var manager = told?.Role == MemberRole.Manager;
            bool FromTold(Message m) => string.Equals(m.Source, member, StringComparison.OrdinalIgnoreCase);
            foreach (var m in thread.OrderBy(m => m.Seq))
            {
                switch (m.Type)
                {
                    case MessageTypes.DeliverableCompleted:
                        return new WorkflowTellOutcome(WorkflowTellOutcome.Done, Text(m, "delivered"));
                    case MessageTypes.DeliverableClosed:
                        return new WorkflowTellOutcome(WorkflowTellOutcome.Failed, Text(m, "reason") ?? "the deliverable was closed");
                    case MessageTypes.DeliverableExpired:
                        return new WorkflowTellOutcome(WorkflowTellOutcome.Failed, Text(m, "detail") ?? Text(m, "reason") ?? "the deliverable expired");
                    case MessageTypes.Handback when !manager && FromTold(m):
                        return new WorkflowTellOutcome(WorkflowTellOutcome.Done, Text(m, "delivered"));
                    case MessageTypes.Blocked when FromTold(m):
                        return new WorkflowTellOutcome(WorkflowTellOutcome.Blocked, Text(m, "reason"));
                    case MessageTypes.Failed when FromTold(m) && Text(m, "failureClass") is not (FailureClasses.Interrupted or FailureClasses.NotStarted
                        or FailureClasses.Rate or FailureClasses.Stopped):
                        return new WorkflowTellOutcome(WorkflowTellOutcome.Failed, Text(m, "reason") ?? "the run failed");
                }
            }
            return (WorkflowTellOutcome?)null;
        }, ct);

    /// <summary>
    /// Where a workflow may tell: each team that is not archived, with its agent members (workflow members are not told), the Manager
    /// first, then by name.
    /// </summary>
    public Task<IReadOnlyList<WorkflowTellTeam>> WorkflowTellTargetsAsync(CancellationToken ct = default) =>
        WithGate(() => (IReadOnlyList<WorkflowTellTeam>)_store.Teams().Where(t => !t.Archived).Select(t => new WorkflowTellTeam(t.Id, t.Name,
                _store.AllMembers().Where(m => m.TeamId == t.Id && WorkflowMembers?.IsWorkflowMember(m.Id) != true)
                    .OrderBy(m => m.Role == MemberRole.Manager ? 0 : 1).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(m => m.Name).ToList()))
            .ToList(), ct);

    /// <summary>
    /// Opens a decision card from a member outside any run (a workflow member's repeated failures): an agent.needsDecision
    /// from the member, rooting its own deliverable. The Manager is not woken by it. Returns its seq.
    /// </summary>
    public Task<long> OpenMemberDecisionAsync(long memberId, DecisionAsk ask, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var member = _store.Member(memberId) ?? throw new BusRefusal("no_such_member", $"there is no member {memberId}");
            _statusLines[member.Id] = "needs decision: " + ask.Headline;
            return Publish(new NewMessage(MessageTypes.NeedsDecision, ask.ToPayload().ToJsonString(), member.Qualified)).Seq;
        }, ct);
}
