using System.Net;
using Harness.Host.Auth;

namespace Harness.Tests.Host;

/// <summary>
/// AGENTS.md, Authorization: a container principal is bound to its own team and is refused another
/// team's with the same body a missing team gets; a person passes to the handler, which answers 404
/// for a team that does not exist. Driven through `GET /api/teams/{team}/hiring`, a Read route whose
/// handler answers its own 404.
/// </summary>
public sealed class TeamGateTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Missing = "no-such-team";

    private static string Hiring(string team) => $"/api/teams/{team}/hiring";

    [Fact]
    public async Task A_container_reaches_its_own_team()
    {
        using var client = host.Container(host.AlphaContainerKey);

        var response = await client.GetAsync(Hiring(host.Alpha), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_container_is_refused_another_team_with_the_body_a_missing_team_gets()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = host.Container(host.AlphaContainerKey);

        var other = await client.GetAsync(Hiring(host.Beta), ct);
        var missing = await client.GetAsync(Hiring(Missing), ct);

        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(missing.StatusCode, other.StatusCode);
        Assert.Equal(TeamGate.NoSuchTeam, await other.Content.ReadAsStringAsync(ct));
        Assert.Equal(await missing.Content.ReadAsStringAsync(ct), await other.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task A_person_on_a_missing_team_gets_the_handlers_404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();

        var response = await client.GetAsync(Hiring(Missing), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains($"No team '{Missing}'.", await response.Content.ReadAsStringAsync(ct));
    }
}
