using Harness.Contracts;
using Harness.Kanban;

namespace Harness.Tests;

/// <summary>
/// A card in Done stays in Done until NEW work for it starts or a person moves it.
///
/// A run exiting puts its card in Done, and the member is often woken again in the same workflow
/// without being given anything new - a Manager woken by its member's hand-back is the everyday
/// case. Its `started` used to pull the card back to In Progress and its exit put it in Done again,
/// so a card crossed the board twice with nobody touching it.
/// </summary>
public sealed class KanbanDoneStaysDoneTests
{
    private const long Workflow = 1;

    private static Message Row(long seq, string type, string source, string payload = "{}") =>
        new(seq, type, payload, source, Workflow, seq > 1 ? seq - 1 : null, 0, DateTimeOffset.UnixEpoch.AddSeconds(seq));

    private static Message Told(long seq, string member) =>
        Row(seq, MessageTypes.InstructionPrefix + "Alpha/" + member, "console", """{"instruction":"Write the brief"}""");

    private static Message Started(long seq, string member, string trigger) =>
        Row(seq, MessageTypes.Started, "Alpha/" + member, $$"""{"trigger":"{{trigger}}"}""");

    private static string ToldType(string member) => MessageTypes.InstructionPrefix + "Alpha/" + member;

    private static KanbanCard Card(string id, params Message[] rows) =>
        Assert.Single(KanbanProjector.Project(rows).Cards, card => card.Id == id);

    /// <summary>The Manager told once, its run over: its card is in Done.</summary>
    private static Message[] ManagerRanAndExited() =>
    [
        Told(1, "Manager"),
        Started(2, "Manager", ToldType("Manager")),
        Row(3, MessageTypes.Completed, "Alpha/Manager"),
    ];

    [Fact]
    public void A_done_card_stays_done_while_its_member_is_woken_by_a_hand_back()
    {
        var card = Card($"{Workflow}_Manager",
        [
            .. ManagerRanAndExited(),
            Row(4, MessageTypes.Handback, "Alpha/Developer", """{"delivered":"the brief"}"""),
            Started(5, "Manager", MessageTypes.Handback),
        ]);

        Assert.Equal(KanbanLanes.Done, card.LaneId);
    }

    [Fact]
    public void A_card_the_workflow_declared_done_stays_done_when_its_member_wakes_without_new_work()
    {
        var card = Card($"{Workflow}_Manager",
        [
            Told(1, "Manager"),
            Started(2, "Manager", ToldType("Manager")),
            Row(3, MessageTypes.WorkflowCompleted, "Alpha/Manager", """{"delivered":"the brief"}"""),
            Row(4, MessageTypes.Completed, "Alpha/Manager"),
            Started(5, "Manager", MessageTypes.Failed),
        ]);

        Assert.Equal(KanbanLanes.Done, card.LaneId);
    }

    [Fact]
    public void A_new_instruction_after_the_done_moves_the_card_out_of_done()
    {
        var card = Card($"{Workflow}_Manager",
        [
            .. ManagerRanAndExited(),
            Told(4, "Manager"),
            Started(5, "Manager", ToldType("Manager")),
        ]);

        Assert.Equal(KanbanLanes.Running, card.LaneId);
    }

    /// <summary>
    /// Work queued BEFORE the card reached Done is still new work when it starts: the member was
    /// told twice and is only now running the second. The run's trigger is what says so.
    /// </summary>
    [Fact]
    public void An_instruction_queued_before_the_done_moves_the_card_when_its_run_starts()
    {
        var card = Card($"{Workflow}_Developer",
        [
            Told(1, "Developer"),
            Told(2, "Developer"),
            Started(3, "Developer", ToldType("Developer")),
            Row(4, MessageTypes.Handback, "Alpha/Developer", """{"delivered":"first"}"""),
            Row(5, MessageTypes.Completed, "Alpha/Developer"),
            Started(6, "Developer", ToldType("Developer")),
        ]);

        Assert.Equal(KanbanLanes.Running, card.LaneId);
    }

    /// <summary>A run woken by something else while an instruction waits in the same batch.</summary>
    [Fact]
    public void An_instruction_after_the_done_moves_the_card_whatever_woke_the_run()
    {
        var card = Card($"{Workflow}_Manager",
        [
            .. ManagerRanAndExited(),
            Row(4, MessageTypes.Handback, "Alpha/Developer", """{"delivered":"the brief"}"""),
            Told(5, "Manager"),
            Started(6, "Manager", MessageTypes.Handback),
        ]);

        Assert.Equal(KanbanLanes.Running, card.LaneId);
    }

    [Fact]
    public void A_card_that_is_not_done_still_moves_to_in_progress_on_any_wake()
    {
        var card = Card($"{Workflow}_Manager",
        [
            Told(1, "Manager"),
            Started(2, "Manager", ToldType("Manager")),
            Row(3, MessageTypes.Blocked, "Alpha/Manager", """{"reason":"no key"}"""),
            Row(4, MessageTypes.Completed, "Alpha/Manager"),
            Started(5, "Manager", MessageTypes.Handback),
        ]);

        Assert.Equal(KanbanLanes.Running, card.LaneId);
    }

    [Fact]
    public void A_person_moving_a_done_card_back_is_obeyed()
    {
        var card = Card($"{Workflow}_Manager",
        [
            .. ManagerRanAndExited(),
            Row(4, MessageTypes.KanbanCardMoved, "person-1", $$"""{"cardId":"{{Workflow}}_Manager","laneId":"in-progress","team":"Alpha"}"""),
        ]);

        Assert.Equal(KanbanLanes.Running, card.LaneId);
    }
}
