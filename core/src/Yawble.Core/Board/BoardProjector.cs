using System.Text.Json.Nodes;
using Yawble.Core.Bus;

namespace Yawble.Core.Board;

/// <summary>The board's four lanes, and which status sits in which.</summary>
public static class Lanes
{
    public const string Todo = "todo", InProgress = "in-progress", NeedsYou = "blocked", Done = "done";
    public static readonly (string Id, string Label)[] All = [(Todo, "To Do"), (InProgress, "In Progress"), (NeedsYou, "Needs You"), (Done, "Done")];
    public static readonly string[] Statuses = ["queued", "running", "blocked", "needs-decision", "failed", "completed", "handback", "done"];

    public static string Of(string status) => status switch
    {
        "running" => InProgress,
        "blocked" or "needs-decision" or "failed" => NeedsYou,
        "completed" or "handback" or "done" => Done,
        _ => Todo,
    };

    public static bool IsLane(string lane) => All.Any(l => l.Id == lane);

    /// <summary>How loudly a status calls for a person: a quieter one never overwrites a louder one.</summary>
    public static int Loudness(string status) => status switch { "failed" => 3, "blocked" => 2, "needs-decision" => 1, _ => 0 };
}

/// <summary>
/// Keeps the stored cards in step with the bus. Read in order, once:
/// <list type="bullet">
/// <item>An instruction to a member makes its card for that workflow, or puts new work on it (back to queued, To Do).
///   An instruction naming a planned card (tell's <c>card</c>) claims it for the member. A note to the Manager about a
///   card or a backlog item (payload <c>about</c> / <c>item</c>) makes no card.</item>
/// <item>A member's reports move its card: progress to In Progress, blocked / a question / a failed run to Needs You,
///   a finished run or a hand-back to Done. A louder status (failed, blocked, needs a decision) is not overwritten by a
///   quieter one until new work arrives. A card a person or the Manager placed keeps its lane, except for a give-up.</item>
/// <item>The workflow declared complete sends its cards to Done, except those waiting on a person, and marks the backlog
///   item dispatched as that workflow "declared".</item>
/// <item>Anything the team's Manager sends means it has seen the person's edits: the cards stop awaiting it.</item>
/// </list>
/// </summary>
public sealed class BoardProjector(BoardStore store)
{
    /// <summary>When the message being applied happened: the cards and their trails carry the work's own times.</summary>
    private string? _at;

    public void Apply(Message m)
    {
        _at = m.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var (team, sender) = Split(m.Source);
        if (team is not null && string.Equals(sender, "Manager", StringComparison.OrdinalIgnoreCase)) store.SetAwaitingManager(team, false);

        if (m.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
        {
            var (t, member) = Split(m.Type[MessageTypes.InstructionPrefix.Length..]);
            if (t is not null && member is not null) Instruction(m, t, member);
            return;
        }
        if (m.Type is MessageTypes.WorkflowCompleted or MessageTypes.WorkflowClosed)
        {
            WorkflowDone(m);
            return;
        }
        if (team is null || sender is null) return;
        var card = store.CardOf(team, sender, m.CorrelationId) ?? store.OpenCardOf(team, sender);
        if (card is null) return;
        var p = Payload(m);
        switch (m.Type)
        {
            case MessageTypes.Progress:
                Report(card, "running", Text(p, "status"), "progress", giveUp: false);
                break;
            case MessageTypes.Blocked:
                Report(card, "blocked", Text(p, "reason"), "blocked", giveUp: true);
                break;
            case MessageTypes.NeedsDecision:
                Report(card, "needs-decision", Text(p, "question"), "asked", giveUp: true);
                break;
            case MessageTypes.Failed:
                Report(card, "failed", Text(p, "reason"), "failed", giveUp: true);
                break;
            case MessageTypes.Completed:
                Report(card, "completed", null, "run ended", giveUp: false);
                break;
            case MessageTypes.Handback:
                Report(card, "handback", Text(p, "delivered"), "handed back", giveUp: false);
                break;
        }
    }

    private void Instruction(Message m, string team, string member)
    {
        var p = Payload(m);
        var text = Text(p, "text") ?? "";
        var from = Who(m.Source);
        if (p?["about"] is not null || p?["item"] is not null) return; // a note to the Manager about a card or an item
        if (p?["card"] is JsonValue v && v.TryGetValue<long>(out var claimed) && store.Card(claimed) is { } planned
            && string.Equals(planned.Team, team, StringComparison.OrdinalIgnoreCase))
        {
            var lane = planned.Placed ? planned.Lane : Lanes.Todo;
            store.Save(planned with { Member = member, Workflow = m.CorrelationId, Status = "queued", Lane = lane }, _at);
            store.AddTrail(planned.Id, from, "assigned", $"Assigned to {member}: {text}", _at);
            return;
        }
        if (store.CardOf(team, member, m.CorrelationId) is { } card)
        {
            store.Save(card with { Status = "queued", Lane = card.Placed && card.Lane != Lanes.Done ? card.Lane : Lanes.Todo }, _at);
            store.AddTrail(card.Id, from, "told", text, _at);
            return;
        }
        // Only work makes a card: the person starting a workflow, or a teammate (the Manager) telling a member. The app's
        // own notices (the idle reminder, a wake) and the person's replies inside a workflow (an answer to a question)
        // are not new work: they go on the trail of the workflow's cards, if it has any.
        if (m.Source == MessageTypes.HostSource || (m.Source == MessageTypes.PersonSource && m.CausationSeq is not null))
        {
            foreach (var c in store.CardsOfWorkflow(m.CorrelationId).Where(c => c.Lane != Lanes.Done).Take(1))
                store.AddTrail(c.Id, from, m.Source == MessageTypes.HostSource ? "notice" : "told", text, _at);
            return;
        }
        var item = store.ItemDispatchedAs(m.CorrelationId);
        var fresh = store.AddCard(team, member, item?.Label, Title(text), text, "queued", Lanes.Todo, m.CorrelationId, _at);
        store.AddTrail(fresh.Id, from, "told", text, _at);
    }

    private void Report(CardRow card, string status, string? text, string kind, bool giveUp)
    {
        var louder = Lanes.Loudness(status) >= Lanes.Loudness(card.Status);
        var next = louder ? status : card.Status;
        var lane = card.Placed && !giveUp ? card.Lane : Lanes.Of(next);
        store.Save(card with { Status = next, Lane = lane }, _at);
        store.AddTrail(card.Id, card.Member ?? "member", kind, text ?? kind, _at);
    }

    private void WorkflowDone(Message m)
    {
        foreach (var card in store.CardsOfWorkflow(m.CorrelationId))
        {
            if (Lanes.Loudness(card.Status) > 0) continue;
            store.Save(card with { Status = "done", Lane = Lanes.Done }, _at);
            if (m.Type == MessageTypes.WorkflowClosed)
                store.AddTrail(card.Id, Who(m.Source), "workflow closed", Text(Payload(m), "reason") ?? "closed without being declared complete", _at);
            else
                store.AddTrail(card.Id, Who(m.Source), "workflow done", Text(Payload(m), "delivered") ?? "the work is declared complete", _at);
        }
        if (m.Type == MessageTypes.WorkflowCompleted && store.ItemDispatchedAs(m.CorrelationId) is { State: "ready" } item) store.SaveItem(item with { State = "declared" });
    }

    /// <summary>A card's title from an instruction: its first line, without markdown markers.</summary>
    public static string Title(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim().TrimStart('#', '-', '*', ' ')).FirstOrDefault(l => l.Length > 0) ?? "(no text)";
        return line.Length <= 80 ? line : line[..79] + "…";
    }

    private static (string? Team, string? Member) Split(string qualified) =>
        qualified.IndexOf('/') is > 0 and var i ? (qualified[..i], qualified[(i + 1)..]) : (null, null);

    private static string Who(string source) =>
        source == MessageTypes.PersonSource ? "you" : source == MessageTypes.HostSource ? "schedule" : Split(source).Member ?? source;

    private static JsonObject? Payload(Message m)
    {
        try { return JsonNode.Parse(m.Payload) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string? Text(JsonObject? p, string name) =>
        p?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
