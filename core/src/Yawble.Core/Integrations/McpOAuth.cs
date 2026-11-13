using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Connections;

namespace Yawble.Core.Integrations;

/// <summary>A sign-in that can no longer be used without the person (a refused refresh, nothing to refresh with): the Connection is already marked needs reconnect.</summary>
public sealed class McpSignInExpiredException(string message) : McpUnauthorizedException(message, null);

/// <summary>
/// The MCP authorization flow ("The server signs me in"): protected-resource metadata (RFC 9728) names the authorization server, whose
/// metadata (RFC 8414, or OpenID discovery, through <see cref="OAuthDiscovery"/>) gives its endpoints; Yawble registers itself (RFC 7591),
/// then <see cref="OAuthFlow"/> sends the person's browser to sign in with PKCE S256, a random state and the MCP URL as the resource
/// (RFC 8707), takes the code at a loopback redirect, and keeps the tokens in a Connection of kind oauth, refreshing them on the host.
/// Every address used must be https (or http to this computer); no redirect is followed; every reply is capped at 1 MB.
/// Tokens, codes, verifiers and the server's WWW-Authenticate value are never logged or put in a message.
/// </summary>
public sealed class McpOAuth(OAuthFlow flow)
{
    public const int MaxReplyBytes = OAuthFlow.MaxReplyBytes;

    /// <summary>A flow of its own (tests); the host shares one <see cref="OAuthFlow"/> with the APIs, so a Connection has one refresh at a time.</summary>
    public McpOAuth(HttpMessageHandler handler, ConnectionService conns, IBrowser browser, Func<IRedirectReceiver> redirect, TimeProvider clock)
        : this(new OAuthFlow(handler, conns, browser, redirect, clock)) { }

    /// <summary>How long the person has to finish in the browser.</summary>
    internal TimeSpan SignInTimeout { get => flow.SignInTimeout; set => flow.SignInTimeout = value; }

    private OAuthHttp Http => flow.Http;

    // ------------------------------------------------------------------ signing in

    public async Task<long> SignInAsync(string server, Uri mcpUrl, string? wwwAuthenticate, long? existingConnectionId, CancellationToken ct)
    {
        var resource = mcpUrl.AbsoluteUri;
        var (asUrl, prmScopes, fromOrigin) = await ProtectedResourceAsync(mcpUrl, wwwAuthenticate, ct);
        var meta = await OAuthDiscovery.MetadataAsync(Http, asUrl, ct)
            ?? throw new McpException(fromOrigin ? $"the server did not say where to sign in (no sign-in information at {mcpUrl.Host})" : "the sign-in server did not describe itself (no authorization server metadata)");
        var provider = OAuthDiscovery.Provider(meta);
        var register = meta["registration_endpoint"] is null ? throw new McpException("the server's sign-in does not let Yawble register itself, so it cannot sign in here")
            : OAuthHttp.Endpoint(meta, "registration_endpoint", "registration address");
        // an MCP server must offer S256 when it lists its methods (an absent list is taken as yes, as the MCP spec's servers do)
        if (meta["code_challenge_methods_supported"] is JsonArray && !provider.Pkce)
            throw new McpException("the server's sign-in does not support PKCE with S256, so Yawble will not use it");

        await using var receiver = flow.OpenRedirect();
        var registration = await Http.SendJsonAsync(register, new JsonObject
        {
            ["client_name"] = "Yawble", ["redirect_uris"] = new JsonArray(receiver.RedirectUri.AbsoluteUri),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"), ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        }, "registration", ct);
        var clientId = OAuthHttp.Str(registration["client_id"]) is { Length: > 0 } cid ? cid : throw new McpException("the server's registration gave no client id");
        var clientSecret = OAuthHttp.Str(registration["client_secret"]) is { Length: > 0 } cs ? cs : null;

        var client = new OAuthClient(clientId, clientSecret, provider.AuthorizeEndpoint, provider.TokenEndpoint, prmScopes, resource,
            OAuthHttp.Str(meta["issuer"]), provider.IssRequired);
        return await flow.SignInWithAsync(receiver, $"{server} sign-in", client, existingConnectionId, [], ct);
    }

    /// <summary>The authorization server named by the protected-resource metadata, and the scopes it lists.</summary>
    private async Task<(Uri Server, IReadOnlyList<string> Scopes, bool FromOrigin)> ProtectedResourceAsync(Uri mcpUrl, string? www, CancellationToken ct)
    {
        var origin = new Uri(mcpUrl.GetLeftPart(UriPartial.Authority));
        // each candidate: the address, and whether its resource may be the origin (the bare and the named ones) or must be the MCP URL exactly
        var candidates = new List<(Uri Url, bool OriginOk)>();
        var named = ResourceMetadataOf(www);
        if (named is not null)
        {
            if (!Uri.TryCreate(named, UriKind.Absolute, out var u)) throw new McpException("the server's sign-in information address is not valid");
            candidates.Add((OAuthHttp.Checked(u, "sign-in information address"), true));
        }
        else
        {
            var path = mcpUrl.AbsolutePath.TrimEnd('/');
            if (path.Length > 0) candidates.Add((new Uri(origin, "/.well-known/oauth-protected-resource" + path), false));
            candidates.Add((new Uri(origin, "/.well-known/oauth-protected-resource"), true));
        }
        JsonObject? prm = null;
        var originOk = false;
        foreach (var c in candidates)
            if ((prm = await Http.GetJsonAsync(c.Url, ct)) is not null) { originOk = c.OriginOk; break; }
        if (prm is null)
        {
            if (named is not null) throw new McpException("the server did not say where to sign in (its sign-in information could not be found)");
            // no protected-resource metadata at all: the MCP server's origin is the authorization server (it must still describe itself)
            return (origin, [], true);
        }
        // RFC 9728 §3.3: the metadata must be about this server
        if (OAuthHttp.Str(prm["resource"]) is not { Length: > 0 } res || !(OAuthHttp.SameUrl(res, mcpUrl.AbsoluteUri) || originOk && OAuthHttp.SameUrl(res, origin.AbsoluteUri)))
            throw new McpException("the server's sign-in information is not about this address, so Yawble will not use it");
        var first = (prm["authorization_servers"] as JsonArray)?.Select(OAuthHttp.Str).FirstOrDefault(s => s is { Length: > 0 })
            ?? throw new McpException("the server did not name where to sign in");
        if (!Uri.TryCreate(first, UriKind.Absolute, out var server)) throw new McpException("the server named a sign-in address that is not valid");
        OAuthHttp.Checked(server, "sign-in server");
        var scopes = (prm["scopes_supported"] as JsonArray ?? []).Select(OAuthHttp.Str).OfType<string>().Where(s => s.Length > 0 && !s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))).ToList();
        return (server, scopes, false);
    }

    // ------------------------------------------------------------------ tokens

    /// <summary>The access token, refreshed first when it expires within a minute. Concurrent callers share one refresh.</summary>
    public Task<string> AccessTokenAsync(long connectionId, CancellationToken ct) => flow.AccessTokenAsync(connectionId, ct);

    /// <summary>
    /// After the server refused <paramref name="refused"/>: refresh (unless another caller already replaced that token) and return the new token.
    /// Throws <see cref="McpSignInExpiredException"/> (the Connection marked) when it cannot be refreshed.
    /// </summary>
    public Task<string> RefreshAfterRefusalAsync(long connectionId, string? refused, CancellationToken ct) => flow.RefreshAfterRefusalAsync(connectionId, refused, ct);

    // ------------------------------------------------------------------ helpers

    /// <summary>The resource_metadata parameter of a Bearer challenge (RFC 9728 §5.1), quoted or not.</summary>
    internal static string? ResourceMetadataOf(string? www)
    {
        if (string.IsNullOrEmpty(www)) return null;
        var m = Regex.Match(www, "(?:^|[\\s,])resource_metadata\\s*=\\s*(?:\"([^\"]*)\"|([^\\s,]+))", RegexOptions.IgnoreCase);
        return !m.Success ? null : m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    /// <summary>Whether a sign-in made for <paramref name="resource"/> belongs to the server at <paramref name="url"/>: same origin and path (a trailing slash aside).</summary>
    public static bool SameResource(string? resource, string? url) =>
        Uri.TryCreate(resource, UriKind.Absolute, out var r) && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && string.Equals(r.GetLeftPart(UriPartial.Path).TrimEnd('/'), u.GetLeftPart(UriPartial.Path).TrimEnd('/'), StringComparison.Ordinal);
}
