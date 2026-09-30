using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Solutions;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
        Assert.NotNull(await Get<ITenantLog>().FindLatestAsync(TenantActions.TeamPaused, team, Ct));
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
        var id = panel.GetProperty("triggers")[1].GetProperty("id").GetString()!;

        var patched = await person.PatchAsJsonAsync($"/api/teams/{team}/triggers/{id}", new { dailyTokenCap = 1234, enabled = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        var after = (await JsonAsync(await person.GetAsync($"/api/teams/{team}/solution/panel", Ct))).GetProperty("triggers")[1];
        Assert.Equal(1234, after.GetProperty("dailyTokenCap").GetInt64());
        Assert.False(after.GetProperty("enabled").GetBoolean());

        // The existing route's own tenant row, as the Triggers dialog's change writes it.
        Assert.NotNull(await Get<ITenantLog>().FindLatestAsync(TenantActions.ScheduleChanged, id, Ct));
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

    // ---- AGENTS.md, Solution packages: the panel's three rules ------------------------------

    [Fact]
    public void Every_control_on_the_panel_is_an_existing_route_with_its_permit_marker()
    {
        var endpoints = Get<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        foreach (var (control, method, route) in SolutionPanels.Controls)
        {
            var endpoint = endpoints.SingleOrDefault(e =>
                string.Equals(e.RoutePattern.RawText, route, StringComparison.Ordinal)
                && e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);

            Assert.True(endpoint is not null, $"{control}: no {method} {route}");
            Assert.True(
                endpoint!.Metadata.GetMetadata<HumansOnlyMarker>() is not null || endpoint.Metadata.GetMetadata<PermitRequirement>() is not null,
                $"{control}: {method} {route} carries no permit marker");
        }

        // THE PANEL ADDS NO WRITE ROUTE BUT UNINSTALL: under a team's solution there are its two
        // reads and Uninstall, and nothing else.
        var solutionRoutes = endpoints
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/teams/{team}/solution", StringComparison.Ordinal) == true)
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)} {e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            ["GET /api/teams/{team}/solution", "GET /api/teams/{team}/solution/panel", "POST /api/teams/{team}/solution/uninstall"],
            solutionRoutes);
    }

    [Fact]
    public async Task Package_text_is_answered_as_the_characters_it_is_never_as_markup()
    {
        var folder = PackageWithPlugin("jb-markup");
        SolutionSamples.Edit(folder, m =>
        {
            m["description"] = "<script>alert(1)</script> & <b>jobs</b>";
            m["panel"]!["status"] = "<img src=x onerror=alert(1)> {data.jobs.count status=<i>new</i>} & {lastRun.outcome}";
        });
        var team = await InstallAsync(folder, "Markup");
        await Get<SiteService>().PutDocumentAsync(team, "tracker", "jobs", "a", "{\"status\":\"<i>new</i>\"}",
            SiteActor.Person("person-id", "person@example.test"), Ct);
        using var person = await host.PersonAsync();

        var panel = await JsonAsync(await person.GetAsync($"/api/teams/{team}/solution/panel", Ct));

        Assert.Equal("<script>alert(1)</script> & <b>jobs</b>", panel.GetProperty("description").GetString());
        Assert.StartsWith("<img src=x onerror=alert(1)> 1 & ", panel.GetProperty("status").GetString());
        Assert.Equal(panel.GetProperty("status").GetString(),
            Tile(await JsonAsync(await person.GetAsync("/api/solutions/installed", Ct)), team).GetProperty("status").GetString());
    }

    [Fact]
    public void The_webs_solution_screens_never_render_markup_from_a_string()
    {
        var web = Path.Combine(SolutionSamples.RepoRoot(), "web", "src");
        var screens = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".vue", StringComparison.Ordinal) || f.EndsWith(".ts", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}__tests__{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f).Contains("olution", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(screens);
        var offending = screens.Where(f => File.ReadAllText(f) is var text
            && (text.Contains("v-html", StringComparison.Ordinal) || text.Contains("innerHTML", StringComparison.Ordinal)));
        Assert.Empty(offending);
    }

    [Fact]
    public async Task Nothing_on_the_panel_is_estimated_its_spend_is_the_triggers_own_measured_view()
    {
        var team = await InstallAsync(Package(), "Measured");
        using var person = await host.PersonAsync();

        var raw = await (await person.GetAsync($"/api/teams/{team}/solution/panel", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("estimat", raw, StringComparison.OrdinalIgnoreCase);

        var panel = JsonDocument.Parse(raw).RootElement.GetProperty("triggers").EnumerateArray().ToList();
        var own = (await JsonAsync(await person.GetAsync($"/api/teams/{team}/triggers", Ct))).EnumerateArray()
            .ToDictionary(t => t.GetProperty("id").GetString()!);

        Assert.Equal(5, panel.Count);
        foreach (var trigger in panel)
        {
            var theirs = own[trigger.GetProperty("id").GetString()!];
            Assert.Equal(theirs.GetProperty("spentToday").GetRawText(), trigger.GetProperty("spentToday").GetRawText());
            Assert.Equal(theirs.GetProperty("capReachedToday").GetBoolean(), trigger.GetProperty("capReachedToday").GetBoolean());
            Assert.Equal(theirs.GetProperty("dailyTokenCap").GetRawText(), trigger.GetProperty("dailyTokenCap").GetRawText());
        }
    }
}
