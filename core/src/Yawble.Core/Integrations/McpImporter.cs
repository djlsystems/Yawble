using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yawble.Core.Integrations;

/// <summary>A config file of another app that may list MCP servers. Shape: "mcpServers", "claudeCode" or "vscode".</summary>
public sealed record McpConfigSource(string App, string Path, string Shape);

/// <summary>
/// A server found in another app's config. Secrets hold the values (host only: never sent to a screen in full); Env's secret entries carry no value.
/// ArgsShown, UrlShown and EnvShown are what a screen may see (secrets in args, addresses and plain values masked); Hidden lists the raw values masked there.
/// </summary>
public sealed record McpCandidate(string Key, string Name, string Transport, string? Command, IReadOnlyList<string> Args, string? Url,
    IReadOnlyList<McpEnvVar> Env, IReadOnlyDictionary<string, string> Secrets, IReadOnlyList<string> Sources, string? AlreadyAdded, McpAuth? Auth = null,
    string? Note = null, IReadOnlyList<string>? ArgsShown = null, string? UrlShown = null, IReadOnlyDictionary<string, string>? EnvShown = null,
    IReadOnlyList<string>? Hidden = null);

/// <summary>Finds MCP servers other apps on this computer already know, reading their config files read-only (never writing, never moving).</summary>
public static class McpImporter
{
    public static IReadOnlyList<McpConfigSource> DefaultSources()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (home.Length == 0) return [];
        var list = new List<McpConfigSource> { new("Claude Code", Path.Combine(home, ".claude.json"), "claudeCode") };
        if (appData.Length > 0) list.Add(new("Claude Desktop", Path.Combine(appData, "Claude", "claude_desktop_config.json"), "mcpServers"));
        list.Add(new("Cursor", Path.Combine(home, ".cursor", "mcp.json"), "mcpServers"));
        if (appData.Length > 0)
        {
            list.Add(new("VS Code", Path.Combine(appData, "Code", "User", "mcp.json"), "vscode"));
            list.Add(new("VS Code", Path.Combine(appData, "Code", "User", "settings.json"), "vscode"));
        }
        return list;
    }

    private static readonly HashSet<string> SecretParts = new(StringComparer.Ordinal)
    {
        "KEY", "KEYS", "TOKEN", "TOKENS", "SECRET", "SECRETS", "PASSWORD", "PASSWD", "PASS", "PAT", "CREDENTIAL", "CREDENTIALS", "AUTH", "BEARER", "PRIVATE",
    };
    private static readonly string[] SecretSuffixes = ["APIKEY", "ACCESSKEY", "SECRETKEY", "PRIVATEKEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD"];

    /// <summary>Whether a name carries a secret: a whole word part (split at _ - . and camelCase) is key, token, secret, password, pass, pat, credential, auth, bearer or private (KEYBOARD and MONKEY are not).</summary>
    public static bool NameLooksSecret(string? name)
    {
        var spaced = Regex.Replace(name ?? "", "([a-z0-9])([A-Z])", "$1_$2").ToUpperInvariant();
        foreach (var part in Regex.Split(spaced, "[^A-Z0-9]+"))
            if (part.Length > 0 && (SecretParts.Contains(part) || SecretSuffixes.Any(x => part.Length >= x.Length && part.EndsWith(x, StringComparison.Ordinal)))) return true;
        return false;
    }

    /// <summary>Whether a variable looks like a secret by its name, or its value is a placeholder to be filled in later.</summary>
    public static bool LooksSecret(string name, string? value) => NameLooksSecret(name) || IsPlaceholder(DefaultOf(value) ?? value);

    /// <summary>"${VAR:-default}" gives the default; anything else null.</summary>
    internal static string? DefaultOf(string? v)
    {
        var m = v is null ? null : Regex.Match(v.Trim(), @"^\$\{[^}:]+:-(.*)\}$", RegexOptions.Singleline);
        return m is { Success: true } ? m.Groups[1].Value : null;
    }

    /// <summary>A value still to be filled in: "&lt;...&gt;", a whole "${VAR}", YOUR_..., xxx... ("${HOME}/x" and "${VAR:-default}" are plain values).</summary>
    internal static bool IsPlaceholder(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        v = v.Trim();
        return (v.Length > 2 && v[0] == '<' && v[^1] == '>') || (Regex.IsMatch(v, @"^\$\{[^}]*\}$") && DefaultOf(v) is null)
            || v.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(v, "^[xX]{3,}$");
    }

    private const long MaxFileBytes = 32L * 1024 * 1024;

    // A random key for this run: a candidate's key is stable between a scan and its import, but reveals nothing of the secrets it covers.
    private static readonly byte[] KeySalt = RandomNumberGenerator.GetBytes(32);

    // ---- masking: what a screen may be shown (the real values stay on the host)

    private static string Mask(string v) => Connections.ConnectionService.Mask(v);

    private static void AddUser(List<string> raw, string userInfo)
    {
        raw.Add(userInfo);
        if (userInfo.IndexOf(':') is var c and >= 0 && c + 1 < userInfo.Length) raw.Add(userInfo[(c + 1)..]);
    }

    private static readonly Regex ConnString = new(@"://[^/\s:@]+:[^@\s/]+@");

    /// <summary>Text with a "://user:pass@" part masked.</summary>
    private static string MaskConn(string text, List<string> raw) =>
        Regex.Replace(text, @"(://)([^/\s:@]+:[^@\s/]+)@", m => { AddUser(raw, m.Groups[2].Value); return m.Groups[1].Value + Mask(m.Groups[2].Value) + "@"; });

    /// <summary>Args with the value after a secret-looking flag (--api-key x, --token=x) and NAME=value secrets masked; raw collects what was hidden.</summary>
    internal static List<string> MaskArgs(IReadOnlyList<string> args, List<string> raw)
    {
        var shown = new List<string>();
        var maskNext = false;
        foreach (var a in args)
        {
            if (maskNext && !a.StartsWith('-')) { raw.Add(a); shown.Add(Mask(a)); maskNext = false; continue; }
            maskNext = false;
            var eq = a.IndexOf('=');
            if (a.StartsWith('-'))
            {
                var flag = (eq < 0 ? a : a[..eq]).TrimStart('-');
                if (NameLooksSecret(flag))
                {
                    if (eq < 0) { maskNext = true; shown.Add(a); continue; }
                    if (eq + 1 < a.Length) { raw.Add(a[(eq + 1)..]); shown.Add(a[..(eq + 1)] + Mask(a[(eq + 1)..])); continue; }
                }
            }
            else if (eq > 0 && eq + 1 < a.Length && Regex.IsMatch(a[..eq], "^[A-Za-z_][A-Za-z0-9_]*$") && LooksSecret(a[..eq], a[(eq + 1)..]))
            { raw.Add(a[(eq + 1)..]); shown.Add(a[..(eq + 1)] + Mask(a[(eq + 1)..])); continue; }
            shown.Add(ConnString.IsMatch(a) ? MaskConn(a, raw) : a);
        }
        return shown;
    }

    /// <summary>A remote address with its user:password and secret-looking query values masked.</summary>
    internal static string MaskUrl(string url, List<string> raw)
    {
        var s = Regex.Replace(url, @"^([A-Za-z][A-Za-z0-9+.-]*://)([^/?#@]+)@", m => { AddUser(raw, m.Groups[2].Value); return m.Groups[1].Value + Mask(m.Groups[2].Value) + "@"; });
        return Regex.Replace(s, @"([?&])([^=&#]+)=([^&#]*)", m =>
        {
            if (m.Groups[3].Length == 0 || !NameLooksSecret(Uri.UnescapeDataString(m.Groups[2].Value))) return m.Value;
            raw.Add(m.Groups[3].Value);
            return m.Groups[1].Value + m.Groups[2].Value + "=" + Mask(m.Groups[3].Value);
        });
    }

    /// <summary>The secrets that stay in a server's args, address and plain values as written (the host keeps them out of agent output).</summary>
    public static IReadOnlyList<string> ArgSecretValues(IReadOnlyList<string> args, string? url, IEnumerable<McpEnvVar>? env = null)
    {
        var raw = new List<string>();
        MaskArgs(args, raw);
        if (url is not null) MaskUrl(url, raw);
        foreach (var v in env ?? [])
            if (!v.Secret && v.Value is { } val && ConnString.IsMatch(val)) MaskConn(val, raw);
        return raw;
    }

    // ---- identity

    private static string NormUrl(string? url)
    {
        if (url is not null && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && u.Host.Length > 0)
            return $"{u.Scheme.ToLowerInvariant()}://{u.UserInfo}{(u.UserInfo.Length > 0 ? "@" : "")}{u.Host.ToLowerInvariant()}:{u.Port}{u.AbsolutePath.TrimEnd('/')}{u.Query}";
        return (url ?? "").Trim().TrimEnd('/');
    }

    internal static string Identity(bool http, string? command, IEnumerable<string> args, string? url, IEnumerable<McpEnvVar> env, string? authHeader)
    {
        if (http) return "http\n" + NormUrl(url) + "\n" + (authHeader ?? "").ToLowerInvariant();
        var cmd = (command ?? "").Trim();
        if (OperatingSystem.IsWindows()) cmd = cmd.ToLowerInvariant();
        var plain = env.Where(v => !v.Secret).Select(v => v.Name + "=" + v.Value).Order(StringComparer.Ordinal);
        return "stdio\n" + cmd + "\n" + string.Join('\u0001', args) + "\n" + string.Join('\u0001', plain);
    }

    public static IReadOnlyList<McpCandidate> Scan(IEnumerable<McpConfigSource> sources, IReadOnlyList<McpServerRow> existing)
    {
        var found = new List<McpCandidate>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var src in sources)
        {
            var root = Read(src.Path);
            if (root is null) continue;
            foreach (var (name, node) in Servers(root, src.Shape))
            {
                var c = Candidate(name, node, src.App, existing);
                if (c is null) continue;
                if (byKey.TryGetValue(c.Key, out var at))
                {
                    var have = found[at];
                    if (!have.Sources.Contains(src.App)) found[at] = have with { Sources = [.. have.Sources, src.App] };
                }
                else { byKey[c.Key] = found.Count; found.Add(c); }
            }
        }
        return found;
    }

    private static JsonObject? Read(string path)
    {
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > MaxFileBytes })
            {
                System.Diagnostics.Trace.TraceWarning("MCP import: skipped a config file over 32 MB");
                return null;
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonNode.Parse(fs, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Servers(JsonObject root, string shape)
    {
        var objects = new List<JsonObject?>();
        switch (shape)
        {
            case "claudeCode":
                objects.Add(root["mcpServers"] as JsonObject);
                if (root["projects"] is JsonObject projects)
                    foreach (var (_, p) in projects) objects.Add((p as JsonObject)?["mcpServers"] as JsonObject);
                break;
            case "vscode":
                objects.Add(root["servers"] as JsonObject);
                objects.Add(root["mcp.servers"] as JsonObject);
                objects.Add((root["mcp"] as JsonObject)?["servers"] as JsonObject);
                break;
            default:
                objects.Add(root["mcpServers"] as JsonObject);
                break;
        }
        foreach (var o in objects)
            if (o is not null)
                foreach (var kv in o) yield return kv;
    }

    private static string? Text(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : null;

    internal static McpCandidate? Candidate(string name, JsonNode? node, string app, IReadOnlyList<McpServerRow> existing)
    {
        if (node is not JsonObject o) return null;
        var type = (Text(o["type"]) ?? Text(o["transport"]))?.Trim().ToLowerInvariant();
        var url = Text(o["url"])?.Trim();
        var remote = type is "http" or "streamable-http" or "sse" || (type is null or "" && !string.IsNullOrEmpty(url));
        var secrets = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var env = new List<McpEnvVar>();
        var envShown = new Dictionary<string, string>(StringComparer.Ordinal);
        var raw = new List<string>();
        string? command = null, note = null, urlShown = null;
        List<string> args = [], argsShown = [];
        McpAuth? auth = null;
        if (remote)
        {
            if (string.IsNullOrEmpty(url)) return null;
            urlShown = MaskUrl(url, raw);
            if (o["headers"] is JsonObject headers)
            {
                var all = headers.Select(kv => (Name: kv.Key, Value: Text(kv.Value)?.Trim() ?? "")).Where(h => h.Value.Length > 0).ToList();
                var pick = all.FirstOrDefault(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
                if (pick.Name is null) pick = all.FirstOrDefault(h => NameLooksSecret(h.Name));
                if (pick.Name is not null)
                {
                    var bearer = pick.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && pick.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
                    var token = bearer ? pick.Value[7..].Trim() : pick.Value;
                    auth = bearer ? new McpAuth("bearer", null) : new McpAuth("header", pick.Name);
                    if (!IsPlaceholder(token)) secrets["token"] = token;
                }
                var ignored = all.Where(h => h.Name != pick.Name).Select(h => h.Name).ToList();
                if (ignored.Count > 0) note = "Other headers were not imported: " + string.Join(", ", ignored);
            }
        }
        else
        {
            command = Text(o["command"])?.Trim();
            if (string.IsNullOrEmpty(command)) return null;
            args = (o["args"] as JsonArray ?? []).Select(a => Text(a) ?? "").ToList();
            argsShown = MaskArgs(args, raw);
            if (o["env"] is JsonObject eo)
                foreach (var (k, v) in eo)
                {
                    if (!Regex.IsMatch(k, "^[A-Za-z_][A-Za-z0-9_]*$")) continue;
                    var value = Text(v) ?? "";
                    value = DefaultOf(value) ?? value;
                    if (LooksSecret(k, value))
                    {
                        env.Add(new McpEnvVar(k, null, true));
                        if (value.Length > 0 && !IsPlaceholder(value)) secrets[k] = value;
                    }
                    else
                    {
                        env.Add(new McpEnvVar(k, value, false));
                        if (ConnString.IsMatch(value)) envShown[k] = MaskConn(value, raw);
                    }
                }
        }
        var identity = Identity(remote, command, args, url, env, auth?.Header);
        var key = Convert.ToHexStringLower(HMACSHA256.HashData(KeySalt, Encoding.UTF8.GetBytes(
            identity + "\n" + string.Join('\n', secrets.Select(kv => kv.Key + "=" + kv.Value)))));
        var already = existing.FirstOrDefault(s => (s.Transport == "http") == remote
            && Identity(remote, s.Command, s.Args, s.Url, s.Env, s.Auth?.Header) == identity)?.Name;
        return new McpCandidate(key, name, remote ? "http" : "stdio", command, args, remote ? url : null, env, secrets, [app], already, auth,
            note, argsShown, urlShown, envShown, raw);
    }
}
