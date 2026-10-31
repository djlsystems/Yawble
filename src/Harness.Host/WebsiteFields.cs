using System.Text.RegularExpressions;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A WEBSITE CONNECTION'S FIELDS (CON 6): what the platform's own browser signs in to a site with, and
/// the page that shows it is signed in. The same columns, words and rules as the Web Credentials
/// plugin's entries table, so an entry carried over (<see cref="WebCredentialsCarryOver"/>) is the same
/// connection: a Site URL and the other hosts the browser may go to; a method (<c>password</c> fills
/// the sign-in form, with a two-step code from an optional TOTP secret; <c>token</c> sends a header;
/// <c>interactive</c> uses the session a person keeps with Sign in); the signed-in check; under
/// Advanced, the field selectors and the browser's language and time zone; and May act on pages, off
/// unless a person turns it on.
/// </summary>
/// <remarks>
/// Site URL, Sign-in URL and check URL are <c>url</c> fields: a bare address (<c>mysite.com/login</c>)
/// is given <c>https://</c>, a path (<c>/login</c>) stays a path on the site. Other hosts stay as
/// written. The saved session is masked and read-only: Sign in keeps it, and no form or agent types it.
/// </remarks>
public static partial class WebsiteFields
{
    public const string SiteUrl = "siteUrl";
    public const string Hosts = "hosts";
    public const string Method = "method";
    public const string SignInUrl = "signInUrl";
    public const string Username = "username";
    public const string Password = "password";
    public const string TotpSecret = "totpSecret";
    public const string Token = "token";
    public const string TokenHeader = "tokenHeader";
    public const string CheckUrl = "checkUrl";
    public const string CheckText = "checkText";
    public const string UsernameSelector = "usernameSelector";
    public const string PasswordSelector = "passwordSelector";
    public const string SubmitSelector = "submitSelector";
    public const string TotpSelector = "totpSelector";
    public const string Languages = "languages";
    public const string TimeZone = "timeZone";
    public const string MayAct = "mayAct";
    public const string Session = "session";

    public const string PasswordMethod = "password";
    public const string TokenMethod = "token";
    public const string InteractiveMethod = "interactive";

    /// <summary>The methods, as Web Credentials names them.</summary>
    public static readonly IReadOnlyList<string> Methods = [PasswordMethod, TokenMethod, InteractiveMethod];

    /// <summary>The longest saved session kept: Playwright's state for a few hosts is tens of kilobytes.</summary>
    public const int MaxSessionLength = PluginSignInRows.MaxSessionLength;

    /// <summary>What a person reads beside May act on pages, and what a refused act command names.</summary>
    public const string MayActLabel = "May act on pages";

    public static IReadOnlyList<CredentialFieldSpec> All { get; } =
    [
        new(SiteUrl, "Site URL", false, true, CredentialFieldSpec.Url,
            Help: "The origin the connection may sign in to, such as https://app.example.com. An address without http:// or https:// is given https://."),
        new(Hosts, "Other hosts", false, false,
            Help: "Other hosts the browser may go to for this site (pages and frames), such as www.example.com, separated by commas or spaces. Images, scripts and fetches from other hosts still load."),
        new(Method, "Method", false, true, CredentialFieldSpec.Enum, Methods,
            Help: "password fills the sign-in form; token sends a header; interactive uses the session you keep with Sign in."),
        new(SignInUrl, "Sign-in URL", false, false, CredentialFieldSpec.Url,
            Help: "For password: the sign-in page, a path on the site (/login) or a full URL on its hosts. Default: the Site URL."),
        new(Username, "Username", false, false, Help: "For password: the username or email address."),
        new(Password, "Password", true, false, Help: "For password: the password or app password. Never shown again."),
        new(TotpSecret, "TOTP secret", true, false, Help: "Optional, for password: the two-step secret (base32), so the browser can type the code."),
        new(Token, "Token", true, false, Help: "For token: the token. Never shown again."),
        new(TokenHeader, "Token header", false, false, Default: "Authorization",
            Help: "For token: the header name. Authorization sends Bearer <token>; any other header sends the token as it is."),
        new(CheckUrl, "Signed-in check URL", false, false, CredentialFieldSpec.Url,
            Help: "A page only a signed-in user can see, such as /dashboard. Default: the Site URL."),
        new(CheckText, "Signed-in check text", false, false,
            Help: "Optional: text that must appear on the check page, or a CSS selector written css=<selector>."),
        new(UsernameSelector, "Username field selector", false, false, Advanced: true,
            Help: "Optional, for password: a CSS selector when the username field is not found on its own."),
        new(PasswordSelector, "Password field selector", false, false, Advanced: true, Help: "Optional, for password: a CSS selector for the password field."),
        new(SubmitSelector, "Submit button selector", false, false, Advanced: true, Help: "Optional, for password: a CSS selector for the sign-in button."),
        new(TotpSelector, "Code field selector", false, false, Advanced: true, Help: "Optional, for password: a CSS selector for the two-step code field."),
        new(Languages, "Language", false, false, Advanced: true,
            Help: "The languages the browser says it reads, most preferred first, such as en-GB, en. A session kept with Sign in uses the languages of the browser that signed in. Default: en-US."),
        new(TimeZone, "Time zone", false, false, Advanced: true,
            Help: "Your time zone, such as Europe/London, for the browser. A session kept with Sign in uses the time zone of the browser that signed in. Default: UTC."),
        new(MayAct, MayActLabel, false, false, CredentialFieldSpec.Bool, Default: "false",
            Help: "Off: a team member may open pages signed in and read them, never click, fill or submit. On: it may act on the site's pages too, on its hosts only."),
        new(Session, "Saved session", true, false, ReadOnly: true,
            Help: "Sign in keeps it: open Sign in on this connection and sign in yourself. Never shown."),
    ];

    /// <summary>The fields kept as masked values, the saved session among them.</summary>
    public static readonly IReadOnlyList<string> Secrets = [Password, TotpSecret, Token, Session];

    private static readonly Regex BareHost = BareHostPattern();

    /// <summary>
    /// A Site URL, Sign-in URL or check URL written as a bare address (<c>mysite.com</c>,
    /// <c>www.mysite.com/login</c>) with <c>https://</c> before it; anything else exactly as written - any
    /// scheme (http stays http), a path (<c>/login</c>, <c>dashboard</c>) and text that is not an address.
    /// </summary>
    public static string WithScheme(string? value)
    {
        var raw = value?.Trim() ?? "";
        if (raw.Length == 0) return raw;
        if (SchemeStart().IsMatch(raw) && !HostAndPort().IsMatch(raw)) return raw;
        var host = raw.Split(['/', '?', '#'])[0];
        return BareHost.IsMatch(host) ? "https://" + raw : raw;
    }

    /// <summary>A language field's tags, each in its canonical case (en-gb as en-GB), joined by ", ".</summary>
    public static string CanonicalLanguages(string value) =>
        string.Join(", ", LanguageTags.CanonicalList(value.Split([',', ' ', ';', '\t'], StringSplitOptions.RemoveEmptyEntries)));

    /// <summary>Why a website field's value is refused, naming the field; null when it is fine.</summary>
    public static string? Refusal(CredentialFieldSpec spec, string value)
    {
        switch (spec.Name)
        {
            case SiteUrl when !(Uri.TryCreate(value, UriKind.Absolute, out var site) && site.Scheme is "http" or "https"):
                return $"`{SiteUrl}` (Site URL) is a web address, such as https://app.example.com.";
            case SignInUrl or CheckUrl when SchemeStart().IsMatch(value) && !(Uri.TryCreate(value, UriKind.Absolute, out var page) && page.Scheme is "http" or "https"):
                return $"`{spec.Name}` ({spec.Label}) is a path on the site, such as /login, or a web address on its hosts.";
            case TokenHeader when !HeaderName().IsMatch(value):
                return $"`{TokenHeader}` (Token header) is a header name, such as Authorization or X-Api-Key.";
            case Languages when CanonicalLanguages(value).Length == 0:
                return $"`{Languages}` (Language) is one or more language tags, such as en-GB, en.";
            case TimeZone when !ZoneName().IsMatch(value) || value.Contains("..", StringComparison.Ordinal):
                return $"`{TimeZone}` (Time zone) is a time zone name, such as Europe/London or UTC.";
            default:
                return null;
        }
    }

    /// <summary>What a website's account reads as: its site's host.</summary>
    public static string AccountOf(CredentialSettings settings) =>
        Uri.TryCreate(Value(settings, SiteUrl), UriKind.Absolute, out var site) ? site.Host : "";

    /// <summary>A field's unmasked value as stored, or null.</summary>
    public static string? Value(CredentialSettings settings, string name) =>
        settings.Fields.FirstOrDefault(f => f.Name == name && !f.Masked && f.Set)?.Value;

    /// <summary>Whether May act on pages is on: a person turned it on; it is off by default.</summary>
    public static bool MayActOn(CredentialSettings settings) => Value(settings, MayAct) == "true";

    /// <summary>
    /// The hosts a website may use: its Site URL's, then each of Other hosts (names or URLs, separated by
    /// commas or spaces), lower case, without port, once each. None for a Site URL that is not http(s).
    /// </summary>
    public static IReadOnlyList<string> HostsOf(string? siteUrl, string? hosts)
    {
        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var site) || site.Scheme is not ("http" or "https")) return [];

        var output = new List<string>();
        void Add(string host)
        {
            var name = host.ToLowerInvariant().TrimEnd('.');
            if (name.Length > 0 && !output.Contains(name)) output.Add(name);
        }

        Add(site.Host);
        foreach (var part in (hosts ?? "").Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (SchemeSlashes().IsMatch(part))
            {
                if (Uri.TryCreate(part, UriKind.Absolute, out var url)) Add(url.Host);
            }
            else
            {
                Add(Port().Replace(PortAndPath().Replace(part, ""), ""));
            }
        }

        return output;
    }

    /// <summary>What a sentence names a website's sign-in by.</summary>
    public static string MethodOf(CredentialSettings settings) => Value(settings, Method) ?? "";

    [GeneratedRegex(@"^(?:(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?\.)+(?:\p{L}{2,63}|xn--[A-Za-z0-9-]{1,59})|localhost|(?:[0-9]{1,3}\.){3}[0-9]{1,3})\.?(?::[0-9]{1,5})?$", RegexOptions.IgnoreCase)]
    private static partial Regex BareHostPattern();

    [GeneratedRegex("^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeStart();

    [GeneratedRegex("^[^:/?#]+:[0-9]+(?:[/?#]|$)")]
    private static partial Regex HostAndPort();

    [GeneratedRegex("^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeSlashes();

    [GeneratedRegex("(/.*$)")]
    private static partial Regex PortAndPath();

    [GeneratedRegex(":[0-9]+$")]
    private static partial Regex Port();

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,64}$")]
    private static partial Regex HeaderName();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+)*$")]
    private static partial Regex ZoneName();
}
