using System.Security.Claims;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// Writes to the tenant log on behalf of a route, resolving WHO from the request.
///
/// A thin helper rather than a service with logic, and it exists for one reason: every call site
/// would otherwise repeat the same three steps - pull the principal off the context, find an email
/// to store beside the id, serialise a detail object - and the third is the one where a secret gets
/// into an audit trail by accident.
///
/// <b>The email is read from the CLAIM, not looked up.</b> Two reasons, and the second is the one
/// that matters: a lookup is a database round trip on every administrative write, and an actor who
/// has just deleted their own account cannot be looked up at all. The claim is minted at login and
/// is exactly the address that signed in.
/// </summary>
public sealed class TenantLogging(ITenantLog log)
{
    /// <summary>
    /// Records an act by the caller of <paramref name="context"/>.
    ///
    /// <b>Never throws.</b> A failure to write the log must not fail the operation being logged -
    /// a team that was deleted is deleted whether or not the record landed, and turning that into a
    /// 500 would tell the caller their action failed when it did not. The cost of this choice is
    /// that a broken log is silent, which is why <see cref="ITenantLog"/> is a plain table with no
    /// moving parts.
    /// </summary>
    public async Task WriteAsync(
        HttpContext context,
        string action,
        string? subject = null,
        string? subjectName = null,
        object? detail = null,
        CancellationToken ct = default)
    {
        try
        {
            await log.WriteAsync(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                context.User.FindFirstValue(ClaimTypes.Email),
                action,
                subject,
                subjectName,
                // Serialised here so no call site has to think about it. Whatever is passed is
                // shaped by the call site, and the rule that binds all of them is in ITenantLog:
                // never a credential, never a password hash, never an Agent definition's env.
                detail is null ? null : JsonSerializer.Serialize(detail),
                ct);
        }
        catch
        {
            // Deliberately swallowed - see the summary.
        }
    }

    /// <summary>
    /// Records an act for somebody who is not (yet, or any longer) an authenticated caller - a sign
    /// in, or a sign out after the cookie is gone. The id and email are given rather than read.
    /// </summary>
    public async Task WriteAsAsync(
        string? actorId,
        string? actorEmail,
        string action,
        string? subject = null,
        string? subjectName = null,
        object? detail = null,
        CancellationToken ct = default)
    {
        try
        {
            await log.WriteAsync(
                actorId, actorEmail, action, subject, subjectName,
                detail is null ? null : JsonSerializer.Serialize(detail), ct);
        }
        catch
        {
        }
    }
}
