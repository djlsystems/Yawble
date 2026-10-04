namespace Harness.Host;

/// <summary>
/// AN OAUTH PROVIDER a connection belongs to, as the Host uses it: its endpoints, the client a person
/// set, and what the build knows about it. <c>google</c> and <c>microsoft</c> are built in; anything
/// else that speaks OAuth 2.0 authorization code + PKCE is <c>custom-&lt;id&gt;</c>.
///
/// The client SECRET is not here: <see cref="ConnectionStore.ClientSecretAsync"/> is the one read of
/// it, made only where the Host talks to the provider. Nothing that answers a route holds it.
/// </summary>
public sealed record OAuthProvider(
    string Id,
    string Kind,
    string Name,
    string? ClientId,
    bool ClientSecretSet,
    string? Tenant,
    string AuthorizeUrl,
    string TokenUrl,
    string? UserinfoUrl,
    string? RevokeUrl,
    IReadOnlyList<string> DefaultScopes,
    IReadOnlyDictionary<string, string> AuthorizeParameters,
    string Help,
    string? DeviceAuthorizationUrl = null)
{
    public bool Configured => !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(AuthorizeUrl) && !string.IsNullOrWhiteSpace(TokenUrl);

    public bool Revokes => RevokeUrl is not null;

    /// <summary>What a route answers: never the secret, only whether one is set. <paramref name="guide"/>
    /// is the provider's setup guide (<see cref="ConnectionGuides.View"/>), or null.</summary>
    public object View(object? guide = null) => new
    {
        id = Id,
        kind = Kind,
        name = Name,
        clientId = ClientId,
        clientSecretSet = ClientSecretSet,
        configured = Configured,
        tenant = Kind == ConnectionProviders.Microsoft ? Tenant ?? ConnectionProviders.MicrosoftDefaultTenant : null,
        audience = Kind == ConnectionProviders.Microsoft ? ConnectionProviders.AudienceOf(Tenant) : null,
        deviceFlow = DeviceAuthorizationUrl is not null,
        authorizeUrl = AuthorizeUrl,
        tokenUrl = TokenUrl,
        userinfoUrl = UserinfoUrl,
        revokes = Revokes,
        defaultScopes = DefaultScopes,
        help = Help,
        guide,
    };
}

/// <summary>The stored half of a provider: what a person set. Null URLs on a built-in.</summary>
public sealed record StoredProvider(
    string Id,
    string Kind,
    string? Name,
    string? ClientId,
    bool ClientSecretSet,
    string? Tenant,
    string? AuthorizeUrl,
    string? TokenUrl,
    string? UserinfoUrl,
    IReadOnlyList<string> DefaultScopes);

/// <summary>What the build knows about each built-in provider, and how a custom one is named.</summary>
public static class ConnectionProviders
{
    public const string Google = "google";

    public const string Microsoft = "microsoft";

    public const string Custom = "custom";

    public const string CustomPrefix = "custom-";

    public const string MicrosoftDefaultTenant = "common";

    /// <summary>Who can sign in to a Microsoft public client, as the guided setup asks it: personal and
    /// any work account (<c>common</c>), work accounts only (<c>organizations</c>), or one tenant.</summary>
    public const string AudienceCommon = "common";

    public const string AudienceOrganizations = "organizations";

    public const string AudienceTenant = "tenant";

    /// <summary>The who-can-sign-in choice a stored tenant reads as, or null for one set under Advanced
    /// that is none of them (a domain, <c>consumers</c>).</summary>
    public static string? AudienceOf(string? tenant) => tenant switch
    {
        null or "" or MicrosoftDefaultTenant => AudienceCommon,
        AudienceOrganizations => AudienceOrganizations,
        _ when Guid.TryParse(tenant, out _) => AudienceTenant,
        _ => null,
    };

    /// <summary>A GUID as Entra writes one: 8-4-4-4-12 hex digits.</summary>
    public static bool IsGuid(string? text) => Guid.TryParseExact(text?.Trim(), "D", out _);

    /// <summary>The OpenID Connect scopes that are not an API's: granted with an ID token or a
    /// refresh token, and often left out of a token response's <c>scope</c>.</summary>
    public static readonly IReadOnlySet<string> IdentityScopes =
        new HashSet<string>(StringComparer.Ordinal) { "openid", "email", "profile", "offline_access" };

    public static IReadOnlyList<string> BuiltIns { get; } = [Google, Microsoft];

    public static bool IsBuiltIn(string id) => id is Google or Microsoft;

    /// <summary><c>custom-</c> then lowercase letters, digits and hyphens, at most 40 characters.</summary>
    public static bool IsCustomId(string id) =>
        id.StartsWith(CustomPrefix, StringComparison.Ordinal)
        && id.Length is > 7 and <= 40
        && id[CustomPrefix.Length..].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && char.IsAsciiLetterOrDigit(id[CustomPrefix.Length]);

    /// <summary>The shape of a Google OAuth client ID: a name, then <c>.apps.googleusercontent.com</c>.</summary>
    public static bool IsGoogleClientId(string clientId) =>
        clientId.EndsWith(GoogleClientIdSuffix, StringComparison.Ordinal)
        && clientId.Length > GoogleClientIdSuffix.Length
        && clientId[..^GoogleClientIdSuffix.Length].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public const string GoogleClientIdSuffix = ".apps.googleusercontent.com";

    public static bool IsKnownId(string id) => IsBuiltIn(id) || IsCustomId(id);

    public static string KindOf(string id) => IsBuiltIn(id) ? id : Custom;

    /// <summary>A person-facing name for a provider id or manifest provider word.</summary>
    public static string Display(string id) => id switch
    {
        Google => "Google",
        Microsoft => "Microsoft",
        Custom => "custom",
        _ => id,
    };

    /// <summary>The provider as the Host uses it: the build's endpoints under what a person stored.</summary>
    public static OAuthProvider Resolve(string id, StoredProvider? stored)
    {
        switch (id)
        {
            case Google:
                return new OAuthProvider(
                    Google, Google, "Google", stored?.ClientId, stored?.ClientSecretSet == true, null,
                    "https://accounts.google.com/o/oauth2/v2/auth",
                    "https://oauth2.googleapis.com/token",
                    "https://openidconnect.googleapis.com/v1/userinfo",
                    "https://oauth2.googleapis.com/revoke",
                    ["openid", "email"],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        // What makes Google issue a refresh token, every time, including on Reconnect.
                        ["access_type"] = "offline",
                        ["prompt"] = "consent",
                    },
                    "Create a \"Desktop app\" OAuth client for the CLI flow (it accepts http://127.0.0.1 on any port), "
                    + "or a \"Web application\" client with the redirect URI shown here registered. Publish the "
                    + "consent screen \"In production\" or its refresh tokens expire after 7 days.");

            case Microsoft:
                var tenant = string.IsNullOrWhiteSpace(stored?.Tenant) ? MicrosoftDefaultTenant : stored!.Tenant!;
                var authority = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0";
                return new OAuthProvider(
                    Microsoft, Microsoft, "Microsoft", stored?.ClientId, stored?.ClientSecretSet == true, tenant,
                    authority + "/authorize",
                    authority + "/token",
                    "https://graph.microsoft.com/oidc/userinfo",
                    // Microsoft has no refresh-token revocation endpoint.
                    null,
                    ["openid", "email", "offline_access"],
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    "Register an app in Microsoft Entra ID with public client flows allowed, and paste its Application "
                    + "(client) ID: you sign in with a code, and no secret or redirect URI is needed. Under Advanced, a "
                    + "\"Web\" app with a client secret and the redirect URIs shown here works as before.",
                    authority + "/devicecode");

            default:
                return new OAuthProvider(
                    id, Custom, string.IsNullOrWhiteSpace(stored?.Name) ? id : stored!.Name!,
                    stored?.ClientId, stored?.ClientSecretSet == true, null,
                    stored?.AuthorizeUrl ?? "", stored?.TokenUrl ?? "", stored?.UserinfoUrl, null,
                    stored?.DefaultScopes ?? [],
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    "Any OAuth 2.0 provider that supports the authorization code grant with PKCE. Register the "
                    + "redirect URI shown here with it.");
        }
    }

    /// <summary>Why <paramref name="url"/> cannot be a provider endpoint, or null: absolute https, or
    /// http on loopback for testing.</summary>
    public static string? UrlRefusal(string field, string? url, bool required)
    {
        if (string.IsNullOrWhiteSpace(url)) return required ? $"`{field}` is required for a custom provider." : null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            return $"`{field}` must be an absolute https:// URL.";
        }

        return null;
    }

    /// <summary>
    /// Whether a manifest's provider word <paramref name="allowed"/> admits a connection of provider
    /// <paramref name="providerId"/>: the same id, or <c>custom</c> for every custom provider.
    /// </summary>
    public static bool Admits(string allowed, string providerId) =>
        allowed == providerId || (allowed == Custom && IsCustomId(providerId));
}
