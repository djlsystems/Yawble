using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host.Auth;

namespace Harness.Tests.Host;

/// <summary>
/// A container credential on any team is refused another team's route with the
/// same status and body a missing team gets, across the verbs a Manager uses on its own
/// team - tell, hire, move a card, read status - while a person (session and API key) and the
/// tenant Concierge still reach every team.
///
/// Each refusal is compared with the SAME request against a team that does not exist, made by the
/// same credential, and every target on the other team is real (its Manager, one of its cards), so
/// a gate that leaked would show up as a handler's answer rather than as the missing team's.
/// </summary>
public sealed class CrossTeamTests(CrossTeamFixture host) : IClassFixture<CrossTeamFixture>
{
    private const string Missing = "no-such-team";

    public static TheoryData<string, string> Callers => new()
    {
        { "Alpha", "Manager" },
        { "Alpha", "Worker" },
        { "Beta", "Manager" },
        { "Beta", "Worker" },
    };

    public static TheoryData<string> Verbs => new()
    {
        "tell", "hire", "card-move", "status-member", "status-messages", "hiring", "board",
    };

    public static TheoryData<string, string, string> CallersAndVerbs
    {
        get
        {
            var data = new TheoryData<string, string, string>();

            foreach (var (team, member) in new[] { ("Alpha", "Manager"), ("Alpha", "Worker"), ("Beta", "Manager"), ("Beta", "Worker") })
            {
                foreach (var verb in Verbs) data.Add(team, member, verb.Data);
            }

            return data;
        }
    }

    private string Team(string label) => label == "Alpha" ? host.Alpha : host.Beta;

    private string Other(string label) => label == "Alpha" ? host.Beta : host.Alpha;

    private string CallerKey(string team, string member) =>
        host.ContainerKeys[CrossTeamFixture.Key(Team(team), member == "Manager" ? host.ManagerName : member)];

    /// <summary>
    /// The request each verb makes against <paramref name="team"/>. The card id is always the
    /// OTHER-team card when one is asked for, so the missing-team request carries the same id.
    /// </summary>
    private HttpRequestMessage Request(string verb, string team, string card, string tag)
    {
        var manager = host.ManagerName;

        return verb switch
        {
            "tell" => Post($"/api/teams/{team}/containers/{manager}/tell", new { instruction = $"Cross-team tell {tag}." }),
            "hire" => Post($"/api/teams/{team}/containers", new { name = $"Hire{tag}" }),
            "card-move" => Post($"/api/teams/{team}/kanban/cards/{card}/move", new { laneId = "done" }),
            "status-member" => new HttpRequestMessage(HttpMethod.Get, $"/api/teams/{team}/containers/{manager}"),
            "status-messages" => new HttpRequestMessage(HttpMethod.Get, $"/api/teams/{team}/containers/{manager}/messages?take=20"),
            "hiring" => new HttpRequestMessage(HttpMethod.Get, $"/api/teams/{team}/hiring"),
            "board" => new HttpRequestMessage(HttpMethod.Get, $"/api/teams/{team}/kanban/board"),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null),
        };
    }

    private static HttpRequestMessage Post(string path, object body) =>
        new(HttpMethod.Post, path) { Content = JsonContent.Create(body) };

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    [Theory]
    [MemberData(nameof(CallersAndVerbs))]
    public async Task A_container_is_refused_another_teams_verb_with_what_a_missing_team_gets(
        string team, string member, string verb)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = host.WithKey(CallerKey(team, member));
        var other = Other(team);
        var card = host.Cards[other];

        using var toOther = Request(verb, other, card, Tag());
        using var toMissing = Request(verb, Missing, card, Tag());

        var refused = await client.SendAsync(toOther, ct);
        var missing = await client.SendAsync(toMissing, ct);

        var refusedBody = await refused.Content.ReadAsStringAsync(ct);
        var missingBody = await missing.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(missing.StatusCode, refused.StatusCode);
        Assert.Equal(TeamGate.NoSuchTeam, refusedBody);
        Assert.Equal(missingBody, refusedBody);
        Assert.Equal(missing.Content.Headers.ContentType?.ToString(), refused.Content.Headers.ContentType?.ToString());
    }

    /// <summary>
    /// The control for the refusal above: the same credential and the same reads succeed on its own
    /// team, so the refusal is about the OTHER team and not about the verb or the key.
    /// </summary>
    [Theory]
    [MemberData(nameof(Callers))]
    public async Task A_container_reaches_its_own_team(string team, string member)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = host.WithKey(CallerKey(team, member));
        var own = Team(team);

        foreach (var verb in new[] { "status-member", "status-messages", "hiring", "board" })
        {
            using var request = Request(verb, own, host.Cards[own], Tag());
            var response = await client.SendAsync(request, ct);

            Assert.True(response.IsSuccessStatusCode, $"{verb} on own team answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        }
    }

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task A_refused_cross_team_hire_creates_no_member(string team, string member)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = host.WithKey(CallerKey(team, member));
        var other = Other(team);
        var name = $"Hire{Tag()}";

        var refused = await client.PostAsJsonAsync($"/api/teams/{other}/containers", new { name }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var person = await host.PersonAsync();
        var lookup = await person.GetAsync($"/api/teams/{other}/containers/{name}", ct);

        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task A_refused_cross_team_tell_puts_no_card_on_the_other_board(string team, string member)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = host.WithKey(CallerKey(team, member));
        var other = Other(team);
        var instruction = $"Cross-team tell {Tag()}.";

        var refused = await client.PostAsJsonAsync(
            $"/api/teams/{other}/containers/{host.ManagerName}/tell", new { instruction }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using var person = await host.PersonAsync();
        var board = await person.GetStringAsync($"/api/teams/{other}/kanban/board", ct);

        Assert.DoesNotContain(instruction, board);
    }

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task A_container_sees_only_its_own_team_in_the_overview(string team, string member)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = host.WithKey(CallerKey(team, member));

        var teams = await OverviewTeamsAsync(client, ct);

        Assert.Equal([Team(team)], teams);
    }

    // ---- A person, a person's key, and the tenant Concierge reach every team ----

    public static TheoryData<string> Reachers => new() { "session", "api-key", "concierge" };

    private async Task<HttpClient> ReacherAsync(string reacher) => reacher switch
    {
        "session" => await host.PersonAsync(),
        "api-key" => host.WithKey(host.PersonApiKey),
        "concierge" => host.WithKey(host.ConciergeKey),
        _ => throw new ArgumentOutOfRangeException(nameof(reacher), reacher, null),
    };

    [Theory]
    [MemberData(nameof(Reachers))]
    public async Task A_person_and_a_concierge_see_every_team_in_the_overview(string reacher)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await ReacherAsync(reacher);

        var teams = await OverviewTeamsAsync(client, ct);

        Assert.Equal(new[] { host.Alpha, host.Beta }.Order(StringComparer.OrdinalIgnoreCase), teams);
    }

    [Theory]
    [MemberData(nameof(Reachers))]
    public async Task A_person_and_a_concierge_reach_every_team(string reacher)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await ReacherAsync(reacher);

        // The Concierge holds Read, Tell and CreateContainer, not Progress, so moving a card is a
        // verb it was never given - a permit question, not a team one.
        var verbs = reacher == "concierge"
            ? new[] { "tell", "hire", "status-member", "status-messages", "hiring", "board" }
            : new[] { "tell", "hire", "card-move", "status-member", "status-messages", "hiring", "board" };

        foreach (var team in new[] { host.Alpha, host.Beta })
        {
            foreach (var verb in verbs)
            {
                using var request = Request(verb, team, host.Cards[team], Tag());

                if (verb == "card-move")
                {
                    request.Content = JsonContent.Create(new { laneId = await SomeLaneAsync(client, team, ct) });
                }

                var response = await client.SendAsync(request, ct);

                Assert.True(
                    response.IsSuccessStatusCode,
                    $"{reacher} {verb} on {team} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
            }
        }
    }

    private static async Task<string> SomeLaneAsync(HttpClient client, string team, CancellationToken ct)
    {
        using var board = JsonDocument.Parse(await client.GetStringAsync($"/api/teams/{team}/kanban/board", ct));

        return board.RootElement.GetProperty("lanes").EnumerateArray().Last().GetProperty("id").GetString()!;
    }

    private static async Task<string[]> OverviewTeamsAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.GetAsync("/api/overview", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var overview = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        return overview.RootElement.GetProperty("teams").EnumerateArray()
            .Select(t => t.GetProperty("id").GetString()!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
