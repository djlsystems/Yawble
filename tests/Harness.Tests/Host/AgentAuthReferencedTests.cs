using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A seeded preset nobody runs, never signed in, must not light the red "installed but
/// not authenticated" banner for every person. `GET /api/agents/auth` says which presets are in use
/// - by a team's member, a team's hiring allowlist or the Concierge - and the banner warns only
/// about those.
/// </summary>
public sealed class AgentAuthReferencedTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task Each_report_says_whether_a_team_or_the_concierge_uses_its_preset()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = host.Services.GetRequiredService<AgentCatalog>();
        var chosen = catalog.Definitions.Last(d => d.Mode == AgentMode.Headless).Name;
        await host.Services.GetRequiredService<TeamRegistry>().SetMemberAgentsAsync(host.Alpha, [chosen], ct);
        var expected = await AgentReferences.OfAsync(host.Services.GetRequiredService<ITeamStore>(), ct);

        using var client = await host.PersonAsync();
        var reports = await client.GetFromJsonAsync<JsonElement[]>("/api/agents/auth", ct);

        Assert.NotNull(reports);
        Assert.Equal(catalog.Definitions.Count, reports.Length);
        Assert.True(Assert.Single(reports, r => r.GetProperty("agent").GetString() == chosen)
            .GetProperty("referenced").GetBoolean());
        Assert.All(reports, report => Assert.Equal(
            expected.Contains(report.GetProperty("agent").GetString()!),
            report.GetProperty("referenced").GetBoolean()));
    }

    [Fact]
    public async Task Antigravity_is_not_reported()
    {
        using var client = await host.PersonAsync();
        var reports = await client.GetFromJsonAsync<JsonElement[]>(
            "/api/agents/auth", TestContext.Current.CancellationToken);

        Assert.NotNull(reports);
        Assert.DoesNotContain(reports, r => r.GetProperty("command").GetString() == "agy"
            || r.GetProperty("agent").GetString()!.Contains("antigravity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Marking_matches_a_preset_name_without_regard_to_case()
    {
        var reports = new[]
        {
            new AgentAuthReport("Grok-Headless", "grok", true, true, ""),
            new AgentAuthReport("codex-headless", "codex", true, false, "not signed in"),
        };

        var marked = AgentAuthReport.MarkReferenced(
            reports, new HashSet<string>(["grok-headless"], StringComparer.OrdinalIgnoreCase));

        Assert.True(marked[0].Referenced);
        Assert.False(marked[1].Referenced);
    }
}
