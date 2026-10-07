using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Solutions;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A SOLUTION PACKAGE'S TRIGGER MAY NAME AN OUTCOME BY NAME. The install - a person's, so a person's
/// confirmation - creates it <c>active</c> when no live outcome has the name, or uses the one that
/// does; the trigger stores its id, and its fire links the workflow it roots (<c>how: trigger</c>).
/// </summary>
public sealed class OutcomeSolutionTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Get<T>() where T : notnull => host.Services.GetRequiredService<T>();

    private static readonly SolutionActor Person = new("person-id", "person@example.test");

    private string Package(string outcome)
    {
        var folder = SolutionSamples.JobTracker(
            "1.0.0",
            Path.Combine(Get<TeamDocuments>().EnsureFor(host.Alpha), "packages", Guid.NewGuid().ToString("N")));

        SolutionSamples.Edit(folder, m =>
        {
            m["triggers"]![0]!["outcome"] = outcome;
            m["triggers"]![0]!["runAtInstall"] = true;
            m["triggers"]![0]!["idleOnly"] = false;

            // No Resume to wait for, or the team would skip its first run until one arrives.
            m["inputs"]!["documents"]![0]!["required"] = false;
        });

        return folder;
    }

    private static SolutionDone Done(SolutionOutcome outcome) =>
        outcome.Body as SolutionDone ?? throw new Xunit.Sdk.XunitException(JsonSerializer.Serialize(outcome.Body));

    [Fact]
    public async Task An_installed_trigger_naming_an_outcome_creates_it_active_and_its_fire_links_the_workflow()
    {
        var name = $"Maintain a current pipeline of qualified job openings {Guid.NewGuid():N}";

        var done = Done(await Get<SolutionInstaller>().InstallAsync(new(Package(name), $"Tracker {Guid.NewGuid():N}"[..16]), Person, Ct));
        var triggerId = (await Get<ITeamSolutionStore>().FindAsync(done.Team, Ct))!.Triggers["Scan for postings"];
        var trigger = (await Get<ITriggerStore>().FindAsync(triggerId, Ct))!;

        var outcome = (await Get<IOutcomeStore>().FindLiveByNameAsync(name, Ct))!;
        Assert.Equal((OutcomeStatus.Active, Person.Email, Person.Email), (outcome.Status, outcome.CreatedBy, outcome.ConfirmedBy));
        Assert.Equal(outcome.Id, trigger.OutcomeId);
        Assert.NotNull(await Get<ITenantLog>().FindLatestAsync(TenantActions.OutcomeCreated, outcome.Id, Ct));

        // The first run at install is the schedule's fire, and it linked the workflow it rooted.
        var fire = (await Get<IMessageLog>().FindAsync(trigger.LastSeq!.Value, Ct))!;
        var link = (await Get<IOutcomeStore>().CurrentLinkAsync(fire.CorrelationId, Ct))!;
        Assert.Equal((outcome.Id, OutcomeLinkHow.Trigger, OutcomeActorKind.Person), (link.OutcomeId, link.How, link.SetByKind));

        // A second install naming the same outcome uses it; nothing new is created.
        var count = (await Get<IOutcomeStore>().ListAsync(Ct)).Count;
        var again = Done(await Get<SolutionInstaller>().InstallAsync(new(Package(name), $"Tracker {Guid.NewGuid():N}"[..16]), Person, Ct));
        var second = (await Get<ITriggerStore>().FindAsync(
            (await Get<ITeamSolutionStore>().FindAsync(again.Team, Ct))!.Triggers["Scan for postings"], Ct))!;
        Assert.Equal(outcome.Id, second.OutcomeId);
        Assert.Equal(count, (await Get<IOutcomeStore>().ListAsync(Ct)).Count);
    }
}
