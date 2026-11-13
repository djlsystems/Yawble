using System.Text.Json.Nodes;
using Yawble.Core.Bus;

namespace Yawble.Core.Concierge;

/// <summary>An update the watcher posted: the conversation, the stored message, and whether it asks for a review turn.</summary>
public sealed record WatchHit(string ConversationId, ConciergeMessageRow Message, bool FollowUp);

/// <summary>
/// Keeps the Concierge's promises between turns, with no agent running: it reads the bus for what a person waits on
/// (a workflow finished, a decision asked, a run failed, an agent blocked), matches each event against the active
/// watches (<see cref="WatchRow"/>), and posts each match into its conversation as an update. A workflow watch ends
/// when its workflow is declared complete; any watch ends when it expires. Restart-safe: each watch remembers how far
/// it has read (since_seq), so events that arrived while the app was closed are posted at the next start.
/// </summary>
public sealed class ConciergeWatcher(ConciergeStore store, BusEngine bus, TimeProvider? clock = null)
{
    public static readonly IReadOnlyCollection<string> Types =
        [MessageTypes.WorkflowCompleted, MessageTypes.NeedsDecision, MessageTypes.Failed, MessageTypes.Blocked];

    public static readonly TimeSpan DefaultLife = TimeSpan.FromHours(48);
    public static readonly TimeSpan MaxLife = TimeSpan.FromDays(7);
    public const int TextChars = 800;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Reads the bus once and posts what the active watches were waiting for. Returns the updates posted.</summary>
    public async Task<IReadOnlyList<WatchHit>> PollAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var hits = new List<WatchHit>();
            var watches = store.ActiveWatches();
            if (watches.Count == 0) return hits;

            var now = _clock.GetUtcNow();
            foreach (var w in watches.Where(w => DateTimeOffset.Parse(w.ExpiresAt, System.Globalization.CultureInfo.InvariantCulture) <= now).ToList())
            {
                if (!store.EndWatch(w.Id, "expired")) continue;
                var text = $"Stopped watching **{w.Team}**: {w.What} (no news within the time set for it).";
                var id = store.AddUpdate(w.ConversationId, text, w.Team, "expired", null);
                hits.Add(new WatchHit(w.ConversationId, store.Message(id)!, false));
                watches = watches.Where(x => x.Id != w.Id).ToList();
            }
            if (watches.Count == 0) return hits;

            var (end, messages) = await bus.MessagesSinceAsync(watches.Min(w => w.SinceSeq), Types, ct);
            var roots = new Dictionary<long, string?>();
            var ended = new HashSet<long>();
            foreach (var m in messages)
            {
                var team = await TeamOfAsync(m, roots, ct);
                if (team is null) continue;
                // One update per conversation per event, even when two of its watches match (a workflow and its team).
                foreach (var group in watches
                             .Where(w => !ended.Contains(w.Id) && m.Seq > w.SinceSeq && Matches(w, m, team))
                             .GroupBy(w => w.ConversationId))
                {
                    if (Describe(m, team) is not { } update) continue;
                    var id = store.AddUpdate(group.Key, update.Text, team, update.Kind, m.Seq);
                    hits.Add(new WatchHit(group.Key, store.Message(id)!, group.Any(w => w.FollowUp)));
                    if (m.Type == MessageTypes.WorkflowCompleted)
                        foreach (var w in group.Where(w => w.Workflow == m.CorrelationId))
                            if (store.EndWatch(w.Id, "done")) ended.Add(w.Id);
                }
            }
            store.AdvanceWatches(end);
            return hits;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool Matches(WatchRow w, Message m, string team) => w.Workflow is { } wf
        ? m.CorrelationId == wf
        : string.Equals(w.Team, team, StringComparison.OrdinalIgnoreCase);

    /// <summary>A member's message names its team in its source; the app's own (source "host") by its workflow's root instruction.</summary>
    private async Task<string?> TeamOfAsync(Message m, Dictionary<long, string?> roots, CancellationToken ct)
    {
        var slash = m.Source.IndexOf('/');
        if (slash > 0) return m.Source[..slash];
        if (!roots.TryGetValue(m.CorrelationId, out var team))
        {
            var root = await bus.FindMessageAsync(m.CorrelationId, ct);
            team = root is not null && root.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
                   && root.Type[MessageTypes.InstructionPrefix.Length..] is var target && target.IndexOf('/') is > 0 and var i
                ? target[..i]
                : null;
            roots[m.CorrelationId] = team;
        }
        return team;
    }

    /// <summary>What an event means to the person, in one line of markdown, and its kind (done, decision, failed, blocked).</summary>
    public static (string Kind, string Text)? Describe(Message m, string team)
    {
        var who = m.Source.IndexOf('/') is > 0 and var i ? m.Source[(i + 1)..] : null;
        var p = Payload(m);
        var by = who is null ? "" : $" · {who}";
        return m.Type switch
        {
            MessageTypes.WorkflowCompleted =>
                ("done", $"**{team}** finished: {Clip(Str(p, "delivered") ?? "the work is declared complete")}"),
            MessageTypes.NeedsDecision =>
                ("decision", $"**{team}**{by} needs your decision (question {m.Seq}): {Clip(Str(p, "question") ?? "")}"),
            MessageTypes.Blocked =>
                ("blocked", $"**{team}**{by} is blocked: {Clip(Str(p, "reason") ?? "no reason given")}"),
            MessageTypes.Failed =>
                ("failed", $"**{team}**{by}'s run {(Str(p, "failureClass") is { } fc && fc != FailureClasses.Failed ? $"ended ({fc})" : "failed")}: " +
                           Clip(Str(p, "reason") ?? "no reason given")),
            _ => null,
        };
    }

    private static JsonObject? Payload(Message m)
    {
        try
        {
            return JsonNode.Parse(m.Payload) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static string Clip(string s) => s.Length <= TextChars ? s : s[..TextChars] + "…";
}
