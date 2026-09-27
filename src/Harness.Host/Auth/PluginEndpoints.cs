using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// The installed plugins: read by anyone with Read, re-read from disk by a person. Nothing here
/// installs, downloads, signs or updates - installing is putting a directory under
/// <see cref="PluginCatalog.Root"/>; see <c>docs/plugins.md</c>.
/// </summary>
public static class PluginEndpoints
{
    private const string Area = "Plugins";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/plugins", (PluginCatalog plugins) => Results.Ok(Describe(plugins)))
            .WithTags(Area)
            .RequirePermit(Permits.Read)
            .WithSummary("The installed plugins")
            .WithDescription(
                "Every plugin installed on this Host that can be hired as a member, by its "
                + "`plugin:<id>` reference: what it is, its configuration fields, the secrets it names "
                + "(never their values), the events it declares, its skills. `refused` lists plugin "
                + "directories that were NOT loaded, each with the sentence saying which field is "
                + "wrong. No disk path is disclosed.");

        app.MapPost("/api/plugins/rescan", async (
            PluginCatalog plugins, TenantLogging audit, HttpContext context, CancellationToken ct) =>
        {
            var scan = plugins.Rescan();
            Report(scan, Console.Out);

            await audit.WriteAsync(
                context, TenantActions.PluginsRescanned, null, null,
                new
                {
                    installed = scan.Plugins.Select(p => $"{p.Manifest.Id}@{p.Manifest.Version}").ToArray(),
                    refused = scan.Refused.Select(r => r.Id).ToArray(),
                },
                ct);

            return Results.Ok(Describe(plugins));
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Re-read the installed plugins")
            .WithDescription(
                "Reads the plugins directory again and REPLACES the catalog, so a plugin installed or "
                + "upgraded on disk is registered without restarting the Host. A member already "
                + "pointing at a plugin runs the new version on its next wake. Answers as "
                + "`GET /api/plugins` does.");
    }

    /// <summary>One line per plugin loaded and per directory refused.</summary>
    public static void Report(PluginScan scan, TextWriter output)
    {
        foreach (var plugin in scan.Plugins)
        {
            output.WriteLine(
                $"Plugin {plugin.Manifest.Id} {plugin.Manifest.Version} installed"
                + (plugin.Manifest.Ignored.Count > 0
                    ? $" (ignored unknown keys: {string.Join(", ", plugin.Manifest.Ignored)})"
                    : "")
                + ".");
        }

        foreach (var refused in scan.Refused)
        {
            output.WriteLine($"Plugin directory '{refused.Id}' was not loaded: {refused.Reason}");
        }
    }

    private static object Describe(PluginCatalog plugins) => new
    {
        plugins = plugins.Plugins.Select(p => new
        {
            id = p.Manifest.Id,
            reference = MemberRef.ForPlugin(p.Manifest.Id),
            name = p.Manifest.Name,
            description = p.Manifest.Description,
            version = p.Manifest.Version,
            protocol = p.Manifest.Protocol,
            timeoutSeconds = p.Manifest.TimeoutSeconds,
            config = p.Manifest.Config.ToDictionary(
                c => c.Key,
                c => new { type = c.Value.Type, description = c.Value.Description, required = c.Value.Required, @default = c.Value.Default, @enum = c.Value.Enum }),
            secrets = p.Manifest.Secrets.ToDictionary(
                s => s.Key,
                s => new { description = s.Value.Description, required = s.Value.Required }),
            publishes = p.Manifest.Publishes.Select(e => new { type = $"plugin.{p.Manifest.Id}.{e.Type}", summary = e.Summary }),
            skills = p.Manifest.Skills,
            reserved = p.Manifest.Reserved.Keys,
            ignored = p.Manifest.Ignored,
        }),
        refused = plugins.Refused.Select(r => new { id = r.Id, reason = r.Reason }),
    };
}
