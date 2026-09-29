using System.ComponentModel;
using System.Security.Claims;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// Skills. One tenant-wide list with two kinds: built-in skills, compiled into the host and
/// read-only everywhere, and custom skills, which are the person's and live only in the database.
///
/// Two audiences, two route families. A person reads and writes through <c>/api/skills</c>. An agent
/// reads through <c>/api/me/skills</c>, which the MCP tools <c>skills_get</c> and
/// <c>skills_search</c> call, and sees only the skills offered to its role - a role the server reads
/// from the credential, never from anything the agent says.
///
/// A TEAM SKILL (<see cref="TeamSkills"/>) is offered only to that team's members of its roles, the
/// team read from the credential too: another team's member, and the Concierge, are refused it by
/// <c>skills_get</c> with a sentence and never find it by <c>skills_search</c>. A person manages a
/// team's skills through <c>/api/teams/{team}/skills</c>; <c>/api/skills/{name}</c> addresses an
/// instance-wide skill only.
/// </summary>
public static class SkillsEndpoints
{
    private const string Area = "Skills";

    internal sealed record SkillWrite(
        [property: Description(
            "On `POST /api/skills`, the new skill's name; on PUT, a new name renames the skill (omit to "
            + "keep it); ignored on `POST /api/skills/{name}`. Lowercase "
            + "kebab-case, at most 48 characters, and never a built-in skill's name.")]
        string? Name,
        [property: Description("One line saying when to use the skill.")]
        string? Description,
        [property: Description(
            "Who the skill is offered to: any of `concierge`, `manager`, `member`, `any`. Required.")]
        IReadOnlyList<string>? Roles,
        [property: Description("The skill's text, as markdown.")]
        string? Body);

    /// <summary>The role a caller is offered skills for, or null for a person, who sees them all.</summary>
    public static string? RoleOf(Principal principal) =>
        principal.Kind switch
        {
            PrincipalKind.TenantConcierge or PrincipalKind.Concierge => SkillRoles.Concierge,
            PrincipalKind.Container when ContainerId.TryParse(principal.Id, out var id) =>
                string.Equals(id.Name, TeamRegistry.DefaultManagerName, StringComparison.OrdinalIgnoreCase)
                    ? SkillRoles.Manager
                    : SkillRoles.Member,
            PrincipalKind.Container => SkillRoles.Member,
            _ => null,
        };

    /// <summary>The team a caller's team skills come from: a container's own; null for everyone else.</summary>
    public static string? TeamOf(Principal principal) =>
        principal.Kind == PrincipalKind.Container && ContainerId.TryParse(principal.Id, out var id) ? id.Team : null;

    private static async Task<IResult> CreateAsync(
        string name, SkillWrite request, HttpContext context, ISkillStore skills,
        SkillDirectory directory, TeamRegistry teams, TenantLogging audit, CancellationToken ct)
    {
        if (request.Roles is not { Count: > 0 })
        {
            return Results.BadRequest(new
            {
                error = $"A custom skill must say who it is for: roles, any of {string.Join(", ", SkillRoles.All)}.",
            });
        }

        Skill created;
        try
        {
            created = await skills.CreateCustomAsync(
                new SkillDraft(name, request.Description ?? "", request.Roles, request.Body ?? ""),
                WhoIs(context.User), ct);
        }
        catch (SkillRefusedException refused)
        {
            return Results.Conflict(new { error = refused.Message });
        }
        catch (ArgumentException invalid)
        {
            return Results.BadRequest(new { error = invalid.Message });
        }

        await AfterWriteAsync(skills, directory, teams, ct);
        await audit.WriteAsync(
            context, TenantActions.SkillCreated, null, null,
            new { name = created.Name, roles = created.Roles }, ct);

        return Results.Created($"/api/skills/{created.Name}", Dto(created));
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/skills", async (
            [Description("`custom` (default), `builtin`, `plugin` or `all`.")] string? kind,
            [Description("Full-text search over name, description and body. Omit to list.")] string? q,
            [Description("Rows with an id below this; omit for the first page.")] long? before,
            [Description("Rows per page: default 50, at most 200.")] int? take,
            ISkillStore skills,
            CancellationToken ct) =>
        {
            if (!TryParseKind(kind, out var filter))
            {
                return Results.BadRequest(new { error = "kind must be one of: custom, builtin, plugin, all." });
            }

            var rows = await skills.ListAsync(filter, q, null, before, take ?? 50, ct);
            return Results.Ok(rows.Select(Dto));
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("List skills, a page at a time")
            .WithDescription(
                "Newest row first, keyed by `id`: pass the last row's `id` as `before` for the next "
                + "page. Custom skills by default. Each row carries `name`, `description`, `roles`, "
                + "`kind` (`builtin`, `custom` or `plugin`), `body`, `updatedAt`, `updatedBy` (null for a "
                + "built-in or a plugin's skill), `source` (the plugin id of a plugin's skill, else null) and "
                + "`team` (the team a team skill belongs to, else null). Team skills are custom skills and "
                + "are listed beside the instance-wide ones.");

        app.MapGet("/api/skills/{name}", async (
            [Description("The skill's name.")] string name, ISkillStore skills, CancellationToken ct) =>
            await skills.GetAsync(name, ct) is { } skill
                ? Results.Ok(Dto(skill))
                : Results.NotFound(new { error = $"There is no skill named '{name}'." }))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Read one instance-wide skill, built-in or custom")
            .WithDescription("A team's skill is read through `/api/teams/{team}/skills`.");

        app.MapPost("/api/skills", async (
            SkillWrite request, HttpContext context, ISkillStore skills, SkillDirectory directory,
            TeamRegistry teams, TenantLogging audit, CancellationToken ct) =>
            await CreateAsync(request.Name ?? "", request, context, skills, directory, teams, audit, ct))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Create a custom skill, named in the body")
            .WithDescription(
                "The same as `POST /api/skills/{name}` with `name` carried in the body. `roles` is "
                + "required. 201 with the skill; 409 when the name is a built-in skill's or another "
                + "custom skill's; 400 for an illegal or missing name, a missing description, body "
                + "or role, or an unknown role.");

        app.MapPost("/api/skills/{name}", async (
            [Description("The new custom skill's name.")] string name,
            SkillWrite request, HttpContext context, ISkillStore skills, SkillDirectory directory,
            TeamRegistry teams, TenantLogging audit, CancellationToken ct) =>
            await CreateAsync(name, request, context, skills, directory, teams, audit, ct))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Create a custom skill")
            .WithDescription(
                "`roles` is required. 201 with the skill; 409 when the name is a built-in skill's or "
                + "another custom skill's, or begins `plugin-`; 400 for an illegal name, a missing description, body or "
                + "role, or an unknown role.");

        app.MapPut("/api/skills/{name}", async (
            [Description("The custom skill's current name.")] string name,
            SkillWrite request, HttpContext context, ISkillStore skills, SkillDirectory directory,
            TeamRegistry teams, TenantLogging audit, CancellationToken ct) =>
        {
            var existing = await skills.GetAsync(name, ct);
            if (existing is null)
            {
                return Results.NotFound(new { error = $"There is no custom skill named '{name}'." });
            }

            if (existing.IsLocked)
            {
                return Refused(existing, "edited or relabelled");
            }

            Skill? updated;
            try
            {
                updated = await skills.UpdateCustomAsync(
                    name,
                    new SkillDraft(
                        string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
                        request.Description ?? existing.Description,
                        request.Roles is { Count: > 0 } roles ? roles : existing.Roles,
                        request.Body ?? existing.Body),
                    WhoIs(context.User), ct);
            }
            catch (SkillRefusedException refused)
            {
                return Results.Conflict(new { error = refused.Message });
            }
            catch (ArgumentException invalid)
            {
                return Results.BadRequest(new { error = invalid.Message });
            }

            if (updated is null)
            {
                return Results.NotFound(new { error = $"There is no custom skill named '{name}'." });
            }

            await AfterWriteAsync(skills, directory, teams, ct);
            await audit.WriteAsync(
                context, TenantActions.SkillChanged, null, null,
                new { name, to = updated.Name, roles = updated.Roles }, ct);

            return Results.Ok(Dto(updated));
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Edit or rename a custom skill")
            .WithDescription(
                "Any field left out keeps its value; a different `name` renames the skill. 200 with "
                + "the skill; 403 for a built-in skill, which changes only with the product, or a "
                + "plugin's skill, which changes only with its plugin; 404 when there is no such custom "
                + "skill; 409 when the new name is taken or begins `plugin-`.");

        app.MapDelete("/api/skills/{name}", async (
            [Description("The custom skill to delete.")] string name,
            HttpContext context, ISkillStore skills, SkillDirectory directory, TeamRegistry teams,
            TenantLogging audit, CancellationToken ct) =>
        {
            if (await skills.GetAsync(name, ct) is { IsLocked: true } locked)
            {
                return Refused(locked, "deleted");
            }

            if (!await skills.DeleteCustomAsync(name, ct))
            {
                return Results.NotFound(new { error = $"There is no custom skill named '{name}'." });
            }

            await AfterWriteAsync(skills, directory, teams, ct);
            await audit.WriteAsync(context, TenantActions.SkillDeleted, null, null, new { name }, ct);

            return Results.NoContent();
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Delete a custom skill")
            .WithDescription("204; 403 for a built-in skill or a plugin's skill; 404 when there is no such custom skill.");

        app.MapGet("/api/me/skills", async (
            [Description("Words to search for. Omit to list every skill offered to you.")] string? q,
            HttpContext context, ISkillStore skills, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

            var role = RoleOf(principal);
            var rows = role is null
                ? await skills.ListAsync(SkillKindFilter.All, q, null, null, 200, ct)
                : await skills.ListOfferedAsync(role, TeamOf(principal), q, 200, ct);

            return Results.Ok(rows
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new { s.Name, s.Description, s.Roles }));
        })
            .WithTags(Area)
            .RequirePermit(Permits.Skills)
            .WithSummary("The skills offered to the caller's role")
            .WithDescription(
                "What the MCP tool skills_search answers. The role is the credential's: the tenant "
                + "Concierge, a team's Manager, or a member. A team's own skills are listed only to "
                + "that team's members of their roles.");

        app.MapGet("/api/me/skills/{name}", async (
            [Description("The skill to load.")] string name,
            HttpContext context, ISkillStore skills, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

            var role = RoleOf(principal);
            var callerTeam = TeamOf(principal);
            var found = await skills.FindAllAsync(name, ct);

            // A person sees every skill; an agent its team's own, else the instance-wide one.
            var skill = role is null
                ? found.FirstOrDefault()
                : found.FirstOrDefault(s => s.Team is not null && callerTeam is not null
                        && string.Equals(s.Team, callerTeam, StringComparison.OrdinalIgnoreCase))
                    ?? found.FirstOrDefault(s => s.Team is null);

            if (skill is null && found is [var teamSkill, ..])
            {
                // Another team's skill. A container is bound to its own team, so the sentence does
                // not name the other one; the Concierge, who reaches every team, is told which.
                return Results.Json(
                    new
                    {
                        error = callerTeam is not null
                            ? $"The skill '{teamSkill.Name}' belongs to another team and is offered only to "
                                + "that team's members, so it was not loaded."
                            : $"The skill '{teamSkill.Name}' belongs to team {teamSkill.Team} and is offered only to "
                                + "that team's members, so it was not loaded.",
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (skill is null)
            {
                return Results.NotFound(new
                {
                    error = $"There is no skill named '{name}'. The skills_search tool lists the ones offered to you.",
                });
            }

            if (role is not null && !SkillRoles.Offers(skill.Roles, role))
            {
                return Results.Json(
                    new
                    {
                        error = $"The skill '{skill.Name}' is not offered to the {role} role, so it "
                            + $"was not loaded. It is for: {string.Join(", ", skill.Roles)}.",
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Text(Render(skill), "text/plain");
        })
            .WithTags(Area)
            .RequirePermit(Permits.Skills)
            .WithSummary("Load one skill offered to the caller's role")
            .WithDescription(
                "What the MCP tool skills_get answers: the skill's name, description and roles, then "
                + "its body. 403 with a sentence naming the role when the skill is not offered to it, "
                + "and with a sentence when it is another team's skill.");

        MapTeamSkills(app);
    }

    /// <summary>Team settings → Skills: a person's list, create, edit and delete of one team's skills.</summary>
    private static void MapTeamSkills(WebApplication app)
    {
        app.MapGet("/api/teams/{team}/skills", async (
            [Description("The team.")] string team, TeamSkills teamSkills, CancellationToken ct) =>
            await teamSkills.ListAsync(team, ct) is { } rows
                ? Results.Ok(rows.Select(Dto))
                : Results.NotFound(new { error = $"There is no team named '{team}'." }))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("List a team's own skills")
            .WithDescription(
                "The team's skills by name, each shaped as `GET /api/skills` shapes a row, with `team` set. "
                + "A team skill is offered only to that team's members of its roles.");

        app.MapPost("/api/teams/{team}/skills", async (
            [Description("The team.")] string team,
            SkillWrite request, HttpContext context, TeamSkills teamSkills, CancellationToken ct) =>
        {
            if (request.Roles is not { Count: > 0 })
            {
                return Results.BadRequest(new
                {
                    error = $"A team skill must say who it is for: roles, any of {string.Join(", ", SkillRoles.All)}.",
                });
            }

            var draft = new SkillDraft(request.Name ?? "", request.Description ?? "", request.Roles, request.Body ?? "");

            return await TeamWriteAsync(team, teamSkills, async stored =>
            {
                var created = await teamSkills.CreateAsync(
                    stored, draft, WhoIs(context.User),
                    TenantLogging.Row(context, TenantActions.SkillCreated, stored, draft.Name,
                        new { team = stored, name = draft.Name, roles = draft.Roles }), ct);

                return Results.Created($"/api/teams/{stored}/skills/{created.Name}", Dto(created));
            });
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Create a team skill")
            .WithDescription(
                "`name`, `description`, `roles` and `body` as for `POST /api/skills`. 201 with the skill; "
                + "404 for no such team; 409 when the team already has the name, or it is a built-in's, a "
                + "plugin's or an instance-wide custom skill's, or begins `plugin-`; 400 for an illegal name, a "
                + "missing description, body or role, or an unknown role. The tenant row lands with it.");

        app.MapPut("/api/teams/{team}/skills/{name}", async (
            [Description("The team.")] string team,
            [Description("The team skill's current name.")] string name,
            SkillWrite request, HttpContext context, TeamSkills teamSkills, ISkillStore skills, CancellationToken ct) =>
            await TeamWriteAsync(team, teamSkills, async stored =>
            {
                var existing = (await skills.ListTeamAsync(stored, ct))
                    .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing is null) return NoTeamSkill(stored, name);

                var draft = new SkillDraft(
                    string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
                    request.Description ?? existing.Description,
                    request.Roles is { Count: > 0 } roles ? roles : existing.Roles,
                    request.Body ?? existing.Body);

                var updated = await teamSkills.UpdateAsync(
                    stored, existing.Name, draft, WhoIs(context.User),
                    TenantLogging.Row(context, TenantActions.SkillChanged, stored, existing.Name,
                        new { team = stored, name = existing.Name, to = draft.Name, roles = draft.Roles }), ct);

                return updated is null ? NoTeamSkill(stored, name) : Results.Ok(Dto(updated));
            }))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Edit or rename a team skill")
            .WithDescription(
                "Any field left out keeps its value; a different `name` renames it. 200 with the skill; 404 "
                + "for no such team or team skill; 409 when the new name is taken; 400 as for create.");

        app.MapDelete("/api/teams/{team}/skills/{name}", async (
            [Description("The team.")] string team,
            [Description("The team skill to delete.")] string name,
            HttpContext context, TeamSkills teamSkills, CancellationToken ct) =>
            await TeamWriteAsync(team, teamSkills, async stored =>
                await teamSkills.DeleteAsync(
                    stored, name,
                    TenantLogging.Row(context, TenantActions.SkillDeleted, stored, name, new { team = stored, name }), ct)
                    ? Results.NoContent()
                    : NoTeamSkill(stored, name)))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Delete a team skill")
            .WithDescription("204; 404 for no such team or team skill. The tenant row lands with it.");
    }

    /// <summary>One team skill write: 404 for no team, the store's refusals as 409 and 400.</summary>
    private static async Task<IResult> TeamWriteAsync(
        string team, TeamSkills teamSkills, Func<string, Task<IResult>> write)
    {
        if (teamSkills.TeamOf(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"There is no team named '{team}'." });
        }

        try
        {
            return await write(stored);
        }
        catch (SkillRefusedException refused)
        {
            return Results.Conflict(new { error = refused.Message });
        }
        catch (ArgumentException invalid)
        {
            return Results.BadRequest(new { error = invalid.Message });
        }
        catch (KeyNotFoundException missing)
        {
            return Results.NotFound(new { error = missing.Message });
        }
    }

    private static IResult NoTeamSkill(string team, string name) =>
        Results.NotFound(new { error = $"Team {team} has no skill named '{name}'." });


    /// <summary>What an agent reads: a short header, then the body.</summary>
    public static string Render(Skill skill) =>
        $"""
        ---
        name: {skill.Name}
        description: {skill.Description}
        roles: {string.Join(", ", skill.Roles)}
        ---

        {skill.Body}
        """;

    private static object Dto(Skill skill) => new
    {
        skill.Id,
        skill.Name,
        skill.Description,
        skill.Roles,
        kind = KindName(skill.Kind),
        skill.Body,
        skill.UpdatedAt,
        skill.UpdatedBy,
        skill.Source,
        skill.Team,
    };

    public static string KindName(SkillKind kind) => kind switch
    {
        SkillKind.BuiltIn => "builtin",
        SkillKind.Plugin => "plugin",
        _ => "custom",
    };

    /// <summary>403 for a LOCKED skill - a built-in or a plugin's - naming which it is.</summary>
    private static IResult Refused(Skill skill, string verb) =>
        Results.Json(
            new
            {
                error = skill.Kind == SkillKind.Plugin
                    ? PluginSkillNames.Locked(skill.Name, verb)
                    : $"'{skill.Name}' is a built-in skill. Built-in skills change only with the product, "
                        + $"so it cannot be {verb}.",
            },
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Every prompt lists its role's skills, so a write re-prompts every container.</summary>
    private static async Task AfterWriteAsync(
        ISkillStore skills, SkillDirectory directory, TeamRegistry teams, CancellationToken ct)
    {
        await directory.RefreshAsync(skills, ct);
        await teams.RepromptAllAsync(ct);
    }

    private static string? WhoIs(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    private static bool TryParseKind(string? value, out SkillKindFilter kind)
    {
        switch ((value ?? "custom").Trim().ToLowerInvariant())
        {
            case "" or "custom":
                kind = SkillKindFilter.Custom;
                return true;
            case "builtin" or "built-in":
                kind = SkillKindFilter.BuiltIn;
                return true;
            case "all":
                kind = SkillKindFilter.All;
                return true;
            case "plugin":
                kind = SkillKindFilter.Plugin;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
