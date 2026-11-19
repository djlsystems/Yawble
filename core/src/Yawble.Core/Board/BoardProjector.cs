using System.Text.Json.Nodes;
using Yawble.Core.Bus;

namespace Yawble.Core.Board;

/// <summary>The board's four lanes, and which status sits in which.</summary>
public static class Lanes
{
    public const string Todo = "todo", InProgress = "in-progress", NeedsYou = "blocked", Done = "done";
    public static readonly (string Id, string Label)[] All = [(Todo, "To Do"), (InProgress, "In Progress"), (NeedsYou, "Needs You"), (Done, "Done")];
    /// <summary>B097 (card 437): the card's run was stopped by the person. Not a failure: In Progress, never Needs You.</summary>
    public const string Stopped = "stopped";

    public static readonly string[] Statuses = ["queued", "running", Stopped, "blocked", "needs-decision", "failed", "completed", "handback", "done"];

    public static string Of(string status) => status switch
    {
        "running" or Stopped => InProgress,
        "blocked" or "needs-decision" or "failed" => NeedsYou,
        "completed" or "handback" or "done" => Done,
        _ => Todo,
    };

    public static bool IsLane(string lane) => All.Any(l => l.Id == lane);

    /// <summary>How loudly a status calls for a person: a quieter one never overwrites a louder one.</summary>
    public static int Loudness(string status) => status switch { "failed" => 3, "blocked" => 2, "needs-decision" => 1, _ => 0 };

    /// <summary>
    /// B097: whether a stopped run leaves the card's status as it is: a card in Done (its work was delivered), or one
    /// louder than "stopped" (a question, a block or a failure still waits on the person).
    /// </summary>
    public static bool StopKeeps(string status, string lane) => lane == Done || Loudness(status) > 0;
}

/// <summary>
/// Keeps the stored cards in step with the bus. Read in order, once:
/// <list type="bullet">
/// <item>An instruction to a member makes its card for that workflow, or puts new work on it (back to queued, To Do).
///   An instruction naming a planned card (tell's <c>card</c>) claims it for the member. A note to the Manager about a
///   card or a backlog item (payload <c>about</c> / <c>item</c>) makes no card.</item>
/// <item>A member's reports move its card: progress to In Progress, blocked / a question / a failed run to Needs You,
///   a finished run or a hand-back to Done. A louder status (failed, blocked, needs a decision) is not overwritten by a
///   quieter one until new work arrives. A card a person or the Manager placed keeps its lane, except for a give-up.
///   A run the person stopped (failure class "stopped", B097) is not a failure: the card's status is "stopped" and it
///   stays in its lane; its trail says "stopped". New work re-queues it as usual.</item>
/// <item>The workflow declared complete sends its cards to Done, except those waiting on a person, and marks the backlog
///   item dispatched as that workflow "declared".</item>
/// <item>Anything the team's Manager sends means it has seen the person's edits: the cards stop awaiting it.</item>
/// </list>
/// </summary>
public sealed class BoardProjector(BoardStore store)
{
    /// <summary>The team's testers (B019/B027); set by the board before each page of messages. None by default.</summary>
    public Func<string, IReadOnlySet<string>> TestersOf { get; set; } = _ => NoTesters;
    private static readonly IReadOnlySet<string> NoTesters = new HashSet<string>();

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
        if (m.Type is MessageTypes.WorkflowCompleted or MessageTypes.WorkflowClosed or MessageTypes.WorkflowExpired)
        {
            WorkflowDone(m);
            return;
        }
        if (team is null || sender is null) return;
        var exact = store.CardOf(team, sender, m.CorrelationId);
        var card = exact ?? store.OpenCardOf(team, sender);
        var p = Payload(m);
        // B086: the time of this workflow's work goes on the member's card in it, or, with none (the Manager's own runs),
        // on the member's card-less work in the workflow. Read before the report moves the card.
        var lastActivity = exact is null || m.Type != MessageTypes.Failed ? null : store.Trail(exact.Id).LastOrDefault()?.At;
        if (card is not null)
        {
            switch (m.Type)
            {
                case MessageTypes.Progress:
                    Report(card, sender, "running", Text(p, "status"), "progress", giveUp: false);
                    break;
                case MessageTypes.Blocked:
                    Report(card, sender, "blocked", Text(p, "reason"), "blocked", giveUp: true);
                    break;
                case MessageTypes.NeedsDecision:
                    Report(card, sender, "needs-decision", Text(p, "question"), "asked", giveUp: true);
                    break;
                // B097: a run the person stopped is not a failure: the card says "stopped" (nothing runs it any more) but
                // keeps its lane (no Needs You), and the trail says "stopped".
                case MessageTypes.Failed when Text(p, "failureClass") == FailureClasses.Stopped:
                    Stopped(card, sender, Text(p, "reason"));
                    break;
                case MessageTypes.Failed:
                    Report(card, sender, "failed", Text(p, "reason"), "failed", giveUp: true);
                    break;
                case MessageTypes.Completed:
                    Report(card, sender, "completed", TesterRule.RunEndedText(p), "run ended", giveUp: false);
                    break;
                case MessageTypes.Handback:
                    Report(card, sender, "handback", Text(p, "delivered"), "handed back", giveUp: false);
                    break;
            }
        }
        Time(m, p, exact is null ? null : store.Card(exact.Id), sender, lastActivity);
    }

    /// <summary>
    /// B086: what a report adds to the timeline. A hand-back and a question are markers. A run's end carries its stalls and
    /// lease waits (payload activity); a run the idle watchdog ended is a marker, and when its payload has no stalls (a run
    /// from before B086), it was Stalled from its last sign of activity. A member's run that ends asking to be woken is
    /// Scheduled until the wake. A run the person stopped (B097) keeps its stalls and lease waits but is no watchdog end,
    /// and its card's status "stopped" is Idle time (not Blocked, not Agent working).
    /// </summary>
    private void Time(Message m, JsonObject? p, CardRow? card, string sender, string? lastActivity)
    {
        var cardId = card?.Id;
        long? workflow = m.CorrelationId;
        switch (m.Type)
        {
            case MessageTypes.Handback:
                store.AddMarker(cardId, workflow, sender, TimeMarkers.Handback, _at!);
                return;
            case MessageTypes.NeedsDecision:
                store.AddMarker(cardId, workflow, sender, TimeMarkers.DecisionAsked, _at!);
                return;
            case MessageTypes.Completed or MessageTypes.Failed:
                break;
            default:
                return;
        }
        var stalls = Spans(p?["activity"]?["stalls"]);
        foreach (var (start, end) in stalls) store.AddSpan(cardId, workflow, sender, "stall", start, end);
        foreach (var (start, end) in Spans(p?["activity"]?["holds"])) store.AddSpan(cardId, workflow, sender, "hold", start, end);
        if (m.Type == MessageTypes.Failed && Text(p, "failureClass") == FailureClasses.Idle)
        {
            if (stalls.Count == 0 && TimelineRebuild.IdleSeconds(Text(p, "reason")) is { } n)
                store.AddSpan(cardId, workflow, sender, "stall", TimelineRebuild.StallStart(lastActivity, _at!, n), _at!);
            store.AddMarker(cardId, workflow, sender, TimeMarkers.WatchdogEnd, _at!);
        }
        if (m.Type == MessageTypes.Completed && card is not null && p?["wake"]?["at"] is JsonValue w && w.TryGetValue<string>(out var wakeText)
            && DateTimeOffset.TryParseExact(wakeText, "yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var wakeAt)
            && wakeAt > m.OccurredAt)
            store.AddScheduled(card, _at!, TimelineCalculator.Iso(wakeAt));
    }

    private static List<(string Start, string End)> Spans(JsonNode? node)
    {
        var list = new List<(string, string)>();
        if (node is not JsonArray a) return list;
        foreach (var s in a.OfType<JsonObject>())
            if (s["start"] is JsonValue sv && sv.TryGetValue<string>(out var start) && s["end"] is JsonValue ev && ev.TryGetValue<string>(out var end))
                list.Add((start, end));
        return list;
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
            store.Unend(planned.Id);
            store.AddTrail(planned.Id, from, "assigned", $"Assigned to {member}: {text}", _at);
            return;
        }
        if (store.CardOf(team, member, m.CorrelationId) is { } card)
        {
            // B086: the person's word to a member that asked them something answers it.
            if (m.Source == MessageTypes.PersonSource && card.Status == "needs-decision")
                store.AddMarker(card.Id, card.Workflow, member, TimeMarkers.DecisionAnswered, _at!);
            store.Save(card with { Status = "queued", Lane = card.Placed && card.Lane != Lanes.Done ? card.Lane : Lanes.Todo }, _at);
            store.Unend(card.Id); // new work: a card its workflow's end ended is live again (B085)
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

    private void Report(CardRow card, string sender, string status, string? text, string kind, bool giveUp)
    {
        var louder = Lanes.Loudness(status) >= Lanes.Loudness(card.Status);
        var next = louder ? status : card.Status;
        var lane = card.Placed && !giveUp ? card.Lane : Lanes.Of(next);
        store.AddTrail(card.Id, sender, kind, text ?? kind, _at);
        // B027 item 3: someone else's hand-back or finished run does not put a tester's card in Done.
        string? untested = null;
        if (lane == Lanes.Done && card.Lane != Lanes.Done
            && (untested = TesterRule.UntestedTester(card, store.Trail(card.Id), TestersOf(card.Team))) is not null)
        {
            lane = card.Lane == Lanes.Todo ? Lanes.InProgress : card.Lane;
            store.AddTrail(card.Id, BoardSource, "comment", $"Not Done yet: {untested} has not tested it since it was last assigned. " +
                $"Give it to {untested} with tell (card: {card.Id}), or move it to done with the reason in note.", _at);
        }
        store.Save(card with { Status = next, Lane = lane }, _at);
    }

    /// <summary>
    /// B097 (card 437): the person stopped the member's run. The card says "stopped" and stays in its lane (To Do or In
    /// Progress; never Needs You, no give-up). A card in Done, or one that calls louder for the person (a question, a
    /// block, a failure), keeps its status: <see cref="Lanes.StopKeeps"/>.
    /// </summary>
    private void Stopped(CardRow card, string sender, string? reason)
    {
        store.AddTrail(card.Id, sender, "stopped", reason ?? "stopped", _at);
        if (Lanes.StopKeeps(card.Status, card.Lane)) return;
        store.Save(card with { Status = Lanes.Stopped }, _at);
    }

    /// <summary>Who the board's own comments are from.</summary>
    public const string BoardSource = "board";

    /// <summary>
    /// The workflow ended. Completed: its cards go to Done, except those waiting on a person and a tester's untested card.
    /// Closed (by the person) or expired (by the app): every card of it goes to Done, a waiting one too: nobody works
    /// it any more. Either way every card not ended yet records the end (B085): when, and how (completed, closed or
    /// expired); an expired workflow's end is its last real activity, so the expiry adds no time to a card.
    /// </summary>
    private void WorkflowDone(Message m)
    {
        var how = m.Type switch { MessageTypes.WorkflowClosed => "closed", MessageTypes.WorkflowExpired => "expired", _ => "completed" };
        var p = Payload(m);
        var lastActivity = m.Type == MessageTypes.WorkflowExpired && Text(p, "endedAt") is { } la ? la : null;
        foreach (var card in store.CardsOfWorkflow(m.CorrelationId))
        {
            if (m.Type == MessageTypes.WorkflowExpired)
            {
                if (card.Lane == Lanes.Done && card.EndedAt is not null) continue;
                // B086: the card's time ends at the workflow's last activity, not at the expiry.
                if (card.Lane != Lanes.Done) store.Save(card with { Status = "done", Lane = Lanes.Done }, _at, lastActivity);
                store.AddTrail(card.Id, Who(m.Source), "workflow expired", Text(p, "detail") ?? "expired: nothing happened in it for too long", _at);
                store.AddMarker(card.Id, card.Workflow, card.Member, TimeMarkers.Expired, _at!);
                continue;
            }
            if (m.Type == MessageTypes.WorkflowClosed && card.Lane != Lanes.Done && Lanes.Loudness(card.Status) > 0)
            {
                store.Save(card with { Status = "done", Lane = Lanes.Done }, _at);
                store.AddTrail(card.Id, Who(m.Source), "workflow closed", Text(p, "reason") ?? "closed without being declared complete", _at);
                continue;
            }
            if (Lanes.Loudness(card.Status) > 0) continue;
            // B027 item 1: declaring the workflow complete leaves out a tester's card the tester has not tested, and says why.
            if (m.Type == MessageTypes.WorkflowCompleted && card.Lane != Lanes.Done
                && TesterRule.UntestedTester(card, store.Trail(card.Id), TestersOf(card.Team)) is { } untested)
            {
                store.AddTrail(card.Id, BoardSource, "comment", $"Left out of Done when the workflow was declared complete: {untested} has not " +
                    $"tested it since it was last assigned. Give it to {untested} with tell (card: {card.Id}), or move it to done with the reason in note.", _at);
                continue;
            }
            store.Save(card with { Status = "done", Lane = Lanes.Done }, _at);
            if (m.Type == MessageTypes.WorkflowClosed)
                store.AddTrail(card.Id, Who(m.Source), "workflow closed", Text(Payload(m), "reason") ?? "closed without being declared complete", _at);
            else
                store.AddTrail(card.Id, Who(m.Source), "workflow done", Text(Payload(m), "delivered") ?? "the work is declared complete", _at);
        }
        var endedAt = m.Type == MessageTypes.WorkflowExpired && Text(p, "endedAt") is { } last ? last : _at!;
        store.EndCardsOf(m.CorrelationId, how, endedAt);
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
