using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Microsoft.AspNetCore.Http;

namespace Harness.Host;

/// <summary>What a run is handed for one slot: the fresh access token and what it is for, or for a
/// mailbox its servers and app password. A Microsoft slot also has <see cref="Tokens"/>: one fresh
/// access token per API its scopes name, the first of them being <see cref="AccessToken"/>.</summary>
public sealed record ConnectionGrant(
    string ConnectionId, string Provider, string Account, string AccessToken, DateTimeOffset? ExpiresAt, IReadOnlyList<string> Scopes,
    MailboxGrant? Mailbox = null, IReadOnlyList<ApiToken>? Tokens = null, CredentialGrant? Credential = null)
{
    /// <summary>The values the run's redaction set takes: every access token, the app password, or a
    /// credential's masked values.</summary>
    public IReadOnlyList<string> Secrets => Mailbox is { } mailbox
        ? [mailbox.Password]
        : Credential is { } credential ? credential.Secrets
        : [.. new[] { AccessToken }.Concat(Tokens?.Select(t => t.AccessToken) ?? []).Distinct(StringComparer.Ordinal)];
}

/// <summary>One Microsoft API's fresh access token, as a run is handed it. Its printed form leaves the
/// token out.</summary>
public sealed record ApiToken(string Api, string AccessToken, DateTimeOffset? ExpiresAt, IReadOnlyList<string> Scopes)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Api = {Api}, ExpiresAt = {ExpiresAt}, Scopes = [{string.Join(' ', Scopes)}]");
        return true;
    }
}

/// <summary>A mailbox as a run is handed it. Its printed form leaves the password out.</summary>
public sealed record MailboxGrant(string Username, string Password, MailServer Imap, MailServer Smtp)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Username = {Username}, Imap = {Imap}, Smtp = {Smtp}");
        return true;
    }
}

/// <summary>A mailbox write's answer: the HTTP status, the login's sentence or the refusal, and the
/// connection when it was saved.</summary>
public sealed record MailboxAnswer(int Status, string? Sentence, string? Error, ConnectionRecord? Connection);

/// <summary>A Test connection's answer: the health it recorded (<c>ok</c> or <c>needs-reconnect</c>) and
/// its sentence, or a status and <see cref="Error"/> when nothing was recorded.</summary>
public sealed record ConnectionTestAnswer(int Status, string? Health, string? Sentence, string? Error, ConnectionRecord? Connection);

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

/// <summary>A person's sign-in with a code still waiting, as a reopened dialog picks it back up.
/// Never the device code.</summary>
public sealed record OpenDeviceFlow(string FlowId, string Provider, string UserCode, string VerificationUri, DateTimeOffset ExpiresAt, string State)
{
    public object Body() => new { flowId = FlowId, provider = Provider, userCode = UserCode, verificationUri = VerificationUri, expiresAt = ExpiresAt, state = State };
}

/// <summary>A sign-in with a code as it stands: <c>waiting</c>, <c>done</c> (with the connection's
/// id), <c>refused</c> or <c>expired</c>, and the sentence that says so, with the code the person
/// enters, where, and until when: a second code replaces the first in place. Never the device code.</summary>
public sealed record DeviceFlowState(
    string State, string Sentence, string? ConnectionId, string UserCode, string VerificationUri, DateTimeOffset ExpiresAt);

/// <summary>Who acts: a person's id and email, or the operator at the engine.</summary>
public sealed record ConnectionActor(string Id, string? Email)
{
    /// <summary>The operator CLI's `connect`, through the file exchange: root on the instance.</summary>
    public static readonly ConnectionActor Operator = new("operator", "operator (CLI connect)");

    /// <summary>A member asking through Team Connections (a test by name): no person's id, its name as who.</summary>
    public static ConnectionActor ForMember(ContainerId member) => new(MemberPrefix + member, $"{TeamConnectionsMember.Label} for {member}");

    private const string MemberPrefix = "member:";

    public TriggerAudit Row(string action, string? subject, string? subjectName, object detail) =>
        new(this == Operator || Id.StartsWith(MemberPrefix, StringComparison.Ordinal) ? null : Id, Email, action, subject, subjectName, JsonSerializer.Serialize(detail));
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
public sealed partial class Connections(
    IConnectionStore store, IOAuthEndpoints endpoints, TimeProvider clock, IUserStore? users = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null, IMailLogin? mail = null, string? microsoftClientId = null) : IDisposable
{
    /// <summary>
    /// THE INSTANCE'S MICROSOFT CLIENT: a public client's Application (client) ID, named by whoever
    /// starts the Host (the operator CLI passes its published app's), used while no Microsoft client is
    /// saved. The Host names no ID of its own; with none, or one not shaped like a GUID, Microsoft is not
    /// set up until a person saves a client.
    /// </summary>
    public const string MicrosoftClientSetting = "Connections:MicrosoftClientId";

    public const string MicrosoftClientVariable = "HARNESS_MICROSOFT_CLIENT_ID";

    private readonly string? _microsoftClient = ConnectionProviders.IsGuid(microsoftClientId) ? microsoftClientId!.Trim() : null;

    /// <summary>How long a mailbox's last good login is trusted before a run logs in again.</summary>
    public static readonly TimeSpan MailboxRecheck = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan RefreshEarly = TimeSpan.FromMinutes(5);

    public const string CallbackPath = "/api/connections/callback";

    public const int MaximumNameLength = 80;

    /// <summary>Where a person reconnects, as every sentence names it.</summary>
    public const string Where = "Admin → Connections";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshing = new(StringComparer.Ordinal);

    /// <summary>Each further Microsoft API's access token, per connection and API: in this Host's
    /// memory only, never in a row or a file. A restart redeems it again from the refresh token.</summary>
    private readonly ConcurrentDictionary<(string Connection, string Api), ApiToken> _apiTokens = new();

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((wait, ct) => Task.Delay(wait, clock, ct));

    public IConnectionStore Store => store;

    public IOAuthEndpoints Endpoints => endpoints;

    // ---- providers ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<OAuthProvider>> ProvidersAsync(CancellationToken ct = default)
    {
        var stored = (await store.ProvidersAsync(ct)).ToDictionary(p => p.Id, StringComparer.Ordinal);

        return
        [
            .. ConnectionProviders.BuiltIns.Select(id => ConnectionProviders.Resolve(id, stored.GetValueOrDefault(id), _microsoftClient)),
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

        return ConnectionProviders.Resolve(id, stored, _microsoftClient);
    }

    /// <summary>The client secret sent to <paramref name="provider"/>: the saved one, and never one for the
    /// instance's default client, which is public.</summary>
    private async Task<string?> ClientSecretAsync(OAuthProvider provider, CancellationToken ct) =>
        provider.ClientSource == ConnectionProviders.ClientSourceDefault ? null : await store.ClientSecretAsync(provider.Id, ct);

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

        var (provider, reconnecting, scopes, directory, error) = await PrepareAsync(request, ct);
        if (error is not null) return (null, error);

        // The instance's default client is public and registers no redirect URI: it signs in with a code.
        if (provider!.ClientSource == ConnectionProviders.ClientSourceDefault)
        {
            return (null, $"{provider.Name} signs in with a code on this Host: start it with `flow: \"{DeviceFlow}\"`.");
        }

        var (redirectUri, loopback, redirectError) = Redirect(request.RedirectUri, origin);
        if (redirectError is not null) return (null, redirectError);

        var state = Random(32);
        var verifier = Random(48);
        var now = clock.GetUtcNow();

        await store.InsertFlowAsync(new ConnectionFlow(
            state, actor.Id, provider!.Id, scopes!, string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
            reconnecting?.Id, redirectUri!, loopback, verifier, now, null, directory), ct);

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

    /// <summary>What both flows check first: the connection reconnected, the provider set up, the
    /// scopes to ask - the provider's defaults, the asked, and a reconnect's old ones - and the
    /// directory signed in through: the asked one, else a reconnect's own (an empty one asked clears
    /// it), the provider answered at that directory's endpoints.</summary>
    private async Task<(OAuthProvider? Provider, ConnectionRecord? Reconnecting, IReadOnlyList<string>? Scopes, string? Directory, string? Error)> PrepareAsync(
        StartConnection request, CancellationToken ct)
    {
        ConnectionRecord? reconnecting = null;

        if (!string.IsNullOrWhiteSpace(request.ReconnectId))
        {
            reconnecting = await store.GetAsync(request.ReconnectId.Trim(), ct);
            if (reconnecting is null) return (null, null, null, null, $"There is no connection '{request.ReconnectId}' to reconnect.");

            if (reconnecting.Mailbox is not null)
            {
                return (null, null, null, null, $"Connection {reconnecting.Named} is a mailbox; update its password instead of reconnecting it.");
            }

            if (reconnecting.Credential is not null)
            {
                return (null, null, null, null, $"Connection {reconnecting.Named} is a {ConnectionKinds.Display(reconnecting.Kind)} connection; edit its fields instead of reconnecting it.");
            }

            if (!string.IsNullOrWhiteSpace(request.Provider) && request.Provider.Trim() != reconnecting.Provider)
            {
                return (null, null, null, null, $"Connection {reconnecting.Named} is a {ConnectionProviders.Display(reconnecting.Provider)} connection; reconnect it with that provider.");
            }
        }

        var providerId = reconnecting?.Provider ?? request.Provider?.Trim();
        if (string.IsNullOrEmpty(providerId)) return (null, null, null, null, "`provider` is required: google, microsoft or a custom provider's id.");

        if (await ProviderAsync(providerId, ct) is not { } provider)
        {
            return (null, null, null, null, $"'{providerId}' is not a provider on this Host. Set one up in {Where} first.");
        }

        if (!provider.Configured)
        {
            return (null, null, null, null, $"{provider.Name} has no OAuth client yet. Set its client ID{(provider.DeviceAuthorizationUrl is null ? " and secret" : "")} in {Where} first.");
        }

        if (Scopes(request.Scopes) is not { } asked) return (null, null, null, null, "`scopes` must be scope strings with no spaces.");

        if (request.Name is { } name && name.Trim().Length > MaximumNameLength)
        {
            return (null, null, null, null, $"`name` is longer than {MaximumNameLength} characters.");
        }

        // THE NAME IS THE KEY: a new connection's name is refused before anyone signs in, not after.
        if (reconnecting is null && !string.IsNullOrWhiteSpace(request.Name) && await store.NameTakenAsync(request.Name.Trim(), null, ct))
        {
            return (null, null, null, null, NameTaken(request.Name.Trim()));
        }

        var directory = reconnecting?.Tenant;

        if (request.Tenant is { } named)
        {
            if (string.IsNullOrWhiteSpace(named)) directory = null;
            else if (provider.Kind != ConnectionProviders.Microsoft)
            {
                return (null, null, null, null, $"{ConnectionProviders.DirectoryField} is for a Microsoft connection only; leave it out for {provider.Name}.");
            }
            else if (ConnectionProviders.ParseDirectory(named) is { } parsed) directory = parsed;
            else return (null, null, null, null, ConnectionProviders.DirectoryRefusal);
        }

        var scopes = Union(provider.DefaultScopes, asked, reconnecting?.Scopes ?? []);
        if (provider.Kind == ConnectionProviders.Microsoft) scopes = Union(scopes, ["offline_access"]);

        return (ConnectionProviders.InDirectory(provider, directory), reconnecting, scopes, directory, null);
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
            if (ConnectionProviders.NotInDirectory(providerError, providerErrorDescription)
                && (await ProviderAsync(flow.Provider, ct))?.Kind == ConnectionProviders.Microsoft)
            {
                return (null, false, ConnectionProviders.MicrosoftNotInDirectory);
            }

            return (null, false, $"The provider did not grant access ({providerError}{(string.IsNullOrWhiteSpace(providerErrorDescription) ? "" : ": " + providerErrorDescription)}).");
        }

        if (string.IsNullOrWhiteSpace(code)) return (null, false, "The provider sent back no code. Start again.");

        if (await ProviderAsync(flow.Provider, ct) is not { Configured: true } found)
        {
            return (null, false, $"'{flow.Provider}' is no longer set up on this Host.");
        }

        var provider = ConnectionProviders.InDirectory(found, flow.Tenant);

        // The web callback carries no session: the person is the one the state was issued to.
        var who = actor ?? (flow.UserId == ConnectionActor.Operator.Id
            ? ConnectionActor.Operator
            : new ConnectionActor(flow.UserId, users is null ? null : (await users.FindByIdAsync(flow.UserId, ct))?.Email));

        var answer = await endpoints.ExchangeCodeAsync(provider, await ClientSecretAsync(provider, ct), code, flow.RedirectUri, flow.CodeVerifier, ct);
        if (!answer.Ok && provider.Kind == ConnectionProviders.Microsoft && ConnectionProviders.NotInDirectory(answer.Error, answer.ErrorDescription))
        {
            return (null, false, ConnectionProviders.MicrosoftNotInDirectory);
        }

        if (!answer.Ok) return (null, false, $"{provider.Name} refused the sign-in: {answer.Reason}.");

        return await FinishAsync(provider, answer, flow.UserId, flow.Scopes, flow.Name, flow.ReconnectId, flow.Tenant, who, now, ct);
    }

    /// <summary>
    /// THE ONE COMPLETION of every flow, once the provider has answered tokens: the account read
    /// from the ID token or userinfo, then a new connection, or a reconnect that must be the same
    /// account with the scopes merged. Tokens are stored as ciphertext, with the tenant row in the
    /// same transaction. <paramref name="directory"/> is the directory signed in through, stored on the
    /// connection and named in its tenant row (it is not a secret); a row of a connection that has
    /// none is as it always was.
    /// </summary>
    private async Task<(ConnectionRecord? Connection, bool Reconnected, string? Error)> FinishAsync(
        OAuthProvider provider, OAuthTokenAnswer answer, string userId, IReadOnlyList<string> asked, string? name,
        string? reconnectId, string? directory, ConnectionActor who, DateTimeOffset now, CancellationToken ct)
    {
        var account = await AccountAsync(provider, answer, ct);
        var granted = Granted(asked, answer);

        if (provider.Kind == ConnectionProviders.Microsoft && MicrosoftApis.Several(asked))
        {
            // ONE TOKEN ANSWERS ONE API: the sign-in's token names only the first API's scopes. Every
            // other API asked is redeemed from the refresh token at the run, which says so when the
            // account cannot get it.
            var answered = granted.Where(s => !ConnectionProviders.IdentityScopes.Contains(s)).Select(MicrosoftApis.ApiOf).ToHashSet(StringComparer.Ordinal);
            granted = Union(granted, asked.Where(s => !answered.Contains(MicrosoftApis.ApiOf(s))));
        }
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

                // ANOTHER ACCOUNT UNDER THE SAME NAME is the person's choice: the name is what teams and
                // members use, and each of them gets this account at its next run. The row says so.
                var otherAccount = !string.Equals(existing.Account, account, StringComparison.OrdinalIgnoreCase);

                var scopes = Union(existing.Scopes, granted);
                ForgetApis(existing.Id);
                var wasNeedingReconnect = existing.Status == ConnectionRecord.NeedsReconnect;
                await store.ReconnectAsync(existing.Id, scopes, tokens, who.Row(
                    TenantActions.ConnectionReconnected, existing.Id, existing.Name,
                    (otherAccount, directory is null && existing.Tenant is null) switch
                    {
                        (false, true) => new { connection = existing.Id, provider = provider.Id, account = existing.Account, scopes, wasNeedingReconnect },
                        (false, false) => (object)new { connection = existing.Id, provider = provider.Id, account = existing.Account, tenant = directory, scopes, wasNeedingReconnect },
                        (true, true) => new { connection = existing.Id, provider = provider.Id, account, fromAccount = existing.Account, scopes, wasNeedingReconnect },
                        (true, false) => new { connection = existing.Id, provider = provider.Id, account, fromAccount = existing.Account, tenant = directory, scopes, wasNeedingReconnect },
                    }), ct,
                    directory, otherAccount ? account : null);
                await HealthChangedAsync(existing.Id, ct);

                return (await store.GetAsync(existing.Id, ct), true, null);
            }
            finally
            {
                gate.Release();
            }
        }

        var record = new ConnectionRecord(
            NewId(), await UniqueNameAsync(name ?? account, ct), provider.Id, account, granted, now, null, ConnectionRecord.Ok, null, Tenant: directory);

        TriggerAudit Row(ConnectionRecord connected) => who.Row(
            TenantActions.ConnectionConnected, connected.Id, connected.Name,
            directory is null
                ? new { connection = connected.Id, provider = provider.Id, account, scopes = granted }
                : new { connection = connected.Id, provider = provider.Id, account, tenant = directory, scopes = granted });

        try
        {
            await store.InsertAsync(record, tokens, userId, Row(record), ct);
        }
        catch (ConnectionNameTakenException)
        {
            // TAKEN MEANWHILE, after the person signed in: the sign-in is kept under the next free name.
            record = record with { Name = await UniqueNameAsync(record.Name, ct) };
            await store.InsertAsync(record, tokens, userId, Row(record), ct);
        }

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

        var (provider, reconnecting, scopes, directory, error) = await PrepareAsync(request, ct);
        if (error is not null) return (null, error);

        if (provider!.DeviceAuthorizationUrl is null)
        {
            return (null, $"{provider.Name} has no sign-in with a code on this Host; sign in through the browser instead.");
        }

        var answer = await endpoints.DeviceAuthorizationAsync(provider, scopes!, ct);

        var codeScopes = scopes!;

        if (!answer.Ok && provider.Kind == ConnectionProviders.Microsoft && MicrosoftApis.Several(scopes!) && SeveralApisRefused(answer.Error, answer.ErrorDescription))
        {
            // A SIGN-IN THAT MAY NAME ONE API ONLY: it asks for the first API's scopes, and the run
            // redeems the refresh token for each other API.
            codeScopes = FirstApiScopes(scopes!);
            answer = await endpoints.DeviceAuthorizationAsync(provider, codeScopes, ct);
        }

        if (!answer.Ok && provider.Kind == ConnectionProviders.Microsoft && ConnectionProviders.NeedsAdminApproval(answer.Error, answer.ErrorDescription))
        {
            return (null, ConnectionProviders.MicrosoftAdminApproval);
        }

        if (!answer.Ok) return (null, $"{provider.Name} did not start the sign-in: {answer.Reason}.");

        var now = clock.GetUtcNow();
        Sweep(now);

        var flow = new DeviceSignIn(
            Random(24), actor, provider.Id, scopes!, codeScopes, string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(), reconnecting?.Id,
            answer.DeviceCode!, answer.UserCode!, answer.VerificationUri!, CodeExpiry(now, answer), directory);

        _deviceFlows[flow.Id] = flow;
        _ = Task.Run(() => PollAsync(flow, provider, answer.Interval is > 0 ? TimeSpan.FromSeconds(answer.Interval.Value) : DefaultInterval));

        var shown = flow.Read();
        return (new DeviceStart(flow.Id, shown.UserCode, shown.VerificationUri, shown.ExpiresAt), null);
    }

    /// <summary>A sign-in with a code as its starter reads it, or null - for a flow that does not
    /// exist and, alike, for one somebody else started.</summary>
    public DeviceFlowState? DeviceFlowOf(string flowId, ConnectionActor actor) =>
        _deviceFlows.TryGetValue(flowId, out var flow) && string.Equals(flow.Actor.Id, actor.Id, StringComparison.Ordinal)
            ? flow.Read()
            : null;

    /// <summary>The sign-ins with a code <paramref name="actor"/> started that are still waiting and
    /// not past their expiry, soonest to expire first. Nobody else's, ever.</summary>
    public IReadOnlyList<OpenDeviceFlow> OpenDeviceFlows(ConnectionActor actor)
    {
        var now = clock.GetUtcNow();

        return [.. _deviceFlows.Values
            .Where(f => string.Equals(f.Actor.Id, actor.Id, StringComparison.Ordinal) && f.ExpiresAt > now && f.Read().State == Waiting)
            .OrderBy(f => f.ExpiresAt)
            .Select(f => f.Open())
            .OfType<OpenDeviceFlow>()];
    }

    /// <summary>The first API's scopes and the sign-in scopes asked: what a Microsoft code that may
    /// name one API only asks for.</summary>
    private static IReadOnlyList<string> FirstApiScopes(IReadOnlyList<string> scopes)
    {
        var first = MicrosoftApis.Tokens(scopes)[0].Api;
        return [.. scopes.Where(s => ConnectionProviders.IdentityScopes.Contains(s) || MicrosoftApis.ApiOf(s) == first)];
    }

    private static DateTimeOffset CodeExpiry(DateTimeOffset now, DeviceAuthorizationAnswer answer) =>
        answer.ExpiresIn is > 0 ? now.AddSeconds(answer.ExpiresIn.Value) : now + FlowLifetime;

    /// <summary>What a waiting flow says once Microsoft has asked for a second code.</summary>
    public static string SecondCodeSentence(string provider, string verificationUri) =>
        $"{provider} needs a second code to finish the sign-in. Enter the new code at {verificationUri}; it asks no new questions.";

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
            var secret = await ClientSecretAsync(provider, ct);

            while (true)
            {
                await _delay(interval, ct);

                if (clock.GetUtcNow() >= flow.ExpiresAt)
                {
                    flow.Settle(Expired, ExpiredSentenceFor(provider));
                    return;
                }

                var answer = await endpoints.DeviceTokenAsync(provider, secret, flow.DeviceCode!, ct);

                if (answer.Ok)
                {
                    var (connection, reconnected, refusal) = await FinishAsync(
                        provider, answer, flow.Actor.Id, flow.Scopes, flow.Name, flow.ReconnectId, flow.Tenant, flow.Actor, clock.GetUtcNow(), ct);

                    if (refusal is not null) flow.Settle(Refused, refusal);
                    else flow.Settle(Done, $"{(reconnected ? "Reconnected" : "Connected")} {connection!.Named}.", connection.Id);
                    return;
                }

                if (provider.Kind == ConnectionProviders.Microsoft && ConnectionProviders.NeedsAdminApproval(answer.Error, answer.ErrorDescription))
                {
                    flow.Settle(Refused, ConnectionProviders.MicrosoftAdminApproval);
                    return;
                }

                if (provider.Kind == ConnectionProviders.Microsoft && ConnectionProviders.NotInDirectory(answer.Error, answer.ErrorDescription))
                {
                    flow.Settle(Refused, ConnectionProviders.MicrosoftNotInDirectory);
                    return;
                }

                if (provider.Kind == ConnectionProviders.Microsoft && !flow.Renewed && MicrosoftApis.Several(flow.CodeScopes)
                    && SeveralApisRefused(answer.Error, answer.ErrorDescription))
                {
                    // TWO CODES: Microsoft takes the consent to every API at the first code, then
                    // refuses to redeem that code for more than one API. A second code naming the
                    // first API's scopes only redeems; the consent is already recorded, so it asks
                    // nothing new, and the run redeems the refresh token for each other API. The
                    // flow keeps every scope asked. Never a third code.
                    var codeScopes = FirstApiScopes(flow.CodeScopes);
                    var second = await endpoints.DeviceAuthorizationAsync(provider, codeScopes, ct);

                    if (!second.Ok)
                    {
                        flow.Settle(Refused, ConnectionProviders.NeedsAdminApproval(second.Error, second.ErrorDescription)
                            ? ConnectionProviders.MicrosoftAdminApproval
                            : $"{provider.Name} did not start the second code: {second.Reason}. Nothing was stored; try again.");
                        return;
                    }

                    flow.Renew(codeScopes, second.DeviceCode!, second.UserCode!, second.VerificationUri!, CodeExpiry(clock.GetUtcNow(), second),
                        SecondCodeSentence(provider.Name, second.VerificationUri!));
                    if (second.Interval is > 0) interval = TimeSpan.FromSeconds(second.Interval.Value);
                    continue;
                }

                switch (answer.Error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += SlowDown;
                        continue;
                    case "expired_token" or "code_expired":
                        flow.Settle(Expired, ExpiredSentenceFor(provider));
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
            flow.Settle(Refused, exception is System.Data.Common.DbException fault
                ? $"The connection could not be stored ({DatabaseFault.Code(fault)}). Try again."
                : $"The sign-in could not be finished ({exception.GetType().Name}). Try again.");
        }
    }

    public const string ExpiredSentence = "The code expired before the sign-in was finished. Nothing was stored; try again.";

    /// <summary>What an expired code says: for Microsoft, a second line after the sentence, since its own
    /// page turns away an account that is not in the directory without the Host ever hearing of it.</summary>
    public static string ExpiredSentenceFor(OAuthProvider provider) =>
        provider.Kind == ConnectionProviders.Microsoft ? $"{ExpiredSentence}\n{ConnectionProviders.MicrosoftExpiredNotInDirectory}" : ExpiredSentence;

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

    /// <summary>One sign-in with a code, in memory. The device code goes when the flow settles, or
    /// when a second code replaces it. <see cref="Scopes"/> are every scope asked, which the
    /// connection records; <see cref="CodeScopes"/> are what the current code named; <see cref="Tenant"/>
    /// is the directory it signs in through, or null for the provider's.</summary>
    private sealed class DeviceSignIn(
        string id, ConnectionActor actor, string provider, IReadOnlyList<string> scopes, IReadOnlyList<string> codeScopes,
        string? name, string? reconnectId, string deviceCode, string userCode, string verificationUri, DateTimeOffset expiresAt,
        string? tenant)
    {
        private readonly Lock _lock = new();

        private DeviceFlowState _state = new(
            Waiting, $"Waiting for you to enter the code at {verificationUri}.", null, userCode, verificationUri, expiresAt);

        private IReadOnlyList<string> _codeScopes = codeScopes;

        private string? _deviceCode = deviceCode;

        private bool _renewed;

        public string Id => id;

        public ConnectionActor Actor => actor;

        public string Provider => provider;

        public IReadOnlyList<string> Scopes => scopes;

        public string? Name => name;

        public string? ReconnectId => reconnectId;

        public string? Tenant => tenant;

        public DateTimeOffset ExpiresAt
        {
            get { lock (_lock) return _state.ExpiresAt; }
        }

        public IReadOnlyList<string> CodeScopes
        {
            get { lock (_lock) return _codeScopes; }
        }

        /// <summary>Whether a second code has replaced the first; there is never a third.</summary>
        public bool Renewed
        {
            get { lock (_lock) return _renewed; }
        }

        public string? DeviceCode
        {
            get { lock (_lock) return _deviceCode; }
        }

        /// <summary>The second code: the same flow, still waiting, on a new device code.</summary>
        public void Renew(
            IReadOnlyList<string> codeScopes, string deviceCode, string userCode, string verificationUri, DateTimeOffset expiresAt, string sentence)
        {
            lock (_lock)
            {
                _codeScopes = codeScopes;
                _deviceCode = deviceCode;
                _renewed = true;
                _state = new DeviceFlowState(Waiting, sentence, null, userCode, verificationUri, expiresAt);
            }
        }

        public void Settle(string state, string sentence, string? connectionId = null)
        {
            lock (_lock)
            {
                _state = _state with { State = state, Sentence = sentence, ConnectionId = connectionId };
                _deviceCode = null;
            }
        }

        /// <summary>The flow as the open list shows it, while it waits.</summary>
        public OpenDeviceFlow? Open()
        {
            lock (_lock)
            {
                return _state.State == Waiting
                    ? new OpenDeviceFlow(id, provider, _state.UserCode, _state.VerificationUri, _state.ExpiresAt, Waiting)
                    : null;
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
    /// reads the token the first stored. <paramref name="scopes"/> are what the slot asks of the
    /// connection: a Microsoft connection whose scopes span several APIs is handed one token per API
    /// those scopes name (all of its own when null).
    /// </summary>
    public async Task<(ConnectionGrant? Grant, string? Refusal)> GrantAsync(
        string connectionId, string slot, CancellationToken ct = default, IReadOnlyList<string>? scopes = null)
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

            if (connection.Mailbox is { } mailbox) return await MailboxGrantAsync(connection, mailbox, ct);

            if (connection.Credential is { } credential) return await CredentialGrantAsync(connection, credential, ct);

            var microsoft = connection.Provider == ConnectionProviders.Microsoft;
            if (microsoft && MicrosoftApis.Several(connection.Scopes)) return await ApisGrantAsync(connection, scopes ?? connection.Scopes, ct);

            var tokens = await store.TokensAsync(connectionId, ct) ?? new ConnectionTokens(null, null, null);
            var now = clock.GetUtcNow();

            if (tokens.AccessToken is { } cached && (tokens.AccessExpiresAt is null || tokens.AccessExpiresAt > now + RefreshEarly))
            {
                return (OneApiGrant(connection, cached, tokens.AccessExpiresAt, microsoft), null);
            }

            if (tokens.RefreshToken is null)
            {
                return (null, await MarkAsync(connection, "the provider issued no refresh token, so the access token cannot be renewed", ct));
            }

            if (await ProviderAsync(connection.Provider, ct) is not { Configured: true } found)
            {
                return (null, $"Connection {connection.Named} could not be refreshed: its provider '{connection.Provider}' has no OAuth client on this Host. Set it in {Where}.");
            }

            var provider = ConnectionProviders.InDirectory(found, connection.Tenant);
            var answer = await endpoints.RefreshAsync(provider, await ClientSecretAsync(provider, ct), tokens.RefreshToken, ct);

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
            catch (System.Data.Common.DbException exception)
            {
                throw new RefreshNotStoredException(exception);
            }

            return (OneApiGrant(connection, answer.AccessToken!, refreshed.AccessExpiresAt, microsoft), null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A connection with one API: its stored token, and for Microsoft that token again as
    /// the one entry of <see cref="ConnectionGrant.Tokens"/>.</summary>
    private static ConnectionGrant OneApiGrant(ConnectionRecord connection, string accessToken, DateTimeOffset? expiresAt, bool microsoft) =>
        new(connection.Id, connection.Provider, connection.Account, accessToken, expiresAt, connection.Scopes,
            Tokens: microsoft ? [new ApiToken(MicrosoftApis.Tokens(connection.Scopes)[0].Api, accessToken, expiresAt, connection.Scopes)] : null);

    /// <summary>
    /// A MICROSOFT CONNECTION WITH SEVERAL APIS: one access token per API <paramref name="asked"/>
    /// names, each redeemed from the one stored refresh token with that API's scopes and kept in
    /// memory until 5 minutes before it expires, so each is refreshed on its own. A rotated refresh
    /// token is stored after each redemption, before the next and before any token is handed out.
    /// Called under the connection's gate.
    ///
    /// A refusal that is the API's (no consent to it, a scope it does not have, sign-in rules for it),
    /// or any refusal once another API was redeemed with the same refresh token, blocks the run naming
    /// the API and leaves the connection as it is; any other refusal is the refresh token's, and marks
    /// the connection <c>needs-reconnect</c> as a single API's does.
    /// </summary>
    private async Task<(ConnectionGrant? Grant, string? Refusal)> ApisGrantAsync(
        ConnectionRecord connection, IReadOnlyList<string> asked, CancellationToken ct)
    {
        var granted = MicrosoftApis.Group(connection.Scopes).ToDictionary(g => g.Api, g => g.Scopes, StringComparer.Ordinal);
        var handed = new List<ApiToken>();
        var redeemed = false;
        OAuthProvider? provider = null;
        string? secret = null;

        foreach (var api in MicrosoftApis.Tokens(asked))
        {
            var now = clock.GetUtcNow();

            if (_apiTokens.TryGetValue((connection.Id, api.Api), out var held) && (held.ExpiresAt is null || held.ExpiresAt > now + RefreshEarly))
            {
                handed.Add(held);
                continue;
            }

            var tokens = await store.TokensAsync(connection.Id, ct);
            if (tokens?.RefreshToken is null)
            {
                return (null, await MarkAsync(connection, "the provider issued no refresh token, so the access token cannot be renewed", ct));
            }

            if (provider is null)
            {
                if (await ProviderAsync(connection.Provider, ct) is not { Configured: true } found)
                {
                    return (null, $"Connection {connection.Named} could not be refreshed: its provider '{connection.Provider}' has no OAuth client on this Host. Set it in {Where}.");
                }

                provider = ConnectionProviders.InDirectory(found, connection.Tenant);
                secret = await ClientSecretAsync(provider, ct);
            }

            IReadOnlyList<string> apiScopes = [.. (granted.GetValueOrDefault(api.Api) ?? api.Scopes).Where(s => !ConnectionProviders.IdentityScopes.Contains(s))];
            var answer = await endpoints.RefreshAsync(provider, secret, tokens.RefreshToken, ct, [.. apiScopes, "offline_access"]);

            if (!answer.Ok)
            {
                if (answer.Refused && (redeemed || IsApisOwnRefusal(answer))) return (null, ApiRefused(connection, api.Api, answer.Reason));
                if (answer.Refused) return (null, await MarkAsync(connection, answer.Reason, ct));

                // TRANSIENT: the provider was not reached. The connection is not the problem.
                return (null, $"Connection {connection.Named} could not get a token for {MicrosoftApis.Named(api.Api)} just now ({answer.Reason}). The next run tries again.");
            }

            // STORED FIRST: a rotated refresh token is on disk before this API is redeemed again, the
            // next API is redeemed, or any token leaves.
            try
            {
                await store.StoreRotatedRefreshAsync(connection.Id, answer.RefreshToken, ct);
            }
            catch (System.Data.Common.DbException exception)
            {
                throw new RefreshNotStoredException(exception);
            }

            var token = new ApiToken(api.Api, answer.AccessToken!, Expiry(now, answer.ExpiresIn), apiScopes);
            _apiTokens[(connection.Id, api.Api)] = token;
            handed.Add(token);
            redeemed = true;
        }

        var first = handed[0];
        return (new ConnectionGrant(connection.Id, connection.Provider, connection.Account, first.AccessToken, first.ExpiresAt, connection.Scopes, Tokens: handed), null);
    }

    /// <summary>Microsoft's answers that refuse one API and not the refresh token: no consent to it
    /// (AADSTS65001, AADSTS65004), a scope or resource it does not know (AADSTS70011, AADSTS500011),
    /// or a sign-in rule for it (multi-factor, Conditional Access).</summary>
    private static bool IsApisOwnRefusal(OAuthTokenAnswer answer) =>
        answer.Error is "invalid_scope" or "consent_required" or "interaction_required"
        || answer.Suberror is "consent_required"
        || ApiRefusalCodes.Any(code => answer.ErrorDescription?.Contains(code, StringComparison.Ordinal) == true);

    private static readonly string[] ApiRefusalCodes = ["AADSTS65001", "AADSTS65004", "AADSTS70011", "AADSTS500011", "AADSTS50076", "AADSTS50079", "AADSTS53003"];

    /// <summary>A device authorization, or a poll of its device code, Microsoft refused for naming
    /// more than one API.</summary>
    private static bool SeveralApisRefused(string? error, string? description) =>
        error is "invalid_scope"
        || new[] { "AADSTS28000", "AADSTS28003", "AADSTS70011" }.Any(code => description?.Contains(code, StringComparison.Ordinal) == true);

    public static string ApiRefused(ConnectionRecord connection, string api, string reason) =>
        $"Connection {connection.Named} cannot get a token for {MicrosoftApis.Named(api)}: Microsoft refused it ({reason}). "
        + $"Reconnect it from {Where} and consent to {MicrosoftApis.NameOf(api)}, or have an administrator consent to it for the app.";

    /// <summary>Drops a connection's held API tokens: it was reconnected, marked or disconnected.</summary>
    private void ForgetApis(string connectionId)
    {
        foreach (var key in _apiTokens.Keys.Where(k => k.Connection == connectionId)) _apiTokens.TryRemove(key, out _);
    }

    /// <summary>One gate per connection: its refreshes and its reconnects, one at a time.</summary>
    private SemaphoreSlim Gate(string connectionId) => _refreshing.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));

    /// <summary>A disconnected connection's gate is dropped, so the gates do not grow without end.</summary>
    public void Forget(string connectionId)
    {
        _refreshing.TryRemove(connectionId, out _);
        ForgetApis(connectionId);
    }

    private async Task<string> MarkAsync(ConnectionRecord connection, string reason, CancellationToken ct)
    {
        ForgetApis(connection.Id);
        await store.MarkNeedsReconnectAsync(connection.Id, reason, new TriggerAudit(
            null, null, TenantActions.ConnectionNeedsReconnect, connection.Id, connection.Name,
            JsonSerializer.Serialize(new { connection = connection.Id, provider = connection.Provider, account = connection.Account, reason })), ct);
        await HealthChangedAsync(connection.Id, ct);

        return NeedsReconnect(connection, reason);
    }

    /// <summary>
    /// WHEN A CONNECTION'S HEALTH MAY HAVE MOVED - marked <c>needs-reconnect</c> by a run or a test, or
    /// cleared by a Reconnect, an edit, a new password or a passing test - this is told its id, after
    /// the write. The Host sets it to <see cref="PluginSetup.ConnectionChangedAsync"/>: every member
    /// using the connection reads Needs you, or stops reading it and its Manager is told. Best effort:
    /// the write stands whatever this does.
    /// </summary>
    public Func<string, CancellationToken, Task>? HealthChanged { get; set; }

    private async Task HealthChangedAsync(string id, CancellationToken ct)
    {
        if (HealthChanged is not { } changed) return;

        try
        {
            await changed(id, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The connection's own write is done; the marks are read again at the next change or restart.
        }
    }

    /// <summary>
    /// A PLUGIN'S RUN REPORTED THE CONNECTION IT WAS HANDED <c>refused</c> or <c>sign-in</c>
    /// (<see cref="RunConnectionFailures"/>): the connection is marked <c>needs-reconnect</c> with
    /// <paramref name="sentence"/>, with a <c>connections.needs-reconnect</c> row naming the member, the
    /// slot and the kind, and every member using it reads Needs you. Never a value: the sentence was
    /// redacted by the run. Under the connection's gate, as a refresh's own mark is.
    /// </summary>
    public async Task MarkFromRunAsync(
        string connectionId, string sentence, string kind, ContainerId member, string slot, CancellationToken ct = default)
    {
        var gate = Gate(connectionId);
        await gate.WaitAsync(ct);

        try
        {
            if (await store.GetAsync(connectionId, ct) is not { } connection) return;

            ForgetApis(connection.Id);
            await store.MarkNeedsReconnectAsync(connection.Id, sentence, new TriggerAudit(
                null, null, TenantActions.ConnectionNeedsReconnect, connection.Id, connection.Name,
                JsonSerializer.Serialize(new
                {
                    connection = connection.Id, provider = connection.Provider, account = connection.Account, reason = sentence,
                    team = member.Team, member = member.Name, slot, kind,
                })), ct);
            await HealthChangedAsync(connection.Id, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// AN MCP SERVER'S CHECK WAS REFUSED the credential this connection holds (MCP 1): the connection is
    /// marked <c>needs-reconnect</c> with <paramref name="sentence"/>, with a <c>connections.needs-reconnect</c>
    /// row naming the server, and every member using it reads Needs you, as a run's refusal marks it. Never
    /// a value: the sentence was redacted by the check.
    /// </summary>
    public async Task MarkFromMcpServerAsync(string connectionId, string sentence, string server, CancellationToken ct = default)
    {
        var gate = Gate(connectionId);
        await gate.WaitAsync(ct);

        try
        {
            if (await store.GetAsync(connectionId, ct) is not { } connection) return;

            ForgetApis(connection.Id);
            await store.MarkNeedsReconnectAsync(connection.Id, sentence, new TriggerAudit(
                null, null, TenantActions.ConnectionNeedsReconnect, connection.Id, connection.Name,
                JsonSerializer.Serialize(new
                {
                    connection = connection.Id, provider = connection.Provider, account = connection.Account, reason = sentence,
                    mcpServer = server, kind = "refused",
                })), ct);
            await HealthChangedAsync(connection.Id, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public static string NeedsReconnect(ConnectionRecord connection, string? reason) => connection.Mailbox is not null
        ? $"Connection {connection.Named} needs a new app password: {reason ?? "the server refused the password."} Update its password in {Where}."
        : connection.Credential is not null
        ? $"Connection {connection.Named} needs you: {reason ?? "its test was refused."} Edit it in {Where}, then test it again."
        : $"Connection {connection.Named} needs to be reconnected: the provider refused to refresh it ({reason ?? "no reason given"}). "
            + $"Reconnect it from {Where}.";

    // ---- mailboxes (kind imap) --------------------------------------------------------------------

    /// <summary>
    /// A mailbox for a run: its app password and servers. Its last good login is trusted for
    /// <see cref="MailboxRecheck"/>; after that the Host logs in again first, the way an access token
    /// is refreshed. A refused login marks it <c>needs-reconnect</c> with the login's sentence; a
    /// server not reached blocks only this run.
    /// </summary>
    private async Task<(ConnectionGrant? Grant, string? Refusal)> MailboxGrantAsync(
        ConnectionRecord connection, MailboxSettings mailbox, CancellationToken ct)
    {
        if (await store.MailboxPasswordAsync(connection.Id, ct) is not { } password)
        {
            return (null, $"The app password of connection {connection.Named} could not be read on this Host. Update its password in {Where}.");
        }

        var now = clock.GetUtcNow();

        if (connection.RefreshedAt is not { } verified || now - verified >= MailboxRecheck)
        {
            if (mail is null) return (null, $"This Host cannot log in to mailboxes, so connection {connection.Named} could not be checked.");

            var login = await mail.LoginAsync(connection.Account, mailbox, password, ct);

            if (login.Outcome == MailLoginOutcome.Refused) return (null, await MarkAsync(connection, login.Sentence, ct));

            if (!login.Ok)
            {
                // TRANSIENT: the server was not reached. The password is not the problem.
                return (null, $"Connection {connection.Named} could not be checked just now: {login.Sentence} The next run tries again.");
            }

            await store.StoreMailboxVerifiedAsync(connection.Id, now, ct);
        }

        return (new ConnectionGrant(
            connection.Id, connection.Provider, connection.Account, "", null, [],
            new MailboxGrant(mailbox.Username, password, mailbox.Imap, mailbox.Smtp)), null);
    }

    /// <summary>
    /// Adds a mailbox: the fields checked, then an IMAP login and an SMTP authentication with them.
    /// Saved - with its tenant row, in one transaction - only when both succeed; a refused or failed
    /// login saves nothing and answers its sentence (422).
    /// </summary>
    public async Task<MailboxAnswer> AddMailboxAsync(ConnectionActor actor, AddMailbox request, CancellationToken ct = default)
    {
        if (MailboxRefusal(request) is { } refusal) return new MailboxAnswer(StatusCodes.Status400BadRequest, null, refusal, null);
        if (!string.IsNullOrWhiteSpace(request.Name) && await store.NameTakenAsync(request.Name.Trim(), null, ct))
        {
            return new MailboxAnswer(StatusCodes.Status409Conflict, null, NameTaken(request.Name.Trim()), null);
        }

        if (mail is null) return new MailboxAnswer(StatusCodes.Status400BadRequest, null, "This Host cannot log in to mailboxes.", null);

        var account = request.Account!.Trim();

        var settings = new MailboxSettings(
            string.IsNullOrWhiteSpace(request.Username) ? account : request.Username.Trim(),
            Server(request.Imap!), Server(request.Smtp!),
            string.IsNullOrWhiteSpace(request.Preset) ? null : request.Preset.Trim(), true);

        var login = await mail.LoginAsync(account, settings, request.Password!, ct);
        if (!login.Ok) return new MailboxAnswer(StatusCodes.Status422UnprocessableEntity, null, login.Sentence, null);

        var now = clock.GetUtcNow();
        var record = new ConnectionRecord(
            NewId(), string.IsNullOrWhiteSpace(request.Name) ? await UniqueNameAsync(account, ct) : request.Name.Trim(), MailboxSettings.Kind, account, [], now, now,
            ConnectionRecord.Ok, null);

        await store.InsertMailboxAsync(record, settings, request.Password!, actor.Id, actor.Row(
            TenantActions.ConnectionConnected, record.Id, record.Name,
            new
            {
                connection = record.Id, provider = MailboxSettings.Kind, account, username = settings.Username, preset = settings.Preset,
                imap = $"{settings.Imap.Host}:{settings.Imap.Port} {settings.Imap.Security}",
                smtp = $"{settings.Smtp.Host}:{settings.Smtp.Port} {settings.Smtp.Security}",
            }), ct);

        return new MailboxAnswer(StatusCodes.Status200OK, login.Sentence, null, await store.GetAsync(record.Id, ct));
    }

    /// <summary>
    /// Update password: a login with the mailbox's servers and the new password, and only when it
    /// succeeds the new password stored, <c>needs-reconnect</c> cleared and the tenant row written, in
    /// one transaction. A refused login changes nothing and answers its sentence (422).
    /// </summary>
    public async Task<MailboxAnswer> UpdateMailboxPasswordAsync(
        ConnectionActor actor, string id, string? password, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) is not { } connection)
        {
            return new MailboxAnswer(StatusCodes.Status404NotFound, null, $"There is no connection '{id}'.", null);
        }

        if (connection.Mailbox is not { } mailbox)
        {
            return new MailboxAnswer(StatusCodes.Status400BadRequest, null,
                $"Connection {connection.Named} signs in with {ConnectionProviders.Display(connection.Provider)}, not a password. Reconnect it instead.", null);
        }

        if (PasswordRefusal(password) is { } refusal) return new MailboxAnswer(StatusCodes.Status400BadRequest, null, refusal, null);
        if (mail is null) return new MailboxAnswer(StatusCodes.Status400BadRequest, null, "This Host cannot log in to mailboxes.", null);

        var gate = Gate(id);
        await gate.WaitAsync(ct);

        try
        {
            var login = await mail.LoginAsync(connection.Account, mailbox, password!, ct);
            if (!login.Ok) return new MailboxAnswer(StatusCodes.Status422UnprocessableEntity, null, login.Sentence, null);

            var updated = await store.UpdateMailboxPasswordAsync(id, password!, clock.GetUtcNow(), actor.Row(
                TenantActions.ConnectionPasswordUpdated, id, connection.Name,
                new { connection = id, provider = MailboxSettings.Kind, account = connection.Account, wasNeedingReconnect = connection.Status == ConnectionRecord.NeedsReconnect }), ct);
            if (updated) await HealthChangedAsync(id, ct);

            return updated
                ? new MailboxAnswer(StatusCodes.Status200OK, login.Sentence, null, await store.GetAsync(id, ct))
                : new MailboxAnswer(StatusCodes.Status404NotFound, null, $"There is no connection '{id}'.", null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// A mailbox's edit: what is given replaces what is stored (the account, username, either server,
    /// the password), then a login with the result. Only when it succeeds is it stored, with
    /// <c>needs-reconnect</c> cleared and a <c>connections.credentials-changed</c> row naming what
    /// changed - never a value - in one transaction. A refused login changes nothing (422).
    /// </summary>
    public async Task<MailboxAnswer> EditMailboxAsync(ConnectionActor actor, string id, EditMailbox request, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) is not { } connection)
        {
            return new MailboxAnswer(StatusCodes.Status404NotFound, null, $"There is no connection '{id}'.", null);
        }

        if (connection.Mailbox is not { } mailbox)
        {
            return new MailboxAnswer(StatusCodes.Status400BadRequest, null, $"Connection {connection.Named} is not a mailbox.", null);
        }

        var stored = await store.MailboxPasswordAsync(id, ct);
        var merged = new AddMailbox(
            mailbox.Preset, connection.Name, request.Account ?? connection.Account, request.Username ?? mailbox.Username,
            request.Password ?? stored,
            request.Imap ?? new MailServerRequest(mailbox.Imap.Host, mailbox.Imap.Port, mailbox.Imap.Security),
            request.Smtp ?? new MailServerRequest(mailbox.Smtp.Host, mailbox.Smtp.Port, mailbox.Smtp.Security));

        if (merged.Password is null) return new MailboxAnswer(StatusCodes.Status400BadRequest, null, PasswordRefusal(null), null);
        if (MailboxRefusal(merged) is { } refusal) return new MailboxAnswer(StatusCodes.Status400BadRequest, null, refusal, null);
        if (mail is null) return new MailboxAnswer(StatusCodes.Status400BadRequest, null, "This Host cannot log in to mailboxes.", null);

        var account = merged.Account!.Trim();
        var settings = new MailboxSettings(
            string.IsNullOrWhiteSpace(merged.Username) ? account : merged.Username.Trim(), Server(merged.Imap!), Server(merged.Smtp!), mailbox.Preset, true);

        var changed = new List<string>();
        if (!string.Equals(account, connection.Account, StringComparison.Ordinal)) changed.Add("account");
        if (settings.Username != mailbox.Username) changed.Add("username");
        if (settings.Imap != mailbox.Imap) changed.Add("imap");
        if (settings.Smtp != mailbox.Smtp) changed.Add("smtp");
        if (request.Password is not null && request.Password != stored) changed.Add("password");

        var gate = Gate(id);
        await gate.WaitAsync(ct);

        try
        {
            var login = await mail.LoginAsync(account, settings, merged.Password, ct);
            if (!login.Ok) return new MailboxAnswer(StatusCodes.Status422UnprocessableEntity, null, login.Sentence, null);

            var updated = await store.UpdateMailboxAsync(id, account, settings, request.Password, clock.GetUtcNow(), actor.Row(
                TenantActions.ConnectionCredentialsChanged, id, connection.Name,
                new
                {
                    connection = id, kind = MailboxSettings.Kind, account, changed,
                    wasNeedingReconnect = connection.Status == ConnectionRecord.NeedsReconnect,
                }), ct);
            if (updated) await HealthChangedAsync(id, ct);

            return updated
                ? new MailboxAnswer(StatusCodes.Status200OK, login.Sentence, null, await store.GetAsync(id, ct))
                : new MailboxAnswer(StatusCodes.Status404NotFound, null, $"There is no connection '{id}'.", null);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- credentials (website, API key, username and password) ------------------------------------

    /// <summary>
    /// A new website, API key or username-and-password connection: the kind's form checked, then the
    /// connection, its unmasked fields and its masked values as ciphertext saved with a
    /// <c>connections.connected</c> row naming the fields - never a value - in one transaction. Nothing
    /// is signed in to: the values are checked by the service they are for, at a run.
    /// </summary>
    public async Task<MailboxAnswer> AddCredentialAsync(ConnectionActor actor, AddCredential request, CancellationToken ct = default)
    {
        var kind = request.Kind?.Trim() ?? "";
        if (!ConnectionKinds.IsCredential(kind))
        {
            return Refusal400($"`kind` is {string.Join(", ", ConnectionKinds.Credentials)}; a sign-in and a mailbox have their own routes.");
        }

        var presetId = string.IsNullOrWhiteSpace(request.Preset) ? null : request.Preset.Trim();
        if (CredentialPresets.For(kind, presetId) is not { } preset)
        {
            var ids = CredentialPresets.IdsOf(kind);
            return Refusal400(ids.Count == 0
                ? $"A {ConnectionKinds.Display(kind)} connection takes no `preset`; leave it out."
                : $"`preset` is one of {string.Join(", ", ids)} for an {ConnectionKinds.Display(kind)} connection.");
        }

        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaximumNameLength) return Refusal400($"`name` is required: 1 to {MaximumNameLength} characters.");
        if (await store.NameTakenAsync(name, null, ct)) return new MailboxAnswer(StatusCodes.Status409Conflict, null, NameTaken(name), null);

        var (settings, secrets, _, error) = CredentialForm.Read(preset, request.Fields);
        if (error is not null) return Refusal400(error);

        var record = new ConnectionRecord(
            NewId(), name, kind, CredentialForm.AccountOf(preset, settings!), [], clock.GetUtcNow(), null, ConnectionRecord.Ok, null,
            Credential: settings);

        try
        {
            await store.InsertCredentialAsync(record, secrets!, actor.Id, actor.Row(
                TenantActions.ConnectionConnected, record.Id, record.Name,
                new { connection = record.Id, provider = kind, preset = preset.Id, account = record.Account, fields = settings!.Fields.Select(f => f.Name) }), ct);
        }
        catch (ConnectionNameTakenException)
        {
            return new MailboxAnswer(StatusCodes.Status409Conflict, null, NameTaken(name), null);
        }

        return new MailboxAnswer(StatusCodes.Status200OK, null, null, await store.GetAsync(record.Id, ct));
    }

    /// <summary>
    /// A credential connection's edit: each field given replaces its value (a masked one left out, or
    /// null, keeps its own), under the same name and id, so every team and member using it gets the new
    /// value at its next run. A <c>connections.credentials-changed</c> row names the fields that changed,
    /// never a value, in the same transaction.
    /// </summary>
    public async Task<MailboxAnswer> EditCredentialAsync(ConnectionActor actor, string id, EditCredential request, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) is not { } connection)
        {
            return new MailboxAnswer(StatusCodes.Status404NotFound, null, $"There is no connection '{id}'.", null);
        }

        if (connection.Credential is not { } before || CredentialPresets.For(connection.Kind, before.Preset) is not { } preset)
        {
            return Refusal400(connection.Mailbox is not null
                ? $"Connection {connection.Named} is a mailbox; edit it as one."
                : $"Connection {connection.Named} signs in with {ConnectionProviders.Display(connection.Provider)}; reconnect it instead.");
        }

        var gate = Gate(id);
        await gate.WaitAsync(ct);

        try
        {
            var stored = await store.CredentialSecretsAsync(id, ct) ?? new Dictionary<string, string>();

            // AN EDIT OF A FIXED FORM names only what changes: every field it leaves out is kept.
            var given = preset.NamedFields
                ? request.Fields
                : [.. preset.Fields.Select(spec => request.Fields?.FirstOrDefault(f => f?.Name?.Trim() == spec.Name) ?? new CredentialFieldValue(spec.Name, null)),
                   .. (request.Fields ?? []).Where(f => f is null || !preset.Fields.Any(spec => spec.Name == f.Name?.Trim()))];

            var (settings, secrets, changed, error) = CredentialForm.Read(preset, given, before, stored);
            if (error is not null) return Refusal400(error);

            var account = CredentialForm.AccountOf(preset, settings!);
            var updated = await store.UpdateCredentialAsync(id, account, settings!, secrets!, actor.Row(
                TenantActions.ConnectionCredentialsChanged, id, connection.Name,
                new { connection = id, kind = connection.Kind, preset = preset.Id, account, changed }), ct);
            if (updated) await HealthChangedAsync(id, ct);

            return updated
                ? new MailboxAnswer(StatusCodes.Status200OK, null, null, await store.GetAsync(id, ct))
                : new MailboxAnswer(StatusCodes.Status404NotFound, null, $"There is no connection '{id}'.", null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A credential connection for a run: every field's value, the masked ones opened.</summary>
    private async Task<(ConnectionGrant? Grant, string? Refusal)> CredentialGrantAsync(
        ConnectionRecord connection, CredentialSettings credential, CancellationToken ct)
    {
        var secrets = await store.CredentialSecretsAsync(connection.Id, ct) ?? new Dictionary<string, string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in credential.Fields.Where(f => f.Set))
        {
            if (!field.Masked) values[field.Name] = field.Value!;
            else if (secrets.TryGetValue(field.Name, out var value)) values[field.Name] = value;
            else return (null, $"The value of `{field.Name}` in connection {connection.Named} could not be read on this Host. Edit the connection in {Where}.");
        }

        return (new ConnectionGrant(
            connection.Id, connection.Provider, connection.Account, "", null, [],
            Credential: new CredentialGrant(connection.Kind, credential.Preset, values,
                credential.Fields.Where(f => f.Masked).Select(f => f.Name).ToHashSet(StringComparer.Ordinal))), null);
    }

    private static MailboxAnswer Refusal400(string error) => new(StatusCodes.Status400BadRequest, null, error, null);

    // ---- Test connection ----------------------------------------------------------------------------

    /// <summary>How long a test may take on its worker: a mailbox's IMAP login and SMTP authentication each have 20 s.</summary>
    public static readonly TimeSpan TestBound = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether a connection has a test: every sign-in, every mailbox and every website (its signed-in
    /// check); an API key or username and password when its preset declares one and the connection has
    /// what it names.
    /// </summary>
    public static bool Testable(ConnectionRecord connection) =>
        connection.Mailbox is not null
        || (connection.Credential is { } credential ? CredentialPresets.Testable(connection.Kind, credential) : connection.Kind == ConnectionKinds.OAuth);

    /// <summary>
    /// TEST CONNECTION: what the connection holds sent to one worker as a sealed probe - a mailbox's
    /// login, a sign-in's refresh and one read of the profile, a preset's one harmless request - and its
    /// answer recorded as the connection's health with a <c>connections.tested</c> row, in one
    /// transaction: ok, or <c>needs-reconnect</c> with the answer's sentence. A sign-in's rotated refresh
    /// token is stored first. A test no worker ran records nothing. Under the connection's gate, so a
    /// run's refresh and a test never race.
    /// </summary>
    public async Task<ConnectionTestAnswer> TestAsync(ConnectionActor actor, string id, WorkerAsks asks, CancellationToken ct = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);

        try
        {
            if (await store.GetAsync(id, ct) is not { } connection)
            {
                return new ConnectionTestAnswer(StatusCodes.Status404NotFound, null, null, $"There is no connection '{id}'.", null);
            }

            if (!Testable(connection))
            {
                return new ConnectionTestAnswer(StatusCodes.Status400BadRequest, null, null, NoTest(connection), null);
            }

            var (probe, secrets, needsYou, unrecorded) = await ProbeAsync(connection, ct);

            string outcome, sentence;
            if (probe is not null)
            {
                var asked = await asks.AskAsync<ConnectionTested>(
                    new TestConnection(WorkerAsks.NewRequest(), probe.Seal()), probe is WebsiteProbe ? WebsiteBound : TestBound, ct);
                if (asked.Answer is not { } tested)
                {
                    return new ConnectionTestAnswer(StatusCodes.Status503ServiceUnavailable, null, null,
                        $"The test did not run: {asked.Why} Nothing was recorded; try again.", null);
                }

                // STORED FIRST: a rotated refresh token is the one that works now, whatever the test said.
                if (tested.RefreshToken is { } rotated)
                {
                    try
                    {
                        await store.StoreRotatedRefreshAsync(id, rotated.Value, ct);
                    }
                    catch (System.Data.Common.DbException exception)
                    {
                        throw new RefreshNotStoredException(exception);
                    }
                }

                (outcome, sentence) = (tested.Outcome, ConnectionTester.Redact(tested.Sentence, secrets));

                // A SITE THAT DID NOT ANSWER says nothing about what the website holds: the test is
                // answered and recorded, and the connection's health stays as it was (CON 4).
                if (probe is WebsiteProbe && outcome == ConnectionTested.Failed)
                {
                    await store.RecordAsync(actor.Row(
                        TenantActions.ConnectionTested, id, connection.Name,
                        new { connection = id, kind = connection.Kind, provider = connection.Provider, outcome, sentence }), ct);
                    return new ConnectionTestAnswer(StatusCodes.Status200OK, connection.Status, sentence, null, connection);
                }
            }
            else if (needsYou is not null)
            {
                (outcome, sentence) = (ConnectionTested.Refused, needsYou);
            }
            else
            {
                return new ConnectionTestAnswer(StatusCodes.Status422UnprocessableEntity, null, null, unrecorded, null);
            }

            var ok = outcome == ConnectionTested.Ok;
            if (!ok) ForgetApis(id);

            await store.RecordTestAsync(id, ok ? null : sentence, clock.GetUtcNow(), actor.Row(
                TenantActions.ConnectionTested, id, connection.Name,
                new { connection = id, kind = connection.Kind, provider = connection.Provider, outcome, sentence }), ct);
            await HealthChangedAsync(id, ct);

            return new ConnectionTestAnswer(StatusCodes.Status200OK, ok ? ConnectionRecord.Ok : ConnectionRecord.NeedsReconnect, sentence, null, await store.GetAsync(id, ct));
        }
        finally
        {
            gate.Release();
        }
    }

    public static string NoTest(ConnectionRecord connection) => connection.Kind switch
    {
        ConnectionKinds.Login => $"Connection {connection.Named} names no URL to sign in to, so there is nothing to test.",
        _ => $"Connection {connection.Named} has no test: its service declares no request to try it with.",
    };

    /// <summary>The probe a connection's test sends and the values no sentence may carry; or, with
    /// none, the sentence its health records (<c>NeedsYou</c>) or the one only answered (<c>Unrecorded</c>).</summary>
    private async Task<(ConnectionProbe? Probe, IReadOnlyList<string> Secrets, string? NeedsYou, string? Unrecorded)> ProbeAsync(
        ConnectionRecord connection, CancellationToken ct)
    {
        if (connection.Mailbox is { } mailbox)
        {
            if (await store.MailboxPasswordAsync(connection.Id, ct) is not { } password)
            {
                return (null, [], null, $"The app password of connection {connection.Named} could not be read on this Host. Update its password in {Where}.");
            }

            return (new MailboxProbe(
                connection.Account, mailbox.Username,
                new ProbeServer(mailbox.Imap.Host, mailbox.Imap.Port, mailbox.Imap.Security),
                new ProbeServer(mailbox.Smtp.Host, mailbox.Smtp.Port, mailbox.Smtp.Security), mailbox.Preset, password), [password], null, null);
        }

        if (connection.Credential is { } website && connection.Kind == ConnectionKinds.Website)
        {
            return await WebsiteProbeAsync(connection, website, WebsiteProbe.Verify, ct) is { } verify
                ? (verify, WebsiteSecrets(verify), null, null)
                : (null, [], null, $"A value of connection {connection.Named} could not be read on this Host. Edit the connection in {Where}.");
        }

        if (connection.Credential is { } credential)
        {
            var secrets = await store.CredentialSecretsAsync(connection.Id, ct) ?? new Dictionary<string, string>();
            var probe = CredentialPresets.For(connection.Kind, credential.Preset)?.Test is { } test
                ? test.Probe(CredentialPresets.For(connection.Kind, credential.Preset)!, credential, secrets)
                : null;

            return probe is null
                ? (null, [], null, $"A value connection {connection.Named} is tested with could not be read on this Host. Edit the connection in {Where}.")
                : (probe, probe.Secrets, null, null);
        }

        var tokens = await store.TokensAsync(connection.Id, ct);
        if (tokens?.RefreshToken is not { } refresh)
        {
            return (null, [], "The provider issued no refresh token, so the sign-in cannot be renewed. Reconnect it.", null);
        }

        if (await ProviderAsync(connection.Provider, ct) is not { Configured: true } found)
        {
            return (null, [], null, $"Connection {connection.Named} could not be tested: its provider '{connection.Provider}' has no OAuth client on this Host. Set it in {Where}.");
        }

        var provider = ConnectionProviders.InDirectory(found, connection.Tenant);
        var secret = await ClientSecretAsync(provider, ct);

        // MICROSOFT answers one API's token per refresh: the sign-in's own scopes give the token its
        // profile endpoint reads, whatever APIs the connection holds.
        IReadOnlyList<string>? scopes = provider.Kind == ConnectionProviders.Microsoft
            ? [.. provider.DefaultScopes.Where(ConnectionProviders.IdentityScopes.Contains).Union(["offline_access"], StringComparer.Ordinal)]
            : null;

        return (new OAuthProbe(provider.Name, provider.TokenUrl, provider.ClientId ?? "", secret, refresh, scopes, provider.UserinfoUrl),
            [refresh, .. secret is null ? Array.Empty<string>() : [secret]], null, null);
    }

    private static MailServer Server(MailServerRequest request) =>
        new(request.Host!.Trim().ToLowerInvariant(), request.Port!.Value, request.Security!.Trim().ToUpperInvariant());

    /// <summary>Why a mailbox cannot be added as asked, naming the field, or null.</summary>
    private static string? MailboxRefusal(AddMailbox request)
    {
        if (!string.IsNullOrWhiteSpace(request.Preset) && !MailPresets.IsKnown(request.Preset.Trim()))
        {
            return $"`preset` is one of {string.Join(", ", MailPresets.All.Select(p => p.Id))}, or left out.";
        }

        var account = request.Account?.Trim() ?? "";
        if (account.Length is 0 or > 254 || !account.Contains('@') || !Printable(account, space: false))
        {
            return "`account` is required: the mailbox's email address, such as person@example.com.";
        }

        if (!string.IsNullOrWhiteSpace(request.Username) && (request.Username.Trim().Length > 254 || !Printable(request.Username.Trim(), space: false)))
        {
            return "`username` is the name the mail server signs in with, at most 254 characters with no spaces.";
        }

        if (request.Name is { } name && name.Trim().Length > MaximumNameLength) return $"`name` is longer than {MaximumNameLength} characters.";

        if (PasswordRefusal(request.Password) is { } password) return password;

        return ServerRefusal("imap", "IMAP", request.Imap) ?? ServerRefusal("smtp", "SMTP", request.Smtp);
    }

    private static string? ServerRefusal(string field, string what, MailServerRequest? server)
    {
        var host = server?.Host?.Trim() ?? "";
        if (host.Length is 0 or > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            return $"`{field}.host` is required: the {what} server's name, such as {field}.example.com.";
        }

        if (server!.Port is not (>= 1 and <= 65535)) return $"`{field}.port` is the {what} server's port, 1 to 65535.";

        if (server.Security?.Trim().ToUpperInvariant() is not (MailServer.Tls or MailServer.StartTls))
        {
            return $"`{field}.security` is {MailServer.Tls} or {MailServer.StartTls}.";
        }

        return null;
    }

    /// <summary>An app password: 4 to 256 printable ASCII characters (spaces allowed, as Gmail shows
    /// one), so it can be sent in a login and kept out of everything a plugin writes.</summary>
    private static string? PasswordRefusal(string? password) =>
        password is null || password.Length < PluginMemberRunner.MinimumSecretLength || password.Length > 256 || !Printable(password, space: true)
            ? $"`password` is the app password: {PluginMemberRunner.MinimumSecretLength} to 256 characters, letters, digits, spaces and punctuation."
            : null;

    private static bool Printable(string text, bool space) => text.All(c => c is > ' ' and <= '~' || (space && c == ' '));

    // ---- bindings -------------------------------------------------------------------------------

    /// <summary>
    /// Why <paramref name="bindings"/> (slot to connection id) cannot be bound on a member of
    /// <paramref name="manifest"/>, or null. <paramref name="assignedToTeam"/> is set for an agent's hire
    /// (a Manager or a Concierge): each connection must then be one a person assigned to that team.
    /// <paramref name="retry"/> ends a missing-scope refusal: what the person does after the
    /// Reconnect offered beside it.
    /// </summary>
    public async Task<BindingRefusal?> BindingRefusalAsync(
        PluginManifest manifest, IReadOnlyDictionary<string, string> bindings, IReadOnlySet<string>? assignedToTeam,
        CancellationToken ct = default, string retry = BindAgain)
    {
        foreach (var (key, connectionId) in bindings)
        {
            // A MULTIPLE SLOT'S SECOND, THIRD... CONNECTION is bound under `<slot>#2`, `<slot>#3`; any
            // other slot takes one, so a second binding of it is refused.
            var slot = PluginConnectionSlot.BindingKey(key)?.Slot ?? key;

            if (!manifest.Connections.TryGetValue(slot, out var declared))
            {
                return new BindingRefusal($"`{key}` is not a connection slot of plugin '{manifest.Id}'. It has: "
                    + (manifest.Connections.Count == 0 ? "none" : string.Join(", ", manifest.Connections.Keys)) + ".");
            }

            if (slot != key && !declared.Multiple)
            {
                return new BindingRefusal($"Slot `{slot}` takes one connection, so `{key}` cannot bind another. "
                    + "Only a slot its manifest declares `\"multiple\": true` takes several.");
            }

            if (slot != key && bindings.Any(other => other.Key != key && other.Value == connectionId && PluginConnectionSlot.BindingKey(other.Key)?.Slot == slot))
            {
                return new BindingRefusal($"Slot `{slot}` binds the connection '{connectionId}' twice. Each of its connections is bound once.");
            }

            if (assignedToTeam is not null && !assignedToTeam.Contains(connectionId))
            {
                return new BindingRefusal(
                    $"The connection '{connectionId}' named for slot `{slot}` is not assigned to this team. "
                    + "Only a person assigns a connection to a team, in Team settings, Connections; a hire may then bind any connection assigned to its own team.");
            }

            if (await store.GetAsync(connectionId, ct) is not { } connection)
            {
                return new BindingRefusal($"There is no connection '{connectionId}'. A person connects an account in {Where}.");
            }

            if (ScopeRefusal(slot, declared, connection, retry) is { } refusal) return refusal;
        }

        return null;
    }

    /// <summary>Of <paramref name="ids"/>, each existing connection not yet assigned to <paramref name="team"/>:
    /// what a person's binding of them assigns, each with its own <c>team.connection-assigned</c> row.</summary>
    public async Task<IReadOnlyList<ConnectionRecord>> NotYetAssignedAsync(string team, IEnumerable<string> ids, CancellationToken ct = default)
    {
        var assigned = await AssignedIdsAsync(team, ct);
        var list = new List<ConnectionRecord>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal).Where(id => !assigned.Contains(id)))
        {
            if (await store.GetAsync(id, ct) is { } connection) list.Add(connection);
        }

        return list;
    }

    /// <summary>A <c>team.connection-assigned</c> row's detail: never a value.</summary>
    public static object AssignedDetail(string team, ConnectionRecord connection, string via, string? clonedFrom = null) =>
        new { team, connection = connection.Id, name = connection.Name, kind = connection.Kind, via, clonedFrom };

    /// <summary>The ids of the connections assigned to <paramref name="team"/> (its stored id).</summary>
    public async Task<IReadOnlySet<string>> AssignedIdsAsync(string team, CancellationToken ct = default) =>
        (await store.AssignedAsync(team, ct)).Select(a => a.ConnectionId).ToHashSet(StringComparer.Ordinal);

    /// <summary>How a missing-scope refusal ends where a binding is saved.</summary>
    public const string BindAgain = "bind it again";

    /// <summary>Why <paramref name="connection"/> cannot serve <paramref name="slot"/>: the wrong
    /// provider, or a scope it was not granted. That one offers Reconnect with exactly the missing
    /// scopes, and its sentence names that Reconnect - not Admin → Connections, whose Reconnect asks
    /// for no scopes. With no <paramref name="retry"/> there is no Reconnect beside the sentence (a
    /// run refused), so it names the member's settings, whose Reconnect asks for them.</summary>
    public static BindingRefusal? ScopeRefusal(string slot, PluginConnectionSlot declared, ConnectionRecord connection, string? retry = BindAgain)
    {
        if (!declared.Admits(connection.Provider))
        {
            var names = declared.Providers.Select(ConnectionProviders.Display).ToList();
            var joined = names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1];
            return new BindingRefusal($"Slot `{slot}` takes a {joined} connection; {connection.Named} is a {ConnectionProviders.Display(connection.Provider)} connection.");
        }

        var asked = declared.ScopesFor(connection.Provider);
        var missing = asked.Where(s => !connection.Scopes.Contains(s, StringComparer.Ordinal)).ToList();

        // EVERY API'S SCOPES are checked; a slot spanning several Microsoft APIs names each one's.
        var apis = connection.Provider == ConnectionProviders.Microsoft && MicrosoftApis.Several(asked);

        if (missing.Count > 0)
        {
            return new BindingRefusal(
                $"Connection {connection.Named} was not granted the scope{(missing.Count == 1 ? "" : "s")} "
                + string.Join(", ", missing.Select(s => apis ? $"`{s}` ({MicrosoftApis.NameOf(MicrosoftApis.ApiOf(s))})" : $"`{s}`"))
                + $" that slot `{slot}` needs. "
                + (retry is null
                    ? $"Reconnect it from the member's settings, which asks for {(missing.Count == 1 ? "it" : "them")}."
                    : $"Press Reconnect to grant {(missing.Count == 1 ? "it" : "them")}, then {retry}."),
                connection.Id, missing);
        }

        return null;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static string NewId() => "conn-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>The sentence a name another connection has is refused with.</summary>
    public static string NameTaken(string name) =>
        $"Another connection is already called '{name}'. Every connection has its own name: choose another.";

    /// <summary><paramref name="name"/>, or the first of "name (2)", "name (3)", ... no connection has:
    /// what a connection is called when the person named none and its account's name is taken.</summary>
    public async Task<string> UniqueNameAsync(string name, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length > MaximumNameLength) name = name[..MaximumNameLength].TrimEnd();
        if (!await store.NameTakenAsync(name, null, ct)) return name;

        for (var n = 2; ; n++)
        {
            var suffix = $" ({n})";
            var candidate = (name.Length + suffix.Length > MaximumNameLength ? name[..(MaximumNameLength - suffix.Length)].TrimEnd() : name) + suffix;
            if (!await store.NameTakenAsync(candidate, null, ct)) return candidate;
        }
    }

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
public sealed class RefreshNotStoredException(System.Data.Common.DbException inner)
    : Exception("A refreshed token could not be stored.", inner)
{
    /// <summary>The engine's own number for the fault (<see cref="DatabaseFault.Code"/>).</summary>
    public int Code => DatabaseFault.Code(inner);
}

/// <summary>Body of <c>PUT /api/connections/providers/{id}</c>.</summary>
public sealed record ProviderChange(
    string? ClientId, string? ClientSecret, string? Tenant = null, string? Name = null,
    string? AuthorizeUrl = null, string? TokenUrl = null, string? UserinfoUrl = null, IReadOnlyList<string>? DefaultScopes = null,
    string? Audience = null, string? TenantId = null);

/// <summary>Body of <c>POST /api/connections/start</c>. <see cref="Tenant"/> is a Microsoft connection's
/// directory: a tenant ID or a domain; left out, a Reconnect keeps the connection's, and empty clears it.</summary>
public sealed record StartConnection(
    string? Provider, IReadOnlyList<string>? Scopes = null, string? Name = null, string? ReconnectId = null, string? RedirectUri = null,
    string? Flow = null, string? Tenant = null);

/// <summary>Body of <c>POST /api/connections/imap</c>. Its printed form leaves the password out.</summary>
public sealed record AddMailbox(
    string? Preset, string? Name, string? Account, string? Username, string? Password, MailServerRequest? Imap, MailServerRequest? Smtp)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Preset = {Preset}, Name = {Name}, Account = {Account}, Username = {Username}, Imap = {Imap}, Smtp = {Smtp}");
        return true;
    }
}

/// <summary>One server of <see cref="AddMailbox"/>.</summary>
public sealed record MailServerRequest(string? Host, int? Port, string? Security);

/// <summary>Body of <c>PUT /api/connections/{id}/mailbox</c>: what is given replaces what is stored. Its
/// printed form leaves the password out.</summary>
public sealed record EditMailbox(string? Account, string? Username, string? Password, MailServerRequest? Imap, MailServerRequest? Smtp)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Account = {Account}, Username = {Username}, Imap = {Imap}, Smtp = {Smtp}");
        return true;
    }
}

/// <summary>Body of <c>PUT /api/connections/{id}/password</c>. Its printed form leaves the password out.</summary>
public sealed record MailboxPassword(string? Password)
{
    private bool PrintMembers(StringBuilder builder) => false;
}

/// <summary>Body of <c>POST /api/connections/complete</c>.</summary>
public sealed record CompleteConnection(string? State, string? Code);
