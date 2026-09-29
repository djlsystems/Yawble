using System.ComponentModel;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host.Solutions;

/// <summary>
/// Solution packages over <c>/api</c>. Only the check for now; see <c>docs/solutions.md</c>.
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

            var answer = solutions.Check(request.Folder);

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
                + "link leaving it. A person and the Concierge only; a member is refused with 403.");
    }

    /// <summary>The route's answer, and the CLI's (through <c>--solution-check</c>): one shape.</summary>
    public static object Body(SolutionCheck check) => check.Ok
        ? new { ok = true, folder = check.Folder, plan = check.Plan, refusals = Array.Empty<SolutionRefusal>() }
        : new { ok = false, folder = check.Folder, plan = (SolutionPlan?)null, refusals = check.Refusals };
}

/// <summary>Body of <c>POST /api/solutions/check</c>.</summary>
public sealed record SolutionCheckRequest(
    [property: Description("The absolute path of the package folder, inside the data root.")]
    string? Folder);
