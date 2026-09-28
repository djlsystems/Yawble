using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Host.Auth;

/// <summary>
/// The installed plugins: read by anyone with Read, re-read from disk, installed from a folder inside
/// the instance (<see cref="PluginInstaller"/>) and their members' settings changed by a person.
/// Nothing here downloads, signs or updates; see <c>docs/plugins.md</c>.
/// </summary>
public static class PluginEndpoints
{
    private const string Area = "Plugins";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/plugins", (PluginCatalog plugins, TeamRegistry teams, HttpContext context) =>
                Results.Ok(Listing(plugins, teams, context)))
            .WithTags(Area)
            .RequirePermit(Permits.Read)
            .WithSummary("The installed plugins")
            .WithDescription(
                "Every plugin installed on this Host that can be hired as a member, by its "
                + "`plugin:<id>` reference: what it is, its configuration fields, the secrets it names "
                + "(never their values), the events it declares, its skills. `refused` lists plugin "
                + "directories that were NOT loaded, each with the sentence saying which field is "
                + "wrong. `versions` lists every version folder with `active` and a `verdict` "
                + "(installed, refused with `reason`, or inactive). Each plugin carries `members` "
                + "(team and member hired on it) for a person, `[]` for a machine caller. No disk "
                + "path is disclosed.");

        app.MapGet("/api/plugins/{id}/{version}/manifest", (
            [Description("The plugin's id.")] string id,
            [Description("One of its version folders.")] string version,
            PluginCatalog plugins) =>
        {
            if (!MemberRef.IsValidPluginId(id) || version.Length == 0 || version is "." or ".."
                || version.Contains('/') || version.Contains('\\'))
            {
                return Results.NotFound(new { error = $"No plugin '{id}' version '{version}'." });
            }

            var manifest = Path.Combine(plugins.Root, id, version, PluginManifest.FileName);

            if (PluginCatalog.Inside(plugins.Root, Path.Combine(id, version, PluginManifest.FileName)) is null || !File.Exists(manifest))
            {
                return Results.NotFound(new { error = $"No plugin '{id}' version '{version}' with a {PluginManifest.FileName}." });
            }

            return Results.Text(File.ReadAllText(manifest), "application/json");
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("A plugin version's manifest")
            .WithDescription(
                "The raw text of `plugin.json` for one version folder - active, inactive or refused - "
                + "unchanged, for reading. 404 with a sentence when there is none.");

        app.MapPost("/api/plugins/install", async (
            InstallPlugin request, PluginInstaller installer, TenantLogging audit, HttpContext context,
            CancellationToken ct) =>
        {
            var result = await installer.InstallAsync(request.Path, request.Replace, ct);

            if (result.Status == StatusCodes.Status200OK)
            {
                await audit.WriteAsync(
                    context, TenantActions.PluginInstalled, result.Id, result.Id,
                    new
                    {
                        id = result.Id,
                        version = result.Version,
                        source = request.Path,
                        replaced = result.Replaced,
                        installed = result.Installed,
                        reason = result.Reason,
                    },
                    ct);

                return Results.Ok(new
                {
                    id = result.Id, version = result.Version, installed = result.Installed,
                    replaced = result.Replaced, reason = result.Reason,
                });
            }

            return Results.Json(
                new
                {
                    error = result.Reason, reason = result.Reason, installed = false,
                    id = result.Id, version = result.Version,
                },
                statusCode: result.Status);
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Install a plugin version from a folder inside the instance")
            .WithDescription(
                "Body `{ \"path\": \"<absolute folder inside the data root>\", \"replace\": false }`: one built "
                + "version of a plugin, holding `plugin.json`. Checked before anything is written - a folder "
                + "outside the data root, one reached through a symlink leaving it, a symlink inside it "
                + "leaving it, a manifest the catalog refuses (400), or an id and version already installed "
                + "without `replace` (409), each answered `{ error, reason, installed: false }`. Then copied to "
                + "`plugins/<id>/<version>/` (Host user, the agent's group, directories 0750, files 0640, "
                + "the manifest's executables 0750), made `active`, the catalog rescanned, and a "
                + "`plugins.installed` tenant row appended. Answers `{ id, version, installed, replaced, "
                + "reason }`: `installed` is the Host's verdict after the rescan.");

        app.MapGet("/api/teams/{team}/members/{member}/plugin-settings", async (
            [Description(Describe.Team)] string team,
            [Description("The member, as addressed in its route.")] string member,
            TeamRegistry teams, PluginCatalog plugins, IPluginMemberSettingsStore settings, CancellationToken ct) =>
        {
            var (found, plugin, refusal) = await PluginMemberAsync(teams, plugins, team, member, ct);
            if (refusal is not null) return refusal;

            var id = new ContainerId(found!.Team, found.Name);
            return Results.Ok(Settings(id, plugin!, await settings.ForAsync(id, ct)));
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("A plugin member's settings")
            .WithDescription(
                "`{ team, member, plugin, version, config, secrets, fields, secretFields }`: what is stored "
                + "for this plugin member - its configuration, and each secret's LOGICAL KEY (never a value) - "
                + "with its plugin's declarations to build the form from. 404 for an unknown member, 400 "
                + "for a member that is not a plugin, 409 when its plugin is not installed.");

        app.MapPut("/api/teams/{team}/members/{member}/plugin-settings", async (
            [Description(Describe.Team)] string team,
            [Description("The member, as addressed in its route.")] string member,
            PluginSettingsChange request, TeamRegistry teams, PluginCatalog plugins,
            IPluginMemberSettingsStore settings, ISecretStore secrets, HttpContext context, CancellationToken ct) =>
        {
            var (found, plugin, refusal) = await PluginMemberAsync(teams, plugins, team, member, ct);
            if (refusal is not null) return refusal;

            var id = new ContainerId(found!.Team, found.Name);
            var changed = new PluginMemberSettings(
                request.Config ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                request.Secrets ?? new Dictionary<string, string>(StringComparer.Ordinal));

            // EXACTLY AS A HIRE: the same check the hire runs, against the active manifest.
            if (PluginMemberRunner.SettingsRefusal(plugin!.Manifest, changed, secrets) is { } invalid)
            {
                return Results.BadRequest(new { error = invalid });
            }

            var before = await settings.ForAsync(id, ct);

            var fields = before.Config.Keys.Union(changed.Config.Keys, StringComparer.Ordinal)
                .Where(name => !(before.Config.TryGetValue(name, out var old) && changed.Config.TryGetValue(name, out var now)
                    && JsonElement.DeepEquals(old, now)))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var secretNames = before.Secrets.Keys.Union(changed.Secrets.Keys, StringComparer.Ordinal)
                .Where(name => before.Secrets.GetValueOrDefault(name) != changed.Secrets.GetValueOrDefault(name))
                .Order(StringComparer.Ordinal)
                .ToArray();

            try
            {
                await settings.SaveAsync(
                    id, changed,
                    new TriggerAudit(
                        context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                        context.User.FindFirstValue(ClaimTypes.Email),
                        TenantActions.MemberPluginSettingsChanged,
                        $"{id.Team}/{id.Name}",
                        found.Label ?? found.Name,
                        JsonSerializer.Serialize(new
                        {
                            team = id.Team,
                            plugin = plugin.Manifest.Id,
                            config = fields,
                            secrets = secretNames,
                        })),
                    ct);
            }
            catch (SqliteException exception)
            {
                return Results.Json(
                    new { error = $"The settings were not saved: their record could not be written ({exception.Message})." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(Settings(id, plugin, changed));
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("Change a plugin member's settings")
            .WithDescription(
                "Body `{ config, secrets }` REPLACES the member's configuration and secret bindings (send "
                + "only the fields that differ from their default). Validated exactly as a hire is - "
                + "field names, types, `enum`, required fields and secrets, the logical key's form and "
                + "that it is set - and refused 400 naming the field, with nothing written. Secrets are "
                + "logical keys only; a value is never taken or answered. Saved with a "
                + "`member.plugin-settings-changed` tenant row in the same transaction, and read by the "
                + "member's next run. Answers as GET does.");

        app.MapPost("/api/plugins/rescan", async (
            PluginCatalog plugins, TeamRegistry teams, TenantLogging audit, HttpContext context, CancellationToken ct) =>
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

            return Results.Ok(Listing(plugins, teams, context));
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

    /// <summary>The member, its active plugin, or the answer saying why there is none.</summary>
    private static async Task<(PersistedMember? Member, InstalledPlugin? Plugin, IResult? Refusal)> PluginMemberAsync(
        TeamRegistry teams, PluginCatalog plugins, string team, string member, CancellationToken ct)
    {
        PersistedMember found;

        try
        {
            if (!ContainerId.IsLegalName(member)) throw new InvalidOperationException();
            found = await teams.MemberAsync(team, member, ct);
        }
        catch (InvalidOperationException)
        {
            return (null, null, Results.NotFound(new { error = $"No member '{member}' in team '{team}'." }));
        }

        if (!MemberRef.IsPlugin(found.Agent, out var id))
        {
            return (found, null, Results.BadRequest(new
            {
                error = $"'{member}' runs {found.Agent}, not a plugin; only a plugin member has settings here.",
            }));
        }

        if (plugins.For(id!) is not { } plugin)
        {
            return (found, null, Results.Conflict(new { error = plugins.RefusalFor(id!) }));
        }

        return (found, plugin, null);
    }

    private static object Settings(ContainerId id, InstalledPlugin plugin, PluginMemberSettings stored) => new
    {
        team = id.Team,
        member = id.Name,
        plugin = plugin.Manifest.Id,
        version = plugin.Manifest.Version,
        config = stored.Config,
        secrets = stored.Secrets,
        fields = Fields(plugin.Manifest),
        secretFields = SecretFields(plugin.Manifest),
    };

    private static Dictionary<string, object> Fields(PluginManifest manifest) => manifest.Config.ToDictionary(
        c => c.Key,
        c => (object)new
        {
            type = c.Value.Type,
            description = c.Value.Description,
            required = c.Value.Required,
            @default = c.Value.Default,
            @enum = c.Value.Enum,
            setBy = c.Value.PersonOnly ? "person" : "anyone",
        });

    private static Dictionary<string, object> SecretFields(PluginManifest manifest) => manifest.Secrets.ToDictionary(
        s => s.Key,
        s => (object)new { description = s.Value.Description, required = s.Value.Required });

    private static object Listing(PluginCatalog plugins, TeamRegistry teams, HttpContext context)
    {
        // WHO IS HIRED ON EACH PLUGIN names members of every team: a person's answer only.
        var person = PrincipalClaims.From(context.User) is not { Kind: not PrincipalKind.User };
        var hired = person
            ? teams.All()
                .SelectMany(team => team.Containers)
                .Where(member => MemberRef.IsPlugin(member.Agent))
                .ToLookup(member => MemberRef.IsPlugin(member.Agent, out var id) ? id! : "", StringComparer.Ordinal)
            : Enumerable.Empty<ContainerSnapshot>().ToLookup(member => "", StringComparer.Ordinal);

        return new
        {
            plugins = plugins.Plugins.Select(p => new
            {
                id = p.Manifest.Id,
                reference = MemberRef.ForPlugin(p.Manifest.Id),
                name = p.Manifest.Name,
                description = p.Manifest.Description,
                version = p.Manifest.Version,
                active = true,
                verdict = "installed",
                protocol = p.Manifest.Protocol,
                timeoutSeconds = p.Manifest.TimeoutSeconds,
                config = Fields(p.Manifest),
                secrets = SecretFields(p.Manifest),
                publishes = p.Manifest.Publishes.Select(e => new { type = $"plugin.{p.Manifest.Id}.{e.Type}", summary = e.Summary }),
                skills = p.Manifest.Skills,
                skill = PluginSkills.SkillOf(p),
                requires = p.Manifest.Requires,
                members = hired[p.Manifest.Id].Select(m => new { team = m.Team, member = m.Id }),
                reserved = p.Manifest.Reserved.Keys,
                ignored = p.Manifest.Ignored,
            }),
            refused = plugins.Refused.Select(r => new { id = r.Id, reason = r.Reason }),
            versions = Versions(plugins),
        };
    }

    /// <summary>Every version folder under the plugins root, with what the Host made of it.</summary>
    private static IEnumerable<object> Versions(PluginCatalog plugins)
    {
        if (!Directory.Exists(plugins.Root)) yield break;

        foreach (var pluginDirectory in Directory.EnumerateDirectories(plugins.Root).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(pluginDirectory);
            if (id.StartsWith('.')) continue;

            var loaded = plugins.For(id);
            var refused = plugins.Refused.FirstOrDefault(r => r.Id == id);
            var activeFile = Path.Combine(pluginDirectory, PluginCatalog.ActiveFile);
            var active = loaded?.Manifest.Version
                ?? (File.Exists(activeFile) ? File.ReadAllText(activeFile).Trim() : null);
            var folders = Directory.EnumerateDirectories(pluginDirectory)
                .Select(Path.GetFileName)
                .Where(v => v is not null && !v.StartsWith('.'))
                .Order(StringComparer.Ordinal)
                .ToList();

            if (folders.Count == 0 && refused is not null)
            {
                yield return new { id, version = (string?)null, name = (string?)null, active = false, verdict = "refused", reason = (string?)refused.Reason };
                continue;
            }

            if (active is null && folders.Count == 1) active = folders[0];

            foreach (var version in folders)
            {
                var isActive = version == active;
                var verdict = isActive && loaded is not null ? "installed" : isActive || refused is not null && active is null ? "refused" : "inactive";

                yield return new
                {
                    id,
                    version,
                    name = NameOf(Path.Combine(pluginDirectory, version!)),
                    active = isActive,
                    verdict,
                    reason = verdict == "refused" ? refused?.Reason : null,
                };
            }
        }
    }

    private static string? NameOf(string versionDirectory)
    {
        try
        {
            var manifest = Path.Combine(versionDirectory, PluginManifest.FileName);
            return File.Exists(manifest) ? PluginManifest.Parse(File.ReadAllText(manifest)).Manifest?.Name : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Body of <c>POST /api/plugins/install</c>.</summary>
public sealed record InstallPlugin(string? Path, bool Replace = false);

/// <summary>Body of <c>PUT .../members/{member}/plugin-settings</c>: both maps replace what is stored.</summary>
public sealed record PluginSettingsChange(
    Dictionary<string, JsonElement>? Config, Dictionary<string, string>? Secrets);
