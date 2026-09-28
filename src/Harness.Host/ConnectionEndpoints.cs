using System.ComponentModel;
using System.Security.Claims;
using Harness.Contracts;
using Harness.Host.Auth;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>
/// Admin → Connections: a person's OAuth accounts, held by the Host for plugins. Every route is a
/// person's (<c>HumansOnly</c>) except the provider's redirect back, which carries no session - the
/// auth cookie is SameSite=Strict - and is accepted only with a <c>state</c> the Host issued, once,
/// within 10 minutes. No route answers a token or a client secret. See <c>docs/connections.md</c>.
/// </summary>
public static class ConnectionEndpoints
{
    private const string Area = "Connections";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/connections/providers", async (Connections connections, CancellationToken ct) =>
                Results.Ok((await connections.ProvidersAsync(ct)).Select(p => p.View())))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("The OAuth providers a connection can belong to")
            .WithDescription(
                "Google and Microsoft (built in, configured or not), then every custom provider: its "
                + "endpoints, its client ID, `clientSecretSet` (the secret itself is never answered), "
                + "`configured`, `revokes`, `defaultScopes` and one line of `help`.");

        app.MapPut("/api/connections/providers/{id}", async (
            [Description("google, microsoft, or custom-<id> (created when new).")] string id,
            ProviderChange request, Connections connections, HttpContext context, CancellationToken ct) =>
        {
            try
            {
                var (provider, error) = await connections.SaveProviderAsync(id, request, Actor(context), ct);
                return error is not null ? Results.BadRequest(new { error }) : Results.Ok(provider!.View());
            }
            catch (SqliteException exception)
            {
                return Unrecorded("The provider was not saved", exception);
            }
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Set a provider's OAuth client")
            .WithDescription(
                "Body `{ clientId, clientSecret?, tenant? (microsoft), name?, authorizeUrl, tokenUrl, "
                + "userinfoUrl?, defaultScopes (custom) }`. `clientSecret` omitted keeps the stored one, "
                + "`\"\"` clears it. The secret is stored encrypted with the instance's Data Protection "
                + "keys and never answered. 400 naming the field. Saved with a "
                + "`connections.provider-saved` tenant row in the same transaction.");

        app.MapDelete("/api/connections/providers/{id}", async (
            [Description("A custom provider's id.")] string id,
            Connections connections, HttpContext context, CancellationToken ct) =>
        {
            if (ConnectionProviders.IsBuiltIn(id))
            {
                return Results.BadRequest(new { error = $"{ConnectionProviders.Display(id)} is built in; clear its client instead." });
            }

            if (!ConnectionProviders.IsCustomId(id) || await connections.Store.ProviderAsync(id, ct) is null)
            {
                return Results.NotFound(new { error = $"There is no provider '{id}'." });
            }

            try
            {
                var (removed, used) = await connections.Store.DeleteProviderAsync(
                    id, Actor(context).Row(TenantActions.ConnectionProviderRemoved, id, id, new { provider = id }), ct);

                return removed
                    ? Results.NoContent()
                    : Results.Conflict(new { error = $"Provider '{id}' still has connections: {string.Join(", ", used)}. Disconnect them first." });
            }
            catch (SqliteException exception)
            {
                return Unrecorded("The provider was not removed", exception);
            }
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Remove a custom provider")
            .WithDescription("204, or 409 naming its connections while any exists. A built-in answers 400.");

        app.MapGet("/api/connections", async (Connections connections, TeamRegistry teams, CancellationToken ct) =>
        {
            var uses = await connections.Store.AllUsesAsync(ct);
            return Results.Ok((await connections.Store.ListAsync(ct)).Select(c => View(c, uses[c.Id], teams)));
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("The connected accounts")
            .WithDescription(
                "Each connection: `id`, `name`, `provider`, `providerKind`, `account`, `scopes`, "
                + "`connectedAt`, `refreshedAt`, `status` (`ok` or `needs-reconnect`, with the provider's "
                + "`statusReason`) and `usedBy` (`team`, `member`, `label`, `slot`). Never a token.");

        app.MapPost("/api/connections/start", async (
            StartConnection request, Connections connections, HttpContext context, CancellationToken ct) =>
        {
            var (start, error) = await connections.StartAsync(Actor(context), request, Origin(context), ct);
            if (error is not null) return Results.BadRequest(new { error });

            return Results.Ok(new
            {
                authorizationUrl = start!.AuthorizationUrl,
                state = start.State,
                redirectUri = start.RedirectUri,
                expiresAt = start.ExpiresAt,
            });
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Start connecting an account")
            .WithDescription(
                "Body `{ provider, scopes, name?, reconnectId?, redirectUri? }`. Answers the provider's "
                + "`authorizationUrl`, the `state` (issued to the caller, single use, 10 minutes), the "
                + "`redirectUri` and `expiresAt`. With no `redirectUri` the Host's own "
                + "`/api/connections/callback` at the address the request came to is used; a loopback "
                + "`http://127.0.0.1:<port>` starts the CLI flow, finished with `complete`. The PKCE "
                + "verifier stays on the Host. A reconnect asks the old scopes plus `scopes`.");

        app.MapGet(Connections.CallbackPath, async (
            [Description("The provider's authorization code.")] string? code,
            [Description("The state the Host issued at start.")] string? state,
            [Description("The provider's refusal, when it refused.")] string? error,
            [Description("The provider's reason.")] string? error_description,
            Connections connections, CancellationToken ct) =>
        {
            var (connection, reconnected, refusal) = await connections.CompleteAsync(
                state, code, actor: null, viaCallback: true, error, error_description, ct);

            return Results.Redirect(refusal is not null
                ? $"/console?connection=refused&reason={Uri.EscapeDataString(refusal)}"
                : $"/console?connection={(reconnected ? "reconnected" : "connected")}&id={Uri.EscapeDataString(connection!.Id)}");
        })
            .AllowAnonymous()
            .NoPermitRequired()
            .WithTags(Area)
            .WithSummary("The provider's redirect back (web flow)")
            .WithDescription(
                "Anonymous: the provider sends the browser here, and the session cookie is not sent on "
                + "that cross-site navigation. Accepted only with a `state` this Host issued for the web "
                + "flow, once, within 10 minutes; anything else stores nothing. The Host exchanges the "
                + "code with the stored PKCE verifier and client secret, then redirects to "
                + "`/console?connection=connected|reconnected&id=...` or `/console?connection=refused&reason=...`.");

        app.MapPost("/api/connections/complete", async (
            CompleteConnection request, Connections connections, TeamRegistry teams, HttpContext context, CancellationToken ct) =>
        {
            var (connection, _, error) = await connections.CompleteAsync(
                request.State, request.Code, Actor(context), viaCallback: false, ct: ct);

            if (error is not null) return Results.BadRequest(new { error });

            var uses = await connections.Store.UsedByAsync(connection!.Id, ct);
            return Results.Ok(View(connection, uses, teams));
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Finish the CLI's loopback flow")
            .WithDescription(
                "Body `{ state, code }`: the code the loopback listener caught. The state must be one "
                + "`start` issued to the caller for a loopback redirect. The Host does the exchange; the "
                + "client secret never leaves it. Answers the stored connection.");

        app.MapPatch("/api/connections/{id}", async (
            [Description("The connection's id.")] string id,
            RenameConnection request, Connections connections, TeamRegistry teams, HttpContext context, CancellationToken ct) =>
        {
            var name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > Connections.MaximumNameLength)
            {
                return Results.BadRequest(new { error = $"A connection's name is 1 to {Connections.MaximumNameLength} characters." });
            }

            if (await connections.Store.GetAsync(id, ct) is not { } before) return Results.NotFound(new { error = $"There is no connection '{id}'." });

            try
            {
                await connections.Store.RenameAsync(
                    id, name, Actor(context).Row(TenantActions.ConnectionRenamed, id, name, new { connection = id, from = before.Name, to = name }), ct);
            }
            catch (SqliteException exception)
            {
                return Unrecorded("The connection was not renamed", exception);
            }

            return Results.Ok(View((await connections.Store.GetAsync(id, ct))!, await connections.Store.UsedByAsync(id, ct), teams));
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Rename a connection")
            .WithDescription("Body `{ name }`, 1 to 80 characters. A `connections.renamed` tenant row lands with it.");

        app.MapDelete("/api/connections/{id}", async (
            [Description("The connection's id.")] string id,
            Connections connections, TeamRegistry teams, HttpContext context, CancellationToken ct) =>
        {
            var (status, body) = await DisconnectAsync(connections, teams, id, Actor(context), ct);
            return status == StatusCodes.Status204NoContent ? Results.NoContent() : Results.Json(body, statusCode: status);
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Disconnect an account")
            .WithDescription(
                "Refused 409 while a member binds it, naming them (`usedBy`). Otherwise revokes at the "
                + "provider where it supports that (a failed revoke is recorded, never blocking), deletes "
                + "the tokens and the connection, and answers 204.");
    }

    /// <summary>The route's and the operator exchange's disconnect: (204, null), or a status and body.</summary>
    public static async Task<(int Status, object? Body)> DisconnectAsync(
        Connections connections, TeamRegistry teams, string id, ConnectionActor actor, CancellationToken ct)
    {
        if (await connections.Store.GetAsync(id, ct) is not { } connection)
        {
            return (StatusCodes.Status404NotFound, new { error = $"There is no connection '{id}'." });
        }

        var used = await connections.Store.UsedByAsync(id, ct);
        if (used.Count > 0) return InUse(connection, used, teams);

        // REVOKED FIRST, best effort: a provider that cannot be reached must not keep a person from
        // deleting the tokens here. Whatever happened is in the tenant row.
        string? revoke = null;
        var tokens = await connections.Store.TokensAsync(id, ct);
        var provider = await connections.ProviderAsync(connection.Provider, ct);

        if (provider is { Revokes: true } && (tokens?.RefreshToken ?? tokens?.AccessToken) is { } token)
        {
            revoke = await connections.Endpoints.RevokeAsync(provider, token, ct) ?? "revoked";
        }

        try
        {
            var (deleted, stillUsed) = await connections.Store.DeleteAsync(id, actor.Row(
                TenantActions.ConnectionDisconnected, id, connection.Name,
                new { connection = id, provider = connection.Provider, account = connection.Account, revoke = revoke ?? "not supported" }), ct);

            if (!deleted && stillUsed.Count > 0) return InUse(connection, stillUsed, teams);
        }
        catch (SqliteException exception)
        {
            return (StatusCodes.Status500InternalServerError, new { error = $"The connection was not disconnected: its record could not be written ({exception.Message})." });
        }

        return (StatusCodes.Status204NoContent, null);
    }

    private static (int, object) InUse(ConnectionRecord connection, IReadOnlyList<ConnectionUse> used, TeamRegistry teams) =>
        (StatusCodes.Status409Conflict, new
        {
            error = $"Connection {connection.Named} is used by "
                + string.Join(", ", used.Select(u => $"{u.Team}/{u.Member} (slot {u.Slot})"))
                + ". Unbind it from those members first.",
            usedBy = UsedBy(used, teams),
        });

    /// <summary>A connection as every answer shows it: no token, ever.</summary>
    public static object View(ConnectionRecord connection, IEnumerable<ConnectionUse> uses, TeamRegistry teams) => new
    {
        id = connection.Id,
        name = connection.Name,
        provider = connection.Provider,
        providerKind = ConnectionProviders.KindOf(connection.Provider),
        account = connection.Account,
        scopes = connection.Scopes,
        connectedAt = connection.ConnectedAt,
        refreshedAt = connection.RefreshedAt,
        status = connection.Status,
        statusReason = connection.StatusReason,
        usedBy = UsedBy(uses, teams),
    };

    private static object[] UsedBy(IEnumerable<ConnectionUse> uses, TeamRegistry teams)
    {
        var members = teams.All().SelectMany(t => t.Containers).ToList();

        return [.. uses.Select(u => (object)new
        {
            team = u.Team,
            member = u.Member,
            label = members.FirstOrDefault(m => string.Equals(m.Team, u.Team, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.Id, u.Member, StringComparison.OrdinalIgnoreCase))?.Name ?? u.Member,
            slot = u.Slot,
        })];
    }

    /// <summary>The person acting: their own id, or the person a person's key acts as.</summary>
    public static ConnectionActor Actor(HttpContext context) => new(
        context.User.FindFirstValue(PrincipalClaims.OwnerClaim) ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
        context.User.FindFirstValue(ClaimTypes.Email));

    /// <summary>The address the request came to, as the browser wrote it.</summary>
    private static string Origin(HttpContext context) => $"{context.Request.Scheme}://{context.Request.Host.Value}";

    private static IResult Unrecorded(string what, SqliteException exception) => Results.Json(
        new { error = $"{what}: its record could not be written ({exception.Message})." },
        statusCode: StatusCodes.Status500InternalServerError);
}

/// <summary>Body of <c>PATCH /api/connections/{id}</c>.</summary>
public sealed record RenameConnection(string? Name);
