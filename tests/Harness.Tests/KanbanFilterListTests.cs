using Harness.Contracts;
using Harness.Kanban;

namespace Harness.Tests;

/// <summary>
/// EACH BOARD FILTER TAKES SEVERAL VALUES, comma-separated: a card is kept when it matches ANY value
/// of a filter, and every filter given must hold. A single value is a list of one.
/// </summary>
public sealed class KanbanFilterListTests
{
    private static Message Row(long seq, string type, string payload) =>
        new(seq, type, payload, "person-1", seq, null, 0, DateTimeOffset.UnixEpoch);

    private static readonly Message[] Log =
    [
        Row(1, MessageTypes.KanbanCardPlanned, """{"team":"Alpha","title":"One"}"""),
        Row(2, MessageTypes.KanbanCardPlanned, """{"team":"Beta","title":"Two"}"""),
        Row(3, MessageTypes.KanbanCardPlanned, """{"team":"Gamma","title":"Three"}"""),
    ];

    private static List<string> Titles(KanbanFilter filter) =>
        [.. KanbanProjector.Project(Log, filter).Cards.Select(c => c.Title).Order()];

    [Fact]
    public void Several_teams_keep_a_card_of_any_of_them_whatever_their_case_and_spacing()
    {
        Assert.Equal(["One", "Two"], Titles(new KanbanFilter(Team: "alpha, Beta")));
        Assert.Equal(["Three"], Titles(new KanbanFilter(Team: "Gamma")));
        Assert.Equal(["One", "Three", "Two"], Titles(new KanbanFilter(Team: " , ")));
    }

    [Fact]
    public void Several_statuses_keep_a_card_in_any_of_them_and_every_filter_must_hold()
    {
        var status = KanbanProjector.Project(Log).Cards.First().Status!;

        Assert.Equal(["One", "Three", "Two"], Titles(new KanbanFilter(Status: $"no-such-status,{status}")));
        Assert.Equal(["One", "Three"], Titles(new KanbanFilter(Team: "Alpha,Gamma", Status: $"no-such-status,{status}")));
        Assert.Empty(Titles(new KanbanFilter(Team: "Alpha", Status: "no-such-status")));
    }

    [Fact]
    public void A_value_is_split_on_commas_and_empty_parts_are_dropped()
    {
        Assert.Equal(["a", "b"], KanbanFilter.Values(" a ,, b ,"));
        Assert.Empty(KanbanFilter.Values(null));
        Assert.Empty(KanbanFilter.Values("   "));
    }
}
