using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Solutions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The real Host in memory, with a Team picture template of its own (<c>TeamPicture:Template</c>), so
/// a test can ship a newer one over a customized page.
/// </summary>
public sealed class TeamPictureHost : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-picture-test-{Guid.NewGuid():N}");

    private WebApplicationFactory<Program> _factory = null!;

    public string Template { get; } = Path.Combine(Path.GetTempPath(), $"harness-picture-template-{Guid.NewGuid():N}");

    public IServiceProvider Services => _factory.Services;

    public string Agent { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(Path.Combine(Template, "css"));
        File.WriteAllText(Path.Combine(Template, "index.html"), "<!doctype html><title>Team picture</title><script src=\"app.js\"></script>");
        File.WriteAllText(Path.Combine(Template, "app.js"), "// template one");
        File.WriteAllText(Path.Combine(Template, "css", "page.css"), "body{}");

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host
                .UseSetting("DataRoot", _dataRoot)
                .UseSetting("TeamPicture:Template", Template)
                .UseSetting("Logging:LogLevel:Default", "Warning"));

        Agent = Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        await Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", HostFixture.Password);
    }

    public HttpClient Anonymous(bool allowAutoRedirect = true) =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = allowAutoRedirect });

    public HttpClient Container(string key)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        return client;
    }

    public async Task<HttpClient> PersonAsync(bool allowAutoRedirect = true)
    {
        var client = Anonymous(allowAutoRedirect);
        (await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = HostFixture.Password })).EnsureSuccessStatusCode();
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        foreach (var folder in new[] { _dataRoot, Template })
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// THE TEAM PICTURE: the flag, the reserved site, who writes which collection, the shipped template's
/// publish, and the live collections the platform keeps from the team's own figures with no agent run.
/// </summary>
public sealed class TeamPictureTests(TeamPictureHost host) : IClassFixture<TeamPictureHost>
{
    private const string Site = TeamPicture.SiteName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Get<T>() where T : notnull => host.Services.GetRequiredService<T>();

    private ISiteStore Store => Get<ISiteStore>();

    private IMessageLog Log => Get<IMessageLog>();

    private static string Unique(string stem) => $"{stem}{Guid.NewGuid().ToString("N")[..8]}";

    private async Task<string> TeamAsync(string stem = "Pic") =>
        (await Get<TeamRegistry>().CreateAsync(Unique(stem), host.Agent, memberAgent: host.Agent)).Id;

    private async Task<string> KeyAsync(string team, string member)
    {
        return await Get<IPrincipalStore>().MintAsync(
            new ContainerId(team, member).ToString(), PrincipalKind.Container, team, Permits.All, ct: Ct);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error").GetString()!;

    private async Task<HttpResponseMessage> TurnAsync(HttpClient person, string team, bool on) =>
        await person.PutAsJsonAsync($"/api/teams/{team}/team-picture", new { teamPicture = on }, Ct);

    private async Task OnAsync(string team)
    {
        using var person = await host.PersonAsync();
        var turned = await TurnAsync(person, team, true);
        Assert.Equal(HttpStatusCode.OK, turned.StatusCode);
    }

    /// <summary>A person opens the page; answers its capability path.</summary>
    private async Task<string> OpenAsync(string team)
    {
        using var person = await host.PersonAsync(allowAutoRedirect: false);
        var entry = await person.GetAsync($"/sites/{team}/{Site}/", Ct);
        Assert.Equal(HttpStatusCode.Redirect, entry.StatusCode);
        return entry.Headers.Location!.OriginalString;
    }

    /// <summary>What the page itself reads from a collection, through its capability.</summary>
    private async Task<List<JsonElement>> PageReadsAsync(string team, string collection)
    {
        var page = await OpenAsync(team);
        using var sandbox = host.Anonymous();
        var read = await sandbox.GetAsync($"{page}_api/data/{collection}", Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        return [.. (await read.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray()];
    }

    private async Task<JsonElement?> DocAsync(string team, string collection, string id) =>
        await Store.GetDocumentAsync(team, Site, collection, id, Ct) is { } doc ? JsonDocument.Parse(doc.Json).RootElement : null;

    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100, Ct);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    private async Task<string> LiveSourceAsync(string team)
    {
        var site = (await Store.FindAsync(team, Site, Ct))!;
        return (await Store.VersionsAsync(team, Site, Ct)).Single(v => v.Version == site.LiveVersion).Source;
    }

    private async Task<int> UnmeasuredTodayAsync(string team) =>
        (await Get<IUsageLedger>().ReadTeamRunsEndedAsync(team, Get<TeamRegistry>().FloorFor(team), DateTimeOffset.UtcNow.UtcDateTime.Date, null, Ct))
        .Count(r => !r.Measured);

    [Fact]
    public async Task Turned_on_off_and_on_the_page_is_published_unpublished_with_its_data_kept_and_published_again()
    {
        var team = await TeamAsync();
        using var person = await host.PersonAsync();

        var on = await TurnAsync(person, team, true);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await on.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("teamPicture").GetBoolean());
        Assert.True(Get<TeamRegistry>().HasTeamPicture(team));
        Assert.StartsWith(TeamPicture.SourcePrefix, await LiveSourceAsync(team), StringComparison.Ordinal);

        // The page is served from the shipped template under the site policy, and the platform's
        // collections are there before any agent has run.
        var page = await OpenAsync(team);
        using (var sandbox = host.Anonymous())
        {
            Assert.Equal("// template one", await sandbox.GetStringAsync($"{page}app.js", Ct));
        }

        Assert.Equal(team, (await DocAsync(team, TeamPictureRules.Team, "team"))!.Value.GetProperty("team").GetString());

        // A person's customization (as the Concierge's) is data.
        var settings = await person.PutAsync($"/api/teams/{team}/sites/{Site}/data/{TeamPictureRules.Settings}/page", Json("""{"hidden":["spend"]}"""), Ct);
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);

        var off = await TurnAsync(person, team, false);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await off.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("teamPicture").GetBoolean());
        Assert.Null((await Store.FindAsync(team, Site, Ct))!.LiveVersion);
        Assert.NotNull(await DocAsync(team, TeamPictureRules.Settings, "page"));
        Assert.Equal(HttpStatusCode.NotFound, (await person.GetAsync($"/sites/{team}/{Site}/", Ct)).StatusCode);

        // Survives a restart of memory: the row says off.
        Assert.False((await Get<ITeamStore>().TeamsAsync(Ct)).Single(t => t.Id == team).TeamPicture);

        var again = await TurnAsync(person, team, true);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.NotNull((await Store.FindAsync(team, Site, Ct))!.LiveVersion);
        Assert.Equal("""{"hidden":["spend"]}""", (await Store.GetDocumentAsync(team, Site, TeamPictureRules.Settings, "page", Ct))!.Json);
        Assert.True((await Get<ITeamStore>().TeamsAsync(Ct)).Single(t => t.Id == team).TeamPicture);

        var rows = (await Get<ITenantLog>().ReadAsync(take: 500, ct: Ct)).Events
            .Where(r => r.Action == TenantActions.TeamPictureChanged && r.Subject == team).ToList();
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task New_team_sets_it_and_a_clone_keeps_its_sources_choice_with_a_page_of_its_own()
    {
        using var person = await host.PersonAsync();
        var created = await person.PostAsJsonAsync("/api/teams", new
        {
            name = Unique("Made"), agent = host.Agent, memberAgents = new[] { host.Agent }, teamPicture = true,
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(body.GetProperty("teamPicture").GetBoolean());
        var team = body.GetProperty("id").GetString()!;
        Assert.NotNull((await Store.FindAsync(team, Site, Ct))!.LiveVersion);

        var plain = await person.PostAsJsonAsync("/api/teams", new { name = Unique("Plain"), agent = host.Agent, memberAgents = new[] { host.Agent } }, Ct);
        var plainTeam = (await plain.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        Assert.False(Get<TeamRegistry>().HasTeamPicture(plainTeam));
        Assert.Null(await Store.FindAsync(plainTeam, Site, Ct));

        var cloned = await person.PostAsJsonAsync($"/api/teams/{team}/clone", new { name = Unique("Clone") }, Ct);
        Assert.Equal(HttpStatusCode.Created, cloned.StatusCode);
        var clone = (await cloned.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("team");
        Assert.True(clone.GetProperty("teamPicture").GetBoolean());
        var cloneId = clone.GetProperty("id").GetString()!;
        Assert.NotNull((await Store.FindAsync(cloneId, Site, Ct))!.LiveVersion);

        var plainClone = await person.PostAsJsonAsync($"/api/teams/{plainTeam}/clone", new { name = Unique("PlainClone") }, Ct);
        Assert.False((await plainClone.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("team").GetProperty("teamPicture").GetBoolean());
    }

    [Fact]
    public async Task The_install_wizards_team_step_sets_it()
    {
        var docs = Get<TeamDocuments>().EnsureFor(await TeamAsync("Pkg"));
        var folder = SolutionSamples.JobTracker("1.0.0", Path.Combine(docs, "packages", Guid.NewGuid().ToString("N")));

        var outcome = await Get<SolutionInstaller>().InstallAsync(
            new SolutionInstallRequest(folder, Unique("Installed"), TeamPicture: true),
            new SolutionActor("person-id", "person@example.test"), Ct);

        var done = outcome.Body as SolutionDone ?? throw new Xunit.Sdk.XunitException(JsonSerializer.Serialize(outcome.Body));
        Assert.True(Get<TeamRegistry>().HasTeamPicture(done.Team));
        Assert.NotNull((await Store.FindAsync(done.Team, Site, Ct))!.LiveVersion);
    }

    [Fact]
    public async Task The_reserved_name_is_refused_to_a_team_a_member_and_a_package_and_its_page_is_the_platforms()
    {
        var team = await TeamAsync();
        using var person = await host.PersonAsync();

        var asked = await person.PostAsJsonAsync($"/api/teams/{team}/sites", new { name = Site }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        Assert.Equal(TeamPicture.ReservedName, await ErrorAsync(asked));

        var member = await Get<SiteService>().CreateAsync(team, Site, SiteActor.Member(new ContainerId(team, "Dev")), Ct);
        Assert.Equal(409, member.Status);
        Assert.Null(await Store.FindAsync(team, Site, Ct));

        // Once the platform keeps it, its page is not published, rolled back, unpublished or deleted by anyone else.
        await OnAsync(team);
        var folder = Path.Combine(Get<TeamDocuments>().EnsureFor(team), "fake");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.html"), "mine");

        foreach (var (method, path, content) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Post, "publish", new { folder }),
                     (HttpMethod.Post, "rollback", new { }),
                     (HttpMethod.Post, "unpublish", new { }),
                     (HttpMethod.Delete, "?confirm=true", null),
                 })
        {
            using var request = new HttpRequestMessage(method, $"/api/teams/{team}/sites/{Site}{(path.StartsWith('?') ? path : "/" + path)}")
            {
                Content = content is null ? null : JsonContent.Create(content),
            };
            var refused = await person.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal(TeamPicture.PlatformsPage, await ErrorAsync(refused));
        }

        Assert.StartsWith(TeamPicture.SourcePrefix, await LiveSourceAsync(team), StringComparison.Ordinal);

        // A package that ships a site of that name fails its check.
        var docs = Get<TeamDocuments>().EnsureFor(team);
        var package = SolutionSamples.JobTracker("1.0.0", Path.Combine(docs, "packages", Guid.NewGuid().ToString("N")));
        var shipped = Directory.GetDirectories(Path.Combine(package, "sites")).First();
        Directory.Move(shipped, Path.Combine(package, "sites", Site));
        SolutionSamples.Edit(package, m => m["sites"] = new System.Text.Json.Nodes.JsonArray(Site));
        var check = Get<SolutionService>().Check(package);
        Assert.Contains(check.Check!.Refusals, r => r.Reason == TeamPicture.ReservedName);
    }

    [Fact]
    public async Task Who_writes_what_members_pages_and_other_teams_are_refused_and_nobody_writes_the_platforms_collections()
    {
        var team = await TeamAsync();
        var other = await TeamAsync("Other");
        await OnAsync(team);
        await OnAsync(other);

        using var manager = host.Container(await KeyAsync(team, TeamRegistry.DefaultManagerName));
        using var developer = host.Container(await KeyAsync(team, "Developer"));
        using var stranger = host.Container(await KeyAsync(other, TeamRegistry.DefaultManagerName));
        using var person = await host.PersonAsync();
        var data = $"/api/teams/{team}/sites/{Site}/data";

        // The Manager draws the picture; a member writes the operational figures.
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsync($"{data}/tp-picture/w1", Json("""{"kind":"build","title":"T"}"""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await developer.PutAsync($"{data}/tp-ops/today", Json("""{"date":"2026-11-04","counts":{"spam":1}}"""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await developer.PutAsync($"{data}/tp-extra-deploys/d1", Json("""{"title":"v1"}"""), Ct)).StatusCode);

        // A member other than the Manager does not draw; only the Concierge (a person) sets the settings.
        var drawn = await developer.PutAsync($"{data}/tp-picture/w1", Json("""{"kind":"build"}"""), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, drawn.StatusCode);
        Assert.Contains("Manager", await ErrorAsync(drawn), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsync($"{data}/tp-settings/page", Json("{}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await developer.DeleteAsync($"{data}/tp-picture/w1", Ct)).StatusCode);

        // Nobody writes the platform's collections: not a member, the Manager or a person.
        foreach (var collection in TeamPictureRules.PlatformCollections)
        {
            foreach (var client in new[] { manager, developer, person })
            {
                var refused = await client.PutAsync($"{data}/{collection}/x", Json("{}"), Ct);
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Contains("kept by the platform", await ErrorAsync(refused), StringComparison.Ordinal);
            }
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await person.DeleteAsync($"{data}/tp-team/team", Ct)).StatusCode);
        Assert.NotNull(await DocAsync(team, TeamPictureRules.Team, "team"));

        // Another name, and a wrong shape, are refused with a sentence.
        Assert.Equal(HttpStatusCode.Forbidden, (await person.PutAsync($"{data}/jobs/x", Json("{}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PutAsync($"{data}/tp-picture/w1", Json("""{"kind":"poster"}"""), Ct)).StatusCode);
        var shape = await developer.PutAsync($"{data}/tp-ops/today", Json("""{"date":"today","counts":{}}"""), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, shape.StatusCode);
        Assert.Contains("YYYY-MM-DD", await ErrorAsync(shape), StringComparison.Ordinal);

        // Another team's Manager gets what a missing team answers, reading or writing.
        foreach (var refused in new[]
                 {
                     await stranger.PutAsync($"{data}/tp-picture/w1", Json("{}"), Ct),
                     await stranger.GetAsync($"{data}/tp-picture", Ct),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(TeamGate.NoSuchTeam, await refused.Content.ReadAsStringAsync(Ct));
        }

        // And bound to its own team in the service as well: another team's site is a missing one.
        var bound = await Get<SiteService>().PutDocumentAsync(team, Site, TeamPictureRules.Picture, "w1", "{}",
            SiteActor.Member(new ContainerId(other, TeamRegistry.DefaultManagerName)), Ct);
        Assert.Equal((404, SiteService.NoSuchSite), (bound.Status, bound.Refusal));

        // The Concierge draws and customizes, and is refused the platform's collections like anyone.
        var owner = await Get<IUserStore>().FindAsync("person@example.test", Ct);
        using var concierge = host.Container(await Get<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(owner!.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: owner.Id, ct: Ct));
        Assert.Equal(HttpStatusCode.OK, (await concierge.PutAsync($"{data}/tp-settings/page", Json("""{"hidden":["runs"]}"""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await concierge.PutAsync($"{data}/tp-picture/w2", Json("""{"kind":"operational"}"""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await concierge.PutAsync($"{data}/tp-now/x", Json("{}"), Ct)).StatusCode);

        // The page writes nothing on this site, not even its own team's picture.
        var page = await OpenAsync(team);
        using var sandbox = host.Anonymous();
        var fromPage = await sandbox.PostAsync($"{page}_api/data/tp-picture/w1", Json("""{"kind":"build"}"""), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, fromPage.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sandbox.PostAsync($"{page}_api/data/tp-picture/w1/delete", Json("{}"), Ct)).StatusCode);
        Assert.Equal("T", (await DocAsync(team, TeamPictureRules.Picture, "w1"))!.Value.GetProperty("title").GetString());

        // A person (the Concierge's own authority) customizes.
        Assert.Equal(HttpStatusCode.OK, (await person.PutAsync($"{data}/tp-settings/page", Json("""{"hidden":[]}"""), Ct)).StatusCode);
    }

    [Fact]
    public async Task The_live_collections_move_as_cards_runs_and_progress_rows_are_written_with_no_agent_run()
    {
        var team = await TeamAsync();
        await OnAsync(team);

        var manager = new ContainerId(team, TeamRegistry.DefaultManagerName).ToString();
        var developer = new ContainerId(team, "Developer");

        // Told work: the Manager is told, and tells a member. No agent runs anywhere.
        // Rooted at a planner that is not hired, so no member of this test Host starts a run.
        var root = await Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(new ContainerId(team, "Planner")),
            """{"instruction":"Build the export."}""", "console"), Ct);
        var workflow = root.CorrelationId;
        var key = $"w{workflow}";

        await Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(developer), """{"instruction":"Write the CSV writer."}""", manager, workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", developer.ToString(), workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Progress, """{"status":"Writing the CSV header"}""", developer.ToString(), workflow), Ct);

        await UntilAsync(async () => (await PageReadsAsync(team, TeamPictureRules.Now))
            .Any(d => d.GetProperty("doc").GetProperty("lastProgress").ValueKind == JsonValueKind.Object
                && d.GetProperty("doc").GetProperty("lastProgress").GetProperty("text").GetString() == "Writing the CSV header"), "tp-now to name the progress");

        var now = Assert.Single(await PageReadsAsync(team, TeamPictureRules.Now)).GetProperty("doc");
        Assert.Equal("Developer", now.GetProperty("member").GetString());
        Assert.Equal(key, now.GetProperty("item").GetString());
        Assert.Equal($"{workflow}_Developer", now.GetProperty("card").GetProperty("id").GetString());

        var item = Assert.Single(await PageReadsAsync(team, TeamPictureRules.Items), d => d.GetProperty("id").GetString() == key).GetProperty("doc");
        Assert.Equal("workflow", item.GetProperty("kind").GetString());
        Assert.Equal(1, item.GetProperty("progress").GetProperty("running").GetInt32());

        // The run ends, unmeasured: in flight empties, the run is recorded, and its tokens read not measured.
        await Log.AppendAsync(new NewMessage(MessageTypes.Handback, """{"delivered":"Done."}""", developer.ToString(), workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", developer.ToString(), workflow), Ct);

        await UntilAsync(async () => (await PageReadsAsync(team, TeamPictureRules.Now)).Count == 0
            && (await PageReadsAsync(team, TeamPictureRules.Runs)).Any(d => d.GetProperty("doc").GetProperty("member").GetString() == "Developer"
                && d.GetProperty("doc").GetProperty("outcome").GetString() != "running"), "the run to be recorded");

        var run = (await PageReadsAsync(team, TeamPictureRules.Runs)).Single(d => d.GetProperty("doc").GetProperty("member").GetString() == "Developer").GetProperty("doc");
        Assert.Equal(key, run.GetProperty("item").GetString());
        Assert.False(run.GetProperty("tokens").GetProperty("measured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("tokens").GetProperty("input").ValueKind);

        var spend = (await PageReadsAsync(team, TeamPictureRules.Spend)).Single(d => d.GetProperty("id").GetString() == "team").GetProperty("doc");
        var today = spend.GetProperty("today");
        Assert.False(today.GetProperty("measured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, today.GetProperty("tokens").ValueKind);
        // Every run of the team counts, the new team's Manager's own (stubbed) wakes among them.
        Assert.True(today.GetProperty("runsNotMeasured").GetInt32() >= 1);
        await UntilAsync(async () => (await DocAsync(team, TeamPictureRules.Spend, "team"))!.Value.GetProperty("today")
            .GetProperty("runsNotMeasured").GetInt32() == await UnmeasuredTodayAsync(team), "the unmeasured runs to match the ledger");
        Assert.False(spend.GetProperty("estimatedExecutionCost").GetProperty("available").GetBoolean());

        // A measured run moves the figure; a blocked card waits on a person.
        await Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", developer.ToString(), workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.NeedsDecision, """{"question":"CSV or TSV?"}""", developer.ToString(), workflow), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Completed, """{"tokensIn":1200,"tokensOut":300}""", developer.ToString(), workflow), Ct);

        await UntilAsync(async () => (await DocAsync(team, TeamPictureRules.Spend, "team"))?.GetProperty("today").GetProperty("measured").GetBoolean() == true,
            "a measured run to reach tp-spend");
        var measured = (await DocAsync(team, TeamPictureRules.Spend, "team"))!.Value.GetProperty("today");
        Assert.Equal(1500, measured.GetProperty("tokens").GetInt64());
        Assert.Equal(1, measured.GetProperty("runsMeasured").GetInt32());
        await UntilAsync(async () => (await DocAsync(team, TeamPictureRules.Spend, "team"))!.Value.GetProperty("today")
            .GetProperty("runsNotMeasured").GetInt32() == await UnmeasuredTodayAsync(team), "the unmeasured runs to match the ledger");

        await UntilAsync(async () => (await PageReadsAsync(team, TeamPictureRules.Waiting)).Any(d => d.GetProperty("doc").GetProperty("kind").GetString() == "decision"),
            "the decision to wait on a person");

        // The workflow is declared: the item moves to the history, with the picture it had.
        await Store.PutDocumentAsync(team, Site, TeamPictureRules.Picture, key, """{"kind":"build","title":"Export"}""", "test", DateTimeOffset.UtcNow, Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", "console", workflow), Ct);

        await UntilAsync(async () => (await DocAsync(team, TeamPictureRules.History, key)) is not null, "the item to reach the history");
        Assert.Null(await DocAsync(team, TeamPictureRules.Items, key));
        Assert.Equal("Export", (await DocAsync(team, TeamPictureRules.History, key))!.Value.GetProperty("picture").GetProperty("title").GetString());
        Assert.Empty(await PageReadsAsync(team, TeamPictureRules.Waiting));
    }

    [Fact]
    public async Task A_backlog_dispatch_is_an_item_under_its_backlog_id_and_a_schedules_workflow_is_the_ongoing_picture()
    {
        var team = await TeamAsync();
        await OnAsync(team);
        var backlog = Get<IBacklogStore>();

        var entry = await backlog.CreateAsync(null, "Team picture", "Build it.", "person@example.test", Ct);
        var root = await Log.AppendAsync(new NewMessage(MessageTypes.BacklogItemDispatched,
            JsonSerializer.Serialize(new { item = entry.Id, team, title = entry.Title }), "person@example.test"), Ct);
        await backlog.AddDispatchAsync(entry.Id, team, team, root.CorrelationId, "person@example.test", Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(new ContainerId(team, "Planner")),
            """{"instruction":"Plan it."}""", "backlog", root.CorrelationId), Ct);

        // A schedule's fire: its run is the ongoing work, never an item.
        var desk = new ContainerId(team, "Desk");
        var fire = await Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(desk), """{"instruction":"Read the mailbox."}""", "schedule:t1"), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", desk.ToString(), fire.CorrelationId), Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", desk.ToString(), fire.CorrelationId), Ct);

        var key = PlatformBacklogId.Format(entry.Id);
        await UntilAsync(async () => await DocAsync(team, TeamPictureRules.Items, key) is not null
            && (await PageReadsAsync(team, TeamPictureRules.Runs)).Any(d => d.GetProperty("doc").GetProperty("member").GetString() == "Desk"), "the dispatch and the schedule's run");

        var item = (await DocAsync(team, TeamPictureRules.Items, key))!.Value;
        Assert.Equal("backlog", item.GetProperty("kind").GetString());
        Assert.Equal("Team picture", item.GetProperty("title").GetString());
        Assert.Equal(entry.Id, item.GetProperty("backlogId").GetInt64());
        Assert.Single(await PageReadsAsync(team, TeamPictureRules.Items));

        var run = (await PageReadsAsync(team, TeamPictureRules.Runs)).Single(d => d.GetProperty("doc").GetProperty("member").GetString() == "Desk");
        Assert.Equal("ongoing", run.GetProperty("doc").GetProperty("item").GetString());
    }

    [Fact]
    public async Task A_newer_template_replaces_only_the_page_files_and_keeps_the_customization()
    {
        var team = await TeamAsync();
        await OnAsync(team);
        var picture = Get<TeamPicture>();
        var first = await LiveSourceAsync(team);
        var firstVersion = (await Store.FindAsync(team, Site, Ct))!.LiveVersion;

        using var person = await host.PersonAsync();
        using var manager = host.Container(await KeyAsync(team, TeamRegistry.DefaultManagerName));
        var data = $"/api/teams/{team}/sites/{Site}/data";
        Assert.Equal(HttpStatusCode.OK, (await person.PutAsync($"{data}/tp-settings/page", Json("""{"colours":{"accent":"#1d4ed8"}}"""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsync($"{data}/tp-picture/ongoing", Json("""{"kind":"operational","title":"Desk"}"""), Ct)).StatusCode);

        // The same template at start publishes nothing new.
        await picture.RepublishAllAsync(Ct);
        Assert.Equal(firstVersion, (await Store.FindAsync(team, Site, Ct))!.LiveVersion);

        var original = File.ReadAllText(Path.Combine(host.Template, "app.js"));

        try
        {
            File.WriteAllText(Path.Combine(host.Template, "app.js"), "// template two");

            // What the start does when the shipped template is newer.
            await picture.RepublishAllAsync(Ct);

            Assert.NotEqual(first, await LiveSourceAsync(team));
            Assert.Equal(picture.ShippedSource(), await LiveSourceAsync(team));

            var page = await OpenAsync(team);
            using var sandbox = host.Anonymous();
            Assert.Equal("// template two", await sandbox.GetStringAsync($"{page}app.js", Ct));

            Assert.Equal("""{"colours":{"accent":"#1d4ed8"}}""", (await Store.GetDocumentAsync(team, Site, TeamPictureRules.Settings, "page", Ct))!.Json);
            Assert.Equal("Desk", (await DocAsync(team, TeamPictureRules.Picture, "ongoing"))!.Value.GetProperty("title").GetString());

            // And after an off-and-on.
            Assert.Equal(HttpStatusCode.OK, (await TurnAsync(person, team, false)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await TurnAsync(person, team, true)).StatusCode);
            Assert.Equal("""{"colours":{"accent":"#1d4ed8"}}""", (await Store.GetDocumentAsync(team, Site, TeamPictureRules.Settings, "page", Ct))!.Json);
            Assert.NotNull(await DocAsync(team, TeamPictureRules.Picture, "ongoing"));
        }
        finally
        {
            File.WriteAllText(Path.Combine(host.Template, "app.js"), original);
        }
    }

    [Fact]
    public async Task What_the_page_keeps_is_bounded_however_many_runs_and_items_the_team_makes()
    {
        var team = await TeamAsync();
        await OnAsync(team);
        var desk = new ContainerId(team, "Desk");

        // A five-minute team's day and a half: more runs than the page keeps.
        var fire = await Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(desk), """{"instruction":"Read."}""", "schedule:t2"), Ct);
        for (var i = 0; i < TeamPictureFigures.MaxRuns + 30; i++)
        {
            await Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", desk.ToString(), fire.CorrelationId), Ct);
            await Log.AppendAsync(new NewMessage(MessageTypes.Completed, "{}", desk.ToString(), fire.CorrelationId), Ct);
        }

        // More finished items than the history keeps.
        var first = 0L;
        for (var i = 0; i < TeamPictureFigures.MaxHistory + 3; i++)
        {
            var root = await Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(new ContainerId(team, "Planner")),
                $$"""{"instruction":"Item {{i}}"}""", "console"), Ct);
            if (i == 0)
            {
                first = root.CorrelationId;
                await Store.PutDocumentAsync(team, Site, TeamPictureRules.Picture, $"w{first}", """{"kind":"build"}""", "test", DateTimeOffset.UtcNow, Ct);
            }

            await Log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", "console", root.CorrelationId), Ct);
        }

        await UntilAsync(async () => (await Store.ListDocumentsAsync(team, Site, TeamPictureRules.History, Ct)).Count == TeamPictureFigures.MaxHistory
            && (await Store.ListDocumentsAsync(team, Site, TeamPictureRules.Runs, Ct)).Count == TeamPictureFigures.MaxRuns, "the bounds");

        await Get<TeamPictureFigures>().RecomputeAsync(team, Ct);
        Assert.Equal(TeamPictureFigures.MaxRuns, (await Store.ListDocumentsAsync(team, Site, TeamPictureRules.Runs, Ct)).Count);
        Assert.Equal(TeamPictureFigures.MaxHistory, (await Store.ListDocumentsAsync(team, Site, TeamPictureRules.History, Ct)).Count);

        // The oldest item left the history, and its picture with it.
        Assert.Null(await DocAsync(team, TeamPictureRules.History, $"w{first}"));
        Assert.Null(await DocAsync(team, TeamPictureRules.Picture, $"w{first}"));
    }
}
