using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Harness.Contracts;
using System.Data.Common;

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
        app.MapGet("/api/plugins", async (PluginCatalog plugins, TeamRegistry teams, Marketplace marketplace, ITeamSolutionStore solutions,
                HttpContext context, CancellationToken ct) =>
                Results.Ok(Listing(plugins, teams, marketplace, await solutions.ListAsync(ct), context)))
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
                + "(team and member hired on it) for a person, `[]` for a machine caller, and "
                + "`marketplaceUrl`: the plugin's page on the package catalog's origin when the catalog "
                + "lists it, null otherwise. Each also carries "
                + "`solutions`: each installed solution team whose package ships it (`team`, `teamName`, "
                + "`packageId`, `package`, `version`) with the oldest version of it that team needs "
                + "(`minimum`) - a version older than that is refused at install - for a person, `[]` "
                + "for a machine caller. No disk path is disclosed.");

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

        app.MapDelete("/api/plugins/{id}", async (
            [Description("The plugin's id.")] string id,
            [Description("Only this version; the whole plugin when omitted.")] string? version,
            PluginRemover remover, HttpContext context, CancellationToken ct) =>
        {
            var result = await remover.RemoveAsync(
                id, version, TenantLogging.Row(context, TenantActions.PluginRemoved, id, id, null), ct);

            var members = result.Members.Select(m => new { team = m.Team, member = m.Member, teamName = m.TeamName, memberName = m.MemberName });

            return result.Status == StatusCodes.Status200OK
                ? Results.Ok(new { id = result.Id, version = result.Version, whole = result.Whole, versions = result.Versions })
                : Results.Json(
                    new { error = result.Reason, reason = result.Reason, id = result.Id, version = result.Version, members },
                    statusCode: result.Status);
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Remove a plugin, or one version of it")
            .WithDescription(
                "The rules of `plugin remove`. Without `version`, or when it names the only version, the "
                + "whole plugin goes, and that is REFUSED 409 while any member is hired on it, naming each "
                + "(`members`: team, member and their names): remove those members first. With `version`, "
                + "that kept version goes; the active version is refused 409 while others are kept. 404 for "
                + "a plugin or version that is not there, 400 for a malformed id or version. On success the "
                + "folder is gone, a `plugins.removed` tenant row is appended in the same transaction, and the "
                + "catalog is rescanned. Answers `{ id, version, whole, versions }`. Deleting a team never "
                + "removes a plugin.");

        app.MapGet("/api/teams/{team}/members/{member}/plugin-settings", async (
            [Description(Describe.Team)] string team,
            [Description("The member, as addressed in its route.")] string member,
            TeamRegistry teams, PluginCatalog plugins, IPluginMemberSettingsStore settings, MemberSecretReads reads,
            CancellationToken ct) =>
        {
            var (found, plugin, refusal) = await PluginMemberAsync(teams, plugins, team, member, ct);
            if (refusal is not null) return refusal;

            var id = new ContainerId(found!.Team, found.Name);
            return Results.Ok(await SettingsAsync(id, plugin!, await settings.ForAsync(id, ct), reads, ct));
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("A plugin member's settings")
            .WithDescription(
                "`{ team, member, plugin, version, description, config, secrets, secretStates, fields, secretFields, "
                + "namedSecrets, namedSecretStates, outOfRange, marketplaceUrl }`: `namedSecrets` is the manifest's "
                + "`namedSecrets` block (`label`, `description`, `max`) or null, and `namedSecretStates` each named secret the "
                + "member holds (`name`, `setBy`, `setAt`; never a value). What is stored for this plugin member - its configuration, each secret's bound "
                + "LOGICAL KEY if any (`secrets`), and `secretStates`, each manifest secret field as "
                + "`GET .../secrets` answers it (whether it is set, by whom, from which source; a value only for a "
                + "field the manifest declares `masked: false`) - with its plugin's declarations to build the form "
                + "from (each with its `label`, and a secret's `masked`), the plugin's `description` (its manifest's, "
                + "whole; null when it has none), `marketplaceUrl` (the plugin's page on the "
                + "package catalog's origin when the catalog lists it, else null), and `outOfRange` - each stored number "
                + "outside its field's bounds, by field, with the sentence a write of it is refused with; never "
                + "rewritten, `{}` when none. 404 for an unknown member, 400 "
                + "for a member that is not a plugin, 409 when its plugin is not installed.");

        app.MapPut("/api/teams/{team}/members/{member}/plugin-settings", async (
            [Description(Describe.Team)] string team,
            [Description("The member, as addressed in its route.")] string member,
            PluginSettingsChange request, TeamRegistry teams, PluginCatalog plugins,
            IPluginMemberSettingsStore settings, ISecretStore secrets, PluginMemberSecretStore memberSecrets, MemberSecretReads reads,
            Connections connections, IMessageLog log, SignInProfileCleanup signInProfiles, HttpContext context,
            CancellationToken ct) =>
        {
            var (found, plugin, refusal) = await PluginMemberAsync(teams, plugins, team, member, ct);
            if (refusal is not null) return refusal;

            var id = new ContainerId(found!.Team, found.Name);

            // ONE WRITER OF THE MEMBER'S TABLES AT A TIME: a plugin's row.status waits for this save.
            await using var tableWrite = await PluginTableWrites.LockAsync(id, ct);

            var before = await settings.ForAsync(id, ct);
            var sent = new Dictionary<string, JsonElement>(request.Config ?? [], StringComparer.Ordinal);

            // A TABLE'S READ-ONLY CELLS ARE THE PLUGIN'S: whatever the body says, each row keeps what is stored.
            foreach (var (name, field) in plugin!.Manifest.Config)
            {
                if (field.Table is { } table && sent.TryGetValue(name, out var rows))
                {
                    sent[name] = table.Merge(rows, before.Config.TryGetValue(name, out var kept) ? kept : null);
                }
            }

            var changed = new PluginMemberSettings(
                sent,

                // OMITTED KEEPS what is bound, as for connections: the settings screen shows no key
                // name, so its Save sends none and unbinds nothing; `{}` unbinds every key.
                request.Secrets ?? before.Secrets)
            {
                // OMITTED KEEPS what is bound, so a form that knows nothing of connections cannot unbind
                // them by saving; `{}` unbinds every slot.
                Connections = request.Connections ?? before.Connections,
            };

            // THE MEMBER'S OWN VALUES: a string sets or replaces, null clears, a field not named stays.
            var values = request.SecretValues ?? new Dictionary<string, string?>(StringComparer.Ordinal);
            if (PluginMemberSecretStore.ChangesRefusal(plugin.Manifest, values) is { } valueRefusal)
            {
                return Results.BadRequest(new { error = valueRefusal });
            }

            // THE MEMBER'S NAMED SECRETS: a value sets or replaces, null removes, a name not sent stays.
            var named = request.NamedSecretValues ?? new Dictionary<string, string?>(StringComparer.Ordinal);
            if (named.Count > 0 && await NamedRefusalAsync(memberSecrets, id, plugin.Manifest, named, ct) is { } namedRefusal)
            {
                return Results.BadRequest(new { error = namedRefusal });
            }

            var valued = await ValuedAfterAsync(memberSecrets, id, values, ct);

            // EXACTLY AS A HIRE: the same check the hire runs, against the active manifest - except
            // that a value already stored, sent back unchanged, is kept even outside its bounds.
            if (PluginMemberRunner.SettingsRefusal(plugin.Manifest, changed, secrets, stored: before, valued: valued) is { } invalid)
            {
                return Results.BadRequest(new { error = invalid });
            }

            // A TABLE'S SECRET CELLS: a value sets or replaces, null clears, a cell not sent stays; a row
            // the save removes takes its cells with it. Checked against the rows as saved, so a row added
            // in this save takes its secrets in the same save.
            var cells = new List<TableCellChange>();
            var removedRows = new List<(string Table, string Row)>();
            foreach (var (table, rowValues) in request.TableSecretValues ?? [])
            {
                foreach (var (row, columnValues) in rowValues ?? [])
                {
                    foreach (var (column, value) in columnValues ?? [])
                    {
                        if (PluginMemberSecretStore.CellRefusal(plugin.Manifest, changed, table, row, column, value) is { } cellRefusal)
                        {
                            return Results.BadRequest(new { error = cellRefusal });
                        }

                        cells.Add(new TableCellChange(table, row, column, value));
                    }
                }
            }

            foreach (var (name, field) in plugin.Manifest.Config)
            {
                if (field.Table is not { } table) continue;

                var removed = table.Removed(
                    before.Config.TryGetValue(name, out var was) ? was : null, changed.Config.TryGetValue(name, out var now) ? now : null);
                foreach (var row in removed)
                {
                    removedRows.Add((name, row));
                    cells.AddRange(table.SecretColumns.Select(column => new TableCellChange(name, row, column.Name, null)));
                }
            }

            // A PERSON BINDS: this route is HumansOnly, so any connection may be named - but it must
            // exist, suit the slot's providers and hold every scope the slot asks for.
            if (await connections.BindingRefusalAsync(plugin.Manifest, changed.Connections, assignedToTeam: null, ct) is { } bindingRefusal)
            {
                return Results.BadRequest(bindingRefusal.Body());
            }

            // A PERSON'S BINDING IS ALSO AN ASSIGNMENT: a connection not yet assigned to the team (New
            // connection... from this slot) is assigned with the binding, its own row in the same transaction.
            var assigning = await connections.NotYetAssignedAsync(id.Team, changed.Connections.Values, ct);

            try
            {
                IReadOnlyList<TriggerAudit> rows =
                [
                    .. PersonChangeRows(
                        context.User.FindFirstValue(ClaimTypes.NameIdentifier), context.User.FindFirstValue(ClaimTypes.Email),
                        id, found.Label ?? found.Name, plugin.Manifest.Id, before, changed),
                    .. assigning.Select(c => new TriggerAudit(
                        context.User.FindFirstValue(ClaimTypes.NameIdentifier), context.User.FindFirstValue(ClaimTypes.Email),
                        TenantActions.TeamConnectionAssigned, c.Id, c.Name,
                        JsonSerializer.Serialize(Connections.AssignedDetail(id.Team, c, "member binding")))),
                ];

                // The settings, their rows and every value with its own row land in ONE transaction.
                if (cells.Count > 0)
                {
                    await memberSecrets.SaveWithSettingsAsync(
                        id, found.Label ?? found.Name, plugin.Manifest.Id, changed, rows, values, named, cells, SecretActor(context), ct);
                }
                else if (values.Count > 0 || named.Count > 0)
                {
                    await memberSecrets.SaveWithSettingsAsync(
                        id, found.Label ?? found.Name, plugin.Manifest.Id, changed, rows, values, named, SecretActor(context), ct);
                }
                else
                {
                    await settings.SaveAsync(id, changed, rows, ct);
                }
            }
            catch (DbException exception)
            {
                return Results.Json(
                    new { error = $"The settings were not saved: their record could not be written ({exception.Message})." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // A REMOVED ROW'S SIGN-IN BROWSER PROFILE goes with it, on every worker.
            foreach (var (table, row) in removedRows) await signInProfiles.ForgetRowAsync(id, table, row, ct);

            // A PERSON'S SAVE THAT LEAVES NOTHING MISSING tells the Manager in every workflow whose run
            // of this member failed needs-person. Best effort: the save stands whatever the notice does.
            await RefreshSetupAsync(context, id, ct);
            await NoticeIfReadyAsync(log, id, found.Label ?? found.Name, plugin.Manifest, changed, secrets, valued, ct);

            // A PERSON'S BINDING MAY HAVE ASSIGNED the team its first connection: Team Connections shows.
            await teams.RefreshConnectionPresenceAsync(CancellationToken.None);

            return Results.Ok(await SettingsAsync(id, plugin, changed, reads, ct));
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("Change a plugin member's settings")
            .WithDescription(
                "Body `{ config, secrets?, secretValues?, namedSecretValues?, tableSecretValues?, connections? }` REPLACES the member's configuration, its "
                + "secret key bindings when `secrets` is given (omitted keeps them; `{}` unbinds every key), and its "
                + "connection bindings (slot to connection id) when `connections` is given "
                + "(omitted keeps them; `{}` unbinds every slot). `secretValues` sets the member's OWN value for each "
                + "secret field named (null clears it; a field not named is left), checked as "
                + "`PUT .../secrets/{field}` checks it, each with its `member.secret-*` row in the same transaction "
                + "as the settings. `namedSecretValues` sets or replaces each NAMED secret given (null removes it; a "
                + "name not given is left), for a plugin whose manifest declares `namedSecrets`, checked as "
                + "`PUT .../named-secrets/{name}` checks it, each with its `member.named-secret-*` row in the same "
                + "transaction. A `table` field's value is its rows (every cell but a secret one); a read-only cell "
                + "sent is ignored and the stored one kept. `tableSecretValues` (`{ <table>: { <row>: { <column>: value } } }`) "
                + "sets or replaces each secret cell given (null clears it; a cell not given is left), each with its "
                + "`member.secret-*` row naming the table, row and column, in the same transaction; a row the save "
                + "removes clears its cells. A secret cell is never in `config` and never answered. A binding must name a connection that exists, "
                + "of a provider the slot takes, granted every scope the slot asks for - refused 400 otherwise, "
                + "with `reconnect: { connectionId, scopes }` when only scopes are missing; a change appends "
                + "`member.connections-changed` in the same transaction. (send "
                + "only the fields that differ from their default). Validated exactly as a hire is - "
                + "field names, types, `enum`, a number's `min`/`max`/`integer`, required fields and secrets, "
                + "the logical key's form and that it is set - and refused 400 naming the field (and the bound), "
                + "with nothing written. A value already stored outside bounds added later may be sent back "
                + "unchanged: it is kept, and `outOfRange` names it. A masked value is never answered. Saved with a "
                + "`member.plugin-settings-changed` tenant row in the same transaction, and read by the "
                + "member's next run. Answers as GET does.");

        // EACH PLUGIN MEMBER OWNS ITS SECRETS: whether each of its manifest's secret fields is set, by
        // whom, from which source - and, for a field the manifest declares `masked: false`, its value.
        app.MapGet("/api/teams/{team}/members/{member}/secrets", async (
            [Description(Describe.Team)] string team,
            [Description("The member, as addressed in its route.")] string member,
            TeamRegistry teams, PluginCatalog plugins, IPluginMemberSettingsStore settings, MemberSecretReads reads,
            CancellationToken ct) =>
        {
            var (found, plugin, refusal) = await PluginMemberAsync(teams, plugins, team, member, ct);
            if (refusal is not null) return refusal;

            var id = new ContainerId(found!.Team, found.Name);
            return Results.Ok(new
            {
                team = id.Team,
                member = id.Name,
                secrets = await reads.StatesAsync(id, plugin!.Manifest, await settings.ForAsync(id, ct), ct),
            });
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("A plugin member's secret fields: whether each is set, by whom and from where")
            .WithDescription(
                "`{ team, member, secrets: [{ field, label, masked, required, needed, set, setBy, setAt, source, key, "
                + "value? }] }`, one per manifest secret field in the manifest's order. `source` is `member` (the "
                + "member's own value, typed by a person), `key` (the bound key, stored from the screen), `environment` "
                + "(the bound key, from the Host's environment) or null; the member's own value wins. `key` is the bound "
                + "key's name or null. `value` is answered only for a field the manifest declares `masked: false`; a "
                + "masked value is never answered. 404 for an unknown member, 400 for a member that is not a plugin, "
                + "409 when its plugin is not installed.");

        app.MapPut("/api/teams/{team}/members/{member}/secrets/{field}", async (
            [Description(Describe.Team)] string team,
            [Description("The member, as addressed in its route.")] string member,
            [Description("A secret field the member's plugin manifest declares.")] string field,
            SecretSubmission body, TeamRegistry teams, PluginCatalog plugins, IPluginMemberSettingsStore settings,
            PluginMemberSecretStore memberSecrets, MemberSecretReads reads, ISecretStore secrets, IMessageLog log,
            HttpContext context, CancellationToken ct) =>
        {
            var (found, plugin, refusal) = await PluginMemberAsync(teams, plugins, team, member, ct);
            if (refusal is not null) return refusal;

            var id = new ContainerId(found!.Team, found.Name);
            if (PluginMemberSecretStore.ChangesRefusal(plugin!.Manifest, new Dictionary<string, string?> { [field] = body.Value ?? "" }) is { } invalid)
            {
                return Results.BadRequest(new { error = invalid });
            }

            try
            {
                await memberSecrets.SetAsync(id, found.Label ?? found.Name, plugin.Manifest.Id, field, body.Value!, SecretActor(context), ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(new { error = UnrecordedField(field, exception) }, statusCode: StatusCodes.Status500InternalServerError);
            }

            var bound = await settings.ForAsync(id, ct);
            await RefreshSetupAsync(context, id, ct);
            await NoticeIfReadyAsync(
                log, id, found.Label ?? found.Name, plugin.Manifest, bound, secrets,
                (await memberSecrets.StatesAsync(id, ct)).Keys.ToHashSet(StringComparer.Ordinal), ct);

            return Results.Ok(await reads.StateAsync(id, plugin.Manifest, bound, field, ct));
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("Set or replace a plugin member's own value for one secret field")
            .WithDescription(
                "Body `{ \"value\": \"...\" }`: kept as THIS member's own value for the field, as Data Protection "
                + "ciphertext, with a `member.secret-set` or `member.secret-replaced` tenant row (team, member, plugin, "
                + "field - never the value) in the same transaction. No other member, of this plugin or another, on "
                + "this team or another, reads or changes it. Read by the member's next run before any key binding. "
                + "Answers the field's entry as `GET .../secrets` does. 400 with `error` for a field the manifest does "
                + "not declare, or a value that is empty, multi-line, over 8192 characters, or - for a masked field - "
                + "padded with white space or under 4 characters; the sentence never repeats the value. 500, with "
                + "nothing stored, when the row cannot be written. A save that leaves nothing missing tells the "
                + "Manager the member is ready.");

        app.MapDelete("/api/teams/{team}/members/{member}/secrets/{field}", async (
            [Description(Describe.Team)] string team,
            [Description("The member, as addressed in its route.")] string member,
            [Description("A secret field the member's plugin manifest declares.")] string field,
            TeamRegistry teams, PluginCatalog plugins, IPluginMemberSettingsStore settings,
            PluginMemberSecretStore memberSecrets, MemberSecretReads reads, HttpContext context, CancellationToken ct) =>
        {
            var (found, plugin, refusal) = await PluginMemberAsync(teams, plugins, team, member, ct);
            if (refusal is not null) return refusal;

            var id = new ContainerId(found!.Team, found.Name);
            if (PluginMemberSecretStore.ChangesRefusal(plugin!.Manifest, new Dictionary<string, string?> { [field] = null }) is { } invalid)
            {
                return Results.BadRequest(new { error = invalid });
            }

            var bound = await settings.ForAsync(id, ct);

            try
            {
                await memberSecrets.ClearAsync(id, found.Label ?? found.Name, plugin.Manifest.Id, field, SecretActor(context), ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var kept = await reads.StateAsync(id, plugin.Manifest, bound, field, ct);
                kept["error"] = UnrecordedField(field, exception);
                return Results.Json(kept, statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(await reads.StateAsync(id, plugin.Manifest, bound, field, ct));
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("Clear a plugin member's own value for one secret field")
            .WithDescription(
                "Removes the member's own value with a `member.secret-cleared` row in the same transaction; the field "
                + "then resolves through its bound key, if any (stored, then the Host's environment). Nothing stored "
                + "answers the same and writes no row. Answers the field's entry as `GET .../secrets` does. 400 for a "
                + "field the manifest does not declare; 500, with nothing cleared, when the row cannot be written.");

        // THE NAMES, NEVER A VALUE, OF EVERY PLUGIN MEMBER'S NAMED SECRETS ON A TEAM, for whoever reads
        // the team's plugin members - the Manager's and the Concierge's `status` - so an agent can say
        // "add a secret named X" and check it is there. A name is not a secret; the values stay behind
        // the HumansOnly routes below.
        app.MapGet("/api/teams/{team}/named-secret-names", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, PluginCatalog plugins, PluginMemberSecretStore memberSecrets, CancellationToken ct) =>
        {
            if (teams.All().FirstOrDefault(t => string.Equals(t.Id, team, StringComparison.OrdinalIgnoreCase)) is not { } found)
            {
                return Refusals.NoTeam(team);
            }

            var members = new List<object>();
            foreach (var member in found.Containers.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                if (!MemberRef.IsPlugin(member.Agent, out var pluginId) || plugins.For(pluginId!)?.Manifest.NamedSecrets is null) continue;

                var names = await memberSecrets.NamedStatesAsync(new ContainerId(member.Team, member.Id), ct);
                members.Add(new { member = member.Id, name = member.Name, plugin = pluginId, names = names.Select(n => n.Name) });
            }

            return Results.Ok(new { team = found.Id, members });
        })
            .WithTags("Members")
            .RequirePermit(Permits.Read)
            .WithSummary("The names of each plugin member's named secrets on a team, never a value")
            .WithDescription(
                "`{ team, members: [{ member, name, plugin, names }] }`: each member of the team whose plugin declares "
                + "a `namedSecrets` block, with the names of the secrets a person gave it, in name order. Never a "
                + "value, and never who set one. 404 for an unknown team.");

        // A PLUGIN MEMBER'S NAMED SECRETS: each name a person gave a secret, who set it and when -
        // never a value - for a plugin whose manifest declares a `namedSecrets` block.
        app.MapGet("/api/teams/{team}/members/{member}/named-secrets", async (
            [Description(Describe.Team)] string team,
            [Description("The member, as addressed in its route.")] string member,
            TeamRegistry teams, PluginCatalog plugins, PluginMemberSecretStore memberSecrets, CancellationToken ct) =>
        {
            var (found, manifest, refusal) = await NamedSecretsMemberAsync(teams, plugins, team, member, ct);
            if (refusal is not null) return refusal;

            return Results.Ok(await NamedSecretsAnswerAsync(new ContainerId(found!.Team, found.Name), manifest!.NamedSecrets!, memberSecrets, ct));
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("A plugin member's named secrets: each name, who set it and when")
            .WithDescription(
                "`{ team, member, label, description, max, namedSecrets: [{ name, setBy, setAt }] }`, in name order: "
                + "the manifest's `namedSecrets` block and each secret a person named on this member. Never a value. "
                + "404 for an unknown member, 400 for a member that is not a plugin or whose plugin declares no "
                + "`namedSecrets` block, 409 when its plugin is not installed.");

        app.MapPut("/api/teams/{team}/members/{member}/named-secrets/{name}", async (
            [Description(Describe.Team)] string team,
            [Description("The member, as addressed in its route.")] string member,
            [Description("The secret's name: a letter, then letters, digits and underscores, at most 64.")] string name,
            SecretSubmission body, TeamRegistry teams, PluginCatalog plugins, PluginMemberSecretStore memberSecrets,
            HttpContext context, CancellationToken ct) =>
        {
            var (found, manifest, refusal) = await NamedSecretsMemberAsync(teams, plugins, team, member, ct);
            if (refusal is not null) return refusal;

            var id = new ContainerId(found!.Team, found.Name);
            var change = new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = body.Value ?? "" };
            if (await NamedRefusalAsync(memberSecrets, id, manifest!, change, ct) is { } invalid)
            {
                return Results.BadRequest(new { error = invalid });
            }

            try
            {
                await memberSecrets.ChangeNamedAsync(id, found.Label ?? found.Name, manifest!.Id, change, SecretActor(context), ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(new { error = UnrecordedName(name, exception) }, statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(await NamedSecretsAnswerAsync(id, manifest.NamedSecrets!, memberSecrets, ct));
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("Set or replace one of a plugin member's named secrets")
            .WithDescription(
                "Body `{ \"value\": \"...\" }`: kept as THIS member's secret under `{name}`, as Data Protection "
                + "ciphertext, with a `member.named-secret-set` or `member.named-secret-replaced` tenant row (team, member, "
                + "plugin, name - never the value) in the same transaction. Read by the member's next run as "
                + "`namedSecrets` on stdin. Answers as `GET .../named-secrets` does. 400 with `error` naming the name, "
                + "never the value: a name that is not a letter then letters, digits and underscores (at most 64), one "
                + "the member holds in another case, one equal to a manifest `secrets` field, one past the block's `max`, "
                + "or a value that is empty, multi-line, over 8192 characters, padded with white space, under 4 "
                + "characters or equal to a name the plugin is told. 500, with nothing stored, when the row cannot be written.");

        app.MapDelete("/api/teams/{team}/members/{member}/named-secrets/{name}", async (
            [Description(Describe.Team)] string team,
            [Description("The member, as addressed in its route.")] string member,
            [Description("The secret's name, in any case.")] string name,
            TeamRegistry teams, PluginCatalog plugins, PluginMemberSecretStore memberSecrets,
            HttpContext context, CancellationToken ct) =>
        {
            var (found, manifest, refusal) = await NamedSecretsMemberAsync(teams, plugins, team, member, ct);
            if (refusal is not null) return refusal;

            var id = new ContainerId(found!.Team, found.Name);
            var change = new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = null };
            if (await NamedRefusalAsync(memberSecrets, id, manifest!, change, ct) is { } invalid)
            {
                return Results.BadRequest(new { error = invalid });
            }

            try
            {
                await memberSecrets.ChangeNamedAsync(id, found.Label ?? found.Name, manifest!.Id, change, SecretActor(context), ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(new { error = UnrecordedName(name, exception) }, statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(await NamedSecretsAnswerAsync(id, manifest.NamedSecrets!, memberSecrets, ct));
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("Remove one of a plugin member's named secrets")
            .WithDescription(
                "Removes the member's secret named `{name}` (matched ignoring case) with a `member.named-secret-removed` "
                + "row in the same transaction. A name not held answers the same and writes no row. Answers as "
                + "`GET .../named-secrets` does. 400 for a name that is not usable; 500, with nothing removed, when the "
                + "row cannot be written.");

        // WHETHER A KEY IS SET, by its name only - what a solution's preview says of each secret it
        // binds (`set`, `setWith`), for the key a person types at a hire - and who set it from the
        // screen, when, and which source holds it. The value never leaves the store; a key a plugin
        // may never be pointed at answers its refusal and no `set`.
        app.MapGet("/api/secrets/{key}", (
            [Description("The logical key, as in ACME_STORAGE_KEY.")] string key,
            PluginSecretStore secrets) => Results.Ok(SecretAnswer(key, secrets)))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Whether a secret key is set on this Host, by whom and from which source")
            .WithDescription(
                "`{ key, set, refusal, setWith, setBy, setAt, source }` for one logical key: `set` is whether "
                + "the Host has a value for it, asked by name - the value is never read into an answer. "
                + "`source` is `stored` (set from the screen; `setBy` and `setAt` say who and when), "
                + "`environment` (the Host's environment, where the operator CLI's `secret set` arrives; "
                + "`setBy` and `setAt` null) or null when not set. A stored key wins over the environment. "
                + "`refusal` is the sentence a binding of it is refused with (not a key's form, the "
                + "platform's own, a model provider's credential), with `set` false; null otherwise. "
                + "`setWith` is how a person sets it: the screen first, the operator CLI second.");

        // A KEY SET FROM THE SCREEN, by a person only: stored as Data Protection ciphertext with its
        // tenant row in the same transaction, read by the next run of every member binding it.
        app.MapPut("/api/secrets/{key}", async (
            [Description("The logical key, as in ACME_STORAGE_KEY.")] string key,
            SecretSubmission body, PluginSecretStore secrets, TeamRegistry teams, PluginCatalog plugins,
            IPluginMemberSettingsStore settings, PluginMemberSecretStore memberSecrets, IMessageLog log, HttpContext context, CancellationToken ct) =>
            {
                if (EnvironmentSecretStore.Refusal(key) is { } refusal)
                {
                    return Results.Json(SecretAnswer(key, secrets, refusal), statusCode: StatusCodes.Status400BadRequest);
                }

                if (PluginSecretStore.ValueRefusal(body.Value) is { } refused)
                {
                    return Results.Json(SecretAnswer(key, secrets, refused), statusCode: StatusCodes.Status400BadRequest);
                }

                try
                {
                    await secrets.SetAsync(key, body.Value!, SecretActor(context), ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return Results.Json(SecretAnswer(key, secrets, Unrecorded(key, exception)), statusCode: StatusCodes.Status500InternalServerError);
                }

                // EVERY PLUGIN MEMBER BOUND TO THIS KEY that is ready now tells its Manager, as a save does.
                foreach (var member in teams.All().SelectMany(t => t.Containers))
                {
                    if (!MemberRef.IsPlugin(member.Agent, out var pluginId) || plugins.For(pluginId) is not { } plugin) continue;
                    var id = new ContainerId(member.Team, member.Id);
                    var bound = await settings.ForAsync(id, ct);
                    if (!bound.Secrets.Values.Contains(key, StringComparer.Ordinal)) continue;
                    await RefreshSetupAsync(context, id, ct);
                    await NoticeIfReadyAsync(
                        log, id, member.Name, plugin.Manifest, bound, secrets,
                        (await memberSecrets.StatesAsync(id, ct)).Keys.ToHashSet(StringComparer.Ordinal), ct);
                }

                return Results.Ok(SecretAnswer(key, secrets));
            })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Set or replace a secret key from the screen")
            .WithDescription(
                "Stores `{ \"value\": \"...\" }` for the logical key as Data Protection ciphertext, with a "
                + "`secrets.set` or `secrets.replaced` tenant row naming the key (never the value) in the same "
                + "transaction. Every plugin member binding the key reads it from its next run, with no restart; "
                + "it wins over the Host's environment. Answers as GET does. 400 with `error` (and `refusal` for "
                + "the key) for a key a plugin may not be pointed at, or a value that is empty, multi-line, padded "
                + "with white space, under 4 or over 8192 characters; 500, with nothing stored, when the tenant "
                + "row cannot be written. No answer carries the value.");

        app.MapDelete("/api/secrets/{key}", async (
            [Description("The logical key, as in ACME_STORAGE_KEY.")] string key,
            PluginSecretStore secrets, HttpContext context, CancellationToken ct) =>
            {
                if (EnvironmentSecretStore.Refusal(key) is { } refusal)
                {
                    return Results.Json(SecretAnswer(key, secrets, refusal), statusCode: StatusCodes.Status400BadRequest);
                }

                try
                {
                    await secrets.ClearAsync(key, SecretActor(context), ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return Results.Json(SecretAnswer(key, secrets, Unrecorded(key, exception)), statusCode: StatusCodes.Status500InternalServerError);
                }

                return Results.Ok(SecretAnswer(key, secrets));
            })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Clear a secret key set from the screen")
            .WithDescription(
                "Removes the stored key with a `secrets.cleared` tenant row in the same transaction; a run then "
                + "reads the Host's environment for it, so the answer's `source` is `environment` when the Host "
                + "has it there. Nothing stored answers the same and writes no row. Answers as GET does. 400 "
                + "for a refused key; 500, with nothing cleared, when the row cannot be written.");

        app.MapPost("/api/plugins/rescan", async (
            PluginCatalog plugins, TeamRegistry teams, Marketplace marketplace, ITeamSolutionStore solutions, TenantLogging audit,
            HttpContext context, CancellationToken ct) =>
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

            return Results.Ok(Listing(plugins, teams, marketplace, await solutions.ListAsync(ct), context));
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

    /// <summary>The <c>member.connections-changed</c> row's detail: each slot's connection before and
    /// after, by id - never a token - and who may set a binding: always a person.</summary>
    /// <summary>
    /// THE ROWS A PERSON'S SETTINGS CHANGE IS SAVED WITH, in the same transaction: a
    /// <c>member.connections-changed</c> row when a slot's binding changed, and always the
    /// <c>member.plugin-settings-changed</c> row naming the changed config fields, secret fields and
    /// slots - names only, never a value. The settings route and a solution update share it, so a
    /// package's binding is recorded exactly as a person's is.
    /// </summary>
    public static IReadOnlyList<TriggerAudit> PersonChangeRows(
        string? userId, string? email, ContainerId id, string label, string plugin, PluginMemberSettings before, PluginMemberSettings changed)
    {
        var fields = before.Config.Keys.Union(changed.Config.Keys, StringComparer.Ordinal)
            .Where(name => !(before.Config.TryGetValue(name, out var old) && changed.Config.TryGetValue(name, out var now)
                && JsonElement.DeepEquals(old, now)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var secretNames = before.Secrets.Keys.Union(changed.Secrets.Keys, StringComparer.Ordinal)
            .Where(name => before.Secrets.GetValueOrDefault(name) != changed.Secrets.GetValueOrDefault(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var slots = before.Connections.Keys.Union(changed.Connections.Keys, StringComparer.Ordinal)
            .Where(slot => before.Connections.GetValueOrDefault(slot) != changed.Connections.GetValueOrDefault(slot))
            .Order(StringComparer.Ordinal)
            .ToArray();

        List<TriggerAudit> rows = [];

        if (slots.Length > 0)
        {
            rows.Add(new TriggerAudit(
                userId, email, TenantActions.MemberConnectionsChanged, $"{id.Team}/{id.Name}", label,
                JsonSerializer.Serialize(ConnectionsChangedDetail(id, plugin, slots, before, changed))));
        }

        rows.Add(new TriggerAudit(
            userId, email, TenantActions.MemberPluginSettingsChanged, $"{id.Team}/{id.Name}", label,
            JsonSerializer.Serialize(new { team = id.Team, plugin, config = fields, secrets = secretNames, connections = slots })));

        return rows;
    }

    public static object ConnectionsChangedDetail(
        ContainerId id, string plugin, IEnumerable<string> slots, PluginMemberSettings before, PluginMemberSettings after) => new
    {
        team = id.Team,
        plugin,
        setBy = "person",
        slots = slots.Select(slot => new
        {
            slot,
            from = before.Connections.GetValueOrDefault(slot),
            to = after.Connections.GetValueOrDefault(slot),
        }),
    };

    /// <summary>The member, its active plugin, or the answer saying why there is none.</summary>
    /// <summary>
    /// Tells the Manager the member is ready (<see cref="PluginReadyNotice"/>) when nothing a person
    /// sets is missing any more. A notice that cannot be written is logged by the log and never
    /// undoes the person's save.
    /// </summary>
    /// <summary>The member's Needs you mark read again from what was just saved (<see cref="PluginSetup"/>).</summary>
    private static Task RefreshSetupAsync(HttpContext context, ContainerId id, CancellationToken ct) =>
        context.RequestServices.GetService<PluginSetup>() is { } setup ? setup.RefreshAsync(id, ct) : Task.CompletedTask;

    private static async Task NoticeIfReadyAsync(
        IMessageLog log, ContainerId id, string name, PluginManifest manifest, PluginMemberSettings bound, ISecretStore secrets,
        IReadOnlySet<string>? valued, CancellationToken ct)
    {
        if (PluginReadyNotice.Missing(manifest, bound, secrets, valued).Count > 0) return;

        try
        {
            await PluginReadyNotice.SendAsync(log, id, name, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The save is the person's act and stands; the Manager can still be told by a person.
        }
    }

    internal static async Task<(PersistedMember? Member, InstalledPlugin? Plugin, IResult? Refusal)> PluginMemberAsync(
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

        // A BUILT-IN PLUGIN'S MEMBER HAS NO SETTINGS OF ITS OWN: Team Connections holds what its team is
        // assigned, in Team settings -> Connections.
        if (plugin.BuiltIn)
        {
            return (found, null, Results.Conflict(new { error = TeamConnectionsMember.ChangeRefused }));
        }

        return (found, plugin, null);
    }

    private static async Task<object> SettingsAsync(
        ContainerId id, InstalledPlugin plugin, PluginMemberSettings stored, MemberSecretReads reads, CancellationToken ct) => new
    {
        team = id.Team,
        member = id.Name,
        plugin = plugin.Manifest.Id,
        version = plugin.Manifest.Version,
        description = string.IsNullOrWhiteSpace(plugin.Manifest.Description) ? null : plugin.Manifest.Description,
        config = stored.Config,
        secrets = stored.Secrets,
        secretStates = await reads.StatesAsync(id, plugin.Manifest, stored, ct),
        connections = stored.Connections,
        fields = Fields(plugin.Manifest),
        secretFields = SecretFields(plugin.Manifest),
        namedSecrets = NamedSecretsField(plugin.Manifest),
        namedSecretStates = plugin.Manifest.NamedSecrets is null ? [] : await reads.NamedStatesAsync(id, ct),

        // EACH TABLE'S SECRET CELLS THAT HOLD A VALUE: who set each and when; a value only for an unmasked column.
        tableSecretStates = await reads.TableStatesAsync(id, plugin.Manifest, stored, ct),
        connectionFields = ConnectionFields(plugin.Manifest),
        outOfRange = PluginMemberRunner.OutOfRange(plugin.Manifest, stored),
        marketplaceUrl = reads.MarketplacePage(plugin.Manifest.Id),
    };

    /// <summary>The fields with a member value of their own once <paramref name="changes"/> are applied.</summary>
    internal static async Task<IReadOnlySet<string>> ValuedAfterAsync(
        PluginMemberSecretStore memberSecrets, ContainerId id, IReadOnlyDictionary<string, string?> changes, CancellationToken ct)
    {
        var valued = (await memberSecrets.StatesAsync(id, ct)).Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var (field, value) in changes)
        {
            if (value is null) valued.Remove(field);
            else valued.Add(field);
        }

        return valued;
    }

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
            min = c.Value.Min,
            max = c.Value.Max,
            integer = c.Value.Integer,
            advanced = c.Value.Advanced,
            label = c.Value.Label,
            table = c.Value.Table?.Describe(),
        });

    /// <summary>Each connection slot, its scopes in the object form, the Plugins screen's line, and
    /// for a slot admitting Microsoft each API it needs a token for.</summary>
    public static Dictionary<string, object> ConnectionFields(PluginManifest manifest) => manifest.Connections.ToDictionary(
        c => c.Key,
        c => (object)new
        {
            description = c.Value.Description,
            providers = c.Value.Providers,
            scopes = c.Value.Scopes,
            required = c.Value.Required,
            summary = c.Value.Summary,
            // Each Microsoft API the slot needs a token for, with its scopes; null when it does not admit Microsoft.
            microsoftApis = c.Value.Admits(ConnectionProviders.Microsoft)
                ? c.Value.MicrosoftApis.Select(a => new { api = a.Api, name = a.Name, scopes = a.Scopes })
                : null,
        });

    /// <summary>The manifest's <c>namedSecrets</c> block as every answer carries it, or null.</summary>
    private static object? NamedSecretsField(PluginManifest manifest) => manifest.NamedSecrets is { } block
        ? new { label = block.Label, description = block.Description, max = block.Max }
        : null;

    /// <summary>Why <paramref name="changes"/> to the member's named secrets cannot be kept, or null.</summary>
    internal static async Task<string?> NamedRefusalAsync(
        PluginMemberSecretStore memberSecrets, ContainerId id, PluginManifest manifest,
        IReadOnlyDictionary<string, string?> changes, CancellationToken ct) =>
        NamedSecretRules.ChangesRefusal(
            manifest, [.. (await memberSecrets.NamedStatesAsync(id, ct)).Select(s => s.Name)], changes);

    /// <summary>The one answer of every named-secrets route: the block, and each name held. Never a value.</summary>
    private static async Task<object> NamedSecretsAnswerAsync(
        ContainerId id, PluginNamedSecrets block, PluginMemberSecretStore memberSecrets, CancellationToken ct) => new
    {
        team = id.Team,
        member = id.Name,
        label = block.Label,
        description = block.Description,
        max = block.Max,
        namedSecrets = (await memberSecrets.NamedStatesAsync(id, ct)).Select(s => new { name = s.Name, setBy = s.SetBy, setAt = s.SetAt }),
    };

    private static Dictionary<string, object> SecretFields(PluginManifest manifest) => manifest.Secrets.ToDictionary(
        s => s.Key,
        s => (object)new { description = s.Value.Description, required = s.Value.Required, label = s.Value.Label, masked = s.Value.Masked });

    private static object Listing(
        PluginCatalog plugins, TeamRegistry teams, Marketplace marketplace, IReadOnlyList<TeamSolutionRow> solutions, HttpContext context)
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
                // THE PLATFORM'S OWN: shipped in the image, versioned with it, never uninstalled, never in the
                // Marketplace or Add member, never hired. Every team has its member (TeamConnectionsMember).
                builtIn = p.BuiltIn,
                locked = p.BuiltIn,
                protocol = p.Manifest.Protocol,
                timeoutSeconds = p.Manifest.TimeoutSeconds,
                config = Fields(p.Manifest),
                secrets = SecretFields(p.Manifest),
                namedSecrets = NamedSecretsField(p.Manifest),
                connections = ConnectionFields(p.Manifest),
                publishes = p.Manifest.Publishes.Select(e => new { type = $"plugin.{p.Manifest.Id}.{e.Type}", summary = e.Summary }),
                skills = p.Manifest.Skills,
                skill = PluginSkills.SkillOf(p),
                requires = p.Manifest.Requires,
                members = p.BuiltIn ? [] : hired[p.Manifest.Id].Select(m => new { team = m.Team, member = m.Id }),
                solutions = person
                    ? solutions
                        .Where(row => row.MinimumFor(p.Manifest.Id) is not null)
                        .Select(row => new
                        {
                            team = row.Team, teamName = teams.LabelFor(row.Team), packageId = row.PackageId,
                            package = row.Name, version = row.Version, minimum = row.MinimumFor(p.Manifest.Id),
                        })
                        .ToList()
                    : [],
                reserved = p.Manifest.Reserved.Keys,
                ignored = p.Manifest.Ignored,
                marketplaceUrl = p.BuiltIn ? null : marketplace.PluginPage(p.Manifest.Id),
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

    /// <summary>The one shape every <c>/api/secrets/{key}</c> answer takes, with <c>error</c> when
    /// <paramref name="error"/> is given. Never a value.</summary>
    private static Dictionary<string, object?> SecretAnswer(string key, PluginSecretStore secrets, string? error = null)
    {
        var refusal = EnvironmentSecretStore.Refusal(key);
        var status = refusal is null ? secrets.Status(key) : SecretStatus.NotSet;
        var answer = new Dictionary<string, object?>
        {
            ["key"] = key,
            ["set"] = status.Set,
            ["refusal"] = refusal,
            ["setWith"] = Solutions.SolutionSecret.SetWithFor(key),
            ["setBy"] = status.SetBy,
            ["setAt"] = status.SetAt,
            ["source"] = status.Source,
        };

        if (error is not null) answer["error"] = error;
        return answer;
    }

    private static string UnrecordedName(string name, Exception exception) =>
        $"The secret `{name}` was not changed: its record could not be written ({exception.Message}).";

    /// <summary>The plugin member and its manifest, which declares a <c>namedSecrets</c> block, or the
    /// answer saying why there is none.</summary>
    private static async Task<(PersistedMember? Member, PluginManifest? Manifest, IResult? Refusal)> NamedSecretsMemberAsync(
        TeamRegistry teams, PluginCatalog plugins, string team, string member, CancellationToken ct)
    {
        var (found, plugin, refusal) = await PluginMemberAsync(teams, plugins, team, member, ct);
        if (refusal is not null) return (found, null, refusal);

        if (plugin!.Manifest.NamedSecrets is null)
        {
            return (found, null, Results.BadRequest(new
            {
                error = $"Plugin '{plugin.Manifest.Id}' declares no named secrets; its secrets are the fields its manifest names.",
            }));
        }

        return (found, plugin.Manifest, null);
    }

    private static string UnrecordedField(string field, Exception exception) =>
        $"`{field}` was not changed: its record could not be written ({exception.Message}).";

    private static string Unrecorded(string key, Exception exception) =>
        $"`{key}` was not changed: its record could not be written ({exception.Message}).";

    internal static CredentialActor SecretActor(HttpContext context) => new(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier),
        context.User.FindFirstValue(ClaimTypes.Email) ?? "unknown");
}

/// <summary>Body of <c>POST /api/plugins/install</c>.</summary>
public sealed record InstallPlugin(string? Path, bool Replace = false);

/// <summary>Body of <c>PUT .../members/{member}/plugin-settings</c>: <c>config</c> replaces what is
/// stored; <c>secrets</c> and <c>connections</c> replace it when given and keep it when omitted;
/// <c>secretValues</c> sets (a string) or clears (null) the member's own value of each field named;
/// <c>namedSecretValues</c> sets (a string) or removes (null) each named secret given;
/// <c>tableSecretValues</c> sets (a string) or clears (null) each table secret cell given.</summary>
public sealed record PluginSettingsChange(
    Dictionary<string, JsonElement>? Config, Dictionary<string, string>? Secrets,
    Dictionary<string, string>? Connections = null,
    Dictionary<string, string?>? SecretValues = null,
    Dictionary<string, string?>? NamedSecretValues = null,
    Dictionary<string, Dictionary<string, Dictionary<string, string?>>>? TableSecretValues = null);

/// <summary>Body of <c>PUT /api/secrets/{key}</c>: the value, which no answer ever carries.</summary>
public sealed record SecretSubmission(string? Value);
