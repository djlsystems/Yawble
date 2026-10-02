using System.Security.Claims;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// A PLATFORM CALL UNDER A CONCIERGE SESSION'S OWN CREDENTIAL IS THAT SESSION'S ACTIVITY, from the
/// moment it begins until it ends - a fifteen-minute workflow wait included. Every REST route and
/// <c>/mcp</c> alike pass through here after authentication.
/// </summary>
/// <remarks>
/// In memory, as the sessions are: both die with the Host. <c>principals.last_used_at</c> stays what
/// the credential roster shows; it sees only when a request began, so a long wait shorter than the
/// window would read as idle half-way through.
/// </remarks>
public static class ConciergeCallClock
{
    /// <summary>Puts the clock in <paramref name="app"/>'s pipeline. After authentication, which is what names the caller.</summary>
    public static void Use(IApplicationBuilder app) =>
        app.Use((context, next) => InvokeAsync(
            context, next, context.RequestServices.GetRequiredService<ConciergeSessionStore>()));

    /// <summary>
    /// Runs <paramref name="next"/>, counting it as a call of the caller's session while it runs.
    /// The count is released however the call ends: returned, thrown or aborted.
    /// </summary>
    public static async Task InvokeAsync(HttpContext context, Func<Task> next, ConciergeSessionStore sessions)
    {
        using var call = SessionOf(context.User) is { } key ? sessions.CallBegan(key) : null;

        await next();
    }

    /// <summary>
    /// The session whose own credential made this request, or null for anyone else. Only the
    /// session's credential, minted under <see cref="ConciergeLaunchFactory.PrincipalId"/> for its
    /// owner - never a person, a member, or another key that acts as that person.
    /// </summary>
    public static ConciergeSessionKey? SessionOf(ClaimsPrincipal user) =>
        PrincipalClaims.From(user) is { Kind: PrincipalKind.TenantConcierge, OwnerUserId: { } owner } principal
            && string.Equals(principal.Id, ConciergeLaunchFactory.PrincipalId(owner), StringComparison.Ordinal)
                ? new ConciergeSessionKey(owner)
                : null;
}
