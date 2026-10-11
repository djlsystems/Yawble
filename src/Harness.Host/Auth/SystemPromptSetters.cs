using System.Security.Claims;
using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// Names who is setting a member's own instructions, from the request.
///
/// A Container is a Manager hiring through the `member` tool - only a Manager holds
/// `CreateContainer` - and is named by its member id on the team. Every other principal acts as a
/// PERSON (a sign-in, or a Concierge or API key acting as its owner) and is named by email, the way
/// the tenant log and the backlog name people: the claim when the ticket carries one, else the
/// owner's, looked up.
/// </summary>
public static class SystemPromptSetters
{
    public static async Task<SystemPromptSetter?> ForAsync(
        HttpContext context, IUserStore users, CancellationToken ct)
    {
        if (PrincipalClaims.From(context.User) is not { } principal) return null;

        var now = DateTimeOffset.UtcNow;

        if (principal.Kind == PrincipalKind.Container)
        {
            return ContainerId.TryParse(principal.Id, out var member)
                ? new SystemPromptSetter(member.Name, SystemPromptSetter.Manager, now)
                : null;
        }

        var email = context.User.FindFirstValue(ClaimTypes.Email);

        if (string.IsNullOrWhiteSpace(email) && principal.OwnerUserId is { } owner)
        {
            email = (await users.FindByIdAsync(owner, ct))?.Email;
        }

        return new SystemPromptSetter(
            string.IsNullOrWhiteSpace(email) ? principal.OwnerUserId ?? principal.Id : email,
            SystemPromptSetter.Person,
            now);
    }
}
