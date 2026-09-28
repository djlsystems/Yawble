using System.Reflection;
using System.Security.Claims;
using System.Text;
using Harness.Contracts;
using Harness.Host.Auth;
using Microsoft.AspNetCore.StaticFiles;

namespace Harness.Host;

/// <summary>
/// WHAT A BROWSER ASKS OF A SITE, and how each request is authorized. See <c>docs/sites.md</c>.
///
/// <list type="bullet">
/// <item><c>GET /sites/{team}/{site}/</c> - the ENTRY. The one site route the session cookie
/// authorizes: a signed-in person is issued a <see cref="SiteCapability"/> for this site and
/// redirected to the capability's path. Nothing else is served here.</item>
/// <item><c>/sites/{team}/{site}/_c/{capability}/...</c> - the site's files, and under
/// <c>_api/</c> its data, its actions and <c>whoami</c>. Authorized by the capability and NOTHING
/// else: the handler never reads the cookie, and a capability for another site, an altered one or an
/// expired one is refused. Every response carries <see cref="SitePolicy"/> in place of the app's CSP.</item>
/// <item><c>GET /sites/_sdk/site.js</c> - the helper script, the same bytes for everyone.</item>
/// </list>
///
/// The data and action calls are GETs and POSTs of <c>text/plain</c>, so the opaque-origin page's
/// requests are CORS simple requests that need no preflight; their answers carry
/// <c>Access-Control-Allow-Origin: *</c> without credentials, because what authorizes them is the
/// capability in the path, never an ambient cookie.
/// </summary>
public static class SiteEndpoints
{
    public const string Prefix = "/sites";

    public const string SdkPath = "/sites/_sdk/site.js";

    /// <summary>The capability segment's marker: <c>/sites/{team}/{site}/_c/{capability}/</c>.</summary>
    public const string CapabilitySegment = "_c";

    public const string SignInFirst = "Sign in to open this site.";

    public const string CapabilityRefused =
        "This page's access has expired or is not valid. Open the site again from the app.";

    public const string CapabilityForAnotherSite = "This page's access is for another site.";

    private const string Area = "Sites";

    public static void Map(WebApplication app)
    {
        app.MapGet(SdkPath, () => Results.Text(Sdk, "text/javascript; charset=utf-8"))
            .AllowAnonymous()
            .NoPermitRequired()
            .WithTags(Area)
            .WithSummary("The site helper script")
            .WithDescription(
                "Plain JavaScript a site includes with `<script src=\"/sites/_sdk/site.js\"></script>`. It "
                + "offers `site.data`, `site.action` and `site.whoami`, and carries the page's capability "
                + "on every call. Anonymous: it is the same file for everyone and holds nothing.");

        app.MapGet("/sites/{team}/{site}/", Entry)
            .AllowAnonymous()
            .HumansOnly()
            .WithTags(Area)
            .WithSummary("Open a site")
            .WithDescription(
                "For a signed-in person only (the session cookie). Issues a short-lived capability for "
                + "this one site and redirects to the site's pages under it. A site that is not "
                + "published answers 404.");

        var capability = $"/sites/{{team}}/{{site}}/{CapabilitySegment}/{{capability}}";
        var api = $"{capability}/{SiteRules.ApiSegment}";

        // HUMANS ONLY, AND ANONYMOUS TO THE AUTH SCHEMES: the capability is the only authority, it
        // is only ever issued to a person, and the cookie is never read here.
        app.MapGet($"{capability}/{{**path}}", File).AllowAnonymous().HumansOnly().WithTags(Area)
            .WithSummary("A site's file").WithDescription("Authorized by the capability in the path only.");

        app.MapGet($"{api}/whoami", WhoAmI).AllowAnonymous().HumansOnly().WithTags(Area)
            .WithSummary("Who the page is open for").WithDescription("The person's display name. Never the email or a credential.");

        app.MapGet($"{api}/data/{{collection}}", ListDocuments).AllowAnonymous().HumansOnly().WithTags(Area)
            .WithSummary("A collection's documents").WithDescription("Authorized by the capability in the path only.");

        app.MapGet($"{api}/data/{{collection}}/{{id}}", GetDocument).AllowAnonymous().HumansOnly().WithTags(Area)
            .WithSummary("One document").WithDescription("Authorized by the capability in the path only.");

        app.MapPost($"{api}/data/{{collection}}/{{id}}", PutDocument).AllowAnonymous().HumansOnly().WithTags(Area)
            .WithSummary("Write a document")
            .WithDescription("The body is the document's JSON, sent as text/plain. 64 KB a document, 10 000 a collection, 50 MB a site.");

        app.MapPost($"{api}/data/{{collection}}/{{id}}/delete", DeleteDocument).AllowAnonymous().HumansOnly().WithTags(Area)
            .WithSummary("Delete a document").WithDescription("Authorized by the capability in the path only.");

        app.MapPost($"{api}/actions/{{action}}", PostAction).AllowAnonymous().HumansOnly().WithTags(Area)
            .WithSummary("Post an action")
            .WithDescription(
                "Appends one `site.action` row to the team's log, source `site:<team>/<site>`, rooting a "
                + "new workflow. The body is the payload's JSON as text/plain, at most 16 KB.");
    }

    private static async Task<IResult> Entry(
        string team, string site, HttpContext context, SiteService sites, SiteCapability capabilities,
        CancellationToken ct)
    {
        // THE COOKIE, AND ONLY FOR A PERSON. A key-authenticated caller is not a browser.
        if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.User } person
            || context.User.FindFirstValue(ClaimTypes.Email) is not { Length: > 0 } email)
        {
            return Results.Text(SignInFirst, "text/plain; charset=utf-8", statusCode: 401);
        }

        var found = await sites.FindAsync(team, site, null, ct);
        if (found.Value is not { } row) return Results.Text(found.Refusal, "text/plain; charset=utf-8", statusCode: found.Status);
        if (row.LiveVersion is null) return Results.Text(SiteService.NotPublished, "text/plain; charset=utf-8", statusCode: 404);

        var token = capabilities.Issue(row.Team, row.Name, person.Id, email);
        context.Response.Headers.CacheControl = "no-store";

        return Results.Redirect($"{SiteService.EntryPath(row.Team, row.Name)}{CapabilitySegment}/{token}/");
    }

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private static async Task<IResult> File(
        string team, string site, string capability, string? path, HttpContext context,
        SiteService sites, SiteCapability capabilities, CancellationToken ct)
    {
        var granted = await GrantAsync(team, site, capability, context, sites, capabilities, ct);

        if (granted.Refusal is { } refusal)
        {
            // A person reloading a page whose capability has expired is sent back to the entry,
            // which asks for the cookie and issues a new one. `Sec-Fetch-Mode` is set by the browser
            // and cannot be set by a page's script.
            if (granted.Status == 401
                && string.Equals(context.Request.Headers["Sec-Fetch-Mode"], "navigate", StringComparison.Ordinal)
                && SiteRules.IsSlug(site))
            {
                return Results.Redirect(SiteService.EntryPath(team, site));
            }

            return Results.Text(refusal, "text/plain; charset=utf-8", statusCode: granted.Status);
        }

        if (sites.LiveFile(granted.Site!, path) is not { } file)
        {
            return Results.Text("No such file in this site.", "text/plain; charset=utf-8", statusCode: 404);
        }

        if (!ContentTypes.TryGetContentType(file, out var type)) type = "application/octet-stream";

        context.Response.Headers.CacheControl = "no-cache";
        return Results.File(file, type);
    }

    private static async Task<IResult> WhoAmI(
        string team, string site, string capability, HttpContext context,
        SiteService sites, SiteCapability capabilities, CancellationToken ct)
    {
        var granted = await ApiGrantAsync(team, site, capability, context, sites, capabilities, ct);
        if (granted.Refusal is { } refusal) return Refuse(refusal, granted.Status);

        return Results.Ok(new { displayName = granted.Grant!.DisplayName });
    }

    private static async Task<IResult> ListDocuments(
        string team, string site, string capability, string collection, HttpContext context,
        SiteService sites, SiteCapability capabilities, CancellationToken ct)
    {
        var granted = await ApiGrantAsync(team, site, capability, context, sites, capabilities, ct);
        if (granted.Refusal is { } refusal) return Refuse(refusal, granted.Status);

        var listed = await sites.ListDocumentsAsync(granted.Site!.Team, granted.Site.Name, collection, null, ct);
        if (listed.Refusal is { } why) return Refuse(why, listed.Status);

        return Results.Text(DocumentsJson(listed.Value!), "application/json; charset=utf-8");
    }

    private static async Task<IResult> GetDocument(
        string team, string site, string capability, string collection, string id, HttpContext context,
        SiteService sites, SiteCapability capabilities, CancellationToken ct)
    {
        var granted = await ApiGrantAsync(team, site, capability, context, sites, capabilities, ct);
        if (granted.Refusal is { } refusal) return Refuse(refusal, granted.Status);

        var found = await sites.GetDocumentAsync(granted.Site!.Team, granted.Site.Name, collection, id, null, ct);
        if (found.Refusal is { } why) return Refuse(why, found.Status);

        return Results.Text(DocumentJson(found.Value!), "application/json; charset=utf-8");
    }

    private static async Task<IResult> PutDocument(
        string team, string site, string capability, string collection, string id, HttpContext context,
        SiteService sites, SiteCapability capabilities, CancellationToken ct)
    {
        var granted = await ApiGrantAsync(team, site, capability, context, sites, capabilities, ct);
        if (granted.Refusal is { } refusal) return Refuse(refusal, granted.Status);

        var body = await ReadBodyAsync(context.Request, SiteRules.MaxDocumentBytes, ct);
        if (body is null) return Refuse(SiteRules.DocumentTooLarge(SiteRules.MaxDocumentBytes + 1), 413);

        var written = await sites.PutDocumentAsync(
            granted.Site!.Team, granted.Site.Name, collection, id, body, Person(granted.Grant!), ct);
        if (written.Refusal is { } why) return Refuse(why, written.Status);

        return Results.Text(DocumentJson(written.Value!), "application/json; charset=utf-8");
    }

    private static async Task<IResult> DeleteDocument(
        string team, string site, string capability, string collection, string id, HttpContext context,
        SiteService sites, SiteCapability capabilities, CancellationToken ct)
    {
        var granted = await ApiGrantAsync(team, site, capability, context, sites, capabilities, ct);
        if (granted.Refusal is { } refusal) return Refuse(refusal, granted.Status);

        var deleted = await sites.DeleteDocumentAsync(
            granted.Site!.Team, granted.Site.Name, collection, id, Person(granted.Grant!), ct);
        if (deleted.Refusal is { } why) return Refuse(why, deleted.Status);

        return Results.Ok(new { deleted = deleted.Value });
    }

    private static async Task<IResult> PostAction(
        string team, string site, string capability, string action, HttpContext context,
        SiteService sites, SiteCapability capabilities, CancellationToken ct)
    {
        var granted = await ApiGrantAsync(team, site, capability, context, sites, capabilities, ct);
        if (granted.Refusal is { } refusal) return Refuse(refusal, granted.Status);

        var body = await ReadBodyAsync(context.Request, SiteRules.MaxActionPayloadBytes, ct);

        if (body is null)
        {
            return Refuse(
                $"This action's payload is over the limit of {SiteRules.MaxActionPayloadBytes} bytes (16 KB). "
                + "Send an id and keep the rest in the site's data.", 413);
        }

        var posted = await sites.PostActionAsync(granted.Site!.Team, granted.Site.Name, action, body, granted.Grant!.Email, ct);
        if (posted.Refusal is { } why) return Refuse(why, posted.Status);

        return Results.Ok(new { seq = posted.Value!.Seq });
    }

    private sealed record Granted(SiteGrant? Grant, SiteRow? Site, string? Refusal, int Status);

    /// <summary>
    /// The capability's grant, checked against THIS route's team and site and the site's being live.
    /// Only a valid grant gets the site policy; a refusal keeps the app's.
    /// </summary>
    private static async Task<Granted> GrantAsync(
        string team, string site, string capability, HttpContext context,
        SiteService sites, SiteCapability capabilities, CancellationToken ct)
    {
        if (capabilities.Read(capability) is not { } grant) return new(null, null, CapabilityRefused, 401);

        if (!string.Equals(grant.Team, team, StringComparison.Ordinal)
            || !string.Equals(grant.Site, site, StringComparison.Ordinal))
        {
            return new(null, null, CapabilityForAnotherSite, 403);
        }

        var found = await sites.FindAsync(grant.Team, grant.Site, null, ct);
        if (found.Value is not { } row) return new(null, null, found.Refusal, found.Status);
        if (row.LiveVersion is null) return new(null, null, SiteService.NotPublished, 404);

        context.Items[SecurityHeaders.PolicyOverride] = SitePolicy.For(Origin(context.Request), row.Team, row.Name, capability);

        return new(grant, row, null, 200);
    }

    private static async Task<Granted> ApiGrantAsync(
        string team, string site, string capability, HttpContext context,
        SiteService sites, SiteCapability capabilities, CancellationToken ct)
    {
        // The page runs in an opaque origin, so every call it makes is cross-origin. The answer may
        // be read by it; no credential is sent or allowed, and the capability is what authorizes.
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.CacheControl = "no-store";

        return await GrantAsync(team, site, capability, context, sites, capabilities, ct);
    }

    private static SiteActor Person(SiteGrant grant) => SiteActor.Person(grant.UserId, grant.Email);

    private static string Origin(HttpRequest request) => $"{request.Scheme}://{request.Host.Value}";

    private static IResult Refuse(string sentence, int status) =>
        Results.Json(new { error = sentence }, statusCode: status);

    /// <summary>The body as text, or null when it is longer than <paramref name="limit"/> bytes.</summary>
    internal static async Task<string?> ReadBodyAsync(HttpRequest request, int limit, CancellationToken ct)
    {
        if (request.ContentLength > limit) return null;

        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit) return null;
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string DocumentJson(SiteDocument document) =>
        $$"""{"id":{{System.Text.Json.JsonSerializer.Serialize(document.Id)}},"doc":{{document.Json}},"updatedAt":{{System.Text.Json.JsonSerializer.Serialize(document.UpdatedAt)}},"updatedBy":{{System.Text.Json.JsonSerializer.Serialize(document.UpdatedBy)}}}""";

    private static string DocumentsJson(IReadOnlyList<SiteDocument> documents) =>
        "[" + string.Join(",", documents.Select(DocumentJson)) + "]";

    /// <summary>The helper script, compiled in from <c>Sites/site.js</c>.</summary>
    public static string Sdk { get; } = ReadSdk();

    private static string ReadSdk()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Harness.Host.Sites.site.js")
            ?? throw new InvalidOperationException("The site helper script is not compiled into the Host.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// THE CSP EVERY SITE RESPONSE CARRIES, in place of the app's, through
/// <see cref="SecurityHeaders.PolicyOverride"/> and nothing else.
///
/// <list type="bullet">
/// <item><c>sandbox allow-scripts allow-forms allow-downloads</c> and NOT <c>allow-same-origin</c>: the page runs in
/// an opaque origin, so it cannot read the app's cookie, storage or DOM, and its own requests to
/// the Host carry no cookie (it is <c>SameSite=Strict</c>, and an opaque origin is never same-site).
/// No <c>allow-top-navigation</c>, no <c>allow-popups</c>, so no <c>allow-popups-to-escape-sandbox</c>.
/// <c>allow-downloads</c> lets a link save a file the site itself serves (a document a team made for
/// the person): without it the browser drops every download silently. It widens nothing the page can
/// read: a download's request from the opaque origin carries no cookie (<c>SameSite=Strict</c>), so it
/// reaches only what the capability in its path already serves, and the browser shows every download.</item>
/// <item>Scripts, styles, images and fonts from the site's own files under this capability only,
/// plus the helper script; nothing inline, nothing from anywhere else.</item>
/// <item><c>connect-src</c> the site's own <c>_api/</c> under this capability, and nothing else.</item>
/// <item><c>form-action 'none'</c>: a form is handled by script; it can never post to <c>/api</c>.</item>
/// </list>
///
/// Sources carry the request's scheme and host, because a CSP path needs an origin; under CSP Level
/// 3 an <c>http:</c> source also matches the same host over <c>https:</c>, so a TLS tunnel is covered.
/// </summary>
public static class SitePolicy
{
    public static string For(string origin, string team, string site, string capability)
    {
        var files = $"{origin}{SiteService.EntryPath(team, site)}{SiteEndpoints.CapabilitySegment}/{capability}/";
        var api = $"{files}{SiteRules.ApiSegment}/";
        var sdk = $"{origin}{SiteEndpoints.SdkPath}";

        return "sandbox allow-scripts allow-forms allow-downloads; "
            + "default-src 'none'; "
            + $"script-src {files} {sdk}; "
            + $"style-src {files}; "
            + $"img-src {files}; "
            + $"font-src {files}; "
            + $"connect-src {api}; "
            + "form-action 'none'; "
            + "base-uri 'none'; "
            + "object-src 'none'; "
            + "frame-ancestors 'none'";
    }
}
