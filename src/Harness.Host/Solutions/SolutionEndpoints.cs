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

        app.MapGet("/api/solutions/installed", async (SolutionPanels panels, CancellationToken ct) =>
            Results.Ok(await panels.TilesAsync(ct)))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Every team installed from a solution package: the Solutions launcher")
            .WithDescription(
                "200 `[{ team, teamName, id, name, version, installedAt, updatedAt, installedBy, plugins, folder, "
                + "primarySite, status, state, paused }]`, by team name: each team's package id and version, "
                + "which the install wizard offers to update, and its launcher tile. `primarySite` is "
                + "`{ name, url, published }` for the package's `panel.primarySite`, or null. `status` is the "
                + "package's `panel.status` filled in - plain text, never markup - or, without one, the last "
                + "run and the state. `state` is `{ kind, reason }`: `paused`, `blocked` (naming what the team "
                + "waits for), `running`, `capped` (a trigger reached its daily cap today, measured spend "
                + "only) or `idle`, the first that holds.");

        app.MapPost("/api/solutions/preview", async (SolutionPreviewRequest request, SolutionInstaller installer, CancellationToken ct) =>
            Answer(await installer.PreviewAsync(request.Folder, request.Team, ct)))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("What installing a solution package would do")
            .WithDescription(
                "Writes nothing. Body `{ folder, team? }`. A new team: 200 `{ ok: true, mode: \"install\", "
                + "teamName, nameRefusal, plan, connections, reinstallable }`, where `nameRefusal` says why "
                + "that name cannot be used, or null, and `reinstallable` lists each team `{ team, teamName, "
                + "version, uninstalledAt }` that kept an uninstalled install of this package id. `team` "
                + "naming one of those: the same with `reinstall: { team, teamName, from }` and `previous: "
                + "{ settings: [{ member, setting, value }], connections: [{ member, slot, connection }] }`, "
                + "the person's earlier answers that still apply, to prefill. `team` naming a team installed from an earlier version of the same "
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
                + "check; 400 `{ error }` for a refused folder or answer - a connection lacking scopes its "
                + "slot needs adds `reconnect: { connectionId, scopes }`, the first such binding and exactly "
                + "the scopes it lacks; 409 for a team name that is taken. `teamName` naming a team with no "
                + "package installed that kept an uninstalled install of this same package id installs over "
                + "it: its kept sites are published again with their data, its members and triggers made "
                + "again, and the answer carries `reinstalledFrom`, the version it held. A person only.");

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

        app.MapGet("/api/teams/{team}/solution/panel", async (
            [Description(Describe.Team)] string team, SolutionPanels panels, CancellationToken ct) =>
            await panels.PanelAsync(team, ct) is { } panel
                ? Results.Ok(panel)
                : Results.NotFound(new { error = $"'{team}' was not installed from a solution package." }))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("A solution's control panel: its status, controls, results and maintenance")
            .WithDescription(
                "Writes nothing. 200 with the launcher tile's fields and `description`, `members` (each "
                + "`{ packageName, member, kind, role, state, blocked, failed, needsDecision, queueDepth, "
                + "lastRun }`), `triggers` (each the Triggers dialog's own view - `spentToday` is measured "
                + "billable tokens with `measuredRuns` and `unmeasuredRuns`, never estimated - plus "
                + "`packageName`, `packageKind` and `runNow`), `blocked` (each `{ kind, name, member, "
                + "packageMember, description, reason, fix }`, `fix` an `upload` folder or a `connection` "
                + "member and slot), `settings` (the package's `panel.settings`, first in the panel), "
                + "`outputs` (each `panel.outputs` folder's files newest first, with a `download` link) and "
                + "`recentRuns`. Every control on the panel is an existing route: pause and resume, Run "
                + "now, a trigger's PATCH, plugin settings, the documents upload and the wizard's update. "
                + "Package text is text: render it as text, never HTML. 404 for a team not installed from "
                + "a package, or no such team.");

        app.MapPost("/api/teams/{team}/solution/uninstall", async (
            [Description(Describe.Team)] string team, SolutionUninstallBody? request, SolutionInstaller installer,
            PluginRemover remover, HttpContext context, CancellationToken ct) =>
            Answer(await installer.UninstallAsync(team, request?.RemovePlugins ?? false, ActorOf(context), remover, ct)))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Uninstall the solution a team was installed from")
            .WithDescription(
                "Body `{ removePlugins?: false }`. Removes the team's triggers, members (the Manager stays, "
                + "with the package's instructions cleared), team skills and tools folder (`solution."
                + "uninstalled`). Takes the package's sites offline and KEEPS them, with every version and "
                + "all their data, and keeps the record that the team held the package, with the person's "
                + "answers, so installing the same package onto this team publishes them again. KEEPS the "
                + "team and its documents. Deleting the team removes the kept sites and data. With `removePlugins: true`, each of the package's plugins is removed when no "
                + "other team has a member on it. 200 `{ ok, team, teamName, id, version, removed: { "
                + "triggers, members, skills, tools }, sitesKept, plugins: { removed, kept: [{ id, usedBy }] }, "
                + "documentsKept, failures }`; `ok` is false when something could not be removed, each named "
                + "in `failures`. Asking first is the caller's: this acts when called. 404 for a team not "
                + "installed from a package. A person only.");
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

/// <summary>Body of <c>POST /api/teams/{team}/solution/uninstall</c>.</summary>
public sealed record SolutionUninstallBody(
    [property: Description("True to remove each of the package's plugins that no other team has a member on. Defaults to false.")]
    bool? RemovePlugins = null);
