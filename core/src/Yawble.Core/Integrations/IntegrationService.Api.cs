using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Ipc;

namespace Yawble.Core.Integrations;

/// <summary>
/// APIs as integrations, the setup half: the person adds a web service by its URL (and docs URL), Discover reads
/// its OpenAPI spec into the checklist, and a shared Connection signs it in. The agents' half: one tool per API offered to a team, each
/// call decided by the rules on its exact action id and sent from this computer with the Connection's secret, which agents never see.
/// </summary>
public sealed partial class IntegrationService
{
    private sealed record ApiRecord(long Id, string Name, string BaseUrl, string? DocsUrl, string? SpecUrl, string? Title, long? ConnectionId,
        JsonObject? Auth, string? Status, bool EnabledAll, long Runs, string Kind = "http");

    private static bool IsApiMethod(string method) =>
        method.StartsWith("integrations.api", StringComparison.Ordinal) || method.StartsWith("integrations.connection", StringComparison.Ordinal) || IsWebsiteMethod(method) || IsOAuthMethod(method);

    private async Task<JsonNode?> DispatchApiAsync(string method, JsonObject? p, CancellationToken ct) => method switch
    {
        "integrations.connections" => new JsonArray(Conns.List().Select(ConnectionJson).ToArray()),
        "integrations.connectionAttention" => ConnectionAttentionJson(),
        "integrations.connectionSave" => await ConnectionSaveAsync(p, ct),
        "integrations.websiteSignIn" => await WebsiteSignInAsync(OptLong(p, "connectionId") ?? throw new RpcException("bad_params", "which connection?"), ct),
        "integrations.websiteSignInDone" => await WebsiteSignInDoneAsync(OptLong(p, "connectionId") ?? throw new RpcException("bad_params", "which connection?"), ct),
        "integrations.connectionRemove" => await ConnectionRemoveAsync(OptLong(p, "id") ?? throw new RpcException("bad_params", "which connection?"), p?["confirm"] is JsonValue c && c.TryGetValue<bool>(out var yes) && yes),
        "integrations.connectionTest" => await ConnectionTestAsync(OptLong(p, "id") ?? throw new RpcException("bad_params", "which connection?"), ct),
        "integrations.apiList" => ApiList(),
        "integrations.oauthDiscover" => await OAuthDiscoverAsync(p, ct),
        "integrations.oauthSignIn" => await OAuthSignInAsync(p, ct),
        "integrations.apiSave" => ApiSave(p),
        "integrations.apiDeleteRule" => ApiDeleteRule(OptLong(p, "id") ?? throw new RpcException("bad_params", "which rule?")),
        "integrations.apiEnable" => ApiEnable(Str(p, "name"), p?["all"]?.GetValue<bool>(), OptLong(p, "teamId"), p?["on"]?.GetValue<bool>() ?? true),
        "integrations.apiRemove" => await ApiRemoveAsync(Str(p, "name"), p?["removeConnection"] is JsonValue r && r.TryGetValue<bool>(out var rc) && rc),
        _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
    };

    // ------------------------------------------------------------------ connections

    /// <summary>A Connection as the UI sees it: secret fields only as their masked form.</summary>
    private static JsonNode ConnectionJson(ConnectionView v) => new JsonObject
    {
        ["id"] = v.Id, ["name"] = v.Name, ["kind"] = v.Kind,
        ["fields"] = new JsonArray(v.Fields.Select(f => (JsonNode?)new JsonObject { ["key"] = f.Key, ["label"] = f.Label, ["secret"] = f.Secret, ["shown"] = f.Shown, ["set"] = f.Set }).ToArray()),
        ["status"] = v.Status, ["statusReason"] = v.StatusReason, ["testedAt"] = v.TestedAt,
        ["usedBy"] = new JsonArray(v.UsedBy.Select(u => (JsonNode?)u).ToArray()),
    };

    private async Task<JsonNode> ConnectionSaveAsync(JsonObject? p, CancellationToken ct)
    {
        if (OptStr(p, "kind") == "website") return await WebsiteConnectionSaveAsync(p);
        // a browser sign-in is edited as one: its tokens are the flow's (kept, or dropped when the app or the addresses change)
        if (OptLong(p, "id") is { } eid && Conns.Get(eid) is { Kind: "oauth" }) return await OAuthConnectionSaveAsync(eid, p, ct);
        if (OptStr(p, "kind") == "oauth") throw new ArgumentException("an OAuth sign-in is made by signing in: use Sign in");
        var fields = new List<ConnectionField>();
        foreach (var n in p?["fields"] as JsonArray ?? throw new RpcException("bad_params", "send 'fields' (key, label, secret per field)"))
        {
            if (n is not JsonObject o) throw new ArgumentException("each field needs a key and a label");
            var key = OptStr(o, "key") ?? throw new ArgumentException("each field needs a 'key'");
            var label = OptStr(o, "label") ?? key;
            var secret = o["secret"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
            fields.Add(new ConnectionField(key, label, secret, OptStr(o, "value")));
        }
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (p?["secrets"] is JsonObject so)
            foreach (var (k, v) in so)
                if (v is JsonValue sv && sv.TryGetValue<string>(out var text)) secrets[k] = text;
        if (p?["reuseIdentical"] is JsonValue ru && ru.TryGetValue<bool>(out var reuse) && reuse && OptLong(p, "id") is null
            && Conns.FindIdentical(secrets, fields.Where(f => !f.Secret).Select(f => f.Value)) is { } same && Conns.Get(same) is { } existing)
        {
            var json = (JsonObject)ConnectionJson(existing);
            json["reused"] = true;
            return json;
        }
        return ConnectionJson(Conns.Save(OptLong(p, "id"), Str(p, "name"), fields, secrets));
    }

    private JsonNode ApiDeleteRule(long id)
    {
        Exec("DELETE FROM api_rules WHERE id = $id;", ("$id", id));
        return new JsonObject { ["ok"] = true };
    }

    private async Task<JsonNode> ConnectionRemoveAsync(long id, bool confirm)
    {
        var view = Conns.Get(id) ?? throw new ArgumentException("no such connection");
        if (view.UsedBy.Count > 0 && !confirm)
            return new JsonObject { ["ok"] = false, ["usedBy"] = new JsonArray(view.UsedBy.Select(u => (JsonNode?)u).ToArray()) };
        var partition = view.Kind == "website" ? WebsitePartition(id) : null;
        Conns.Remove(id); // the apis' connection_id goes NULL by the foreign key
        if (partition is not null) await ForgetWebsiteAsync(partition);
        return new JsonObject { ["ok"] = true };
    }

    // ------------------------------------------------------------------ APIs

    private List<ApiRecord> ApiRows(string? name = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, base_url, docs_url, spec_url, title, connection_id, auth, status, enabled_all, discover_runs, kind FROM apis " +
            "WHERE ($n IS NULL OR name = $n COLLATE NOCASE) ORDER BY name COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$n", (object?)name?.Trim() ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<ApiRecord>();
        while (r.Read())
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            JsonObject? auth = null;
            try { auth = S(7) is { } a ? JsonNode.Parse(a) as JsonObject : null; } catch (System.Text.Json.JsonException) { }
            list.Add(new ApiRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), S(3), S(4), S(5), r.IsDBNull(6) ? null : r.GetInt64(6), auth, S(8), r.GetInt64(9) == 1, r.GetInt64(10), r.GetString(11)));
        }
        return list;
    }

    private ApiRecord ApiRow(string name) => ApiRows(name).FirstOrDefault() ?? throw new ArgumentException($"no API named '{name}'");

    private IReadOnlyList<long> ApiTeamsOf(long apiId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT team_id FROM api_teams WHERE api_id = $a ORDER BY team_id;";
        cmd.Parameters.AddWithValue("$a", apiId);
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    private static string? AuthType(JsonObject? auth) => OptStr(auth, "type");

    /// <summary>What the person fills in to sign in, from the API's auth: bearer = Token; header / query = API key under the header or query name; basic = username + password.</summary>
    private static JsonNode? SignInJson(JsonObject? auth)
    {
        var type = AuthType(auth);
        (string Label, (string Key, string Label, bool Secret)[] Fields)? plan = type switch
        {
            "bearer" => ("Token", [("token", "Token", true)]),
            "header" or "query" => OptStr(auth, "name") is { } n ? ("API key", [(n, "API key", true)]) : ("API key", [("key", "API key", true)]),
            "basic" => ("Username and password", [("username", "Username", false), ("password", "Password", true)]),
            _ => null,
        };
        if (plan is null) return null;
        return new JsonObject
        {
            ["label"] = plan.Value.Label, ["description"] = OptStr(auth, "description"),
            ["fields"] = new JsonArray(plan.Value.Fields.Select(f => (JsonNode?)new JsonObject { ["key"] = f.Key, ["label"] = f.Label, ["secret"] = f.Secret }).ToArray()),
        };
    }

    private JsonNode ApiList() => new JsonArray(ApiRows().Select(a =>
    {
        // the name and status only: the list never reads a secret from the credential store
        var conn = a.ConnectionId is { } cid && connections?.Brief(cid) is { } cv
            ? new JsonObject { ["id"] = cv.Id, ["name"] = cv.Name, ["status"] = cv.Status, ["statusReason"] = a.Kind == "website" ? cv.StatusReason : null } : null;
        return (JsonNode?)new JsonObject
        {
            ["name"] = a.Name, ["kind"] = a.Kind, ["baseUrl"] = a.BaseUrl, ["docsUrl"] = a.DocsUrl, ["specUrl"] = a.SpecUrl, ["title"] = a.Title,
            ["connection"] = conn,
            ["auth"] = a.Auth is null ? null : new JsonObject
            {
                ["type"] = AuthType(a.Auth), ["name"] = OptStr(a.Auth, "name"), ["description"] = OptStr(a.Auth, "description"),
                ["chosen"] = a.Auth["chosen"] is JsonValue ch && ch.TryGetValue<bool>(out var chosen) && chosen,
            },
            ["signIn"] = SignInJson(a.Auth),
            ["oauth"] = a.Kind == "http" && AuthType(a.Auth) == "oauth2" ? OAuthApiJson(a) : null,
            ["status"] = a.Status, ["all"] = a.EnabledAll, ["teams"] = new JsonArray(ApiTeamsOf(a.Id).Select(t => (JsonNode?)t).ToArray()),
            ["counts"] = a.Runs == 0 ? null : CountsJson(() => Discover.GetApiChecklist(a.Name, null)),
        };
    }).ToArray());

    private static Uri? HttpUrl(string? text) =>
        text is { Length: > 0 } && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.Host.Length > 0 ? u : null;

    private JsonNode ApiSave(JsonObject? p)
    {
        var kind = OptStr(p, "kind");
        if (kind is not (null or "http" or "website")) throw new ArgumentException("kind is http or website");
        if (kind == "website") return WebsiteSave(p);
        var baseText = OptStr(p, "baseUrl")?.Trim();
        var docsText = OptStr(p, "docsUrl")?.Trim();
        var given = OptStr(p, "name")?.Trim();
        var baseUrl = baseText is null ? null : HttpUrl(baseText) ?? throw new ArgumentException("the service URL must be a full web address starting with http:// or https://");
        var docsUrl = docsText is null ? null : HttpUrl(docsText) ?? throw new ArgumentException("the documentation link must be a full web address starting with http:// or https://");

        var name = given is { Length: > 0 } ? given : baseUrl?.Host ?? docsUrl?.Host
            ?? throw new ArgumentException("give the service URL, or a documentation link to find it from");
        if (name.Length > 60) throw new ArgumentException("the name is too long (60 characters at most)");
        var existing = ApiRows(name).FirstOrDefault();
        if (existing is { Kind: "website" }) throw new ArgumentException($"{existing.Name} is a website, not a web API");
        // "Add API" (create: true) never edits an existing one, whether its name was typed or comes from the service's host
        if (existing is not null && p?["create"] is JsonValue cr && cr.TryGetValue<bool>(out var create) && create)
            throw new ArgumentException($"an API named {existing.Name} already exists; edit it instead");
        // docsUrl as connectionId and auth: present = set (null or blank clears), absent = unchanged on an update
        var docsStored = p is not null && p.ContainsKey("docsUrl") ? docsText : existing?.DocsUrl;

        // the service URL may be left blank only when a docs link can supply it (Discover then reads it from the spec)
        if (baseUrl is null && existing is not { BaseUrl.Length: > 0 } && HttpUrl(docsStored) is null)
            throw new ArgumentException("give the service URL, or a documentation link to find it from");

        var tool = ToolName(name);
        if ((ApiToolPrefix + tool).Contains("__", StringComparison.Ordinal))
            throw new ArgumentException($"'{name}' would give agents the tool name '{ApiToolPrefix}{tool}', and two underscores in a row are kept for MCP servers' tools: choose another name");
        if (ApiRows().FirstOrDefault(a => a.Kind == "http" && !string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase) && ToolName(a.Name) == tool) is { } clash)
            throw new ArgumentException($"'{name}' is too close to the existing API '{clash.Name}' (agents would see the same tool name 'api_{tool}'): choose another name");
        if (Rows().FirstOrDefault(r => ToolName(r.Name) == ApiToolPrefix + tool) is { } cli)
            throw new ArgumentException($"'{name}' would give agents the tool name '{ApiToolPrefix}{tool}', which the CLI integration '{cli.Name}' already has: choose another name");

        long? connectionId = existing?.ConnectionId;
        if (p is not null && p.ContainsKey("connectionId"))
        {
            connectionId = OptLong(p, "connectionId");
            if (connectionId is { } cid)
            {
                var picked = Conns.Get(cid) ?? throw new ArgumentException("no such connection");
                if (picked.Kind == "website") throw new ArgumentException("that connection is a website login: it signs in a website, not a web API");
            }
        }

        var auth = existing?.Auth;
        if (p is not null && p.ContainsKey("auth"))
            auth = p["auth"] is JsonObject ao ? CleanAuth(ao) : null;

        var now = Now();
        var url = baseText ?? existing?.BaseUrl ?? "";
        if (existing is null)
            Exec("INSERT INTO apis (name, base_url, docs_url, connection_id, auth, created_at, updated_at) VALUES ($n, $b, $d, $c, $a, $now, $now);",
                ("$n", name), ("$b", url), ("$d", docsStored), ("$c", connectionId), ("$a", auth?.ToJsonString()), ("$now", now));
        else
            Exec("UPDATE apis SET base_url = $b, docs_url = $d, connection_id = $c, auth = $a, updated_at = $now WHERE id = $id;",
                ("$b", url), ("$d", docsStored), ("$c", connectionId), ("$a", auth?.ToJsonString()), ("$now", now), ("$id", existing.Id));
        return new JsonObject { ["ok"] = true, ["name"] = ApiRow(name).Name };
    }

    private static JsonObject CleanAuth(JsonObject a)
    {
        var type = OptStr(a, "type");
        if (type is not ("bearer" or "header" or "query" or "basic")) throw new ArgumentException("auth type is bearer, header, query or basic");
        var name = OptStr(a, "name")?.Trim();
        if (type is "header" or "query" && string.IsNullOrEmpty(name)) throw new ArgumentException($"say which {type} name carries the key");
        // chosen: the person said how it signs in (the spec did not), so the dialog keeps offering the choice
        return new JsonObject { ["type"] = type, ["name"] = type is "header" or "query" ? name : null, ["description"] = OptStr(a, "description"), ["chosen"] = true };
    }

    private JsonNode ApiEnable(string name, bool? all, long? teamId, bool on)
    {
        var a = ApiRow(name);
        if (all is { } al) Exec("UPDATE apis SET enabled_all = $a, updated_at = $now WHERE id = $id;", ("$a", al ? 1 : 0), ("$now", Now()), ("$id", a.Id));
        if (teamId is { } t)
        {
            if (on && Scalar("SELECT count(*) FROM teams WHERE id = $t;", ("$t", t)) == 0) throw new ArgumentException($"no team {t}");
            Exec(on ? "INSERT OR IGNORE INTO api_teams (api_id, team_id) VALUES ($s, $t);" : "DELETE FROM api_teams WHERE api_id = $s AND team_id = $t;", ("$s", a.Id), ("$t", t));
        }
        return new JsonObject { ["ok"] = true };
    }

    private async Task<JsonNode> ApiRemoveAsync(string name, bool removeConnection)
    {
        var a = ApiRow(name);
        JsonObject? sole = null;
        if (a.ConnectionId is { } cid && Conns.Get(cid) is { } view && view.UsedBy.Count == 1 && string.Equals(view.UsedBy[0], a.Name, StringComparison.OrdinalIgnoreCase))
            sole = new JsonObject { ["id"] = view.Id, ["name"] = view.Name };
        RemoveWithPendingAsks("api:" + a.Name, "API removed", "DELETE FROM apis WHERE id = $id;", ("$id", a.Id)); // operations, offers, choices and rules go by cascade
        if (removeConnection && sole is not null)
        {
            var partition = Conns.Get(a.ConnectionId!.Value)?.Kind == "website" ? WebsitePartition(a.ConnectionId.Value) : null;
            Conns.Remove(a.ConnectionId!.Value);
            if (partition is not null) await ForgetWebsiteAsync(partition);
        }
        // a Website's own public session goes with it (a later row could get the same id), and so do its open pages, whichever session they use
        if (a.Kind == "website") await ForgetWebsiteAsync(PublicWebsitePartition(a.Id), pagesOf: a.Id + ":");
        return new JsonObject { ["ok"] = true, ["soleConnection"] = sole, ["connectionRemoved"] = removeConnection && sole is not null };
    }

    // ------------------------------------------------------------------ Discover

    /// <summary>
    /// The reader Discover runs for an API: finds the spec (docs URL, then the usual places on the service URL), reads it, and keeps its
    /// address, the service URL if the person left it blank, and a status line. A failure is kept as the status too.
    /// </summary>
    private async Task<ApiSpec> ReadApiSpecAsync(string name, CancellationToken ct)
    {
        var a = ApiRow(name);
        if (a.Kind == "website") return WebsiteSpec(a);
        try
        {
            var docs = HttpUrl(a.DocsUrl);
            var service = HttpUrl(a.BaseUrl) ?? docs ?? throw new ArgumentException("this API has no service URL or documentation link to look at");
            using var handler = NewSpecHandler();
            var (text, specUrl) = await new SpecFinder(handler).FindAsync(docs, service, ct);
            var spec = OpenApiReader.Read(text, specUrl);
            var fillBase = a.BaseUrl.Length == 0;
            var serverUrl = HttpUrl(spec.ServerUrl);
            if (fillBase && serverUrl is null)
                throw new ArgumentException("the spec does not say where the service is: add the service URL and try again");
            Exec("UPDATE apis SET spec_url = $s, base_url = CASE WHEN base_url = '' THEN $b ELSE base_url END, status = $st, updated_at = $now WHERE id = $id;",
                ("$s", specUrl.AbsoluteUri), ("$b", serverUrl?.AbsoluteUri.TrimEnd('/') ?? ""), ("$st", $"read {spec.Actions.Count} action{(spec.Actions.Count == 1 ? "" : "s")} from the OpenAPI spec"),
                ("$now", Now()), ("$id", a.Id));
            return spec;
        }
        catch (ArgumentException e)
        {
            Exec("UPDATE apis SET status = $st, updated_at = $now WHERE id = $id;", ("$st", "not working: " + Clip(e.Message, 300)), ("$now", Now()), ("$id", a.Id));
            throw;
        }
    }

    // ------------------------------------------------------------------ the agents' side

    /// <summary>An API's tool is "api_" + its name as a tool name (never containing "__", which is kept for MCP servers' tools).</summary>
    public const string ApiToolPrefix = "api_";
    private const int ApiListedActions = 60;
    /// <summary>At most this much of a response body is read (far beyond the 64 KB the agent sees, so a cut never splits a secret before it is scrubbed).</summary>
    private const int ApiMaxBodyBytes = 1024 * 1024;
    /// <summary>How long one API call may take (60 s; tests shorten it).</summary>
    internal TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Headers never sent from an agent's parameters: the sign-in, cookies, the connection and the body's framing are the app's.</summary>
    private static readonly HashSet<string> ApiReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Authorization", "Proxy-Authorization", "Cookie", "Content-Length", "Content-Type", "Transfer-Encoding", "Connection", "Expect", "Upgrade", "TE", "Keep-Alive",
    };

    private SocketsHttpHandler? _apiHandler;
    private readonly Lock _apiHandlerGate = new();

    /// <summary>
    /// The handler API calls go through: their own seam (<c>apiCallHandler</c>, tests only: FakeHttp), never the spec reader's factory.
    /// Production: one shared, long-lived handler owned by this service (disposed in DisposeAsync) that never follows a redirect itself (the
    /// call decides) and keeps no cookies between calls. Never created or disposed per call.
    /// </summary>
    private HttpMessageHandler ApiHandler()
    {
        if (apiCallHandler is not null) return apiCallHandler();
        lock (_apiHandlerGate)
            return _apiHandler ??= new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
    }

    /// <summary>One operation of an API as stored (api_commands): its id, HTTP method, route template, class and parameters schema.</summary>
    private sealed record ApiOp(string Id, string Method, string Route, string Summary, string Class, JsonObject Params, bool Gone, IReadOnlyList<string> Scopes);

    /// <summary>
    /// The sign-in the host adds to a request (a header, or a query parameter) and the secret texts in it, scrubbed from whatever comes back.
    /// From an oauth Connection: its id and the access token sent, for one refresh and retry after a 401.
    /// </summary>
    private sealed record ApiSign(bool InQuery, string Name, string Value, IReadOnlyList<string> Secrets, long? OAuthConnection = null, string? Token = null);

    /// <summary>A request built from an agent's parameters, before the sign-in is added. Its URL is on the service URL's origin and under its path.</summary>
    private sealed record ApiRequest(HttpMethod Method, Uri Url, IReadOnlyList<(string Name, string Value)> Headers, string? JsonBody, IReadOnlyList<KeyValuePair<string, string>>? Form);

    /// <summary>What a call returned: the agent's text, the record for the log, and the HTTP status and status line (null when nothing answered).</summary>
    private sealed record ApiResult(string Text, CliOutput? Output, int? Status, string? Head);

    private List<ApiOp> ApiOps(long apiId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, method, route, summary, class, params, gone, scopes FROM api_commands WHERE api_id = $a ORDER BY path;";
        cmd.Parameters.AddWithValue("$a", apiId);
        using var r = cmd.ExecuteReader();
        var list = new List<ApiOp>();
        while (r.Read())
        {
            JsonObject? schema = null;
            try { schema = JsonNode.Parse(r.GetString(5)) as JsonObject; } catch (JsonException) { }
            list.Add(new ApiOp(r.GetString(0), r.GetString(1).ToUpperInvariant(), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.GetString(4),
                schema ?? new JsonObject { ["type"] = "object" }, r.GetInt64(6) == 1,
                r.IsDBNull(7) ? [] : r.GetString(7).Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }
        return list;
    }

    /// <summary>The rules of one scope (a team, or null = every team) as the decision reads them.</summary>
    private List<CliRule> ApiRulesOf(long apiId, long? teamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, pattern, decision FROM api_rules WHERE api_id = $a AND team_id IS $t ORDER BY id;";
        cmd.Parameters.AddWithValue("$a", apiId);
        cmd.Parameters.AddWithValue("$t", (object?)teamId ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<CliRule>();
        while (r.Read()) list.Add(new CliRule(r.GetString(1), r.GetString(2), teamId is null ? "global" : "team", r.GetInt64(0)));
        return list;
    }

    /// <summary>A sign-in action is refused whatever the rules say; any other is decided on its exact id, the team's rules first.</summary>
    private static (string Decision, CliRule? Rule) ApiDecide(ApiOp op, IReadOnlyList<CliRule> team, IReadOnlyList<CliRule> global) =>
        op.Class == CommandClass.Credential ? (Decisions.Deny, new CliRule(op.Id, Decisions.Deny, "floor"))
        : CliRules.DecideExact(op.Id, team, global);

    private IEnumerable<ApiRecord> ApisOfferedTo(long teamId) => ApiRows().Where(a => a.Kind == "http" && (a.EnabledAll || ApiTeamsOf(a.Id).Contains(teamId)));

    private ApiRecord? ApiToolOf(long teamId, string tool) => ApisOfferedTo(teamId).FirstOrDefault(a => ApiToolPrefix + ToolName(a.Name) == tool);

    private bool IsApiTool(string name) => name.StartsWith(ApiToolPrefix, StringComparison.Ordinal) && ApiRows().Any(a => a.Kind == "http" && ApiToolPrefix + ToolName(a.Name) == name);

    private static string ApiLabel(ApiRecord a) =>
        a.Title is { Length: > 0 } t && !string.Equals(t.Trim(), a.Name, StringComparison.OrdinalIgnoreCase) ? $"{a.Name} ({Clip(t.Trim(), 80)})" : a.Name;

    /// <summary>One tool per API offered to the team that has a service URL and actions: the action list in its description, the call by action id.</summary>
    private IEnumerable<JsonObject> ApiToolsFor(long teamId)
    {
        foreach (var a in ApisOfferedTo(teamId).ToList())
        {
            if (HttpUrl(a.BaseUrl) is not { } service) continue;
            var ops = ApiOps(a.Id).Where(o => !o.Gone).ToList();
            if (ops.Count == 0) continue;
            var team = ApiRulesOf(a.Id, teamId);
            var global = ApiRulesOf(a.Id, null);
            var open = ops.Where(o => ApiDecide(o, team, global).Decision != Decisions.Deny).ToList();
            var sb = new StringBuilder();
            sb.Append($"Call {ApiLabel(a)}, the person's web API at {service.Host}, under the person's rules: some actions run at once, some ask the person first ")
              .Append($"(give {ReasonProperty}; you are woken with the result), some are never allowed. The app signs each request in on the person's computer: ")
              .Append("you never get the credentials, and you do not need them. Give an action id and its params by name (path, query and header parameters; ")
              .Append("a request body as params.body). action \"list\" (params.filter) lists the actions with their ids; action \"describe\" (params.action) ")
              .Append("shows an action's parameters. The result is the HTTP status, the content type and the body.\n\nActions:\n");
            foreach (var o in open.Take(ApiListedActions))
                sb.Append("- ").Append(o.Id).Append(" — ").Append(Clip(o.Summary.Length > 0 ? o.Summary : $"{o.Method} {o.Route}", 120)).Append('\n');
            if (open.Count > ApiListedActions) sb.Append($"... {open.Count - ApiListedActions} more: use action list.\n");
            if (open.Count == 0) sb.Append("(none is allowed for your team yet)\n");
            yield return new JsonObject
            {
                ["name"] = ApiToolPrefix + ToolName(a.Name),
                ["description"] = sb.ToString().TrimEnd(),
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["action"] = new JsonObject { ["type"] = "string", ["description"] = "The action id (from the list, or see action \"list\"), or \"list\" / \"describe\"." },
                        ["params"] = new JsonObject { ["type"] = "object", ["description"] = "The action's parameters by name, the request body as \"body\". For list: {\"filter\": \"text\"}. For describe: {\"action\": \"id\"}." },
                        [ReasonProperty] = new JsonObject { ["type"] = "string", ["description"] = "Why you need this call, in one line (shown to the person if it asks them)." },
                    },
                    ["required"] = new JsonArray("action"),
                    ["additionalProperties"] = false,
                },
            };
        }
    }

    /// <summary>An agent's call of an API tool: list and describe (always allowed, not logged), or an action decided by the rules and sent from here.</summary>
    private async Task<string> CallApiAsync(RunContext run, ApiRecord api, JsonObject args, CancellationToken ct)
    {
        var action = args["action"] is JsonValue av && av.TryGetValue<string>(out var at) && at.Trim().Length > 0 ? at.Trim()
            : throw new ArgumentException("give an action id (action \"list\" shows them)");
        var prms = args["params"] switch
        {
            null => new JsonObject(),
            JsonObject o => (JsonObject)o.DeepClone(),
            _ => throw new ArgumentException("params is an object of parameters by name, e.g. {\"petId\": \"7\"}"),
        };
        var reason = args[ReasonProperty] is JsonValue rv && rv.TryGetValue<string>(out var rs) && !string.IsNullOrWhiteSpace(rs) ? rs.Trim() : null;
        var ops = ApiOps(api.Id);
        var team = ApiRulesOf(api.Id, run.Member.TeamId);
        var global = ApiRulesOf(api.Id, null);
        if (action == "list") return ApiListText(api, ops, team, global, OptStr(prms, "filter"));
        if (action == "describe") return ApiDescribeText(api, ops, OptStr(prms, "action"));

        var op = ops.FirstOrDefault(o => o.Id == action && !o.Gone);
        if (op is null) return $"refused: no action '{Clip(SecretScrubber.Scrub(action), 100)}'; use action list";
        var integration = "api:" + api.Name;
        string[] callArgs = [op.Id, SecretScrubber.Scrub(prms.ToJsonString())];
        var (decision, rule) = ApiDecide(op, team, global);
        void Record(string d, string? r, CliOutput? output) =>
            Insert(integration, run.Member.Team, run.Member.Qualified, run.Correlation, callArgs, d, r, output, Provenance.Actor.Current);
        var ruleText = rule is null ? null : $"{rule.Layer}: {rule.Pattern}";
        if (decision == Decisions.Deny)
        {
            Record(Decisions.Deny, ruleText, null);
            return $"refused: {api.Name}'s {op.Id} is not allowed for your team{(rule?.Layer == "floor" ? " (it reveals or changes a sign-in)" : "")}. Do not try to work around it.";
        }
        // checked before anyone is asked: a request that cannot be sent is refused now
        ApiRequest request;
        try { request = PlanApiRequest(api, op, prms); }
        catch (ArgumentException e) { return "refused: " + ApiScrub(e.Message, null); }
        var (sign, view, refusal, refusedFor) = await ResolveSignInAsync(api, op, ct);
        if (refusal is not null)
        {
            Record(Decisions.Deny, refusedFor, null);
            return refusal;
        }
        if (decision == Decisions.Ask)
        {
            if (reason is null) throw new ArgumentException($"this call asks the person first: give a reason ({ReasonProperty}, one line)");
            var id = Scalar(
                """
                INSERT INTO integration_asks (at, integration, team_id, member, args, reason, workflow, causation_seq, action)
                VALUES ($at, $i, $t, $m, $a, $r, $w, $c, $action) RETURNING id;
                """,
                ("$at", Now()), ("$i", integration), ("$t", run.Member.TeamId), ("$m", run.Member.Qualified), ("$a", JsonSerializer.Serialize(callArgs)),
                ("$r", Clip(SecretScrubber.Scrub(reason), 500)), ("$w", run.Correlation), ("$c", run.Causation), ("$action", op.Id));
            Record("asked", ruleText, null);
            return $"asked: {api.Name}'s {op.Id} waits for the person's approval (request {id}). You will be woken with the result in this workflow. Do other work or end your turn; do not ask again.";
        }
        var result = await SendApiAsync(api, request, sign, view, run.Member.TeamId, ct);
        Record(Decisions.Allow, ruleText, result.Output);
        return result.Text;
    }

    private static string ApiListText(ApiRecord api, IReadOnlyList<ApiOp> ops, IReadOnlyList<CliRule> team, IReadOnlyList<CliRule> global, string? filter)
    {
        const int Cap = 400;
        var f = filter?.Trim() ?? "";
        var hits = ops.Where(o => !o.Gone && (f.Length == 0 || o.Id.Contains(f, StringComparison.OrdinalIgnoreCase)
            || o.Summary.Contains(f, StringComparison.OrdinalIgnoreCase) || o.Route.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        if (hits.Count == 0) return $"No actions of {api.Name}{(f.Length > 0 ? $" match '{Clip(f, 60)}'" : "")}.";
        var sb = new StringBuilder($"{api.Name} actions and what each does for your team (run = at once, ask = the person is asked, refused / never = not allowed):\n");
        foreach (var o in hits.Take(Cap))
        {
            var (d, rule) = ApiDecide(o, team, global);
            var word = d == Decisions.Allow ? "run" : d == Decisions.Ask ? "ask" : rule?.Layer == "floor" ? "never" : "refused";
            sb.Append("- ").Append(o.Id).Append(" [").Append(word).Append("] ").Append(o.Method).Append(' ').Append(o.Route);
            if (o.Summary.Length > 0) sb.Append(" — ").Append(Clip(o.Summary, 100));
            sb.Append('\n');
        }
        if (hits.Count > Cap) sb.Append($"... {hits.Count - Cap} more: narrow with params.filter.\n");
        return sb.ToString().TrimEnd();
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string ApiDescribeText(ApiRecord api, IReadOnlyList<ApiOp> ops, string? which)
    {
        if (which is null) return "describe needs params.action: the action id (action \"list\" shows them)";
        var op = ops.FirstOrDefault(o => o.Id == which.Trim() && !o.Gone);
        if (op is null) return $"refused: no action '{Clip(SecretScrubber.Scrub(which), 100)}'; use action list";
        var text = $"{op.Id}: {op.Method} {op.Route}{(op.Summary.Length > 0 ? " — " + op.Summary : "")}\n" +
            "params (a JSON schema: x-in says where each one goes, x-name its name in the request; the body goes in params.body):\n" + op.Params.ToJsonString(Indented);
        return text.Length > CliRunner.MaxOutputChars ? text[..CliRunner.MaxOutputChars] + "\n(output cut at 64 KB)" : text;
    }

    /// <summary>The header the sign-in uses (bearer and basic: Authorization; header: its name), or null.</summary>
    private static string? AuthHeaderName(JsonObject? auth) => AuthType(auth) switch
    {
        "bearer" or "basic" or "oauth2" => "Authorization",
        "header" => OptStr(auth, "name"),
        _ => null,
    };

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    /// <summary>
    /// Builds the request from the agent's parameters by the action's schema (x-in, x-name): path parameters are single percent-encoded
    /// segments (never a URL or "..": "/" becomes %2F), query and header parameters by their names, the body as JSON or a form. Unknown or
    /// missing parameters are refused. The URL must stay on the service URL's scheme, host and port, and under its path.
    /// </summary>
    private static ApiRequest PlanApiRequest(ApiRecord api, ApiOp op, JsonObject prms)
    {
        var service = HttpUrl(api.BaseUrl) ?? throw new ArgumentException($"{api.Name} has no service URL yet; ask the person to add it");
        if (service.UserInfo.Length > 0) throw new ArgumentException($"{api.Name}'s service URL holds a user name; ask the person to use a connection instead");
        var props = op.Params["properties"] as JsonObject ?? [];
        foreach (var (key, _) in prms)
            if (!props.ContainsKey(key)) throw new ArgumentException($"{op.Id} has no parameter '{Clip(key, 60)}' (use action describe)");
        foreach (var req in (op.Params["required"] as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>())
            if (prms[req] is null) throw new ArgumentException($"'{req}' is required for {op.Id} (use action describe)");

        static string? In(JsonNode? schema) => OptStr(schema as JsonObject, "x-in");
        static string Wire(string key, JsonNode? schema) => OptStr(schema as JsonObject, "x-name") ?? key;
        var authHeader = AuthHeaderName(api.Auth);
        var authQuery = AuthType(api.Auth) == "query" ? OptStr(api.Auth, "name") : null;

        var path = Regex.Replace(op.Route, @"\{([^{}/]+)\}", m =>
        {
            var name = m.Groups[1].Value;
            var key = props.FirstOrDefault(p => In(p.Value) == "path" && Wire(p.Key, p.Value) == name).Key
                ?? throw new ArgumentException($"{op.Id}'s route needs '{name}', which its spec does not describe");
            var value = ApiSingle(key, prms[key]) ?? throw new ArgumentException($"'{key}' is required for {op.Id}");
            if (value.Trim('.').Length == 0) throw new ArgumentException($"'{key}' cannot be '{Clip(value, 20)}': a path parameter is one value, not a path");
            return Uri.EscapeDataString(value);
        });

        var query = new List<string>();
        if (service.Query.Length > 1) query.Add(service.Query[1..]);
        var headers = new List<(string, string)>();
        foreach (var (key, schema) in props)
        {
            if (prms[key] is not { } value) continue;
            var wire = Wire(key, schema);
            switch (In(schema))
            {
                case "query":
                    if (authQuery is not null && string.Equals(wire, authQuery, StringComparison.OrdinalIgnoreCase)) break; // the sign-in's, added by the app
                    foreach (var v in ApiMany(key, value)) query.Add(Uri.EscapeDataString(wire) + "=" + Uri.EscapeDataString(v));
                    break;
                case "header":
                    if (ApiReservedHeaders.Contains(wire) || (authHeader is not null && string.Equals(wire, authHeader, StringComparison.OrdinalIgnoreCase))) break;
                    var hv = ApiSingle(key, value) ?? "";
                    if (hv.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException($"'{key}' cannot hold a line break");
                    headers.Add((wire, hv));
                    break;
            }
        }

        string? json = null;
        List<KeyValuePair<string, string>>? form = null;
        if (prms["body"] is { } body && In(props["body"]) is null)
        {
            switch (OptStr(op.Params, "x-body"))
            {
                case "json":
                    json = body.ToJsonString();
                    break;
                case "form": // sent as application/x-www-form-urlencoded (a multipart spec's fields too)
                    if (body is not JsonObject fields) throw new ArgumentException($"{op.Id}'s body is a form: give params.body as an object of fields");
                    form = [];
                    foreach (var (k, v) in fields)
                        if (v is not null) foreach (var s in ApiMany("body." + k, v)) form.Add(new(k, s));
                    break;
                default:
                    throw new ArgumentException($"{op.Id} takes no request body");
            }
        }

        var basePath = service.AbsolutePath.TrimEnd('/');
        var text = service.GetLeftPart(UriPartial.Authority) + basePath + (path.StartsWith('/') ? path : "/" + path) + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || !WithinService(url, service))
            throw new ArgumentException($"that request would leave {api.Name}'s service URL");
        return new ApiRequest(new HttpMethod(op.Method), url, headers, json, form);
    }

    /// <summary>The one check every URL the app sends to must pass: the service URL's scheme, host and port, no user info, and its path or under it.</summary>
    private static bool WithinService(Uri url, Uri service)
    {
        var basePath = service.AbsolutePath.TrimEnd('/');
        return SameOrigin(url, service) && url.UserInfo.Length == 0
            && (basePath.Length == 0 || url.AbsolutePath == basePath || url.AbsolutePath.StartsWith(basePath + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Why a redirect is not followed, or null when it may be. Only a GET or HEAD is ever followed (as the same method, no body). The target
    /// must be within the service URL and not a sign-in route (a credential segment such as "tokens", or the route of a credential-class
    /// action). And the rules decide it as a call would: if its path matches the route of an action of the follow-up's method (GET; for a
    /// HEAD, HEAD or GET) that is not allowed for the calling team (<paramref name="teamId"/>; null = every team's rules only, as Test), it
    /// is not followed.
    /// </summary>
    private string? WhyNotFollow(ApiRecord api, Uri target, Uri service, HttpMethod method, long? teamId)
    {
        if (method != HttpMethod.Get && method != HttpMethod.Head) return $"redirect of a {method}: not followed";
        if (!WithinService(target, service)) return "outside the service URL: not followed";
        var rest = target.AbsolutePath[service.AbsolutePath.TrimEnd('/').Length..];
        var path = "/" + string.Join('/', rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString));
        if (OpenApiReader.IsCredentialRoute(path)) return "a sign-in address: not followed";
        var team = teamId is { } t ? ApiRulesOf(api.Id, t) : [];
        var global = ApiRulesOf(api.Id, null);
        foreach (var op in ApiOps(api.Id))
        {
            var pattern = "^" + string.Join("/", op.Route.Split('/').Select(s => s.StartsWith('{') && s.EndsWith('}') ? "[^/]+" : Regex.Escape(s))) + "/?$";
            if (!Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200))) continue;
            if (op.Class == CommandClass.Credential) return "a sign-in address: not followed";
            var sameMethod = op.Method == "GET" || (method == HttpMethod.Head && op.Method == "HEAD");
            if (sameMethod && ApiDecide(op, team, global).Decision != Decisions.Allow) return $"{op.Id} is not allowed for your team: not followed";
        }
        return null;
    }

    /// <summary>A parameter as one text: a string as is, a number or true/false as written; a list or object is refused.</summary>
    private static string? ApiSingle(string key, JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => throw new ArgumentException($"'{key}' is one value, not a list or an object"),
    };

    /// <summary>A query or form parameter as its texts: a list repeats the name; an object is sent as its JSON.</summary>
    private static IEnumerable<string> ApiMany(string key, JsonNode node) => node switch
    {
        JsonArray list => list.Where(n => n is not null).Select(n => n is JsonValue ? ApiSingle(key, n)! : n!.ToJsonString()).ToList(),
        JsonObject o => [o.ToJsonString()],
        _ => [ApiSingle(key, node)!],
    };

    private static string NeedsConnection(ApiRecord api) =>
        $"refused: {api.Name} needs a connection; ask the person to pick one in Settings → Integrations → APIs";

    /// <summary>
    /// The sign-in for an API from its auth and its Connection's secrets (host only). No auth: nothing is added. Auth but no Connection
    /// (or it was removed): the agent is told to ask for one. Field keys as the sign-in dialog writes them: bearer "token", header or query
    /// its name, basic "username" (not secret) and "password"; a Connection with one secret uses it whatever its key. An oauth Connection
    /// sends its access token as a bearer (refreshed on the host), only to an API whose spec names its addresses and only for an action
    /// (<paramref name="op"/>) whose scopes it was granted; an oauth2 API with a pasted token sends it as a bearer.
    /// RefusedFor is what the call record says of a refusal.
    /// </summary>
    private async Task<(ApiSign? Sign, ConnectionView? Connection, string? Refusal, string RefusedFor)> ResolveSignInAsync(ApiRecord api, ApiOp? op, CancellationToken ct)
    {
        const string NoConnection = "no connection";
        var type = AuthType(api.Auth);
        var view = api.ConnectionId is { } cid ? connections?.Get(cid) : null;
        if (type is null) return (null, view, null, NoConnection);
        if (view is null) return (null, null, NeedsConnection(api), NoConnection);
        if (view.Kind == "oauth")
        {
            var (oauth, why, rule) = await OAuthSignAsync(api, op, view, ct);
            return (oauth, view, why, rule ?? NoConnection);
        }
        var secrets = connections!.Secrets(view.Id);
        // A single-secret sign-in (bearer, header, query) takes the field it expects, else the connection's only secret whatever its key
        // (one made in the Connections tab keeps a key as "token" or a label's slug). With several secrets and none by that key, it is refused.
        string? Secret(params string[] keys)
        {
            foreach (var k in keys)
                if (secrets.TryGetValue(k, out var v) && v.Length > 0) return v;
            return secrets.Count == 1 ? secrets.Values.First() : null;
        }
        var name = OptStr(api.Auth, "name");
        ApiSign? sign = type switch
        {
            "bearer" or "oauth2" => Secret("token") is { } t ? new ApiSign(false, "Authorization", "Bearer " + t, [t]) : null,
            "header" when name is not null => Secret(name, "key") is { } k ? new ApiSign(false, name, k, [k]) : null,
            "query" when name is not null => Secret(name, "key") is { } q ? new ApiSign(true, name, q, [q, Uri.EscapeDataString(q)]) : null,
            "basic" => Secret("password") is { } pw ? BasicSign(view, pw) : null,
            _ => null,
        };
        if (sign is null && type is ("bearer" or "oauth2" or "header" or "query") && secrets.Count > 1)
            return (null, view, $"refused: the connection '{view.Name}' holds {secrets.Count} keys and none of them is the one {api.Name} signs in with " +
                $"('{(type is "bearer" or "oauth2" ? "token" : name ?? "key")}'); ask the person to pick a connection with one key in Settings → Integrations → APIs", NoConnection);
        if (sign is null || sign.Value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            return (null, view, $"refused: the connection '{view.Name}' has no key {api.Name} can sign in with; ask the person to fix it in Settings → Integrations → APIs", NoConnection);
        return (sign, view, null, NoConnection);
    }

    private static ApiSign BasicSign(ConnectionView view, string password)
    {
        var user = view.Fields.FirstOrDefault(f => !f.Secret && f.Key == "username")?.Shown ?? view.Fields.FirstOrDefault(f => !f.Secret)?.Shown ?? "";
        var pair = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
        return new ApiSign(false, "Authorization", "Basic " + pair, [password, pair]);
    }

    /// <summary>Removes this call's own secret texts (the token, its base64 or URL forms), then every known secret and the usual patterns.</summary>
    private static string ApiScrub(string text, ApiSign? sign)
    {
        if (sign is not null)
            foreach (var s in sign.Secrets.Where(s => s.Length >= 4).OrderByDescending(s => s.Length))
                text = text.Replace(s, "[redacted]", StringComparison.Ordinal);
        return CliRunner.Redact(text);
    }

    private string Stamp() => _clock.GetUtcNow().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Sends the request with the sign-in, through a handler that never follows redirects: a 3xx of a GET or HEAD is followed once, as the same
    /// method with no body, when <see cref="WhyNotFollow"/> allows it for the calling team (<paramref name="teamId"/>); anything else is shown
    /// with its scrubbed location. The body is read up to a cap,
    /// scrubbed, then cut at 64 KB. A 401 or 403 marks the Connection "needs reconnect" (when <paramref name="view"/> is given).
    /// </summary>
    private async Task<ApiResult> SendApiAsync(ApiRecord api, ApiRequest request, ApiSign? sign, ConnectionView? view, long? teamId, CancellationToken ct)
    {
        if (sign is not { OAuthConnection: { } oid }) return await SendApiOnceAsync(api, request, sign, view, teamId, markUnauthorized: true, ct);
        // an OAuth sign-in refused with 401: one refresh (shared with every API and MCP server signed in with it) and one retry
        var first = await SendApiOnceAsync(api, request, sign, view, teamId, markUnauthorized: false, ct);
        if (first.Status != 401) return first;
        string fresh;
        try { fresh = await OAuthFlow.RefreshAfterRefusalAsync(oid, sign.Token, ct); }
        catch (McpSignInExpiredException) // marked needs reconnect by the flow
        {
            return view is null ? first : first with { Text = first.Text + "\n\n" + NeedsReconnecting(view, api) };
        }
        catch (Exception e) when (e is McpException or Credentials.CredentialStoreException)
        {
            return first with { Text = first.Text + $"\n\n{api.Name}'s sign-in could not be refreshed right now; try again later." };
        }
        if (fresh.IndexOfAny(['\r', '\n', '\0']) >= 0) return first;
        var again = sign with { Value = "Bearer " + fresh, Secrets = [fresh, .. sign.Secrets], Token = fresh };
        return await SendApiOnceAsync(api, request, again, view, teamId, markUnauthorized: true, ct);
    }

    /// <summary>One send of <see cref="SendApiAsync"/>; a 401 marks the Connection only when <paramref name="markUnauthorized"/> (a 403 always does).</summary>
    private async Task<ApiResult> SendApiOnceAsync(ApiRecord api, ApiRequest request, ApiSign? sign, ConnectionView? view, long? teamId, bool markUnauthorized, CancellationToken ct)
    {
        var service = HttpUrl(api.BaseUrl) ?? throw new ArgumentException($"{api.Name} has no service URL yet");
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        using var http = new HttpClient(ApiHandler(), disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        HttpRequestMessage Build(Uri url, HttpMethod method, bool withBody)
        {
            if (sign is { InQuery: true } && !HasQueryParam(url, sign.Name))
            {
                var b = new UriBuilder(url);
                var pair = Uri.EscapeDataString(sign.Name) + "=" + Uri.EscapeDataString(sign.Value);
                b.Query = b.Query.TrimStart('?') is { Length: > 0 } q ? q + "&" + pair : pair;
                url = b.Uri;
            }
            var m = new HttpRequestMessage(method, url);
            foreach (var (n, v) in request.Headers) m.Headers.TryAddWithoutValidation(n, v);
            if (sign is { InQuery: false })
            {
                m.Headers.Remove(sign.Name);
                m.Headers.TryAddWithoutValidation(sign.Name, sign.Value);
            }
            if (withBody)
                m.Content = request.JsonBody is { } j ? new StringContent(j, Encoding.UTF8, "application/json")
                    : request.Form is { } f ? new FormUrlEncodedContent(f) : null;
            return m;
        }

        HttpResponseMessage? response = null;
        try
        {
            using (var first = Build(request.Url, request.Method, true))
                response = await http.SendAsync(first, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            string? location = null;
            var where = "";
            if (IsRedirect(response) && response.Headers.Location is { } loc)
            {
                var target = loc.IsAbsoluteUri ? loc : new Uri(request.Url, loc);
                if (WhyNotFollow(api, target, service, request.Method, teamId) is not { } why)
                {
                    response.Dispose();
                    response = null;
                    using var again = Build(target, request.Method, false); // GET or HEAD: the same method, no body
                    response = await http.SendAsync(again, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if (IsRedirect(response) && response.Headers.Location is { } next)
                    {
                        location = (next.IsAbsoluteUri ? next : new Uri(target, next)).ToString();
                        where = "redirected again: not followed";
                    }
                }
                else
                {
                    location = target.ToString();
                    where = why;
                }
            }

            var status = (int)response.StatusCode;
            var head = ApiScrub($"{status} {response.ReasonPhrase}".TrimEnd(), sign);
            var media = response.Content.Headers.ContentType;
            var (bytes, over) = await ReadCappedAsync(response.Content, timeout.Token);
            string body;
            if (IsTextual(media?.MediaType, bytes)) body = Encoding.UTF8.GetString(bytes);
            else
            {
                var size = response.Content.Headers.ContentLength is { } len ? len.ToString(CultureInfo.InvariantCulture)
                    : over ? $"more than {ApiMaxBodyBytes}" : bytes.Length.ToString(CultureInfo.InvariantCulture);
                body = $"(binary, {size} bytes)";
            }
            body = ApiScrub(body, sign);
            var truncated = body.Length > CliRunner.MaxOutputChars;
            if (truncated) body = body[..CliRunner.MaxOutputChars] + "\n(output cut at 64 KB)";
            var sb = new StringBuilder("HTTP ").Append(head).Append('\n').Append(ApiScrub(media?.ToString() ?? "", sign));
            if (location is not null) sb.Append("\nLocation: ").Append(ApiScrub(location, sign)).Append(" (").Append(where).Append(')');
            sb.Append("\n\n").Append(body);
            if ((status == 403 || status == 401 && markUnauthorized) && sign is not null && view is not null)
            {
                connections?.MarkStatus(view.Id, "needs_reconnect", $"the service answered {head} at {Stamp()}");
                sb.Append("\n\n").Append(NeedsReconnecting(view, api));
            }
            return new ApiResult(sb.ToString(), new CliOutput(status, "", "", bytes.Length, watch.Elapsed, false, truncated), status, head);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ApiResult($"the call to {api.Name} timed out after {ApiTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s (stopped)", new CliOutput(-1, "", "", 0, watch.Elapsed, true, false), null, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or UriFormatException)
        {
            return new ApiResult($"the call to {api.Name} failed: {ApiScrub(e.Message, sign)}", null, null, null);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static bool IsRedirect(HttpResponseMessage r) => (int)r.StatusCode is >= 300 and < 400;

    private static bool HasQueryParam(Uri url, string name) =>
        url.Query.TrimStart('?').Split('&').Any(kv => string.Equals(Uri.UnescapeDataString(kv.Split('=')[0]), name, StringComparison.OrdinalIgnoreCase));

    private static async Task<(byte[] Bytes, bool Over)> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (ms.Length < ApiMaxBodyBytes)
        {
            var n = await s.ReadAsync(buffer, ct);
            if (n == 0) return (ms.ToArray(), false);
            ms.Write(buffer, 0, n);
        }
        return (ms.ToArray(), true);
    }

    /// <summary>Text when the content type is text, JSON, XML or a form; with no content type, when the bytes are UTF-8 without NULs.</summary>
    private static bool IsTextual(string? mediaType, byte[] bytes)
    {
        if (mediaType is null)
        {
            if (bytes.Length == 0) return true;
            if (Array.IndexOf(bytes, (byte)0) >= 0) return false;
            try { _ = new UTF8Encoding(false, true).GetString(bytes); return true; }
            catch (ArgumentException) { return false; }
        }
        var m = mediaType.ToLowerInvariant();
        return m.StartsWith("text/", StringComparison.Ordinal) || m.Contains("json", StringComparison.Ordinal) || m.Contains("xml", StringComparison.Ordinal)
            || m.Contains("javascript", StringComparison.Ordinal) || m.Contains("yaml", StringComparison.Ordinal) || m == "application/x-www-form-urlencoded";
    }

    /// <summary>An approved request for an API action: run it now (args were [actionId, paramsJson]). Rechecked: the action, its class, the sign-in.</summary>
    private async Task<ApiResult> RunApprovedApiAsync(AskRow ask, CancellationToken ct)
    {
        var name = ask.Integration["api:".Length..];
        if (ApiRows(name).FirstOrDefault() is not { } api) return new ApiResult($"{name} is no longer there", null, null, null);
        if (api.Kind == "website") return await RunApprovedWebsiteAsync(api, ask, ct);
        var op = ApiOps(api.Id).FirstOrDefault(o => o.Id == ask.Args[0] && !o.Gone);
        if (op is null) return new ApiResult($"refused: {api.Name} has no action '{ask.Args[0]}' any more", null, null, null);
        if (op.Class == CommandClass.Credential) return new ApiResult($"refused: {api.Name}'s {op.Id} reveals or changes a sign-in and is never allowed", null, null, null);
        JsonObject prms;
        try { prms = ask.Args.Count > 1 ? JsonNode.Parse(ask.Args[1]) as JsonObject ?? [] : []; }
        catch (JsonException) { return new ApiResult("refused: the request's parameters could not be read", null, null, null); }
        ApiRequest request;
        try { request = PlanApiRequest(api, op, prms); }
        catch (ArgumentException e) { return new ApiResult("refused: " + ApiScrub(e.Message, null), null, null, null); }
        var (sign, view, refusal, _) = await ResolveSignInAsync(api, op, ct);
        if (refusal is not null) return new ApiResult(refusal, null, null, null);
        return await SendApiAsync(api, request, sign, view, ask.TeamId, ct);
    }

    /// <summary>Why an approval's "always" or "keep asking" cannot be remembered for this action (as <see cref="DiscoverService.RememberApi"/> would refuse it), or null.</summary>
    private string? ApiRememberProblem(ApiRecord api, string action) =>
        ApiOps(api.Id).FirstOrDefault(o => o.Id == action) is not { } op ? $"'{action}' is not an operation of {api.Name}"
        : op.Class == CommandClass.Credential ? $"'{action}' is locked: it reveals or changes a sign-in and is never allowed"
        : null;

    /// <summary>Whether the approval card may offer "always allow" for an API action: it is one of the API's actions and not a sign-in one.</summary>
    private bool CanAlwaysApi(string api, string action) =>
        ApiRows(api).FirstOrDefault() is { } a && ApiOps(a.Id).FirstOrDefault(o => o.Id == action) is { } op && op.Class != CommandClass.Credential;

    /// <summary>
    /// Sends an approved request in the background, on the app's lifetime rather than the approve RPC's token, then logs it and tells the
    /// agent. Anything that goes wrong becomes a "could not run" message to the agent (scrubbed), never an unobserved crash.
    /// </summary>
    /// <param name="remembered">What the approval remembered, as the agent reads it, or "".</param>
    private async Task RunApprovedApiInBackgroundAsync(AskRow ask, string remembered, Provenance.Actor who)
    {
        var what = $"to {ask.Integration[4..]}'s {ask.Args[0]} (request {ask.Id}){remembered}";
        string text;
        try
        {
            var result = await RunApprovedApiAsync(ask, CancellationToken.None);
            LogFor(ask, "approved", result.Output, who);
            text = $"The person approved your call {what}. It returned:\n\n{result.Text}";
        }
        catch (Exception e)
        {
            try { LogFor(ask, "approved", null, who); } catch (Exception) { /* the store is gone (shutting down): nothing more to record */ }
            text = $"The person approved your call {what}, but it could not run: {ApiScrub(e.Message, null)}";
        }
        try { await Tell(ask, text, CancellationToken.None); }
        catch (Exception) { /* the member, team or bus is gone: the call is still recorded */ }
    }

    /// <summary>
    /// Tests a Connection: for each API using it whose sign-in (auth) is set, the first GET that is not a sign-in action, needs no parameters
    /// and is allowed for at least one scope (every team, or one team) by <see cref="CliRules.DecideExact"/> is called with it. Only a 2xx
    /// means works and only a 401 or 403 needs reconnect (it wins over works). Anything else (a 5xx, a timeout, no answer, a request that
    /// cannot be built) proves nothing, so the status is left as it was, with "the service did not answer clearly (...); status unchanged".
    /// Nothing to call: unchanged too, "nothing to test against yet". Not logged as a call.
    /// </summary>
    private async Task<JsonNode> ConnectionTestAsync(long id, CancellationToken ct)
    {
        var view = Conns.Get(id) ?? throw new ArgumentException("no such connection");
        if (view.Kind == "website") return WebsiteConnectionTest(id);
        if (view.Kind == "oauth" && await OAuthConnectionTestAsync(view, ct) is { } tested) return tested;
        const string Unclear = "unclear";
        var outcomes = new List<(string Status, string Reason)>();
        foreach (var name in view.UsedBy)
        {
            if (ApiRows(name).FirstOrDefault() is not { } api || api.ConnectionId != id || AuthType(api.Auth) is null) continue; // no sign-in: the secret would not be used
            var global = ApiRulesOf(api.Id, null);
            var teams = ApiRuleTeams(api.Id).Concat(ApiTeamsOf(api.Id)).Distinct().Select(t => ApiRulesOf(api.Id, t)).ToList();
            bool AllowedSomewhere(ApiOp o) =>
                CliRules.DecideExact(o.Id, [], global).Decision == Decisions.Allow || teams.Any(t => CliRules.DecideExact(o.Id, t, global).Decision == Decisions.Allow);
            var op = ApiOps(api.Id)
                .FirstOrDefault(o => !o.Gone && o.Method == "GET" && o.Class != CommandClass.Credential && (o.Params["required"] as JsonArray ?? []).Count == 0 && AllowedSomewhere(o));
            if (op is null) continue;
            ApiRequest request;
            try { request = PlanApiRequest(api, op, []); }
            catch (ArgumentException e) { outcomes.Add((Unclear, Clip(e.Message, 200))); continue; }
            var (sign, _, refusal, _) = await ResolveSignInAsync(api with { ConnectionId = id }, op, ct);
            if (refusal is not null) { outcomes.Add((Unclear, Clip(refusal, 200))); continue; }
            var result = await SendApiAsync(api, request, sign, null, null, ct);
            outcomes.Add(result.Status switch
            {
                >= 200 and < 300 => ("works", $"{api.Name} answered {result.Head} to {op.Id} at {Stamp()}"),
                401 or 403 => ("needs_reconnect", $"the service answered {result.Head} at {Stamp()}"),
                not null => (Unclear, $"{api.Name} answered {result.Head} to {op.Id} at {Stamp()}"),
                null => (Unclear, Clip(result.Text, 200)),
            });
        }
        var decided = outcomes.FirstOrDefault(o => o.Status == "needs_reconnect") is { Status: not null } bad ? bad
            : outcomes.FirstOrDefault(o => o.Status == "works") is { Status: not null } good ? good
            : ((string Status, string Reason)?)null;
        if (decided is { } d)
        {
            Conns.MarkStatus(id, d.Status, d.Reason);
            return new JsonObject { ["status"] = d.Status, ["reason"] = d.Reason, ["changed"] = true };
        }
        // nothing proved either way: the status stays (a real "needs reconnect" is never cleared without a 2xx)
        var reason = outcomes.Count > 0 ? $"the service did not answer clearly ({outcomes[^1].Reason}); status unchanged" : "nothing to test against yet; status unchanged";
        return new JsonObject { ["status"] = view.Status, ["reason"] = reason, ["changed"] = false };
    }

    /// <summary>The Connections waiting for the person (needs reconnect), for Needs You: name, reason and where to fix it. No secret is read.</summary>
    public IReadOnlyList<ConnectionBrief> ConnectionsNeedingReconnect() => connections?.NeedingReconnect() ?? [];

    /// <summary>Where the person fixes a Connection, as Needs You says it.</summary>
    public const string ConnectionsPlace = "Settings → Integrations → Connections";

    private JsonNode ConnectionAttentionJson() => new JsonArray(ConnectionsNeedingReconnect().Select(b => (JsonNode?)new JsonObject
    {
        ["id"] = b.Id, ["name"] = b.Name, ["reason"] = b.StatusReason, ["where"] = ConnectionsPlace,
    }).ToArray());

    /// <summary>The teams that have rules of their own on an API.</summary>
    private List<long> ApiRuleTeams(long apiId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT team_id FROM api_rules WHERE api_id = $a AND team_id IS NOT NULL;";
        cmd.Parameters.AddWithValue("$a", apiId);
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }
}
