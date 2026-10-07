namespace Harness.Host;

/// <summary>One installed slot that admits a provider: the scopes it asks of a connection of it.</summary>
public sealed record SlotNeed(string Plugin, string Slot, string? Description, IReadOnlyList<string> Scopes);

/// <summary>One scope the merged needs ask for, the plugins that want it, and its words or null.</summary>
public sealed record ScopeNeed(string Scope, string? Words, IReadOnlyList<string> Plugins);

/// <summary>A Google API a scope needs turned on in the person's project, and where to turn it on.</summary>
public sealed record ApiNeed(string Api, string Name, string Link);

/// <summary>
/// WHAT A CONNECTION SHOULD ASK FOR, answered from the installed plugins' connection slots and never
/// typed by a person: one slot's scopes for a provider, or every slot that admits it, merged.
///
/// Scopes are put in words where this table knows them; a scope it does not know reads as itself,
/// and nothing is guessed. Google scopes also say which API must be turned on in the person's project.
/// </summary>
public static class ConnectionNeeds
{
    /// <summary>The console page that turns on one API; <c>{projectId}</c> is the person's project,
    /// or empty.</summary>
    public const string ApiLinkTemplate = "https://console.cloud.google.com/apis/library/{0}?project={{projectId}}";

    /// <summary>The console flow that turns on several APIs at once.</summary>
    public const string ApisLinkTemplate = "https://console.cloud.google.com/flows/enableapi?apiid={0}&project={{projectId}}";

    private const string GoogleAuth = "https://www.googleapis.com/auth/";

    private const string Graph = "https://graph.microsoft.com/";

    /// <summary>Scope to the words a person reads. Graph scopes are matched with or without their
    /// <c>https://graph.microsoft.com/</c> prefix, in any case.</summary>
    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        ["https://mail.google.com/"] = "Read, change, send and permanently delete all your Gmail",
        [GoogleAuth + "gmail.modify"] = "Read, change and send your Gmail",
        [GoogleAuth + "gmail.readonly"] = "Read your Gmail",
        [GoogleAuth + "gmail.send"] = "Send email as you",
        [GoogleAuth + "gmail.compose"] = "Draft and send email as you",
        [GoogleAuth + "gmail.labels"] = "See and change your Gmail labels",
        [GoogleAuth + "drive"] = "See, change and delete all your Google Drive files",
        [GoogleAuth + "drive.readonly"] = "See all your Google Drive files",
        [GoogleAuth + "drive.file"] = "See and change only the Drive files it opens or creates",
        [GoogleAuth + "calendar"] = "See, change and delete all your Google calendars",
        [GoogleAuth + "calendar.readonly"] = "See your Google calendars",
        [GoogleAuth + "calendar.events"] = "See and change events on your Google calendars",
        [GoogleAuth + "calendar.events.readonly"] = "See events on your Google calendars",
        [GoogleAuth + "spreadsheets"] = "See, change and delete your Google Sheets",
        [GoogleAuth + "spreadsheets.readonly"] = "See your Google Sheets",
        [GoogleAuth + "documents"] = "See, change and delete your Google Docs",
        [GoogleAuth + "documents.readonly"] = "See your Google Docs",
        [GoogleAuth + "contacts"] = "See and change your contacts",
        [GoogleAuth + "contacts.readonly"] = "See your contacts",
        [GoogleAuth + "userinfo.email"] = "See your email address",
        [GoogleAuth + "userinfo.profile"] = "See your name and profile picture",
        ["openid"] = "Confirm who you are",
        ["email"] = "See your email address",
        ["profile"] = "See your name and profile picture",
        ["offline_access"] = "Stay connected when you are not using it",
    };

    private static readonly Dictionary<string, string> GraphWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mail.Read"] = "Read your mail",
        ["Mail.ReadWrite"] = "Read and change your mail",
        ["Mail.Send"] = "Send mail as you",
        ["Calendars.Read"] = "Read your calendars",
        ["Calendars.ReadWrite"] = "Read and change your calendars",
        ["Files.Read"] = "Read your files",
        ["Files.Read.All"] = "Read all files you can open",
        ["Files.ReadWrite"] = "Read and change your files",
        ["Files.ReadWrite.All"] = "Read and change all files you can open",
        ["User.Read"] = "Sign you in and read your profile",
    };

    /// <summary>Google scope (after <c>https://www.googleapis.com/auth/</c>) prefix to the API it needs.</summary>
    private static readonly (string Prefix, string Api, string Name)[] GoogleApis =
    [
        ("gmail.", "gmail.googleapis.com", "Gmail API"),
        ("drive", "drive.googleapis.com", "Google Drive API"),
        ("calendar", "calendar-json.googleapis.com", "Google Calendar API"),
        ("spreadsheets", "sheets.googleapis.com", "Google Sheets API"),
        ("documents", "docs.googleapis.com", "Google Docs API"),
        ("contacts", "people.googleapis.com", "People API"),
        ("directory.readonly", "people.googleapis.com", "People API"),
        ("user.", "people.googleapis.com", "People API"),
    ];

    /// <summary>
    /// The slots of <paramref name="plugins"/> that admit <paramref name="provider"/>, each with what
    /// it asks of it: every one, or with <paramref name="plugin"/> and <paramref name="slot"/> only that one.
    /// </summary>
    public static IReadOnlyList<SlotNeed> For(IEnumerable<PluginManifest> plugins, string provider, string? plugin = null, string? slot = null) =>
    [
        .. plugins
            .Where(p => plugin is null || p.Id == plugin)
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .SelectMany(p => p.Connections
                .Where(s => (slot is null || s.Key == slot) && s.Value.Admits(provider))
                .OrderBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => new SlotNeed(
                    p.Id, s.Key, string.IsNullOrWhiteSpace(s.Value.Description) ? null : s.Value.Description,
                    s.Value.ScopesFor(provider)))),
    ];

    /// <summary>The scopes of <paramref name="needs"/>, once each in first-asked order, with the plugins
    /// that want each.</summary>
    public static IReadOnlyList<ScopeNeed> Merge(IEnumerable<SlotNeed> needs)
    {
        var merged = new List<(string Scope, List<string> Plugins)>();

        foreach (var need in needs)
        {
            foreach (var scope in need.Scopes)
            {
                var found = merged.FindIndex(m => m.Scope == scope);
                if (found < 0) merged.Add((scope, [need.Plugin]));
                else if (!merged[found].Plugins.Contains(need.Plugin)) merged[found].Plugins.Add(need.Plugin);
            }
        }

        return [.. merged.Select(m => new ScopeNeed(m.Scope, WordsFor(m.Scope), m.Plugins))];
    }

    /// <summary>The words for <paramref name="scope"/>, or null when this table has none.</summary>
    public static string? WordsFor(string scope)
    {
        if (Words.TryGetValue(scope, out var words)) return words;

        var bare = scope.StartsWith(Graph, StringComparison.OrdinalIgnoreCase) ? scope[Graph.Length..] : scope;
        return GraphWords.GetValueOrDefault(bare);
    }

    /// <summary>What <paramref name="scopes"/> let a plugin do, as a person reads it: each scope's words,
    /// once each in first-granted order - <c>email</c> and <c>userinfo.email</c> are one line - and a
    /// scope this table has no words for as itself.</summary>
    public static IReadOnlyList<string> Permissions(IEnumerable<string> scopes) =>
        [.. scopes.Select(scope => WordsFor(scope) ?? scope).Distinct(StringComparer.Ordinal)];

    /// <summary>The APIs <paramref name="scopes"/> need turned on at <paramref name="provider"/>, once
    /// each in first-needed order. Only Google has such a map; any other provider needs none.</summary>
    public static IReadOnlyList<ApiNeed> Apis(string provider, IEnumerable<string> scopes)
    {
        if (provider != ConnectionProviders.Google) return [];

        var apis = new List<ApiNeed>();

        foreach (var scope in scopes)
        {
            if (ApiFor(scope) is { } found && !apis.Any(a => a.Api == found.Api))
            {
                apis.Add(new ApiNeed(found.Api, found.Name, string.Format(ApiLinkTemplate, found.Api)));
            }
        }

        return apis;
    }

    private static (string Api, string Name)? ApiFor(string scope)
    {
        if (scope == "https://mail.google.com/") return ("gmail.googleapis.com", "Gmail API");
        if (!scope.StartsWith(GoogleAuth, StringComparison.Ordinal)) return null;

        var rest = scope[GoogleAuth.Length..];
        foreach (var (prefix, api, name) in GoogleApis)
        {
            if (rest.StartsWith(prefix, StringComparison.Ordinal)) return (api, name);
        }

        return null;
    }

    /// <summary>What <c>GET /api/connections/needs</c> answers.</summary>
    public static object View(string provider, IReadOnlyList<SlotNeed> needs)
    {
        var scopes = Merge(needs);

        return new
        {
            provider,
            needs = needs.Select(n => new { plugin = n.Plugin, slot = n.Slot, description = n.Description, scopes = n.Scopes }),
            scopes = scopes.Select(s => new { scope = s.Scope, words = s.Words, plugins = s.Plugins }),
            apis = Apis(provider, scopes.Select(s => s.Scope)).Select(a => new { api = a.Api, link = a.Link }),
        };
    }
}
