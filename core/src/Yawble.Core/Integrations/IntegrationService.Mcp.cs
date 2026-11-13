using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Ipc;

namespace Yawble.Core.Integrations;

/// <summary>A local MCP server the person added (0020).</summary>
public sealed record McpServerRow(long Id, string Name, string Command, IReadOnlyList<string> Args, JsonArray Tools, string? Status, bool EnabledAll,
    string Transport, string? Url, McpAuth? Auth, IReadOnlyList<McpEnvVar> Env, long? ConnectionId, IReadOnlyList<string> Sources);

/// <summary>An environment variable for a local server: a secret one has no value here (it is a field of the server's Connection).</summary>
public sealed record McpEnvVar(string Name, string? Value, bool Secret);

/// <summary>How a remote server signs in: none, bearer (a token), header (a key in a named header) or oauth.</summary>
public sealed record McpAuth(string Type, string? Header);

/// <summary>
/// MCP servers as integrations: the person's local MCP servers, started on this computer, their tools offered
/// to the teams chosen as "server__tool", every call decided by the person's rules on the tool name (allow, ask, deny;
/// nothing said: ask), asked calls approved like any other, all logged as integration "mcp:server".
/// </summary>
public sealed partial class IntegrationService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IMcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    public const string ReasonProperty = "_reason";

    private static bool IsMcpMethod(string method) => method.StartsWith("integrations.mcp", StringComparison.Ordinal);

    private async Task<JsonNode?> DispatchMcpAsync(string method, JsonObject? p, CancellationToken ct) => method switch
    {
        "integrations.mcpList" => await McpListAsync(ct),
        "integrations.mcpSave" => McpSave(p),
        "integrations.mcpImportScan" => McpImportScan(),
        "integrations.mcpParse" => McpParse(Str(p, "text")),
        "integrations.mcpImport" => McpImport(p),
        "integrations.mcpCheck" => await McpCheckAsync(Str(p, "name"), ct),
        "integrations.mcpSignIn" => await McpSignInAsync(Str(p, "name"), ct),
        "integrations.mcpEnable" => McpEnable(Str(p, "name"), p?["all"]?.GetValue<bool>(), OptLong(p, "teamId"), p?["on"]?.GetValue<bool>() ?? true),
        "integrations.mcpRemove" => await McpRemoveAsync(Str(p, "name"), p?["removeConnection"] is JsonValue rc && rc.TryGetValue<bool>(out var rcv) && rcv),
        "integrations.mcpSaveRule" => McpSaveRule(Str(p, "name"), OptLong(p, "teamId"), Str(p, "pattern"), Str(p, "decision")),
        "integrations.mcpDeleteRule" => McpDeleteRule(OptLong(p, "id") ?? throw new RpcException("bad_params", "which rule?")),
        _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
    };

    private async Task<JsonNode> McpListAsync(CancellationToken ct)
    {
        var teams = await bus.TeamsAsync(ct);
        return new JsonArray(McpRows().Select(s => (JsonNode?)new JsonObject
        {
            ["name"] = s.Name, ["transport"] = s.Transport, ["url"] = s.Url is null ? null : McpImporter.MaskUrl(s.Url, []),
            ["auth"] = s.Auth is null ? null : McpAuthJson(s.Auth), ["env"] = McpEnvView(s),
            ["connection"] = s.ConnectionId is { } cid && connections?.Brief(cid) is { } cv ? new JsonObject { ["id"] = cv.Id, ["name"] = cv.Name, ["status"] = cv.Status, ["statusReason"] = cv.StatusReason } : null,
            ["sources"] = new JsonArray(s.Sources.Select(x => (JsonNode?)x).ToArray()),
            ["command"] = s.Command, ["args"] = new JsonArray(McpImporter.MaskArgs(s.Args, []).Select(a => (JsonNode?)a).ToArray()),
            ["status"] = s.Status, ["counts"] = s.Tools.Count == 0 ? null : CountsJson(() => Discover.GetMcpChecklist(s.Name, null)), ["all"] = s.EnabledAll, ["teams"] = new JsonArray(McpTeamsOf(s.Id).Select(t => (JsonNode?)t).ToArray()),
            ["tools"] = new JsonArray(s.Tools.OfType<JsonObject>().Select(t => (JsonNode?)new JsonObject
            {
                ["name"] = McpClassifier.StringOf(t["name"]), ["description"] = Clip(McpClassifier.StringOf(t["description"]) ?? "", 200),
            }).ToArray()),
            ["rules"] = new JsonArray(McpRules(s.Id).Select(r => (JsonNode?)new JsonObject
            {
                ["id"] = r.Rule.Id, ["teamId"] = r.TeamId, ["team"] = teams.FirstOrDefault(t => t.Id == r.TeamId)?.Name, ["pattern"] = r.Rule.Pattern, ["decision"] = r.Rule.Decision,
            }).ToArray()),
        }).ToArray());
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip));

    /// <summary>A remote server's address: https, or plain http only to this computer (a token sent over http elsewhere could be read on the way).</summary>
    private static string CleanMcpUrl(string? text)
    {
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var u) || u.Host.Length == 0 || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("give the server's full web address, starting with https://");
        if (u.UserInfo.Length > 0) throw new ArgumentException("put the sign-in in the token field, not in the address");
        if (u.Scheme == Uri.UriSchemeHttp && !IsLoopback(u.Host))
            throw new ArgumentException("a remote server's address must start with https:// (http:// is allowed only for a server on this computer)");
        return u.AbsoluteUri;
    }

    private static McpAuth CleanMcpAuth(JsonNode? n)
    {
        if (n is not JsonObject o) return new McpAuth("none", null);
        var type = OptStr(o, "type") ?? "none";
        if (type is not ("none" or "bearer" or "header" or "oauth")) throw new ArgumentException("sign-in is none, bearer, header or oauth");
        var header = OptStr(o, "header")?.Trim();
        if (type == "header" && string.IsNullOrEmpty(header)) throw new ArgumentException("say which header carries the key");
        return new McpAuth(type, type == "header" ? header : null);
    }

    private static List<McpEnvVar> CleanMcpEnv(JsonNode? n)
    {
        var list = new List<McpEnvVar>();
        foreach (var e in n as JsonArray ?? [])
        {
            if (e is not JsonObject o) throw new ArgumentException("each environment variable needs a name");
            var name = OptStr(o, "name")?.Trim() ?? throw new ArgumentException("each environment variable needs a name");
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")) throw new ArgumentException($"'{name}' is not an environment variable name");
            if (list.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"{name} is listed twice");
            var secret = o["secret"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
            list.Add(new McpEnvVar(name, secret ? null : o["value"] is JsonValue vv && vv.TryGetValue<string>(out var vs) ? vs : "", secret));
        }
        return list;
    }

    /// <param name="sources">Where an imported server came from (saved in its row); null leaves a row's as it is.</param>
    /// <param name="allowMissing">An import may leave a secret to be entered later (the server then says it needs it when started).</param>
    private JsonNode McpSave(JsonObject? p, IReadOnlyList<string>? sources = null, bool allowMissing = false)
    {
        var name = CheckName(Str(p, "name"));
        var existing = McpRow(name);
        var transport = OptStr(p, "transport") ?? (p is not null && p.ContainsKey("transport") ? "stdio" : existing?.Transport ?? "stdio");
        if (transport is not ("stdio" or "http")) throw new ArgumentException("transport is stdio or http");
        string command = "", url = "";
        List<string> args = [];
        McpAuth? auth = null;
        if (transport == "stdio")
        {
            command = Str(p, "command").Trim();
            if (command.Length == 0) throw new ArgumentException("give the command that starts the server, e.g. npx");
            args = p is not null && p.ContainsKey("args") ? (p["args"] as JsonArray ?? []).Select(a => a?.ToString() ?? "").ToList() : existing?.Args.ToList() ?? []; // absent on an edit = as stored (the list shows them masked)
        }
        else
        {
            url = CleanMcpUrl(OptStr(p, "url") ?? existing?.Url);
            auth = p is not null && p.ContainsKey("auth") ? CleanMcpAuth(p["auth"]) : existing?.Auth ?? new McpAuth("none", null);
        }
        // env: present = set, absent = unchanged on an edit (so a secret-only edit, or an older screen, leaves it alone)
        var env = p is not null && p.ContainsKey("env") ? CleanMcpEnv(p["env"]) : existing?.Env.ToList() ?? [];
        if (transport == "http") env = [];

        var secretNames = transport == "stdio" ? env.Where(v => v.Secret).Select(v => v.Name).ToList()
            : auth is { Type: "bearer" or "header" } ? ["token"] : [];
        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        if (p?["secrets"] is JsonObject so)
            foreach (var (k, v) in so)
                if (v is JsonValue sv && sv.TryGetValue<string>(out var text) && text.Length > 0)
                {
                    if (!secretNames.Contains(k)) throw new ArgumentException($"{k} is not a secret of this server");
                    given[k] = text;
                }
        var connectionId = McpConnectionFor(name, existing, p, secretNames, given, keepForOauth: auth?.Type == "oauth", allowMissing);

        Exec("""
            INSERT INTO mcp_servers (name, command, args, transport, url, auth, env, connection_id, sources, created_at, updated_at) VALUES ($n, $c, $a, $t, $u, $au, $e, $cid, $src, $now, $now)
            ON CONFLICT(name) DO UPDATE SET command = excluded.command, args = excluded.args, transport = excluded.transport, url = excluded.url, auth = excluded.auth,
                env = excluded.env, connection_id = excluded.connection_id, sources = COALESCE(excluded.sources, sources), tools = NULL, status = NULL, updated_at = excluded.updated_at;
            """, ("$n", name), ("$c", command), ("$a", JsonSerializer.Serialize(args)), ("$t", transport), ("$u", transport == "http" ? url : null),
            ("$au", auth is null ? null : McpAuthJson(auth).ToJsonString()), ("$e", McpEnvJson(env).ToJsonString()), ("$cid", connectionId), ("$src", sources is null ? null : JsonSerializer.Serialize(sources)), ("$now", Now()));
        DropOrphanedConnection(name, existing?.ConnectionId, connectionId);
        if (_clients.TryRemove(name, out var old)) _ = old.DisposeAsync().AsTask();
        return new JsonObject { ["ok"] = true };
    }

    /// <summary>
    /// After the server's row is saved: the Connection it used before is removed when the edit moved it elsewhere, nothing else uses it, and it
    /// was made for this server (named exactly "&lt;server&gt; secrets", "&lt;server&gt; token" or "&lt;server&gt; sign-in"). A Connection the person made and picked is never deleted.
    /// </summary>
    private void DropOrphanedConnection(string name, long? before, long? now)
    {
        if (before is not { } old || old == now || Conns.Get(old) is not { } v) return;
        // made for this server: "<server> secrets|token|sign-in" with an optional " 2", " 3"..., and of the kind that name is for
        var m = System.Text.RegularExpressions.Regex.Match(v.Name, "^" + System.Text.RegularExpressions.Regex.Escape(name) + " (secrets|token|sign-in)( [0-9]+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var auto = m.Success && v.Kind == (m.Groups[1].Value.Equals("sign-in", StringComparison.OrdinalIgnoreCase) ? "oauth" : "secret");
        if (auto && v.UsedBy.Count == 0) Conns.Remove(old);
    }

    /// <summary>
    /// Which Connection holds this server's secrets. An explicit connectionId is used as it is; secrets given are saved (reusing a Connection that
    /// already holds exactly them); secrets not re-sent stay as they were. The values themselves never go into the server's row.
    /// </summary>
    private long? McpConnectionFor(string name, McpServerRow? existing, JsonObject? p, List<string> secretNames, Dictionary<string, string> given, bool keepForOauth, bool allowMissing = false)
    {
        var explicitId = p is not null && p.ContainsKey("connectionId") && OptLong(p, "connectionId") is not null;
        if (explicitId && given.Count > 0) throw new ArgumentException("choose a connection or paste a value, not both");
        // A null or absent connectionId means "keep the Connection the server has" (on purpose: an older screen, or a secret-only edit, never sends it).
        long? id = explicitId ? OptLong(p, "connectionId") : existing?.ConnectionId;
        if (id is { } eid && Conns.Get(eid) is null) throw new ArgumentException("no such connection");
        if (explicitId && Conns.Get(id!.Value) is { Kind: "website" } login)
            throw new ArgumentException($"'{login.Name}' is a website login: it signs in to a website in this app's window, not an MCP server; choose an API key or token");
        if (secretNames.Count == 0) return explicitId || keepForOauth ? id : null;

        var current = id is { } cid ? Conns.Secrets(cid) : new Dictionary<string, string>();
        if (explicitId)
        {
            foreach (var n in secretNames)
                if (!current.ContainsKey(n)) throw new ArgumentException($"that connection has no '{n}' value; choose another or enter it here");
            return id;
        }
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in secretNames)
        {
            if (given.TryGetValue(n, out var g)) merged[n] = g;
            else if (current.TryGetValue(n, out var c)) merged[n] = c;
            else if (allowMissing) continue;
            else throw new ArgumentException($"give a value for {n}");
        }
        if (given.Count == 0) return id;
        secretNames = secretNames.Where(merged.ContainsKey).ToList();

        // Reuse needs the same field keys as well as the same values: the server reads each secret by its field key (the variable name, or "token").
        if (Conns.FindIdentical(merged) is { } same && Conns.Secrets(same) is var theirs && secretNames.All(n => theirs.TryGetValue(n, out var v) && v == merged[n]))
            return same;
        var fields = secretNames.Select(n => new ConnectionField(n, n, true, null)).ToList();
        // a Connection only this server uses is edited in place; a shared one is left alone, and so is a browser sign-in (a token is a new Connection; the sign-in is dropped)
        if (id is { } mine && Conns.Get(mine) is { Kind: not "oauth" } view && view.UsedBy.All(u => string.Equals(u, "MCP: " + name, StringComparison.OrdinalIgnoreCase)))
            return Conns.Save(mine, view.Name, fields, given).Id; // same Connection: nothing to drop
        var baseName = $"{name} {(secretNames is ["token"] ? "token" : "secrets")}";
        var taken = Conns.List().Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pick = baseName;
        for (var i = 2; taken.Contains(pick); i++) pick = $"{baseName} {i}";
        return Conns.Save(null, pick, fields, merged).Id;
    }

    // ------------------------------------------------------------------ import from this computer

    /// <summary>Parses pasted text into drafts for the Add form. Values are echoed: they come from the person's own paste and are not saved yet.</summary>
    private static JsonNode McpParse(string text) => new JsonObject { ["drafts"] = System.Text.Json.JsonSerializer.SerializeToNode(McpPaste.Parse(text), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) };

    private JsonNode McpImportScan()
    {
        var found = McpImporter.Scan((mcpSources ?? McpImporter.DefaultSources)(), McpRows());
        return new JsonArray(found.Select(c => (JsonNode?)new JsonObject
        {
            ["key"] = c.Key, ["name"] = c.Name, ["transport"] = c.Transport, ["command"] = c.Command,
            ["args"] = new JsonArray((c.ArgsShown ?? c.Args).Select(a => (JsonNode?)a).ToArray()), ["url"] = c.UrlShown ?? c.Url, ["note"] = c.Note,
            ["auth"] = c.Auth is null ? null : McpAuthJson(c.Auth),
            ["tokenShown"] = c.Secrets.TryGetValue("token", out var tv) ? Connections.ConnectionService.Mask(tv) : null,
            ["env"] = new JsonArray(c.Env.Select(v => (JsonNode?)(v.Secret
                ? new JsonObject { ["name"] = v.Name, ["secret"] = true, ["shown"] = c.Secrets.TryGetValue(v.Name, out var sv) ? Connections.ConnectionService.Mask(sv) : null }
                : new JsonObject { ["name"] = v.Name, ["secret"] = false, ["value"] = c.EnvShown?.GetValueOrDefault(v.Name) ?? v.Value })).ToArray()),
            ["sources"] = new JsonArray(c.Sources.Select(x => (JsonNode?)x).ToArray()), ["alreadyAdded"] = c.AlreadyAdded,
        }).ToArray());
    }

    /// <summary>Imports the chosen servers (found again by key, so the screen never holds a secret): secrets go into a Connection, names stay unique.</summary>
    private JsonNode McpImport(JsonObject? p)
    {
        var keys = (p?["keys"] as JsonArray ?? []).Select(k => k?.ToString() ?? "").ToList();
        var found = McpImporter.Scan((mcpSources ?? McpImporter.DefaultSources)(), McpRows());
        var imported = new JsonArray();
        var missing = new JsonArray();
        var failed = new JsonArray();
        var already = new JsonArray();
        foreach (var key in keys.Distinct())
        {
            if (found.FirstOrDefault(c => c.Key == key) is not { } c) { missing.Add(key); continue; }
            if (c.AlreadyAdded is { } have) { already.Add(have); continue; }
            var name = FreeMcpName(c.Name);
            var save = new JsonObject { ["name"] = name, ["transport"] = c.Transport };
            if (c.Transport == "http")
            {
                save["url"] = c.Url;
                save["auth"] = c.Auth is null ? new JsonObject { ["type"] = "none" } : McpAuthJson(c.Auth);
            }
            else
            {
                save["command"] = c.Command;
                save["args"] = new JsonArray(c.Args.Select(a => (JsonNode?)a).ToArray());
                save["env"] = McpEnvJson(c.Env);
            }
            save["secrets"] = new JsonObject(c.Secrets.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
            try
            {
                McpSave(save, c.Sources, allowMissing: true);
                // secrets that stay in the args or address as written: kept out of agent output
                if (McpRow(name) is { } row && c.Hidden is { Count: > 0 } hidden) SecretScrubber.Set(_mcpScrubOwner, row.Id, hidden);
                imported.Add(name);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed.Add(new JsonObject { ["key"] = key, ["name"] = c.Name, ["error"] = SecretScrubber.Scrub(e.Message) });
            }
        }
        return new JsonObject { ["imported"] = imported, ["missing"] = missing, ["failed"] = failed, ["alreadyAdded"] = already };
    }

    private string FreeMcpName(string wanted)
    {
        var clean = System.Text.RegularExpressions.Regex.Replace(wanted ?? "", "[^A-Za-z0-9._-]+", "-").Trim('-', '.', '_');
        if (clean.Length == 0) clean = "server";
        if (clean.Length > 36) clean = clean[..36];
        var pick = clean;
        for (var i = 2; McpRow(pick) is not null; i++) pick = $"{clean}-{i}";
        return pick;
    }

    private static JsonObject McpAuthJson(McpAuth a) => new() { ["type"] = a.Type, ["header"] = a.Header };

    /// <summary>The row's env: a secret entry has no value (its value is a field of the server's Connection).</summary>
    private static JsonArray McpEnvJson(IEnumerable<McpEnvVar> env) => new(env.Select(v =>
    {
        var o = new JsonObject { ["name"] = v.Name };
        if (!v.Secret) o["value"] = v.Value ?? "";
        o["secret"] = v.Secret;
        return (JsonNode?)o;
    }).ToArray());

    /// <summary>The env as the screen sees it: plain values as they are, secret ones only masked.</summary>
    private JsonArray McpEnvView(McpServerRow s)
    {
        IReadOnlyDictionary<string, string> secrets = new Dictionary<string, string>();
        if (s.ConnectionId is { } cid && s.Env.Any(v => v.Secret) && connections is not null)
        {
            try { secrets = connections.Secrets(cid); }
            catch (Credentials.CredentialStoreException) { /* shown as not set */ }
        }
        return new JsonArray(s.Env.Select(v => (JsonNode?)(v.Secret
            ? new JsonObject { ["name"] = v.Name, ["secret"] = true, ["set"] = secrets.ContainsKey(v.Name), ["shown"] = secrets.TryGetValue(v.Name, out var sv) ? Connections.ConnectionService.Mask(sv) : null }
            : new JsonObject { ["name"] = v.Name, ["value"] = v.Value, ["secret"] = false })).ToArray());
    }

    /// <summary>The server's env with its secrets read from its Connection (host only, for starting the server); each secret is also registered with the scrubber.</summary>
    private IReadOnlyDictionary<string, string> McpEnvFor(McpServerRow s)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in s.Env.Where(v => !v.Secret)) env[v.Name] = v.Value ?? "";
        // secrets left in the args or address as written are kept out of agent output whether or not there is any secret env
        SecretScrubber.Set(_mcpScrubOwner, s.Id, McpImporter.ArgSecretValues(s.Args, s.Url, s.Env));
        if (s.Env.Any(v => v.Secret))
        {
            var secrets = s.ConnectionId is { } cid ? Conns.Secrets(cid) : new Dictionary<string, string>();
            foreach (var v in s.Env.Where(v => v.Secret))
                env[v.Name] = secrets.TryGetValue(v.Name, out var value) ? value : throw new McpException($"{s.Name} needs a value for {v.Name}: edit the server and enter it again");
            Connections.SecretScrubber.Set(_mcpScrubOwner, s.Id, env.Where(kv => s.Env.Any(v => v.Secret && v.Name == kv.Key)).Select(kv => kv.Value).Concat(McpImporter.ArgSecretValues(s.Args, s.Url, s.Env)));
        }
        return env;
    }

    /// <summary>Starts the server (or reuses it), lists its tools and keeps them, so they can be offered and ruled on.</summary>
    private async Task<JsonNode> McpCheckAsync(string name, CancellationToken ct)
    {
        var tools = await ListAndStoreToolsAsync(name, ct);
        return new JsonObject { ["ok"] = true, ["tools"] = tools.Count };
    }

    /// <summary>The body of mcpCheck, shared with Discover: starts the server (or reuses it), lists its tools and stores them and the status line.</summary>
    private async Task<JsonArray> ListAndStoreToolsAsync(string name, CancellationToken ct)
    {
        var s = McpRow(name) ?? throw new ArgumentException($"no MCP server named '{name}'");
        try
        {
            var tools = await WithRefreshAsync(s, () => Client(s).ListToolsAsync(ct), ct);
            McpWorks(s);
            Exec("UPDATE mcp_servers SET tools = $t, status = $s, updated_at = $now WHERE id = $id;",
                ("$t", tools.ToJsonString()), ("$s", $"running: {tools.Count} tool{(tools.Count == 1 ? "" : "s")}"), ("$now", Now()), ("$id", s.Id));
            return tools;
        }
        catch (McpException e)
        {
            var said = McpFailure(s, e);
            Exec("UPDATE mcp_servers SET status = $s, updated_at = $now WHERE id = $id;", ("$s", "not working: " + Clip(said, 300)), ("$now", Now()), ("$id", s.Id));
            if (_clients.TryRemove(s.Name, out var broken)) await broken.DisposeAsync();
            throw new ArgumentException(said);
        }
    }

    private JsonNode McpEnable(string name, bool? all, long? teamId, bool on)
    {
        var s = McpRow(name) ?? throw new ArgumentException($"no MCP server named '{name}'");
        if (all is { } a) Exec("UPDATE mcp_servers SET enabled_all = $a, updated_at = $now WHERE id = $id;", ("$a", a ? 1 : 0), ("$now", Now()), ("$id", s.Id));
        if (teamId is { } t)
            Exec(on ? "INSERT OR IGNORE INTO mcp_server_teams (server_id, team_id) VALUES ($s, $t);" : "DELETE FROM mcp_server_teams WHERE server_id = $s AND team_id = $t;",
                ("$s", s.Id), ("$t", t));
        return new JsonObject { ["ok"] = true };
    }

    /// <summary>Whether this is a remote (http) MCP server (the Cloud Link's check before it lets a viewer run Discover on one).</summary>
    public bool IsRemoteMcp(string name) => McpRow(name)?.Transport == "http";

    private async Task<JsonNode> McpRemoveAsync(string name, bool removeConnection)
    {
        var gone = McpRow(name);
        JsonObject? sole = null;
        if (gone?.ConnectionId is { } cid && connections?.Get(cid) is { } view && view.UsedBy.Count == 1 && string.Equals(view.UsedBy[0], "MCP: " + gone.Name, StringComparison.OrdinalIgnoreCase))
            sole = new JsonObject { ["id"] = view.Id, ["name"] = view.Name };
        if (gone is not null) SecretScrubber.Forget(_mcpScrubOwner, gone.Id);
        RemoveWithPendingAsks("mcp:" + name, "MCP server removed", "DELETE FROM mcp_servers WHERE name = $n;", ("$n", name));
        if (_clients.TryRemove(name, out var c)) await c.DisposeAsync();
        if (removeConnection && sole is not null) Conns.Remove(gone!.ConnectionId!.Value);
        return new JsonObject { ["ok"] = true, ["soleConnection"] = sole, ["connectionRemoved"] = removeConnection && sole is not null };
    }

    private JsonNode McpSaveRule(string name, long? teamId, string pattern, string decision)
    {
        if (CliRules.Problem(pattern) is { } why) throw new ArgumentException(why);
        if (!Decisions.All.Contains(decision)) throw new ArgumentException("decision is allow, ask or deny");
        var s = McpRow(name) ?? throw new ArgumentException($"no MCP server named '{name}'");
        Exec("DELETE FROM mcp_rules WHERE server_id = $s AND team_id IS $t AND pattern = $p COLLATE NOCASE;", ("$s", s.Id), ("$t", teamId), ("$p", pattern.Trim()));
        Exec("INSERT INTO mcp_rules (server_id, team_id, pattern, decision, created_at) VALUES ($s, $t, $p, $d, $now);",
            ("$s", s.Id), ("$t", teamId), ("$p", pattern.Trim()), ("$d", decision), ("$now", Now()));
        return new JsonObject { ["ok"] = true };
    }

    private JsonNode McpDeleteRule(long id)
    {
        Exec("DELETE FROM mcp_rules WHERE id = $id;", ("$id", id));
        return new JsonObject { ["ok"] = true };
    }

    // ------------------------------------------------------------------ the agents' side

    /// <summary>"server__tool" names, with the server's own schema plus an optional reason (shown if it asks).</summary>
    private IEnumerable<JsonObject> McpToolsFor(long teamId)
    {
        foreach (var s in McpRows().Where(s => s.EnabledAll || McpTeamsOf(s.Id).Contains(teamId)))
        {
            foreach (var t in s.Tools.OfType<JsonObject>())
            {
                if (McpClassifier.StringOf(t["name"]) is not { Length: > 0 } tool) continue;
                var schema = t["inputSchema"]?.DeepClone() as JsonObject ?? new JsonObject { ["type"] = "object" };
                if (schema["properties"] is not JsonObject props) schema["properties"] = props = [];
                props[ReasonProperty] = new JsonObject { ["type"] = "string", ["description"] = "Why you need this call, in one line (shown to the person if it asks them)." };
                yield return new JsonObject
                {
                    ["name"] = McpToolName(s.Name, tool),
                    ["description"] = $"[{s.Name}, the person's MCP server, under rules: it may ask the person first] {McpClassifier.StringOf(t["description"]) ?? ""}",
                    ["inputSchema"] = schema,
                };
            }
        }
    }

    public static string McpToolName(string server, string tool) => ToolName(server) + "__" + ToolName(tool);

    private (McpServerRow Server, string Tool)? McpToolOf(long teamId, string name)
    {
        foreach (var s in McpRows().Where(s => s.EnabledAll || McpTeamsOf(s.Id).Contains(teamId)))
            foreach (var t in s.Tools.OfType<JsonObject>())
                if (McpClassifier.StringOf(t["name"]) is { } tool && McpToolName(s.Name, tool) == name) return (s, tool);
        return null;
    }

    private async Task<string> CallMcpAsync(RunContext run, McpServerRow server, string tool, JsonObject args, CancellationToken ct)
    {
        var reason = args[ReasonProperty] is JsonValue rv && rv.TryGetValue<string>(out var rs) && !string.IsNullOrWhiteSpace(rs) ? rs.Trim() : null;
        var clean = args.DeepClone().AsObject();
        clean.Remove(ReasonProperty);
        var integration = "mcp:" + server.Name;
        var callArgs = new[] { tool, clean.ToJsonString() };
        var (decision, rule) = CliRules.Decide([tool], null, McpRules(server.Id, run.Member.TeamId), McpRules(server.Id, null));
        switch (decision)
        {
            case Decisions.Deny:
                Insert(integration, run.Member.Team, run.Member.Qualified, run.Correlation, callArgs, Decisions.Deny, rule?.Pattern, null, Provenance.Actor.Current);
                return $"refused: {server.Name}'s {tool} is not allowed for your team. Do not try to work around it.";
            case Decisions.Ask:
                if (reason is null) throw new ArgumentException($"this call asks the person first: give a reason ({ReasonProperty}, one line)");
                var id = Scalar(
                    """
                    INSERT INTO integration_asks (at, integration, team_id, member, args, reason, workflow, causation_seq, action)
                    VALUES ($at, $i, $t, $m, $a, $r, $w, $c, $action) RETURNING id;
                    """,
                    ("$at", Now()), ("$i", integration), ("$t", run.Member.TeamId), ("$m", run.Member.Qualified), ("$a", JsonSerializer.Serialize(callArgs)),
                    ("$r", Clip(reason, 500)), ("$w", run.Correlation), ("$c", run.Causation), ("$action", tool));
                Insert(integration, run.Member.Team, run.Member.Qualified, run.Correlation, callArgs, "asked", rule?.Pattern, null, Provenance.Actor.Current);
                return $"asked: {server.Name}'s {tool} waits for the person's approval (request {id}). You will be woken with the result in this workflow. Do other work or end your turn; do not ask again.";
            default:
                var text = await RunMcpAsync(server, tool, clean, ct);
                Insert(integration, run.Member.Team, run.Member.Qualified, run.Correlation, callArgs, Decisions.Allow, rule?.Pattern, null, Provenance.Actor.Current);
                return text;
        }
    }

    private async Task<string> RunMcpAsync(McpServerRow server, string tool, JsonObject args, CancellationToken ct)
    {
        try
        {
            var (text, isError) = await WithRefreshAsync(server, () => Client(server).CallToolAsync(tool, args, ct), ct);
            McpWorks(server);
            text = CliRunner.Redact(text);
            if (text.Length > CliRunner.MaxOutputChars) text = text[..CliRunner.MaxOutputChars] + "\n(output cut at 64 KB)";
            return (isError ? "the tool reported an error:\n" : "") + text;
        }
        catch (McpException e)
        {
            var said = McpFailure(server, e);
            return e is McpUnauthorizedException or McpNeedsConnectionException ? said : $"the MCP server {server.Name} failed: {Clip(said, 600)}";
        }
        catch (ObjectDisposedException)
        {
            return $"the MCP server {server.Name} was changed while this call ran; try again";
        }
    }

    /// <summary>An approved request for an MCP tool: run it now (args were [tool, jsonArguments]).</summary>
    private async Task<string> RunApprovedMcpAsync(AskRow ask, CancellationToken ct)
    {
        var server = McpRow(ask.Integration["mcp:".Length..]) ?? throw new ArgumentException($"{ask.Integration} is no longer there");
        var args = ask.Args.Count > 1 ? JsonNode.Parse(ask.Args[1]) as JsonObject ?? [] : [];
        return await RunMcpAsync(server, ask.Args[0], args, ct);
    }

    private IMcpClient Client(McpServerRow s) => _clients.GetOrAdd(s.Name, _ => s.Transport == "stdio"
        ? new McpClient(s.Command, s.Args, McpEnvFor(s))
        : BuildHttpClient(s));

    /// <summary>A remote client whose headers are bound to the address and Connection it was built for: once the row says otherwise, it gets no sign-in to send.</summary>
    private McpHttpClient BuildHttpClient(McpServerRow s)
    {
        var url = s.Url ?? throw new McpException($"{s.Name} has no address");
        var boundConnection = s.ConnectionId;
        SecretScrubber.Set(_mcpScrubOwner, s.Id, McpImporter.ArgSecretValues(s.Args, s.Url, s.Env));
        return new McpHttpClient(new Uri(url), ct => McpHeadersAsync(s.Name, url, boundConnection, ct), McpHandler());
    }

    /// <summary>Raised before anything is sent when a remote server needs a sign-in the person has not set.</summary>
    private sealed class McpNeedsConnectionException(string message) : McpException(message);

    /// <summary>The server answered: its Connection works now (cleared from needs reconnect; the change is announced only when the status actually changes).</summary>
    private void McpWorks(McpServerRow s)
    {
        if (s.Transport != "http" || s.ConnectionId is not { } cid || connections?.Brief(cid) is not { } b || b.Status == "works") return;
        connections.MarkStatus(cid, "works", $"{s.Name} answered at {Stamp()}");
    }

    private const string McpPlace = "Settings → Integrations → MCP servers";

    /// <summary>
    /// The sign-in headers for one request, read fresh each time from the server's Connection (so a re-entered token is used at once; the
    /// row is re-read too). The values are registered with the scrubber. Never logged.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> McpHeadersAsync(string name, string? boundUrl, long? boundConnection, CancellationToken ct)
    {
        var s = McpRow(name) ?? throw new McpException($"no MCP server named '{name}'");
        // this client was made for another address or Connection than the row has now (the server was edited): the old sign-in must not go to the new address, nor the new one to the old
        if (!string.Equals(s.Url, boundUrl, StringComparison.Ordinal) || s.ConnectionId != boundConnection)
            throw new McpNeedsConnectionException($"{s.Name} was changed while this call ran; try again.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var type = s.Auth?.Type ?? "none";
        if (type != "none")
        {
            string? value = null;
            if (type == "oauth")
            {
                // refreshed on the host shortly before it expires; the token sent is recorded for this call so a refusal refreshes only that one
                if (s.ConnectionId is not { } oid || connections?.Get(oid) is not { } ov) throw new McpNeedsConnectionException($"{s.Name} needs a connection: set its sign-in in {McpPlace}.");
                // a sign-in is for the address it was made for: after the server's address changed, its token is not sent there (nothing is marked)
                if (!McpOAuth.SameResource(ov.Fields.FirstOrDefault(f => f.Key == "resource" && !f.Secret)?.Shown, s.Url))
                    throw new McpNeedsConnectionException($"{s.Name} needs you to sign in again: its address changed since it was signed in; {NeedsSignInHint}.");
                try { value = await OAuth.AccessTokenAsync(oid, ct); }
                catch (Credentials.CredentialStoreException) { throw new McpNeedsConnectionException($"{s.Name} needs a connection: set its sign-in in {McpPlace}."); }
                if (_sentToken.Value is { } box) box.Value = value;
            }
            else
            {
                try { if (s.ConnectionId is { } cid && Conns.Secrets(cid).TryGetValue("token", out var v) && v.Length > 0) value = v; }
                catch (Credentials.CredentialStoreException) { /* as not set */ }
            }
            if (value is null) throw new McpNeedsConnectionException($"{s.Name} needs a connection: set its sign-in in {McpPlace}.");
            if (type == "header") headers[s.Auth!.Header ?? "Authorization"] = value;
            else headers["Authorization"] = "Bearer " + value;
        }
        return headers;
    }

    /// <summary>The access token the current call last sent (set by <see cref="WithRefreshAsync"/> for its call only; host memory, never logged).</summary>
    private readonly AsyncLocal<System.Runtime.CompilerServices.StrongBox<string?>?> _sentToken = new();

    /// <summary>
    /// A server signed in through the browser that refuses its token gets one refresh and one retry; a refusal after that (or a refresh the
    /// sign-in server refuses) is the usual needs reconnect.
    /// </summary>
    private async Task<T> WithRefreshAsync<T>(McpServerRow s, Func<Task<T>> op, CancellationToken ct)
    {
        var sent = new System.Runtime.CompilerServices.StrongBox<string?>();
        _sentToken.Value = sent;
        try { return await op(); }
        catch (McpUnauthorizedException e) when (e is not McpSignInExpiredException && e.Status != 403 && s.Auth?.Type == "oauth" && s.ConnectionId is { } cid && connections?.Get(cid) is not null)
        {
            await OAuth.RefreshAfterRefusalAsync(cid, sent.Value, ct);
            return await op();
        }
    }

    /// <summary>The scrubber owner of this service's MCP server secrets (row ids are only unique per database, so the owner is this instance's).</summary>
    private readonly object _mcpScrubOwner = new();

    private OAuthFlow? _oauthFlow;
    private McpOAuth? _oauth;
    private readonly Lock _oauthGate = new();

    /// <summary>
    /// The browser sign-in and its refreshes, through the same non-redirecting handler as the servers: one flow for MCP servers and APIs alike,
    /// so a Connection they share is refreshed once at a time.
    /// </summary>
    private OAuthFlow OAuthFlow
    {
        get
        {
            lock (_oauthGate)
                return _oauthFlow ??= new OAuthFlow(McpHandler(), Conns, browser ?? new NoBrowser(), redirect ?? (() => throw new McpException("signing in through the browser is not available here")), _clock);
        }
    }

    /// <summary>The MCP sign-in (protected-resource metadata, registration) on top of <see cref="OAuthFlow"/>.</summary>
    private McpOAuth OAuth
    {
        get
        {
            var flow = OAuthFlow;
            lock (_oauthGate)
                return _oauth ??= new McpOAuth(flow);
        }
    }

    private sealed class NoBrowser : IBrowser
    {
        public void Open(Uri url) => throw new McpException("signing in through the browser is not available here");
    }

    /// <summary>What a refused server with no sign-in says, so the screen offers "The server signs me in" (DiscoverService.ToJson marks it needsSignIn).</summary>
    public const string NeedsSignInHint = "choose \"The server signs me in\"";

    private readonly ConcurrentDictionary<string, string> _challenges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _signingIn = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// integrations.mcpSignIn: the browser sign-in for a remote server. On success the server's sign-in is "oauth" with the new (or renewed)
    /// Connection, which is marked works. Uses what the server's last refusal said (WWW-Authenticate) to find where to sign in, if it said anything.
    /// </summary>
    private async Task<JsonNode> McpSignInAsync(string name, CancellationToken ct)
    {
        var s = McpRow(name) ?? throw new ArgumentException($"no MCP server named '{name}'");
        if (s.Transport != "http" || s.Url is null) throw new ArgumentException($"{s.Name} runs on this computer: only a remote server signs you in");
        if (!_signingIn.TryAdd(s.Name, 0)) throw new ArgumentException($"a sign-in for {s.Name} is already open in your browser: finish it there");
        try
        {
            // renew the server's own browser sign-in in place; any other Connection (a token, one shared with others) is left alone
            long? existing = s.ConnectionId is { } cid && Conns.Get(cid) is { Kind: "oauth" } v
                && v.UsedBy.All(u => string.Equals(u, "MCP: " + s.Name, StringComparison.OrdinalIgnoreCase)) ? cid : null;
            _challenges.TryGetValue(s.Name, out var www);
            long id;
            try { id = await OAuth.SignInAsync(s.Name, new Uri(s.Url), www, existing, ct); }
            catch (McpException e) { throw new ArgumentException(CliRunner.Redact(e.Message)); }
            Exec("UPDATE mcp_servers SET auth = $au, connection_id = $cid, status = NULL, updated_at = $now WHERE id = $id;",
                ("$au", McpAuthJson(new McpAuth("oauth", null)).ToJsonString()), ("$cid", id), ("$now", Now()), ("$id", s.Id));
            DropOrphanedConnection(s.Name, s.ConnectionId, id);
            _challenges.TryRemove(s.Name, out _);
            if (_clients.TryRemove(s.Name, out var old)) await old.DisposeAsync();
            Conns.MarkStatus(id, "works", $"{s.Name}: signed in at {Stamp()}");
            var b = Conns.Brief(id)!;
            return new JsonObject
            {
                ["ok"] = true,
                ["connection"] = new JsonObject { ["id"] = b.Id, ["name"] = b.Name, ["status"] = b.Status, ["statusReason"] = b.StatusReason },
            };
        }
        finally { _signingIn.TryRemove(s.Name, out _); }
    }

    /// <summary>
    /// What a failed call or check says (scrubbed). A refused sign-in marks the server's Connection "needs reconnect" (the screen and Needs You
    /// show it), drops the cached client, and tells the agent to stop; a server with no Connection to mark is told to get one.
    /// </summary>
    private string McpFailure(McpServerRow s, McpException e)
    {
        if (e is not McpUnauthorizedException u) return CliRunner.Redact(e.Message);
        // 403: signed in, but this call is not allowed for this sign-in: the sign-in is fine (nothing is marked, nothing dropped)
        if (u.Status == 403 && u is not McpSignInExpiredException) return $"{s.Name} refused this call (not allowed for this sign-in).";
        if (_clients.TryRemove(s.Name, out var old)) _ = old.DisposeAsync().AsTask();
        // kept (in memory only, never logged) for a sign-in that follows: it may say where the server's sign-in is
        if (u.WwwAuthenticate is { Length: > 0 and <= 4096 } www) _challenges[s.Name] = www;
        if (s.Transport == "http" && (s.Auth?.Type ?? "none") == "none")
            return $"{s.Name} asks you to sign in: {NeedsSignInHint} and sign in in your browser.";
        if (s.ConnectionId is { } cid && connections?.Get(cid) is { } view)
        {
            // a refresh that was refused has already marked it, with its own reason
            if (view.Status != "needs_reconnect") connections.MarkStatus(cid, "needs_reconnect", $"{s.Name} refused the sign-in");
            return $"{s.Name} needs you to sign in again (Connection {view.Name}): stop and tell the person; do not retry.";
        }
        return $"{s.Name} needs a connection: set its sign-in in {McpPlace}.";
    }

    private SocketsHttpHandler? _mcpHandler;
    private readonly Lock _mcpHandlerGate = new();

    /// <summary>The handler remote MCP servers go through: the test seam, or one shared, long-lived handler that never follows a redirect (the sign-in must not leave the server's address) and keeps no cookies.</summary>
    private HttpMessageHandler McpHandler()
    {
        if (mcpHandler is not null) return mcpHandler();
        lock (_mcpHandlerGate)
            return _mcpHandler ??= new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients.Values) await c.DisposeAsync();
        _clients.Clear();
        _apiHandler?.Dispose();
        _mcpHandler?.Dispose();
    }

    // ------------------------------------------------------------------ store

    private IReadOnlyList<McpServerRow> McpRows()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, command, args, tools, status, enabled_all, transport, url, auth, env, connection_id, sources FROM mcp_servers ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var list = new List<McpServerRow>();
        while (r.Read())
            list.Add(new McpServerRow(r.GetInt64(0), r.GetString(1), r.GetString(2), JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? [],
                (r.IsDBNull(4) ? null : JsonNode.Parse(r.GetString(4)) as JsonArray) ?? [], r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6) == 1,
                r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : McpAuthOf(r.GetString(9)),
                (JsonNode.Parse(r.GetString(10)) as JsonArray ?? []).OfType<JsonObject>()
                    .Select(o => new McpEnvVar(OptStr(o, "name") ?? "", o["value"] is JsonValue ev && ev.TryGetValue<string>(out var evs) ? evs : null, o["secret"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb)).ToList(),
                r.IsDBNull(11) ? null : r.GetInt64(11),
                r.IsDBNull(12) ? [] : (JsonNode.Parse(r.GetString(12)) as JsonArray ?? []).Select(x => x?.ToString() ?? "").ToList()));
        return list;
    }

    private static McpAuth? McpAuthOf(string json) =>
        JsonNode.Parse(json) is JsonObject o && OptStr(o, "type") is { } t ? new McpAuth(t, OptStr(o, "header")) : null;

    private McpServerRow? McpRow(string name) => McpRows().FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<long> McpTeamsOf(long serverId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT team_id FROM mcp_server_teams WHERE server_id = $s ORDER BY team_id;";
        cmd.Parameters.AddWithValue("$s", serverId);
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    private IReadOnlyList<CliRule> McpRules(long serverId, long? teamId) =>
        McpRules(serverId).Where(r => r.TeamId == teamId).Select(r => r.Rule with { Layer = teamId is null ? "global" : "team" }).ToList();

    private IReadOnlyList<(CliRule Rule, long? TeamId)> McpRules(long serverId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, team_id, pattern, decision FROM mcp_rules WHERE server_id = $s ORDER BY id;";
        cmd.Parameters.AddWithValue("$s", serverId);
        using var r = cmd.ExecuteReader();
        var list = new List<(CliRule, long?)>();
        while (r.Read()) list.Add((new CliRule(r.GetString(2), r.GetString(3), "", r.GetInt64(0)), r.IsDBNull(1) ? null : r.GetInt64(1)));
        return list;
    }
}
