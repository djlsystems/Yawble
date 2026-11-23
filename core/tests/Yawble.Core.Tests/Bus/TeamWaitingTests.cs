using Yawble.Core.Bus;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>B130: a team whose member has an open needs_decision counts it in TeamSummary.Waiting (the team pill's
/// "Waiting on you"), by the same rule as the member card's DecisionId; the person's answer brings it back to 0.</summary>
public class TeamWaitingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMembersOpenDecisionCountsAsWaitingUntilThePersonAnswers()
    {
        using var db = new TempDb();
        var runs = 0;
        var runner = new ScriptedRunner().On("Alpha/Di", async (run, ct) =>
        {
            // The first run asks; the answer's run only reads it.
            if (Interlocked.Increment(ref runs) == 1) await run.NeedsDecisionAsync("which db?", ct);
            return RunResult.Ok;
        });
        await using var bus = new BusEngine(db.OpenStore(), runner, new BusOptions { IdleOffer = false });
        var teamId = await bus.CreateTeamAsync("Alpha", ct: Ct);
        await bus.CreateTeamAsync("Beta", ct: Ct);
        await bus.AddMemberAsync("Alpha", "Di", ct: Ct);
        await bus.StartAsync(Ct);

        Assert.All(await bus.TeamsAsync(Ct), t => Assert.Equal(0, t.Waiting));

        await bus.PersonTellAsync("Alpha", "Di", "pick the database", ct: Ct);
        await Wait.UntilAsync(() => bus.IsQuiescentAsync(Ct), "quiet");

        var teams = await bus.TeamsAsync(Ct);
        Assert.Equal(1, teams.Single(t => t.Name == "Alpha").Waiting);
        Assert.Equal(0, teams.Single(t => t.Name == "Beta").Waiting);
        var decision = (await bus.AgentCardsAsync(teamId, Ct)).Single(c => c.Member.Name == "Di").DecisionId;
        Assert.NotNull(decision);

        await bus.PersonAnswerAsync("Alpha", "Di", "Postgres", decision!.Value, ct: Ct);
        await Wait.UntilAsync(() => bus.IsQuiescentAsync(Ct), "quiet after the answer");

        Assert.Equal(0, (await bus.TeamsAsync(Ct)).Single(t => t.Name == "Alpha").Waiting);
        Assert.Null((await bus.AgentCardsAsync(teamId, Ct)).Single(c => c.Member.Name == "Di").DecisionId);
    }
}
