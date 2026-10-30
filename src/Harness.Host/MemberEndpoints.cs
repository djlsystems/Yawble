using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;
using Harness.Containers;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Capacity;
using Harness.Host.Solutions;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;
using Harness.Skills;

namespace Harness.Host;

internal static class MemberEndpoints
{
    public static void Map(WebApplication app)
    {
        // WHAT A MEMBER'S RUNS ACTUALLY COST, for the trigger dialog before a person saves. Measured only:
        // nothing here projects a day, and a member nobody has measured says so with a null median.
        app.MapGet("/api/teams/{team}/containers/{name}/cost", async (
            [Description(Describe.Team)] string team,
            [Description("The member's current identifier, as addressed in its route.")] string name,
            TeamRegistry teams,
            TriggerCost cost,
            CancellationToken ct) =>
        {
            try
            {
                var member = await teams.MemberAsync(team, name, ct);

                return Results.Ok(await cost.RecentAsync(
                    new ContainerId(member.Team, member.Name), MemberRef.KindOf(member.Agent), ct));
            }
            catch (InvalidOperationException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        })
            .WithTags("Teams")
            .RequirePermit(Permits.Read)
            .WithSummary("A member's measured recent cost")
            .WithDescription(
                "What this member's last runs (up to 10) actually cost: `lastRuns`, how many of them were "
                + "measured (`measuredRuns`) and not (`unmeasuredRuns`), the median `BillableTokens` of the "
                + "measured ones (`medianBillableTokens`, null when none was), and `kind` (`agent` or "
                + "`plugin`). Nothing is estimated. A run is one terminal row carrying its run's figures, so "
                + "a batched run counts once.\n\n"
                + "404 for an unknown team or member.");

        app.MapGet("/api/teams/{team}/hiring", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams,
            ITeamStore teamStore,
            AgentCatalog catalog,
            PluginCatalog plugins,
            IConnectionStore connectionStore,
            CancellationToken ct) =>
        {
            if (!teams.Exists(team))
            {
                return Refusals.NoTeam(team);
            }

            // THE CONNECTIONS A PERSON HAS ASSIGNED TO THIS TEAM: the only ones a Manager's hire may name. By
            // id, name, kind and provider - never a token or any other value.
            var assigned = (await connectionStore.AssignedAsync(teams.ExistingName(team) ?? team, ct))
                .Select(a => a.ConnectionId)
                .ToHashSet(StringComparer.Ordinal);
            var assignedHere = (await connectionStore.ListAsync(ct))
                .Where(c => assigned.Contains(c.Id))
                .Select(c => new { id = c.Id, name = c.Name, kind = c.Kind, provider = c.Provider })
                .ToArray();

            var allowlist = teams.MemberAgentsFor(team) ?? [];
            var members = (await teamStore.MembersAsync(ct))
                .Where(member => string.Equals(member.Team, team, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var rows = allowlist.Select(agent =>
            {
                var tags = catalog.Definition(agent)?.Tags?
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                    ?? [];

                var counts = tags.Select(tag => new
                {
                    tag,
                    count = members.Count(member =>
                        string.Equals(member.Agent, agent, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(member.HiredFor, tag, StringComparison.OrdinalIgnoreCase)),
                });

                return new { agent, tags, counts };
            }).ToArray();

            return Results.Ok(new
            {
                team = teams.ExistingName(team) ?? team,
                allowlist = rows,
                // The roles a hire would ask for that NO allowed agent carries: hiring for one of these
                // falls back to the first allowed agent, and the Manager should know before it asks.
                uncoveredTags = HireTags.Uncovered(rows.Select(row => row.tags)),

                // THE INSTALLED PLUGINS, which a Manager may hire onto its own team with the `member`
                // tool's `plugin`: each one's id, one line, and the skill that says how to use it
                // (`skills_get`), whether or not it is hired anywhere.
                plugins = plugins.Plugins.Select(p => new
                {
                    id = p.Manifest.Id,
                    reference = MemberRef.ForPlugin(p.Manifest.Id),
                    description = p.Manifest.Description,
                    skill = PluginSkills.SkillOf(p),
                    connections = p.Manifest.Connections.ToDictionary(c => c.Key, c => c.Value.Summary),
                }).ToArray(),

                connections = assignedHere,
            });
        })
            .WithTags("Teams")
            .RequirePermit(Permits.Read)
            .WithSummary("Show this team's dynamic-member hiring mix")
            .WithDescription(
                "Returns only this team's member-agent allowlist in order, each entry's tags, and the "
                + "current per-tag counts on this team, plus `uncoveredTags`: the hire roles (developer, "
                + "tester, researcher) that no allowed agent carries, and `plugins`: every plugin installed "
                + "on this Host (id, reference, one line, its skill's name, and what each connection slot "
                + "needs), and `connections`: each connection a person has assigned to this team (`id`, `name`, "
                + "`kind`, `provider`) - the only ones a Manager's hire may bind.\n\n"
                + "This route never returns command lines, `env`, a token, or Agents outside this team's allowlist.");

        app.MapPost("/api/teams/{team}/containers", async (
            [Description(Describe.Team)] string team,
            CreateContainer request, TeamRegistry teams, ITeamStore teamStore, AgentCatalog catalog,
            TenantLogging audit, AgentInstallProbe probe, HttpContext context, ISecretStore secretStore,
            IPluginMemberSettingsStore pluginSettings, PluginCatalog plugins, IUserStore users,
            Connections connections, PluginMemberSecretStore memberSecrets, MemberDeletion deletion, CancellationToken ct) =>
        {
            // `name` is free text here too, exactly as it is for a team: the identifier is derived inside
            // TeamRegistry and never asked for. A member called "Data Ingest" is `DataIngest` on disk.
            var name = (request.Name ?? "").Trim();

            if (name.Length == 0) return Results.BadRequest(new { error = "A member needs a name." });
            if (!teams.Exists(team)) return Refusals.NoTeam(team);

            if (name.Length > TeamRegistry.MaximumLabelLength)
            {
                return Results.BadRequest(new
                {
                    error = $"A member name cannot be longer than {TeamRegistry.MaximumLabelLength} characters.",
                });
            }

            try
            {
                // A PERMIT GRANTS THE VERB, NOT THE FIELDS - and that gap is what this refusal closes.
                //
                // `Permits.CreateContainer` says a manager may HIRE. It does not say it may choose what the
                // hire RUNS. Without this refusal `request.Agent` would win unconditionally below, so the
                // team's agent allowlist would be binding on the hire-without-saying path and merely
                // advisory the moment a caller named something - and a team deliberately created on one
                // preset could have its members hired on another by its own manager, a spend decision an
                // agent made on its owner's behalf.
                //
                // THE MANAGER DOES NOT INVENT THE FIELD. The `member` tool advertises `agent` and explains
                // what it does, so an agent picking the one it judges best is behaving exactly as invited.
                // That makes this the platform's job, and a reworded prompt or help page is not the fix.
                //
                // REFUSED RATHER THAN SILENTLY SUBSTITUTED. A manager told "this team hires on X" adapts;
                // one whose choice was quietly replaced believes it got what it asked for, and the mismatch
                // is then visible only to somebody reading the database.
                //
                // AND ONLY ON A MISMATCH. Refusing every `agent` from a machine principal would stop a
                // manager that passes the correct value from hiring at all, which turns a cost guard into a
                // team that cannot work.
                //
                // ApiKey and User are NOT bounded here: both act as a PERSON who chose deliberately.
                // Container and Concierge
                // are the two an LLM holds.
                var allowlist = teams.MemberAgentsFor(team);

                if (allowlist is null || allowlist.Count == 0)
                {
                    throw new TeamHasNoMemberAgentException(team);
                }

                var hirer = PrincipalClaims.From(context.User);

                // A MANAGER MAY HIRE ANY INSTALLED PLUGIN ONTO ITS OWN TEAM (a person's decision,
                // 2026-09-27) - the allowlist bounds which AGENTS a hire may run, a spend decision, and a
                // plugin runs no model. It is validated against its manifest exactly as a person's hire is,
                // in `HireMemberAsync`, and an uninstalled one is refused there. Its own team only: the
                // route's `{team}` is TeamGate's, and a Manager's credential reaches no other team.
                //
                // SECRETS ARE LOGICAL KEYS A PERSON HAS ALREADY BOUND ON THIS TEAM (round 2, M1). A Manager
                // never handles a value, and it may bind only a key some PERSON's hire already bound on its
                // own team - optional or not. "Set on this Host" is not the test: the secret store is the
                // Host's whole environment, so that would hand a plugin PATH, HOSTNAME or any operator
                // variable, and the Host cannot tell which names `secret set` wrote (that list lives in the
                // operator's env file, outside the container). A person's binding is a deliberate choice
                // for this team, which is the decision the person made. The set only grows by a person's
                // hire, because a Manager can bind nothing outside it; firing the last member that bound a
                // key takes it out again. The refusal names the key the Manager sent, never a value.
                var managerHiresPlugin = hirer is { Kind: PrincipalKind.Container }
                    && request.Agent is { } pluginReference
                    && MemberRef.IsPlugin(pluginReference.Trim(), out _);

                if (managerHiresPlugin && request.Secrets is { Count: > 0 } bindings)
                {
                    var bound = await pluginSettings.KeysBoundOnAsync(team, ct);

                    foreach (var (secret, key) in bindings)
                    {
                        if (EnvironmentSecretStore.Refusal(key) is { } keyRefusal)
                        {
                            return Results.BadRequest(new { error = keyRefusal });
                        }

                        if (!bound.Contains(key) || secretStore.TryGet(key) is null)
                        {
                            return Results.BadRequest(new
                            {
                                error = $"The key `{key}` bound for `{secret}` is not one a person has bound on this team. "
                                    + "A Manager binds only logical keys a person has already set (`secret set <KEY>`) and "
                                    + "bound on a member of this team, never a value. Ask a person to hire the first member "
                                    + "that uses it.",
                            });
                        }
                    }
                }

                // A SECRET VALUE IS A PERSON'S TO TYPE. An agent's hire (a Manager's credential, or a
                // Concierge's) never carries one: incoming content reaches an agent's context, and a value
                // an agent handled is a value it could have been steered to. Refused rather than dropped,
                // so the agent hears it; without values its hire waits for a person (PluginReadyNotice).
                var values = request.SecretValues ?? new Dictionary<string, string>(StringComparer.Ordinal);
                if (values.Count > 0)
                {
                    if (request.Agent is not { } valuedAgent || !MemberRef.IsPlugin(valuedAgent.Trim(), out var valuedPluginId))
                    {
                        return Results.BadRequest(new { error = "`secretValues` belong to a plugin member; an Agent takes its settings from its preset." });
                    }

                    if (hirer is { Kind: PrincipalKind.Container or PrincipalKind.Concierge or PrincipalKind.TenantConcierge })
                    {
                        return Results.BadRequest(new
                        {
                            error = "An agent's hire cannot carry a secret value; hire without it and a person sets it in the member's settings.",
                        });
                    }

                    if (plugins.For(valuedPluginId) is { } valuedPlugin
                        && PluginMemberSecretStore.ChangesRefusal(
                            valuedPlugin.Manifest, values.ToDictionary(v => v.Key, v => (string?)(v.Value ?? ""), StringComparer.Ordinal)) is { } valueRefusal)
                    {
                        return Results.BadRequest(new { error = valueRefusal });
                    }
                }

                // A NAMED SECRET IS A VALUE TOO, and a person's to type, exactly as `secretValues` above.
                var namedValues = (request.NamedSecretValues ?? new Dictionary<string, string>(StringComparer.Ordinal))
                    .ToDictionary(v => v.Key, v => (string?)(v.Value ?? ""), StringComparer.Ordinal);
                if (namedValues.Count > 0)
                {
                    if (request.Agent is not { } namedAgent || !MemberRef.IsPlugin(namedAgent.Trim(), out var namedPluginId))
                    {
                        return Results.BadRequest(new { error = "`namedSecretValues` belong to a plugin member; an Agent takes its settings from its preset." });
                    }

                    if (hirer is { Kind: PrincipalKind.Container or PrincipalKind.Concierge or PrincipalKind.TenantConcierge })
                    {
                        return Results.BadRequest(new
                        {
                            error = "An agent's hire cannot carry a secret value; hire without it and a person adds each named secret in the member's settings.",
                        });
                    }

                    if (plugins.For(namedPluginId) is { } namedPlugin
                        && NamedSecretRules.ChangesRefusal(namedPlugin.Manifest, [], namedValues) is { } namedRefusal)
                    {
                        return Results.BadRequest(new { error = namedRefusal });
                    }
                }

                // SETTINGS ONLY A PERSON CHOOSES. A manifest marks a field `"setBy": "person"` when it
                // widens what the plugin does outward - a real send mode, an allowlist. An agent's hire (a
                // Manager's credential, or a Concierge's) may leave such a field at its default and nothing
                // else: incoming content reaches an agent's context by design, so an email saying "hire a
                // sender with this allowlist" must not be able to do it. A person's own hire is not bounded.
                if (hirer is { Kind: PrincipalKind.Container or PrincipalKind.Concierge or PrincipalKind.TenantConcierge }
                    && request.Agent is { } agentHired
                    && MemberRef.IsPlugin(agentHired.Trim(), out var hiredPluginId)
                    && plugins.For(hiredPluginId) is { } hiredPlugin
                    && request.Config is { Count: > 0 } agentConfig)
                {
                    foreach (var (field, value) in agentConfig)
                    {
                        if (hiredPlugin.Manifest.Config.TryGetValue(field, out var declared)
                            && declared.AgentRefusal(field, value) is { } personOnly)
                        {
                            return Results.BadRequest(new { error = personOnly });
                        }
                    }
                }

                // CONNECTIONS ARE A PERSON'S CHOICE. A binding points a plugin at somebody's mailbox, so an
                // agent's hire (a Manager's credential, or a Concierge's) may name only a connection a PERSON
                // has assigned to this same team - otherwise incoming content could steer an agent into
                // pointing a plugin at an account nobody gave this team. A person's hire may name any: one
                // not yet assigned is assigned with the binding, with its own row. Every hire's binding must
                // exist, suit the slot's providers and hold the slot's scopes; the refusal offers Reconnect
                // when only scopes are missing.
                if (request.Connections is { Count: > 0 } connectionBindings)
                {
                    if (request.Agent is not { } boundAgent || !MemberRef.IsPlugin(boundAgent.Trim(), out var boundPluginId))
                    {
                        return Results.BadRequest(new { error = "`connections` belong to a plugin member; an Agent gets no connection and no token." });
                    }

                    if (plugins.For(boundPluginId) is { } boundPlugin)
                    {
                        var agentHire = hirer is { Kind: PrincipalKind.Container or PrincipalKind.Concierge or PrincipalKind.TenantConcierge };
                        var assignedToTeam = agentHire ? await connections.AssignedIdsAsync(teams.ExistingName(team) ?? team, ct) : null;

                        if (await connections.BindingRefusalAsync(boundPlugin.Manifest, connectionBindings, assignedToTeam, ct) is { } bindingRefusal)
                        {
                            return Results.BadRequest(bindingRefusal.Body());
                        }
                    }
                }

                if (request.Agent is { } named
                    && !managerHiresPlugin
                    && hirer
                        is
                        {
                            Kind: PrincipalKind.Container
                                or PrincipalKind.Concierge
                                or PrincipalKind.TenantConcierge,
                        })
                {
                    if (!allowlist.Contains(named, StringComparer.OrdinalIgnoreCase))
                    {
                        return Results.Json(
                            new
                            {
                                error = $"Members of this team may run: {string.Join(", ", allowlist)}. "
                                    + $"Hire without naming an Agent, or name one of those - '{named}' is "
                                    + "not yours to choose. A person changes it in Team Settings, under "
                                    + "Dynamic members.",
                            },
                            statusCode: StatusCodes.Status403Forbidden);
                    }
                }

                var requestedTag = string.IsNullOrWhiteSpace(request.For) ? null : request.For.Trim();
                var chosenByTag = string.IsNullOrWhiteSpace(request.Agent)
                    ? await ResolveMemberAgentForHireAsync(team, allowlist, requestedTag, teamStore, catalog, ct)
                    : (request.Agent!.Trim(), false);

                // No permits, deliberately and with no way to ask for any. A member added through this
                // route is a WORKER: it gets no credential and no HARNESS_* environment, so its agent
                // cannot call the platform at all. A manager is created with the team and is the only container
                // that gets any, which is what stops "add a member" being a way to mint authority.
                // AN AGENT'S PLUGIN HIRE MAY WAIT FOR A PERSON. A Manager or a Concierge may hire a plugin it
                // cannot finish setting up - a key no person bound on this team, a connection only a person
                // chooses: the member is made, its answer names what a person must set, and its runs fail
                // needs-person until a person's save leaves nothing missing (PluginReadyNotice). The keys
                // and connections it DOES name are still checked above; nothing here lets it bind more.
                var agentPluginHire = hirer is { Kind: PrincipalKind.Container or PrincipalKind.Concierge or PrincipalKind.TenantConcierge }
                    && request.Agent is { } agentPlugin && MemberRef.IsPlugin(agentPlugin.Trim(), out _);

                IReadOnlySet<string> valued = values.Keys.ToHashSet(StringComparer.Ordinal);
                var hiredPluginReference = request.Agent?.Trim();

                // A PERSON'S BINDING OF A CONNECTION NOT YET ASSIGNED to the team assigns it, with its own
                // `team.connection-assigned` row beside the binding's. An agent's never gets here with one.
                var assigning = request.Connections is { Count: > 0 } toAssign
                    ? await connections.NotYetAssignedAsync(teams.ExistingName(team) ?? team, toAssign.Values, ct)
                    : [];
                IReadOnlyList<TriggerAudit> AssignedRows(ContainerId member) =>
                    [.. assigning.Select(c => TenantLogging.Row(
                        context, TenantActions.TeamConnectionAssigned, c.Id, c.Name, Connections.AssignedDetail(member.Team, c, "hire")))];

                var snapshot = await teams.HireMemberAsync(
                    // THE TEAM, never a name in code - the Agent half of the same rule its Prompt follows,
                    // and refused rather than defaulted when the team has chosen none.
                    team, name,
                    chosenByTag.Item1,
                    request.SystemPrompt ?? "",
                    request.Subscribes ?? [], hiredFor: requestedTag, ct: ct,
                    settings: request.Config is null && request.Secrets is null && request.Connections is null
                        ? null
                        : new PluginMemberSettings(
                            request.Config ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                            request.Secrets ?? new Dictionary<string, string>(StringComparer.Ordinal))
                        {
                            Connections = request.Connections ?? new Dictionary<string, string>(StringComparer.Ordinal),
                        },

                    // THE BINDING'S ROW lands in the same transaction as the member's settings.
                    connectionsAudit: request.Connections is { Count: > 0 } hiredBindings
                        && request.Agent is { } hiredAgent && MemberRef.IsPlugin(hiredAgent.Trim(), out var hiredId)
                        ? member => [TenantLogging.Row(
                            context, TenantActions.MemberConnectionsChanged, $"{member.Team}/{member.Name}", name,
                            PluginEndpoints.ConnectionsChangedDetail(
                                member, hiredId, hiredBindings.Keys.Order(StringComparer.Ordinal), PluginMemberSettings.None,
                                PluginMemberSettings.None with { Connections = hiredBindings })), .. AssignedRows(member)]
                        : null,

                    // THE HIRER wrote the member's own instructions: a Manager through the `member` tool,
                    // or a person.
                    promptSetBy: await SystemPromptSetters.ForAsync(context, users, ct),
                    waitForPerson: agentPluginHire,
                    valued: valued,

                    // THE MEMBER'S OWN VALUES AND NAMED SECRETS land with its settings, the binding's row
                    // and a `member.secret-set` or `member.named-secret-set` row each, in ONE transaction -
                    // or the member is removed again.
                    saveSettings: values.Count == 0 && namedValues.Count == 0 || !MemberRef.IsPlugin(hiredPluginReference ?? "", out var valuedId)
                        ? null
                        : (member, kept, token) => memberSecrets.SaveWithSettingsAsync(
                            member, name, valuedId!, kept,
                            request.Connections is { Count: > 0 } valuedBindings
                                ? [TenantLogging.Row(
                                    context, TenantActions.MemberConnectionsChanged, $"{member.Team}/{member.Name}", name,
                                    PluginEndpoints.ConnectionsChangedDetail(
                                        member, valuedId!, valuedBindings.Keys.Order(StringComparer.Ordinal), PluginMemberSettings.None,
                                        PluginMemberSettings.None with { Connections = valuedBindings })), .. AssignedRows(member)]
                                : [],
                            values.ToDictionary(v => v.Key, v => (string?)v.Value, StringComparer.Ordinal),
                            namedValues,
                            PluginEndpoints.SecretActor(context), token));

                // A PLUGIN MEMBER HIRED WITH A REQUIRED SLOT OR SECRET UNSET reads Needs you from now on.
                if (context.RequestServices.GetService<PluginSetup>() is { } setup)
                {
                    await setup.RefreshAsync(new ContainerId(snapshot.Team, snapshot.Id), ct);
                }

                // Said in the BODY as well as the header: the body is what the `member` tool hands the
                // Manager, and a substitution said only in a header is never seen by anyone who hired.
                string? hiringNotice = null;
                if (chosenByTag.Item2 && requestedTag is { } unresolved)
                {
                    context.Response.Headers["X-Harness-Hiring-Notice"] =
                        $"no Agent this team may hire on carries the tag '{unresolved}'";
                    hiringNotice = HireTags.Substituted(unresolved, snapshot.Agent);
                }

                var unresolvedList = new List<object>();
                var definition = catalog.Definition(snapshot.Agent);
                if (definition is not null)
                {
                    var installation = probe.Probe(definition);
                    if (installation.State == AgentInstallStates.NotInstalled)
                    {
                        unresolvedList.Add(new { agent = installation.Agent, command = installation.Command, message = installation.Message });
                    }
                }

                await audit.WriteAsync(
                    context, TenantActions.MemberAdded, $"{snapshot.Team}/{snapshot.Id}", snapshot.Name,
                    new { requestedTag, resolvedAgent = snapshot.Agent, team = snapshot.Team }, ct);

                // Serialize snapshot to JsonNode and add unresolvedAgents
                var node = JsonSerializer.SerializeToNode(snapshot, HostJson.WebEnums);
                if (node is JsonObject obj)
                {
                    obj["unresolvedAgents"] = JsonSerializer.SerializeToNode(unresolvedList, HostJson.WebEnums);
                    if (hiringNotice is not null) obj["hiringNotice"] = hiringNotice;

                    // WHAT A PERSON MUST STILL SET, said to whoever hired: the `member` tool hands it to the
                    // Manager, which reports it to the person once rather than running the member to find out.
                    if (MemberRef.IsPlugin(snapshot.Agent, out var waitingPluginId) && plugins.For(waitingPluginId) is { } waitingPlugin)
                    {
                        var missing = PluginReadyNotice.Missing(
                            waitingPlugin.Manifest,
                            new PluginMemberSettings(
                                request.Config ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                                request.Secrets ?? new Dictionary<string, string>(StringComparer.Ordinal))
                            {
                                Connections = request.Connections ?? new Dictionary<string, string>(StringComparer.Ordinal),
                            },
                            secretStore,
                            valued);
                        if (missing.Count > 0)
                        {
                            obj["waitingForPerson"] = new JsonArray([.. missing.Select(m => (JsonNode)JsonValue.Create(m)!)]);
                            obj["waitingNotice"] =
                                $"{snapshot.Name} is hired but waits for a person to {string.Join("; and to ", missing)}. "
                                + "Send it its work as planned and tell the person what to set, once: until then each run "
                                + "fails needs-person without running or spending, and when a person has set it you are "
                                + "told in that workflow to send the instruction again.";
                        }
                    }
                }
                return Results.Json(node, statusCode: 200);
            }
            catch (TeamNameTakenException taken)
            {
                // A DUPLICATE, not a server fault. Every team is created with a Manager, so this is a
                // collision a person reaches by hand, not an unhandled 500. Named by its
                // LABEL, like every other refusal a person reads.
                return Results.Conflict(new { error = $"A member called '{taken.ExistingLabel}' already exists in this team." });
            }
            catch (NoSuchAgentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (PluginSettingsException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (PluginSettingsNotKeptException exception)
            {
                // A HIRE WHOSE VALUES CANNOT BE KEPT MAKES NO MEMBER: the one just made goes again.
                await deletion.DeleteAsync(exception.Member.Team, exception.Member.Name, CancellationToken.None);
                return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (FirehoseSubscriptionException exception)
            {
                // 400, beside its Agent and Prompt siblings: the team and the member both exist and the
                // Agent resolves - it is the SUBSCRIPTION that is refused. A bad target here is never 404,
                // the same reasoning a repoint follows: "not found" reads as the member having vanished.
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (TeamHasNoMemberAgentException exception)
            {
                // The Agent half of the refusal below, and a fourth type rather than words bolted onto it.
                // Same reasoning: a manager reads these, and "no Agent chosen" and "no Prompt chosen" are
                // fixed in two different fields of the same dialog.
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (TeamRootUnreachableException exception)
            {
                // 503, and NOT a 4xx: nothing about this request is wrong. The team is there, the member's
                // name is free and the Agent resolves - what is missing is the team's own folder, which is
                // the Host's side of the conversation. Retrying later is exactly the right response, which
                // is what 503 says and no 4xx does.
                //
                // Unmapped this would be a bare 500 out of Directory.CreateDirectory, for a condition every
                // existing member of the same team explains in one sentence on its own card.
                return Results.Json(
                    new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
            .WithTags("Members")
            .RequirePermit(Permits.CreateContainer)
            .WithSummary("Add a member to a team")
            .WithDescription(
                "Creates an Agent Container - a team member. Every member is headless: it owns one agent, "
                + "holds a queue with a ceiling, and is woken by the message types it subscribes to. There "
                + "is no interactive mode and no second way in.\n\n"
                + "A member's identity is the pair (team, name), because names are global in the message "
                + "log and a bare name is therefore not an identity. As with a team, send the name a person "
                + "would say and the identifier is derived here.\n\n"
                + "400 for an empty or over-long name; 404 for an unknown team; 409 when the team already "
                + "has a member by that name - every team is created with a Manager, so that is a "
                + "collision reachable by hand; 503 when the team's own folder cannot be reached, which is "
                + "the Host's side of the conversation rather than anything wrong with the request.");

        // The STORED row behind a member - its label and its system-prompt OVERRIDE, never the composed
        // text its agent actually receives. That composed text is NOT exposed anywhere: `ContainerSnapshot`
        // deliberately carries no such field, for two reasons that both matter - it is derived data that
        // would answer a question `SystemPrompt` here already answers, disagreeing with it the moment a
        // preset's own default changes; and a snapshot rides every SignalR `containerChanged` push to every
        // connected client on the team, where a multi-kilobyte prompt has no business travelling on a
        // state change nobody asked to see it for.
        //
        // Carries {team} in its template, so TeamGate covers it the same way its PATCH sibling is covered -
        // no bespoke authority check here, and none of the Agent catalog's redaction either: this answers
        // the team's OWN stored member, not another principal's preset or credential.
        app.MapGet("/api/teams/{team}/containers/{name}", async (
            [Description(Describe.Team)] string team,
            [Description("The member's current identifier, as addressed in its route.")] string name,
            TeamRegistry teams, CancellationToken ct) =>
        {
            try
            {
                var member = await teams.MemberAsync(team, name, ct);

                return Results.Ok(new
                {
                    team = member.Team,
                    id = member.Name,
                    name = member.Label ?? member.Name,
                    systemPrompt = member.SystemPrompt,
                    systemPromptSetBy = member.SystemPromptSetBy?.By,
                    systemPromptSetByKind = member.SystemPromptSetBy?.Kind,
                    systemPromptSetAt = SystemPromptSetAt(member.SystemPromptSetBy),
                });
            }
            catch (InvalidOperationException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        })
            .WithTags("Members")
            .RequirePermit(Permits.Read)
            .WithSummary("Read a member's stored settings")
            .WithDescription(
                "The STORED row behind a member - its label and its own role line, never the composed text "
                + "its agent actually receives (not exposed anywhere). `systemPrompt` is the role line the "
                + "member was hired with, appended after the built-in role prompt, or null when it has none. "
                + "There is no prompt choice: the prompt is chosen by role.\n\n"
                + "`systemPromptSetBy` names who last set `systemPrompt` - a Manager's member id, or a "
                + "person's email - and `systemPromptSetByKind` which (`manager` or `person`); "
                + "`systemPromptSetAt` is when, ISO-8601 UTC. Set at hire and by every PATCH that changes the "
                + "text, a clear included. All three are null for a member nobody is known to have set: one "
                + "from before they were recorded, until it is next edited, or a plugin.\n\n"
                + "404 for an unknown team or member.");

        // One member's own recent activity - the message log filtered to one (team, member), newest first.
        // This is also the member-toolbar log, so there is one route rather than two that can disagree.
        //
        // Resolved the same way the GET route above it is: through TeamRegistry.MemberAsync, which resolves
        // the team's STORED spelling and confirms the container actually exists before handing back the
        // row - so the qualified id built below is the FOUND container's, never the caller's spelling. That
        // matters here specifically because ReadForContainerAsync matches on Source, and a type built from
        // what was typed is how a call answers 200 over an empty tail forever.
        app.MapGet("/api/teams/{team}/containers/{name}/messages", async (
            [Description(Describe.Team)] string team,
            [Description("The member's current identifier, as addressed in its route.")] string name,
            [Description("How many rows, newest first. Default 10, clamped to 100.")] int? take,
            TeamRegistry teams, IMessageLog log, CancellationToken ct) =>
        {
            try
            {
                var member = await teams.MemberAsync(team, name, ct);
                var id = new ContainerId(member.Team, member.Name);

                // Clamped rather than trusted: this is a tail, and an agent asking for everything is
                // exactly the context blow-up the transcript was excluded from this feature to avoid.
                var max = Math.Clamp(take ?? 10, 1, 100);

                // The member's own FLOOR, off the row this route has already read. Everything at or below
                // it belongs to whatever same-(team, name) container came before - a deleted team recreated
                // under the same names, or a member deleted and re-hired. Without it this route hands an
                // AGENT its predecessor's task, since the `status` tool reads exactly here, and nothing
                // fails.
                //
                // The STORED floor rather than the live container's: this route already resolves through
                // MemberAsync, and the two are one fact written in one operation - TeamReset writes the row
                // and re-floors the live object together, in that order.
                return Results.Ok(await log.ReadForContainerAsync(id.ToString(), member.FloorSeq, max, ct));
            }
            catch (InvalidOperationException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("What one agent container recently published")
            .WithDescription(
                "That container's own rows - started, completed, failed, progress, blocked, needs-decision - newest "
                + "first. It is a TAIL rather than a history: at most 100 rows, and there is no paging.\n\n"
                + "Instructions addressed TO the container are not here. Those are somebody else's "
                + "publication and appear in the team's activity feed.\n\n"
                + "Not the transcript. A transcript is unbounded raw agent output; this is the "
                + "structured record of what the container reported.\n\n"
                + "404 for an unknown team or member.");

        // Changes what a member is CALLED, what it is TOLD, and what it RUNS.
        //
        // `agent` is repointable here because the runner resolves the preset by name on every invocation -
        // there is no per-container map of resolved commands - so a repoint is one write and takes effect
        // on the member's next wake.
        //
        // Carries {team} in its template, so TeamGate covers it the same way the member-create route
        // beside it is covered - no bespoke authority check here.
        app.MapPatch("/api/teams/{team}/containers/{name}", async (
            [Description(Describe.Team)] string team,
            [Description("The member's current identifier, as addressed in its route.")] string name,
            UpdateContainer request, TeamRegistry teams, AgentInstallProbe probe,
            AgentCatalog catalog, HttpContext context, IUserStore users, CancellationToken ct) =>
        {
            try
            {
                // The row and its tenant_events rows are one transaction: an edit with no record of who
                // made it does not land.
                //
                // WHICH fields moved, not what they moved to. A prompt is a person's words and can be long;
                // recording that one changed keeps the log readable and still answers "who touched this".
                var update = await teams.UpdateMemberAsync(
                    team, name, request.Name, request.SystemPrompt, request.Agent,
                    await SystemPromptSetters.ForAsync(context, users, ct), ct,
                    change => MemberAuditRows(context, change, request.Agent));
                var updated = update.Snapshot;

                var unresolvedList = new List<object>();

                // Only probe if agent was in the body
                if (!string.IsNullOrEmpty(request.Agent))
                {
                    var definition = catalog.Definition(updated.Agent);
                    if (definition is not null)
                    {
                        var installation = probe.Probe(definition);
                        if (installation.State == AgentInstallStates.NotInstalled)
                        {
                            unresolvedList.Add(new { agent = installation.Agent, command = installation.Command, message = installation.Message });
                        }
                    }
                }

                // Serialize updated to JsonNode and add unresolvedAgents
                var node = JsonSerializer.SerializeToNode(updated, HostJson.WebEnums);
                if (node is JsonObject obj)
                {
                    obj["unresolvedAgents"] = JsonSerializer.SerializeToNode(unresolvedList, HostJson.WebEnums);

                    // The same three the GET answers, so a client that just saved need not read back.
                    obj["systemPromptSetBy"] = update.Row.SystemPromptSetBy?.By;
                    obj["systemPromptSetByKind"] = update.Row.SystemPromptSetBy?.Kind;
                    obj["systemPromptSetAt"] = SystemPromptSetAt(update.Row.SystemPromptSetBy);
                }
                return Results.Json(node, statusCode: 200);
            }
            catch (NoSuchAgentException exception)
            {
                // 400, not the 404 the generic catch below would give: the TEAM and the MEMBER both exist -
                // it is the requested Agent that does not, or is interactive. Answering "not found" for a
                // route whose subject is right there reads as the member having vanished. Caught ahead of
                // InvalidOperationException, which it derives from; the compiler enforces the order.
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (FirehoseSubscriptionException exception)
            {
                // 400, not the 404 the generic catch below would give, for the same reason as its two
                // siblings above: the team and the member both exist, and a repoint that would turn a
                // legal pair illegal is refused before the row is written. Caught ahead of
                // InvalidOperationException, which it derives from.
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("Change a member's name, system prompt or Agent")
            .WithDescription(
                "Changes what a member is CALLED, what it is TOLD, and what it RUNS. `agent` must name a "
                + "HEADLESS preset - a member is woken by a message and never typed at. The change takes "
                + "effect on the member's next wake, with no restart, because the catalog entry a runner "
                + "resolves is written in the same operation as the stored row.\n\n"
                + "A system prompt equal to its preset's current default - Ordinal comparison - is stored "
                + "as no override at all, so a member left tracking its preset keeps tracking it as the "
                + "preset improves, and that comparison is against the preset the member ends up on. The "
                + "live container is re-prompted immediately; nothing here waits for a restart.\n\n"
                + "A MANAGER IS EDITABLE LIKE ANY OTHER MEMBER - its label, Agent, Prompt and added "
                + "instructions alike. Its team's CURRENT name and roster still reach it on every run: "
                + "an override is a TEMPLATE resolved on every composition, so "
                + "`{team}` and `{members}` stay exactly as live inside one as inside a preset default, "
                + "and the words are ADDED to the composed System Prompt rather than substituted for it. "
                + "Relabelling a manager and repointing its Prompt is how a team takes on a ROLE - an "
                + "`Auditor Manager`, a `Security Manager` - with no second mechanism behind it.\n\n"
                + "A change to `systemPrompt` - a clear included, a resend of the same words not - records "
                + "the person as who last set it (`systemPromptSetBy`, `systemPromptSetByKind`, "
                + "`systemPromptSetAt`, answered here and on GET) and appends `member.instructions-changed` "
                + "to the tenant log. People only: a Manager sets a member's instructions once, at hire.\n\n"
                + "400 when `agent` names a preset this tenant does not have, or an interactive one.\n\n"
                + "404 for an unknown team or member.");

        // The ORDER the work happens in is MemberDeletion's, and its doc comment is where the reasoning
        // lives. Gated by {team} alone, like the POST and PATCH above it and deliberately NOT people-only
        // the way deleting a TEAM is: whoever can add a member can remove one, and a member is re-creatable
        // in a way a team full of documents and transcripts is not.
        app.MapDelete("/api/teams/{team}/containers/{name}", async (
            [Description(Describe.Team)] string team,
            [Description("The member's identifier, as addressed in its route.")] string name,
            MemberDeletion deletion, TenantLogging audit, HttpContext context,
            CancellationToken ct) =>
        {
            try
            {
                // Null means no such team OR no such member, and both are a 404 rather than a failure:
                // asking twice is not a fault, and a caller retrying after a timeout should not be told
                // something broke.
                if (await deletion.DeleteAsync(team, name, ct) is not { } removed)
                {
                    return Results.NotFound(new { error = $"No member '{name}' on team '{team}'." });
                }

                // Exactly what went. Written AFTER the delete: recording an intention that then failed
                // would be worse than recording nothing. The LABEL travels beside the identifier because
                // afterwards there is nothing left to read it from, and a row naming an identifier a person
                // never saw is a row they cannot match to what they did.
                await audit.WriteAsync(
                    context, TenantActions.MemberDeleted, $"{removed.Team}/{removed.Member}", removed.Label,
                    // Named explicitly rather than by shorthand, and the reason is visible in the log
                    // itself: a shorthand property keeps its C# casing, and nothing camel-cases this
                    // detail on the way out - so `removed.Failures` beside `team = ...` writes one object
                    // spelt two ways, in a column a person reads.
                    new
                    {
                        team = removed.Team,
                        pendingDeliveries = removed.PendingDeliveries,
                        schedules = removed.Schedules,
                        directories = removed.Directories,
                        failures = removed.Failures,
                        remaining = removed.Remaining,
                    },
                    ct);

                // 200 with a body rather than 204, for the reason deleting a team answers one: a workspace
                // that could not be removed is still a deletion, and a caller told only "no content" cannot
                // say which files are still on disk.
                return Results.Ok(removed);
            }
            catch (ManagerCannotBeDeletedException exception)
            {
                // 409 rather than 403: nothing about the caller would make this allowed.
                return Results.Conflict(new { error = exception.Message });
            }
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("Delete a member and everything that names it")
            .WithDescription(
                "Stops the member's Agent Container, removes its cursor, subscriptions, outstanding "
                + "deliveries and credential, deletes its row, and removes its workspace directory.\n\n"
                + "**The team's manager is re-prompted**, so it stops being told it can dispatch to a "
                + "member that is no longer there.\n\n"
                + "**Transcripts are kept**, unlike a team deletion, because messages already in the log "
                + "carry paths into them and the log is append-only and untouched here.\n\n"
                + "Answers 200 with what was removed, including a workspace that could NOT be deleted - "
                + "usually a file still held by a child process that has not finished exiting. Those are "
                + "named rather than swallowed, and the member is gone either way.\n\n"
                + "409 for a team's manager: there is no team without a door into it, so delete the team "
                + "itself instead.\n\n"
                + "404 for an unknown team or member; asking twice is not an error.");

        // WHAT IS WAITING FOR EACH MEMBER: accepted, not started. `status` shows it so a Manager does not
        // re-send what a member already has - see QueuedInstructions.
        app.MapGet("/api/teams/{team}/queued", async (
            [Description(Describe.Team)] string team,
            [Description("Omit for every member. The member's identifier, matched case-insensitively.")] string? member,
            TeamRegistry teams, IMessageLog log, IPendingDeliveries pending, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored) return Refusals.NoTeam(team);

            var queued = await QueuedInstructions.ForTeamAsync(
                pending, log, stored, string.IsNullOrWhiteSpace(member) ? null : member.Trim(), ct);

            return Results.Ok(new { queued });
        })
            .WithTags("Members")
            .RequirePermit(Permits.Read)
            .WithSummary("Each member's queued and deferred instructions")
            .WithDescription(
                "Every delivery a member of this team has accepted and not yet started, oldest first within "
                + "each member: its `seq`, the first `line` of the instruction, its `source` and the workflow "
                + "(`correlation`) it belongs to. `state` is `queued`, or `deferred` for an item a batched run "
                + "put back on the queue, with `deferredFromRun` naming that run. What a member is running "
                + "now is not listed: every item of a running batch has started.\n\n"
                + "404 for an unknown team.");
    }

    // ISO-8601 UTC, `Z`-suffixed, as the member routes answer it.
    static string? SystemPromptSetAt(SystemPromptSetter? setter) =>
        setter?.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    // A member edit's tenant rows: `member.changed` always, and ITS OWN ROW when the member's own
    // instructions changed, so "who told this member to be what it is" is found by name. Who and
    // whether cleared - never the words.
    static IReadOnlyList<TriggerAudit> MemberAuditRows(HttpContext context, MemberChange change, string? agent)
    {
        var subject = $"{change.Id.Team}/{change.Id.Name}";
        var rows = new List<TriggerAudit>
        {
            TenantLogging.Row(
                context, TenantActions.MemberChanged, subject, change.Name,
                new { team = change.Id.Team, renamed = change.Renamed, promptChanged = change.PromptChanged, agent }),
        };

        if (change.PromptChanged)
        {
            rows.Add(TenantLogging.Row(
                context, TenantActions.MemberInstructionsChanged, subject, change.Name,
                new
                {
                    team = change.Id.Team,
                    setBy = change.Row.SystemPromptSetBy?.By,
                    setByKind = change.Row.SystemPromptSetBy?.Kind,
                    cleared = change.Row.SystemPrompt is null,
                }));
        }

        return rows;
    }

    static async Task<(string Agent, bool Substituted)> ResolveMemberAgentForHireAsync(
        string team,
        IReadOnlyList<string> allowlist,
        string? requestedTag,
        ITeamStore teamStore,
        AgentCatalog catalog,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestedTag))
        {
            return (allowlist[0], false);
        }

        var tagged = allowlist
            .Where(agent => AgentCarriesTag(catalog, agent, requestedTag))
            .ToArray();

        if (tagged.Length == 0)
        {
            return (allowlist[0], true);
        }

        var members = (await teamStore.MembersAsync(ct))
            .Where(member => string.Equals(member.Team, team, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var selected = tagged
            .Select((agent, index) => new
            {
                Agent = agent,
                Index = index,
                Count = members.Count(member =>
                    string.Equals(member.Agent, agent, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(member.HiredFor, requestedTag, StringComparison.OrdinalIgnoreCase)),
            })
            .OrderBy(entry => entry.Count)
            .ThenBy(entry => entry.Index)
            .First().Agent;

        return (selected, false);
    }

    static bool AgentCarriesTag(AgentCatalog catalog, string agent, string tag)
    {
        var definition = catalog.Definition(agent);

        if (definition?.Tags is null || definition.Tags.Count == 0)
        {
            return false;
        }

        return definition.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
    }
}
