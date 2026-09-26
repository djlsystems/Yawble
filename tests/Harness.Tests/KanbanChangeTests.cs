using Harness.Contracts;
using Harness.Host;
using Harness.Kanban;

namespace Harness.Tests;

/// <summary>
/// A person watching the all-teams Kanban must see a team's last card move when the team finishes,
/// without opening that team's tab. A `containerChanged` push goes only to the groups of teams
/// whose tabs are open, so every row that moves a card raises `kanbanChanged`.
/// </summary>
public sealed class KanbanChangeTests
{
    private static Message Row(long seq, string type, string source) =>
        new(seq, type, "{}", source, seq, null, 0, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("agentContainer.instruction.wave9/Manager")]
    [InlineData(MessageTypes.Started)]
    [InlineData(MessageTypes.Completed)]
    [InlineData(MessageTypes.Failed)]
    [InlineData(MessageTypes.Blocked)]
    [InlineData(MessageTypes.Handback)]
    [InlineData(MessageTypes.WorkflowCompleted)]
    [InlineData(MessageTypes.KanbanCardPlanned)]
    [InlineData(MessageTypes.KanbanCardMoved)]
    public void A_row_the_projector_reads_moves_the_board(string type)
    {
        Assert.True(KanbanProjector.MovesTheBoard(type));
    }

    [Theory]
    [InlineData(MessageTypes.RepoPushed)]
    [InlineData("tenant.settingChanged")]
    public void A_row_the_projector_ignores_does_not(string type)
    {
        Assert.False(KanbanProjector.MovesTheBoard(type));
    }

    [Fact]
    public void Each_team_whose_card_moved_is_named_once()
    {
        var teams = KanbanChange.TeamsToNotify(
        [
            Row(1, MessageTypes.Started, "wave9/Manager"),
            Row(2, MessageTypes.Completed, "wave9/DeveloperAsha"),
            Row(3, MessageTypes.RepoPushed, "wave8/Manager"),
            Row(4, MessageTypes.WorkflowCompleted, "wave8/Manager"),
        ]);

        Assert.Equal(["wave9", "wave8"], teams);
    }

    [Fact]
    public void Rows_that_move_no_card_notify_nobody()
    {
        Assert.Empty(KanbanChange.TeamsToNotify([Row(1, MessageTypes.RepoPushed, "wave9/Manager")]));
    }
}
