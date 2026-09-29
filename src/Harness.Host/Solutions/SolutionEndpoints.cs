using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host.Solutions;

/// <summary>
/// Solution packages over <c>/api</c>: the check (a person and the Concierge), and the install, the
/// update and what they read (a person only: an agent never installs). See <c>docs/solutions.md</c>.
/// </summary>
public static class SolutionEndpoints
{
    private const string Area = "Solutions";

    /// <summary>What a member hears: the check is for the people and the Concierge who decide to
    /// install, and a member has no part in that.</summary>
    public const string MemberRefusal =
        "Checking a solution package is for a person or the Concierge. A member hands its package back, "
        + "and the Manager's delivery names the folder.";

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/solutions/check", (SolutionCheckRequest request, SolutionService solutions, HttpContext context) =>
        {
            // THE PERMIT BOUNDS EVERY MACHINE PRINCIPAL BUT THE CONCIERGE: `CreateTeam` is the
            // Concierge's and never a team's. A member is refused here too, whatever it was granted,
            // so the rule does not hang on nobody ever granting it that permit.
            if (PrincipalClaims.From(context.User) is { Kind: PrincipalKind.Container })
            {
                return Results.Json(new { error = MemberRefusal }, statusCode: StatusCodes.Status403Forbidden);
            }

            var answer = solutions.Check(request.Folder, fromLink: string.Equals(request.From, "link", StringComparison.Ordinal));

            if (answer.FolderRefused) return Results.BadRequest(new { error = answer.Error });

            return Results.Ok(Body(answer.Check!));
        })
            .WithTags(Area)
            .RequirePermit(Permits.CreateTeam)
            .WithSummary("Check a solution package without installing it")
            .WithDescription(
                "Body `{ \"folder\": \"<absolute folder inside the data root>\" }`: the folder holding "
                + "`solution.json`. Nothing is written. 200 `{ ok: true, plan }` with what an install would "
                + "create - package, team, members, plugins, triggers (full instruction text, wake setting, "
                + "daily cap), skills (with their bodies), sites, tools and the person's inputs - or 200 "
                + "`{ ok: false, refusals: [{ file, field, reason }] }`, each naming the file and the field. "
                + "400 for a folder that is not absolute, is outside the data root or is reached through a "
                + "link leaving it, and - with `from: \"link\"`, which the deep link sends - for a folder "
                + "outside the instance's documents and every team's folder. A person and the Concierge "
                + "only; a member is refused with 403.");

        app.MapGet("/api/solutions/installed", async (SolutionInstaller installer, CancellationToken ct) =>
            Results.Ok(await installer.InstalledAsync(ct)))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Every team installed from a solution package")
            .WithDescription(
                "200 `[{ team, teamName, id, name, version, installedAt, updatedAt, installedBy, plugins }]`: "
                + "each team's package id and version, which the install wizard offers to update.");

        app.MapPost("/api/solutions/preview", async (SolutionPreviewRequest request, SolutionInstaller installer, CancellationToken ct) =>
            Answer(await installer.PreviewAsync(request.Folder, request.Team, ct)))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("What installing a solution package would do")
            .WithDescription(
                "Writes nothing. Body `{ folder, team? }`. A new team: 200 `{ ok: true, mode: \"install\", "
                + "teamName, nameRefusal, plan, connections }`, where `nameRefusal` says why that name cannot "
                + "be used, or null. `team` naming a team installed from an earlier version of the same "
                + "package: 200 `{ ok: true, mode: \"update\", team, teamName, from, to, plan, diff, "
                + "connections }`, the diff naming the members, triggers, skills, sites, tools and plugins "
                + "added, changed and removed. A team that cannot be updated from it: 200 `{ ok: false, "
                + "error }`. A package that fails its check: 200 `{ ok: false, refusals }`. 400 for a "
                + "refused folder.");

        app.MapPost("/api/solutions/install", async (
            SolutionInstallBody request, SolutionInstaller installer, HttpContext context, CancellationToken ct) =>
            Answer(await installer.InstallAsync(
                new SolutionInstallRequest(
                    request.Folder, request.TeamName, request.Agent, request.LocalRepository,
                    new SolutionAnswers(request.Settings, request.Connections)),
                ActorOf(context), ct)))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Install a solution package as a new team")
            .WithDescription(
                "Body `{ folder, teamName?, agent?, localRepository?, settings?: { <member>: { <setting>: "
                + "value } }, connections?: { <member>: { <slot>: <connection id> } } }`. In order: installs "
                + "the package's plugins, creates the team (with a new local repository unless "
                + "`localRepository: false`), hires the members, registers the team skills, copies `tools/` "
                + "to the team's `solution` folder, publishes the sites, creates the triggers and records "
                + "the package and version (`solution.installed`). 200 `{ ok: true, team, teamName, id, "
                + "version, missing, steps }`; `missing` names each required document or connection the "
                + "person skipped, which the team shows until provided. A step that fails: 200 `{ ok: "
                + "false, step, stepNumber, title, reason, undone, notUndone, steps }`, everything made "
                + "undone in reverse order. 200 `{ ok: false, refusals }` for a package that fails its "
                + "check; 400 for a refused folder or answer; 409 for a team name that is taken. A person "
                + "only.");

        app.MapPost("/api/solutions/update", async (
            SolutionUpdateBody request, SolutionInstaller installer, HttpContext context, CancellationToken ct) =>
            Answer(await installer.UpdateAsync(
                new SolutionUpdateRequest(request.Folder, request.Team, new SolutionAnswers(request.Settings, request.Connections)),
                ActorOf(context), ct)))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Update a team to a newer version of its solution package")
            .WithDescription(
                "Body `{ folder, team, settings?, connections? }`. Adds, changes and removes what the diff "
                + "(`POST /api/solutions/preview`) names, keeping the person's settings, bindings, uploaded "
                + "documents and site data (a site the package no longer has is unpublished, not deleted), "
                + "and appends `solution.updated`. The same answers as the install, with `from` and `diff`; "
                + "404 for no such team; 409 for a team not installed from this package or already on this "
                + "version or a newer one. A person only.");

        app.MapGet("/api/teams/{team}/solution", async (
            [Description(Describe.Team)] string team, SolutionInstaller installer, CancellationToken ct) =>
            await installer.DescribeAsync(team, ct) is { } described
                ? Results.Ok(described)
                : Results.NotFound(new { error = $"'{team}' was not installed from a solution package." }))
            .WithTags(Area)
            .RequirePermit(Permits.Read)
            .WithSummary("The solution package a team came from, and what it still waits for")
            .WithDescription(
                "200 `{ team, teamName, id, name, version, installedAt, updatedAt, installedBy, plugins, "
                + "missing: [{ kind, name, member, description }] }`. A team with anything `missing` is "
                + "blocked on it: a required document folder with no file in it, or a required connection "
                + "slot with none bound. 404 for a team not installed from a package, or no such team.");
    }

    private static IResult Answer(SolutionOutcome outcome) => Results.Json(outcome.Body, statusCode: outcome.Status);

    /// <summary>The person installing, as every row the install writes records them.</summary>
    public static SolutionActor ActorOf(HttpContext context) => new(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier),
        context.User.FindFirstValue(ClaimTypes.Email));

    /// <summary>The route's answer, and the CLI's (through <c>--solution-check</c>): one shape.</summary>
    public static object Body(SolutionCheck check) => check.Ok
        ? new { ok = true, folder = check.Folder, plan = check.Plan, refusals = Array.Empty<SolutionRefusal>() }
        : new { ok = false, folder = check.Folder, plan = (SolutionPlan?)null, refusals = check.Refusals };
}

/// <summary>Body of <c>POST /api/solutions/check</c>.</summary>
public sealed record SolutionCheckRequest(
    [property: Description("The absolute path of the package folder, inside the data root.")]
    string? Folder,
    [property: Description("`link` when a deep link asks: the folder must then be in the instance's documents or a team's folder.")]
    string? From = null);

/// <summary>Body of <c>POST /api/solutions/preview</c>.</summary>
public sealed record SolutionPreviewRequest(
    [property: Description("The absolute path of the package folder, inside the data root.")]
    string? Folder,
    [property: Description("The new team's name, or an existing team installed from an earlier version of this package.")]
    string? Team = null);

/// <summary>Body of <c>POST /api/solutions/install</c>.</summary>
public sealed record SolutionInstallBody(
    [property: Description("The absolute path of the package folder, inside the data root.")]
    string? Folder,
    [property: Description("The new team's name; the package's team name when omitted.")]
    string? TeamName = null,
    [property: Description("The Agent preset the package's agent members run when they name none; the instance's default when omitted.")]
    string? Agent = null,
    [property: Description("False to make no local repository for the team. Defaults to true.")]
    bool? LocalRepository = null,
    [property: Description("Package member name to its person-only settings, as the package's inputs ask.")]
    Dictionary<string, Dictionary<string, JsonElement>>? Settings = null,
    [property: Description("Package member name to slot to connection id, as the package's inputs ask.")]
    Dictionary<string, Dictionary<string, string>>? Connections = null);

/// <summary>Body of <c>POST /api/solutions/update</c>.</summary>
public sealed record SolutionUpdateBody(
    [property: Description("The absolute path of the newer package folder, inside the data root.")]
    string? Folder,
    [property: Description("The team to update.")]
    string? Team,
    [property: Description("Package member name to its person-only settings, for members the update adds.")]
    Dictionary<string, Dictionary<string, JsonElement>>? Settings = null,
    [property: Description("Package member name to slot to connection id, for members the update adds.")]
    Dictionary<string, Dictionary<string, string>>? Connections = null);
