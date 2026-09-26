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
            [Description("`custom` (default), `builtin` or `all`.")] string? kind,
            [Description("Full-text search over name, description and body. Omit to list.")] string? q,
            [Description("Rows with an id below this; omit for the first page.")] long? before,
            [Description("Rows per page: default 50, at most 200.")] int? take,
            ISkillStore skills,
            CancellationToken ct) =>
        {
            if (!TryParseKind(kind, out var filter))
            {
                return Results.BadRequest(new { error = "kind must be one of: custom, builtin, all." });
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
                + "`kind` (`builtin` or `custom`), `body`, `updatedAt` and `updatedBy` (null for a "
                + "built-in).");

        app.MapGet("/api/skills/{name}", async (
            [Description("The skill's name.")] string name, ISkillStore skills, CancellationToken ct) =>
            await skills.GetAsync(name, ct) is { } skill
                ? Results.Ok(Dto(skill))
                : Results.NotFound(new { error = $"There is no skill named '{name}'." }))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Read one skill, built-in or custom");

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
                + "another custom skill's; 400 for an illegal name, a missing description, body or "
                + "role, or an unknown role.");

        app.MapPut("/api/skills/{name}", async (
            [Description("The custom skill's current name.")] string name,
            SkillWrite request, HttpContext context, ISkillStore skills, SkillDirectory directory,
            TeamRegistry teams, TenantLogging audit, CancellationToken ct) =>
        {
            if (BuiltInSkills.IsBuiltIn(name))
            {
                return Refused(name, "edited or relabelled");
            }

            var existing = await skills.GetAsync(name, ct);
            if (existing is null)
            {
                return Results.NotFound(new { error = $"There is no custom skill named '{name}'." });
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
                + "the skill; 403 for a built-in skill, which changes only with the product; 404 when "
                + "there is no such custom skill; 409 when the new name is taken.");

        app.MapDelete("/api/skills/{name}", async (
            [Description("The custom skill to delete.")] string name,
            HttpContext context, ISkillStore skills, SkillDirectory directory, TeamRegistry teams,
            TenantLogging audit, CancellationToken ct) =>
        {
            if (BuiltInSkills.IsBuiltIn(name))
            {
                return Refused(name, "deleted");
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
            .WithDescription("204; 403 for a built-in skill; 404 when there is no such custom skill.");

        app.MapGet("/api/me/skills", async (
            [Description("Words to search for. Omit to list every skill offered to you.")] string? q,
            HttpContext context, ISkillStore skills, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

            var role = RoleOf(principal);
            var rows = await skills.ListAsync(SkillKindFilter.All, q, role, null, 200, ct);

            return Results.Ok(rows
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new { s.Name, s.Description, s.Roles }));
        })
            .WithTags(Area)
            .RequirePermit(Permits.Skills)
            .WithSummary("The skills offered to the caller's role")
            .WithDescription(
                "What the MCP tool skills_search answers. The role is the credential's: the tenant "
                + "Concierge, a team's Manager, or a member.");

        app.MapGet("/api/me/skills/{name}", async (
            [Description("The skill to load.")] string name,
            HttpContext context, ISkillStore skills, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

            var skill = await skills.GetAsync(name, ct);
            if (skill is null)
            {
                return Results.NotFound(new
                {
                    error = $"There is no skill named '{name}'. The skills_search tool lists the ones offered to you.",
                });
            }

            if (RoleOf(principal) is { } role && !SkillRoles.Offers(skill.Roles, role))
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
                + "its body. 403 with a sentence naming the role when the skill is not offered to it.");
    }

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
        kind = skill.Kind == SkillKind.BuiltIn ? "builtin" : "custom",
        skill.Body,
        skill.UpdatedAt,
        skill.UpdatedBy,
    };

    private static IResult Refused(string name, string verb) =>
        Results.Json(
            new
            {
                error = $"'{name}' is a built-in skill. Built-in skills change only with the product, "
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
            default:
                kind = default;
                return false;
        }
    }
}
