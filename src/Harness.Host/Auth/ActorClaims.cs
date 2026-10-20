using System.Security.Claims;

namespace Harness.Host.Auth;

/// <summary>Who is acting, as the two claims every tenant row names: the principal's id and its email, either null when absent.</summary>
public static class ActorClaims
{
    public static (string? Id, string? Email) Of(ClaimsPrincipal user) =>
        (user.FindFirstValue(ClaimTypes.NameIdentifier), user.FindFirstValue(ClaimTypes.Email));
}
