using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Solutions;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// Installing and updating a solution package on the real Host: every step's result, the undo of
/// each step when it fails, the update from Job Tracker 1.0.0 to 1.1.0, what a skipped required
/// input does, who may install, the deep link's folder rule and the operator CLI's request file.
/// </summary>
public sealed class SolutionInstallTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Get<T>() where T : notnull => host.Services.GetRequiredService<T>();

    /// <summary>A copy of the sample in Alpha's documents, where a person's package would be.</summary>
    private string Package(string version = "1.0.0") =>
        SolutionSamples.JobTracker(
            version,
            Path.Combine(Get<TeamDocuments>().EnsureFor(host.Alpha), "packages", Guid.NewGuid().ToString("N")));

    /// <summary>The package with its plugin renamed <paramref name="id"/>, so a test starts with the
    /// plugin not installed whatever the other tests installed.</summary>
    private string PackageWithPlugin(string id)
    {
        var folder = Package();
        Directory.Move(Path.Combine(folder, "plugins", "job-board"), Path.Combine(folder, "plugins", id));
        SolutionSamples.EditJson(Path.Combine(folder, "plugins", id, "plugin.json"), m => m["id"] = id);
        SolutionSamples.Edit(folder, m =>
        {
            m["members"]![1]!["pluginId"] = id;
            m["triggers"]![1]!["event"]!["type"] = $"plugin.{id}.posting-found";
        });
        return folder;
    }

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..6]}";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private SolutionActor Person => new("person-id", "person@example.test");

    private static SolutionDone Done(SolutionOutcome outcome) =>
        outcome.Body as SolutionDone ?? throw new Xunit.Sdk.XunitException(JsonSerializer.Serialize(outcome.Body));

    [Fact]
    public async Task A_person_installs_the_package_and_every_step_makes_what_it_says()
    {
        using var person = await host.PersonAsync();
        var name = Unique("Job Tracker");

        var response = await person.PostAsJsonAsync("/api/solutions/install", new
        {
            folder = Package(),
            teamName = name,
            settings = new Dictionary<string, object> { ["Scout"] = new Dictionary<string, object> { ["sources"] = new[] { "sample" } } },
        }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.True(body.GetProperty("ok").GetBoolean(), body.ToString());
        var team = body.GetProperty("team").GetString()!;
        Assert.Equal(name, body.GetProperty("teamName").GetString());
        Assert.Equal(
            ["plugins", "team", "members", "skills", "tools", "sites", "triggers", "record"],
            body.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("step").GetString()));

        var teams = Get<TeamRegistry>();
        var paths = Get<TeamPaths>();
        var tools = Path.Combine(paths.RootFor(team), "solution");

        // 1. The plugin, active (0.1.0, or a newer one another test installed: never downgraded).
        Assert.True(SolutionInstaller.CompareVersions(Get<PluginCatalog>().For("job-board")!.Manifest.Version, "0.1.0") >= 0);

        // 2 and 3. The team, its Manager named and instructed as the package says, and its members.
        var summary = teams.All().Single(t => t.Id == team);
        Assert.Equal(["Manager", "Scout", "Writer"], summary.Containers.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.Equal("Coordinator", summary.Containers.Single(c => c.Id == "Manager").Name);
        Assert.Equal("plugin:job-board", summary.Containers.Single(c => c.Id == "Scout").Agent);
        Assert.NotEmpty(teams.ReposFor(team));

        var writer = Get<ContainerHost>().Find(new ContainerId(team, "Writer"))!;
        Assert.Contains($"{tools}/make-cover-letter.py", writer.SystemPrompt);
        Assert.Contains($"Its tools are in `{tools}`", writer.SystemPrompt);
        Assert.Contains("This team runs a job search for one person.", writer.SystemPrompt);

        var scout = await Get<IPluginMemberSettingsStore>().ForAsync(new ContainerId(team, "Scout"), Ct);
        Assert.Equal("[\"sample\"]", scout.Config["sources"].GetRawText());
        Assert.Equal("[\"engineer\",\"developer\"]", scout.Config["keywords"].GetRawText());

        // 4. The team skill, this team's only.
        var skill = Assert.Single(await Get<ISkillStore>().ListTeamAsync(team, Ct));
        Assert.Equal("job-search-playbook", skill.Name);

        // 5. The tools, read-only to agents.
        Assert.True(File.Exists(Path.Combine(tools, "make-cover-letter.py")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(tools) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.OtherRead));
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(Path.Combine(tools, "make-cover-letter.py")) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }

        // 6. The site, live.
        Assert.NotNull((await Get<ISiteStore>().FindAsync(team, "tracker", Ct))!.LiveVersion);

        // 7. The triggers, with their caps, wake settings and full instructions.
        var triggers = await Get<ITriggerStore>().ListForTeamAsync(team, Ct);
        Assert.Equal(5, triggers.Count);
        var apply = triggers.Single(t => t.Name == "Apply pressed");
        Assert.Equal("Writer", apply.Container);
        Assert.Equal("site.action", apply.EventType);
        Assert.Equal("siteAction eq tracker/apply", apply.Filter);
        Assert.Equal(400000, apply.DailyTokenCap);
        Assert.Equal(WakeManagerPolicy.OnHandbackOrFailure, apply.WakeManager);
        Assert.StartsWith("{event.by} pressed Apply on {event.payload}.", apply.Instruction);
        Assert.Contains($"python3 {tools}/make-cover-letter.py", apply.Instruction);
        var morning = triggers.Single(t => t.Name == "Morning summary");
        Assert.Equal("Manager", morning.Container);
        Assert.Equal("Europe/London", morning.Timezone);
        Assert.Equal(WakeManagerPolicy.Never, morning.WakeManager);
        Assert.Equal("FolderChange", triggers.Single(t => t.Name == "Resume changed").Kind, StringComparer.OrdinalIgnoreCase);

        // 8. The record, and its row.
        var record = (await Get<ITeamSolutionStore>().FindAsync(team, Ct))!;
        Assert.Equal(("job-tracker", "1.0.0"), (record.PackageId, record.Version));
        Assert.Equal("Scout", record.Members["Scout"]);
        Assert.Equal(apply.Id, record.Triggers["Apply pressed"]);
        Assert.NotNull(await Get<ITenantLog>().FindLatestAsync(TenantActions.SolutionInstalled, team, Ct));

        // The team delete dialog's read (ITeamSolutions): which package it came from.
        var from = Get<ITeamSolutions>().For(team)!;
        Assert.Equal(("job-tracker", "Job Tracker", "1.0.0"), (from.Id, from.Name, from.Version));
        Assert.Equal(["job-board"], from.Plugins);

        // The Host's registration is the table, not the placeholder: the team's summary carries it,
        // and a team made by hand carries none.
        var carried = Get<TeamRegistry>().All().Single(t => t.Id == team).Solution!;
        Assert.Equal(("job-tracker", "1.0.0"), (carried.Id, carried.Version));
        Assert.Equal(["job-board"], carried.Plugins);
        Assert.All(Get<TeamRegistry>().All().Where(t => t.Id != team && Get<ITeamSolutions>().For(t.Id) is null),
            t => Assert.Null(t.Solution));

        // The required Resume/ was not given: the team waits for it, by name.
        var missing = Assert.Single(body.GetProperty("missing").EnumerateArray());
        Assert.Equal("Resume/", missing.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Its_team_skill_is_offered_to_its_manager_and_refused_to_another_teams_manager()
    {
        var team = Done(await Get<SolutionInstaller>().InstallAsync(
            new SolutionInstallRequest(Package(), Unique("Skills")), Person, Ct)).Team;

        HttpClient Manager(string ofTeam)
        {
            var key = Get<IPrincipalStore>().MintAsync(
                new ContainerId(ofTeam, "Manager").ToString(), PrincipalKind.Container, ofTeam, Permits.All).GetAwaiter().GetResult();
            return host.Container(key);
        }

        using var own = Manager(team);
        using var other = Manager(host.Beta);

        Assert.Equal(HttpStatusCode.OK, (await own.GetAsync("/api/me/skills/job-search-playbook", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/me/skills/job-search-playbook", Ct)).StatusCode);
    }

    public static TheoryData<string> EveryStep => [.. SolutionInstaller.Steps.Select(s => s.Step)];

    [Theory]
    [MemberData(nameof(EveryStep))]
    public async Task A_failure_at_any_step_leaves_nothing_behind(string failing)
    {
        var plugin = $"jb-{failing}";
        var folder = PackageWithPlugin(plugin);
        var name = Unique("Rollback");
        var derived = ContainerId.DeriveName(name)!;
        var pluginsRoot = Get<PluginCatalog>().Root;

        var outcome = await Get<SolutionInstaller>().InstallAsync(
            new SolutionInstallRequest(folder, name), Person, Ct,
            afterStep: step => step == failing ? throw new InvalidOperationException($"injected at {step}") : Task.CompletedTask);

        var failed = Assert.IsType<SolutionStepFailed>(outcome.Body);
        Assert.False(failed.Ok);
        Assert.Equal(failing, failed.Step);
        Assert.Equal($"injected at {failing}", failed.Reason);
        Assert.Empty(failed.NotUndone);

        // NOTHING IS LEFT: no plugin, team, member, skill, tools, site, trigger, record, folder or repository.
        Assert.Null(Get<PluginCatalog>().For(plugin));
        Assert.False(Directory.Exists(Path.Combine(pluginsRoot, plugin)));
        Assert.Null(Get<TeamRegistry>().ExistingName(derived));
        Assert.DoesNotContain(Get<TeamRegistry>().All(), t => t.Name == name);
        Assert.Empty(await Get<ISkillStore>().ListTeamAsync(derived, Ct));
        Assert.Empty(await Get<ISiteStore>().ListAsync(derived, Ct));
        Assert.False(Directory.Exists(Path.Combine(Get<SiteService>().Root, derived)));
        Assert.Empty(await Get<ITriggerStore>().ListForTeamAsync(derived, Ct));
        Assert.Null(await Get<ITeamSolutionStore>().FindAsync(derived, Ct));
        Assert.Null(Get<ITeamSolutions>().For(derived));
        Assert.False(Directory.Exists(Path.Combine(host.DataRoot, "teams", derived)));
        Assert.False(Directory.Exists(Path.Combine(host.DataRoot, "documents", derived)));
        Assert.False(Get<LocalRepos>().Exists(derived));

        // And the failure is on the tenant log, naming the step.
        var logged = (await Get<ITenantLog>().FindLatestAsync(TenantActions.SolutionFailed, name, Ct))!;
        Assert.Contains($"\"step\":\"{failing}\"", logged.Detail);
    }

    [Fact]
    public async Task A_step_the_platform_refuses_is_named_with_its_own_sentence_and_everything_before_it_undone()
    {
        // What only the store can know: an instance-wide skill already holds the team skill's name.
        var folder = PackageWithPlugin("jb-refused");
        var skill = $"taken-{Guid.NewGuid().ToString("N")[..8]}";
        File.Move(Path.Combine(folder, "skills", "job-search-playbook.md"), Path.Combine(folder, "skills", skill + ".md"));
        var file = Path.Combine(folder, "skills", skill + ".md");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, Ct)).Replace("name: job-search-playbook", $"name: {skill}"), Ct);
        SolutionSamples.Edit(folder, m => m["skills"] = new JsonArray(skill));
        await Get<ISkillStore>().CreateCustomAsync(new SkillDraft(skill, "Someone else's.", ["manager"], "Body."), "person@example.test", Ct);
        var name = Unique("Refused");

        var outcome = await Get<SolutionInstaller>().InstallAsync(new SolutionInstallRequest(folder, name), Person, Ct);

        var failed = Assert.IsType<SolutionStepFailed>(outcome.Body);
        Assert.Equal(("skills", 4, "Register the team skills"), (failed.Step, failed.StepNumber, failed.Title));
        Assert.Contains(skill, failed.Reason);
        Assert.Equal(["plugins", "team", "members"], failed.Steps.Select(s => s.Step));
        Assert.Contains("team " + ContainerId.DeriveName(name), failed.Undone);
        Assert.Null(Get<TeamRegistry>().ExistingName(ContainerId.DeriveName(name)!));
        Assert.Null(Get<PluginCatalog>().For("jb-refused"));
    }

    [Fact]
    public async Task A_team_name_that_is_taken_is_refused_before_anything_is_made()
    {
        var folder = PackageWithPlugin("jb-taken");

        var outcome = await Get<SolutionInstaller>().InstallAsync(new SolutionInstallRequest(folder, "Alpha"), Person, Ct);

        Assert.Equal(409, outcome.Status);
        Assert.Contains("Alpha", JsonSerializer.Serialize(outcome.Body));
        Assert.Null(Get<PluginCatalog>().For("jb-taken"));
    }

    [Fact]
    public async Task Updating_from_1_0_0_to_1_1_0_shows_the_diff_keeps_the_persons_part_and_applies_it()
    {
        var installer = Get<SolutionInstaller>();
        var team = Done(await installer.InstallAsync(
            new SolutionInstallRequest(Package(), Unique("Update"), Answers: new SolutionAnswers(
                Settings: new Dictionary<string, Dictionary<string, JsonElement>>
                {
                    ["Scout"] = new() { ["sources"] = JsonSerializer.SerializeToElement(new[] { "sample" }) },
                })),
            Person, Ct)).Team;

        // The person's part: a document, site data.
        await using (var resume = new MemoryStream("my resume"u8.ToArray()))
        {
            await Get<TeamDocuments>().SaveAsync(team, "Resume", "resume.md", resume, Ct);
        }

        var siteData = await Get<SiteService>().PutDocumentAsync(team, "tracker", "jobs", "p1", """{"title":"Engineer"}""", SiteActor.Person("person-id", "person@example.test"), Ct);
        Assert.NotNull(siteData.Value);
        var newer = Package("1.1.0");

        // THE DIFF, before anything is applied.
        using var person = await host.PersonAsync();
        var preview = await JsonAsync(await person.PostAsJsonAsync("/api/solutions/preview", new { folder = newer, team }, Ct));
        Assert.True(preview.GetProperty("ok").GetBoolean(), preview.ToString());
        Assert.Equal("update", preview.GetProperty("mode").GetString());
        Assert.Equal(("1.0.0", "1.1.0"), (preview.GetProperty("from").GetString(), preview.GetProperty("to").GetString()));
        var diff = preview.GetProperty("diff");
        string[] Names(string section, string kind) => [.. diff.GetProperty(section).GetProperty(kind).EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal(["Reviewer"], Names("members", "added"));
        Assert.Empty(Names("members", "removed"));
        Assert.Equal(["Weekly review"], Names("triggers", "added"));
        Assert.Equal(["New posting"], Names("triggers", "changed"));
        Assert.Equal(["Resume changed"], Names("triggers", "removed"));
        Assert.Equal(["interview-prep"], Names("skills", "added"));
        Assert.Empty(Names("sites", "changed"));
        Assert.Equal(["job-board 0.1.0 → 0.2.0"], Names("plugins", "changed"));
        Assert.Equal("1.0.0", (await Get<ITeamSolutionStore>().FindAsync(team, Ct))!.Version);

        // WHAT IS KEPT of the person's part, so Your part shows it rather than asking again.
        var kept = preview.GetProperty("kept");
        var sources = Assert.Single(kept.GetProperty("settings").EnumerateArray());
        Assert.Equal(("Scout", "sources"), (sources.GetProperty("member").GetString(), sources.GetProperty("setting").GetString()));
        Assert.Equal("[\"sample\"]", sources.GetProperty("value").GetRawText());
        var resumeKept = Assert.Single(kept.GetProperty("documents").EnumerateArray(), d => d.GetProperty("folder").GetString() == "Resume");
        Assert.Equal(["resume.md"], resumeKept.GetProperty("files").EnumerateArray().Select(f => f.GetString()!));

        // APPLIED.
        var response = await person.PostAsJsonAsync("/api/solutions/update", new { folder = newer, team }, Ct);
        var body = await JsonAsync(response);
        Assert.True(body.GetProperty("ok").GetBoolean(), body.ToString());
        Assert.Equal("1.0.0", body.GetProperty("from").GetString());

        var summary = Get<TeamRegistry>().All().Single(t => t.Id == team);
        Assert.Contains(summary.Containers, c => c.Id == "Reviewer");
        var triggers = await Get<ITriggerStore>().ListForTeamAsync(team, Ct);
        Assert.DoesNotContain(triggers, t => t.Name == "Resume changed");
        Assert.Equal(250000, triggers.Single(t => t.Name == "New posting").DailyTokenCap);
        Assert.Equal("Reviewer", triggers.Single(t => t.Name == "Weekly review").Container);
        Assert.Equal(["interview-prep", "job-search-playbook"], (await Get<ISkillStore>().ListTeamAsync(team, Ct)).Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.Equal("0.2.0", Get<PluginCatalog>().For("job-board")!.Manifest.Version);

        // KEPT: the person's setting, the document and the site data.
        var scout = await Get<IPluginMemberSettingsStore>().ForAsync(new ContainerId(team, "Scout"), Ct);
        Assert.Equal("[\"sample\"]", scout.Config["sources"].GetRawText());
        Assert.True(File.Exists(Path.Combine(Get<TeamDocuments>().RootFor(team), "Resume", "resume.md")));
        Assert.NotNull((await Get<SiteService>().GetDocumentAsync(team, "tracker", "jobs", "p1", null, Ct)).Value);

        var record = (await Get<ITeamSolutionStore>().FindAsync(team, Ct))!;
        Assert.Equal("1.1.0", record.Version);
        Assert.False(record.Triggers.ContainsKey("Resume changed"));
        Assert.Equal("1.1.0", Get<ITeamSolutions>().For(team)!.Version);
        Assert.NotNull(await Get<ITenantLog>().FindLatestAsync(TenantActions.SolutionUpdated, team, Ct));

        // The same version again is not an update.
        var again = await installer.PreviewAsync(newer, team, Ct);
        Assert.Contains("already runs Job Tracker 1.1.0", JsonSerializer.Serialize(again.Body));
    }

    [Fact]
    public async Task A_skipped_required_document_blocks_the_team_naming_it_until_it_is_provided()
    {
        var team = Done(await Get<SolutionInstaller>().InstallAsync(
            new SolutionInstallRequest(Package(), Unique("Blocked")), Person, Ct)).Team;
        using var person = await host.PersonAsync();

        var blocked = await JsonAsync(await person.GetAsync($"/api/teams/{team}/solution", Ct));
        var missing = Assert.Single(blocked.GetProperty("missing").EnumerateArray());
        Assert.Equal("document", missing.GetProperty("kind").GetString());
        Assert.Equal("Resume/", missing.GetProperty("name").GetString());
        Assert.Contains("reference resume", missing.GetProperty("description").GetString());

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("resume"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "file", "resume.md");
        form.Add(new StringContent("Resume"), "path");
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsync($"/api/teams/{team}/documents/upload", form, Ct)).StatusCode);

        var provided = await JsonAsync(await person.GetAsync($"/api/teams/{team}/solution", Ct));
        Assert.Empty(provided.GetProperty("missing").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await person.GetAsync($"/api/teams/{host.Alpha}/solution", Ct)).StatusCode);
    }

    [Fact]
    public async Task Install_update_preview_and_the_list_are_a_persons_only_and_an_agent_never_installs()
    {
        var person = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct);
        var conciergeKey = await Get<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(person!.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: person.Id, ct: Ct);
        var folder = PackageWithPlugin("jb-agents");

        foreach (var key in new[] { conciergeKey, host.AlphaContainerKey })
        {
            using var agent = host.Container(key);

            Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/solutions/install", new { folder, teamName = Unique("Agent") }, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/solutions/update", new { folder, team = host.Alpha }, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/solutions/preview", new { folder }, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/solutions/installed", Ct)).StatusCode);
        }

        Assert.Null(Get<PluginCatalog>().For("jb-agents"));
    }

    [Fact]
    public async Task A_link_opens_only_a_folder_in_the_documents_or_a_teams_folder()
    {
        using var person = await host.PersonAsync();

        var inside = await person.PostAsJsonAsync("/api/solutions/check", new { folder = Package(), from = "link" }, Ct);
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);

        var inTeamFolder = SolutionSamples.JobTracker("1.0.0", Path.Combine(Get<TeamPaths>().RootFor(host.Alpha), "packages", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsJsonAsync("/api/solutions/check", new { folder = inTeamFolder, from = "link" }, Ct)).StatusCode);

        var elsewhere = SolutionSamples.JobTracker("1.0.0", Path.Combine(host.DataRoot, "elsewhere", Guid.NewGuid().ToString("N")));
        var refused = await person.PostAsJsonAsync("/api/solutions/check", new { folder = elsewhere, from = "link" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("A link may open only a package inside this instance's documents or a team's folder", (await JsonAsync(refused)).GetProperty("error").GetString());

        // Chosen by hand in the Plugins dialog, the same folder is checked.
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsJsonAsync("/api/solutions/check", new { folder = elsewhere }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Preview_names_a_taken_name_and_the_installed_list_offers_the_team_for_update()
    {
        var installer = Get<SolutionInstaller>();
        var name = Unique("Listed");
        var team = Done(await installer.InstallAsync(new SolutionInstallRequest(Package(), name), Person, Ct)).Team;
        using var person = await host.PersonAsync();

        var taken = await JsonAsync(await person.PostAsJsonAsync("/api/solutions/preview", new { folder = Package(), team = "Beta" }, Ct));
        Assert.Contains("was not installed from a solution package", taken.GetProperty("error").GetString());

        var fresh = await JsonAsync(await person.PostAsJsonAsync("/api/solutions/preview", new { folder = Package() }, Ct));
        Assert.Equal("install", fresh.GetProperty("mode").GetString());
        Assert.Equal("Job Tracker", fresh.GetProperty("teamName").GetString());

        var listed = await JsonAsync(await person.GetAsync("/api/solutions/installed", Ct));
        var row = listed.EnumerateArray().Single(r => r.GetProperty("team").GetString() == team);
        Assert.Equal(("job-tracker", "1.0.0", name), (row.GetProperty("id").GetString(), row.GetProperty("version").GetString(), row.GetProperty("teamName").GetString()));
    }

    [Fact]
    public async Task Deleting_the_team_forgets_its_package_and_keeps_its_plugins()
    {
        var team = Done(await Get<SolutionInstaller>().InstallAsync(new SolutionInstallRequest(Package(), Unique("Deleted")), Person, Ct)).Team;

        await Get<TeamDeletion>().DeleteAsync(team, ct: Ct);

        Assert.Null(await Get<ITeamSolutionStore>().FindAsync(team, Ct));
        Assert.Null(Get<ITeamSolutions>().For(team));
        Assert.NotNull(Get<PluginCatalog>().For("job-board"));
    }

    [Fact]
    public async Task The_operator_clis_request_file_is_answered_with_the_routes_body_and_its_documents_are_copied_in()
    {
        var plugins = Get<PluginCatalog>().Root;
        var stage = Path.Combine(plugins, SolutionRequests.StagingFolder, Guid.NewGuid().ToString("N"));
        var folder = SolutionSamples.JobTracker("1.0.0", stage);
        var resume = Path.Combine(stage, ".documents", "Resume", "cv.md");
        Directory.CreateDirectory(Path.GetDirectoryName(resume)!);
        await File.WriteAllTextAsync(resume, "cv", Ct);
        var name = Unique("Operator");

        // THE RUNNING HOST ANSWERS, as it does the CLI: its SolutionRequests service polls the file.
        async Task<JsonElement> AskAsync(object request)
        {
            var nonce = JsonSerializer.SerializeToElement(request).GetProperty("request").GetString();
            await File.WriteAllTextAsync(Path.Combine(plugins, SolutionRequests.RequestFile), JsonSerializer.Serialize(request), Ct);
            var report = Path.Combine(plugins, SolutionRequests.ReportFile);

            for (var waited = 0; waited < 600; waited++)
            {
                if (File.Exists(report)
                    && JsonDocument.Parse(await File.ReadAllTextAsync(report, Ct)).RootElement is var answer
                    && answer.GetProperty("request").GetString() == nonce)
                {
                    return answer;
                }

                await Task.Delay(100, Ct);
            }

            throw new TimeoutException("The Host did not answer the request file.");
        }

        var preview = await AskAsync(new { request = $"p-{name}", action = "preview", folder, team = name });
        Assert.Equal(200, preview.GetProperty("status").GetInt32());
        Assert.Equal("install", preview.GetProperty("body").GetProperty("mode").GetString());

        var installed = await AskAsync(new
        {
            request = $"i-{name}", action = "install", folder, teamName = name,
            documents = new Dictionary<string, string[]> { ["Resume"] = [resume] },
        });
        var body = installed.GetProperty("body");
        Assert.True(body.GetProperty("ok").GetBoolean(), body.ToString());
        Assert.Empty(body.GetProperty("missing").EnumerateArray());
        var team = body.GetProperty("team").GetString()!;
        Assert.Equal("cv", await File.ReadAllTextAsync(Path.Combine(Get<TeamDocuments>().RootFor(team), "Resume", "cv.md"), Ct));

        // A staged package is never listed as a refused plugin.
        Assert.DoesNotContain(Get<PluginCatalog>().Refused, r => r.Id.StartsWith('.'));
    }
}
