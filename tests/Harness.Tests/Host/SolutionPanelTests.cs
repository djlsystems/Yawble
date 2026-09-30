using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Solutions;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The Solutions launcher and a solution's control panel on the real Host, with the Job Tracker
/// sample installed: the tile's filled status line and state, the panel's status, controls and
/// results, a control that is an existing route taking effect at once, and Uninstall removing what
/// the package made while keeping the team's documents.
/// </summary>
public sealed class SolutionPanelTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Get<T>() where T : notnull => host.Services.GetRequiredService<T>();

    private static readonly SolutionActor Person = new("person-id", "person@example.test");

    private string Package(Action<string>? change = null)
    {
        var folder = SolutionSamples.JobTracker(
            "1.0.0",
            Path.Combine(Get<TeamDocuments>().EnsureFor(host.Alpha), "packages", Guid.NewGuid().ToString("N")));
        change?.Invoke(folder);
        return folder;
    }

    /// <summary>The package with its plugin renamed <paramref name="id"/>, so no other test's team hires it.</summary>
    private string PackageWithPlugin(string id) => Package(folder =>
    {
        Directory.Move(Path.Combine(folder, "plugins", "job-board"), Path.Combine(folder, "plugins", id));
        SolutionSamples.EditJson(Path.Combine(folder, "plugins", id, "plugin.json"), m => m["id"] = id);
        SolutionSamples.Edit(folder, m =>
        {
            m["members"]![1]!["pluginId"] = id;
            m["triggers"]![1]!["event"]!["type"] = $"plugin.{id}.posting-found";
        });
    });

    private async Task<string> InstallAsync(string folder, string prefix)
    {
        var outcome = await Get<SolutionInstaller>().InstallAsync(
            new SolutionInstallRequest(folder, $"{prefix} {Guid.NewGuid().ToString("N")[..6]}"), Person, Ct);
        return (outcome.Body as SolutionDone ?? throw new Xunit.Sdk.XunitException(JsonSerializer.Serialize(outcome.Body))).Team;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static JsonElement Tile(JsonElement tiles, string team) =>
        tiles.EnumerateArray().Single(t => t.GetProperty("team").GetString() == team);

    private async Task UploadAsync(HttpClient person, string team, string folder, string name, string content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "file", name);
        form.Add(new StringContent(folder), "path");
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsync($"/api/teams/{team}/documents/upload", form, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_tile_shows_the_filled_status_line_the_state_and_the_site_it_opens()
    {
        var team = await InstallAsync(Package(), "Tile");
        var sites = Get<SiteService>();
        var actor = SiteActor.Person("person-id", "person@example.test");
        await sites.PutDocumentAsync(team, "tracker", "jobs", "a", "{\"status\":\"new\"}", actor, Ct);
        await sites.PutDocumentAsync(team, "tracker", "jobs", "b", "{\"status\":\"new\"}", actor, Ct);
        await sites.PutDocumentAsync(team, "tracker", "jobs", "c", "{\"status\":\"drafted\"}", actor, Ct);
        using var person = await host.PersonAsync();

        var tile = Tile(await JsonAsync(await person.GetAsync("/api/solutions/installed", Ct)), team);

        Assert.Equal(("job-tracker", "Job Tracker", "1.0.0"),
            (tile.GetProperty("id").GetString(), tile.GetProperty("name").GetString(), tile.GetProperty("version").GetString()));
        Assert.StartsWith("2 new jobs · last checked ", tile.GetProperty("status").GetString());

        var site = tile.GetProperty("primarySite");
        Assert.Equal($"/sites/{Uri.EscapeDataString(team)}/tracker/", site.GetProperty("url").GetString());
        Assert.True(site.GetProperty("published").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await person.GetAsync(site.GetProperty("url").GetString(), Ct)).StatusCode);

        // BLOCKED ON THE SKIPPED RESUME, naming it; uploading it clears the badge with nothing re-run.
        var state = tile.GetProperty("state");
        Assert.Equal(SolutionPanels.StateBlocked, state.GetProperty("kind").GetString());
        Assert.Equal("Upload a file to Resume/", state.GetProperty("reason").GetString());

        await UploadAsync(person, team, "Resume", "resume.md", "resume");
        var unblocked = Tile(await JsonAsync(await person.GetAsync("/api/solutions/installed", Ct)), team).GetProperty("state");
        Assert.Contains(unblocked.GetProperty("kind").GetString(), new[] { SolutionPanels.StateIdle, SolutionPanels.StateRunning });

        // Pausing is the existing route; the badge follows it.
        Assert.Equal(HttpStatusCode.NoContent, (await person.PostAsync($"/api/teams/{team}/pause", null, Ct)).StatusCode);
        var paused = Tile(await JsonAsync(await person.GetAsync("/api/solutions/installed", Ct)), team);
        Assert.Equal(SolutionPanels.StatePaused, paused.GetProperty("state").GetProperty("kind").GetString());
        Assert.True(paused.GetProperty("paused").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await person.PostAsync($"/api/teams/{team}/resume", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_package_without_a_panel_has_no_open_button_and_the_default_status_line()
    {
        var folder = PackageWithPlugin("jb-plain");
        SolutionSamples.Edit(folder, m => m.Remove("panel"));
        var team = await InstallAsync(folder, "Plain");
        using var person = await host.PersonAsync();

        var tile = Tile(await JsonAsync(await person.GetAsync("/api/solutions/installed", Ct)), team);

        Assert.Equal(JsonValueKind.Null, tile.GetProperty("primarySite").ValueKind);
        Assert.Matches(@"^(No runs yet|Last run \S+ \(\w+\)) · blocked$", tile.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_panel_reads_status_controls_and_results_and_estimates_nothing()
    {
        var team = await InstallAsync(Package(), "Panel");
        var drafts = Path.Combine(Get<TeamDocuments>().EnsureFor(team), "Drafts");
        Directory.CreateDirectory(drafts);
        File.WriteAllText(Path.Combine(drafts, "older.md"), "older");
        File.SetLastWriteTimeUtc(Path.Combine(drafts, "older.md"), DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(Path.Combine(drafts, "newer.md"), "newer");
        File.WriteAllText(Path.Combine(drafts, ".hidden"), "not a result");
        using var person = await host.PersonAsync();

        var panel = await JsonAsync(await person.GetAsync($"/api/teams/{team}/solution/panel", Ct));

        Assert.Equal(("job-tracker", "1.0.0"), (panel.GetProperty("id").GetString(), panel.GetProperty("version").GetString()));
        Assert.StartsWith("Finds job postings", panel.GetProperty("description").GetString());
        Assert.True(File.Exists(Path.Combine(panel.GetProperty("folder").GetString()!, "solution.json")));

        // STATUS: each member in the package's order, its state and last run.
        var members = panel.GetProperty("members").EnumerateArray().ToList();
        Assert.Equal(["Coordinator", "Scout", "Writer"], members.Select(m => m.GetProperty("packageName").GetString()));
        Assert.Equal(["Manager", "Scout", "Writer"], members.Select(m => m.GetProperty("member").GetString()));
        Assert.Equal("plugin", members[1].GetProperty("kind").GetString());
        Assert.All(members, m => Assert.Contains(m.GetProperty("state").GetString(), new[] { "idle", "running" }));

        // Each trigger is the Triggers dialog's own view: its next fire, and MEASURED spend with
        // unmeasured runs counted as such.
        var triggers = panel.GetProperty("triggers").EnumerateArray().ToList();
        Assert.Equal(["Scan for postings", "New posting", "Apply pressed", "Resume changed", "Morning summary"],
            triggers.Select(t => t.GetProperty("packageName").GetString()));
        Assert.Equal([true, false, false, false, true], triggers.Select(t => t.GetProperty("runNow").GetBoolean()));
        Assert.All(triggers, t =>
        {
            var spent = t.GetProperty("spentToday");
            Assert.Equal(JsonValueKind.Number, spent.GetProperty("billableTokens").ValueKind);
            Assert.Equal(JsonValueKind.Number, spent.GetProperty("measuredRuns").ValueKind);
            Assert.Equal(JsonValueKind.Number, spent.GetProperty("unmeasuredRuns").ValueKind);
            Assert.False(t.TryGetProperty("estimatedTokens", out _));
        });
        Assert.NotEqual(JsonValueKind.Null, triggers[4].GetProperty("nextDueAt").ValueKind);

        // Blocked, with the fix inline.
        var blocked = Assert.Single(panel.GetProperty("blocked").EnumerateArray());
        Assert.Equal("Resume", blocked.GetProperty("fix").GetProperty("upload").GetProperty("folder").GetString());

        // CONTROLS: the package's settings first, a person-only one marked.
        var settings = panel.GetProperty("settings").EnumerateArray().ToList();
        Assert.Equal([("Scout", "keywords", false), ("Scout", "sources", true)],
            settings.Select(s => (s.GetProperty("member").GetString(), s.GetProperty("setting").GetString(), s.GetProperty("personOnly").GetBoolean())));

        // RESULTS: newest first, hidden files left out, each with a download that works.
        var output = Assert.Single(panel.GetProperty("outputs").EnumerateArray());
        Assert.Equal("Drafts", output.GetProperty("folder").GetString());
        var files = output.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(["newer.md", "older.md"], files.Select(f => f.GetProperty("name").GetString()));
        var download = await person.GetAsync(files[0].GetProperty("download").GetString(), Ct);
        Assert.Equal("newer", await download.Content.ReadAsStringAsync(Ct));

        Assert.Equal(JsonValueKind.Array, panel.GetProperty("recentRuns").ValueKind);
    }

    [Fact]
    public async Task A_trigger_cap_changed_through_its_existing_route_shows_at_once()
    {
        var team = await InstallAsync(Package(), "Cap");
        using var person = await host.PersonAsync();
        var panel = await JsonAsync(await person.GetAsync($"/api/teams/{team}/solution/panel", Ct));
        var id = panel.GetProperty("triggers")[1].GetProperty("id").GetString();

        var patched = await person.PatchAsJsonAsync($"/api/teams/{team}/triggers/{id}", new { dailyTokenCap = 1234, enabled = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        var after = (await JsonAsync(await person.GetAsync($"/api/teams/{team}/solution/panel", Ct))).GetProperty("triggers")[1];
        Assert.Equal(1234, after.GetProperty("dailyTokenCap").GetInt64());
        Assert.False(after.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Uninstall_removes_what_the_package_made_and_keeps_the_team_and_its_documents()
    {
        var team = await InstallAsync(PackageWithPlugin("jb-uninstall"), "Uninstall");
        var documents = Get<TeamDocuments>().EnsureFor(team);
        Directory.CreateDirectory(Path.Combine(documents, "Drafts"));
        File.WriteAllText(Path.Combine(documents, "Drafts", "acme.md"), "a letter");
        var tools = SolutionInstaller.ToolsFolderOf(Get<TeamPaths>(), team);
        Assert.True(Directory.Exists(tools));
        using var person = await host.PersonAsync();

        var body = await JsonAsync(await person.PostAsJsonAsync($"/api/teams/{team}/solution/uninstall", new { removePlugins = true }, Ct));

        Assert.True(body.GetProperty("ok").GetBoolean(), body.ToString());
        Assert.Equal(5, body.GetProperty("removed").GetProperty("triggers").GetArrayLength());
        Assert.Equal(["Scout", "Writer"], body.GetProperty("removed").GetProperty("members").EnumerateArray().Select(m => m.GetString()).Order());
        Assert.Equal(["jb-uninstall"], body.GetProperty("plugins").GetProperty("removed").EnumerateArray().Select(p => p.GetString()));

        // Gone: triggers, members (the Manager stays, without the package's instructions), skills,
        // sites, tools, the plugin no other team used, and the record.
        Assert.Empty(await Get<ITriggerStore>().ListForTeamAsync(team, Ct));
        Assert.Equal(["Manager"], Get<TeamRegistry>().ContainerIdsOf(team).Select(c => c.Name));
        Assert.Empty(await Get<ISkillStore>().ListTeamAsync(team, Ct));
        Assert.Null(await Get<ISiteStore>().FindAsync(team, "tracker", Ct));
        Assert.False(Directory.Exists(tools));
        Assert.Null(Get<PluginCatalog>().For("jb-uninstall"));
        Assert.Null(await Get<ITeamSolutionStore>().FindAsync(team, Ct));
        Assert.DoesNotContain("coordinate the job search", (await Get<TeamRegistry>().MemberAsync(team, "Manager", Ct)).SystemPrompt ?? "");

        // KEPT: the team and its documents, named.
        Assert.NotNull(Get<TeamRegistry>().ExistingName(team));
        Assert.True(File.Exists(Path.Combine(documents, "Drafts", "acme.md")));
        Assert.Equal(documents, body.GetProperty("documentsKept").GetString());

        // Its row, and the solution is no longer one.
        var row = await Get<ITenantLog>().FindLatestAsync(TenantActions.SolutionUninstalled, team, Ct);
        Assert.NotNull(row);
        Assert.Contains("jb-uninstall", row!.Detail);
        Assert.Equal(HttpStatusCode.NotFound, (await person.GetAsync($"/api/teams/{team}/solution/panel", Ct)).StatusCode);
        Assert.DoesNotContain((await JsonAsync(await person.GetAsync("/api/solutions/installed", Ct))).EnumerateArray(),
            t => t.GetProperty("team").GetString() == team);
        Assert.Equal(HttpStatusCode.NotFound, (await person.PostAsJsonAsync($"/api/teams/{team}/solution/uninstall", new { }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Uninstall_keeps_a_plugin_another_team_still_uses_and_every_plugin_unless_asked()
    {
        var first = await InstallAsync(PackageWithPlugin("jb-shared"), "Shared one");
        var second = await InstallAsync(PackageWithPlugin("jb-shared"), "Shared two");
        using var person = await host.PersonAsync();

        var shared = await JsonAsync(await person.PostAsJsonAsync($"/api/teams/{first}/solution/uninstall", new { removePlugins = true }, Ct));
        Assert.Empty(shared.GetProperty("plugins").GetProperty("removed").EnumerateArray());
        var kept = Assert.Single(shared.GetProperty("plugins").GetProperty("kept").EnumerateArray());
        Assert.Equal("jb-shared", kept.GetProperty("id").GetString());
        Assert.Equal([second], kept.GetProperty("usedBy").EnumerateArray().Select(t => t.GetString()));
        Assert.NotNull(Get<PluginCatalog>().For("jb-shared"));

        var unasked = await JsonAsync(await person.PostAsJsonAsync($"/api/teams/{second}/solution/uninstall", new { }, Ct));
        Assert.Empty(unasked.GetProperty("plugins").GetProperty("removed").EnumerateArray());
        Assert.NotNull(Get<PluginCatalog>().For("jb-shared"));
    }

    [Fact]
    public async Task The_panel_and_uninstall_are_a_persons_only()
    {
        var team = await InstallAsync(PackageWithPlugin("jb-agents-panel"), "Agents");
        using var agent = host.Container(host.AlphaContainerKey);

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync($"/api/teams/{team}/solution/panel", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync($"/api/teams/{team}/solution/uninstall", new { }, Ct)).StatusCode);
        Assert.NotNull(await Get<ITeamSolutionStore>().FindAsync(team, Ct));
    }
}
