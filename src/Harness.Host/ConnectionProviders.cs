namespace Harness.Host;

/// <summary>
/// AN OAUTH PROVIDER a connection belongs to, as the Host uses it: its endpoints, the client a person
/// set, and what the build knows about it. <c>google</c> and <c>microsoft</c> are built in; anything
/// else that speaks OAuth 2.0 authorization code + PKCE is <c>custom-&lt;id&gt;</c>.
///
/// <see cref="ClientSource"/> says where the client is from: <c>saved</c> by a person, <c>default</c> (the
/// Microsoft public client the instance was started with, used while no Microsoft client is saved), or
/// null while there is none.
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
    string? DeviceAuthorizationUrl = null,
    string? ClientSource = null)
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
        // The default client's ID is the instance's, not a person's: the forms that edit a client never see it.
        clientId = ClientSource == ConnectionProviders.ClientSourceDefault ? null : ClientId,
        clientSource = ClientSource,
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

    /// <summary>Where a provider's client is from, as <c>clientSource</c> answers it.</summary>
    public const string ClientSourceSaved = "saved";

    public const string ClientSourceDefault = "default";

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

    /// <summary>
    /// What a person reads when Microsoft refuses a sign-in because the account's organisation lets only
    /// an admin approve the app: one sentence, with no error code.
    /// </summary>
    public const string MicrosoftAdminApproval =
        "An admin of that organisation approves the app before its accounts can connect, so ask one to approve it and then sign in again.";

    /// <summary>The Entra codes and OAuth errors that mean an admin of the organisation has to approve
    /// the app first.</summary>
    private static readonly string[] AdminApprovalMarks =
        ["AADSTS65001", "AADSTS90094", "AADSTS90095", "AADSTS900941", "AADSTS90099", "admin_consent_required", "consent_required"];

    /// <summary>Whether Microsoft's refusal (<paramref name="error"/>, <paramref name="description"/>)
    /// is an organisation that needs an admin's approval for the app.</summary>
    public static bool NeedsAdminApproval(string? error, string? description) =>
        AdminApprovalMarks.Any(mark =>
            (error?.Contains(mark, StringComparison.OrdinalIgnoreCase) ?? false)
            || (description?.Contains(mark, StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>
    /// What a person reads when Microsoft refuses a sign-in because the account is not in the directory
    /// the sign-in went to, or is a personal account that directory does not take: one sentence, with no
    /// error code and none of Microsoft's own text.
    /// </summary>
    public const string MicrosoftNotInDirectory =
        "The account was not found in the directory this sign-in used: a personal Microsoft account that owns an Azure subscription "
        + "signs in through that subscription's directory, so enter its ID under Directory and sign in again, and a work account in that directory works too.";

    /// <summary>
    /// The line a Microsoft code that ran out adds after the expiry sentence: Microsoft's own page refuses
    /// such an account ("This username may be incorrect") without telling the Host, so the code just expires.
    /// </summary>
    public const string MicrosoftExpiredNotInDirectory =
        "If Microsoft said the username may be incorrect, the account is not in the directory this sign-in used: name that directory under Directory and sign in again.";

    /// <summary>
    /// The Entra codes that mean the account is not in the directory the sign-in used, as Microsoft's
    /// published list (learn.microsoft.com/entra/identity-platform/reference-error-codes) describes them:
    /// AADSTS50020 (UserUnauthorized: the account "does not exist in tenant"), AADSTS50034
    /// (UserAccountNotFound: the account "isn't in the tenant"), AADSTS51004 (UserAccountNotInDirectory:
    /// the account "doesn't exist in the directory", the app "likely chose the wrong tenant") and
    /// AADSTS90072 (PassThroughUserMfaError: the external account "doesn't exist on the tenant that they
    /// signed into"). AADSTS50177 (external
    /// challenge not supported for passthrough users) is about MFA, not the directory, and AADSTS500200 is
    /// not in the list; neither is here. Whole codes only: AADSTS50020 is a prefix of AADSTS500200.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex NotInDirectoryMarks =
        new(@"\bAADSTS(50020|50034|51004|90072)\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Whether Microsoft's refusal (<paramref name="error"/>, <paramref name="description"/>)
    /// is an account that is not in the directory the sign-in used.</summary>
    public static bool NotInDirectory(string? error, string? description) =>
        NotInDirectoryMarks.IsMatch(error ?? "") || NotInDirectoryMarks.IsMatch(description ?? "");

    /// <summary>A GUID as Entra writes one: 8-4-4-4-12 hex digits.</summary>
    public static bool IsGuid(string? text) => Guid.TryParseExact(text?.Trim(), "D", out _);

    /// <summary>The OpenID Connect scopes that are not an API's: granted with an ID token or a
    /// refresh token, and often left out of a token response's <c>scope</c>.</summary>
    public static readonly IReadOnlySet<string> IdentityScopes =
        new HashSet<string>(StringComparer.Ordinal) { "openid", "email", "profile", "offline_access" };

    public static IReadOnlyList<string> BuiltIns { get; } = [Google, Microsoft];

    /// <summary>Every provider word a plugin manifest's <c>connections.&lt;slot&gt;.providers</c> may
    /// name, besides <c>custom-&lt;id&gt;</c>. The console's provider wording table has words for each.</summary>
    public static IReadOnlyList<string> ManifestProviders { get; } = [Google, Microsoft, Custom, MailboxSettings.Kind, ConnectionKinds.Login, ConnectionKinds.ApiKey];

    /// <summary>The manifest words for a connection with no OAuth sign-in, so no scopes: a mailbox, a
    /// username and password, an API key.</summary>
    public static bool HasNoScopes(string provider) => provider == MailboxSettings.Kind || ConnectionKinds.IsCredential(provider);

    /// <summary>
    /// THE HOST'S PROVIDER WORDS: each provider a manifest may name, as a person says it - "Google" in
    /// "a Google account". Word for word the console's <c>PROVIDER_WORDS</c> names
    /// (<c>web/src/lib/marketplace.ts</c>); a test compares the two.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Words { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Google] = "Google",
        [Microsoft] = "Microsoft",
        [MailboxSettings.Kind] = "mailbox by app password",
        [Custom] = "custom sign-in",
        [ConnectionKinds.Login] = "username and password",
        [ConnectionKinds.ApiKey] = "API key",
    };

    /// <summary>
    /// A provider as a person says it, with its article: "a Google account", "a mailbox by app
    /// password", a <c>custom-&lt;id&gt;</c> by the name <paramref name="named"/> knows for it ("an Acme
    /// account"), and the id itself only when nothing better is known.
    /// </summary>
    public static string Spoken(string id, Func<string, string?>? named = null)
    {
        if (id is MailboxSettings.Kind or ConnectionKinds.Login) return $"a {Words[id]}";
        if (id == ConnectionKinds.ApiKey) return $"an {Words[id]}";

        var name = Words.TryGetValue(id, out var words) ? words
            : IsCustomId(id) && named?.Invoke(id) is { } own && !string.IsNullOrWhiteSpace(own) ? own.Trim()
            : null;
        if (name is null) return string.IsNullOrWhiteSpace(id) ? "an unnamed provider" : id;

        return $"{("aeiouAEIOU".Contains(name[0]) ? "an" : "a")} {name} account";
    }

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

    public static string KindOf(string id) => IsBuiltIn(id) || id == MailboxSettings.Kind || ConnectionKinds.IsCredential(id) ? id : Custom;

    /// <summary>A person-facing name for a provider id or manifest provider word.</summary>
    public static string Display(string id) => id switch
    {
        Google => "Google",
        Microsoft => "Microsoft",
        Custom => "custom",
        MailboxSettings.Kind => "mailbox (IMAP)",
        ConnectionKinds.Login => "username and password",
        ConnectionKinds.ApiKey => "API key",
        ConnectionKinds.Website => "website",
        _ => id,
    };

    /// <summary>
    /// The provider as the Host uses it: the build's endpoints under what a person stored. With no
    /// Microsoft client saved, <paramref name="microsoftDefaultClient"/> (the instance's, or null) is
    /// Microsoft's client: a public one, audience <c>common</c>, signed in with a code, with no secret.
    /// </summary>
    public static OAuthProvider Resolve(string id, StoredProvider? stored, string? microsoftDefaultClient = null)
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
                    + "consent screen \"In production\" or its refresh tokens expire after 7 days.",
                    ClientSource: string.IsNullOrWhiteSpace(stored?.ClientId) ? null : ClientSourceSaved);

            case Microsoft:
                var saved = !string.IsNullOrWhiteSpace(stored?.ClientId);
                var usesDefault = !saved && !string.IsNullOrWhiteSpace(microsoftDefaultClient);
                var tenant = usesDefault || string.IsNullOrWhiteSpace(stored?.Tenant) ? MicrosoftDefaultTenant : stored!.Tenant!;
                var authority = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0";
                return new OAuthProvider(
                    Microsoft, Microsoft, "Microsoft",
                    usesDefault ? microsoftDefaultClient!.Trim() : stored?.ClientId, !usesDefault && stored?.ClientSecretSet == true, tenant,
                    authority + "/authorize",
                    authority + "/token",
                    "https://graph.microsoft.com/oidc/userinfo",
                    // Microsoft has no refresh-token revocation endpoint.
                    null,
                    ["openid", "email", "offline_access"],
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    "Register an app in Microsoft Entra ID with public client flows allowed, and paste its Application "
                    + "(client) ID: you sign in with a code, and no secret or redirect URI is needed. Under Advanced, a "
                    + "\"Web\" app with a client secret and the redirect URIs shown here works as well.",
                    authority + "/devicecode",
                    saved ? ClientSourceSaved : usesDefault ? ClientSourceDefault : null);

            default:
                return new OAuthProvider(
                    id, Custom, string.IsNullOrWhiteSpace(stored?.Name) ? id : stored!.Name!,
                    stored?.ClientId, stored?.ClientSecretSet == true, null,
                    stored?.AuthorizeUrl ?? "", stored?.TokenUrl ?? "", stored?.UserinfoUrl, null,
                    stored?.DefaultScopes ?? [],
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    "Any OAuth 2.0 provider that supports the authorization code grant with PKCE. Register the "
                    + "redirect URI shown here with it.",
                    ClientSource: string.IsNullOrWhiteSpace(stored?.ClientId) ? null : ClientSourceSaved);
        }
    }

    /// <summary>The field a connection's directory is, as a person reads it.</summary>
    public const string DirectoryField = "Directory (tenant) ID or domain";

    /// <summary>What a value that is neither a GUID nor a domain is refused with: one sentence naming the field.</summary>
    public const string DirectoryRefusal =
        DirectoryField + " is a directory ID such as xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx or a domain such as example.onmicrosoft.com.";

    /// <summary>
    /// A connection's directory as stored: a tenant ID (a GUID, lowercased) or a domain (lowercased
    /// labels of letters, digits and hyphens, at least two of them). Null for anything else, including
    /// <c>common</c>, <c>organizations</c> and <c>consumers</c>, which are audiences, not directories.
    /// </summary>
    public static string? ParseDirectory(string? text)
    {
        var value = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value)) return null;
        if (IsGuid(value)) return value;

        var labels = value.Split('.');
        return value.Length <= 253 && labels.Length >= 2
            && labels.All(l => l.Length is > 0 and <= 63 && l[0] != '-' && l[^1] != '-' && l.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            && !labels[^1].All(char.IsAsciiDigit)
                ? value
                : null;
    }

    /// <summary>
    /// <paramref name="provider"/> as one connection uses it: a Microsoft connection with its own
    /// <paramref name="directory"/> signs in, redeems, refreshes and gets each API's token at that
    /// directory's endpoints, with the provider's client. With no directory, the provider unchanged.
    /// </summary>
    public static OAuthProvider InDirectory(OAuthProvider provider, string? directory)
    {
        if (directory is null || provider.Kind != Microsoft) return provider;

        var authority = $"https://login.microsoftonline.com/{Uri.EscapeDataString(directory)}/oauth2/v2.0";
        return provider with
        {
            Tenant = directory,
            AuthorizeUrl = authority + "/authorize",
            TokenUrl = authority + "/token",
            DeviceAuthorizationUrl = provider.DeviceAuthorizationUrl is null ? null : authority + "/devicecode",
        };
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
    /// <paramref name="providerId"/>: the same id (<c>imap</c> for a mailbox), or <c>custom</c> for
    /// every custom provider.
    /// </summary>
    public static bool Admits(string allowed, string providerId) =>
        allowed == providerId || (allowed == Custom && IsCustomId(providerId));
}
