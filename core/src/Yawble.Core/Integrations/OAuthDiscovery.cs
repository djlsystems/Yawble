using System.Text.Json.Nodes;

namespace Yawble.Core.Integrations;

/// <summary>
/// What a provider's authorization server metadata says: its issuer, sign-in and token addresses, the scopes it offers, whether it offers
/// PKCE with S256 (the flow sends PKCE either way; a provider that does not know it ignores it) and whether it promises to name itself in the
/// sign-in answer (RFC 9207).
/// </summary>
public sealed record OAuthProvider(Uri Issuer, Uri AuthorizeEndpoint, Uri TokenEndpoint, IReadOnlyList<string> Scopes, bool Pkce, bool IssRequired);

/// <summary>
/// Finds a provider's authorization server metadata from its address: RFC 8414 §3 (path insertion), then OpenID discovery (inserted, then
/// appended). The document's issuer must be the address asked (§3.3, a trailing slash aside) and its endpoints https (or http to this
/// computer); otherwise it is refused with a sentence. Same request rules as the flow: no redirect, 1 MB, 30 s.
/// </summary>
public static class OAuthDiscovery
{
    internal const int MaxScopes = 200;
    internal const int MaxScopeLength = 200;

    /// <summary>The provider, or null when no metadata document answers (each address 4xx).</summary>
    public static async Task<OAuthProvider?> DiscoverAsync(HttpMessageHandler handler, Uri provider, CancellationToken ct)
    {
        using var http = new OAuthHttp(handler);
        return await MetadataAsync(http, provider, ct) is { } meta ? Provider(meta) : null;
    }

    /// <summary>The metadata document whose issuer is <paramref name="server"/>, or null when none answers.</summary>
    internal static async Task<JsonObject?> MetadataAsync(OAuthHttp http, Uri server, CancellationToken ct)
    {
        OAuthHttp.Checked(server, "provider address");
        var origin = new Uri(server.GetLeftPart(UriPartial.Authority));
        var path = server.AbsolutePath.TrimEnd('/');
        var candidates = path.Length == 0
            ? new[] { new Uri(origin, "/.well-known/oauth-authorization-server"), new Uri(origin, "/.well-known/openid-configuration") }
            : [new Uri(origin, "/.well-known/oauth-authorization-server" + path), new Uri(origin, "/.well-known/openid-configuration" + path), new Uri(origin, path + "/.well-known/openid-configuration")];
        foreach (var c in candidates)
        {
            if (await http.GetJsonAsync(c, ct) is not { } meta) continue;
            if (OAuthHttp.Str(meta["issuer"]) is not { } issuer || !OAuthHttp.SameUrl(issuer, server.AbsoluteUri))
                throw new McpException("the sign-in server's issuer is not the server Yawble asked, so Yawble will not use it");
            return meta;
        }
        return null;
    }

    /// <summary>The provider a metadata document describes (its issuer already checked by <see cref="MetadataAsync"/>).</summary>
    internal static OAuthProvider Provider(JsonObject meta)
    {
        var issuer = new Uri(OAuthHttp.Str(meta["issuer"])!, UriKind.Absolute);
        var authorize = OAuthHttp.Endpoint(meta, "authorization_endpoint", "sign-in page");
        var token = OAuthHttp.Endpoint(meta, "token_endpoint", "token address");
        // RFC 8414 §2: an absent list means no PKCE is offered
        var pkce = meta["code_challenge_methods_supported"] is JsonArray methods && methods.Any(m => OAuthHttp.Str(m) == "S256");
        var issRequired = meta["authorization_response_iss_parameter_supported"] is JsonValue ip && ip.TryGetValue<bool>(out var ipb) && ipb;
        var scopes = (meta["scopes_supported"] as JsonArray ?? []).Select(OAuthHttp.Str).OfType<string>()
            .Where(s => s.Length is > 0 and <= MaxScopeLength && !s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            .Distinct(StringComparer.Ordinal).Take(MaxScopes).ToList();
        return new OAuthProvider(issuer, authorize, token, scopes, pkce, issRequired);
    }
}
