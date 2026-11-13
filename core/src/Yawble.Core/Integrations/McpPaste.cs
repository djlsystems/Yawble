using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yawble.Core.Integrations;

/// <summary>A server read out of pasted text, ready to fill the Add MCP server form. Values come from the person's own paste and are not saved yet.</summary>
public sealed record McpDraft(string? Name, string Transport, string? Command, IReadOnlyList<string> Args, string? Url, McpAuth? Auth,
    IReadOnlyList<McpDraftEnv> Env, string? Note);

/// <summary>One variable of a draft. Needed: the value is empty or a placeholder, so the person must fill it in.</summary>
public sealed record McpDraftEnv(string Name, string? Value, bool Secret, bool Needed);

/// <summary>Turns pasted text (mcpServers JSON, a "claude mcp add" line, an npx/uvx/node/docker line, or a URL) into drafts. Pure text: no file, network or process access; never throws.</summary>
public static class McpPaste
{
    /// <summary>Input above this many characters is not parsed at all (empty list).</summary>
    public const int MaxChars = 100 * 1024;

    public static IReadOnlyList<McpDraft> Parse(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars) return [];
            text = text.Trim();
            if (text[0] == '{' || (text[0] == '"' && Regex.IsMatch(text, @"^""[^""\r\n]*""\s*:", RegexOptions.NonBacktracking))) return FromJson(text);
            var tokens = Tokenize(JoinContinuations(text));
            if (tokens is null || tokens.Count == 0) return [];
            if (tokens.Count == 1 && IsUrl(tokens[0])) return [UrlDraft(tokens[0])];
            if (tokens.Count >= 3 && tokens[0] == "claude" && tokens[1] == "mcp" && tokens[2] == "add") return Wrap(ClaudeAdd(tokens.Skip(3).ToList()));
            if (ProgramName(tokens[0]) is "npx" or "uvx" or "node" or "docker" or "bunx") return Wrap(Runner(tokens));
            return [];
        }
        catch (Exception) { return []; }
    }

    private static IReadOnlyList<McpDraft> Wrap(McpDraft? d) => d is null ? [] : [d];

    // ---------------------------------------------------------------- JSON

    private static IReadOnlyList<McpDraft> FromJson(string text)
    {
        if (text[0] == '"') text = "{" + text + "}";
        JsonObject? root;
        try { root = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 }) as JsonObject; }
        catch (JsonException) { return []; }
        if (root is null) return [];
        var list = new List<McpDraft>();
        var map = root["mcpServers"] as JsonObject ?? root["servers"] as JsonObject ?? (root["mcp"] as JsonObject)?["servers"] as JsonObject;
        if (map is not null) Add(list, map);
        else if (root["command"] is not null || root["url"] is not null) AddOne(list, null, root);
        else Add(list, root);
        return list;
    }

    private static void Add(List<McpDraft> list, JsonObject map)
    {
        foreach (var (name, node) in map) AddOne(list, name, node);
    }

    private static void AddOne(List<McpDraft> list, string? name, JsonNode? node)
    {
        var c = McpImporter.Candidate(name ?? "", node, "paste", []);
        if (c is null) return;
        var env = new List<McpDraftEnv>();
        foreach (var v in c.Env)
        {
            if (!v.Secret) { env.Add(new(v.Name, v.Value, false, string.IsNullOrEmpty(v.Value))); continue; }
            c.Secrets.TryGetValue(v.Name, out var sv);
            env.Add(new(v.Name, sv, true, string.IsNullOrEmpty(sv)));
        }
        if (c.Auth is not null)
        {
            c.Secrets.TryGetValue("token", out var tv);
            env.Add(new("token", tv, true, string.IsNullOrEmpty(tv)));
        }
        list.Add(new McpDraft(string.IsNullOrEmpty(name) ? null : name, c.Transport, c.Command, c.Args, c.Url, c.Auth, env, null));
    }

    // ---------------------------------------------------------------- shell-ish lines

    /// <summary>Splits on whitespace, respecting single and double quotes. Null for an unterminated quote.</summary>
    private static List<string>? Tokenize(string s)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var have = false;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch is '"' or '\'')
            {
                var end = ch;
                have = true;
                for (i++; ; i++)
                {
                    if (i >= s.Length) return null;
                    if (s[i] == end) break;
                    // \" or `" is an escaped quote, unless the quote ends the token (then a backslash is just a path separator).
                    if (end == '"' && i + 1 < s.Length && s[i + 1] == '"'
                        && (s[i] == '`' || (s[i] == '\\' && i + 2 < s.Length && !char.IsWhiteSpace(s[i + 2])))) i++;
                    sb.Append(s[i]);
                }
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (have) { tokens.Add(sb.ToString()); sb.Clear(); have = false; }
            }
            else { sb.Append(ch); have = true; }
        }
        if (have) tokens.Add(sb.ToString());
        return tokens;
    }

    private static bool IsUrl(string s) => Regex.IsMatch(s, @"^https?://[^\s/]+", RegexOptions.IgnoreCase);

    private static McpDraft UrlDraft(string url, string? name = null, McpAuth? auth = null, IReadOnlyList<McpDraftEnv>? env = null) =>
        new(name ?? NameFromHost(url), "http", null, [], url, auth, env ?? [], null);

    private static string? NameFromHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var labels = u.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var pick = labels.FirstOrDefault(l => l is not ("mcp" or "www" or "api"));
        return pick is null ? null : Clean(pick);
    }

    private static string? Clean(string s)
    {
        var c = Regex.Replace(s, "[^A-Za-z0-9_-]+", "-").Trim('-');
        return c.Length == 0 ? null : c;
    }

    private static McpDraftEnv EnvPair(string name, string? value)
    {
        var secret = McpImporter.LooksSecret(name, value);
        var needed = string.IsNullOrWhiteSpace(value) || McpImporter.IsPlaceholder(value);
        return new McpDraftEnv(name, string.IsNullOrEmpty(value) ? null : value, secret, needed);
    }

    /// <summary>"C:\Program Files\nodejs\node.exe" gives "node".</summary>
    private static string ProgramName(string cmd) => Path.GetFileNameWithoutExtension(cmd.Replace((char)92, (char)47).Split((char)47).Last()).ToLowerInvariant();

    private static bool IsRemote(string? t) => t is "http" or "sse" or "streamable-http";

    private static McpDraft? ClaudeAdd(List<string> t)
    {
        string? transport = null, name = null, url = null;
        var env = new List<McpDraftEnv>();
        var headers = new List<(string H, string V)>();
        List<string> tail = [];
        for (var i = 0; i < t.Count; i++)
        {
            var a = t[i];
            if (a == "--") { tail = t.Skip(i + 1).ToList(); break; }
            string? Val() => i + 1 < t.Count ? t[++i] : null;
            if (a is "--transport" or "-t") transport = Val()?.ToLowerInvariant();
            else if (a.StartsWith("--transport=")) transport = a[12..].ToLowerInvariant();
            else if (a is "--scope" or "-s") Val();
            else if (a is "--env" or "-e" or "--header" or "-H")
            {
                var isEnv = a is "--env" or "-e";
                // The real CLI makes these variadic: keep taking values while they look like pairs (never a URL).
                while (i + 1 < t.Count && !t[i + 1].StartsWith('-') && !IsUrl(t[i + 1])
                    && (isEnv ? Regex.IsMatch(t[i + 1], "^[A-Za-z_][A-Za-z0-9_]*=") : t[i + 1].Contains(':')))
                    AddPair(isEnv, t[++i]);
            }
            else if (a.StartsWith("--env=")) AddPair(true, a[6..]);
            else if (a.StartsWith("--header=")) AddPair(false, a[9..]);
            else if (a.StartsWith('-') && a.Length > 1) { } // --scope=x and other options we ignore
            else if (name is null) name = a;
            else if (url is null && (IsUrl(a) || IsRemote(transport))) url = a;
            else { tail = t.Skip(i).ToList(); break; } // the command and its arguments, verbatim
        }
        void AddPair(bool isEnv, string kv)
        {
            if (isEnv)
            {
                var eq = kv.IndexOf('=');
                if (eq > 0) env.Add(EnvPair(kv[..eq].Trim(), kv[(eq + 1)..]));
            }
            else
            {
                var c = kv.IndexOf(':');
                if (c > 0) headers.Add((kv[..c].Trim(), kv[(c + 1)..].Trim()));
            }
        }
        if (tail.Count == 0 && url is not null && (transport is null || IsRemote(transport)))
        {
            McpAuth? auth = null;
            if (headers.Count > 0)
            {
                var (h, v) = headers[0];
                var bearer = h.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
                var token = bearer ? v[7..].Trim() : v;
                auth = bearer ? new McpAuth("bearer", null) : new McpAuth("header", h);
                var needed = token.Length == 0 || McpImporter.IsPlaceholder(token);
                env.Add(new McpDraftEnv("token", needed ? null : token, true, needed));
            }
            return UrlDraft(url, Clean(name ?? "") ?? NameFromHost(url), auth, env);
        }
        if (tail.Count == 0) return null;
        return new McpDraft(Clean(name ?? ""), "stdio", tail[0], tail.Skip(1).ToList(), null, null, env, null);
    }

    /// <summary>Joins lines ending in a backtick or backslash (PowerShell / sh continuation) in one linear pass.</summary>
    private static string JoinContinuations(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is '`' or '\\')
            {
                var j = i + 1;
                while (j < s.Length && s[j] is ' ' or '\t') j++;
                if (j < s.Length && (s[j] == '\n' || (s[j] == '\r' && j + 1 < s.Length && s[j + 1] == '\n')))
                {
                    j += s[j] == '\r' ? 2 : 1;
                    while (j < s.Length && s[j] is ' ' or '\t') j++;
                    while (sb.Length > 0 && sb[^1] is ' ' or '\t') sb.Length--;
                    sb.Append(' ');
                    i = j - 1;
                    continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static McpDraft? Runner(List<string> t)
    {
        var cmd = t[0];
        var args = t.Skip(1).ToList();
        if (args.Count == 0) return null;
        string? name = null;
        switch (ProgramName(cmd))
        {
            case "npx" or "uvx" or "bunx":
                name = PackageName(args.FirstOrDefault(a => !a.StartsWith('-')));
                break;
            case "node":
                var script = args.FirstOrDefault(a => !a.StartsWith('-'));
                if (script is null) return null;
                var file = script.Replace('\\', '/').Split('/').Last();
                var stem = Path.GetFileNameWithoutExtension(file);
                // index.js / server.js say nothing: use the folder instead.
                if (stem is "index" or "server" or "main" or "cli" or "mcp")
                {
                    var parts = script.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var dir = parts.Length >= 2 ? parts[^2] : null;
                    if (dir is "dist" or "build" or "src" or "lib" && parts.Length >= 3) dir = parts[^3];
                    name = PackageName(dir);
                }
                else name = PackageName(stem);
                break;
            case "docker":
                if (args[0] != "run") return null;
                string[] withValue = ["-e", "--env", "-v", "--volume", "-p", "--publish", "--name", "--network", "-w", "--workdir", "-u", "--user", "--env-file", "--entrypoint", "--platform", "--mount"];
                string? image = null;
                for (var i = 1; i < args.Count; i++)
                {
                    if (args[i].StartsWith('-')) { if (withValue.Contains(args[i])) i++; continue; }
                    image = args[i]; break;
                }
                if (image is null) return null;
                name = PackageName(image);
                break;
        }
        return new McpDraft(name, "stdio", cmd, args, null, null, [], null);
    }

    /// <summary>"@modelcontextprotocol/server-github" gives "github"; "mcp-server-time@1.2" gives "time"; "mcp/fetch:latest" gives "fetch".</summary>
    private static string? PackageName(string? pkg)
    {
        if (string.IsNullOrWhiteSpace(pkg)) return null;
        var s = pkg.Trim();
        var at = s.LastIndexOf('@');
        if (at > 0) s = s[..at];
        s = s[(s.LastIndexOf('/') + 1)..];
        var colon = s.IndexOf(':');
        if (colon > 0) s = s[..colon];
        s = Regex.Replace(s, @"\.(js|mjs|cjs|py|ts)$", "", RegexOptions.IgnoreCase);
        string[] prefixes = ["mcp-server-", "server-", "mcp-"];
        string[] suffixes = ["-mcp-server", "-server", "-mcp"];
        foreach (var p in prefixes) if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase) && s.Length > p.Length) { s = s[p.Length..]; break; }
        foreach (var x in suffixes) if (s.EndsWith(x, StringComparison.OrdinalIgnoreCase) && s.Length > x.Length) { s = s[..^x.Length]; break; }
        return Clean(s);
    }
}
