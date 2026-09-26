using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Harness.Tests.Host;

/// <summary>
/// A move names a lane the board has, or nothing is written. A card moved to a lane the board lacks
/// would sit in no column at all, and an agent guessing a lane name is easy to imagine.
/// </summary>
public sealed class KanbanMoveLaneTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_move_to_a_lane_the_board_does_not_have_is_refused_naming_the_lanes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/kanban/cards/1/move", new { laneId = "review" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var error = body.RootElement.GetProperty("error").GetString();
        Assert.Contains("'review'", error);
        Assert.Contains("todo, in-progress, blocked, done", error);
    }
}
