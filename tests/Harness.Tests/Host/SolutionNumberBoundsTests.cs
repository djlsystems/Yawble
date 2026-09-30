using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Solutions;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A SOLUTION'S ANSWERS ARE HELD TO THE PLUGIN'S BOUNDS: the Job Tracker's Scout declares
/// <c>salaryMin</c> with <c>min: 0</c>, and an install or update whose <c>inputs.settings</c> answer is
/// outside it is refused with the sentence the settings route and the hire route give - the same
/// Host check - and nothing is written. The preview carries the bounds so the wizard can say them.
/// </summary>
public sealed class SolutionNumberBoundsTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Get<T>() where T : notnull => host.Services.GetRequiredService<T>();

    /// <summary>The sample at <paramref name="version"/>, asking the person for the Scout's
    /// <c>salaryMin</c> as well as its sources.</summary>
    private string Package(string version = "1.0.0")
    {
        var folder = SolutionSamples.JobTracker(
            version,
            Path.Combine(Get<TeamDocuments>().EnsureFor(host.Alpha), "packages", Guid.NewGuid().ToString("N")));
        SolutionSamples.Edit(folder, m => m["inputs"]!["settings"]!.AsArray().Add(JsonNode.Parse("""
            { "member": "Scout", "setting": "salaryMin", "description": "Lowest annual salary to keep.", "required": false }
            """)));
        return folder;
    }

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..6]}";

    private static object Scout(object salaryMin) =>
        new Dictionary<string, object> { ["Scout"] = new Dictionary<string, object> { ["sources"] = new[] { "sample" }, ["salaryMin"] = salaryMin } };

    private async Task<int> TenantRowCountAsync() =>
        (await Get<ITenantLog>().ReadAsync(take: ITenantLog.MaxTake, ct: Ct)).Events.Count;

    private static async Task<string> RefusalAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task The_check_carries_the_bounds_of_each_setting_the_person_is_asked_for()
    {
        using var person = await host.PersonAsync();
        var check = await (await person.PostAsJsonAsync("/api/solutions/check", new { folder = Package() }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.True(check.GetProperty("ok").GetBoolean(), check.ToString());
        var salary = check.GetProperty("plan").GetProperty("personSettings").EnumerateArray().Single(s => s.GetProperty("setting").GetString() == "salaryMin");
        Assert.Equal(0, salary.GetProperty("min").GetInt32());
        Assert.Equal(JsonValueKind.Null, salary.GetProperty("max").ValueKind);
        Assert.False(salary.GetProperty("integer").GetBoolean());
    }

    [Fact]
    public async Task An_install_refuses_an_answer_outside_its_bounds_naming_the_field_and_bound_writes_nothing_and_saves_one_inside()
    {
        using var person = await host.PersonAsync();
        var folder = Package();
        var name = Unique("Bounds");
        var rows = await TenantRowCountAsync();

        var refused = await person.PostAsJsonAsync("/api/solutions/install", new { folder, teamName = name, settings = Scout(-2) }, Ct);

        Assert.Equal("'Scout': `salaryMin` must be at least 0; -2 is below it.", await RefusalAsync(refused));
        Assert.DoesNotContain(Get<TeamRegistry>().All(), t => t.Name == name);
        Assert.Equal(rows, await TenantRowCountAsync());

        // IN RANGE: installed, the Scout hired with it, and its row.
        var done = await (await person.PostAsJsonAsync("/api/solutions/install", new { folder, teamName = name, settings = Scout(50000) }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(done.GetProperty("ok").GetBoolean(), done.ToString());
        var team = done.GetProperty("team").GetString()!;

        var scout = await Get<IPluginMemberSettingsStore>().ForAsync(new ContainerId(team, "Scout"), Ct);
        Assert.Equal(50000, scout.Config["salaryMin"].GetInt32());
        Assert.NotNull(await Get<ITenantLog>().FindLatestAsync(TenantActions.MemberAdded, $"{team}/Scout", Ct));
    }

    [Fact]
    public async Task An_update_refuses_an_answer_outside_its_bounds_and_leaves_the_team_on_its_version()
    {
        using var person = await host.PersonAsync();
        var installed = await (await person.PostAsJsonAsync("/api/solutions/install", new { folder = Package(), teamName = Unique("Update"), settings = Scout(10) }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(installed.GetProperty("ok").GetBoolean(), installed.ToString());
        var team = installed.GetProperty("team").GetString()!;
        var newer = Package("1.1.0");
        var rows = await TenantRowCountAsync();

        var refused = await person.PostAsJsonAsync("/api/solutions/update", new { folder = newer, team, settings = Scout(-1) }, Ct);

        Assert.Equal("'Scout': `salaryMin` must be at least 0; -1 is below it.", await RefusalAsync(refused));
        Assert.Equal("1.0.0", (await Get<ITeamSolutionStore>().FindAsync(team, Ct))!.Version);
        Assert.Equal(rows, await TenantRowCountAsync());
        Assert.Equal(10, (await Get<IPluginMemberSettingsStore>().ForAsync(new ContainerId(team, "Scout"), Ct)).Config["salaryMin"].GetInt32());

        // IN RANGE: the update goes through.
        var updated = await (await person.PostAsJsonAsync("/api/solutions/update", new { folder = newer, team, settings = Scout(20) }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(updated.GetProperty("ok").GetBoolean(), updated.ToString());
        Assert.Equal("1.1.0", (await Get<ITeamSolutionStore>().FindAsync(team, Ct))!.Version);
    }
}
