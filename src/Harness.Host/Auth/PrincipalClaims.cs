using System.Security.Claims;
using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// The one translation between our Principal and ASP.NET's ClaimsPrincipal.
///
/// Both schemes go through here, which is what makes "nothing downstream knows which arrived" true
/// rather than aspirational.
/// </summary>
public static class PrincipalClaims
{
    public const string KindClaim = "harness:kind";
    public const string PermitClaim = "harness:permit";

    /// <summary>
    /// The user a machine principal ACTS AS - <see cref="Principal.OwnerUserId"/> carried onto the
    /// ticket. Absent for a <c>User</c> sign-in and for a <c>Container</c>, which has authority of
    /// its own.
    ///
    /// This claim never rides a cookie: only a `User` sign-in produces one, and a `User` has no
    /// owner, so no owner claim is ever emitted there. It exists only on a machine principal's
    /// ticket, and `ApiKeyAuthenticationHandler` builds that ticket from
    /// `IPrincipalStore.ResolveAsync` fresh on EVERY request - so the claim is microseconds old.
    /// Only the owner's IDENTITY travels on it; whether that owner still exists is resolved from
    /// the database on every request inside `TeamAccess.EffectiveTeamsAsync`, which is what makes
    /// a deleted owner take effect on the very next call. See `PermitGate`'s doc comment for the
    /// same argument made about the permit claims above.
    /// </summary>
    public const string OwnerClaim = "harness:owner";

    public static ClaimsPrincipal ToClaimsPrincipal(
        Principal principal, string scheme, string? email = null)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, principal.Id),
            new(KindClaim, principal.Kind.ToString()),
            .. principal.Permits.Select(p => new Claim(PermitClaim, p)),
        ];

        if (principal.OwnerUserId is { } owner) claims.Add(new Claim(OwnerClaim, owner));

        if (email is not null) claims.Add(new Claim(ClaimTypes.Email, email));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    public static Principal? From(ClaimsPrincipal claims)
    {
        var id = claims.FindFirstValue(ClaimTypes.NameIdentifier);
        var kind = claims.FindFirstValue(KindClaim);

        if (id is null || kind is null) return null;

        // Guarded for the same reason SqlitePrincipalStore.ResolveAsync is, and found while fixing
        // that one: a kind we cannot read is a refusal. This method already answers null for a
        // ticket it cannot make sense of, so there is an honest answer available - while Enum.Parse
        // threw out of middleware, which reached the caller as a 500 where 401 is correct.
        if (!Enum.TryParse<PrincipalKind>(kind, out var parsedKind)) return null;

        return new Principal(
            id,
            parsedKind,
            new HashSet<string>(
                claims.FindAll(PermitClaim).Select(c => c.Value), StringComparer.Ordinal),
            claims.FindFirstValue(OwnerClaim));
    }
}
