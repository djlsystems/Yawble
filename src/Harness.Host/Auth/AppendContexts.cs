using System.Security.Claims;
using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// THE ONE WAY AN ENDPOINT HANDS THE LOG ITS PRINCIPAL: an <see cref="AppendContext"/> built from the
/// request's authenticated <see cref="ClaimsPrincipal"/>, and from nothing a client wrote - no body
/// field, no header. The log derives the row's origin from it (<see cref="MessageOrigin"/>).
/// </summary>
public static class AppendContexts
{
    /// <summary>The append context for <paramref name="user"/>, or null when the ticket names no principal.</summary>
    public static AppendContext? From(ClaimsPrincipal user) =>
        PrincipalClaims.From(user) is { } principal
            ? new AppendContext(principal.Kind, principal.Id, principal.OwnerUserId, user.FindFirstValue(ClaimTypes.Email))
            : null;
}
