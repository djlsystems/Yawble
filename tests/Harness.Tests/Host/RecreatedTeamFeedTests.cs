using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// <c>GET /api/messages</c> reads a team's rows from its creation. A team's identifier is derived
/// from its name, so a team deleted and created again under the same name has the same one, and a
/// match by identifier alone answers every row its predecessor wrote. The feed and a member's
/// history page cut each team's rows at the log position its creation fell at, as the activity
/// read does.
///
/// <para>
/// Each test stands up its own host: what is under test is which rows a read answers, and a
/// neighbouring test's rows would be among them.
/// </para>
/// </summary>
public sealed class RecreatedTeamFeedTests
{
    private const string Label = "Phoenix";

    [Fact]
    public async Task A_recreated_teams_feed_answers_none_of_its_predecessors_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HostAsync();
        var log = host.Services.GetRequiredService<IMessageLog>();
        using var person = await host.PersonAsync();

        var team = await CreateAsync(host, ct);
        var manager = new ContainerId(team, TeamRegistry.DefaultManagerName);

        // The predecessor's Manager publishes, and a person tells it something.
        var predecessor = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            predecessor.Add((await log.AppendAsync(
                new NewMessage(MessageTypes.Completed, $$"""{"output":"old {{i}}"}""", manager.ToString()), ct)).Seq);
        }
        predecessor.Add((await log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(manager), """{"instruction":"old"}""", "person-1"), ct)).Seq);

        await DeleteAsync(person, team, ct);
        Assert.Equal(team, await CreateAsync(host, ct));

        // The new team's Manager publishes, a person tells it something (a row of no team of its own:
        // its team is its type's), and a person's row no team can be read off answers as it always has.
        var successor = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            successor.Add((await log.AppendAsync(
                new NewMessage(MessageTypes.Completed, $$"""{"output":"new {{i}}"}""", manager.ToString()), ct)).Seq);
        }
        successor.Add((await log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(manager), """{"instruction":"new"}""", "person-1"), ct)).Seq);
        var teamless = (await log.AppendAsync(
            new NewMessage(MessageTypes.Completed, """{"output":"nobody's"}""", "console"), ct)).Seq;

        var seen = await SeqsAsync(person, "/api/messages?after=0&take=2000", ct);
        Assert.All(predecessor, seq => Assert.DoesNotContain(seq, seen));
        Assert.All(successor, seq => Assert.Contains(seq, seen));
        Assert.Contains(teamless, seen);

        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            manager.ToString(), PrincipalKind.Container, team, Permits.All, ct: ct);
        using var container = host.Container(key);

        var its = await SeqsAsync(container, "/api/messages?after=0&take=2000", ct);
        Assert.All(predecessor, seq => Assert.DoesNotContain(seq, its));
        Assert.All(successor, seq => Assert.Contains(seq, its));
    }

    [Fact]
    public async Task A_recreated_teams_manager_history_never_pages_into_its_predecessors_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HostAsync();
        var log = host.Services.GetRequiredService<IMessageLog>();
        using var person = await host.PersonAsync();

        var team = await CreateAsync(host, ct);
        var manager = new ContainerId(team, TeamRegistry.DefaultManagerName);
        var beta = new ContainerId(host.Beta, TeamRegistry.DefaultManagerName);

        var predecessor = new List<long>();
        var existing = new List<long>
        {
            (await log.AppendAsync(new NewMessage(
                MessageTypes.InstructionFor(beta), """{"instruction":"first"}""", "person-1"), ct)).Seq,
        };

        for (var i = 0; i < 4; i++)
        {
            predecessor.Add((await log.AppendAsync(
                new NewMessage(MessageTypes.Completed, $$"""{"output":"old {{i}}"}""", manager.ToString()), ct)).Seq);
            existing.Add((await log.AppendAsync(
                new NewMessage(MessageTypes.Completed, $$"""{"output":"beta {{i}}"}""", beta.ToString()), ct)).Seq);
        }

        await DeleteAsync(person, team, ct);
        Assert.Equal(team, await CreateAsync(host, ct));

        // THE TEAM'S CREATION IS THE FLOOR BENEATH THE MEMBER'S OWN. A member created with its team is
        // floored at the log's head already; one whose stored floor does not reach that far (0 here)
        // still never pages below the team's creation.
        await host.Services.GetRequiredService<ITeamStore>().SetFloorAsync(team, manager.Name, 0, ct);

        var successor = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            successor.Add((await log.AppendAsync(
                new NewMessage(MessageTypes.Completed, $$"""{"output":"new {{i}}"}""", manager.ToString()), ct)).Seq);
            existing.Add((await log.AppendAsync(
                new NewMessage(MessageTypes.Completed, $$"""{"output":"beta late {{i}}"}""", beta.ToString()), ct)).Seq);
        }

        var head = await log.HighestSeqAsync(ct) + 1;

        var walked = await WalkAsync(person, manager, head, ct);
        Assert.All(predecessor, seq => Assert.DoesNotContain(seq, walked));
        Assert.Equal(successor.OrderDescending(), walked);

        // A team never re-created pages exactly as before: every row of its Manager's, newest first,
        // down to the first thing it was told.
        Assert.Equal(existing.OrderDescending(), await WalkAsync(person, beta, head, ct));
    }

    [Fact]
    public async Task A_team_never_recreated_answers_the_same_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HostAsync();
        var log = host.Services.GetRequiredService<IMessageLog>();
        using var person = await host.PersonAsync();

        var alpha = new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName);
        var beta = new ContainerId(host.Beta, TeamRegistry.DefaultManagerName);

        // Rows of both teams by source, by type and by payload, and a row of no team.
        foreach (var manager in new[] { alpha, beta })
        {
            var told = await log.AppendAsync(new NewMessage(
                MessageTypes.InstructionFor(manager), """{"instruction":"go"}""", "person-1"), ct);
            await log.AppendAsync(new NewMessage(MessageTypes.Completed, """{"output":"done"}""", manager.ToString(), told.Seq), ct);
            await log.AppendAsync(new NewMessage(
                MessageTypes.WorkflowClosed, $$"""{"team":"{{manager.Team}}","reason":"done"}""", "person-1", told.Seq), ct);
        }
        await log.AppendAsync(new NewMessage(MessageTypes.Completed, """{"output":"nobody's"}""", "console"), ct);

        var teams = new HashSet<string>([host.Alpha, host.Beta], StringComparer.OrdinalIgnoreCase);

        // What the route answered before the creation cut: the window, kept by MessageTeam.Of alone.
        var everything = (await log.ReadLatestAsync(0, FeedTypes.Explicit, 2000, ct))
            .Where(m => MessageTeam.Of(m) is not { } team || teams.Contains(team))
            .Select(m => m.Seq);
        Assert.Equal(everything, await SeqsAsync(person, "/api/messages?after=0&take=2000", ct));

        var alphas = (await log.ReadLatestAsync(0, FeedTypes.Explicit, [host.Alpha], 2000, ct))
            .Where(m => MessageTeam.Of(m) is { } team && string.Equals(team, host.Alpha, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Seq);
        using var container = host.Container(host.AlphaContainerKey);
        Assert.Equal(alphas, await SeqsAsync(container, "/api/messages?after=0&take=2000", ct));
    }

    private static async Task<HostFixture> HostAsync()
    {
        var host = new HostFixture();
        await host.InitializeAsync();
        return host;
    }

    /// <summary>The team <see cref="Label"/>, paused, so an instruction is a row and not a run.</summary>
    private static async Task<string> CreateAsync(HostFixture host, CancellationToken ct)
    {
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;

        var team = (await registry.CreateAsync(Label, agent, memberAgent: agent, ct: ct)).Id;
        await registry.SetPausedAsync(team, paused: true, ct);
        return team;
    }

    private static async Task DeleteAsync(HttpClient person, string team, CancellationToken ct)
    {
        var deleted = await person.DeleteAsync($"/api/teams/{team}", ct);
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync(ct));
    }

    private static async Task<List<long>> SeqsAsync(HttpClient client, string url, CancellationToken ct)
    {
        var response = await client.GetAsync(url, ct);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}");
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray()
            .Select(row => row.GetProperty("seq").GetInt64())];
    }

    /// <summary>A member's history from <paramref name="head"/> down, two rows a page, to an empty page.</summary>
    private static async Task<List<long>> WalkAsync(
        HttpClient person, ContainerId member, long head, CancellationToken ct)
    {
        var all = new List<long>();
        var before = head;

        for (var page = 0; page < 500; page++)
        {
            var got = await SeqsAsync(
                person,
                $"/api/messages?team={Uri.EscapeDataString(member.Team)}&member={Uri.EscapeDataString(member.Name)}"
                + $"&take=2&before={before}",
                ct);

            if (got.Count == 0) return all;

            all.AddRange(got);
            before = got[^1];
        }

        throw new InvalidOperationException("The cursor never reached an empty page.");
    }
}
