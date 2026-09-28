using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Harness.Host;

public sealed partial class PlatformMcpTools
{
    private const string SiteActions =
        "create, publish, list, show, data, actions, unpublish, or rollback";

    private const string SiteDataActions = "list, get, put, or delete";

    [McpServerTool(Name = "site"), Description(
        "The team's sites: small web pages the platform serves to signed-in people, with a data store and "
        + "actions. action is " + SiteActions + ". data takes op " + SiteDataActions + ". "
        + "A member or Manager acts on its own team's sites; a Concierge passes team. Deleting a site is a "
        + "person's action. This is harness site. Do not request /api yourself.")]
    public async Task<string> Site(
        [Description(SiteActions + ".")] string action,
        [Description("Team id. Required for a Concierge. Omit for a member of a team.")] string? team = null,
        [Description("The site's name: a slug such as triage. Every action but list needs it.")] string? site = null,
        [Description("For publish: the absolute path of the folder to publish, under the team's documents folder or a worktree. It must hold index.html.")]
        string? folder = null,
        [Description("For rollback: the kept version to make live. Omit for the one before the live one.")] int? version = null,
        [Description("For data: list, get, put, or delete.")] string? op = null,
        [Description("For data: the collection's name, a slug.")] string? collection = null,
        [Description("For data get, put and delete: the document's id.")] string? id = null,
        [Description("For data put: the document, as JSON text. At most 64 KB.")] string? doc = null,
        [Description("For actions: how many of the most recent to read, up to 200. 20 by default.")] int? take = null,
        CancellationToken cancellationToken = default)
    {
        var verb = (action ?? "").Trim().ToLowerInvariant();

        if (verb is "delete")
        {
            return "Refused: the site tool does not delete a site. Deleting one is a person's action, in "
                + "Admin > Sites. Use site action: unpublish to stop serving it and keep its files and data.";
        }

        if (verb is not ("create" or "publish" or "list" or "show" or "data" or "actions" or "unpublish" or "rollback"))
        {
            return $"Refused: the site tool's action is {SiteActions}.";
        }

        var resolved = await TeamAsync(team, cancellationToken);
        if (resolved is null) return "Refused: the site tool needs a team. A Concierge has no default team.";

        var sites = "/api/teams/" + Uri.EscapeDataString(resolved) + "/sites";

        if (verb is "list") return await SendAsync(HttpMethod.Get, sites, null, cancellationToken);

        if (string.IsNullOrWhiteSpace(site)) return $"Refused: the site tool's {verb} needs site, the site's name.";

        var name = site.Trim();
        var sitePath = sites + "/" + Uri.EscapeDataString(name);

        switch (verb)
        {
            case "create":
                return await SendAsync(HttpMethod.Post, sites, new { name }, cancellationToken);

            case "show":
                return await SendAsync(HttpMethod.Get, sitePath, null, cancellationToken);

            case "publish":
                if (string.IsNullOrWhiteSpace(folder))
                {
                    return "Refused: the site tool's publish needs folder, the absolute path of the folder to publish.";
                }

                return await SendAsync(HttpMethod.Post, sitePath + "/publish", new { folder = folder.Trim() }, cancellationToken);

            case "rollback":
                return await SendAsync(HttpMethod.Post, sitePath + "/rollback", new { version }, cancellationToken);

            case "unpublish":
                return await SendAsync(HttpMethod.Post, sitePath + "/unpublish", new { }, cancellationToken);

            case "actions":
                return await SendAsync(
                    HttpMethod.Get,
                    sitePath + "/actions" + Query(("take", take?.ToString(CultureInfo.InvariantCulture))),
                    null,
                    cancellationToken);

            default:
                return await SiteDataAsync(sitePath, op, collection, id, doc, cancellationToken);
        }
    }

    private async Task<string> SiteDataAsync(
        string sitePath, string? op, string? collection, string? id, string? doc, CancellationToken ct)
    {
        var verb = (op ?? "").Trim().ToLowerInvariant();
        if (verb is not ("list" or "get" or "put" or "delete"))
        {
            return $"Refused: the site tool's data takes op {SiteDataActions}.";
        }

        if (string.IsNullOrWhiteSpace(collection)) return "Refused: the site tool's data needs collection.";

        var collectionPath = sitePath + "/data/" + Uri.EscapeDataString(collection.Trim());
        if (verb is "list") return await SendAsync(HttpMethod.Get, collectionPath, null, ct);

        if (string.IsNullOrWhiteSpace(id)) return $"Refused: the site tool's data {verb} needs id, the document's id.";

        var documentPath = collectionPath + "/" + Uri.EscapeDataString(id.Trim());

        switch (verb)
        {
            case "get":
                return await SendAsync(HttpMethod.Get, documentPath, null, ct);

            case "delete":
                return await SendAsync(HttpMethod.Delete, documentPath, null, ct);

            default:
                if (string.IsNullOrWhiteSpace(doc)) return "Refused: the site tool's data put needs doc, the document as JSON.";

                JsonElement parsed;
                try
                {
                    using var document = JsonDocument.Parse(doc);
                    parsed = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return "Refused: the site tool's doc must be JSON text, such as {\"title\":\"...\"}.";
                }

                return await SendAsync(HttpMethod.Put, documentPath, parsed, ct);
        }
    }
}
