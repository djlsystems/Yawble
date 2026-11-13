using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Connections;

namespace Yawble.Core.Integrations;

/// <summary>
/// An OAuth app to sign in with: its client (the secret only when the provider gave one), the provider's sign-in and token addresses, the
/// scopes to ask for, the RFC 8707 resource (only when one is bound, as an MCP server's URL is) and the issuer the answer must come from
/// (RFC 9207; <paramref name="IssRequired"/> when the provider promises to name itself).
/// </summary>
public sealed record OAuthClient(string ClientId, string? ClientSecret, Uri AuthorizeEndpoint, Uri TokenEndpoint, IReadOnlyList<string> Scopes, string? Resource, string? Issuer, bool IssRequired);

/// <summary>
/// The authorization-code flow with PKCE S256 and its tokens, for any provider whose client and endpoints are known: the person's browser
/// signs in with a random state, the code comes back at a loopback redirect, and the tokens are kept in a Connection of kind oauth.
/// Access tokens are refreshed on the host shortly before they expire, one refresh per Connection at a time (shared by every API and MCP
/// server signed in with it). Every address used must be https (or http to this computer); no redirect is followed; every reply is capped at
/// 1 MB. Tokens, codes, verifiers and client secrets are never logged or put in a message.
/// </summary>
public sealed class OAuthFlow(HttpMessageHandler handler, ConnectionService conns, IBrowser browser, Func<IRedirectReceiver> redirect, TimeProvider clock)
{
    public const int MaxReplyBytes = 1024 * 1024;
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromSeconds(60);
    private const string Expired = "the sign-in expired; sign in again";
    private const string NotNow = "could not refresh the sign-in right now; try again";

    /// <summary>The fields the flow itself saves; extra fields may not reuse them.</summary>
    private static readonly HashSet<string> OwnKeys = new(StringComparer.Ordinal)
    {
        "access_token", "refresh_token", "client_secret", "client_id", "token_endpoint", "authorize_endpoint", "expires_at", "granted_scopes", "resource",
    };

    /// <summary>How long the person has to finish in the browser.</summary>
    internal TimeSpan SignInTimeout { get; set; } = TimeSpan.FromMinutes(5);

    internal OAuthHttp Http { get; } = new(handler);
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new();

    // ------------------------------------------------------------------ signing in

    /// <summary>
    /// Signs the person in with <paramref name="client"/> and saves the tokens: into <paramref name="existingConnectionId"/> (keeping its name)
    /// when it still exists, else a new Connection named <paramref name="connectionName"/> (" 2", " 3" when that is taken).
    /// <paramref name="extraFields"/> (plain values) are saved after the flow's own fields.
    /// </summary>
    public async Task<long> SignInAsync(string connectionName, OAuthClient client, long? existingConnectionId, IReadOnlyList<ConnectionField> extraFields, CancellationToken ct)
    {
        CheckClient(connectionName, client, extraFields);
        await using var receiver = redirect();
        return await SignInWithAsync(receiver, connectionName, client, existingConnectionId, extraFields, ct);
    }

    /// <summary>A redirect receiver for a caller that must know the redirect address before it has a client (dynamic registration).</summary>
    internal IRedirectReceiver OpenRedirect() => redirect();

    /// <summary>The sign-in through a receiver the caller opened (and disposes).</summary>
    internal async Task<long> SignInWithAsync(IRedirectReceiver receiver, string connectionName, OAuthClient client, long? existingConnectionId,
        IReadOnlyList<ConnectionField> extraFields, CancellationToken ct)
    {
        CheckClient(connectionName, client, extraFields);
        var redirectUri = receiver.RedirectUri.AbsoluteUri;
        var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(32));
        var query = new List<(string, string)>
        {
            ("response_type", "code"), ("client_id", client.ClientId), ("redirect_uri", redirectUri), ("code_challenge", challenge),
            ("code_challenge_method", "S256"), ("state", state),
        };
        if (client.Resource is not null) query.Add(("resource", client.Resource));
        if (client.Scopes.Count > 0) query.Add(("scope", string.Join(' ', client.Scopes)));
        var authorize = client.AuthorizeEndpoint;
        var sep = authorize.Query.Length > 1 ? "&" : "?";
        var url = new Uri(authorize.AbsoluteUri + sep + string.Join('&', query.Select(q => Uri.EscapeDataString(q.Item1) + "=" + Uri.EscapeDataString(q.Item2))));

        try { browser.Open(url); }
        catch (Exception e) when (e is not McpException)
        {
            throw new McpException($"could not open your browser ({e.GetType().Name}); open the sign-in page at {url.Host} yourself and try again");
        }
        IReadOnlyDictionary<string, string> answer;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(SignInTimeout);
            try { answer = await receiver.WaitAsync(state, wait.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new McpException("the sign-in did not finish in time; sign in again"); }
        }

        if (!answer.TryGetValue("state", out var got) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(state)))
            throw new McpException("the sign-in answer did not match the one Yawble started; sign in again");
        if (answer.TryGetValue("error", out var error))
            throw new McpException(error == "access_denied" ? "the sign-in was cancelled" : $"the sign-in failed ({OAuthHttp.Code(error)})");
        // RFC 9207: an authorization server that names itself must be the one Yawble asked
        if (answer.TryGetValue("iss", out var iss) ? client.Issuer is not null && !OAuthHttp.SameUrl(iss, client.Issuer) : client.IssRequired)
            throw new McpException("the sign-in answer came from another server; sign in again");
        if (!answer.TryGetValue("code", out var code) || code.Length == 0) throw new McpException("the sign-in gave no code; sign in again");

        var form = new List<(string, string)>
        {
            ("grant_type", "authorization_code"), ("code", code), ("redirect_uri", redirectUri), ("client_id", client.ClientId), ("code_verifier", verifier),
        };
        if (client.Resource is not null) form.Add(("resource", client.Resource));
        if (client.ClientSecret is not null) form.Add(("client_secret", client.ClientSecret));
        var (status, tokens) = await Http.PostFormAsync(client.TokenEndpoint, form, ct);
        if (status is < 200 or >= 300) throw new McpException($"the server did not accept the sign-in ({status}{OAuthHttp.ErrorCode(tokens)}); sign in again");
        if (tokens is null) throw new McpException("the server's sign-in answer could not be read; sign in again");
        var (access, refresh, expiresAt) = Tokens(tokens);
        // only an absent scope means "what was asked"; nothing asked and nothing named: the grant is unknown, and not saved
        var granted = ScopesOf(tokens) ?? (client.Scopes.Count > 0 ? string.Join(' ', client.Scopes) : null);

        var fields = new List<ConnectionField> { new("access_token", "Access token", true, null) };
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal) { ["access_token"] = access };
        if (refresh is not null) { fields.Add(new("refresh_token", "Refresh token", true, null)); secrets["refresh_token"] = refresh; }
        if (client.ClientSecret is not null) { fields.Add(new("client_secret", "Client secret", true, null)); secrets["client_secret"] = client.ClientSecret; }
        fields.Add(new("client_id", "Client id", false, client.ClientId));
        fields.Add(new("token_endpoint", "Token address", false, client.TokenEndpoint.AbsoluteUri));
        fields.Add(new("authorize_endpoint", "Sign-in address", false, client.AuthorizeEndpoint.AbsoluteUri));
        if (expiresAt is not null) fields.Add(new("expires_at", "Expires at", false, expiresAt));
        if (granted is not null) fields.Add(new("granted_scopes", "Granted scopes", false, granted));
        if (client.Resource is not null) fields.Add(new("resource", "Resource", false, client.Resource));
        fields.AddRange(extraFields);

        var gate = existingConnectionId is { } eid ? Gate(eid) : null;
        if (gate is not null) await gate.WaitAsync(ct);
        try
        {
            if (existingConnectionId is { } id && conns.Get(id) is { } view)
                return conns.Save(id, view.Name, fields, secrets, kind: "oauth").Id;
            var baseName = connectionName.Trim();
            var taken = conns.List().Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pick = baseName;
            for (var i = 2; taken.Contains(pick); i++) pick = $"{baseName} {i}";
            return conns.Save(null, pick, fields, secrets, kind: "oauth").Id;
        }
        finally { gate?.Release(); }
    }

    /// <summary>Before anything is opened or sent: the client is usable and its addresses are https (or http to this computer).</summary>
    private static void CheckClient(string connectionName, OAuthClient client, IReadOnlyList<ConnectionField> extraFields)
    {
        if (string.IsNullOrWhiteSpace(connectionName)) throw new ArgumentException("a sign-in needs a connection name");
        if (string.IsNullOrWhiteSpace(client.ClientId) || client.ClientId.Any(char.IsControl)) throw new McpException("the app's client ID is not valid");
        if (client.ClientSecret is { } cs && (cs.Length == 0 || cs.Any(char.IsControl))) throw new McpException("the app's client secret is not valid");
        if (client.Scopes.Any(s => s.Length == 0 || s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
            throw new McpException("a scope to ask for is not valid (scopes have no spaces)");
        OAuthHttp.Checked(client.AuthorizeEndpoint, "sign-in page");
        OAuthHttp.Checked(client.TokenEndpoint, "token address");
        foreach (var f in extraFields)
            if (f.Secret || OwnKeys.Contains(f.Key)) throw new ArgumentException($"'{f.Key}' cannot be an extra field of a sign-in");
    }

    // ------------------------------------------------------------------ tokens

    /// <summary>The access token, refreshed first when it expires within a minute. Concurrent callers share one refresh.</summary>
    public async Task<string> AccessTokenAsync(long connectionId, CancellationToken ct)
    {
        var (token, expiring) = Current(connectionId);
        if (!expiring) return token!;
        var gate = Gate(connectionId);
        await gate.WaitAsync(ct);
        try
        {
            (token, expiring) = Current(connectionId);
            return expiring ? await RefreshLockedAsync(connectionId, ct) : token!;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// After the server refused <paramref name="refused"/>: refresh (unless another caller already replaced that token) and return the new token.
    /// Throws <see cref="McpSignInExpiredException"/> (the Connection marked) when it cannot be refreshed.
    /// </summary>
    public async Task<string> RefreshAfterRefusalAsync(long connectionId, string? refused, CancellationToken ct)
    {
        var gate = Gate(connectionId);
        await gate.WaitAsync(ct);
        try
        {
            var (token, expiring) = Current(connectionId);
            if (token is not null && !expiring && refused is not null && token != refused) return token;
            return await RefreshLockedAsync(connectionId, ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The scopes a Connection was granted (the last token response's scope, else what was asked); null when it records none: the grant is
    /// unknown. Empty when the provider named a scope that lists nothing usable: nothing was granted.
    /// </summary>
    internal static HashSet<string>? GrantedOf(ConnectionView view) =>
        view.Fields.FirstOrDefault(f => f.Key == "granted_scopes" && !f.Secret) is { } g
            ? (g.Shown ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) : null;

    private SemaphoreSlim Gate(long id) => _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    /// <summary>Runs an edit of the Connection while no refresh of it is in flight (a refresh saves the fields it read, and would undo the edit).</summary>
    internal async Task<T> EditAsync<T>(long connectionId, Func<T> edit, CancellationToken ct)
    {
        var gate = Gate(connectionId);
        await gate.WaitAsync(ct);
        try { return edit(); }
        finally { gate.Release(); }
    }

    /// <summary>The stored access token (null: none) and whether it is due for a refresh.</summary>
    private (string? Token, bool Expiring) Current(long id)
    {
        var view = conns.Get(id) ?? throw new McpSignInExpiredException("the sign-in is gone; sign in again");
        var secrets = conns.Secrets(id);
        if (!secrets.TryGetValue("access_token", out var token) || token.Length == 0) return (null, true);
        var at = view.Fields.FirstOrDefault(f => f.Key == "expires_at")?.Shown;
        var expiring = at is not null && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            && when - clock.GetUtcNow() <= RefreshAhead;
        return (token, expiring);
    }

    private async Task<string> RefreshLockedAsync(long id, CancellationToken ct)
    {
        var view = conns.Get(id) ?? throw new McpSignInExpiredException("the sign-in is gone; sign in again");
        var secrets = conns.Secrets(id);
        string? Plain(string key) => view.Fields.FirstOrDefault(f => f.Key == key && !f.Secret)?.Shown is { Length: > 0 } v ? v : null;
        var clientId = Plain("client_id");
        var endpoint = Plain("token_endpoint");
        if (!secrets.TryGetValue("refresh_token", out var refresh) || clientId is null || endpoint is null || !Uri.TryCreate(endpoint, UriKind.Absolute, out var tokenUrl))
            throw Expire(id);
        OAuthHttp.Checked(tokenUrl, "token address");
        var form = new List<(string, string)> { ("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", clientId) };
        if (Plain("resource") is { } resource) form.Add(("resource", resource));
        if (secrets.TryGetValue("client_secret", out var cs)) form.Add(("client_secret", cs));
        int status;
        JsonObject? tokens;
        try { (status, tokens) = await Http.PostFormAsync(tokenUrl, form, ct); }
        catch (McpException) { throw new McpException(NotNow); } // unreachable, timed out, redirected, too big: try later, the sign-in may be fine
        // only an answer that says the grant or the client is no longer good needs the person; anything else (429, 408, 5xx) is for later
        if (status == 401 || status == 400 && OAuthHttp.Str(tokens?["error"]) is "invalid_grant" or "invalid_client" or "unauthorized_client") throw Expire(id);
        if (status is < 200 or >= 300 || tokens is null) throw new McpException(NotNow);
        var (access, newRefresh, expiresAt) = Tokens(tokens!);
        var granted = ScopesOf(tokens!);

        // keep the fields as they are; replace the tokens, the expiry and (when the answer says) the granted scopes
        var fields = new List<ConnectionField>();
        foreach (var f in view.Fields.Where(f => f.Key != "expires_at"))
            fields.Add(f.Key == "granted_scopes" && !f.Secret && granted is not null
                ? new ConnectionField(f.Key, f.Label, false, granted)
                : new ConnectionField(f.Key, f.Label, f.Secret, f.Secret ? null : f.Shown));
        if (granted is not null && !fields.Any(f => f.Key == "granted_scopes")) fields.Add(new("granted_scopes", "Granted scopes", false, granted));
        if (expiresAt is not null) fields.Add(new("expires_at", "Expires at", false, expiresAt));
        var given = new Dictionary<string, string>(StringComparer.Ordinal) { ["access_token"] = access };
        if (newRefresh is not null) given["refresh_token"] = newRefresh;
        conns.Save(id, view.Name, fields, given, keepStatus: true);
        return access;
    }

    private McpSignInExpiredException Expire(long id)
    {
        conns.MarkStatus(id, "needs_reconnect", Expired);
        return new McpSignInExpiredException(Expired);
    }

    private (string Access, string? Refresh, string? ExpiresAt) Tokens(JsonObject o)
    {
        var access = OAuthHttp.Str(o["access_token"]) is { Length: > 0 } a ? a : throw new McpException("the server's sign-in gave no access token");
        if (OAuthHttp.Str(o["token_type"]) is { } type && !type.Equals("bearer", StringComparison.OrdinalIgnoreCase))
            throw new McpException("the server's sign-in gave a token Yawble cannot use");
        if (access.Any(c => c is '\r' or '\n' || char.IsControl(c))) throw new McpException("the server's sign-in gave a token that is not valid");
        var refresh = OAuthHttp.Str(o["refresh_token"]) is { Length: > 0 } r ? r : null;
        string? expiresAt = null;
        if (o["expires_in"] is JsonValue ev && (ev.TryGetValue<double>(out var secs) || ev.TryGetValue<string>(out var es) && double.TryParse(es, NumberStyles.Float, CultureInfo.InvariantCulture, out secs)) && secs > 0)
            expiresAt = clock.GetUtcNow().AddSeconds(Math.Min(secs, 10L * 365 * 24 * 3600)).ToString("O", CultureInfo.InvariantCulture);
        return (access, refresh, expiresAt);
    }

    /// <summary>
    /// The token response's scope (RFC 6749 §5.1), space-separated and cleaned (commas also separate: GitHub answers "repo,gist"); null when
    /// the answer has no scope, empty when every entry it lists is dropped.
    /// </summary>
    private static string? ScopesOf(JsonObject o)
    {
        if (OAuthHttp.Str(o["scope"]) is not { } text) return null;
        var scopes = text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.Length <= OAuthDiscovery.MaxScopeLength && !s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            .Distinct(StringComparer.Ordinal).Take(OAuthDiscovery.MaxScopes).ToList();
        return string.Join(' ', scopes);
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
