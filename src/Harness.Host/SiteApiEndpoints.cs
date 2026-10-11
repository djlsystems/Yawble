using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>What an agent or a person sends to create a site.</summary>
public sealed record CreateSiteRequest(
    [property: Description("The site's name: a slug (lower-case letters, digits and '-'), unique within the team.")]
    string? Name);

/// <summary>What an agent or a person sends to publish a site.</summary>
public sealed record PublishSiteRequest(
    [property: Description(
        "The absolute path of the folder to publish: under the team's documents folder or its team folder "
        + "(a worktree, a workspace). It must hold an index.html.")]
    string? Folder);

/// <summary>What an agent or a person sends to roll a site back.</summary>
public sealed record RollbackSiteRequest(
    [property: Description("The kept version to make live. Omit for the newest one before the live one.")]
    int? Version);

/// <summary>
/// A TEAM'S SITES THROUGH THE API: what the <c>site</c> MCP tool relays to and what Admin → Sites
/// calls. Every change goes through <see cref="SiteService"/>, which appends its <c>tenant_events</c>
/// row in the same transaction.
///
/// <list type="bullet">
/// <item><c>/api/teams/{team}/sites...</c> - reads carry <see cref="Permits.Read"/>, changes carry
/// <see cref="Permits.Sites"/>. <c>TeamGate</c> bounds a container principal to its own team, and the
/// handler also passes the container's binding to the service (<see cref="SiteActor.Member"/>).</item>
/// <item><c>GET /api/sites</c> and <c>DELETE /api/teams/{team}/sites/{site}</c> are
/// <c>HumansOnly</c>: the every-team list is Admin → Sites', and deleting a site is a person's action
/// that answers 409 with what would be lost until it is confirmed.</item>
/// </list>
/// </summary>
public static class SiteApiEndpoints
{
    private const string Area = "Sites";

    /// <summary>The most recent actions <c>GET .../actions</c> answers by default, and at most.</summary>
    public const int DefaultActions = 20;

    public const int MaxActions = 200;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/sites", ListEvery)
            .HumansOnly()
            .WithTags(Area)
            .WithSummary("Every team's sites")
            .WithDescription(
                "Every site on the instance: team, name, live version, when and by whom the live version was "
                + "published, its data size and the URL a person opens it at. Admin → Sites.\n\n**A person's view.**");

        var team = "/api/teams/{team}/sites";
        var site = team + "/{site}";

        app.MapGet(team, List)
            .RequirePermit(Permits.Read)
            .WithTags(Area)
            .WithSummary("A team's sites")
            .WithDescription("The team's sites, as `GET /api/sites` lists them.");

        app.MapPost(team, Create)
            .RequirePermit(Permits.Sites)
            .WithTags(Area)
            .WithSummary("Create a site")
            .WithDescription(
                "Body `{ \"name\": \"triage\" }`. An empty site; publish a folder to it. Appends `site.created` "
                + "to the tenant log. 400 for a name that is not a slug, 409 when the team has one of that name.");

        app.MapGet(site, Show)
            .RequirePermit(Permits.Read)
            .WithTags(Area)
            .WithSummary("One site")
            .WithDescription("The site, its kept versions, its collections and data size, and its URL.");

        app.MapPost(site + "/publish", Publish)
            .RequirePermit(Permits.Sites)
            .WithTags(Area)
            .WithSummary("Publish a site")
            .WithDescription(
                "Body `{ \"folder\": \"/abs/path\" }`. Copies the folder into a new version and makes it live once "
                + "the copy is complete; the live site does not change before then. The folder must be under the "
                + "team's documents folder or team folder and hold an index.html. Appends `site.published`.");

        app.MapPost(site + "/rollback", Rollback)
            .RequirePermit(Permits.Sites)
            .WithTags(Area)
            .WithSummary("Roll a site back")
            .WithDescription(
                "Body `{ \"version\": 2 }`, or `{}` for the newest kept version before the live one. Appends "
                + "`site.rolled-back`. 409 with a sentence when there is no such kept version.");

        app.MapPost(site + "/unpublish", Unpublish)
            .RequirePermit(Permits.Sites)
            .WithTags(Area)
            .WithSummary("Unpublish a site")
            .WithDescription("Stops serving the site. Its versions and its data are kept. Appends `site.unpublished`.");

        app.MapDelete(site, Delete)
            .HumansOnly()
            .WithTags(Area)
            .WithSummary("Delete a site")
            .WithDescription(
                "Asks first: without `?confirm=true` it answers 409 with what would be lost and changes nothing. "
                + "Confirmed, it removes the site, every version and all its data, and appends `site.deleted`."
                + "\n\n**A person's action.**");

        app.MapGet(site + "/data/{collection}", ListDocuments)
            .RequirePermit(Permits.Read)
            .WithTags(Area)
            .WithSummary("A site collection's documents")
            .WithDescription("Every document in the collection: id, doc, updatedAt, updatedBy.");

        app.MapGet(site + "/data/{collection}/{id}", GetDocument)
            .RequirePermit(Permits.Read)
            .WithTags(Area)
            .WithSummary("One site document")
            .WithDescription("404 with a sentence when there is none.");

        app.MapPut(site + "/data/{collection}/{id}", PutDocument)
            .RequirePermit(Permits.Sites)
            .WithTags(Area)
            .WithSummary("Write a site document")
            .WithDescription(
                "The body is the document's JSON. 64 KB a document, 10 000 a collection and 50 MB a site, each "
                + "refused with a sentence.");

        app.MapDelete(site + "/data/{collection}/{id}", DeleteDocument)
            .RequirePermit(Permits.Sites)
            .WithTags(Area)
            .WithSummary("Delete a site document")
            .WithDescription("Answers whether there was one to delete.");

        app.MapGet(site + "/actions", Actions)
            .RequirePermit(Permits.Read)
            .WithTags(Area)
            .WithSummary("A site's recent actions")
            .WithDescription(
                $"The most recent `site.action` rows this site posted, oldest first: `?take=` up to {MaxActions}, "
                + $"{DefaultActions} by default. Read only: an action is a person's click.");
    }

    // ---- who is asking ----

    /// <summary>
    /// The caller as a <see cref="SiteActor"/>: a person, a member bound to its own team, or - for the
    /// Concierge and a person's key - unbound, acting as its owner.
    /// </summary>
    internal static async Task<SiteActor?> ActorAsync(HttpContext context, IUserStore users, CancellationToken ct)
    {
        if (PrincipalClaims.From(context.User) is not { } principal) return null;

        switch (principal.Kind)
        {
            case PrincipalKind.User:
                return SiteActor.Person(principal.Id, context.User.FindFirstValue(ClaimTypes.Email) ?? principal.Id);

            case PrincipalKind.Container:
                return ContainerId.TryParse(principal.Id, out var member) ? SiteActor.Member(member) : null;

            default:
                var owner = principal.OwnerUserId is { } id ? await users.FindByIdAsync(id, ct) : null;
                return SiteActor.Unbound(principal.Id, owner?.Email);
        }
    }

    private static IResult Refused<T>(SiteResult<T> result) =>
        Results.Json(new { error = result.Refusal }, statusCode: result.Status);

    private static IResult NotSignedIn() => Results.Unauthorized();

    // ---- wire ----

    /// <summary>A site on the wire. <c>filesFolder</c> is the absolute path of its files folder in the
    /// team's documents, repaired here: this is how an agent is told it (the <c>site</c> tool relays
    /// these answers).</summary>
    public static async Task<object> WireAsync(SiteRow site, SiteService sites, ISiteStore store, CancellationToken ct)
    {
        var versions = await store.VersionsAsync(site.Team, site.Name, ct);
        var usage = await store.UsageAsync(site.Team, site.Name, ct);
        var live = versions.FirstOrDefault(v => v.Version == site.LiveVersion);

        return new
        {
            team = site.Team,
            name = site.Name,
            liveVersion = site.LiveVersion,
            publishedAt = live?.PublishedAt,
            publishedBy = live?.PublishedBy,
            createdAt = site.CreatedAt,
            createdBy = site.CreatedBy,
            documents = usage.Documents,
            dataBytes = usage.Bytes,
            url = SiteService.EntryPath(site.Team, site.Name),
            filesFolder = sites.Files.Ensure(site.Team, site.Name),
        };
    }

    private static object Wire(SiteVersionRow version, int? live) => new
    {
        version = version.Version,
        live = version.Version == live,
        publishedAt = version.PublishedAt,
        publishedBy = version.PublishedBy,
        source = version.Source,
        files = version.Files,
        bytes = version.Bytes,
    };

    private static object Wire(SiteDocument document) => new
    {
        collection = document.Collection,
        id = document.Id,
        doc = JsonDocument.Parse(document.Json).RootElement.Clone(),
        updatedAt = document.UpdatedAt,
        updatedBy = document.UpdatedBy,
    };

    // ---- handlers ----

    private static async Task<IResult> ListEvery(SiteService sites, ISiteStore store, CancellationToken ct)
    {
        var listed = new List<object>();
        foreach (var site in await sites.ListAsync(null, ct)) listed.Add(await WireAsync(site, sites, store, ct));
        return Results.Ok(listed);
    }

    private static async Task<IResult> List(
        [Description("The team id")] string team,
        SiteService sites, ISiteStore store, TeamRegistry teams, CancellationToken ct)
    {
        if (teams.ExistingName(team) is null) return Results.NotFound(new { error = TeamGate.NoSuchTeamMessage });

        var listed = new List<object>();
        foreach (var site in await sites.ListAsync(team, ct)) listed.Add(await WireAsync(site, sites, store, ct));
        return Results.Ok(listed);
    }

    private static async Task<IResult> Create(
        [Description("The team id")] string team,
        CreateSiteRequest request, HttpContext context, SiteService sites, ISiteStore store, IUserStore users,
        CancellationToken ct)
    {
        if (await ActorAsync(context, users, ct) is not { } actor) return NotSignedIn();

        var created = await sites.CreateAsync(team, request.Name?.Trim() ?? "", actor, ct);
        if (created.Value is not { } row) return Refused(created);

        return Results.Created(SiteService.EntryPath(row.Team, row.Name), await WireAsync(row, sites, store, ct));
    }

    private static async Task<IResult> Show(
        [Description("The team id")] string team,
        [Description("The site's name")] string site,
        HttpContext context, SiteService sites, ISiteStore store, IUserStore users, CancellationToken ct)
    {
        if (await ActorAsync(context, users, ct) is not { } actor) return NotSignedIn();

        var shown = await sites.ShowAsync(team, site, actor, ct);
        if (shown.Value is not { } view) return Refused(shown);

        return Results.Ok(new
        {
            site = await WireAsync(view.Site, sites, store, ct),
            versions = view.Versions.OrderByDescending(v => v.Version).Select(v => Wire(v, view.Site.LiveVersion)),
            collections = view.Collections,
            url = view.Url,
        });
    }

    private static async Task<IResult> Publish(
        [Description("The team id")] string team,
        [Description("The site's name")] string site,
        PublishSiteRequest request, HttpContext context, SiteService sites, IUserStore users, CancellationToken ct)
    {
        if (await ActorAsync(context, users, ct) is not { } actor) return NotSignedIn();

        if (string.IsNullOrWhiteSpace(request.Folder))
        {
            return Results.BadRequest(new { error = "Name the folder to publish, as an absolute path." });
        }

        var published = await sites.PublishAsync(team, site, request.Folder.Trim(), actor, ct);
        if (published.Value is not { } version) return Refused(published);

        return Results.Ok(new
        {
            version = Wire(version, version.Version),
            url = SiteService.EntryPath(team, site),
        });
    }

    private static async Task<IResult> Rollback(
        [Description("The team id")] string team,
        [Description("The site's name")] string site,
        RollbackSiteRequest? request, HttpContext context, SiteService sites, ISiteStore store, IUserStore users,
        CancellationToken ct)
    {
        if (await ActorAsync(context, users, ct) is not { } actor) return NotSignedIn();

        var rolled = await sites.RollbackAsync(team, site, request?.Version, actor, ct);
        return rolled.Value is { } row ? Results.Ok(await WireAsync(row, sites, store, ct)) : Refused(rolled);
    }

    private static async Task<IResult> Unpublish(
        [Description("The team id")] string team,
        [Description("The site's name")] string site,
        HttpContext context, SiteService sites, ISiteStore store, IUserStore users, CancellationToken ct)
    {
        if (await ActorAsync(context, users, ct) is not { } actor) return NotSignedIn();

        var unpublished = await sites.UnpublishAsync(team, site, actor, ct);
        return unpublished.Value is { } row ? Results.Ok(await WireAsync(row, sites, store, ct)) : Refused(unpublished);
    }

    private static async Task<IResult> Delete(
        [Description("The team id")] string team,
        [Description("The site's name")] string site,
        [Description("`true` to delete. Without it the answer is 409 with what would be lost.")] bool? confirm,
        HttpContext context, SiteService sites, IUserStore users, CancellationToken ct)
    {
        if (await ActorAsync(context, users, ct) is not { } actor) return NotSignedIn();

        var deleted = await sites.DeleteAsync(team, site, confirm == true, actor, ct);
        return deleted.Ok ? Results.NoContent() : Refused(deleted);
    }

    private static async Task<IResult> ListDocuments(
        [Description("The team id")] string team,
        [Description("The site's name")] string site,
        [Description("The collection's name, a slug")] string collection,
        HttpContext context, SiteService sites, IUserStore users, CancellationToken ct)
    {
        if (await ActorAsync(context, users, ct) is not { } actor) return NotSignedIn();

        var listed = await sites.ListDocumentsAsync(team, site, collection, actor, ct);
        return listed.Value is { } documents ? Results.Ok(documents.Select(Wire)) : Refused(listed);
    }

    private static async Task<IResult> GetDocument(
        [Description("The team id")] string team,
        [Description("The site's name")] string site,
        [Description("The collection's name, a slug")] string collection,
        [Description("The document's id")] string id,
        HttpContext context, SiteService sites, IUserStore users, CancellationToken ct)
    {
        if (await ActorAsync(context, users, ct) is not { } actor) return NotSignedIn();

        var got = await sites.GetDocumentAsync(team, site, collection, id, actor, ct);
        return got.Value is { } document ? Results.Ok(Wire(document)) : Refused(got);
    }

    private static async Task<IResult> PutDocument(
        [Description("The team id")] string team,
        [Description("The site's name")] string site,
        [Description("The collection's name, a slug")] string collection,
        [Description("The document's id")] string id,
        HttpContext context, SiteService sites, IUserStore users, CancellationToken ct)
    {
        if (await ActorAsync(context, users, ct) is not { } actor) return NotSignedIn();

        // Read raw and bounded: nothing past the document limit is worth holding in memory.
        var json = await SiteEndpoints.ReadBodyAsync(context.Request, SiteRules.MaxDocumentBytes, ct);
        if (json is null)
        {
            return Results.Json(
                new { error = SiteRules.DocumentTooLarge(SiteRules.MaxDocumentBytes + 1) },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var put = await sites.PutDocumentAsync(team, site, collection, id, json, actor, ct);
        return put.Value is { } document ? Results.Ok(Wire(document)) : Refused(put);
    }

    private static async Task<IResult> DeleteDocument(
        [Description("The team id")] string team,
        [Description("The site's name")] string site,
        [Description("The collection's name, a slug")] string collection,
        [Description("The document's id")] string id,
        HttpContext context, SiteService sites, IUserStore users, CancellationToken ct)
    {
        if (await ActorAsync(context, users, ct) is not { } actor) return NotSignedIn();

        var deleted = await sites.DeleteDocumentAsync(team, site, collection, id, actor, ct);
        return deleted.Ok ? Results.Ok(new { deleted = deleted.Value }) : Refused(deleted);
    }

    private static async Task<IResult> Actions(
        [Description("The team id")] string team,
        [Description("The site's name")] string site,
        [Description("How many of the most recent actions, up to 200; 20 by default")] int? take,
        HttpContext context, SiteService sites, IMessageLog log, IUserStore users, CancellationToken ct)
    {
        if (await ActorAsync(context, users, ct) is not { } actor) return NotSignedIn();

        var found = await sites.FindAsync(team, site, actor, ct);
        if (found.Value is not { } row) return Refused(found);

        var wanted = Math.Clamp(take ?? DefaultActions, 1, MaxActions);
        var recent = await SiteService.RecentActionsAsync(log, row.Team, row.Name, wanted, ct);

        return Results.Ok(recent.Select(message =>
        {
            using var payload = JsonDocument.Parse(message.Payload);
            var root = payload.RootElement;
            return new
            {
                seq = message.Seq,
                correlationId = message.CorrelationId,
                action = root.TryGetProperty(PayloadFields.Action, out var a) ? a.GetString() : null,
                payload = root.TryGetProperty(PayloadFields.Payload, out var p) ? p.Clone() : default(JsonElement?),
                by = root.TryGetProperty(PayloadFields.By, out var b) ? b.GetString() : null,
                at = message.OccurredAt,
            };
        }).ToList());
    }
}
