using System.ComponentModel;
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Harness.Contracts;

namespace Harness.Host.Auth;

public static class AuthEndpoints
{
    /// <summary>
    /// The area these routes are filed under in the API reference. Everything here acts on the
    /// CALLER'S OWN account, which is what separates it from Users - those routes act on someone
    /// else's, and the difference decides whether a current password is required.
    /// </summary>
    private const string Area = "Account";

    /// <summary>Short, but not nothing. The only way back in is an operator at the machine running
    /// --reset-password on the host binary, so a forgotten password costs somebody a walk to the
    /// console rather than a self-service reset.</summary>
    public const int MinimumPasswordLength = 8;

    /// <summary>Guards the "one account, then closed" invariant against concurrent requests. An
    /// in-process guard suffices because the system is committed to one process (ADR 0004), so
    /// there is no inter-process race between check and create. Each request acquires this once and
    /// releases in a finally.</summary>
    private static readonly SemaphoreSlim _bootstrapGuard = new(1, 1);

    public static void Map(WebApplication app)
    {
        // Anonymous by necessity: this is what a caller with no credential is allowed to ask.
        app.MapGet("/api/auth/state", async (IUserStore users, CancellationToken ct) =>
            Results.Ok(new { needsAdmin = !await users.AnyAsync(ct) }))
            .AllowAnonymous()
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Whether this instance still needs its first account")
            .WithDescription(
                "The one question a caller holding no credential is allowed to ask, and the reason "
                + "it is anonymous. `needsAdmin` is true until the first account exists; the "
                + "sign-in screen reads it to decide between offering a login and offering to "
                + "create the tenant's first account. It turns false permanently the moment "
                + "that account is created.");

        app.MapPost("/api/auth/admin", async (
            Credentials request, IUserStore users, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            {
                return Results.BadRequest(new { error = "That is not an email address." });
            }

            if ((request.Password ?? "").Length < MinimumPasswordLength)
            {
                return Results.BadRequest(new
                {
                    error = $"A password needs at least {MinimumPasswordLength} characters.",
                });
            }

            await _bootstrapGuard.WaitAsync(ct);
            try
            {
                // Checked here rather than relying on the UNIQUE index: the rule is "one account, then
                // closed", not "one account per email".
                if (await users.AnyAsync(ct))
                {
                    return Results.Conflict(new { error = "This instance already has an account." });
                }

                var user = await users.CreateAsync(request.Email, request.Password!, ct);

                return Results.Ok(new { user.Id, user.Email });
            }
            finally
            {
                _bootstrapGuard.Release();
            }
        })
            .AllowAnonymous()
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Create this instance's first account")
            .WithDescription(
                "Creates the first account and then closes registration "
                + "permanently - there is no second call and no self-service sign-up anywhere in "
                + "this API. Anonymous by necessity, since nobody can be holding a credential yet.\n\n"
                + "Refuses with 400 when the address has no `@` or the password is shorter than "
                + $"{MinimumPasswordLength} characters, and 409 once any account exists at all - "
                + "the rule is \"one account, then closed\" rather than \"one account per address\".\n\n"
                + "There is no password recovery through this API and none over the network at all. "
                + "The only way back into a locked-out instance is an operator at the machine "
                + "running `Harness.Host --reset-password <email>`, which is deliberately not a "
                + "route and not a CLI verb. Someone who cannot reach the machine cannot recover "
                + "this account, so create a second account immediately afterwards.");

        app.MapPost("/api/auth/login", async (
            Credentials request, IUserStore users, TenantLogging audit, LoginThrottle throttle,
            HttpContext context, CancellationToken ct) =>
        {
            var email = request.Email ?? "";
            var address = context.Connection.RemoteIpAddress;

            // BEFORE the password is checked - see LoginThrottle for why a cool-down refuses the
            // right password as well. The body carries `error` like every other refusal, so the
            // sign-in page shows the sentence as it is.
            if (throttle.Check(email, address) is { } refusal)
            {
                context.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(refusal.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return Results.Json(new { error = refusal.Message }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            var user = await users.VerifyAsync(email, request.Password ?? "", ct);

            // One answer for a wrong password and an unknown account. Telling them apart answers
            // "does this account exist?" to someone who cannot log in.
            if (user is null)
            {
                throttle.Failed(email, address);
                return Results.Unauthorized();
            }

            throttle.Succeeded(email);

            var principal = new Principal(user.Id, PrincipalKind.User, Permits.All);

            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                PrincipalClaims.ToClaimsPrincipal(
                    principal, CookieAuthenticationDefaults.AuthenticationScheme,
                    email: user.Email),
                // Without this the Set-Cookie carries no Expires/Max-Age, so the browser drops it on
                // close and the ExpireTimeSpan above quietly governs only the server-side ticket.
                // Phone browsers evict session cookies aggressively when a tab is backgrounded,
                // which is where this would hurt.
                new AuthenticationProperties { IsPersistent = true });

            // WriteAs, not Write: the cookie was set on the RESPONSE, so `context.User` is still
            // the anonymous caller that arrived. The identity is the one just verified.
            //
            // Successes only. A failed attempt is a security signal rather than an administrative
            // act, and recording every wrong password in a table every person can read turns
            // this into a place to go looking for who is being targeted. That is what
            // LoginThrottle counts, in memory and nowhere a person can browse.
            await audit.WriteAsAsync(
                user.Id, user.Email, TenantActions.SignedIn, ct: ct);

            return Results.Ok(new { user.Id, user.Email });
        })
            .AllowAnonymous()
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Sign in and receive a session cookie")
            .WithDescription(
                "Exchanges an email and password for a session cookie lasting fourteen days. This "
                + "is how a BROWSER authenticates; a machine principal sends its key in the "
                + "`X-Api-Key` header instead and never calls this.\n\n"
                + "A wrong password and an unknown account both answer 401, deliberately and "
                + "identically: telling them apart would answer \"does this account exist?\" to "
                + "someone who cannot sign in.\n\n"
                + $"After {LoginThrottle.PerAccountLimit} failures for one account in "
                + $"{LoginThrottle.Window.TotalMinutes:0} minutes (or {LoginThrottle.PerAddressLimit} "
                + "from one address) every attempt answers 429, with `Retry-After` and a sentence in "
                + $"`error`, for {LoginThrottle.CoolDown.TotalMinutes:0} minutes. Successful sign-ins "
                + "are never counted, so one person on several devices is never limited.\n\n"
                + "The tier and permits in the returned cookie are minted here and never reissued, "
                + "so a promotion made later in the session is not reflected until the next sign-in "
                + "- the server honours it immediately, but this caller's own UI will not.");

        // Two endpoints, not one profile endpoint taking both: an address and a password are
        // confirmed by different things, refused for different reasons, and changed one at a time.
        // Both act on the id in the CLAIMS - never on an id in the body, which would be an "edit
        // any account" endpoint wearing a profile endpoint's name. The confirm-it-twice fields the
        // form asks for never arrive here: a re-type catches a typing slip, and the server has
        // nothing to compare a second copy against that it cannot compare the first to.
        app.MapPost("/api/auth/email", async (
            EmailChange request, IUserStore users, HttpContext context, CancellationToken ct) =>
        {
            if (await CallerAsync(context, users, ct) is not { } user)
            {
                return NoProfile();
            }

            var wanted = (request.Email ?? "").Trim();

            // Compared case-insensitively rather than through the store's own normaliser: the Host
            // composes the modules and does not reach into them, and "the same address
            // typed in a different case" is the same question here as it is there.
            if (!string.Equals(
                    (request.CurrentEmail ?? "").Trim(), user.Email, StringComparison.OrdinalIgnoreCase))
            {
                // 403, deliberately NOT 401 - see the password endpoint below for why that matters.
                // With a body, because a refusal with an empty one leaves the browser nothing to
                // show but "403 Forbidden", and the SPA should never have to read a status line to
                // find out what to say.
                return Refused("That is not the address on this account.");
            }

            if (!wanted.Contains('@'))
            {
                return Results.BadRequest(new { error = "That is not an email address." });
            }

            if (string.Equals(wanted, user.Email, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "That is already your email." });
            }

            if (!await users.TryUpdateEmailAsync(user.Id, wanted, ct))
            {
                return Results.Conflict(new { error = "That email is already in use." });
            }

            var updated = await users.FindByIdAsync(user.Id, ct) ?? user;

            // Signed in again, because the email travels in the cookie's claims. Without this the
            // ticket keeps saying the old address until the session ends - so /me disagrees with
            // the database, and the name in the corner is stale until a re-login nobody knows to
            // perform. Persistent for the same reason the original sign-in is.
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                PrincipalClaims.ToClaimsPrincipal(
                    new Principal(updated.Id, PrincipalKind.User, Permits.All),
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    email: updated.Email),
                new AuthenticationProperties { IsPersistent = true });

            return Results.Ok(new { updated.Id, updated.Email });
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Change your own email address")
            .WithDescription(
                "Changes the address on the CALLING account, confirmed by naming the current one. "
                + "It acts on the id in the session and never on an id in the body, so there is no "
                + "way to spell a request here that reaches somebody else's account - that is what "
                + "the Users routes are for.\n\n"
                + "Refuses with 403 when the confirmation does not match the address on file, or "
                + "when the credential has no person behind it (a container's or a console's key "
                + "authenticates perfectly well and has no account); 400 when the new address has "
                + "no `@` or is the one already held; 409 when another account has it.\n\n"
                + "On success the session cookie is reissued, because the address travels inside "
                + "it - without that the signed-in name stays stale until the session ends.");

        app.MapPost("/api/auth/password", async (
            PasswordChange request, IUserStore users, HttpContext context, CancellationToken ct) =>
        {
            if (await CallerAsync(context, users, ct) is not { } user)
            {
                return NoProfile();
            }

            if ((request.NewPassword ?? "").Length < MinimumPasswordLength)
            {
                return Results.BadRequest(new
                {
                    error = $"A password needs at least {MinimumPasswordLength} characters.",
                });
            }

            if (await users.VerifyAsync(user.Email, request.CurrentPassword ?? "", ct) is null)
            {
                // 403, deliberately NOT 401. The caller IS authenticated - it is this one password
                // that is wrong. The SPA treats a 401 as "the session ended" and returns to the
                // landing page, so answering 401 here would log someone out for a typo.
                return Refused("That is not your current password.");
            }

            await users.UpdatePasswordAsync(user.Id, request.NewPassword!, ct);

            // No re-sign-in: the ticket carries claims, and no claim changed. The session survives
            // a password change on purpose - the person doing it is the person holding it.
            return Results.NoContent();
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Change your own password")
            .WithDescription(
                "Changes the password on the CALLING account, confirmed by the current one. 204 on "
                + "success.\n\n"
                + "A wrong current password answers 403 and deliberately not 401. The caller is "
                + "still perfectly well signed in - it is this one field that is wrong - and the "
                + "browser treats a 401 as \"the session ended\", so answering 401 would sign "
                + $"somebody out over a typo. Under {MinimumPasswordLength} characters is 400.\n\n"
                + "The session survives on purpose: the person changing the password is the person "
                + "holding it, and no claim in the cookie has changed.");

        app.MapPost("/api/auth/logout", async (
            HttpContext context, TenantLogging audit, CancellationToken ct) =>
        {
            // BEFORE the sign-out, while the claims are still there to read. Afterwards the caller
            // is anonymous and the row would say nobody signed out.
            await audit.WriteAsync(context, TenantActions.SignedOut, ct: ct);

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Sign out")
            .WithDescription(
                "Clears the session cookie. Always 204, whether or not there was a session to "
                + "clear. It has no effect on a caller authenticating by `X-Api-Key`, which "
                + "carries no server-side session to end.");
    }
    /// <summary>A refusal a person can read. 403 rather than 401 throughout: the caller is
    /// authenticated, and a 401 tells the SPA the session ended.</summary>
    private static IResult Refused(string reason) =>
        Results.Json(new { error = reason }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult NoProfile() =>
        Refused("This credential has no profile to change.");

    /// <summary>
    /// The user row behind the request, or null when there is none.
    ///
    /// A container's or a console's credential authenticates perfectly well and has no user row
    /// behind it, so "authenticated" is not the same question as "is a person with an account".
    /// </summary>
    private static async Task<StoredUser?> CallerAsync(
        HttpContext context, IUserStore users, CancellationToken ct)
    {
        var principal = PrincipalClaims.From(context.User);

        return principal is null || principal.Kind != PrincipalKind.User
            ? null
            : await users.FindByIdAsync(principal.Id, ct);
    }
}

public sealed record Credentials(
    [property: Description("The account's email address.")]
    string? Email,
    [property: Description(
        "The account's password. When creating the first account this must be at least 8 "
        + "characters; when signing in, whatever it actually is.")]
    string? Password);

/// <summary>
/// A new address, confirmed by naming the current one. There is no id: the account being changed
/// is always the caller's own.
/// </summary>
public sealed record EmailChange(
    [property: Description(
        "The address currently on the account, as confirmation. Compared case-insensitively, and a "
        + "mismatch is refused with 403.")]
    string? CurrentEmail,
    [property: Description(
        "The new address. Must contain an `@`, must differ from the current one, and must not "
        + "already belong to another account.")]
    string? Email);

/// <summary>A new password, confirmed by the current one.</summary>
public sealed record PasswordChange(
    [property: Description(
        "The password currently on the account, as confirmation. A mismatch is refused with 403 "
        + "rather than 401, so that a typo does not read as an expired session.")]
    string? CurrentPassword,
    [property: Description("The new password. At least 8 characters.")]
    string? NewPassword);
