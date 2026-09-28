using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>What a run is handed for one slot: the fresh access token and what it is for.</summary>
public sealed record ConnectionGrant(
    string ConnectionId, string Provider, string Account, string AccessToken, DateTimeOffset? ExpiresAt, IReadOnlyList<string> Scopes);

/// <summary>A binding refused, with what the web offers: Reconnect with these scopes.</summary>
public sealed record BindingRefusal(string Error, string? ReconnectId = null, IReadOnlyList<string>? ReconnectScopes = null)
{
    public object Body() => ReconnectId is null
        ? new { error = Error }
        : new { error = Error, reconnect = new { connectionId = ReconnectId, scopes = ReconnectScopes } };
}

/// <summary>The answer to a flow's start: where to send the browser.</summary>
public sealed record ConnectionStart(string AuthorizationUrl, string State, string RedirectUri, DateTimeOffset ExpiresAt);

/// <summary>Who acts: a person's id and email, or the operator at the engine.</summary>
public sealed record ConnectionActor(string Id, string? Email)
{
    /// <summary>The operator's `yawble connect`, through the file exchange: root on the instance.</summary>
    public static readonly ConnectionActor Operator = new("operator", "operator (yawble connect)");

    public TriggerAudit Row(string action, string? subject, string? subjectName, object detail) =>
        new(this == Operator ? null : Id, Email, action, subject, subjectName, JsonSerializer.Serialize(detail));
}

/// <summary>
/// CONNECTIONS: a person's account at an OAuth provider, held by the Host, so a plugin gets only a
/// fresh access token per run and never writes OAuth code. See <c>docs/connections.md</c>.
///
/// <list type="bullet">
/// <item>A flow starts with <see cref="StartAsync"/>: a <c>state</c> issued to one person, single
/// use, refused after 10 minutes, and a PKCE verifier the Host keeps. The code comes back to the
/// web callback or, for the CLI's loopback flow, through <see cref="CompleteAsync"/>; the Host does
/// the exchange, so the client secret never leaves it.</item>
/// <item><see cref="GrantAsync"/> is the one way a token leaves the Host: to a plugin run, on
/// stdin. Refreshes of one connection are SERIALISED, 5 minutes before expiry, and a rotated refresh
/// token is STORED before the access token is handed out. A refresh the provider refuses marks the
/// connection <c>needs-reconnect</c>, and the next Reconnect clears it.</item>
/// <item>Nothing here answers a token or the client secret to a route, a row or a log.</item>
/// </list>
/// </summary>
public sealed class Connections(ConnectionStore store, IOAuthEndpoints endpoints, TimeProvider clock)
{
    public static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan RefreshEarly = TimeSpan.FromMinutes(5);

    public const string CallbackPath = "/api/connections/callback";

    public const int MaximumNameLength = 80;

    /// <summary>Where a person reconnects, as every sentence names it.</summary>
    public const string Where = "Admin → Connections";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshing = new(StringComparer.Ordinal);

    public ConnectionStore Store => store;

    public IOAuthEndpoints Endpoints => endpoints;

    // ---- providers ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<OAuthProvider>> ProvidersAsync(CancellationToken ct = default)
    {
        var stored = (await store.ProvidersAsync(ct)).ToDictionary(p => p.Id, StringComparer.Ordinal);

        return
        [
            .. ConnectionProviders.BuiltIns.Select(id => ConnectionProviders.Resolve(id, stored.GetValueOrDefault(id))),
            .. stored.Values.Where(p => ConnectionProviders.IsCustomId(p.Id)).OrderBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => ConnectionProviders.Resolve(p.Id, p)),
        ];
    }

    /// <summary>The provider as the Host uses it, or null for an unknown id (a custom one nobody set).</summary>
    public async Task<OAuthProvider?> ProviderAsync(string id, CancellationToken ct = default)
    {
        if (!ConnectionProviders.IsKnownId(id)) return null;

        var stored = await store.ProviderAsync(id, ct);
        if (stored is null && !ConnectionProviders.IsBuiltIn(id)) return null;

        return ConnectionProviders.Resolve(id, stored);
    }

    /// <summary>Sets a provider's client (and a custom provider's endpoints), or says which field is wrong.</summary>
    public async Task<(OAuthProvider? Provider, string? Error)> SaveProviderAsync(
        string id, ProviderChange change, ConnectionActor actor, CancellationToken ct = default)
    {
        if (!ConnectionProviders.IsKnownId(id))
        {
            return (null, $"'{id}' is not a provider id: use google, microsoft, or custom- followed by lowercase letters, digits and hyphens.");
        }

        var custom = !ConnectionProviders.IsBuiltIn(id);
        var before = await store.ProviderAsync(id, ct);

        var clientId = change.ClientId?.Trim();
        if (string.IsNullOrEmpty(clientId)) return (null, "`clientId` is required.");
        if (clientId.Length > 512) return (null, "`clientId` is longer than 512 characters.");
        if (change.ClientSecret is { Length: > 4096 }) return (null, "`clientSecret` is longer than 4096 characters.");

        string? tenant = null;

        if (id == ConnectionProviders.Microsoft && !string.IsNullOrWhiteSpace(change.Tenant))
        {
            tenant = change.Tenant.Trim();
            if (tenant.Length > 128 || !tenant.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            {
                return (null, "`tenant` must be a tenant id or domain, or common, organizations or consumers.");
            }
        }

        IReadOnlyList<string> scopes = [];

        if (custom)
        {
            if (ConnectionProviders.UrlRefusal("authorizeUrl", change.AuthorizeUrl, required: true) is { } a) return (null, a);
            if (ConnectionProviders.UrlRefusal("tokenUrl", change.TokenUrl, required: true) is { } t) return (null, t);
            if (ConnectionProviders.UrlRefusal("userinfoUrl", change.UserinfoUrl, required: false) is { } u) return (null, u);

            if (Scopes(change.DefaultScopes) is not { } parsed) return (null, "`defaultScopes` must be scope strings with no spaces.");
            scopes = parsed;

            if (change.Name is { Length: > MaximumNameLength }) return (null, $"`name` is longer than {MaximumNameLength} characters.");
        }

        var stored = new StoredProvider(
            id, ConnectionProviders.KindOf(id),
            custom ? string.IsNullOrWhiteSpace(change.Name) ? null : change.Name.Trim() : null,
            clientId, false, tenant,
            custom ? change.AuthorizeUrl!.Trim() : null,
            custom ? change.TokenUrl!.Trim() : null,
            custom && !string.IsNullOrWhiteSpace(change.UserinfoUrl) ? change.UserinfoUrl.Trim() : null,
            scopes);

        var changed = new List<string>();
        if (before?.ClientId != stored.ClientId) changed.Add("clientId");
        if (change.ClientSecret is not null) changed.Add(change.ClientSecret.Length == 0 ? "clientSecret (cleared)" : "clientSecret");
        if (before?.Tenant != stored.Tenant) changed.Add("tenant");
        if (before?.Name != stored.Name) changed.Add("name");
        if (before?.AuthorizeUrl != stored.AuthorizeUrl) changed.Add("authorizeUrl");
        if (before?.TokenUrl != stored.TokenUrl) changed.Add("tokenUrl");
        if (before?.UserinfoUrl != stored.UserinfoUrl) changed.Add("userinfoUrl");
        if (!(before?.DefaultScopes ?? []).SequenceEqual(stored.DefaultScopes)) changed.Add("defaultScopes");

        await store.SaveProviderAsync(
            stored, change.ClientSecret,
            actor.Row(TenantActions.ConnectionProviderSaved, id, ConnectionProviders.Resolve(id, stored).Name,
                new { provider = id, created = before is null, changed }),
            ct);

        return (await ProviderAsync(id, ct), null);
    }

    // ---- flows ----------------------------------------------------------------------------------

    /// <summary>
    /// Starts a flow for <paramref name="actor"/>: a state bound to them, the verifier kept here,
    /// and the authorization URL to open. <paramref name="origin"/> is the address the request came
    /// to, used for the web callback when no redirect URI is given.
    /// </summary>
    public async Task<(ConnectionStart? Start, string? Error)> StartAsync(
        ConnectionActor actor, StartConnection request, string? origin, CancellationToken ct = default)
    {
        ConnectionRecord? reconnecting = null;

        if (!string.IsNullOrWhiteSpace(request.ReconnectId))
        {
            reconnecting = await store.GetAsync(request.ReconnectId.Trim(), ct);
            if (reconnecting is null) return (null, $"There is no connection '{request.ReconnectId}' to reconnect.");

            if (!string.IsNullOrWhiteSpace(request.Provider) && request.Provider.Trim() != reconnecting.Provider)
            {
                return (null, $"Connection {reconnecting.Named} is a {ConnectionProviders.Display(reconnecting.Provider)} connection; reconnect it with that provider.");
            }
        }

        var providerId = reconnecting?.Provider ?? request.Provider?.Trim();
        if (string.IsNullOrEmpty(providerId)) return (null, "`provider` is required: google, microsoft or a custom provider's id.");

        if (await ProviderAsync(providerId, ct) is not { } provider)
        {
            return (null, $"'{providerId}' is not a provider on this Host. Set one up in {Where} first.");
        }

        if (!provider.Configured)
        {
            return (null, $"{provider.Name} has no OAuth client yet. Set its client ID and secret in {Where} first.");
        }

        if (Scopes(request.Scopes) is not { } asked) return (null, "`scopes` must be scope strings with no spaces.");

        if (request.Name is { } name && name.Trim().Length > MaximumNameLength)
        {
            return (null, $"`name` is longer than {MaximumNameLength} characters.");
        }

        var scopes = Union(provider.DefaultScopes, asked, reconnecting?.Scopes ?? []);
        if (provider.Kind == ConnectionProviders.Microsoft) scopes = Union(scopes, ["offline_access"]);

        var (redirectUri, loopback, redirectError) = Redirect(request.RedirectUri, origin);
        if (redirectError is not null) return (null, redirectError);

        var state = Random(32);
        var verifier = Random(48);
        var now = clock.GetUtcNow();

        await store.InsertFlowAsync(new ConnectionFlow(
            state, actor.Id, provider.Id, scopes, string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
            reconnecting?.Id, redirectUri!, loopback, verifier, now, null), ct);

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = provider.ClientId!,
            ["redirect_uri"] = redirectUri!,
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
        };

        foreach (var (key, value) in provider.AuthorizeParameters) query[key] = value;

        var separator = provider.AuthorizeUrl.Contains('?') ? '&' : '?';
        var url = provider.AuthorizeUrl + separator + string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));

        return (new ConnectionStart(url, state, redirectUri!, now + FlowLifetime), null);
    }

    /// <summary>
    /// Finishes a flow: the state consumed (whatever follows), the code exchanged with the verifier
    /// and redirect URI it started with, the account read, and the connection stored or reconnected.
    /// </summary>
    /// <param name="actor">The caller of <c>complete</c>, who must be the person the state was
    /// issued to; null for the web callback, where the state alone names the person.</param>
    /// <param name="viaCallback">The web callback accepts only a web flow's state; <c>complete</c>
    /// only a loopback (CLI) flow's.</param>
    public async Task<(ConnectionRecord? Connection, bool Reconnected, string? Error)> CompleteAsync(
        string? state, string? code, ConnectionActor? actor, bool viaCallback,
        string? providerError = null, string? providerErrorDescription = null, CancellationToken ct = default)
    {
        const string Unknown = "This sign-in was not started here, has already been used, or is more than 10 minutes old. Start again.";

        if (string.IsNullOrWhiteSpace(state)) return (null, false, Unknown);

        var now = clock.GetUtcNow();
        var flow = await store.ConsumeFlowAsync(state, now, actor?.Id, ct);

        if (flow is null || flow.UsedAt is not null || now - flow.IssuedAt > FlowLifetime) return (null, false, Unknown);

        if (flow.Loopback == viaCallback)
        {
            return (null, false, viaCallback
                ? "This sign-in was started by `yawble connect`; it finishes there, not in the browser. Start again."
                : "This sign-in was started in the browser; it finishes at the Host's callback. Start again.");
        }

        if (providerError is not null)
        {
            return (null, false, $"The provider did not grant access ({providerError}{(string.IsNullOrWhiteSpace(providerErrorDescription) ? "" : ": " + providerErrorDescription)}).");
        }

        if (string.IsNullOrWhiteSpace(code)) return (null, false, "The provider sent back no code. Start again.");

        if (await ProviderAsync(flow.Provider, ct) is not { Configured: true } provider)
        {
            return (null, false, $"'{flow.Provider}' is no longer set up on this Host.");
        }

        var who = actor ?? (flow.UserId == ConnectionActor.Operator.Id ? ConnectionActor.Operator : new ConnectionActor(flow.UserId, null));

        var answer = await endpoints.ExchangeCodeAsync(provider, await store.ClientSecretAsync(provider.Id, ct), code, flow.RedirectUri, flow.CodeVerifier, ct);
        if (!answer.Ok) return (null, false, $"{provider.Name} refused the sign-in: {answer.Reason}.");

        var account = await AccountAsync(provider, answer, ct);
        var granted = Granted(flow.Scopes, answer);
        var tokens = new ConnectionTokens(answer.RefreshToken, answer.AccessToken, Expiry(now, answer.ExpiresIn));

        if (flow.ReconnectId is { } reconnectId)
        {
            var existing = await store.GetAsync(reconnectId, ct);
            if (existing is null) return (null, false, "The connection being reconnected was disconnected meanwhile. Connect it again.");

            if (!string.Equals(existing.Account, account, StringComparison.OrdinalIgnoreCase))
            {
                return (null, false, $"You signed in as {account}, but {existing.Named} is {existing.Account}. Reconnect with the same account, or connect {account} as a new connection.");
            }

            var scopes = Union(existing.Scopes, granted);
            await store.ReconnectAsync(existing.Id, scopes, tokens, who.Row(
                TenantActions.ConnectionReconnected, existing.Id, existing.Name,
                new { connection = existing.Id, provider = provider.Id, account = existing.Account, scopes, wasNeedingReconnect = existing.Status == ConnectionRecord.NeedsReconnect }), ct);

            return (await store.GetAsync(existing.Id, ct), true, null);
        }

        var record = new ConnectionRecord(
            "conn-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
            flow.Name ?? account, provider.Id, account, granted, now, null, ConnectionRecord.Ok, null);

        await store.InsertAsync(record, tokens, flow.UserId, who.Row(
            TenantActions.ConnectionConnected, record.Id, record.Name,
            new { connection = record.Id, provider = provider.Id, account, scopes = granted }), ct);

        return (await store.GetAsync(record.Id, ct), false, null);
    }

    // ---- the run --------------------------------------------------------------------------------

    /// <summary>
    /// A fresh access token for <paramref name="connectionId"/>, or the sentence the run is blocked
    /// with. Serialised per connection: two members sharing it wait for one refresh, and the second
    /// reads the token the first stored.
    /// </summary>
    public async Task<(ConnectionGrant? Grant, string? Refusal)> GrantAsync(string connectionId, string slot, CancellationToken ct = default)
    {
        var gate = _refreshing.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            var connection = await store.GetAsync(connectionId, ct);
            if (connection is null)
            {
                return (null, $"The connection bound for slot `{slot}` no longer exists. A person binds another in the member's settings.");
            }

            if (connection.Status == ConnectionRecord.NeedsReconnect) return (null, NeedsReconnect(connection, connection.StatusReason));

            var tokens = await store.TokensAsync(connectionId, ct) ?? new ConnectionTokens(null, null, null);
            var now = clock.GetUtcNow();

            if (tokens.AccessToken is { } cached && (tokens.AccessExpiresAt is null || tokens.AccessExpiresAt > now + RefreshEarly))
            {
                return (new ConnectionGrant(connection.Id, connection.Provider, connection.Account, cached, tokens.AccessExpiresAt, connection.Scopes), null);
            }

            if (tokens.RefreshToken is null)
            {
                return (null, await MarkAsync(connection, "the provider issued no refresh token, so the access token cannot be renewed", ct));
            }

            if (await ProviderAsync(connection.Provider, ct) is not { Configured: true } provider)
            {
                return (null, $"Connection {connection.Named} could not be refreshed: its provider '{connection.Provider}' has no OAuth client on this Host. Set it in {Where}.");
            }

            var answer = await endpoints.RefreshAsync(provider, await store.ClientSecretAsync(provider.Id, ct), tokens.RefreshToken, ct);

            if (!answer.Ok)
            {
                if (answer.Refused) return (null, await MarkAsync(connection, answer.Reason, ct));

                // TRANSIENT: the provider was not reached. The connection is not the problem.
                return (null, $"Connection {connection.Named} could not be refreshed just now ({answer.Reason}). The next run tries again.");
            }

            var refreshed = new ConnectionTokens(answer.RefreshToken, answer.AccessToken, Expiry(now, answer.ExpiresIn));

            // STORED FIRST: a rotated refresh token is on disk before the access token leaves.
            await store.StoreRefreshAsync(connection.Id, refreshed, ct);

            return (new ConnectionGrant(connection.Id, connection.Provider, connection.Account, answer.AccessToken!, refreshed.AccessExpiresAt, connection.Scopes), null);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> MarkAsync(ConnectionRecord connection, string reason, CancellationToken ct)
    {
        await store.MarkNeedsReconnectAsync(connection.Id, reason, new TriggerAudit(
            null, null, TenantActions.ConnectionNeedsReconnect, connection.Id, connection.Name,
            JsonSerializer.Serialize(new { connection = connection.Id, provider = connection.Provider, account = connection.Account, reason })), ct);

        return NeedsReconnect(connection, reason);
    }

    public static string NeedsReconnect(ConnectionRecord connection, string? reason) =>
        $"Connection {connection.Named} needs to be reconnected: the provider refused to refresh it ({reason ?? "no reason given"}). "
        + $"Reconnect it from {Where}.";

    // ---- bindings -------------------------------------------------------------------------------

    /// <summary>
    /// Why <paramref name="bindings"/> (slot to connection id) cannot be bound on a member of
    /// <paramref name="manifest"/>, or null. <paramref name="agentTeam"/> is set for an agent's hire
    /// (a Manager or a Concierge): each connection must then be one a person already bound on that
    /// team.
    /// </summary>
    public async Task<BindingRefusal?> BindingRefusalAsync(
        PluginManifest manifest, IReadOnlyDictionary<string, string> bindings, IReadOnlySet<string>? boundOnTeam,
        CancellationToken ct = default)
    {
        foreach (var (slot, connectionId) in bindings)
        {
            if (!manifest.Connections.TryGetValue(slot, out var declared))
            {
                return new BindingRefusal($"`{slot}` is not a connection slot of plugin '{manifest.Id}'. It has: "
                    + (manifest.Connections.Count == 0 ? "none" : string.Join(", ", manifest.Connections.Keys)) + ".");
            }

            if (boundOnTeam is not null && !boundOnTeam.Contains(connectionId))
            {
                return new BindingRefusal(
                    $"The connection '{connectionId}' named for slot `{slot}` is not one a person has bound on this team. "
                    + "Only a person binds a connection first; a Manager may then name the same one when hiring onto this team.");
            }

            if (await store.GetAsync(connectionId, ct) is not { } connection)
            {
                return new BindingRefusal($"There is no connection '{connectionId}'. A person connects an account in {Where}.");
            }

            if (ScopeRefusal(slot, declared, connection) is { } refusal) return refusal;
        }

        return null;
    }

    /// <summary>Why <paramref name="connection"/> cannot serve <paramref name="slot"/>: the wrong
    /// provider, or a scope it was not granted (offering Reconnect).</summary>
    public static BindingRefusal? ScopeRefusal(string slot, PluginConnectionSlot declared, ConnectionRecord connection)
    {
        if (!declared.Admits(connection.Provider))
        {
            var names = declared.Providers.Select(ConnectionProviders.Display).ToList();
            var joined = names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1];
            return new BindingRefusal($"Slot `{slot}` takes a {joined} connection; {connection.Named} is a {ConnectionProviders.Display(connection.Provider)} connection.");
        }

        var missing = declared.ScopesFor(connection.Provider).Where(s => !connection.Scopes.Contains(s, StringComparer.Ordinal)).ToList();

        if (missing.Count > 0)
        {
            return new BindingRefusal(
                $"Connection {connection.Named} was not granted the scope{(missing.Count == 1 ? "" : "s")} "
                + string.Join(", ", missing.Select(s => $"`{s}`"))
                + $" that slot `{slot}` needs. Reconnect it from {Where} with {(missing.Count == 1 ? "that scope" : "those scopes")}, then bind it again.",
                connection.Id, missing);
        }

        return null;
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>The account's name: the ID token's <c>email</c> / <c>preferred_username</c>, else the
    /// provider's userinfo.</summary>
    private async Task<string> AccountAsync(OAuthProvider provider, OAuthTokenAnswer answer, CancellationToken ct)
    {
        string[] keys = provider.Kind switch
        {
            ConnectionProviders.Google => ["email"],
            ConnectionProviders.Microsoft => ["preferred_username", "email", "upn"],
            _ => ["email", "preferred_username", "name", "login", "sub"],
        };

        if (provider.Kind != ConnectionProviders.Custom && Claim(HttpOAuthEndpoints.IdTokenClaims(answer.IdToken), keys) is { } fromIdToken)
        {
            return fromIdToken;
        }

        if (answer.AccessToken is { } access && Claim(await endpoints.UserInfoAsync(provider, access, ct), keys) is { } fromUserInfo)
        {
            return fromUserInfo;
        }

        return Claim(HttpOAuthEndpoints.IdTokenClaims(answer.IdToken), keys) ?? "(unknown account)";
    }

    private static string? Claim(JsonElement? claims, IEnumerable<string> keys)
    {
        if (claims is not { ValueKind: JsonValueKind.Object } found) return null;

        foreach (var key in keys)
        {
            if (found.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text) return text.Trim();
                if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
            }
        }

        return null;
    }

    /// <summary>What was granted: the token response's <c>scope</c> when it has one, plus the identity
    /// scopes asked for (a provider often leaves <c>openid</c> and <c>offline_access</c> out).</summary>
    private static IReadOnlyList<string> Granted(IReadOnlyList<string> asked, OAuthTokenAnswer answer)
    {
        if (string.IsNullOrWhiteSpace(answer.Scope)) return asked;

        var granted = answer.Scope.Split((char[])[' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Union(granted, asked.Where(ConnectionProviders.IdentityScopes.Contains));
    }

    private static DateTimeOffset? Expiry(DateTimeOffset now, int? expiresIn) =>
        expiresIn is > 0 ? now.AddSeconds(expiresIn.Value) : null;

    private static (string? Uri, bool Loopback, string? Error) Redirect(string? requested, string? origin)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            if (string.IsNullOrWhiteSpace(origin)) return (null, false, "`redirectUri` is required here.");
            return (origin.TrimEnd('/') + CallbackPath, false, null);
        }

        if (!Uri.TryCreate(requested.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return (null, false, "`redirectUri` must be this Host's " + CallbackPath + " or a loopback address such as http://127.0.0.1:53121/.");
        }

        if (uri.AbsolutePath == CallbackPath) return (requested.Trim(), false, null);

        if (uri.Scheme == "http" && uri.IsLoopback && !uri.IsDefaultPort) return (requested.Trim(), true, null);

        return (null, false, "`redirectUri` must be this Host's " + CallbackPath + " or a loopback address with a port, such as http://127.0.0.1:53121/.");
    }

    public static IReadOnlyList<string>? Scopes(IEnumerable<string>? scopes)
    {
        var list = new List<string>();

        foreach (var scope in scopes ?? [])
        {
            if (string.IsNullOrWhiteSpace(scope) || scope.Trim().Any(char.IsWhiteSpace) || scope.Length > 512) return null;
            if (!list.Contains(scope.Trim(), StringComparer.Ordinal)) list.Add(scope.Trim());
        }

        return list;
    }

    private static IReadOnlyList<string> Union(params IEnumerable<string>[] lists) =>
        [.. lists.SelectMany(l => l).Distinct(StringComparer.Ordinal)];

    private static string Random(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Body of <c>PUT /api/connections/providers/{id}</c>.</summary>
public sealed record ProviderChange(
    string? ClientId, string? ClientSecret, string? Tenant = null, string? Name = null,
    string? AuthorizeUrl = null, string? TokenUrl = null, string? UserinfoUrl = null, IReadOnlyList<string>? DefaultScopes = null);

/// <summary>Body of <c>POST /api/connections/start</c>.</summary>
public sealed record StartConnection(
    string? Provider, IReadOnlyList<string>? Scopes = null, string? Name = null, string? ReconnectId = null, string? RedirectUri = null);

/// <summary>Body of <c>POST /api/connections/complete</c>.</summary>
public sealed record CompleteConnection(string? State, string? Code);
