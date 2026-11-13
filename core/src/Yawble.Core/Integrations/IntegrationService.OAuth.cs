using System.Globalization;
using System.Text.Json.Nodes;
using Yawble.Core.Connections;

namespace Yawble.Core.Integrations;

/// <summary>
/// OAuth sign-in Connections: the person's own OAuth app signs in through the browser (<see cref="OAuthFlow"/>,
/// the one flow MCP servers share, so a Connection is refreshed once at a time), with the provider's addresses found from its metadata or
/// from an API's spec, and the scopes from the provider's list or, for an API, from the actions that are allowed or ask first. Editing the
/// Connection keeps its tokens unless the app or the addresses change. Nothing here crosses the Cloud Link (RemoteAccess refuses it).
/// </summary>
public sealed partial class IntegrationService
{
    private static bool IsOAuthMethod(string method) => method.StartsWith("integrations.oauth", StringComparison.Ordinal);

    private const string OAuthChanged = "sign in again: the app or the addresses changed";
    private const string ApisPlace = "Settings → Integrations → APIs";

    /// <summary>The fields of an OAuth Connection the person may edit (the rest are the flow's).</summary>
    private static readonly string[] OAuthEditable = ["client_id", "authorize_endpoint", "token_endpoint", "provider"];

    /// <summary>The fields that, when changed, make the tokens useless (another app, or another provider).</summary>
    private static readonly string[] OAuthIdentity = ["client_id", "authorize_endpoint", "token_endpoint"];

    // ------------------------------------------------------------------ discover

    /// <summary>integrations.oauthDiscover: the provider's sign-in and token addresses and the scopes it offers, or found: false.</summary>
    private async Task<JsonNode> OAuthDiscoverAsync(JsonObject? p, CancellationToken ct)
    {
        var provider = ProviderUrl(OptStr(p, "provider")) ?? throw new ArgumentException("give the provider's address (for example https://accounts.example.com)");
        OAuthProvider? found;
        try { found = await OAuthDiscovery.DiscoverAsync(McpHandler(), provider, ct); }
        catch (McpException e) { throw new ArgumentException(CliRunner.Redact(e.Message)); }
        if (found is null) return new JsonObject { ["found"] = false };
        return new JsonObject
        {
            ["found"] = true, ["issuer"] = found.Issuer.AbsoluteUri, ["authorizeUrl"] = found.AuthorizeEndpoint.AbsoluteUri, ["tokenUrl"] = found.TokenEndpoint.AbsoluteUri,
            ["scopes"] = new JsonArray(found.Scopes.Select(s => (JsonNode?)s).ToArray()),
        };
    }

    private static Uri? ProviderUrl(string? text) =>
        text is null ? null : HttpUrl(text) ?? throw new ArgumentException("the provider's address must be a full web address starting with https://");

    private static Uri? GivenUrl(JsonObject? p, string key, string what)
    {
        var text = OptStr(p, key)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var u = HttpUrl(text) ?? throw new ArgumentException($"the {what} must be a full web address starting with https://");
        try { return OAuthHttp.Checked(u, what); }
        catch (McpException e) { throw new ArgumentException(e.Message); }
    }

    // ------------------------------------------------------------------ sign in

    /// <summary>
    /// integrations.oauthSignIn: signs in through the browser with the person's app and keeps the tokens in an oauth Connection (a new one, or
    /// <c>connectionId</c>'s). With <c>api</c>: the addresses are the spec's (given ones must be the same), found from its OpenID address, or
    /// given when the spec has none; the scopes are those of the API's actions that are allowed or ask first anywhere; and the API is signed
    /// in with the Connection. An empty client secret on an existing Connection keeps the stored one.
    /// </summary>
    private async Task<JsonNode> OAuthSignInAsync(JsonObject? p, CancellationToken ct)
    {
        var existing = OptLong(p, "connectionId") is { } cid ? Conns.Get(cid) ?? throw new ArgumentException("no such connection") : null;
        if (existing is { Kind: not "oauth" }) throw new ArgumentException($"'{existing.Name}' is not an OAuth sign-in; sign in into a new connection instead");
        // an MCP server's sign-in is bound to its resource: signing in again here (with an API or without) would drop that, so it is the server's to do
        if (existing is not null && PlainField(existing, "resource") is not null)
            throw new ArgumentException($"'{existing.Name}' is the sign-in of an MCP server; sign in again in {McpPlace} (Sign in), or sign in as a new connection instead");
        var clientId = OptStr(p, "clientId")?.Trim() is { Length: > 0 } ci ? ci : throw new ArgumentException("give your app's client ID");
        var secretText = OptStr(p, "clientSecret");
        var clientSecret = !string.IsNullOrEmpty(secretText) ? secretText
            : existing is not null && Conns.Secrets(existing.Id).TryGetValue("client_secret", out var kept) ? kept : null;
        var provider = ProviderUrl(OptStr(p, "provider")?.Trim() is { Length: > 0 } pt ? pt : null);
        var givenAuthorize = GivenUrl(p, "authorizeUrl", "sign-in address");
        var givenToken = GivenUrl(p, "tokenUrl", "token address");

        ApiRecord? api = null;
        if (OptStr(p, "api")?.Trim() is { Length: > 0 } apiName)
        {
            api = ApiRow(apiName);
            if (api.Kind == "website") throw new ArgumentException($"{api.Name} is a website: it signs in with a website login, never OAuth");
            if (AuthType(api.Auth) != "oauth2") throw new ArgumentException($"{api.Name}'s spec does not say it signs in with OAuth");
            // signing in again into a Connection is for one made for this API only: a shared one is left alone
            if (existing is not null && existing.UsedBy.Any(u => !string.Equals(u, api.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"'{existing.Name}' is also used by {string.Join(", ", existing.UsedBy.Where(u => !string.Equals(u, api.Name, StringComparison.OrdinalIgnoreCase)))}; sign in as a new connection instead");
        }

        Uri authorize, token;
        string? issuer = null;
        var issRequired = false;
        IReadOnlyList<string> scopes;
        try
        {
            var specAuthorize = api is null ? null : HttpUrl(OptStr(api.Auth, "authorizeUrl"));
            var specToken = api is null ? null : HttpUrl(OptStr(api.Auth, "tokenUrl"));
            if (api is not null)
            {
                if (givenAuthorize is not null && specAuthorize is not null && !OAuthHttp.SameUrl(givenAuthorize.AbsoluteUri, specAuthorize.AbsoluteUri))
                    throw new ArgumentException($"the sign-in address must be the one {api.Name}'s spec gives ({specAuthorize.AbsoluteUri})");
                if (givenToken is not null && specToken is not null && !OAuthHttp.SameUrl(givenToken.AbsoluteUri, specToken.AbsoluteUri))
                    throw new ArgumentException($"the token address must be the one {api.Name}'s spec gives ({specToken.AbsoluteUri})");
            }
            // the provider's metadata, when an address is still unknown (an API's OpenID address, else the provider given) or a provider is named
            OAuthProvider? found = null;
            var from = (specAuthorize is null || specToken is null) && api is not null ? OpenIdIssuer(OptStr(api.Auth, "openIdConnectUrl")) ?? provider : provider;
            if (from is not null && (api is null || specAuthorize is null || specToken is null))
                found = await OAuthDiscovery.DiscoverAsync(McpHandler(), from, ct);
            if (found is not null)
            {
                // given addresses must be the provider's own (no silent repointing)
                if (specAuthorize is null && givenAuthorize is not null && !OAuthHttp.SameUrl(givenAuthorize.AbsoluteUri, found.AuthorizeEndpoint.AbsoluteUri))
                    throw new ArgumentException($"the sign-in address must be the one the provider gives ({found.AuthorizeEndpoint.AbsoluteUri})");
                if (specToken is null && givenToken is not null && !OAuthHttp.SameUrl(givenToken.AbsoluteUri, found.TokenEndpoint.AbsoluteUri))
                    throw new ArgumentException($"the token address must be the one the provider gives ({found.TokenEndpoint.AbsoluteUri})");
                issuer = found.Issuer.AbsoluteUri;
                issRequired = found.IssRequired;
            }
            var whose = api is null ? "the provider does not say it" : $"{api.Name}'s spec does not say it";
            authorize = specAuthorize ?? found?.AuthorizeEndpoint ?? givenAuthorize ?? throw new ArgumentException($"give the sign-in address ({whose})");
            token = specToken ?? found?.TokenEndpoint ?? givenToken ?? throw new ArgumentException($"give the token address ({whose})");
            scopes = api is not null ? NeededScopes(api) : GivenScopes(p);
        }
        catch (McpException e) { throw new ArgumentException(CliRunner.Redact(e.Message)); }

        var name = OptStr(p, "name")?.Trim() is { Length: > 0 } n ? n
            : existing?.Name ?? (api is not null ? $"{api.Name} sign-in" : (provider ?? authorize).Host);
        var providerShown = provider?.AbsoluteUri.TrimEnd('/') ?? existing?.Fields.FirstOrDefault(f => f.Key == "provider" && !f.Secret)?.Shown;
        List<ConnectionField> extra = providerShown is { Length: > 0 } ? [new("provider", "Provider", false, providerShown)] : [];

        var key = "oauth:" + (api?.Name ?? existing?.Id.ToString(CultureInfo.InvariantCulture) ?? name);
        if (!_signingIn.TryAdd(key, 0)) throw new ArgumentException("a sign-in for this is already open in your browser: finish it there");
        try
        {
            long id;
            try { id = await OAuthFlow.SignInAsync(name, new OAuthClient(clientId, clientSecret, authorize, token, scopes, null, issuer, issRequired), existing?.Id, extra, ct); }
            catch (McpException e) { throw new ArgumentException(CliRunner.Redact(e.Message)); }
            if (api is not null)
                Exec("UPDATE apis SET connection_id = $c, updated_at = $now WHERE id = $id;", ("$c", id), ("$now", Now()), ("$id", api.Id));
            Conns.MarkStatus(id, "works", $"signed in at {Stamp()}");
            var b = Conns.Brief(id)!;
            return new JsonObject
            {
                ["ok"] = true,
                ["connection"] = new JsonObject { ["id"] = b.Id, ["name"] = b.Name, ["status"] = b.Status, ["statusReason"] = b.StatusReason },
            };
        }
        finally { _signingIn.TryRemove(key, out _); }
    }

    /// <summary>The issuer an OpenID Connect discovery address belongs to (its "/.well-known/openid-configuration" taken off), or null.</summary>
    private static Uri? OpenIdIssuer(string? openIdConnectUrl)
    {
        const string WellKnown = "/.well-known/openid-configuration";
        if (HttpUrl(openIdConnectUrl) is not { } u || u.Query.Length > 1) return null;
        var path = u.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith(WellKnown, StringComparison.Ordinal)) return null;
        return new Uri(u.GetLeftPart(UriPartial.Authority) + path[..^WellKnown.Length]);
    }

    /// <summary>The scopes the person ticked from the provider's list (or typed in Advanced): no spaces in one, no repeats.</summary>
    private static List<string> GivenScopes(JsonObject? p)
    {
        if (p?["scopes"] is null) return [];
        if (p["scopes"] is not JsonArray list) throw new ArgumentException("scopes is a list of scope names");
        var scopes = new List<string>();
        foreach (var n in list)
        {
            var s = n is JsonValue v && v.TryGetValue<string>(out var t) ? t.Trim() : throw new ArgumentException("each scope is a name");
            if (s.Length == 0) continue;
            if (s.Length > OAuthDiscovery.MaxScopeLength || s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new ArgumentException("a scope name has no spaces");
            if (!scopes.Contains(s)) scopes.Add(s);
        }
        return scopes.Count > OAuthDiscovery.MaxScopes ? throw new ArgumentException("too many scopes") : scopes;
    }

    /// <summary>
    /// The scopes an API's sign-in needs: those of its actions (not gone) that are allowed or ask first for any scope of rules, the default
    /// list and every team's own (a team falls back to the default list where it has no rule). With no rules saved for the API at all, the
    /// app's suggestion by class: read-only and change actions, never destructive or sign-in ones. Never adds openid on its own.
    /// </summary>
    private List<string> NeededScopes(ApiRecord api)
    {
        var global = ApiRulesOf(api.Id, null);
        var teams = ApiRuleTeams(api.Id).Concat(ApiTeamsOf(api.Id)).Distinct().Select(t => ApiRulesOf(api.Id, t)).ToList();
        var noRules = global.Count == 0 && teams.All(t => t.Count == 0);
        bool Open(ApiOp o) => noRules ? o.Class is not (CommandClass.Destructive or CommandClass.Credential)
            : ApiDecide(o, [], global).Decision != Decisions.Deny || teams.Any(t => ApiDecide(o, t, global).Decision != Decisions.Deny);
        return ApiOps(api.Id).Where(o => !o.Gone && o.Scopes.Count > 0 && Open(o)).SelectMany(o => o.Scopes).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// A Connection's granted scopes, or null when it records none (a remote MCP server's sign-in, a pasted token, a sign-in that asked for no scope and
    /// was told none): the grant is then unknown, and nothing is refused for scopes.
    /// </summary>
    private static HashSet<string>? GrantedOf(ConnectionView view) =>
        view.Kind == "oauth" ? OAuthFlow.GrantedOf(view) : null;

    private static string? PlainField(ConnectionView view, string key) => view.Fields.FirstOrDefault(f => f.Key == key && !f.Secret)?.Shown is { Length: > 0 } v ? v : null;

    /// <summary>apiList's oauth part of an OAuth 2 API: the spec's addresses, the scopes the ticked actions need and those its Connection lacks.</summary>
    private JsonObject OAuthApiJson(ApiRecord a)
    {
        var needed = NeededScopes(a);
        var granted = a.ConnectionId is { } cid ? connections?.Get(cid) is { } v ? GrantedOf(v) : null : null;
        return new JsonObject
        {
            ["authorizeUrl"] = OptStr(a.Auth, "authorizeUrl"), ["tokenUrl"] = OptStr(a.Auth, "tokenUrl"), ["openIdConnectUrl"] = OptStr(a.Auth, "openIdConnectUrl"),
            ["neededScopes"] = new JsonArray(needed.Select(s => (JsonNode?)s).ToArray()),
            ["missingScopes"] = new JsonArray((granted is null ? [] : needed.Where(s => !granted.Contains(s))).Select(s => (JsonNode?)s).ToArray()),
        };
    }

    // ------------------------------------------------------------------ the API's side

    /// <summary>
    /// The bearer sign-in from an oauth Connection: refused when the Connection signs in elsewhere than the API's spec says, has no token
    /// (it must be signed in again), or was not granted a scope <paramref name="op"/> needs (only a recorded grant is enforced). The access
    /// token is refreshed on the host shortly before it expires.
    /// </summary>
    private async Task<(ApiSign? Sign, string? Refusal, string? Rule)> OAuthSignAsync(ApiRecord api, ApiOp? op, ConnectionView view, CancellationToken ct)
    {
        var type = AuthType(api.Auth);
        if (type is not ("bearer" or "oauth2"))
            return (null, $"refused: the connection '{view.Name}' is an OAuth sign-in, and {api.Name} signs in with a key; ask the person to pick another connection in {ApisPlace}", "no connection");
        // a sign-in bound to a resource (an MCP server's) stays with that resource's origin
        if (PlainField(view, "resource") is { } resource && !SameOriginText(resource, api.BaseUrl))
            return (null, $"refused: {ResourceRefusal(view, resource, api)}", "no connection");
        if (type == "oauth2")
            foreach (var (specKey, field) in new[] { ("authorizeUrl", "authorize_endpoint"), ("tokenUrl", "token_endpoint") })
                if (HttpUrl(OptStr(api.Auth, specKey)) is { } spec && (PlainField(view, field) is not { } mine || !OAuthHttp.SameUrl(mine, spec.AbsoluteUri)))
                    return (null, $"refused: the connection '{view.Name}' signs in somewhere else than {api.Name}'s spec says; ask the person to sign in again in {ApisPlace}", "no connection");
        if (!view.Fields.Any(f => f.Key == "access_token" && f.Secret))
            return (null, $"refused: the connection '{view.Name}' needs signing in again; ask the person to sign in again in {ApisPlace}", "no connection");
        if (op is not null && GrantedOf(view) is { } granted && op.Scopes.Any(s => !granted.Contains(s)))
            return (null, $"refused: {api.Name}'s {op.Id} needs access the sign-in does not have; ask the person to sign in again in {ApisPlace} (Sign in again to allow the new actions)", "scope not granted");
        string token;
        try { token = await OAuthFlow.AccessTokenAsync(view.Id, ct); }
        catch (McpSignInExpiredException) { return (null, "refused: " + NeedsReconnecting(view, api), "needs reconnect"); }
        catch (McpException) { return (null, $"refused: {api.Name}'s sign-in could not be refreshed right now; try again later", "sign-in refresh"); }
        catch (Credentials.CredentialStoreException) { return (null, $"refused: the connection '{view.Name}' has no key {api.Name} can sign in with; ask the person to fix it in {ApisPlace}", "no connection"); }
        if (token.IndexOfAny(['\r', '\n', '\0']) >= 0)
            return (null, $"refused: the connection '{view.Name}' has no key {api.Name} can sign in with; ask the person to fix it in {ApisPlace}", "no connection");
        return (new ApiSign(false, "Authorization", "Bearer " + token, [token], view.Id, token), null, null);
    }

    private static bool SameOriginText(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var ua) && HttpUrl(b) is { } ub && SameOrigin(ua, ub);

    private static string ResourceRefusal(ConnectionView view, string resource, ApiRecord api) =>
        $"{view.Name} is a sign-in for {(Uri.TryCreate(resource, UriKind.Absolute, out var r) ? r.Host : resource)}, not for {HttpUrl(api.BaseUrl)?.Host ?? api.Name}; " +
        $"give {api.Name} its own OAuth sign-in in {ApisPlace}";

    /// <summary>What the agent is told when a Connection needs the person (Needs You shows it).</summary>
    private static string NeedsReconnecting(ConnectionView view, ApiRecord api) =>
        $"The connection '{view.Name}' needs reconnecting: it now shows under Needs You for the person. Stop using {api.Name} until it is reconnected.";

    // ------------------------------------------------------------------ editing and testing the Connection

    /// <summary>
    /// integrations.connectionSave of an oauth Connection: the name, the app's client ID and secret, the addresses and the provider may be
    /// edited; the tokens are the flow's and are kept, unless the client ID or an address changed: then they are dropped and the Connection
    /// needs a new sign-in. An empty client secret keeps the stored one. Made under the flow's gate for the Connection, so a refresh in flight
    /// (which saves the fields it read) cannot write over the edit.
    /// </summary>
    private Task<JsonNode> OAuthConnectionSaveAsync(long id, JsonObject? p, CancellationToken ct) =>
        OAuthFlow.EditAsync(id, () => OAuthConnectionSave(Conns.Get(id) ?? throw new ArgumentException("no such connection"), p), ct);

    private JsonNode OAuthConnectionSave(ConnectionView view, JsonObject? p)
    {
        var name = OptStr(p, "name")?.Trim() is { Length: > 0 } n ? n : view.Name;
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in p?["fields"] as JsonArray ?? [])
            if (node is JsonObject o && OptStr(o, "key") is { } k && OAuthEditable.Contains(k) && o["value"] is JsonValue)
                given[k] = (OptStr(o, "value") ?? "").Trim();
        foreach (var (k, v) in given)
        {
            if (k == "client_id" && (v.Length == 0 || v.Any(char.IsControl))) throw new ArgumentException("the app's client ID is not valid");
            if (k is "authorize_endpoint" or "token_endpoint")
            {
                var u = HttpUrl(v) ?? throw new ArgumentException("an address must be a full web address starting with https://");
                try { OAuthHttp.Checked(u, k == "token_endpoint" ? "token address" : "sign-in address"); }
                catch (McpException e) { throw new ArgumentException(e.Message); }
            }
            if (k == "provider" && v.Length > 0) _ = ProviderUrl(v);
        }

        var changed = false;
        var fields = new List<ConnectionField>();
        foreach (var f in view.Fields)
        {
            if (f.Secret) { fields.Add(new ConnectionField(f.Key, f.Label, true, null)); continue; }
            var value = f.Shown;
            if (given.TryGetValue(f.Key, out var nv))
            {
                if (OAuthIdentity.Contains(f.Key) && !(f.Key == "client_id" ? nv == (f.Shown ?? "") : OAuthHttp.SameUrl(nv, f.Shown ?? ""))) changed = true;
                value = nv;
            }
            fields.Add(new ConnectionField(f.Key, f.Label, false, value));
        }
        if (given.TryGetValue("provider", out var prov) && prov.Length > 0 && !fields.Any(f => f.Key == "provider")) fields.Add(new("provider", "Provider", false, prov));
        fields.RemoveAll(f => f.Key == "provider" && string.IsNullOrEmpty(f.Value) && !f.Secret);

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (p?["secrets"] is JsonObject so && so["client_secret"] is JsonValue cv && cv.TryGetValue<string>(out var cs) && cs.Length > 0)
        {
            if (cs.Any(char.IsControl)) throw new ArgumentException("the app's client secret is not valid");
            secrets["client_secret"] = cs;
            if (!fields.Any(f => f.Key == "client_secret")) fields.Insert(0, new("client_secret", "Client secret", true, null));
        }
        // another app or another provider: the tokens it gave are of no use, and the grant is gone with them
        if (changed) fields.RemoveAll(f => f.Key is "access_token" or "refresh_token" or "expires_at" or "granted_scopes");

        Conns.Save(view.Id, name, fields, secrets, keepStatus: true);
        if (changed) Conns.MarkStatus(view.Id, "needs_reconnect", OAuthChanged);
        return ConnectionJson(Conns.Get(view.Id)!);
    }

    /// <summary>
    /// integrations.connectionTest of an oauth Connection: a refresh when it has a refresh token (works, or needs reconnect as the provider
    /// answers; a provider that cannot answer now leaves the status); else works while the access token has not expired, then needs
    /// reconnect. Null when it has a token that never expires: the APIs using it are tried instead.
    /// </summary>
    private async Task<JsonNode?> OAuthConnectionTestAsync(ConnectionView view, CancellationToken ct)
    {
        JsonObject Marked(string status, string reason)
        {
            Conns.MarkStatus(view.Id, status, reason);
            return new JsonObject { ["status"] = status, ["reason"] = reason, ["changed"] = true };
        }
        IReadOnlyDictionary<string, string> secrets;
        try { secrets = Conns.Secrets(view.Id); }
        catch (Credentials.CredentialStoreException) { secrets = new Dictionary<string, string>(); }
        if (secrets.ContainsKey("refresh_token"))
        {
            try
            {
                await OAuthFlow.RefreshAfterRefusalAsync(view.Id, null, ct);
                return Marked("works", $"the sign-in was refreshed at {Stamp()}");
            }
            catch (McpSignInExpiredException)
            {
                var b = Conns.Brief(view.Id);
                return new JsonObject { ["status"] = b?.Status ?? "needs_reconnect", ["reason"] = b?.StatusReason, ["changed"] = true };
            }
            catch (McpException e)
            {
                return new JsonObject { ["status"] = view.Status, ["reason"] = $"{CliRunner.Redact(e.Message)}; status unchanged", ["changed"] = false };
            }
            catch (Credentials.CredentialStoreException)
            {
                return new JsonObject { ["status"] = view.Status, ["reason"] = "the sign-in could not be read or saved in this computer's credential store; status unchanged", ["changed"] = false };
            }
        }
        if (!secrets.ContainsKey("access_token"))
            return view.Status == "needs_reconnect" ? new JsonObject { ["status"] = view.Status, ["reason"] = view.StatusReason, ["changed"] = false }
                : Marked("needs_reconnect", "sign in again");
        if (PlainField(view, "expires_at") is not { } at) return null;
        if (DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until) && until > _clock.GetUtcNow())
            return Marked("works", $"signed in until {until.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}");
        return Marked("needs_reconnect", "the sign-in expired; sign in again");
    }
}
