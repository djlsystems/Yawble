using System.Text.Json.Nodes;

namespace Yawble.Core.Bus;

/// <summary>
/// Script members as the bus sees them: a trigger aimed at one is handed to the script runner with its structured
/// payload instead of being told as a new workflow (no workflow per fire, no Manager woken); agents' triggers are unchanged.
/// </summary>
public interface IScriptMemberHost
{
    bool IsScriptMember(long memberId);

    /// <summary>Called inside the gate; starts the run in the background and returns at once (fired, or skipped with the reason).</summary>
    TriggerFire FireScript(MemberRef member, string source, JsonObject payload);
}

/// <summary>A script's tell as the bus sent it: the workflow it roots, the instruction's seq, and who got it.</summary>
public sealed record ScriptTellDelivery(long Workflow, long Seq, string Team, string Member, bool Manager);

/// <summary>How a workflow a script told ended: <see cref="Done"/>, <see cref="Failed"/> or <see cref="Blocked"/>, with the closing summary or reason.</summary>
public sealed record ScriptTellOutcome(string Outcome, string? Text)
{
    public const string Done = "done", Failed = "failed", Blocked = "blocked";
}

/// <summary>A team a script may tell, with its agent members (the Manager first).</summary>
public sealed record ScriptTellTeam(long TeamId, string Team, IReadOnlyList<string> Members);

public sealed partial class BusEngine
{
    /// <summary>The script runner (set by the app); null: every member is an agent.</summary>
    public IScriptMemberHost? ScriptMembers { get; set; }

    /// <summary>
    /// A trigger's fire for a script member: the structured payload (TriggerFired, FolderChange or EventItem) goes to its
    /// script run; nothing is published. Null when the member is an agent (its fire is told as before).
    /// </summary>
    private TriggerFire? FireScriptMember(TriggerRow row, MemberRef member, DateTimeOffset now, JsonObject? payload)
    {
        if (ScriptMembers is not { } scripts || !scripts.IsScriptMember(member.Id)) return null;
        payload ??= new JsonObject
        {
            ["triggerId"] = row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), // TriggerFired declares it a string
            ["kind"] = row.Schedule.Kind,
            ["firedAt"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        var fire = scripts.FireScript(member, TriggerSourcePrefix + row.Id, payload);
        // Routine, at debug, with the script run it started (or recorded as skipped).
        Logs.Diag.Debug("scripts", $"trigger {row.Id} ({row.Name}) {fire.Outcome} script member {member.Qualified}"
            + (fire.ScriptRunId is { } id ? $" (script run {id})" : "") + (fire.Reason is { } r ? ": " + r : ""),
            ("team", member.Team), ("member", member.Qualified), ("trigger", row.Id), ("schedule", row.Schedule.Kind),
            ("scriptRun", fire.ScriptRunId), ("status", fire.Outcome));
        return fire;
    }

    /// <summary>
    /// Adds a member that will be a script member (the caller sets its script columns next). No agent is logged for it; its
    /// cli column keeps the table's default ('claude'), which no script run reads (members.kind says what runs it).
    /// </summary>
    public Task<MemberRow> AddScriptMemberAsync(long teamId, string name, CancellationToken ct = default) =>
        AddMemberCoreAsync(teamId, name, "claude", ct);

    /// <summary>
    /// A script's tell step: work for an agent member of a team, sent as the person's tell is (bus.tellManager's path), so it roots
    /// a new workflow: the Manager plans it; another member gets it as the workflow's one card (the Manager sees the workflow but does
    /// not plan it). A script member cannot be told, nor a name that is no member. <paramref name="extra"/> goes on the instruction's
    /// payload (where it came from).
    /// </summary>
    public async Task<ScriptTellDelivery> ScriptTellAsync(string team, string member, string text, System.Text.Json.Nodes.JsonObject? extra,
        CancellationToken ct = default)
    {
        var target = await WithGate(() =>
        {
            var m = _store.Member(team, member) ?? throw new BusRefusal("no_such_member", $"'{team}' has no agent member named '{member}'");
            if (ScriptMembers?.IsScriptMember(m.Id) == true)
                throw new BusRefusal("not_an_agent", $"{m.Name} of {m.Team} is a script member: a script tells only the team's agent members");
            return m;
        }, ct);
        var message = await PersonTellAsync(target.Team, target.Name, text, null, ct, extra);
        return new ScriptTellDelivery(message.CorrelationId, message.Seq, target.Team, target.Name, target.Role == MemberRole.Manager);
    }

    /// <summary>
    /// How the workflow a script told <paramref name="member"/> (team/name) ended, read from its thread: done (the workflow
    /// completed, or a member that is not the Manager handed it back), failed (closed, expired, or the told member's run failed
    /// in a way the bus does not retry), blocked (the told member is blocked); null while it is still going. Text: the closing
    /// summary or the reason.
    /// </summary>
    public Task<ScriptTellOutcome?> ScriptTellOutcomeAsync(long workflow, string member, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var thread = _store.Threads([workflow]).GetValueOrDefault(workflow) ?? [];
            var told = _store.AllMembers().FirstOrDefault(m => string.Equals(m.Qualified, member, StringComparison.OrdinalIgnoreCase));
            var manager = told?.Role == MemberRole.Manager;
            bool FromTold(Message m) => string.Equals(m.Source, member, StringComparison.OrdinalIgnoreCase);
            foreach (var m in thread.OrderBy(m => m.Seq))
            {
                switch (m.Type)
                {
                    case MessageTypes.WorkflowCompleted:
                        return new ScriptTellOutcome(ScriptTellOutcome.Done, Text(m, "delivered"));
                    case MessageTypes.WorkflowClosed:
                        return new ScriptTellOutcome(ScriptTellOutcome.Failed, Text(m, "reason") ?? "the workflow was closed");
                    case MessageTypes.WorkflowExpired:
                        return new ScriptTellOutcome(ScriptTellOutcome.Failed, Text(m, "detail") ?? Text(m, "reason") ?? "the workflow expired");
                    case MessageTypes.Handback when !manager && FromTold(m):
                        return new ScriptTellOutcome(ScriptTellOutcome.Done, Text(m, "delivered"));
                    case MessageTypes.Blocked when FromTold(m):
                        return new ScriptTellOutcome(ScriptTellOutcome.Blocked, Text(m, "reason"));
                    case MessageTypes.Failed when FromTold(m) && Text(m, "failureClass") is not (FailureClasses.Interrupted or FailureClasses.NotStarted
                        or FailureClasses.Rate or FailureClasses.Stopped):
                        return new ScriptTellOutcome(ScriptTellOutcome.Failed, Text(m, "reason") ?? "the run failed");
                }
            }
            return (ScriptTellOutcome?)null;
        }, ct);

    /// <summary>
    /// Where a script may tell: each team that is not archived, with its agent members (script members are not told), the Manager
    /// first, then by name.
    /// </summary>
    public Task<IReadOnlyList<ScriptTellTeam>> ScriptTellTargetsAsync(CancellationToken ct = default) =>
        WithGate(() => (IReadOnlyList<ScriptTellTeam>)_store.Teams().Where(t => !t.Archived).Select(t => new ScriptTellTeam(t.Id, t.Name,
                _store.AllMembers().Where(m => m.TeamId == t.Id && ScriptMembers?.IsScriptMember(m.Id) != true)
                    .OrderBy(m => m.Role == MemberRole.Manager ? 0 : 1).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(m => m.Name).ToList()))
            .ToList(), ct);

    /// <summary>
    /// Opens a decision card from a member outside any run (a script member's repeated failures): an agent.needsDecision
    /// from the member, rooting its own workflow. The Manager is not woken by it. Returns its seq.
    /// </summary>
    public Task<long> OpenMemberDecisionAsync(long memberId, DecisionAsk ask, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var member = _store.Member(memberId) ?? throw new BusRefusal("no_such_member", $"there is no member {memberId}");
            _statusLines[member.Id] = "needs decision: " + ask.Headline;
            return Publish(new NewMessage(MessageTypes.NeedsDecision, ask.ToPayload().ToJsonString(), member.Qualified)).Seq;
        }, ct);
}
