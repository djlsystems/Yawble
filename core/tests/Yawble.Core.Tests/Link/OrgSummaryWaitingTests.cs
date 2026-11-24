using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Link;
using Xunit;

namespace Yawble.Core.Tests.Link;

/// <summary>
/// Card 568 (B126 core follow-up): org.summary's teams section counts each agent by its real waiting state (queued only
/// for the run queue; waitsForRun, held, pending) and lists who waits and why in <c>waiting</c>, with waitsFor only on
/// waits-for-run. The names are those yawble-cloud's OrgWire reads. Fakes only.
/// </summary>
public class OrgSummaryWaitingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static JsonNode Team(params OrgAgent[] agents)
    {
        var facts = new OrgSummaryFacts(new OrgComputer("DESK-1", null, "1", "Windows", Now.AddHours(-1), 1, 4, 0),
            [new OrgTeam("web", Paused: false, Archived: false, agents)], [], [], []);
        return Assert.Single(OrgSummary.Build(facts, Now)["teams"]!.AsArray())!;
    }

    [Fact]
    public void EachWaitState_IsCounted_AndListed_WaitsForOnlyOnWaitsForRun()
    {
        var team = Team(
            new OrgAgent("Manager", "Manager", "running", false, "card 7", Now.AddMinutes(-5)),
            new OrgAgent("Ines", "Member", WaitStates.Queued, false),
            new OrgAgent("Marlo", "Member", WaitStates.WaitsForRun, false, WaitsFor: "Ines"),
            new OrgAgent("Wren", "Member", WaitStates.Held, false, WaitsFor: "ignored"),
            new OrgAgent("Tomas", "Member", WaitStates.Pending, true),
            new OrgAgent("Priya", "Member", "idle", false));

        var agents = team["agents"]!;
        Assert.Equal(6, agents["total"]!.GetValue<int>());
        Assert.Equal(1, agents["running"]!.GetValue<int>());
        Assert.Equal(1, agents["queued"]!.GetValue<int>());
        Assert.Equal(1, agents["waitsForRun"]!.GetValue<int>());
        Assert.Equal(1, agents["held"]!.GetValue<int>());
        Assert.Equal(1, agents["pending"]!.GetValue<int>());
        // Waiting agents are not idle, and a waiting one whose last run failed is not errored either.
        Assert.Equal(1, agents["idle"]!.GetValue<int>());
        Assert.Equal(0, agents["errored"]!.GetValue<int>());

        Assert.Equal(
            """[{"member":"Ines","state":"queued","waitsFor":null},{"member":"Marlo","state":"waits-for-run","waitsFor":"Ines"},{"member":"Wren","state":"held","waitsFor":null},{"member":"Tomas","state":"pending","waitsFor":null}]""",
            team["waiting"]!.ToJsonString());
    }

    [Fact]
    public void NobodyWaiting_ZeroCounts_EmptyList()
    {
        var team = Team(new OrgAgent("Manager", "Manager", "idle", false));
        Assert.Equal([0, 0, 0, 0], new[] { "queued", "waitsForRun", "held", "pending" }.Select(k => team["agents"]![k]!.GetValue<int>()));
        Assert.Empty(team["waiting"]!.AsArray());
    }

    [Fact]
    public void AnUnknownNotRunningState_IsPending_NotIdle()
    {
        var team = Team(new OrgAgent("Ada", "Member", "something-new", false));
        Assert.Equal((1, 0), (team["agents"]!["pending"]!.GetValue<int>(), team["agents"]!["idle"]!.GetValue<int>()));
        Assert.Equal("pending", team["waiting"]![0]!["state"]!.GetValue<string>());
    }
}
