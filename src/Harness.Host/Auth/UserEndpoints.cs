using System.ComponentModel;
using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// User administration: list, create, delete, and reset someone else's password.
/// AuthEndpoints' sibling in shape - same request-record conventions, the same refusal register -
/// and every route here carries <c>.HumansOnly()</c>, which is the whole rule.
///
/// EVERY PERSON REACHES EVERY TEAM. An account has an email and a
/// password and nothing else to administer. A machine principal is never a person, so a container
/// or Concierge credential cannot create or delete accounts here.
///
/// Email is absent from every request record below on purpose. It changes through the owner's own
/// /api/auth/email, never through another person reaching into someone else's row.
/// </summary>
public static class UserEndpoints
{
    /// <summary>Guards the last-account check on DELETE against two deletes racing. In-process
    /// is enough: the system is one process.</summary>
    private static readonly SemaphoreSlim _lastAccountGuard = new(1, 1);

    /// <summary>
    /// The area these routes are filed under in the API reference. Everything here acts on SOMEONE
    /// ELSE'S account, which is what separates it from Account - those
    /// routes act on the caller's own and require a current password to prove it.
    /// </summary>
    private const string Area = "Users";

    /// <summary>Repeated on every route in this file, so the reference states the rule once per
    /// route rather than leaving a reader to discover it as a 403.</summary>
    private const string PeopleOnly =
        "\n\n**A person's action.** Any signed-in person may do this; a machine principal never "
        + "may, so a container, Concierge or API-key credential cannot create or change accounts.";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/users", async (IUserStore users, CancellationToken ct) =>
        {
            var result = new List<object>();

            foreach (var user in await users.ListAsync(ct))
            {
                result.Add(new { user.Id, user.Email });
            }

            return Results.Ok(result);
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("List every account")
            .WithDescription(
                "Every account on the tenant. Every account reaches every team; there is nothing per "
                + "account to configure."
                + PeopleOnly);

        app.MapPost("/api/users", async (
            CreateUser request, IUserStore users, TenantLogging audit,
            HttpContext context, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            {
                return Results.BadRequest(new { error = "That is not an email address." });
            }

            if ((request.Password ?? "").Length < AuthEndpoints.MinimumPasswordLength)
            {
                return Results.BadRequest(new
                {
                    error = $"A password needs at least {AuthEndpoints.MinimumPasswordLength} characters.",
                });
            }

            // Null, not a thrown SqliteException, on a duplicate - see IUserStore.TryCreateAsync's
            // own remarks for why that catch lives one layer down rather than here.
            var user = await users.TryCreateAsync(request.Email, request.Password!, ct);

            if (user is null)
            {
                return Results.Conflict(new { error = "An account with that email already exists." });
            }

            // The address, never the password - see ITenantLog.
            await audit.WriteAsync(context, TenantActions.UserCreated, user.Id, user.Email, ct: ct);

            return Results.Ok(new { user.Id, user.Email });
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Create an account")
            .WithDescription(
                "Creates an account. This is the only way to add "
                + "a person after the first account closed registration.\n\n"
                + "400 when the address has no `@` or the password is shorter than "
                + $"{AuthEndpoints.MinimumPasswordLength} characters; 409 when the address is "
                + "already taken."
                + PeopleOnly);

        app.MapDelete("/api/users/{id}", async (
            [Description("The account's id, as returned by `GET /api/users`.")] string id,
            IUserStore users, TenantLogging audit, HttpContext context, CancellationToken ct) =>
        {
            if (await users.FindByIdAsync(id, ct) is not { } user) return NoSuchUser();

            // THE LAST ACCOUNT CANNOT BE DELETED. With nobody able to sign in, nothing in the
            // product can create an account again. Guarded so two deletes racing each other cannot
            // both read a count of 2.
            await _lastAccountGuard.WaitAsync(ct);
            try
            {
                if ((await users.ListAsync(ct)).Count <= 1)
                {
                    return Results.Json(
                        new { error = "The last account cannot be deleted." },
                        statusCode: StatusCodes.Status409Conflict);
                }

                await users.DeleteAsync(id, ct);
            }
            finally
            {
                _lastAccountGuard.Release();
            }

            // The EMAIL, denormalised into the row. After this the account is gone, so a log that
            // stored only an id would answer "who was deleted" with a string nobody recognises.
            await audit.WriteAsync(context, TenantActions.UserDeleted, user.Id, user.Email, ct: ct);

            return Results.NoContent();
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Delete an account")
            .WithDescription(
                "Removes the account and, by cascade, the API keys and Concierge credentials it "
                + "owned. 204 on success, 404 for an unknown account.\n\n"
                + "409 when it is the last account: with nobody able to sign in, nothing in the "
                + "product could create an account again."
                + PeopleOnly);

        app.MapPost("/api/users/{id}/password", async (
            [Description("The account's id, as returned by `GET /api/users`.")] string id,
            ResetPassword request, IUserStore users, TenantLogging audit, HttpContext context,
            CancellationToken ct) =>
        {
            if (await users.FindByIdAsync(id, ct) is not { } target) return NoSuchUser();

            if ((request.Password ?? "").Length < AuthEndpoints.MinimumPasswordLength)
            {
                return Results.BadRequest(new
                {
                    error = $"A password needs at least {AuthEndpoints.MinimumPasswordLength} characters.",
                });
            }

            // No current password: this is a person acting on someone else's account, not
            // the owner proving they still hold it. Contrast AuthEndpoints' /api/auth/password,
            // which requires one for exactly that reason.
            await users.UpdatePasswordAsync(id, request.Password!, ct);

            // THAT it happened and to whom. Never the password, and never a hash of it.
            await audit.WriteAsync(
                context, TenantActions.PasswordReset, target.Id, target.Email, ct: ct);

            return Results.NoContent();
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Reset another account's password")
            .WithDescription(
                "Sets a new password on someone else's account. **No current password is asked "
                + "for**, and that is the difference from `POST /api/auth/password`: this is an "
                + "person acting on an account they do not own, not the owner proving they "
                + "still hold it.\n\n"
                + "204 on success; 404 for an unknown account; 400 under "
                + $"{AuthEndpoints.MinimumPasswordLength} characters. The account's existing "
                + "sessions are not ended."
                + PeopleOnly);
    }

    private static IResult NoSuchUser() => Results.NotFound(new { error = "No such user." });
}

public sealed record CreateUser(
    [property: Description("The new account's email address. Must contain an `@` and be unused.")]
    string? Email,
    [property: Description("The new account's password. At least 8 characters.")]
    string? Password);

public sealed record ResetPassword(
    [property: Description(
        "The new password. At least 8 characters. No current password accompanies it: the "
        + "caller is not proving ownership of this account.")]
    string? Password);
