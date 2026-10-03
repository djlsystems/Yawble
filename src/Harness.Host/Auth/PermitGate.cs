using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// What a MACHINE principal may cause, read off the matched endpoint's markers.
///
/// Sits immediately after <see cref="TeamGate"/> so "No such team." still wins, and the
/// byte-identity property that refusal goes to deliberate trouble for is untouched: a caller who
/// cannot see a team learns nothing new about it from a permit refusal that never runs.
///
/// It bounds machine principals ONLY. A person holds every permit, derived rather than stored.
///
/// WHY READING THE CLAIM IS SAFE HERE, when Program.cs says of this exact set: "Permits travel here
/// for TWO reasons, and governing access is not one of them ... Do not read it as authority
/// anywhere." That warning is about the COOKIE. A cookie lasts fourteen days and is a stale copy of
/// something revocable. A machine principal has no cookie: <see cref="ApiKeyAuthenticationHandler"/>
/// calls <c>IPrincipalStore.ResolveAsync</c> on EVERY request and builds the ticket from the row it
/// just read, and that query selects id, kind and permits. So for the only principals this gate
/// bounds, the claim was loaded from the database microseconds ago and a revoked permit is refused
/// on the very next request.
///
/// That is a property of the AUTH PATH rather than of this gate, so it is pinned rather than
/// assumed - `A_revoked_permit_is_refused_on_the_next_request` in PermitGateTests is what fails
/// loudly if machine principals ever become cookie-authenticable. If that day comes the fix is a
/// `PermitsForAsync(id)` on IPrincipalStore mirroring `TeamForAsync`, deliberately not written now:
/// an unused query on every request is a real cost paid for a hazard that does not exist.
/// </summary>
public static class PermitGate
{
    /// <summary>
    /// THE SENTENCE THAT STOPS A MEMBER LOOKING FOR A CREDENTIAL, and it is here rather than
    /// hand-typed at each refusal so the boundaries a member can actually meet all say it the same
    /// way. <see cref="TeamGate"/> and <c>BacklogEndpoints</c> compose it into their own refusals.
    ///
    /// WHY A STATUS IS NOT ENOUGH. A bare 403 is read by a member as "wrong credential", so it
    /// goes looking for another credential and reports that the credentials are wrong. They are
    /// not: the boundary is correct and permanent, and there is no credential on the other side of
    /// it. With this sentence the member never starts the search, because the refusal already told
    /// it who has to act.
    ///
    /// IT PROMISES NOTHING AND WIDENS NOTHING. It is prose on a refusal; it changes neither what a
    /// gate refuses nor the status it answers.
    /// </summary>
    public const string NeedsAPerson =
        "A person has to do this, and no credential changes that - report what you need done and "
        + "who has to do it rather than looking for one.";

    public const string HumansOnlyMessage = $"That is a person's action. {NeedsAPerson}";

    /// <summary><see cref="HumansOnlyMessage"/> as the JSON body a handler answers with when it
    /// refuses a machine principal itself rather than through the marker.</summary>
    public const string HumansOnlyBody = $$"""{"error":"{{HumansOnlyMessage}}"}""";

    /// <summary>
    /// NOT <see cref="NeedsAPerson"/>, and the difference is real: a permit can be widened, so this
    /// boundary is not permanent the way HumansOnly is. What it shares with the others is the
    /// failure it causes - a member that reads "may not Read" goes looking for a wider credential
    /// to sign in with, and there is not one. So this names the verb (the fix is a configuration
    /// change somebody has to find) and then says where the fix lives: on THIS credential.
    /// </summary>
    public static string Missing(string permit) =>
        $"This credential may not {permit}. Permits are set on this credential, not on another one "
        + "you could sign in with - say what you need and ask for this one to be widened.";

    public static void Use(WebApplication app) => app.Use(async (context, next) =>
    {
        // 1. No matched endpoint: nothing declared anything, and an unmatched request is the
        //    fallback policy's business. Same as TeamGate's rule 1 and for the same reason.
        if (context.GetEndpoint() is not { } endpoint)
        {
            await next();
            return;
        }

        // 2. Not a machine principal - which includes anonymous, whose refusal the fallback policy
        //    already owns. A user passes every marker but HumansOnly by construction, because a
        //    user is not what any of them bound.
        if (PrincipalClaims.From(context.User) is not { Kind: not PrincipalKind.User } principal)
        {
            await next();
            return;
        }

        if (endpoint.Metadata.GetMetadata<HumansOnlyMarker>() is not null)
        {
            await Refuse(context, HumansOnlyMessage);
            return;
        }

        if (endpoint.Metadata.GetMetadata<HumansOrConciergeMarker>() is { } door
            && !(ConciergeLaunchFactory.IsConcierge(principal) && principal.May(door.Permit)))
        {
            await Refuse(context, HumansOnlyMessage);
            return;
        }

        if (endpoint.Metadata.GetMetadata<PermitRequirement>() is { } required
            && !principal.May(required.Permit))
        {
            // NAMED. The fix is a configuration change somebody has to find, exactly as with the
            // missing-Agent refusal that names both ways out of itself.
            await Refuse(context, Missing(required.Permit));
            return;
        }

        await next();
    });

    private static async Task Refuse(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }
}
