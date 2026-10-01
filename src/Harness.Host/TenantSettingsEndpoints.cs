using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// <c>GET</c> and <c>PUT /api/tenant/settings</c>: every instance-wide setting, where its
/// value came from and who last changed it; and a partial update, validated and audited.
/// </summary>
public static class TenantSettingsEndpoints
{
    /// <summary>The note every FileBrowser root carries: listing it is not the whole of making it
    /// reachable.</summary>
    public const string RootMountNote =
        "A deployment setting: change FileBrowser:Roots in appsettings.Production.json and restart. "
        + "The container must also mount the path (see the podman run command); the setting is a "
        + "container path, not a host path.";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/tenant/settings", (TenantSettings settings, FileBrowserPolicy fileBrowser) =>
                Results.Json(Describe(settings, fileBrowser)))
            .RequirePermit(Permits.Read)
            .WithTags("Tenant")
            .WithSummary("Every instance-wide setting, its source and its last change")
            .WithDescription(
                "`settings` lists every setting with `name`, `value`, `default` (what applies with "
                + "no row: appsettings.json, else the built-in default), `source` (`row` when a "
                + "person has set it, else `appsettings`), `updatedAt` and `updatedBy` (null "
                + "unless `row`), `description`, and `readOnly`. The FileBrowser roots follow as "
                + "read-only entries named `fileBrowser.roots.<name>` with a `note`: they are a "
                + "deployment setting and the container must also mount the path.");

        app.MapPut("/api/tenant/settings", async (
                JsonElement body, TenantSettings settings, FileBrowserPolicy fileBrowser,
                HttpContext context, CancellationToken ct) =>
            {
                if (body.ValueKind != JsonValueKind.Object)
                {
                    return Results.BadRequest(new { error = "The body must be an object of setting name to value." });
                }

                var changes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in body.EnumerateObject()) changes[property.Name] = property.Value;

                var email = context.User.FindFirstValue(ClaimTypes.Email);
                if (string.IsNullOrWhiteSpace(email))
                {
                    return Results.Json(new { error = "A person has to do this." }, statusCode: StatusCodes.Status403Forbidden);
                }

                try
                {
                    await settings.WriteAsync(
                        changes, context.User.FindFirstValue(ClaimTypes.NameIdentifier), email, ct);
                }
                catch (TenantSettingRejected rejected)
                {
                    return Results.BadRequest(new { error = rejected.Message, field = rejected.Field });
                }

                return Results.Json(Describe(settings, fileBrowser));
            })
            .HumansOnly()
            .WithTags("Tenant")
            .WithSummary("Change instance-wide settings")
            .WithDescription(
                "A PARTIAL map of setting name to value; names not in the body are untouched. Every "
                + "entry is validated before anything is written - integers at least 0 and within the "
                + "setting's bound, durations as `hh:mm:ss` within a bounded range - and a failure "
                + "answers 400 with `error` and `field` naming the setting, writing nothing. Each "
                + "setting that changes is written with a `tenant_events` row naming the setting, the "
                + "old value, the new value and the person. Takes effect without a restart. Answers "
                + "the same shape as the GET.");
    }

    private static JsonObject Describe(TenantSettings settings, FileBrowserPolicy fileBrowser)
    {
        var list = new JsonArray();

        foreach (var definition in settings.Definitions)
        {
            var row = settings.Row(definition.Name);

            list.Add(new JsonObject
            {
                ["name"] = definition.Name,
                ["value"] = settings.ToJson(definition.Name, settings.Current(definition.Name)),
                ["default"] = settings.ToJson(definition.Name, settings.Fallback(definition.Name)),
                ["source"] = row is null ? "appsettings" : "row",
                ["updatedAt"] = row?.UpdatedAt,
                ["updatedBy"] = row?.UpdatedBy,
                ["description"] = settings.DescriptionOf(definition.Name),
                ["readOnly"] = false,
            });
        }

        foreach (var root in fileBrowser.Roots)
        {
            list.Add(new JsonObject
            {
                ["name"] = $"fileBrowser.roots.{root.Name}",
                ["value"] = root.Path,
                ["default"] = root.Path,
                ["source"] = "appsettings",
                ["updatedAt"] = null,
                ["updatedBy"] = null,
                ["description"] =
                    $"File-browser root '{root.Name}'. Listing it means a team may be placed there"
                    + (root.AllowCreate ? "; folders may be created and files uploaded." : "; read-only in the picker."),
                ["readOnly"] = true,
                ["note"] = RootMountNote,
            });
        }

        return new JsonObject { ["settings"] = list };
    }
}
