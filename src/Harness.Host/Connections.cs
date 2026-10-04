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

/// <summary>The answer to a sign-in with a code: what the person is shown. Never the device code.</summary>
public sealed record DeviceStart(string FlowId, string UserCode, string VerificationUri, DateTimeOffset ExpiresAt)
{
    public object Body() => new { flowId = FlowId, userCode = UserCode, verificationUri = VerificationUri, expiresAt = ExpiresAt };
}

/// <summary>A sign-in with a code as it stands: <c>waiting</c>, <c>done</c> (with the connection's
/// id), <c>refused</c> or <c>expired</c>, and the sentence that says so.</summary>
public sealed record DeviceFlowState(string State, string Sentence, string? ConnectionId);

/// <summary>Who acts: a person's id and email, or the operator at the engine.</summary>
public sealed record ConnectionActor(string Id, string? Email)
{
    /// <summary>The operator CLI's `connect`, through the file exchange: root on the instance.</summary>
    public static readonly ConnectionActor Operator = new("operator", "operator (CLI connect)");

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
public sealed class Connections(
    ConnectionStore store, IOAuthEndpoints endpoints, TimeProvider clock, IUserStore? users = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    public static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan RefreshEarly = TimeSpan.FromMinutes(5);

    public const string CallbackPath = "/api/connections/callback";

    public const int MaximumNameLength = 80;

    /// <summary>Where a person reconnects, as every sentence names it.</summary>
    public const string Where = "Admin → Connections";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshing = new(StringComparer.Ordinal);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((wait, ct) => Task.Delay(wait, clock, ct));

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

        if (id == ConnectionProviders.Google && !ConnectionProviders.IsGoogleClientId(clientId))
        {
            return (null, "`clientId` is not a Google OAuth client ID: it looks like <numbers>-<letters>.apps.googleusercontent.com. Copy it from the client's page.");
        }
        if (change.ClientSecret is { Length: > 4096 }) return (null, "`clientSecret` is longer than 4096 characters.");

        string? tenant = null;
        var secret = change.ClientSecret;

        if (!string.IsNullOrWhiteSpace(change.Audience))
        {
            // THE GUIDED MICROSOFT SAVE: a public client, signed in with a code. Its tenant is who can
            // sign in, and it holds no secret, so the exchange and every refresh send none.
            if (id != ConnectionProviders.Microsoft) return (null, "`audience` is for Microsoft only.");
            if (!string.IsNullOrWhiteSpace(change.Tenant)) return (null, "`audience` and `tenant` do not go together: `tenant` is the Advanced form's.");
            if (!string.IsNullOrEmpty(change.ClientSecret))
            {
                return (null, "A Microsoft app saved with who can sign in is a public client and takes no `clientSecret`. Leave it out, or set a secret under Advanced.");
            }

            if (!ConnectionProviders.IsGuid(clientId))
            {
                return (null, "`clientId` is not an Application (client) ID: it looks like xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx. Copy it from the app's Overview.");
            }

            switch (change.Audience.Trim())
            {
                case ConnectionProviders.AudienceCommon:
                    tenant = ConnectionProviders.MicrosoftDefaultTenant;
                    break;
                case ConnectionProviders.AudienceOrganizations:
                    tenant = ConnectionProviders.AudienceOrganizations;
                    break;
                case ConnectionProviders.AudienceTenant:
                    if (!ConnectionProviders.IsGuid(change.TenantId))
                    {
                        return (null, "`tenantId` is not a Directory (tenant) ID: it looks like xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx. Copy it from the app's Overview.");
                    }
                    tenant = change.TenantId!.Trim().ToLowerInvariant();
                    break;
                default:
                    return (null, "`audience` is common (personal and any work account), organizations (work accounts only) or tenant (only your organisation, with `tenantId`).");
            }

            secret = "";
        }
        else if (id == ConnectionProviders.Microsoft && !string.IsNullOrWhiteSpace(change.Tenant))
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
        if (secret is { Length: > 0 }) changed.Add("clientSecret");
        else if (secret is not null && (change.ClientSecret is not null || before?.ClientSecretSet == true)) changed.Add("clientSecret (cleared)");
        if (before?.Tenant != stored.Tenant) changed.Add("tenant");
        if (before?.Name != stored.Name) changed.Add("name");
        if (before?.AuthorizeUrl != stored.AuthorizeUrl) changed.Add("authorizeUrl");
        if (before?.TokenUrl != stored.TokenUrl) changed.Add("tokenUrl");
        if (before?.UserinfoUrl != stored.UserinfoUrl) changed.Add("userinfoUrl");
        if (!(before?.DefaultScopes ?? []).SequenceEqual(stored.DefaultScopes)) changed.Add("defaultScopes");

        await store.SaveProviderAsync(
            stored, secret,
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
        if (!string.IsNullOrWhiteSpace(request.Flow))
        {
            return (null, request.Flow.Trim() == DeviceFlow
                ? "A sign-in with a code starts with StartDeviceAsync."
                : $"`flow` is \"{DeviceFlow}\" to sign in with a code, or left out for the browser sign-in.");
        }

        var (provider, reconnecting, scopes, error) = await PrepareAsync(request, ct);
        if (error is not null) return (null, error);

        var (redirectUri, loopback, redirectError) = Redirect(request.RedirectUri, origin);
        if (redirectError is not null) return (null, redirectError);

        var state = Random(32);
        var verifier = Random(48);
        var now = clock.GetUtcNow();

        await store.InsertFlowAsync(new ConnectionFlow(
            state, actor.Id, provider!.Id, scopes!, string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
            reconnecting?.Id, redirectUri!, loopback, verifier, now, null), ct);

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = provider.ClientId!,
            ["redirect_uri"] = redirectUri!,
            ["scope"] = string.Join(' ', scopes!),
            ["state"] = state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
        };

        foreach (var (key, value) in provider.AuthorizeParameters) query[key] = value;

        var separator = provider.AuthorizeUrl.Contains('?') ? '&' : '?';
        var url = provider.AuthorizeUrl + separator + string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));

        return (new ConnectionStart(url, state, redirectUri!, now + FlowLifetime), null);
    }

    /// <summary>What both flows check first: the connection reconnected, the provider set up, and
    /// the scopes to ask - the provider's defaults, the asked, and a reconnect's old ones.</summary>
    private async Task<(OAuthProvider? Provider, ConnectionRecord? Reconnecting, IReadOnlyList<string>? Scopes, string? Error)> PrepareAsync(
        StartConnection request, CancellationToken ct)
    {
        ConnectionRecord? reconnecting = null;

        if (!string.IsNullOrWhiteSpace(request.ReconnectId))
        {
            reconnecting = await store.GetAsync(request.ReconnectId.Trim(), ct);
            if (reconnecting is null) return (null, null, null, $"There is no connection '{request.ReconnectId}' to reconnect.");

            if (!string.IsNullOrWhiteSpace(request.Provider) && request.Provider.Trim() != reconnecting.Provider)
            {
                return (null, null, null, $"Connection {reconnecting.Named} is a {ConnectionProviders.Display(reconnecting.Provider)} connection; reconnect it with that provider.");
            }
        }

        var providerId = reconnecting?.Provider ?? request.Provider?.Trim();
        if (string.IsNullOrEmpty(providerId)) return (null, null, null, "`provider` is required: google, microsoft or a custom provider's id.");

        if (await ProviderAsync(providerId, ct) is not { } provider)
        {
            return (null, null, null, $"'{providerId}' is not a provider on this Host. Set one up in {Where} first.");
        }

        if (!provider.Configured)
        {
            return (null, null, null, $"{provider.Name} has no OAuth client yet. Set its client ID{(provider.DeviceAuthorizationUrl is null ? " and secret" : "")} in {Where} first.");
        }

        if (Scopes(request.Scopes) is not { } asked) return (null, null, null, "`scopes` must be scope strings with no spaces.");

        if (request.Name is { } name && name.Trim().Length > MaximumNameLength)
        {
            return (null, null, null, $"`name` is longer than {MaximumNameLength} characters.");
        }

        var scopes = Union(provider.DefaultScopes, asked, reconnecting?.Scopes ?? []);
        if (provider.Kind == ConnectionProviders.Microsoft) scopes = Union(scopes, ["offline_access"]);

        return (provider, reconnecting, scopes, null);
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
                ? "This sign-in was started by the operator CLI's `connect`; it finishes there, not in the browser. Start again."
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

        // The web callback carries no session: the person is the one the state was issued to.
        var who = actor ?? (flow.UserId == ConnectionActor.Operator.Id
            ? ConnectionActor.Operator
            : new ConnectionActor(flow.UserId, users is null ? null : (await users.FindByIdAsync(flow.UserId, ct))?.Email));

        var answer = await endpoints.ExchangeCodeAsync(provider, await store.ClientSecretAsync(provider.Id, ct), code, flow.RedirectUri, flow.CodeVerifier, ct);
        if (!answer.Ok) return (null, false, $"{provider.Name} refused the sign-in: {answer.Reason}.");

        return await FinishAsync(provider, answer, flow.UserId, flow.Scopes, flow.Name, flow.ReconnectId, who, now, ct);
    }

    /// <summary>
    /// THE ONE COMPLETION of every flow, once the provider has answered tokens: the account read
    /// from the ID token or userinfo, then a new connection, or a reconnect that must be the same
    /// account with the scopes merged. Tokens are stored as ciphertext, with the tenant row in the
    /// same transaction.
    /// </summary>
    private async Task<(ConnectionRecord? Connection, bool Reconnected, string? Error)> FinishAsync(
        OAuthProvider provider, OAuthTokenAnswer answer, string userId, IReadOnlyList<string> asked, string? name,
        string? reconnectId, ConnectionActor who, DateTimeOffset now, CancellationToken ct)
    {
        var account = await AccountAsync(provider, answer, ct);
        var granted = Granted(asked, answer);
        var tokens = new ConnectionTokens(answer.RefreshToken, answer.AccessToken, Expiry(now, answer.ExpiresIn));

        if (reconnectId is not null)
        {
            // UNDER THE REFRESH'S GATE: a refresh in flight with the old refresh token finishes
            // first, so it can neither store its tokens over these nor mark this reconnect refused.
            var gate = Gate(reconnectId);
            await gate.WaitAsync(ct);

            try
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
            finally
            {
                gate.Release();
            }
        }

        var record = new ConnectionRecord(
            "conn-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
            name ?? account, provider.Id, account, granted, now, null, ConnectionRecord.Ok, null);

        await store.InsertAsync(record, tokens, userId, who.Row(
            TenantActions.ConnectionConnected, record.Id, record.Name,
            new { connection = record.Id, provider = provider.Id, account, scopes = granted }), ct);

        return (await store.GetAsync(record.Id, ct), false, null);
    }

    // ---- sign in with a code (the device flow) ---------------------------------------------------

    public const string DeviceFlow = "device";

    public const string Waiting = "waiting";

    public const string Done = "done";

    public const string Refused = "refused";

    public const string Expired = "expired";

    /// <summary>How long a finished sign-in with a code can still be read, for a dialog reopened late.</summary>
    public static readonly TimeSpan DeviceFlowKept = TimeSpan.FromHours(1);

    /// <summary>What a provider's <c>slow_down</c> adds to the wait between polls (RFC 8628).</summary>
    public static readonly TimeSpan SlowDown = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, DeviceSignIn> _deviceFlows = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Starts a sign-in with a code for <paramref name="actor"/>: the provider's device endpoint is
    /// asked for a code, the DEVICE CODE IS KEPT HERE - in memory only, never in a row, a log or a
    /// file, and spent once - and the Host polls the token endpoint by itself until the person
    /// approves, refuses, or the code expires. Only the user code, the link and the expiry are
    /// answered.
    /// </summary>
    public async Task<(DeviceStart? Start, string? Error)> StartDeviceAsync(
        ConnectionActor actor, StartConnection request, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(request.RedirectUri))
        {
            return (null, "`redirectUri` is not used when signing in with a code; leave it out.");
        }

        var (provider, reconnecting, scopes, error) = await PrepareAsync(request, ct);
        if (error is not null) return (null, error);

        if (provider!.DeviceAuthorizationUrl is null)
        {
            return (null, $"{provider.Name} has no sign-in with a code on this Host; sign in through the browser instead.");
        }

        var answer = await endpoints.DeviceAuthorizationAsync(provider, scopes!, ct);
        if (!answer.Ok) return (null, $"{provider.Name} did not start the sign-in: {answer.Reason}.");

        var now = clock.GetUtcNow();
        Sweep(now);

        var flow = new DeviceSignIn(
            Random(24), actor, scopes!, string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(), reconnecting?.Id,
            answer.DeviceCode!, answer.VerificationUri!,
            answer.ExpiresIn is > 0 ? now.AddSeconds(answer.ExpiresIn.Value) : now + FlowLifetime);

        _deviceFlows[flow.Id] = flow;
        _ = Task.Run(() => PollAsync(flow, provider, answer.Interval is > 0 ? TimeSpan.FromSeconds(answer.Interval.Value) : DefaultInterval));

        return (new DeviceStart(flow.Id, answer.UserCode!, flow.VerificationUri, flow.ExpiresAt), null);
    }

    /// <summary>A sign-in with a code as its starter reads it, or null - for a flow that does not
    /// exist and, alike, for one somebody else started.</summary>
    public DeviceFlowState? DeviceFlowOf(string flowId, ConnectionActor actor) =>
        _deviceFlows.TryGetValue(flowId, out var flow) && string.Equals(flow.Actor.Id, actor.Id, StringComparison.Ordinal)
            ? flow.Read()
            : null;

    /// <summary>
    /// Polls at the provider's interval, 5 s more after each <c>slow_down</c>, until the person
    /// approves (the same completion as the web flow), refuses, or the code expires. Refusal and
    /// expiry store nothing. A provider that cannot be reached is asked again at the next interval.
    /// </summary>
    private async Task PollAsync(DeviceSignIn flow, OAuthProvider provider, TimeSpan interval)
    {
        var ct = _stopping.Token;

        try
        {
            var secret = await store.ClientSecretAsync(provider.Id, ct);

            while (true)
            {
                await _delay(interval, ct);

                if (clock.GetUtcNow() >= flow.ExpiresAt)
                {
                    flow.Settle(Expired, ExpiredSentence);
                    return;
                }

                var answer = await endpoints.DeviceTokenAsync(provider, secret, flow.DeviceCode!, ct);

                if (answer.Ok)
                {
                    var (connection, reconnected, refusal) = await FinishAsync(
                        provider, answer, flow.Actor.Id, flow.Scopes, flow.Name, flow.ReconnectId, flow.Actor, clock.GetUtcNow(), ct);

                    if (refusal is not null) flow.Settle(Refused, refusal);
                    else flow.Settle(Done, $"{(reconnected ? "Reconnected" : "Connected")} {connection!.Named}.", connection.Id);
                    return;
                }

                switch (answer.Error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += SlowDown;
                        continue;
                    case "expired_token" or "code_expired":
                        flow.Settle(Expired, ExpiredSentence);
                        return;
                    case "access_denied" or "authorization_declined":
                        flow.Settle(Refused, $"The sign-in was declined at {provider.Name}. Nothing was stored; try again.");
                        return;
                }

                if (answer.Refused)
                {
                    flow.Settle(Refused, $"{provider.Name} refused the sign-in: {answer.Reason}. Nothing was stored; try again.");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The Host is stopping; nobody is left to read the flow.
        }
        catch (Exception exception)
        {
            flow.Settle(Refused, exception is Microsoft.Data.Sqlite.SqliteException sqlite
                ? $"The connection could not be stored ({sqlite.SqliteErrorCode}). Try again."
                : $"The sign-in could not be finished ({exception.GetType().Name}). Try again.");
        }
    }

    private const string ExpiredSentence = "The code expired before the sign-in was finished. Nothing was stored; try again.";

    /// <summary>Finished flows go a while after their expiry; nothing reads them later than that.</summary>
    private void Sweep(DateTimeOffset now)
    {
        foreach (var (id, flow) in _deviceFlows)
        {
            if (flow.ExpiresAt + DeviceFlowKept < now && flow.Read().State != Waiting) _deviceFlows.TryRemove(id, out _);
        }
    }

    /// <summary>Stops every poll still running.</summary>
    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    /// <summary>One sign-in with a code, in memory. The device code goes when the flow settles.</summary>
    private sealed class DeviceSignIn(
        string id, ConnectionActor actor, IReadOnlyList<string> scopes, string? name, string? reconnectId,
        string deviceCode, string verificationUri, DateTimeOffset expiresAt)
    {
        private readonly Lock _lock = new();

        private DeviceFlowState _state = new(Waiting, $"Waiting for you to enter the code at {verificationUri}.", null);

        public string Id => id;

        public ConnectionActor Actor => actor;

        public IReadOnlyList<string> Scopes => scopes;

        public string? Name => name;

        public string? ReconnectId => reconnectId;

        public string VerificationUri => verificationUri;

        public DateTimeOffset ExpiresAt => expiresAt;

        public string? DeviceCode { get; private set; } = deviceCode;

        public void Settle(string state, string sentence, string? connectionId = null)
        {
            lock (_lock)
            {
                _state = new DeviceFlowState(state, sentence, connectionId);
                DeviceCode = null;
            }
        }

        public DeviceFlowState Read()
        {
            lock (_lock) return _state;
        }
    }

    // ---- the run --------------------------------------------------------------------------------

    /// <summary>
    /// A fresh access token for <paramref name="connectionId"/>, or the sentence the run is blocked
    /// with. Serialised per connection: two members sharing it wait for one refresh, and the second
    /// reads the token the first stored.
    /// </summary>
    public async Task<(ConnectionGrant? Grant, string? Refusal)> GrantAsync(string connectionId, string slot, CancellationToken ct = default)
    {
        var gate = Gate(connectionId);
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
            try
            {
                await store.StoreRefreshAsync(connection.Id, refreshed, ct);
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception)
            {
                throw new RefreshNotStoredException(exception);
            }

            return (new ConnectionGrant(connection.Id, connection.Provider, connection.Account, answer.AccessToken!, refreshed.AccessExpiresAt, connection.Scopes), null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>One gate per connection: its refreshes and its reconnects, one at a time.</summary>
    private SemaphoreSlim Gate(string connectionId) => _refreshing.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));

    /// <summary>A disconnected connection's gate is dropped, so the gates do not grow without end.</summary>
    public void Forget(string connectionId) => _refreshing.TryRemove(connectionId, out _);

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

/// <summary>A refresh the provider answered whose tokens could not be stored: the access token is
/// not handed out, since a rotated refresh token lost here would strand the connection.</summary>
public sealed class RefreshNotStoredException(Microsoft.Data.Sqlite.SqliteException inner)
    : Exception("A refreshed token could not be stored.", inner)
{
    public int SqliteErrorCode => inner.SqliteErrorCode;
}

/// <summary>Body of <c>PUT /api/connections/providers/{id}</c>.</summary>
public sealed record ProviderChange(
    string? ClientId, string? ClientSecret, string? Tenant = null, string? Name = null,
    string? AuthorizeUrl = null, string? TokenUrl = null, string? UserinfoUrl = null, IReadOnlyList<string>? DefaultScopes = null,
    string? Audience = null, string? TenantId = null);

/// <summary>Body of <c>POST /api/connections/start</c>.</summary>
public sealed record StartConnection(
    string? Provider, IReadOnlyList<string>? Scopes = null, string? Name = null, string? ReconnectId = null, string? RedirectUri = null,
    string? Flow = null);

/// <summary>Body of <c>POST /api/connections/complete</c>.</summary>
public sealed record CompleteConnection(string? State, string? Code);
