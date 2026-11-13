using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Triggers;

namespace Yawble.Core.Bus;

/// <summary>
/// Events: something a watch noticed (a new pull request, a new issue) is published on the bus as
/// event.&lt;name&gt; for its team, and every enabled event trigger of that team whose name pattern matches tells its
/// member the trigger's instruction with the item, as a new workflow (through the usual checks: a paused team, only
/// when idle). The person's agents never poll: the watch costs no model run, and an agent runs only when there is
/// something to do.
/// </summary>
public sealed partial class BusEngine
{
    public const string EventPrefix = "event.";

    /// <summary>Publishes one event for a team and fires the team's matching event triggers. The triggers it fired.</summary>
    public Task<int> PublishEventAsync(long teamId, string name, JsonObject item, string source, CancellationToken ct = default) =>
        WithGate(() =>
        {
            var now = _store.Clock.GetUtcNow();
            var team = _store.Teams().FirstOrDefault(t => t.Id == teamId) ?? throw new BusRefusal("no_such_team", $"there is no team {teamId}");
            Publish(new NewMessage(EventPrefix + name, JsonSerializer.Serialize(new { team = team.Name, @event = name, item }), source));
            var fired = 0;
            foreach (var row in _store.Triggers(teamId).Where(t => t.Enabled && t.Schedule.Kind == TriggerKinds.Event && t.EventName is { } p
                         && Yawble.Core.Integrations.CliRules.Matches(p, name)))
            {
                var what = $"Event {name}:\n{Describe(item)}";
                var fire = Fire(row, now, what);
                _store.RecordTriggerOutcome(row.Id, fire.Outcome == "fired" ? now : null, null, fire.Outcome, fire.Reason, fire.Seq, row.MissedCount);
                if (fire.Outcome == "fired") fired++;
            }
            Log($"event {name} for {team.Name}: {fired} trigger(s) fired");
            return fired;
        }, ct);

    private static string Describe(JsonObject item)
    {
        var lines = item.Where(kv => kv.Value is JsonValue).Select(kv => $"- {kv.Key}: {kv.Value}").Take(20).ToList();
        var text = lines.Count > 0 ? string.Join("\n", lines) : item.ToJsonString();
        return text.Length <= 3000 ? text : text[..2999] + "…";
    }
}
