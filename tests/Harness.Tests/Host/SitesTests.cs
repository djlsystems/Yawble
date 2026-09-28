using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// Sites on the real Host: opened by a signed-in person, served and backed under a per-site
/// capability that nothing else accepts and that accepts nothing else. The page's own requests are
/// made by a client holding NO cookie, as a sandboxed opaque-origin page's are.
/// </summary>
public sealed class SitesTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Email = "person@example.test";

    private static readonly SiteActor Person = SiteActor.Person("person-1", Email);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SiteService Sites => host.Services.GetRequiredService<SiteService>();

    private ISiteStore Store => host.Services.GetRequiredService<ISiteStore>();

    /// <summary>Writes <paramref name="files"/> into a folder in the team's documents and returns it.</summary>
    private string Folder(string team, params (string Path, string Text)[] files)
    {
        var folder = Path.Combine(
            host.Services.GetRequiredService<TeamDocuments>().EnsureFor(team), "site-src", Guid.NewGuid().ToString("N"));

        foreach (var (path, text) in files)
        {
            var full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        return folder;
    }

    private async Task<string> PublishedAsync(string team, string site, string index = "<p>hello</p>")
    {
        Assert.True((await Sites.CreateAsync(team, site, Person, Ct)).Ok);

        var published = await Sites.PublishAsync(
            team, site, Folder(team, ("index.html", index), ("app.js", "site.whoami();"), ("css/site.css", "p{}")), Person, Ct);
        Assert.True(published.Ok, published.Refusal);

        return site;
    }

    /// <summary>A person opens the site; answers the capability path the entry redirected to.</summary>
    private async Task<string> OpenAsync(string team, string site)
    {
        using var person = await host.PersonAsync(allowAutoRedirect: false);
        var entry = await person.GetAsync($"/sites/{team}/{site}/", Ct);

        Assert.Equal(HttpStatusCode.Redirect, entry.StatusCode);
        Assert.Equal("no-store", entry.Headers.CacheControl?.ToString());

        var location = entry.Headers.Location!.OriginalString;
        Assert.StartsWith($"/sites/{team}/{site}/_c/", location, StringComparison.Ordinal);
        Assert.EndsWith("/", location, StringComparison.Ordinal);
        return location;
    }

    private static string CapabilityOf(string path) => path.Split('/')[5];

    private static StringContent Text(string body) => new(body, Encoding.UTF8, "text/plain");

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error").GetString()!;

    [Fact]
    public async Task A_signed_in_person_opens_a_site_through_a_capability_and_its_page_is_served_without_the_cookie()
    {
        var site = await PublishedAsync(host.Alpha, "open");
        var page = await OpenAsync(host.Alpha, site);
        using var sandbox = host.Anonymous();

        var index = await sandbox.GetAsync(page, Ct);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("<p>hello</p>", await index.Content.ReadAsStringAsync(Ct));
        Assert.Equal("text/html", index.Content.Headers.ContentType?.MediaType);

        var script = await sandbox.GetAsync($"{page}app.js", Ct);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("site.whoami();", await script.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, (await sandbox.GetAsync($"{page}css/site.css", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sandbox.GetAsync($"{page}missing.js", Ct)).StatusCode);

        // Nothing outside the version folder, and no dot-name, whatever a path spells.
        var row = (await Store.FindAsync(host.Alpha, site, Ct))!;
        Assert.NotNull(Sites.LiveFile(row, ""));
        Assert.Null(Sites.LiveFile(row, "../v1/index.html"));
        Assert.Null(Sites.LiveFile(row, "css/../../index.html"));
        Assert.Null(Sites.LiveFile(row, "/etc/passwd"));
        Assert.Null(Sites.LiveFile(row, ".hidden"));
    }

    [Fact]
    public async Task Nobody_signed_in_is_refused_the_entry_with_a_sentence_and_a_container_key_is_not_a_person()
    {
        var site = await PublishedAsync(host.Alpha, "private");

        using var anonymous = host.Anonymous(allowAutoRedirect: false);
        var refused = await anonymous.GetAsync($"/sites/{host.Alpha}/{site}/", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(SiteEndpoints.SignInFirst, await refused.Content.ReadAsStringAsync(Ct));

        // A site is a person's: a member's key gets the permit gate's refusal, and no capability.
        using var container = host.Container(host.AlphaContainerKey);
        var member = await container.GetAsync($"/sites/{host.Alpha}/{site}/", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
        Assert.Contains("That is a person's action.", await member.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var person = await host.PersonAsync(allowAutoRedirect: false);
        var unknown = await person.GetAsync($"/sites/{host.Alpha}/nosuch/", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(SiteService.NoSuchSite, await unknown.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_capability_is_refused_on_another_site_of_its_team_and_on_another_teams_site()
    {
        var mine = await PublishedAsync(host.Alpha, "mine");
        var other = await PublishedAsync(host.Alpha, "other");
        await PublishedAsync(host.Beta, "mine");

        var capability = CapabilityOf(await OpenAsync(host.Alpha, mine));
        using var sandbox = host.Anonymous();

        foreach (var (team, site) in new[] { (host.Alpha, other), (host.Beta, mine) })
        {
            var file = await sandbox.GetAsync($"/sites/{team}/{site}/_c/{capability}/index.html", Ct);
            Assert.Equal(HttpStatusCode.Forbidden, file.StatusCode);
            Assert.Equal(SiteEndpoints.CapabilityForAnotherSite, await file.Content.ReadAsStringAsync(Ct));

            var data = await sandbox.GetAsync($"/sites/{team}/{site}/_c/{capability}/_api/data/jobs", Ct);
            Assert.Equal(HttpStatusCode.Forbidden, data.StatusCode);
            Assert.Equal(SiteEndpoints.CapabilityForAnotherSite, await ErrorAsync(data));

            var action = await sandbox.PostAsync($"/sites/{team}/{site}/_c/{capability}/_api/actions/done", Text("{}"), Ct);
            Assert.Equal(HttpStatusCode.Forbidden, action.StatusCode);
        }
    }

    [Theory]
    [InlineData("/api/auth/me")]
    [InlineData("/api/teams")]
    [InlineData("/api/overview")]
    [InlineData("/mcp")]
    public async Task The_capability_is_refused_on_every_other_route(string path)
    {
        var site = await PublishedAsync(host.Alpha, $"route-{path.Trim('/').Replace('/', '-')}");
        var capability = CapabilityOf(await OpenAsync(host.Alpha, site));

        using var asKey = host.Anonymous();
        asKey.DefaultRequestHeaders.Add("X-Api-Key", capability);
        Assert.Equal(HttpStatusCode.Unauthorized, (await asKey.GetAsync(path, Ct)).StatusCode);

        using var asBearer = host.Anonymous();
        asBearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", capability);
        Assert.Equal(HttpStatusCode.Unauthorized, (await asBearer.GetAsync(path, Ct)).StatusCode);

        using var asCookie = host.Anonymous();
        asCookie.DefaultRequestHeaders.Add("Cookie", $"{InstanceIdentity.CookieNameFor(host.DataRoot)}={capability}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await asCookie.GetAsync(path, Ct)).StatusCode);

        // And on a team route with the capability where a team route has its segments.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await asKey.GetAsync($"/api/teams/{host.Alpha}/documents/view?path=index.html", Ct)).StatusCode);
    }

    [Fact]
    public async Task An_expired_capability_is_refused_and_a_reload_is_sent_back_to_the_entry()
    {
        var site = await PublishedAsync(host.Alpha, "expired");
        var expired = host.Services.GetRequiredService<SiteCapability>()
            .Issue(host.Alpha, site, "person-1", Email, DateTimeOffset.UtcNow.AddMinutes(-1));
        var page = $"/sites/{host.Alpha}/{site}/_c/{expired}/";

        using var sandbox = host.Anonymous(allowAutoRedirect: false);

        var file = await sandbox.GetAsync($"{page}index.html", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, file.StatusCode);
        Assert.Equal(SiteEndpoints.CapabilityRefused, await file.Content.ReadAsStringAsync(Ct));

        var data = await sandbox.GetAsync($"{page}_api/data/jobs", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, data.StatusCode);
        Assert.Equal(SiteEndpoints.CapabilityRefused, await ErrorAsync(data));

        var before = await LastSeqAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await sandbox.PostAsync($"{page}_api/actions/done", Text("{}"), Ct)).StatusCode);
        Assert.Empty(await ActionsAfterAsync(before));

        // A browser's own navigation (a reload) goes back to the entry, which asks for the cookie.
        using var reload = new HttpRequestMessage(HttpMethod.Get, $"{page}index.html");
        reload.Headers.Add("Sec-Fetch-Mode", "navigate");
        var redirected = await sandbox.SendAsync(reload, Ct);
        Assert.Equal(HttpStatusCode.Redirect, redirected.StatusCode);
        Assert.Equal($"/sites/{host.Alpha}/{site}/", redirected.Headers.Location!.OriginalString);

        // A live one issued the same way is accepted: it is the expiry that refused it.
        var live = host.Services.GetRequiredService<SiteCapability>().Issue(host.Alpha, site, "person-1", Email);
        Assert.Equal(HttpStatusCode.OK, (await sandbox.GetAsync($"/sites/{host.Alpha}/{site}/_c/{live}/index.html", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_session_cookie_alone_does_not_authorize_a_sites_files_data_or_actions()
    {
        var site = await PublishedAsync(host.Alpha, "cookie");
        using var person = await host.PersonAsync(allowAutoRedirect: false);
        var page = $"/sites/{host.Alpha}/{site}/_c/not-a-capability/";

        Assert.Equal(HttpStatusCode.Unauthorized, (await person.GetAsync($"{page}index.html", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await person.GetAsync($"{page}_api/data/jobs", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await person.GetAsync($"{page}_api/whoami", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await person.PostAsync($"{page}_api/data/jobs/j1", Text("{}"), Ct)).StatusCode);

        var before = await LastSeqAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await person.PostAsync($"{page}_api/actions/done", Text("{}"), Ct)).StatusCode);
        Assert.Empty(await ActionsAfterAsync(before));
        Assert.Empty(await Store.ListDocumentsAsync(host.Alpha, site, "jobs", Ct));
    }

    [Fact]
    public async Task A_pages_data_round_trips_through_the_capability_and_answers_an_opaque_origin()
    {
        var site = await PublishedAsync(host.Alpha, "data");
        var api = $"{await OpenAsync(host.Alpha, site)}_api/";
        using var sandbox = host.Anonymous();

        var put = await sandbox.PostAsync($"{api}data/jobs/j1", Text("""{"title":"Engineer","applied":false}"""), Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("*", string.Join(",", put.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("sandbox allow-scripts allow-forms", string.Join(",", put.Headers.GetValues("Content-Security-Policy")));

        using var got = JsonDocument.Parse(await sandbox.GetStringAsync($"{api}data/jobs/j1", Ct));
        Assert.Equal("Engineer", got.RootElement.GetProperty("doc").GetProperty("title").GetString());
        Assert.Equal(Email, got.RootElement.GetProperty("updatedBy").GetString());

        await sandbox.PostAsync($"{api}data/jobs/j2", Text("[1,2]"), Ct);
        using var listed = JsonDocument.Parse(await sandbox.GetStringAsync($"{api}data/jobs", Ct));
        Assert.Equal(["j1", "j2"], listed.RootElement.EnumerateArray().Select(d => d.GetProperty("id").GetString()));

        var deleted = await sandbox.PostAsync($"{api}data/jobs/j2/delete", null, Ct);
        Assert.Equal("""{"deleted":true}""", await deleted.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await sandbox.GetAsync($"{api}data/jobs/j2", Ct)).StatusCode);

        var notJson = await sandbox.PostAsync($"{api}data/jobs/j3", Text("not json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Equal("A document must be JSON.", await ErrorAsync(notJson));

        using var who = JsonDocument.Parse(await sandbox.GetStringAsync($"{api}whoami", Ct));
        Assert.Equal("""{"displayName":"person"}""", who.RootElement.GetRawText());
    }

    [Fact]
    public async Task Each_limit_is_refused_with_a_sentence()
    {
        var site = await PublishedAsync(host.Alpha, "limits");
        var api = $"{await OpenAsync(host.Alpha, site)}_api/";
        using var sandbox = host.Anonymous();

        // 64 KB a document.
        var big = await sandbox.PostAsync($"{api}data/jobs/big", Text($"\"{new string('x', SiteRules.MaxDocumentBytes)}\""), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        Assert.Contains("(64 KB) for one document", await ErrorAsync(big), StringComparison.Ordinal);

        // 10 000 a collection: filled beside the route, then one more through it.
        await FillAsync(host.Alpha, site, "full", SiteRules.MaxDocumentsPerCollection, bytesEach: 2);
        var eleventhousand = await sandbox.PostAsync($"{api}data/full/one-more", Text("{}"), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, eleventhousand.StatusCode);
        Assert.Equal(
            "The collection \"full\" already holds 10000 documents, the most one collection may hold. Delete some, or use another collection.",
            await ErrorAsync(eleventhousand));

        // An existing document of a full collection may still be rewritten.
        Assert.Equal(HttpStatusCode.OK, (await sandbox.PostAsync($"{api}data/full/d0", Text("{}"), Ct)).StatusCode);

        // 50 MB a site: one recorded document stands for the rest.
        await FillAsync(host.Alpha, site, "heavy", 1, bytesEach: SiteRules.MaxSiteDataBytes - 21_000);
        var over = await sandbox.PostAsync($"{api}data/jobs/j1", Text($"\"{new string('y', 100)}\""), Ct);
        Assert.Equal(HttpStatusCode.OK, over.StatusCode);
        var past = await sandbox.PostAsync($"{api}data/jobs/j2", Text($"\"{new string('y', 30_000)}\""), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, past.StatusCode);
        Assert.Contains("past its limit of 52428800 bytes (50 MB) of data", await ErrorAsync(past), StringComparison.Ordinal);

        // 16 KB an action payload, and an action name that is a slug.
        var before = await LastSeqAsync();
        var heavy = await sandbox.PostAsync($"{api}actions/done", Text($"\"{new string('z', SiteRules.MaxActionPayloadBytes)}\""), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, heavy.StatusCode);
        Assert.Contains("over the limit of 16384 bytes (16 KB)", await ErrorAsync(heavy), StringComparison.Ordinal);

        var named = await sandbox.PostAsync($"{api}actions/Not_A_Slug", Text("{}"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, named.StatusCode);
        Assert.Equal(SiteRules.NotASlug("action", "Not_A_Slug"), await ErrorAsync(named));
        Assert.Empty(await ActionsAfterAsync(before));
    }

    [Fact]
    public async Task Publishing_changes_the_live_site_only_once_the_copy_is_complete_and_rollback_restores_the_previous()
    {
        var site = await PublishedAsync(host.Alpha, "versions", index: "one");
        var page = await OpenAsync(host.Alpha, site);
        using var sandbox = host.Anonymous();

        string? seenMidPublish = null;

        // A second service over the same store and files, whose copy step reads the live page
        // through the Host after the new version is on disk and before it is made live.
        var watched = new SiteService(
            Store,
            team => string.Equals(team, host.Alpha, StringComparison.OrdinalIgnoreCase) ? host.Alpha : null,
            host.Services.GetRequiredService<TeamPaths>(),
            host.Services.GetRequiredService<IMessageLog>(),
            copied: async (_, version, ct) =>
            {
                Assert.True(Directory.Exists(Sites.VersionFolder(host.Alpha, site, version)));
                seenMidPublish = await sandbox.GetStringAsync($"{page}index.html", ct);
            });

        var two = await watched.PublishAsync(host.Alpha, site, Folder(host.Alpha, ("index.html", "two")), Person, Ct);
        Assert.True(two.Ok, two.Refusal);
        Assert.Equal(2, two.Value!.Version);

        Assert.Equal("one", seenMidPublish);
        Assert.Equal("two", await sandbox.GetStringAsync($"{page}index.html", Ct));

        // A publish that fails leaves the live version, and makes no folder.
        var refused = await Sites.PublishAsync(
            host.Alpha, site, Folder(host.Alpha, ("index.html", "three"), ("_api/x.json", "{}")), Person, Ct);
        Assert.False(refused.Ok);
        Assert.Contains("\"_api\"", refused.Refusal, StringComparison.Ordinal);
        Assert.Equal("two", await sandbox.GetStringAsync($"{page}index.html", Ct));
        Assert.False(Directory.Exists(Sites.VersionFolder(host.Alpha, site, 3)));

        var outside = await Sites.PublishAsync(host.Alpha, site, Path.GetTempPath(), Person, Ct);
        Assert.False(outside.Ok);
        Assert.StartsWith("A site is published from a folder the team may write", outside.Refusal, StringComparison.Ordinal);

        // Rollback returns the previous version, and both are still kept.
        var back = await Sites.RollbackAsync(host.Alpha, site, null, Person, Ct);
        Assert.True(back.Ok, back.Refusal);
        Assert.Equal(1, back.Value!.LiveVersion);
        Assert.Equal("one", await sandbox.GetStringAsync($"{page}index.html", Ct));
        Assert.Equal([2, 1], (await Store.VersionsAsync(host.Alpha, site, Ct)).Select(v => v.Version));

        Assert.True((await Sites.RollbackAsync(host.Alpha, site, 2, Person, Ct)).Ok);
        Assert.Equal("two", await sandbox.GetStringAsync($"{page}index.html", Ct));

        // Unpublish stops serving and keeps the files and the data.
        await Sites.PutDocumentAsync(host.Alpha, site, "jobs", "kept", "{}", Person, Ct);
        Assert.True((await Sites.UnpublishAsync(host.Alpha, site, Person, Ct)).Ok);
        Assert.Equal(HttpStatusCode.NotFound, (await sandbox.GetAsync($"{page}index.html", Ct)).StatusCode);
        Assert.True(Directory.Exists(Sites.VersionFolder(host.Alpha, site, 2)));
        Assert.NotNull(await Store.GetDocumentAsync(host.Alpha, site, "jobs", "kept", Ct));

        // Every one of those changes is on the tenant log.
        var actions = (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(null, ITenantLog.MaxTake, Ct)).Events
            .Where(e => e.Subject == $"{host.Alpha}/{site}")
            .Select(e => e.Action)
            .Reverse();
        Assert.Equal(
            [TenantActions.SiteCreated, TenantActions.SitePublished, TenantActions.SitePublished,
             TenantActions.SiteRolledBack, TenantActions.SiteRolledBack, TenantActions.SiteUnpublished],
            actions);
    }

    [Fact]
    public async Task A_site_action_is_one_event_row_rooting_a_new_workflow()
    {
        var site = await PublishedAsync(host.Alpha, "actions");
        var api = $"{await OpenAsync(host.Alpha, site)}_api/";
        using var sandbox = host.Anonymous();

        var before = await LastSeqAsync();
        var posted = await sandbox.PostAsync($"{api}actions/apply", Text("""{"job":"j1"}"""), Ct);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        var row = Assert.Single(await ActionsAfterAsync(before));
        Assert.Equal($"site:{host.Alpha}/{site}", row.Source);
        Assert.Null(row.CausationSeq);
        Assert.Equal(row.Seq, row.CorrelationId);
        Assert.Equal(host.Alpha, MessageTeam.Of(row));

        using var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(
            ["team", "site", "action", "siteAction", "payload", "by", "at"],
            payload.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(site, payload.RootElement.GetProperty("site").GetString());
        Assert.Equal("apply", payload.RootElement.GetProperty("action").GetString());
        Assert.Equal($"{site}/apply", payload.RootElement.GetProperty("siteAction").GetString());
        Assert.Equal("""{"job":"j1"}""", payload.RootElement.GetProperty("payload").GetRawText());
        Assert.Equal(Email, payload.RootElement.GetProperty("by").GetString());
        Assert.True(DateTimeOffset.TryParse(payload.RootElement.GetProperty("at").GetString(), out _));

        Assert.Equal($$"""{"seq":{{row.Seq}}}""", await posted.Content.ReadAsStringAsync(Ct));

        var definition = EventCatalog.For(MessageTypes.SiteAction);
        Assert.NotNull(definition);
        Assert.False(EventCatalog.IsHighVolume(MessageTypes.SiteAction));
    }

    [Fact]
    public async Task Deleting_a_site_asks_first_and_a_team_deletion_removes_its_sites_and_data()
    {
        var site = await PublishedAsync(host.Alpha, "doomed");
        await Sites.PutDocumentAsync(host.Alpha, site, "jobs", "j1", "{}", Person, Ct);

        var asked = await Sites.DeleteAsync(host.Alpha, site, confirmed: false, Person, Ct);
        Assert.Equal(409, asked.Status);
        Assert.Equal(
            "Deleting the site \"doomed\" removes its files and its 1 document(s) (2 bytes) for good. Confirm to delete it, or unpublish it to keep them.",
            asked.Refusal);
        Assert.NotNull(await Store.FindAsync(host.Alpha, site, Ct));

        Assert.True((await Sites.DeleteAsync(host.Alpha, site, confirmed: true, Person, Ct)).Ok);
        Assert.Null(await Store.FindAsync(host.Alpha, site, Ct));
        Assert.Empty(await Store.ListDocumentsAsync(host.Alpha, site, "jobs", Ct));
        Assert.False(Directory.Exists(Path.Combine(Sites.Root, host.Alpha, site)));
        Assert.NotNull(await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.SiteDeleted, $"{host.Alpha}/{site}", Ct));

        // A team deletion takes every site of the team, with its files, its data and a row each.
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var gamma = (await registry.CreateAsync($"Gamma{Guid.NewGuid():N}"[..12], agent, memberAgent: agent, ct: Ct)).Id;

        await PublishedAsync(gamma, "board");
        await Sites.PutDocumentAsync(gamma, "board", "jobs", "j1", "{}", Person, Ct);
        Assert.True(Directory.Exists(Path.Combine(Sites.Root, gamma)));

        await host.Services.GetRequiredService<TeamDeletion>().DeleteAsync(gamma, ct: Ct);

        Assert.Empty(await Store.ListAsync(gamma, Ct));
        Assert.Empty(await Store.ListDocumentsAsync(gamma, "board", "jobs", Ct));
        Assert.False(Directory.Exists(Path.Combine(Sites.Root, gamma)));

        var row = await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.SiteDeleted, $"{gamma}/board", Ct);
        Assert.NotNull(row);
        Assert.Equal("""{"reason":"team deleted"}""", row.Detail);
    }

    [Fact]
    public async Task A_member_bound_to_another_team_is_refused_as_if_the_site_did_not_exist()
    {
        var site = await PublishedAsync(host.Alpha, "bound");
        var beta = SiteActor.Member(new ContainerId(host.Beta, "Worker"));
        var alpha = SiteActor.Member(new ContainerId(host.Alpha, "Worker"));

        var refused = await Sites.PutDocumentAsync(host.Alpha, site, "jobs", "j1", "{}", beta, Ct);
        Assert.Equal(404, refused.Status);
        Assert.Equal(SiteService.NoSuchSite, refused.Refusal);
        Assert.Equal(SiteService.NoSuchSite, (await Sites.ShowAsync(host.Alpha, site, beta, Ct)).Refusal);
        Assert.Equal(SiteService.NoSuchSite, (await Sites.UnpublishAsync(host.Alpha, site, beta, Ct)).Refusal);
        Assert.Equal(404, (await Sites.CreateAsync(host.Alpha, "another", beta, Ct)).Status);
        Assert.Empty(await Store.ListDocumentsAsync(host.Alpha, site, "jobs", Ct));

        var written = await Sites.PutDocumentAsync(host.Alpha, site, "jobs", "j1", "{}", alpha, Ct);
        Assert.True(written.Ok, written.Refusal);
        Assert.Equal($"{host.Alpha}/Worker", written.Value!.UpdatedBy);
    }

    private async Task FillAsync(string team, string site, string collection, int count, long bytesEach)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(host.DataRoot, "messages.db")};Pooling=False");
        connection.Open();
        await using var transaction = connection.BeginTransaction();

        for (var i = 0; i < count; i++)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO site_documents (team, site, collection, id, doc, bytes, updated_at, updated_by)
                VALUES ($team, $site, $collection, $id, '{}', $bytes, '2026-09-28T00:00:00Z', 'test')
                """;
            insert.Parameters.AddWithValue("$team", team);
            insert.Parameters.AddWithValue("$site", site);
            insert.Parameters.AddWithValue("$collection", collection);
            insert.Parameters.AddWithValue("$id", $"d{i}");
            insert.Parameters.AddWithValue("$bytes", bytesEach);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);
    }

    private async Task<long> LastSeqAsync() =>
        (await host.Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [MessageTypes.SiteAction], int.MaxValue, Ct))
            .Select(m => m.Seq).DefaultIfEmpty(0).Max();

    private async Task<IReadOnlyList<Message>> ActionsAfterAsync(long seq) =>
        await host.Services.GetRequiredService<IMessageLog>().ReadAfterAsync(seq, [MessageTypes.SiteAction], int.MaxValue, Ct);
}
