using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Host;

namespace Harness.Tests.Host;

/// <summary>
/// A card's own times go out as instants, with their offset, as its trail's do. A UTC time sent with
/// no offset is read as local by every browser, so a panel showed "Created" hours away from the
/// trail row for the same moment.
/// </summary>
public sealed partial class KanbanCardTimesTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"(Z|[+-]\d\d:\d\d)$")]
    private static partial Regex HasOffset();

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    [Fact]
    public async Task A_cards_created_and_moved_times_carry_an_offset_and_match_its_trail()
    {
        using var person = await host.PersonAsync();
        var told = await person.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers/{TeamRegistry.DefaultManagerName}/tell",
            new { instruction = "read the openings" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
        var workflow = (await JsonAsync(told)).GetProperty("correlationId").GetInt64();

        var board = await JsonAsync(await person.GetAsync($"/api/teams/{host.Alpha}/kanban/board", Ct));
        var id = board.GetProperty("cards").EnumerateArray()
            .Single(c => c.GetProperty("workflowSeq").GetInt64() == workflow).GetProperty("id").GetString();
        var card = await JsonAsync(await person.GetAsync($"/api/teams/{host.Alpha}/kanban/cards/{id}", Ct));

        var created = card.GetProperty("createdAt").GetString()!;
        var updated = card.GetProperty("updatedAt").GetString()!;
        Assert.Matches(HasOffset(), created);
        Assert.Matches(HasOffset(), updated);

        var told0 = card.GetProperty("trail").EnumerateArray().First().GetProperty("occurredAt").GetString()!;
        Assert.Equal(DateTimeOffset.Parse(told0), DateTimeOffset.Parse(created));
    }
}
