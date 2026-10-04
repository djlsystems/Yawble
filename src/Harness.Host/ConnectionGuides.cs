namespace Harness.Host;

/// <summary>A value a person copies into the provider's console.</summary>
public sealed record GuideCopy(string Label, string Value);

/// <summary>
/// One step of a provider's setup guide. <paramref name="Link"/> is a URL template that may hold
/// <c>{projectId}</c> (the person's Google project id, or empty), or null.
/// </summary>
public sealed record GuideStep(string Id, string Title, string Text, string? Link, IReadOnlyList<GuideCopy> Copy);

/// <summary>
/// THE SETUP GUIDE OF EACH BUILT-IN PROVIDER: how a person registers the OAuth client the Host signs
/// in with, in order, with the values to copy. Built for the scopes about to be asked and the
/// redirect URI of the address in use. A custom provider has none.
///
/// NO STEP CARRIES A SECRET. The client secret is pasted by the person and only ever stored; the
/// guide names where to find it, never what it is.
/// </summary>
public static class ConnectionGuides
{
    private const string Console = "https://console.cloud.google.com";

    /// <summary>The guide for <paramref name="provider"/>, or null for a custom provider.</summary>
    public static IReadOnlyList<GuideStep>? For(OAuthProvider provider, IReadOnlyList<string> scopes, string origin)
    {
        var redirectUri = origin.TrimEnd('/') + Connections.CallbackPath;
        var warning = RedirectUriWarning(origin);

        return provider.Kind switch
        {
            ConnectionProviders.Google => Google(scopes, redirectUri, warning),
            ConnectionProviders.Microsoft => Microsoft(scopes, redirectUri, warning),
            _ => null,
        };
    }

    private static IReadOnlyList<GuideStep> Google(IReadOnlyList<string> scopes, string redirectUri, string? warning)
    {
        var apis = ConnectionNeeds.Apis(ConnectionProviders.Google, scopes);

        return
        [
            new("project", "Create or pick a project",
                "In the Google Cloud console, create a project for this, or pick one you already have. "
                + "Type its project id here and the links below open in it.",
                Console + "/projectcreate", []),

            new("apis", "Turn on the APIs",
                apis.Count == 0
                    ? "The scopes asked need no API turned on; go on to the next step."
                    : "Turn on " + (apis.Count == 1 ? "the API" : "each API") + " the scopes need, in the same project.",
                apis.Count == 0 ? null : string.Format(ConnectionNeeds.ApisLinkTemplate, string.Join(",", apis.Select(a => a.Api))),
                [.. apis.Select(a => new GuideCopy(a.Name, a.Api))]),

            new("branding", "Branding and audience",
                "Give the app a name and a support email, set Audience to External, then press Publish app. "
                + "Left in Testing, Google ends the sign-in after 7 days. Signing in, Google shows "
                + "\"Google hasn't verified this app\": that is expected for your own app - choose Advanced, "
                + "then Go to the app's name.",
                Console + "/auth/branding?project={projectId}", []),

            new("data-access", "Data access",
                "Under Data access, add exactly these scopes.",
                Console + "/auth/scopes?project={projectId}",
                [.. scopes.Select(s => new GuideCopy(ConnectionNeeds.WordsFor(s) ?? s, s))]),

            new("client", "Create the client",
                Warned(warning, "Create an OAuth client of type Web application and add this redirect URI under "
                    + "Authorized redirect URIs, exactly as shown."),
                Console + "/auth/clients/create?project={projectId}",
                [new("Redirect URI", redirectUri)]),

            new("credentials", "Paste the client ID and secret",
                "Copy the new client's ID (it ends in .apps.googleusercontent.com) and its secret, and paste them "
                + "here. The secret is stored encrypted and never shown again.",
                null, []),
        ];
    }

    private static IReadOnlyList<GuideStep> Microsoft(IReadOnlyList<string> scopes, string redirectUri, string? warning) =>
    [
        new("register", "Register an app",
            "In Microsoft Entra ID, register a new application. Choose who may sign in: accounts in any "
            + "organization and personal Microsoft accounts work with the tenant common.",
            "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/CreateApplicationBlade", []),

        new("client", "Add the redirect URI",
            Warned(warning, "Under Authentication, add the Web platform with this redirect URI, exactly as shown. "
                + "Add http://localhost there too for the CLI's sign-in."),
            null,
            [new("Redirect URI", redirectUri), new("CLI redirect URI", "http://localhost")]),

        new("data-access", "API permissions",
            "Under API permissions, add these Microsoft Graph delegated permissions.",
            null,
            [.. scopes.Select(s => new GuideCopy(ConnectionNeeds.WordsFor(s) ?? s, s))]),

        new("secret", "Create a client secret",
            "Under Certificates & secrets, create a new client secret and copy its Value - Microsoft shows it only once.",
            null, []),

        new("credentials", "Paste the client ID and secret",
            "Paste the Application (client) ID, the Directory (tenant) ID or common, and the secret's Value here. "
            + "The secret is stored encrypted and never shown again.",
            null, []),
    ];

    private static string Warned(string? warning, string text) => warning is null ? text : warning + " " + text;

    /// <summary>
    /// WHY GOOGLE OR MICROSOFT WILL REFUSE THE REDIRECT URI AT <paramref name="origin"/>, or null.
    /// They never call it - they send the browser there - so it need not be reachable; what they
    /// check is its spelling: http only for localhost, no IP address but loopback, and an https name
    /// ending in a public top-level domain. The web's <c>redirectUriWarning</c> says the same.
    /// </summary>
    public static string? RedirectUriWarning(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var url)) return null;

        var host = url.Host.ToLowerInvariant();
        if (host is "localhost" or "127.0.0.1" or "[::1]" or "::1") return null;

        var port = url.IsDefaultPort ? "" : $":{url.Port}";
        var fix = $" Open this page at http://localhost{port} on the machine running the container, where the "
            + "providers accept http, and register that address instead; or connect from that machine with the operator CLI's `connect`.";

        if (url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return $"Google and Microsoft refuse a redirect URI on an IP address such as {host}.{fix}";
        }

        if (url.Scheme == Uri.UriSchemeHttp)
        {
            return $"Google and Microsoft accept a redirect URI over http only for localhost, and this address is {host}.{fix}";
        }

        if (!host.Contains('.') || host.EndsWith(".local") || host.EndsWith(".lan") || host.EndsWith(".internal")
            || host.EndsWith(".home") || host.EndsWith(".localdomain"))
        {
            return $"Google refuses a redirect URI whose name does not end in a public top-level domain, such as {host}.{fix}";
        }

        return null;
    }

    /// <summary>What a provider view carries as <c>guide</c>: <c>{ steps }</c>, or null.</summary>
    public static object? View(IReadOnlyList<GuideStep>? steps) => steps is null ? null : new
    {
        steps = steps.Select(s => new
        {
            id = s.Id,
            title = s.Title,
            text = s.Text,
            link = s.Link,
            copy = s.Copy.Select(c => new { label = c.Label, value = c.Value }),
        }),
    };
}
