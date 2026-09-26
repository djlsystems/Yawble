using Harness.Contracts;
using Harness.Kanban;

namespace Harness.Tests;

/// <summary>
/// The board has ONE set of lanes. A row on the append-only log may still move a card into a lane
/// the board does not have; such a card would sit in no column at all, so the move is ignored and
/// the card stays where its status put it.
/// </summary>
public sealed class KanbanLanesTests
{
    private static Message Row(long seq, string type, string payload) =>
        new(seq, type, payload, "person-1", 1, null, 0, DateTimeOffset.UnixEpoch);

    private static KanbanCard Planned(params Message[] after) =>
        Assert.Single(KanbanProjector.Project(
        [
            Row(1, MessageTypes.KanbanCardPlanned, """{"team":"Alpha","title":"Write the brief"}"""),
            .. after,
        ]).Cards);

    [Fact]
    public void The_board_has_one_set_of_lanes()
    {
        var board = KanbanProjector.Project([]);

        Assert.Equal(["todo", "in-progress", "blocked", "done"], board.Lanes.Select(lane => lane.Id));
    }

    [Fact]
    public void A_logged_move_into_a_retired_lane_leaves_the_card_where_its_status_put_it()
    {
        var card = Planned(Row(2, MessageTypes.KanbanCardMoved, """{"cardId":"1","laneId":"review","team":"Alpha"}"""));

        Assert.Equal("todo", card.LaneId);
    }

    [Fact]
    public void A_move_into_a_lane_the_board_has_is_kept_in_the_boards_spelling()
    {
        var card = Planned(Row(2, MessageTypes.KanbanCardMoved, """{"cardId":"1","laneId":"Done","team":"Alpha"}"""));

        Assert.Equal("done", card.LaneId);
    }
}
