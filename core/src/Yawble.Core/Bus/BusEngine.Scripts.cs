using System.Text.Json.Nodes;

namespace Yawble.Core.Bus;

/// <summary>
/// Script members (B131) as the bus sees them: a trigger aimed at one is handed to the script runner with its structured
/// payload instead of being told as a new workflow (no workflow per fire, no Manager woken); agents' triggers are unchanged.
/// </summary>
public interface IScriptMemberHost
{
    bool IsScriptMember(long memberId);

    /// <summary>Called inside the gate; starts the run in the background and returns at once (fired, or skipped with the reason).</summary>
    TriggerFire FireScript(MemberRef member, string source, JsonObject payload);
}

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
