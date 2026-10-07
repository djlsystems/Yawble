using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Solutions;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A trigger's spend today across a solution update, from Job Tracker 1.0.0 to 1.1.0: New posting
/// is kept (changed), Resume changed is removed and Weekly review is added. Spend is seeded as the
/// ledger records it - a fire's instruction row with the trigger's source and the run's terminal row
/// with measured tokens - for a member no runner serves, so nothing runs and nothing reaches a network.
/// </summary>
public sealed class SolutionUpdateTriggerSpendTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Get<T>() where T : notnull => host.Services.GetRequiredService<T>();

    private SolutionActor Person => new("person-id", "person@example.test");

    private string Package(string version) =>
        SolutionSamples.JobTracker(
            version,
            Path.Combine(Get<TeamDocuments>().EnsureFor(host.Alpha), "packages", Guid.NewGuid().ToString("N")));

    private static SolutionDone Done(SolutionOutcome outcome) =>
        outcome.Body as SolutionDone ?? throw new Xunit.Sdk.XunitException(JsonSerializer.Serialize(outcome.Body));

    private async Task<string> InstallAsync()
    {
        var name = $"Spend {Guid.NewGuid().ToString("N")[..6]}";
        return Done(await Get<SolutionInstaller>().InstallAsync(
            new SolutionInstallRequest(Package("1.0.0"), name, Answers: new SolutionAnswers(
                Settings: new Dictionary<string, Dictionary<string, JsonElement>>
                {
                    ["Scout"] = new() { ["sources"] = JsonSerializer.SerializeToElement(new[] { "sample" }) },
                })),
            Person, Ct)).Team;
    }

    private async Task UpdateAsync(string team) =>
        Done(await Get<SolutionInstaller>().UpdateAsync(new SolutionUpdateRequest(Package("1.1.0"), team), Person, Ct));

    private async Task<TriggerRow> TriggerAsync(string team, string name) =>
        (await Get<ITriggerStore>().ListForTeamAsync(team, Ct)).Single(t => t.Name == name);

    /// <summary>One measured run that answered a fire of <paramref name="trigger"/> today.</summary>
    private async Task SpendAsync(TriggerRow trigger, int tokensIn, int tokensOut)
    {
        var log = Get<IMessageLog>();
        var ghost = new ContainerId(trigger.Team, "Ledger-only");

        var fire = await log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(ghost),
            JsonSerializer.Serialize(new { instruction = "seeded" }),
            $"trigger:{trigger.Id}"), Ct);

        await log.AppendAsync(new NewMessage(
            MessageTypes.Completed,
            JsonSerializer.Serialize(new { tokensIn, tokensOut, tokensSource = "test" }),
            ghost.ToString(),
            fire.Seq), Ct);
    }

    private Task<WorkflowSpend> SpentTodayAsync(TriggerRow trigger) =>
        Get<TriggerCost>().SpentTodayAsync(trigger, DateTimeOffset.UtcNow, Ct);

    [Fact]
    public async Task A_trigger_kept_by_an_update_reads_the_same_spend_today_before_and_after()
    {
        var team = await InstallAsync();
        var before = await TriggerAsync(team, "New posting");
        await SpendAsync(before, 30000, 4682);

        // MEASURED spend only: a Writer run the install's postings woke reports no usage here, and
        // may end between the two reads as an unmeasured run.
        var spentBefore = await SpentTodayAsync(before);
        Assert.Equal((34682, 1), (spentBefore.TokensSpent, spentBefore.RunsWithMeasuredUsage));

        await UpdateAsync(team);

        var after = await TriggerAsync(team, "New posting");
        Assert.Equal(250000, after.DailyTokenCap);
        var spentAfter = await SpentTodayAsync(after);
        Assert.Equal((34682, 1), (spentAfter.TokensSpent, spentAfter.RunsWithMeasuredUsage));

        // What every trigger route and the solution panel show reads the same.
        var view = await Get<TriggerCost>().ViewAsync(after, DateTimeOffset.UtcNow, Ct);
        Assert.Equal(34682, view["spentToday"]!["billableTokens"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_kept_triggers_cap_holds_across_the_update_counting_what_it_spent_before()
    {
        var team = await InstallAsync();
        var before = await TriggerAsync(team, "New posting");

        // Under the 1.1.0 cap of 250,000 alone, over it with the 1.0.0 spend counted.
        await SpendAsync(before, 240000, 15000);

        await UpdateAsync(team);

        var after = await TriggerAsync(team, "New posting");
        var cause = await Get<IMessageLog>().AppendAsync(new NewMessage(
            "plugin.job-board.posting-found", JsonSerializer.Serialize(new { title = "Engineer" }), $"{team}/Scout"), Ct);

        // The fire the pump would make next is skipped as capped. The day's one logged skip may be
        // this one or an earlier fire's - Scout's run at install publishes postings too - so the
        // row is looked for over the whole log.
        Assert.True(await Get<TriggerCost>().SkipIfCappedAsync(
            after, new ContainerId(team, after.Container), DateTimeOffset.UtcNow, nextDueAt: null, cause.Seq, Ct));

        var skipped = Assert.Single(
            await Get<IMessageLog>().ReadAfterAsync(0, [MessageTypes.ScheduleSkipped], int.MaxValue, Ct),
            m => m.Source == $"trigger:{after.Id}");
        Assert.Equal(MessageTypes.ScheduleSkippedCapReason,
            JsonDocument.Parse(skipped.Payload).RootElement.GetProperty("reason").GetString());

        var stored = (await Get<ITriggerStore>().FindAsync(after.Id, Ct))!;
        Assert.Equal("capped", stored.LastOutcome);
        Assert.True(stored.CappedSkips >= 1);
    }

    [Fact]
    public async Task A_trigger_an_update_removes_and_one_it_adds_are_separate_and_the_added_one_starts_at_zero()
    {
        var team = await InstallAsync();
        var removed = await TriggerAsync(team, "Resume changed");
        await SpendAsync(removed, 50000, 5000);

        await UpdateAsync(team);

        var triggers = await Get<ITriggerStore>().ListForTeamAsync(team, Ct);
        Assert.DoesNotContain(triggers, t => t.Name == "Resume changed");
        Assert.Null(await Get<ITriggerStore>().FindAsync(removed.Id, Ct));

        var added = triggers.Single(t => t.Name == "Weekly review");
        Assert.NotEqual(removed.Id, added.Id);
        Assert.Equal(new WorkflowSpend(0, 0, 0), await SpentTodayAsync(added));

        // No trigger the update left reads the removed one's spend.
        foreach (var trigger in triggers)
        {
            Assert.Equal(0, (await SpentTodayAsync(trigger)).TokensSpent);
        }
    }
}
