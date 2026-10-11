namespace Harness.Host;

/// <summary>One Microsoft API a slot or a connection names, and the scopes it names of it, in
/// first-named order.</summary>
public sealed record MicrosoftApi(string Api, string Name, IReadOnlyList<string> Scopes);

/// <summary>
/// WHICH MICROSOFT API A SCOPE IS FOR. A Microsoft access token is for one API, so a connection
/// whose scopes span several gets one token per API, each redeemed from its one refresh token.
///
/// A scope's API is the part before its last path segment
/// (<c>https://management.azure.com/user_impersonation</c> is <c>https://management.azure.com/</c>);
/// a bare scope (<c>Mail.Read</c>, <c>openid</c>, <c>offline_access</c>) is Microsoft Graph's. The
/// sign-in scopes (<c>openid</c>, <c>email</c>, <c>profile</c>, <c>offline_access</c>) go with any
/// API's request, so Graph is a token of its own only when it is named for more than them, or when
/// nothing else is named.
/// </summary>
public static class MicrosoftApis
{
    public const string Graph = "https://graph.microsoft.com/";

    /// <summary>An API's name as a person reads it; an API this table does not know reads as itself.</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        [Graph] = "Microsoft Graph",
        ["https://management.azure.com/"] = "Azure management",
        ["https://management.core.windows.net/"] = "Azure management",
        ["https://database.windows.net/"] = "Azure SQL",
        ["https://storage.azure.com/"] = "Azure Storage",
        ["https://vault.azure.net/"] = "Key Vault",
        ["https://api.loganalytics.io/"] = "Log Analytics",
    };

    /// <summary>The API <paramref name="scope"/> is for. Azure SQL's resource is often written with a
    /// double slash (<c>https://database.windows.net//.default</c>); the API is the same one.</summary>
    public static string ApiOf(string scope)
    {
        var scheme = scope.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return Graph;

        var cut = scope.LastIndexOf('/');
        var api = cut < scheme + 3 ? scope + "/" : scope[..(cut + 1)];
        while (api.EndsWith("//", StringComparison.Ordinal) && !api.EndsWith("://", StringComparison.Ordinal)) api = api[..^1];

        return string.Equals(api, Graph, StringComparison.OrdinalIgnoreCase) ? Graph : api;
    }

    public static string NameOf(string api) => Names.GetValueOrDefault(api) ?? api;

    /// <summary>"Azure SQL (https://database.windows.net/)", as a sentence names an API.</summary>
    public static string Named(string api) => Names.TryGetValue(api, out var name) ? $"{name} ({api})" : api;

    /// <summary>Every API <paramref name="scopes"/> name, each with its scopes, in first-named order,
    /// the sign-in scopes under Graph: what a person reads.</summary>
    public static IReadOnlyList<MicrosoftApi> Group(IEnumerable<string> scopes)
    {
        var groups = new List<(string Api, List<string> Scopes)>();

        foreach (var scope in scopes)
        {
            var api = ApiOf(scope);
            var found = groups.FindIndex(g => g.Api == api);
            if (found < 0) groups.Add((api, [scope]));
            else if (!groups[found].Scopes.Contains(scope, StringComparer.Ordinal)) groups[found].Scopes.Add(scope);
        }

        return [.. groups.Select(g => new MicrosoftApi(g.Api, NameOf(g.Api), g.Scopes))];
    }

    /// <summary>The APIs <paramref name="scopes"/> need a token for, in first-named order. Graph is one
    /// only when a scope other than a sign-in scope is Graph's, or when no other API is named.</summary>
    public static IReadOnlyList<MicrosoftApi> Tokens(IEnumerable<string> scopes)
    {
        var groups = Group(scopes);
        var apis = groups.Where(g => g.Scopes.Any(s => !ConnectionProviders.IdentityScopes.Contains(s))).ToList();
        return apis.Count > 0 ? apis : [.. groups.Take(1).DefaultIfEmpty(new MicrosoftApi(Graph, NameOf(Graph), []))];
    }

    /// <summary>Whether <paramref name="scopes"/> need tokens for more than one API.</summary>
    public static bool Several(IEnumerable<string> scopes) => Tokens(scopes).Count > 1;
}
