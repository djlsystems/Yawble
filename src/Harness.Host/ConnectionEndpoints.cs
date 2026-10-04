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
        app.MapGet("/api/connections/providers", async (
            [Description("The scopes about to be asked, repeated; the guide is built for them. Omitted: every installed slot's for that provider.")] string[]? scopes,
            Connections connections, PluginCatalog plugins, HttpContext context, CancellationToken ct) =>
        {
            var asked = Connections.Scopes(scopes);
            if (asked is null) return Results.BadRequest(new { error = "`scopes` must be scope strings with no spaces." });

            return Results.Ok((await connections.ProvidersAsync(ct)).Select(p => p.View(Guide(p, asked, plugins, context))));
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("The OAuth providers a connection can belong to")
            .WithDescription(
                "Google and Microsoft (built in, configured or not), then every custom provider: its "
                + "endpoints, its client ID, `clientSecretSet` (the secret itself is never answered), "
                + "`configured`, `revokes`, `defaultScopes`, one line of `help`, and a built-in's setup "
                + "`guide`: `{ steps: [{ id, title, text, link, copy: [{ label, value }] }] }`, in order. A "
                + "`link` may hold `{projectId}`, the person's Google project id or empty. The client step "
                + "says first when the address in use gives a redirect URI the provider will refuse. No step "
                + "carries a secret. A custom provider's `guide` is null.");

        app.MapGet("/api/connections/needs", (
            [Description("google, microsoft, or custom-<id>.")] string? provider,
            [Description("A plugin id; with `slot`, only that slot.")] string? plugin,
            [Description("A slot name of `plugin`.")] string? slot,
            PluginCatalog plugins) =>
        {
            provider = provider?.Trim();
            if (string.IsNullOrEmpty(provider)) return Results.BadRequest(new { error = "`provider` is required: google, microsoft or a custom provider's id." });

            if (string.IsNullOrWhiteSpace(plugin) != string.IsNullOrWhiteSpace(slot))
            {
                return Results.BadRequest(new { error = "`plugin` and `slot` go together: name both for one slot, or neither for every slot." });
            }

            var needs = ConnectionNeeds.For(
                plugins.Plugins.Select(p => p.Manifest), provider,
                string.IsNullOrWhiteSpace(plugin) ? null : plugin.Trim(), string.IsNullOrWhiteSpace(slot) ? null : slot.Trim());

            return Results.Ok(ConnectionNeeds.View(provider, needs));
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("What a connection of a provider should ask for")
            .WithDescription(
                "From the installed plugins' connection slots, never typed: `needs` (each slot admitting "
                + "`provider`: `plugin`, `slot`, `description`, `scopes`), `scopes` (merged, once each: "
                + "`scope`, `words` or null when the Host has none, `plugins`) and `apis` (the Google APIs "
                + "those scopes need, each with an enable `link` that may hold `{projectId}`; empty for "
                + "other providers). `plugin` and `slot` together narrow it to one slot.");

        app.MapPut("/api/connections/providers/{id}", async (
            [Description("google, microsoft, or custom-<id> (created when new).")] string id,
            ProviderChange request, Connections connections, PluginCatalog plugins, HttpContext context, CancellationToken ct) =>
        {
            try
            {
                var (provider, error) = await connections.SaveProviderAsync(id, request, Actor(context), ct);
                return error is not null
                    ? Results.BadRequest(new { error })
                    : Results.Ok(provider!.View(Guide(provider, [], plugins, context)));
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
                + "keys and never answered. Google's `clientId` must end in `.apps.googleusercontent.com`. Answers "
                + "the provider as the list does, guide included. 400 naming the field. Saved with a "
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
            if (request.Flow?.Trim() == Connections.DeviceFlow)
            {
                var (device, refusal) = await connections.StartDeviceAsync(Actor(context), request, ct);
                return refusal is not null ? Results.BadRequest(new { error = refusal }) : Results.Ok(device!.Body());
            }

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
                "Body `{ provider, scopes, name?, reconnectId?, redirectUri?, flow? }`. Answers the provider's "
                + "`authorizationUrl`, the `state` (issued to the caller, single use, 10 minutes), the "
                + "`redirectUri` and `expiresAt`. With no `redirectUri` the Host's own "
                + "`/api/connections/callback` at the address the request came to is used; a loopback "
                + "`http://127.0.0.1:<port>` starts the CLI flow, finished with `complete`. The PKCE "
                + "verifier stays on the Host. A reconnect asks the old scopes plus `scopes`. With "
                + "`flow: \"device\"` (a provider with a device endpoint: Microsoft) it is a sign-in with a "
                + "code instead: answers only `{ flowId, userCode, verificationUri, expiresAt }`; the Host "
                + "keeps the device code and polls the provider itself. Read it with `GET /api/connections/flows/{flowId}`.");

        app.MapGet("/api/connections/flows/open", (Connections connections, HttpContext context) =>
            Results.Ok(connections.OpenDeviceFlows(Actor(context)).Select(f => f.Body())))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("The caller's own sign-ins with a code still waiting")
            .WithDescription(
                "So a reopened dialog picks a waiting sign-in back up: each "
                + "`{ flowId, provider, userCode, verificationUri, expiresAt, state }` the caller started that "
                + "is still `waiting` and not past `expiresAt`, soonest to expire first. Another person's are "
                + "never listed. Never the device code, a client secret or a token.");

        app.MapGet("/api/connections/flows/{flowId}", async (
            [Description("The flowId a sign-in with a code started with.")] string flowId,
            Connections connections, TeamRegistry teams, HttpContext context, CancellationToken ct) =>
        {
            if (connections.DeviceFlowOf(flowId, Actor(context)) is not { } flow) return Results.NotFound(new { error = MissingFlow });

            return Results.Ok(await FlowViewAsync(connections, flow, c => View(c.Connection, c.Uses, teams), ct));
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Read a sign-in with a code")
            .WithDescription(
                "The starter's only: anyone else gets exactly what a missing flow gets (404). Answers "
                + "`{ state: waiting|done|refused|expired, sentence, connection? }`, the connection only when "
                + "`done`. Closing the dialog cancels nothing: reading again gives the same state.");

        app.MapGet(Connections.CallbackPath, async (
            [Description("The provider's authorization code.")] string? code,
            [Description("The state the Host issued at start.")] string? state,
            [Description("The provider's refusal, when it refused.")] string? error,
            [Description("The provider's reason.")] string? error_description,
            Connections connections, CancellationToken ct) =>
        {
            ConnectionRecord? connection;
            bool reconnected;
            string? refusal;

            try
            {
                (connection, reconnected, refusal) = await connections.CompleteAsync(
                    state, code, actor: null, viaCallback: true, error, error_description, ct);
            }
            catch (SqliteException exception)
            {
                // ALWAYS A REDIRECT: the person lands on the console with the reason, never a 500 page.
                (connection, reconnected, refusal) = (null, false,
                    $"The connection could not be stored ({exception.SqliteErrorCode}). Start again.");
            }

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
            ConnectionRecord? connection;
            string? error;

            try
            {
                (connection, _, error) = await connections.CompleteAsync(
                    request.State, request.Code, Actor(context), viaCallback: false, ct: ct);
            }
            catch (SqliteException exception)
            {
                return Unrecorded("The connection was not stored", exception);
            }

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
                "Refused 409 while a member binds it, naming them (`usedBy`). Otherwise deletes the tokens "
                + "and the connection, then revokes at the provider where it supports that, best effort: "
                + "the result is a `connections.revoked` tenant row, and a failed revoke never blocks. 204.");
    }

    /// <summary>What a flow nobody may read answers: the same for a missing flow and another person's.</summary>
    public const string MissingFlow = "There is no such sign-in, or it was not started by you. Start again.";

    /// <summary>A sign-in with a code as the route and the operator exchange answer it.</summary>
    public static async Task<object> FlowViewAsync(
        Connections connections, DeviceFlowState flow, Func<(ConnectionRecord Connection, IReadOnlyList<ConnectionUse> Uses), object> view,
        CancellationToken ct)
    {
        var connection = flow is { State: Connections.Done, ConnectionId: { } id } ? await connections.Store.GetAsync(id, ct) : null;

        return connection is null
            ? new { state = flow.State, sentence = flow.Sentence }
            : new { state = flow.State, sentence = flow.Sentence, connection = view((connection, await connections.Store.UsedByAsync(connection.Id, ct))) };
    }

    /// <summary>The route's and the operator exchange's disconnect: (204, null), or a status and body.</summary>
    public static async Task<(int Status, object? Body)> DisconnectAsync(
        Connections connections, TeamRegistry teams, string id, ConnectionActor actor, CancellationToken ct)
    {
        if (await connections.Store.GetAsync(id, ct) is not { } connection)
        {
            return (StatusCodes.Status404NotFound, new { error = $"There is no connection '{id}'." });
        }

        var tokens = await connections.Store.TokensAsync(id, ct);
        var provider = await connections.ProviderAsync(connection.Provider, ct);
        var revokes = provider is { Revokes: true } && (tokens?.RefreshToken ?? tokens?.AccessToken) is not null;

        // DELETED FIRST, the in-use check inside the same transaction: a binding made meanwhile
        // refuses the delete, and nothing is revoked for a connection that stays.
        try
        {
            var (deleted, stillUsed) = await connections.Store.DeleteAsync(id, actor.Row(
                TenantActions.ConnectionDisconnected, id, connection.Name,
                new { connection = id, provider = connection.Provider, account = connection.Account, revoke = revokes ? "follows" : "not supported" }), ct);

            if (!deleted)
            {
                return stillUsed.Count > 0
                    ? InUse(connection, stillUsed, teams)
                    : (StatusCodes.Status404NotFound, new { error = $"There is no connection '{id}'." });
            }
        }
        catch (SqliteException exception)
        {
            return (StatusCodes.Status500InternalServerError, new { error = $"The connection was not disconnected: its record could not be written ({exception.Message})." });
        }

        connections.Forget(id);

        // THEN REVOKED, best effort: the tokens are already gone here, and a provider that cannot be
        // reached changes nothing for the person. What happened is its own tenant row.
        if (revokes)
        {
            // Not the request's token: a person who closes the page after the delete still gets the revoke.
            var result = await connections.Endpoints.RevokeAsync(
                provider!, (tokens!.RefreshToken ?? tokens.AccessToken)!, CancellationToken.None) ?? "revoked";

            try
            {
                await connections.Store.RecordAsync(actor.Row(
                    TenantActions.ConnectionRevoked, id, connection.Name,
                    new { connection = id, provider = connection.Provider, account = connection.Account, revoke = result }), CancellationToken.None);
            }
            catch (SqliteException)
            {
                // The disconnect itself is recorded; only the revoke's result is lost.
            }
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

    /// <summary>A provider's setup guide for <paramref name="asked"/> - or, when none are named, every
    /// installed slot's scopes for it - under its default scopes, at the address in use.</summary>
    private static object? Guide(OAuthProvider provider, IReadOnlyList<string> asked, PluginCatalog plugins, HttpContext context)
    {
        if (!ConnectionProviders.IsBuiltIn(provider.Kind)) return null;

        var scopes = asked.Count > 0
            ? asked
            : ConnectionNeeds.Merge(ConnectionNeeds.For(plugins.Plugins.Select(p => p.Manifest), provider.Id)).Select(s => s.Scope).ToList();

        return ConnectionGuides.View(ConnectionGuides.For(
            provider, [.. provider.DefaultScopes.Union(scopes, StringComparer.Ordinal)], Origin(context)));
    }

    /// <summary>The address the request came to, as the browser wrote it.</summary>
    private static string Origin(HttpContext context) => $"{context.Request.Scheme}://{context.Request.Host.Value}";

    private static IResult Unrecorded(string what, SqliteException exception) => Results.Json(
        new { error = $"{what}: its record could not be written ({exception.Message})." },
        statusCode: StatusCodes.Status500InternalServerError);
}

/// <summary>Body of <c>PATCH /api/connections/{id}</c>.</summary>
public sealed record RenameConnection(string? Name);
