using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A team's sites through <c>/api</c> and the <c>site</c> MCP tool: reads are Read, changes are the
/// dedicated <see cref="Permits.Sites"/> permit (a member's, a Manager's and the Concierge's by
/// default), a container reaches its own team only, and deleting is a person's action that asks first.
/// </summary>
public sealed class SiteApiTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SiteService Sites => host.Services.GetRequiredService<SiteService>();

    private ISiteStore Store => host.Services.GetRequiredService<ISiteStore>();

    private static string Unique(string stem) => $"{stem}-{Guid.NewGuid():N}"[..(stem.Length + 9)];

    private string Folder(string team, string index = "<p>hello</p>")
    {
        var folder = Path.Combine(
            host.Services.GetRequiredService<TeamDocuments>().EnsureFor(team), "site-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.html"), index);
        return folder;
    }

    /// <summary>A container key of <paramref name="team"/> holding exactly <paramref name="permits"/>.</summary>
    private async Task<(string Id, string Key)> MemberKeyAsync(string team, params string[] permits)
    {
        var id = new ContainerId(team, Unique("m")).ToString();
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            id, PrincipalKind.Container, team, new HashSet<string>(permits), ct: Ct);
        return (id, key);
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error").GetString()!;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task The_Sites_permit_is_granted_by_default_to_members_the_manager_and_the_concierge()
    {
        Assert.Contains(Permits.Sites, Permits.All);
        Assert.Contains(Permits.Sites, TeamRegistry.ManagerPermits);
        Assert.Contains(Permits.Sites, ConciergeLaunchFactory.ConciergePermits);

        // A member hired with no permits named, and one hired with a narrow explicit set, both hold it.
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        await registry.AddContainerAsync(host.Alpha, "Builder", agent, "", [], ct: Ct);
        await registry.AddContainerAsync(host.Alpha, "Narrow", agent, "", [], permits: new HashSet<string> { Permits.Tell }, ct: Ct);

        var members = await host.Services.GetRequiredService<ITeamStore>().MembersAsync(Ct);
        Assert.Contains(Permits.Sites, members.Single(m => m.Team == host.Alpha && m.Name == "Builder").Permits);
        Assert.Contains(Permits.Sites, members.Single(m => m.Team == host.Alpha && m.Name == "Narrow").Permits);
    }

    [Fact]
    public async Task Without_the_Sites_permit_a_key_reads_sites_but_cannot_change_them()
    {
        var site = Unique("ro");
        Assert.True((await Sites.CreateAsync(host.Alpha, site, SiteActor.Person("p", "person@example.test"), Ct)).Ok);

        var (_, readOnly) = await MemberKeyAsync(host.Alpha, Permits.Read, Permits.Progress, Permits.Skills);
        using var reader = host.Container(readOnly);
        var path = $"/api/teams/{host.Alpha}/sites";

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(path, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{path}/{site}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{path}/{site}/data/items", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{path}/{site}/actions", Ct)).StatusCode);

        var changes = new (HttpMethod Method, string Path, string Body)[]
        {
            (HttpMethod.Post, path, """{"name":"another"}"""),
            (HttpMethod.Post, $"{path}/{site}/publish", $$"""{"folder":{{JsonSerializer.Serialize(Folder(host.Alpha))}}}"""),
            (HttpMethod.Post, $"{path}/{site}/rollback", "{}"),
            (HttpMethod.Post, $"{path}/{site}/unpublish", "{}"),
            (HttpMethod.Put, $"{path}/{site}/data/items/t1", """{"title":"x"}"""),
            (HttpMethod.Delete, $"{path}/{site}/data/items/t1", ""),
        };

        foreach (var (method, route, body) in changes)
        {
            using var request = new HttpRequestMessage(method, route) { Content = Json(body) };
            var refused = await reader.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(PermitGate.Missing(Permits.Sites), await ErrorAsync(refused));
        }

        Assert.Null((await Store.FindAsync(host.Alpha, site, Ct))!.LiveVersion);
    }

    [Fact]
    public async Task A_member_with_the_Sites_permit_creates_publishes_writes_rolls_back_and_unpublishes_its_own_teams_site()
    {
        var (id, key) = await MemberKeyAsync(host.Alpha, Permits.Read, Permits.Sites);
        using var member = host.Container(key);
        var site = Unique("own");
        var path = $"/api/teams/{host.Alpha}/sites";

        var created = await member.PostAsJsonAsync(path, new { name = site }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(id, (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("createdBy").GetString());

        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync($"{path}/{site}/publish", new { folder = Folder(host.Alpha, "one") }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync($"{path}/{site}/publish", new { folder = Folder(host.Alpha, "two") }, Ct)).StatusCode);
        Assert.Equal(2, (await Store.FindAsync(host.Alpha, site, Ct))!.LiveVersion);

        var put = await member.PutAsync($"{path}/{site}/data/items/t1", Json("""{"title":"<b>jammed</b>","status":"open"}"""), Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var got = await member.GetFromJsonAsync<JsonElement>($"{path}/{site}/data/items/t1", Ct);
        Assert.Equal("<b>jammed</b>", got.GetProperty("doc").GetProperty("title").GetString());
        Assert.Equal(id, got.GetProperty("updatedBy").GetString());

        var shown = await member.GetFromJsonAsync<JsonElement>($"{path}/{site}", Ct);
        Assert.Equal(2, shown.GetProperty("site").GetProperty("liveVersion").GetInt32());
        Assert.Equal(id, shown.GetProperty("site").GetProperty("publishedBy").GetString());
        Assert.Equal(1, shown.GetProperty("site").GetProperty("documents").GetInt32());
        Assert.Equal([2, 1], shown.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));
        Assert.Equal(["items"], shown.GetProperty("collections").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal($"/sites/{host.Alpha}/{site}/", shown.GetProperty("url").GetString());

        var rolled = await member.PostAsJsonAsync($"{path}/{site}/rollback", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, rolled.StatusCode);
        Assert.Equal(1, (await Store.FindAsync(host.Alpha, site, Ct))!.LiveVersion);

        var missing = await member.PostAsJsonAsync($"{path}/{site}/rollback", new { version = 9 }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.StartsWith($"The site \"{site}\" keeps no version 9.", await ErrorAsync(missing), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync($"{path}/{site}/unpublish", new { }, Ct)).StatusCode);
        Assert.Null((await Store.FindAsync(host.Alpha, site, Ct))!.LiveVersion);

        var deleted = await member.DeleteAsync($"{path}/{site}/data/items/t1", Ct);
        Assert.True((await deleted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("deleted").GetBoolean());

        // Every change to the site appended its tenant row, naming the member.
        var tenant = host.Services.GetRequiredService<ITenantLog>();
        foreach (var action in new[] { TenantActions.SiteCreated, TenantActions.SitePublished, TenantActions.SiteRolledBack, TenantActions.SiteUnpublished })
        {
            var row = await tenant.FindLatestAsync(action, $"{host.Alpha}/{site}", Ct);
            Assert.NotNull(row);
        }
    }

    [Fact]
    public async Task A_limit_through_the_api_is_refused_with_a_sentence()
    {
        var (_, key) = await MemberKeyAsync(host.Alpha, Permits.Read, Permits.Sites);
        using var member = host.Container(key);
        var site = Unique("lim");
        Assert.True((await Sites.CreateAsync(host.Alpha, site, SiteActor.Person("p", "person@example.test"), Ct)).Ok);

        var big = $$"""{"text":"{{new string('x', SiteRules.MaxDocumentBytes)}}"}""";
        var refused = await member.PutAsync($"/api/teams/{host.Alpha}/sites/{site}/data/items/big", Json(big), Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Contains("64 KB", await ErrorAsync(refused), StringComparison.Ordinal);

        var slug = await member.PostAsJsonAsync($"/api/teams/{host.Alpha}/sites", new { name = "Not A Slug" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, slug.StatusCode);
        Assert.EndsWith(".", await ErrorAsync(slug), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_container_is_refused_another_teams_sites_with_what_a_missing_team_gets_and_the_concierge_reaches_any()
    {
        var site = Unique("beta");
        Assert.True((await Sites.CreateAsync(host.Beta, site, SiteActor.Person("p", "person@example.test"), Ct)).Ok);

        var (_, key) = await MemberKeyAsync(host.Alpha, Permits.All.ToArray());
        using var alpha = host.Container(key);
        var path = $"/api/teams/{host.Beta}/sites";

        foreach (var route in new[] { path, $"{path}/{site}", $"{path}/{site}/data/items", $"{path}/{site}/actions" })
        {
            var refused = await alpha.GetAsync(route, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(TeamGate.NoSuchTeam, await refused.Content.ReadAsStringAsync(Ct));
        }

        var write = await alpha.PutAsync($"{path}/{site}/data/items/t1", Json("{}"), Ct);
        Assert.Equal(TeamGate.NoSuchTeam, await write.Content.ReadAsStringAsync(Ct));
        Assert.Empty(await Store.ListDocumentsAsync(host.Beta, site, "items", Ct));

        // The Concierge acts as its owner on any team.
        var person = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct);
        var concierge = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(person!.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: person.Id, ct: Ct);
        using var console = host.Container(concierge);

        var written = await console.PutAsync($"{path}/{site}/data/items/t1", Json("""{"ok":true}"""), Ct);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        Assert.Equal("person@example.test", (await written.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("updatedBy").GetString());
    }

    [Fact]
    public async Task Deleting_a_site_and_listing_every_team_are_a_persons_and_delete_asks_first()
    {
        var site = Unique("del");
        Assert.True((await Sites.CreateAsync(host.Alpha, site, SiteActor.Person("p", "person@example.test"), Ct)).Ok);
        Assert.True((await Sites.PublishAsync(host.Alpha, site, Folder(host.Alpha), SiteActor.Person("p", "person@example.test"), Ct)).Ok);

        using var container = host.Container(host.AlphaContainerKey);
        foreach (var request in new[]
                 {
                     new HttpRequestMessage(HttpMethod.Delete, $"/api/teams/{host.Alpha}/sites/{site}?confirm=true"),
                     new HttpRequestMessage(HttpMethod.Get, "/api/sites"),
                 })
        {
            var refused = await container.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(PermitGate.HumansOnlyMessage, await ErrorAsync(refused));
        }

        using var person = await host.PersonAsync();

        var every = await person.GetFromJsonAsync<JsonElement>("/api/sites", Ct);
        var listed = every.EnumerateArray().Single(s => s.GetProperty("name").GetString() == site);
        Assert.Equal(host.Alpha, listed.GetProperty("team").GetString());
        Assert.Equal(1, listed.GetProperty("liveVersion").GetInt32());
        Assert.Equal("person@example.test", listed.GetProperty("publishedBy").GetString());
        Assert.Equal(0, listed.GetProperty("dataBytes").GetInt64());

        var asked = await person.DeleteAsync($"/api/teams/{host.Alpha}/sites/{site}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        Assert.StartsWith($"Deleting the site \"{site}\" removes its files", await ErrorAsync(asked), StringComparison.Ordinal);
        Assert.NotNull(await Store.FindAsync(host.Alpha, site, Ct));

        var deleted = await person.DeleteAsync($"/api/teams/{host.Alpha}/sites/{site}?confirm=true", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(await Store.FindAsync(host.Alpha, site, Ct));

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.SiteDeleted, $"{host.Alpha}/{site}", Ct);
        Assert.NotNull(row);
        Assert.Equal("person@example.test", row.ActorEmail);
    }

    [Fact]
    public async Task Recent_actions_are_this_sites_only_newest_last_and_read_only()
    {
        var person = SiteActor.Person("p", "person@example.test");
        var site = Unique("act");
        var other = Unique("oth");
        foreach (var name in new[] { site, other })
        {
            Assert.True((await Sites.CreateAsync(host.Alpha, name, person, Ct)).Ok);
            Assert.True((await Sites.PublishAsync(host.Alpha, name, Folder(host.Alpha), person, Ct)).Ok);
        }

        await Sites.PostActionAsync(host.Alpha, site, "done", """{"id":"t1"}""", "person@example.test", Ct);
        await Sites.PostActionAsync(host.Alpha, other, "done", """{"id":"x"}""", "person@example.test", Ct);
        await Sites.PostActionAsync(host.Alpha, site, "assign", """{"id":"t2","assignee":"sam"}""", "person@example.test", Ct);

        using var container = host.Container(host.AlphaContainerKey);
        var actions = await container.GetFromJsonAsync<JsonElement>($"/api/teams/{host.Alpha}/sites/{site}/actions", Ct);

        Assert.Equal(["done", "assign"], actions.EnumerateArray().Select(a => a.GetProperty("action").GetString()));
        var last = actions.EnumerateArray().Last();
        Assert.Equal("sam", last.GetProperty("payload").GetProperty("assignee").GetString());
        Assert.Equal("person@example.test", last.GetProperty("by").GetString());

        var one = await container.GetFromJsonAsync<JsonElement>($"/api/teams/{host.Alpha}/sites/{site}/actions?take=1", Ct);
        Assert.Equal(["assign"], one.EnumerateArray().Select(a => a.GetProperty("action").GetString()));

        // There is no route that posts an action with a key: a click is a person's, through the page.
        var posted = await container.PostAsJsonAsync($"/api/teams/{host.Alpha}/sites/{site}/actions", new { }, Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, posted.StatusCode);
    }

    // ---- the site tool ----

    /// <summary>The tools as a member holding <paramref name="permits"/> calls them. Not async: the
    /// accessor keeps its context in an AsyncLocal, which would not flow back out of an async method.</summary>
    private PlatformMcpTools Tools(string team, params string[] permits)
    {
        var (id, key) = MemberKeyAsync(team, permits).GetAwaiter().GetResult();
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal(id, PrincipalKind.Container, new HashSet<string>(permits)), "test");

        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context },
            host.Services.GetRequiredService<IPrincipalStore>(),
            host.Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(host.ServerHandler()));
    }

    [Fact]
    public async Task The_site_tool_creates_publishes_shows_and_writes_data_on_its_own_team()
    {
        var tools = Tools(host.Alpha, Permits.Read, Permits.Sites);
        var site = Unique("tool");

        Assert.StartsWith("HTTP 201", await tools.Site("create", site: site, cancellationToken: Ct));
        Assert.StartsWith("HTTP 200", await tools.Site("publish", site: site, folder: Folder(host.Alpha), cancellationToken: Ct));

        var put = await tools.Site("data", site: site, op: "put", collection: "items", id: "t1",
            doc: """{"title":"Printer","status":"open"}""", cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", put);

        var got = await tools.Site("data", site: site, op: "get", collection: "items", id: "t1", cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", got);
        Assert.Contains("\"status\":\"open\"", got, StringComparison.Ordinal);

        var shown = await tools.Site("show", site: site, cancellationToken: Ct);
        Assert.Contains($"\"url\":\"/sites/{host.Alpha}/{site}/\"", shown, StringComparison.Ordinal);
        Assert.Contains("\"liveVersion\":1", shown, StringComparison.Ordinal);

        Assert.Contains(site, await tools.Site("list", cancellationToken: Ct), StringComparison.Ordinal);
        Assert.StartsWith("HTTP 200", await tools.Site("actions", site: site, cancellationToken: Ct));
        Assert.StartsWith("HTTP 200", await tools.Site("unpublish", site: site, cancellationToken: Ct));
        Assert.StartsWith("HTTP 200", await tools.Site("data", site: site, op: "list", collection: "items", cancellationToken: Ct));
        Assert.StartsWith("HTTP 200", await tools.Site("data", site: site, op: "delete", collection: "items", id: "t1", cancellationToken: Ct));
    }

    [Fact]
    public async Task The_site_tool_names_each_sites_files_folder()
    {
        var tools = Tools(host.Alpha, Permits.Read, Permits.Sites);
        var docs = host.Services.GetRequiredService<TeamDocuments>().RootFor(host.Alpha);
        string[] names = [Unique("board"), Unique("ledger")];

        foreach (var name in names) Assert.StartsWith("HTTP 201", await tools.Site("create", site: name, cancellationToken: Ct));

        var listed = await tools.Site("list", cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", listed);
        var sites = JsonDocument.Parse(listed[listed.IndexOf('\n')..]).RootElement.EnumerateArray().ToList();

        foreach (var name in names)
        {
            var folder = Path.Combine(docs, "sites", name, "files");
            Assert.True(Path.IsPathFullyQualified(folder));

            Assert.Equal(folder, sites.Single(s => s.GetProperty("name").GetString() == name).GetProperty("filesFolder").GetString());

            var shown = await tools.Site("show", site: name, cancellationToken: Ct);
            Assert.Equal(folder, JsonDocument.Parse(shown[shown.IndexOf('\n')..]).RootElement.GetProperty("site").GetProperty("filesFolder").GetString());
            Assert.True(Directory.Exists(folder));
        }
    }

    [Fact]
    public async Task The_site_tools_refusals_name_the_tool_and_never_a_url()
    {
        var tools = Tools(host.Alpha, Permits.Read, Permits.Sites);
        var site = Unique("ref");
        await tools.Site("create", site: site, cancellationToken: Ct);

        var refusals = new[]
        {
            await tools.Site("delete", site: site, cancellationToken: Ct),
            await tools.Site("explode", site: site, cancellationToken: Ct),
            await tools.Site("show", cancellationToken: Ct),
            await tools.Site("publish", site: site, cancellationToken: Ct),
            await tools.Site("data", site: site, op: "burn", collection: "items", cancellationToken: Ct),
            await tools.Site("data", site: site, op: "get", cancellationToken: Ct),
            await tools.Site("data", site: site, op: "get", collection: "items", cancellationToken: Ct),
            await tools.Site("data", site: site, op: "put", collection: "items", id: "t1", doc: "{not json", cancellationToken: Ct),
        };

        foreach (var refusal in refusals)
        {
            Assert.StartsWith("Refused: ", refusal, StringComparison.Ordinal);
            Assert.Contains("site tool", refusal, StringComparison.Ordinal);
            Assert.DoesNotContain("/api", refusal, StringComparison.Ordinal);
            Assert.DoesNotContain("http", refusal, StringComparison.OrdinalIgnoreCase);
        }

        // What the routes refuse reaches the agent as the route's sentence, with no URL either.
        var passedThrough = new[]
        {
            await Tools(host.Alpha, Permits.Read).Site("create", site: Unique("no"), cancellationToken: Ct),
            await tools.Site("show", team: host.Beta, site: site, cancellationToken: Ct),
            await tools.Site("publish", site: site, folder: "/etc", cancellationToken: Ct),
        };

        Assert.Contains(PermitGate.Missing(Permits.Sites), passedThrough[0], StringComparison.Ordinal);
        Assert.Contains(TeamGate.NoSuchTeamMessage, passedThrough[1], StringComparison.Ordinal);
        Assert.All(passedThrough, text => Assert.DoesNotContain("/api", text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_triage_sample_publishes_through_the_tool_and_is_served_under_the_site_policy()
    {
        var sample = Path.Combine(RepoRoot(), "tests", "Fixtures", "Sites", "triage");
        var index = File.ReadAllText(Path.Combine(sample, "index.html"));

        // The site policy allows no inline script or style, and no network but the site's own.
        Assert.Contains("<script src=\"/sites/_sdk/site.js\"></script>", index, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)", index);
        Assert.DoesNotContain("style=", index, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", index, StringComparison.Ordinal);
        Assert.DoesNotContain("://", index, StringComparison.Ordinal);
        var app = File.ReadAllText(Path.Combine(sample, "app.js"));
        Assert.DoesNotMatch(@"\.(innerHTML|outerHTML|insertAdjacentHTML)\b", app);
        Assert.DoesNotMatch(@"\blocalStorage\.\w", app);

        // As a member publishes it: from a copy in a folder the team may write.
        var folder = Path.Combine(
            host.Services.GetRequiredService<TeamDocuments>().EnsureFor(host.Alpha), "site-src", Guid.NewGuid().ToString("N"), "triage");
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(sample)) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));

        var tools = Tools(host.Alpha, Permits.Read, Permits.Sites);
        var site = Unique("triage");
        Assert.StartsWith("HTTP 201", await tools.Site("create", site: site, cancellationToken: Ct));
        var published = await tools.Site("publish", site: site, folder: folder, cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", published);
        Assert.StartsWith("HTTP 200", await tools.Site("data", site: site, op: "put", collection: "items", id: "t1",
            doc: """{"title":"Printer on 3 is jammed","status":"open"}""", cancellationToken: Ct));

        using var person = await host.PersonAsync(allowAutoRedirect: false);
        var entry = await person.GetAsync($"/sites/{host.Alpha}/{site}/", Ct);
        var page = entry.Headers.Location!.OriginalString;

        using var sandbox = host.Anonymous();
        foreach (var file in new[] { "", "app.js", "site.css" })
        {
            var served = await sandbox.GetAsync(page + file, Ct);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.StartsWith("sandbox allow-scripts allow-forms allow-downloads;", served.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        }

        var items = await sandbox.GetFromJsonAsync<JsonElement>(page + "_api/data/items", Ct);
        Assert.Equal("open", items.EnumerateArray().Single().GetProperty("doc").GetProperty("status").GetString());
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No Harness.slnx above the test output.");
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
