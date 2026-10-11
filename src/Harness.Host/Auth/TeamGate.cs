using Harness.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Host.Auth;

/// <summary>
/// One gate, keyed on the route's team value. It binds MACHINE principals to the team their
/// identity names; a person passes, because every person reaches every team.
///
/// A per-route filter is the alternative and is refused: its failure mode is silence, so a new
/// team route without the filter is ungated and nothing complains, and it would be a second
/// mechanism answering the question this one already answers first. Every team-scoped route is
/// gated by EXISTING, without a route list to maintain.
///
/// Keyed on ROUTE DATA, not on parsing the URL path: route data couples the gate to STRUCTURE - a
/// route is gated because its template declares `{team}`, the same declaration that lets its
/// handler read a team at all, so a handler cannot be written that acts on a path-supplied team
/// without also feeding the gate. Parsing the path instead would couple it to SPELLING - "the path
/// must literally read /api/teams/{x}/..." - and a future route like
/// /api/admin/teams/{team}/members would declare and bind {team} while this gate quietly never sees
/// it. That is the exact silent failure a per-route filter is refused for above; a path-parsing
/// gate would reintroduce it under a different name. (A verb with no matching handler resolves to
/// ASP.NET's synthetic 405 endpoint and carries no route values, so such a request passes through
/// via Rule 1 - but routing knows nothing about teams, so a 405 answers identically whether the
/// team is the caller's, someone else's, or never existed. It is not an oracle: a mutating route
/// must be hit with its real verb to exercise this gate at all, which is why TeamGateTests does.)
/// </summary>
public static class TeamGate
{
    /// <summary>
    /// The phrase itself, separated from the JSON body below because it is not only the gate's:
    /// <c>ContainerHub.JoinTeam</c> throws a bare <c>HubException</c> carrying this exact text
    /// rather than a JSON body, and the two staying byte-identical is a property this refusal
    /// design goes to deliberate trouble for - see the gate's own doc comment. A hand-typed copy at
    /// the hub call site would match today by discipline rather than by structure.
    /// </summary>
    public const string NoSuchTeamMessage = "No such team.";

    public const string NoSuchTeam = $$"""{"error":"{{NoSuchTeamMessage}}"}""";

    public const string TeamCreationNeedsOwnerMessage =
        "This machine principal cannot create teams because it has no owner. "
        + PermitGate.NeedsAPerson;

    public const string TeamCreationNeedsOwner =
        $$"""{"error":"{{TeamCreationNeedsOwnerMessage}}"}""";

    public const string TeamCreationOwnerMissingMessage =
        "This machine principal cannot create teams because its owner no longer exists. "
        + PermitGate.NeedsAPerson;

    public const string TeamCreationOwnerMissing =
        $$"""{"error":"{{TeamCreationOwnerMissingMessage}}"}""";

    public static void Use(WebApplication app) => app.Use(async (context, next) =>
    {
        // 1. No team in the route: nothing to check. This is what keeps a request matching NO
        //    endpoint - which the fallback policy challenges - out of the gate's way.
        if (context.GetRouteData().Values["team"] is not string team)
        {
            await next();
            return;
        }

        // 2. Not authenticated: the fallback policy has already refused. Do not answer twice.
        if (PrincipalClaims.From(context.User) is not { } principal)
        {
            await next();
            return;
        }

        // 3. A PERSON REACHES EVERY TEAM. There is no per-team access list, so there is no set to
        //    consult, and a person naming a team that does not exist reaches the handler and gets
        //    its ordinary 404 rather than a permission-shaped refusal for a typo.
        if (principal.Kind == PrincipalKind.User)
        {
            await next();
            return;
        }

        // 4. A machine principal is bound by its identity: a container to its own team, a
        //    Concierge to its team or the tenant, a key to its owner's reach. Refused with the
        //    same body a missing team would get, so a credential probing names cannot tell "not
        //    yours" from "not there".
        var access = context.RequestServices.GetRequiredService<TeamAccess>();

        if (await access.MayActOnAsync(principal, team, context.RequestAborted))
        {
            await next();
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(NoSuchTeam, context.RequestAborted);
    });

    /// <summary>
    /// Team creation authority is inherited, never held: a person may create a team, and a machine
    /// principal may create one only when it acts as a person who still exists.
    /// </summary>
    public static TBuilder RequireTeamCreationAuthority<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var principal = PrincipalClaims.From(context.HttpContext.User);

            if (principal is null) return Results.Unauthorized();

            // A PERSON MAY CREATE A TEAM. A machine principal may only when it has an owner who
            // still exists.
            if (principal.Kind == PrincipalKind.User) return await next(context);

            if (principal.OwnerUserId is not { } ownerId)
            {
                return Results.Content(TeamCreationNeedsOwner, "application/json", statusCode: 403);
            }

            var users = context.HttpContext.RequestServices.GetRequiredService<IUserStore>();

            if (await users.FindByIdAsync(ownerId, context.HttpContext.RequestAborted) is null)
            {
                return Results.Content(TeamCreationOwnerMissing, "application/json", statusCode: 403);
            }

            return await next(context);
        });

        return builder;
    }
}
