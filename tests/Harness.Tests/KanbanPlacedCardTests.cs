using Harness.Contracts;
using Harness.Kanban;

namespace Harness.Tests;

/// <summary>
/// A card the Manager or a person has placed with `kanban move` keeps its lane when a run on it
/// ends well: each step of a card the Manager drives one instruction at a time is a run, and one
/// step ending is not the card being done. A card nobody placed still goes to Done on its member's
/// ending, and the give-ups still move a placed card.
/// </summary>
public sealed class KanbanPlacedCardTests
{
    private const long Workflow = 1;
    private const string CardId = "1_Developer";

    private static Message Row(long seq, string type, string source, string payload = "{}") =>
        new(seq, type, payload, source, Workflow, seq > 1 ? seq - 1 : null, 0, DateTimeOffset.UnixEpoch.AddSeconds(seq));

    private static string ToldType => MessageTypes.InstructionPrefix + "Alpha/Developer";

    private static Message Told(long seq) =>
        Row(seq, ToldType, "Alpha/Manager", """{"instruction":"Run the next step"}""");

    private static Message Started(long seq) =>
        Row(seq, MessageTypes.Started, "Alpha/Developer", $$"""{"trigger":"{{ToldType}}"}""");

    private static Message Completed(long seq, string output) =>
        Row(seq, MessageTypes.Completed, "Alpha/Developer", $$"""{"output":"{{output}}"}""");

    private static Message Moved(long seq, string lane) =>
        Row(seq, MessageTypes.KanbanCardMoved, "Alpha/Manager", $$"""{"cardId":"{{CardId}}","laneId":"{{lane}}","team":"Alpha"}""");

    private static KanbanCardDetail Card(IEnumerable<Message> rows) =>
        KanbanProjector.ProjectCard(rows.ToList(), CardId) ?? throw new InvalidOperationException("no card");

    /// <summary>Told once and running, then placed in In progress by the Manager.</summary>
    private static List<Message> PlacedInProgress() =>
    [
        Told(1),
        Started(2),
        Moved(3, KanbanLanes.Running),
    ];

    [Fact]
    public void A_placed_card_stays_in_progress_through_three_completions_and_a_hand_back_until_the_manager_moves_it_to_done()
    {
        var rows = PlacedInProgress();
        long seq = 3;

        foreach (var step in new[] { "step one", "step two", "step three" })
        {
            rows.Add(Told(++seq));
            rows.Add(Started(++seq));
            rows.Add(Completed(++seq, step));

            var afterStep = Card(rows);
            Assert.Equal(KanbanLanes.Running, afterStep.LaneId);
            Assert.Equal("completed", afterStep.Status);
        }

        rows.Add(Told(++seq));
        rows.Add(Started(++seq));
        rows.Add(Row(++seq, MessageTypes.Handback, "Alpha/Developer", """{"delivered":"the last step"}"""));

        var handedBack = Card(rows);
        Assert.Equal(KanbanLanes.Running, handedBack.LaneId);
        Assert.Equal("handback", handedBack.Status);
        Assert.Equal(KanbanLanes.ColourForStatus("handback"), handedBack.Color);

        var endings = handedBack.Trail
            .Where(entry => entry.Type is MessageTypes.Completed or MessageTypes.Handback)
            .Select(entry => (entry.Type, entry.Text))
            .ToList();
        Assert.Equal(
            [
                (MessageTypes.Completed, "step one"),
                (MessageTypes.Completed, "step two"),
                (MessageTypes.Completed, "step three"),
                (MessageTypes.Handback, "the last step"),
            ],
            endings);

        rows.Add(Moved(++seq, KanbanLanes.Done));

        Assert.Equal(KanbanLanes.Done, Card(rows).LaneId);
    }

    [Fact]
    public void A_card_nobody_moved_goes_to_done_on_its_members_completion()
    {
        var card = Card([Told(1), Started(2), Completed(3, "all of it")]);

        Assert.Equal(KanbanLanes.Done, card.LaneId);
        Assert.Equal("completed", card.Status);
    }

    [Fact]
    public void A_card_nobody_moved_goes_to_done_on_its_members_hand_back()
    {
        var card = Card([Told(1), Started(2), Row(3, MessageTypes.Handback, "Alpha/Developer", """{"delivered":"all of it"}""")]);

        Assert.Equal(KanbanLanes.Done, card.LaneId);
    }

    [Fact]
    public void A_placed_card_still_goes_to_blocked_on_blocked_and_back_on_its_members_next_start()
    {
        List<Message> rows = [.. PlacedInProgress(), Row(4, MessageTypes.Blocked, "Alpha/Developer", """{"reason":"no key"}""")];
        Assert.Equal(KanbanLanes.ForStatus("blocked"), Card(rows).LaneId);

        rows.Add(Completed(5, "gave up"));
        Assert.Equal(KanbanLanes.ForStatus("blocked"), Card(rows).LaneId);

        rows.Add(Told(6));
        rows.Add(Started(7));
        Assert.Equal(KanbanLanes.Running, Card(rows).LaneId);
        Assert.Equal("running", Card(rows).Status);
    }

    [Fact]
    public void A_placed_card_still_goes_to_failed_on_a_failure_and_back_on_its_members_next_start()
    {
        List<Message> rows = [.. PlacedInProgress(), Row(4, MessageTypes.Failed, "Alpha/Developer", """{"output":"crashed"}""")];
        Assert.Equal(KanbanLanes.ForStatus("failed"), Card(rows).LaneId);

        rows.Add(Told(5));
        rows.Add(Started(6));
        Assert.Equal(KanbanLanes.Running, Card(rows).LaneId);
    }

    [Fact]
    public void A_placed_card_still_goes_to_blocked_on_needs_decision()
    {
        var card = Card([.. PlacedInProgress(), Row(4, MessageTypes.NeedsDecision, "Alpha/Developer", """{"question":"which region?"}""")]);

        Assert.Equal(KanbanLanes.ForStatus("needs-decision"), card.LaneId);
    }

    [Fact]
    public void Workflow_completed_sweeps_a_placed_card_to_done()
    {
        var card = Card(
        [
            .. PlacedInProgress(),
            Completed(4, "a step"),
            Row(5, MessageTypes.WorkflowCompleted, "Alpha/Manager", """{"delivered":"the build"}"""),
        ]);

        Assert.Equal(KanbanLanes.Done, card.LaneId);
        Assert.Equal("done", card.Status);
    }

    [Fact]
    public void Workflow_completed_leaves_a_placed_card_waiting_on_a_person()
    {
        var card = Card(
        [
            .. PlacedInProgress(),
            Row(4, MessageTypes.Blocked, "Alpha/Developer", """{"reason":"no key"}"""),
            Row(5, MessageTypes.WorkflowCompleted, "Alpha/Manager", """{"delivered":"the build"}"""),
        ]);

        Assert.Equal(KanbanLanes.ForStatus("blocked"), card.LaneId);
    }
}
