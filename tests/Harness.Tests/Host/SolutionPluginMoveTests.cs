using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Solutions;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A SOLUTION UPDATE MOVES A KEPT PLUGIN MEMBER onto the plugin the package's member now names: Job
/// Tracker with its plugin called <c>&lt;a&gt;</c>, updated to a version that calls it
/// <c>&lt;a&gt;-plugin</c>. The member is repointed with its settings and own secret values as they
/// were, the package's event trigger follows the new plugin's events, one <c>member.changed</c> row
/// names both plugins, a failed update puts the member back, and the preview names the move and a
/// trigger a person made by hand on the old plugin's events. The old plugin stays installed.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SolutionPluginMoveTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Get<T>() where T : notnull => host.Services.GetRequiredService<T>();

    private SolutionActor Person => new("person-id", "person@example.test");

    private static SolutionDone Done(SolutionOutcome outcome) =>
        outcome.Body as SolutionDone ?? throw new Xunit.Sdk.XunitException(JsonSerializer.Serialize(outcome.Body));

    /// <summary>Job Tracker <paramref name="version"/> with its plugin, Scout's, and the package
    /// trigger's event called <paramref name="plugin"/>.</summary>
    private string Package(string plugin, string version)
    {
        var folder = SolutionSamples.JobTracker(
            "1.0.0", Path.Combine(Get<TeamDocuments>().EnsureFor(host.Alpha), "packages", Guid.NewGuid().ToString("N")));
        Directory.Move(Path.Combine(folder, "plugins", "job-board"), Path.Combine(folder, "plugins", plugin));
        SolutionSamples.EditJson(Path.Combine(folder, "plugins", plugin, "plugin.json"), m => m["id"] = plugin);
        SolutionSamples.Edit(folder, m =>
        {
            m["version"] = version;
            m["members"]![1]!["pluginId"] = plugin;
            m["triggers"]![1]!["event"]!["type"] = $"plugin.{plugin}.posting-found";
        });
        return folder;
    }

    /// <summary>A fresh plugin id, so no other test has installed it or a member running it.</summary>
    private static string NewPlugin() => $"jb{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Installs the package on plugin <paramref name="plugin"/>, Scout with a value of its
    /// own for one secret field (built here, never a literal); answers the team and that value.</summary>
    private async Task<(string Team, string Own)> InstallAsync(string plugin)
    {
        var own = $"own-{Guid.NewGuid():N}";
        var team = Done(await Get<SolutionInstaller>().InstallAsync(
            new SolutionInstallRequest(Package(plugin, "1.0.0"), $"Move {Guid.NewGuid().ToString("N")[..6]}", Answers: new SolutionAnswers(
                Settings: new Dictionary<string, Dictionary<string, JsonElement>>
                {
                    ["Scout"] = new() { ["sources"] = JsonSerializer.SerializeToElement(new[] { "sample" }) },
                },
                SecretValues: new Dictionary<string, Dictionary<string, string>> { ["Scout"] = new() { ["adzunaAppId"] = own } })),
            Person, Ct)).Team;
        return (team, own);
    }

    private async Task<string> AgentOfAsync(string team, string member) => (await Get<TeamRegistry>().MemberAsync(team, member, Ct)).Agent;

    private async Task<List<TenantEvent>> RowsAsync(string action, string subject)
    {
        var rows = new List<TenantEvent>();
        long? before = null;

        while (true)
        {
            var page = await Get<ITenantLog>().ReadAsync(before, ITenantLog.MaxTake, Ct);
            if (page.Events.Count == 0) return rows;
            rows.AddRange(page.Events.Where(e => e.Action == action && e.Subject == subject));
            before = page.Events[^1].Seq;
        }
    }

    [Fact]
    public async Task An_update_moves_a_kept_plugin_member_onto_the_plugin_the_package_now_names_keeping_its_settings_and_own_secrets()
    {
        var old = NewPlugin();
        var moved = old + "-plugin";
        var (team, own) = await InstallAsync(old);
        var scout = new ContainerId(team, "Scout");
        Assert.Equal(MemberRef.ForPlugin(old), await AgentOfAsync(team, "Scout"));

        var settingsBefore = await Get<IPluginMemberSettingsStore>().ForAsync(scout, Ct);
        var ownBefore = await Get<PluginMemberSecretStore>().ValuesAsync(scout, Ct);
        Assert.Equal(own, ownBefore["adzunaAppId"]);

        var done = Done(await Get<SolutionInstaller>().UpdateAsync(new SolutionUpdateRequest(Package(moved, "1.0.1"), team), Person, Ct));
        Assert.Equal("1.0.1", done.Version);
        Assert.Equal(new SolutionMemberMove("Scout", old, moved), Assert.Single(done.Diff!.Moves));

        // The member runs the new plugin, with its settings and its own secret values unchanged.
        Assert.Equal(MemberRef.ForPlugin(moved), await AgentOfAsync(team, "Scout"));
        var settingsAfter = await Get<IPluginMemberSettingsStore>().ForAsync(scout, Ct);
        Assert.Equal(JsonSerializer.Serialize(settingsBefore.Config), JsonSerializer.Serialize(settingsAfter.Config));
        Assert.Equal(settingsBefore.Secrets, settingsAfter.Secrets);
        Assert.Equal(ownBefore, await Get<PluginMemberSecretStore>().ValuesAsync(scout, Ct));

        // The package's event trigger fires on the new plugin's events: its row names them and the
        // set the pump delivers to Writer holds them, not the old plugin's.
        var posting = (await Get<ITriggerStore>().ListForTeamAsync(team, Ct)).Single(t => t.Name == "New posting");
        Assert.Equal($"plugin.{moved}.posting-found", posting.EventType);
        Assert.True(posting.Enabled);
        var writer = await Get<ISubscriptions>().ForAsync(new ContainerId(team, posting.Container), Ct);
        Assert.Contains($"plugin.{moved}.posting-found", writer);
        Assert.DoesNotContain($"plugin.{old}.posting-found", writer);

        // Exactly one member.changed row, naming both plugins.
        var row = Assert.Single(await RowsAsync(TenantActions.MemberChanged, $"{team}/Scout"));
        var detail = JsonDocument.Parse(row.Detail!).RootElement;
        Assert.Equal(MemberRef.ForPlugin(old), detail.GetProperty("from").GetString());
        Assert.Equal(MemberRef.ForPlugin(moved), detail.GetProperty("to").GetString());

        // The old plugin stays installed: another team may run it.
        Assert.NotNull(Get<PluginCatalog>().For(old));
        Assert.NotNull(Get<PluginCatalog>().For(moved));
    }

    [Fact]
    public async Task The_same_update_failing_at_a_later_step_leaves_the_member_on_its_old_plugin()
    {
        var old = NewPlugin();
        var (team, own) = await InstallAsync(old);

        var outcome = await Get<SolutionInstaller>().UpdateAsync(
            new SolutionUpdateRequest(Package(old + "-plugin", "1.0.1"), team), Person, Ct,
            afterStep: step => step == SolutionInstaller.StepTriggers ? throw new InvalidOperationException($"injected at {step}") : Task.CompletedTask);

        var failed = Assert.IsType<SolutionStepFailed>(outcome.Body);
        Assert.Equal(SolutionInstaller.StepTriggers, failed.Step);
        Assert.Empty(failed.NotUndone);
        Assert.Contains(failed.Undone, u => u.Contains("Scout (moved to plugin", StringComparison.Ordinal));

        Assert.Equal(MemberRef.ForPlugin(old), await AgentOfAsync(team, "Scout"));
        Assert.Equal(own, (await Get<PluginMemberSecretStore>().ValuesAsync(new ContainerId(team, "Scout"), Ct))["adzunaAppId"]);
        Assert.Equal("1.0.0", (await Get<ITeamSolutionStore>().FindAsync(team, Ct))!.Version);

        // The move and its undo each say so.
        var rows = await RowsAsync(TenantActions.MemberChanged, $"{team}/Scout");
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => JsonDocument.Parse(r.Detail!).RootElement.TryGetProperty("undone", out _));
    }

    [Fact]
    public async Task The_preview_names_the_move_and_a_hand_made_trigger_on_the_old_plugins_events()
    {
        var old = NewPlugin();
        var moved = old + "-plugin";
        var (team, _) = await InstallAsync(old);

        // A PERSON'S OWN TRIGGER on the old plugin's events, beside the package's.
        var made = await Get<TriggerCreation>().CreateAsync(
            team,
            new NewTrigger("My postings", "Writer", "Look at {event.title}.", nameof(TriggerKind.Event), EventType: $"plugin.{old}.posting-found"),
            "person@example.test", "person@example.test",
            row => new TriggerAudit("person-id", "person@example.test", TenantActions.ScheduleCreated, row.Id, row.Name, null),
            Ct);
        Assert.Null(made.Refusal);

        using var person = await host.PersonAsync();
        using var response = await person.PostAsJsonAsync("/api/solutions/preview", new { folder = Package(moved, "1.0.1"), team }, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var diff = JsonDocument.Parse(text).RootElement.GetProperty("diff");

        var move = Assert.Single(diff.GetProperty("moves").EnumerateArray());
        Assert.Equal(("Scout", old, moved), (move.GetProperty("member").GetString(), move.GetProperty("from").GetString(), move.GetProperty("to").GetString()));
        Assert.Equal($"Scout moves from plugin {old} to {moved}", move.GetProperty("line").GetString());

        // Only the hand-made one: the package's own "New posting" follows the new plugin's events.
        var stranded = Assert.Single(diff.GetProperty("noLongerFire").EnumerateArray());
        Assert.Equal("My postings", stranded.GetProperty("trigger").GetString());
        Assert.Equal(made.Row!.Id, stranded.GetProperty("id").GetString());
        Assert.Equal($"plugin.{old}.posting-found", stranded.GetProperty("eventType").GetString());
        var line = stranded.GetProperty("line").GetString()!;
        Assert.Contains("My postings", line);
        Assert.Contains("will no longer fire", line);
        Assert.Contains($"plugin.{moved}.posting-found", line);

        // The update does not rewrite it.
        Done(await Get<SolutionInstaller>().UpdateAsync(new SolutionUpdateRequest(Package(moved, "1.0.1"), team), Person, Ct));
        Assert.Equal($"plugin.{old}.posting-found", (await Get<ITriggerStore>().FindAsync(made.Row.Id, Ct))!.EventType);
    }
}
