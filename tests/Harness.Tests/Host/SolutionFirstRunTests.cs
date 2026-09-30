using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Solutions;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A SCHEDULE'S FIRST RUN AT INSTALL: a schedule with <c>runAtInstall</c> fires once, right after the
/// install's last step, through the fire its schedule makes, and then on its interval; one without
/// it waits for its first due time; a failed step makes no fire. The result names each schedule's
/// first run.
/// </summary>
public sealed class SolutionFirstRunTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Get<T>() where T : notnull => host.Services.GetRequiredService<T>();

    private static readonly SolutionActor Person = new("person-id", "person@example.test");

    /// <summary>A copy of the sample with <paramref name="change"/> made to its solution.json.</summary>
    private string Package(Action<System.Text.Json.Nodes.JsonObject>? change = null)
    {
        var folder = SolutionSamples.JobTracker(
            "1.0.0",
            Path.Combine(Get<TeamDocuments>().EnsureFor(host.Alpha), "packages", Guid.NewGuid().ToString("N")));
        if (change is not null) SolutionSamples.Edit(folder, change);
        return folder;
    }

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..6]}";

    private static SolutionDone Done(SolutionOutcome outcome) =>
        outcome.Body as SolutionDone ?? throw new Xunit.Sdk.XunitException(JsonSerializer.Serialize(outcome.Body));

    /// <summary>The instructions trigger <paramref name="id"/> has appended to Scout: its fires.</summary>
    private async Task<IReadOnlyList<Message>> FiresAsync(string team, string id) =>
        [.. (await Get<IMessageLog>().ReadAfterAsync(
                0, [MessageTypes.InstructionFor(new ContainerId(team, "Scout"))], int.MaxValue, Ct))
            .Where(m => m.Source == $"schedule:{id}")];

    [Fact]
    public async Task RunAtInstall_fires_once_at_install_through_the_schedules_own_fire_and_next_on_its_interval()
    {
        // Not idle-only here, so the interval's fire below cannot be skipped while the first run is
        // still in hand: the point is when it comes due, not whether Scout is idle.
        var folder = Package(m => m["triggers"]![0]!["idleOnly"] = false);

        var done = Done(await Get<SolutionInstaller>().InstallAsync(new(folder, Unique("First run")), Person, Ct));
        var team = done.Team;
        var id = (await Get<ITeamSolutionStore>().FindAsync(team, Ct))!.Triggers["Scan for postings"];

        // EXACTLY ONE FIRE AT INSTALL, the schedule's own: source schedule:<id>, its wakeManager.
        var fire = Assert.Single(await FiresAsync(team, id));
        Assert.Contains($"\"{PayloadFields.WakeManager}\":\"never\"", fire.Payload);
        Assert.NotNull(await Get<ITenantLog>().FindLatestAsync(TenantActions.ScheduleFired, id, Ct));

        // Run now's own call, recorded as the installing person's.
        var installed = await Get<ITenantLog>().FindLatestAsync(TenantActions.ScheduleRunAtInstall, id, Ct);
        Assert.Equal(Person.Email, installed?.ActorEmail);

        var scan = Assert.Single(done.FirstRuns, r => r.Trigger == "Scan for postings");
        Assert.True(scan.RunAtInstall);
        Assert.True(scan.RanNow);
        Assert.Equal(SolutionFirstRun.Fired, scan.Outcome);

        // THEN ON ITS INTERVAL, from the first run.
        var row = (await Get<ITriggerStore>().FindAsync(id, Ct))!;
        Assert.Equal("fired", row.LastOutcome);
        Assert.Equal(scan.At!.Value.AddSeconds(3600), row.NextDueAt!.Value);
        Assert.Equal(row.NextDueAt, scan.Next);

        var sweep = Get<TriggerSweep>();
        await sweep.FireDueAsync(row.NextDueAt.Value.AddSeconds(-1), Ct);
        Assert.Single(await FiresAsync(team, id));

        await sweep.FireDueAsync(row.NextDueAt.Value, Ct);
        Assert.Equal(2, (await FiresAsync(team, id)).Count);

        // The other schedule has no runAtInstall: it first runs when it comes due.
        var morning = Assert.Single(done.FirstRuns, r => r.Trigger == "Morning summary");
        Assert.False(morning.RanNow);
        Assert.Equal(SolutionFirstRun.Scheduled, morning.Outcome);
        var morningRow = (await Get<ITriggerStore>().ListForTeamAsync(team, Ct)).Single(t => t.Name == "Morning summary");
        Assert.Equal(morningRow.NextDueAt, morning.At);
        Assert.Null(morningRow.LastFiredAt);

        // Only schedules have a first run to name.
        Assert.Equal(["Scan for postings", "Morning summary"], done.FirstRuns.Select(r => r.Trigger));
    }

    [Fact]
    public async Task Without_runAtInstall_nothing_fires_at_install_and_the_result_names_the_first_due_time()
    {
        var folder = Package(m => m["triggers"]![0]!.AsObject().Remove("runAtInstall"));

        var done = Done(await Get<SolutionInstaller>().InstallAsync(new(folder, Unique("No first run")), Person, Ct));
        var id = (await Get<ITeamSolutionStore>().FindAsync(done.Team, Ct))!.Triggers["Scan for postings"];

        Assert.Empty(await FiresAsync(done.Team, id));
        Assert.Null(await Get<ITenantLog>().FindLatestAsync(TenantActions.ScheduleFired, id, Ct));

        var row = (await Get<ITriggerStore>().FindAsync(id, Ct))!;
        Assert.Null(row.LastFiredAt);
        var scan = Assert.Single(done.FirstRuns, r => r.Trigger == "Scan for postings");
        Assert.Equal((false, false, SolutionFirstRun.Scheduled), (scan.RunAtInstall, scan.RanNow, scan.Outcome));
        Assert.Equal(row.NextDueAt, scan.At);
    }
}
