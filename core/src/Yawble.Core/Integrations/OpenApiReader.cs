using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace Yawble.Core.Integrations;

/// <summary>One operation of an API: its id, HTTP method and route, a JSON-schema object for the parameters, and its class.</summary>
public sealed record ApiAction(string Id, string Method, string Route, string? Tag, string Summary, JsonObject Params, Classification Class,
    IReadOnlyList<string>? ScopesOrNull = null)
{
    /// <summary>The OAuth scopes the operation needs (its security entry for the API's scheme, else the document's); empty when none are known.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = ScopesOrNull ?? [];
}

/// <summary>
/// How the API wants to be signed in: bearer, header, query, basic or oauth2. Name is the header or query-parameter name. For oauth2 the
/// https authorize and token addresses and the scopes (name to description) come from the spec's authorization-code flow, or only
/// OpenIdConnectUrl does for an openIdConnect scheme (the addresses are then found at sign-in).
/// </summary>
public sealed record ApiAuth(string Type, string? Name, string? Description, string? AuthorizeUrl = null, string? TokenUrl = null,
    IReadOnlyDictionary<string, string>? Scopes = null, string? OpenIdConnectUrl = null);

public sealed record ApiSpec(string? Title, string? ServerUrl, IReadOnlyList<ApiAction> Actions, ApiAuth? Auth, int Dropped);

/// <summary>
/// Turns an OpenAPI 3.x or Swagger 2.0 document (JSON or YAML, as text) into actions. Pure: it never fetches anything,
/// remote $refs become {} and a spec url is only used to resolve a relative server url.
/// </summary>
public static class OpenApiReader
{
    public const int MaxActions = 2000;
    private const int MaxRefDepth = 8;
    private const int MaxYamlNodes = 200_000;
    private const int MaxFlowDepth = 200, MaxBlockDepth = 400, MaxParamsNodes = 5000, MaxParamsDepth = 32;
    private const string TooDeep = "the spec is too deeply nested to read";
    private const string TooLarge = "the spec is too large or uses too many YAML aliases to read";
    private static readonly string[] Methods = ["get", "head", "post", "put", "patch", "delete"];
    private static readonly HashSet<string> CredentialSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "tokens", "token", "secrets", "secret", "credentials", "api-keys", "api_keys", "apikeys", "access-tokens", "passwords", "password",
    };

    /// <summary>True when the text is JSON or YAML with a top-level openapi or swagger key. Throws ArgumentException only for hostile input (too deep, too large).</summary>
    public static bool LooksLikeSpec(string text)
    {
        try { return Parse(text) is not null; }
        catch (HostileSpecException) { throw; }
        catch (ArgumentException) { return false; }
    }

    public static ApiSpec Read(string text, Uri? specUrl)
    {
        var root = Parse(text) ?? throw new ArgumentException("This is not an OpenAPI or Swagger spec: it has no top-level \"openapi\" or \"swagger\" key.");
        var v2 = root["swagger"] != null && root["openapi"] == null;
        var title = (root["info"] as JsonObject)?["title"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : null;
        var (auth, schemeName) = ReadAuth(root, specUrl);
        var skipHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Authorization" };
        var skipQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (auth is { Type: "header", Name: { } hn }) skipHeaders.Add(hn);
        if (auth is { Type: "query", Name: { } qn }) skipQueries.Add(qn);

        var ops = new List<(string Method, string Route, JsonObject Op, JsonObject PathItem)>();
        if (root["paths"] is JsonObject paths)
            foreach (var (route, pn) in paths)
            {
                if (Deref(root, pn) is not JsonObject item) continue;
                foreach (var m in item)
                    if (Array.IndexOf(Methods, m.Key.ToLowerInvariant()) >= 0 && m.Value is JsonObject op && m.Key == m.Key.ToLowerInvariant())
                        ops.Add((m.Key.ToUpperInvariant(), route, op, item));
            }

        var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in ops)
            if (OperationId(o.Op) is { } oid) idCounts[oid] = idCounts.GetValueOrDefault(oid) + 1;

        // the document's own scopes for the scheme, once; null when the API is not OAuth 2
        var topScopes = auth is { Type: "oauth2" } && schemeName != null ? SecurityScopes(root["security"], schemeName) ?? [] : null;
        var actions = new List<ApiAction>();
        foreach (var (method, route, op, item) in ops.Take(MaxActions))
        {
            var oid = OperationId(op);
            var id = oid != null && idCounts[oid] == 1 ? oid : $"{method} {route}";
            var tag = op["tags"] is JsonArray ta && ta.Count > 0 && ta[0] is JsonValue tv && tv.TryGetValue<string>(out var tg) ? tg : null;
            actions.Add(new ApiAction(id, method, route, tag, Summary(op), BuildParams(root, v2, op, item, skipHeaders, skipQueries), Classify(method, route),
                topScopes is not null ? OperationScopes(op, topScopes, schemeName!) : []));
        }
        return new ApiSpec(title, ServerUrl(root, v2, specUrl), actions, auth, Math.Max(0, ops.Count - MaxActions));
    }

    // ---- parsing -------------------------------------------------------------------------------------------------------------

    private sealed class HostileSpecException(string message) : ArgumentException(message);

    /// <summary>The spec object, or null when the text parses but is not a spec. Throws ArgumentException (a plain sentence) otherwise.</summary>
    private static JsonObject? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        Guard(trimmed);
        JsonNode? node;
        try
        {
            node = ParseOnBigStack(trimmed);
        }
        catch (JsonException e) when (e.Message.Contains("maximum configured depth", StringComparison.OrdinalIgnoreCase)) { throw new HostileSpecException(TooDeep); }
        catch (JsonException e) { throw new ArgumentException("the spec could not be read as YAML or JSON: " + Short(e.Message)); }
        catch (YamlDotNet.Core.YamlException e)
        {
            if (trimmed.StartsWith('<')) return null; // an HTML page, not a spec
            throw new ArgumentException("the spec could not be read as YAML or JSON: " + Short(e.Message));
        }
        return node is JsonObject o && (o["openapi"] != null || o["swagger"] != null) ? o : null;
    }

    private static string Short(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length > 120 ? line[..120] + "..." : line;
    }

    /// <summary>
    /// A cheap, conservative linear pre-scan before the parsers recurse on the text. It does not try to understand quotes (over-counting
    /// only rejects, and real specs are shallow): every [ and { raises the flow depth and every ] and } lowers it (never below 0), and a
    /// YAML line's block depth is its leading indentation plus its "- ", "? " and ": " indicators. Comments (" #" or a leading #) are skipped.
    /// </summary>
    private static void Guard(string text)
    {
        var json = text.StartsWith('{'); // JSON is bounded by MaxDepth in the parser; its ": " are not block indicators
        var flow = 0;
        var i = 0;
        while (i < text.Length)
        {
            var indent = 0;
            while (i < text.Length && text[i] is ' ' or '\t') { indent++; i++; }
            var block = indent;
            if (!json && block > MaxBlockDepth) throw new HostileSpecException(TooDeep);
            var commentOk = true; // a # starts a comment at the start of a line or after whitespace
            for (; i < text.Length && text[i] != '\n'; i++)
            {
                var c = text[i];
                if (c == '#' && commentOk) { while (i < text.Length && text[i] != '\n') i++; break; }
                commentOk = c is ' ' or '\t';
                switch (c)
                {
                    case '[' or '{': if (++flow > MaxFlowDepth) throw new HostileSpecException(TooDeep); break;
                    case ']' or '}': if (flow > 0) flow--; break;
                    case '-' or '?' or ':' when !json && (i + 1 >= text.Length || text[i + 1] is ' ' or '\t' or '\r' or '\n'):
                        if (++block > MaxBlockDepth) throw new HostileSpecException(TooDeep);
                        break;
                }
            }
            i++; // the newline
        }
    }

    /// <summary>Runs the parsers on a thread with a 256 MB stack, so nesting the guard missed still needs far more than any real document holds.</summary>
    private static JsonNode? ParseOnBigStack(string text)
    {
        JsonNode? node = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                node = text.StartsWith('{')
                    ? JsonNode.Parse(text, null, new JsonDocumentOptions { MaxDepth = MaxFlowDepth })
                    : YamlToJson(text);
            }
            catch (Exception e) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
        }, 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        error?.Throw();
        return node;
    }

    private static JsonNode? YamlToJson(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count != 1) return null;
        var budget = MaxYamlNodes;
        return Convert(stream.Documents[0].RootNode, 0, ref budget);
    }

    private static JsonNode? Convert(YamlNode node, int depth, ref int budget)
    {
        if (depth > MaxFlowDepth) throw new HostileSpecException(TooDeep);
        if (--budget < 0) throw new HostileSpecException(TooLarge);
        switch (node)
        {
            case YamlMappingNode map:
                var o = new JsonObject();
                foreach (var (k, v) in map.Children)
                    o[(k as YamlScalarNode)?.Value ?? ""] = Convert(v, depth + 1, ref budget);
                return o;
            case YamlSequenceNode seq:
                var a = new JsonArray();
                foreach (var c in seq.Children) a.Add(Convert(c, depth + 1, ref budget));
                return a;
            case YamlScalarNode s:
                return Scalar(s);
            default:
                return null;
        }
    }

    /// <summary>Quoted scalars stay strings ("200", "2.0"); plain ones become null, bool or number when they look like one.</summary>
    private static JsonNode? Scalar(YamlScalarNode s)
    {
        var v = s.Value;
        if (v == null) return null;
        if (s.Style != YamlDotNet.Core.ScalarStyle.Plain)
            return JsonValue.Create(v);
        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE": return JsonValue.Create(true);
            case "false" or "False" or "FALSE": return JsonValue.Create(false);
        }
        var c = v[0];
        if (!(char.IsAsciiDigit(c) || ((c == '-' || c == '+' || c == '.') && v.Length > 1 && (char.IsAsciiDigit(v[1]) || v[1] == '.')))) return JsonValue.Create(v);
        var digits = v.TrimStart('-', '+');
        if (digits.Length > 1 && digits[0] == '0' && digits[1] != '.') return JsonValue.Create(v);
        if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }

    // ---- refs ----------------------------------------------------------------------------------------------------------------

    /// <summary>The node a local $ref points at, or null (remote, missing, malformed).</summary>
    private static JsonNode? Pointer(JsonObject root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        JsonNode? cur = root;
        foreach (var raw in reference[2..].Split('/'))
        {
            string seg;
            try { seg = Uri.UnescapeDataString(raw); } catch (UriFormatException) { return null; }
            seg = seg.Replace("~1", "/").Replace("~0", "~");
            cur = cur switch
            {
                JsonObject o => o[seg],
                JsonArray arr when int.TryParse(seg, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < arr.Count => arr[i],
                _ => null,
            };
            if (cur == null) return null;
        }
        return cur;
    }

    private static string? RefOf(JsonNode? n) => n is JsonObject o && o["$ref"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Follows a chain of $refs on the node itself (at most 8); the result is part of the original tree, not a copy.</summary>
    private static JsonNode? Deref(JsonObject root, JsonNode? n)
    {
        for (var i = 0; i <= MaxRefDepth; i++)
        {
            var r = RefOf(n);
            if (r == null) return n;
            n = Pointer(root, r);
        }
        return null;
    }

    /// <summary>The state of one action's Params build: a node budget, and the refs being inlined right now.</summary>
    private sealed class InlineCtx
    {
        public int Budget = MaxParamsNodes;
        public readonly List<string> Stack = [];
    }

    /// <summary>
    /// A deep copy with every local $ref replaced by its target. A ref already being inlined (a cycle), a remote ref, a chain deeper
    /// than 8, nesting deeper than 32 and anything past the node budget all become {}.
    /// </summary>
    private static JsonNode? Inline(JsonObject root, JsonNode? n, InlineCtx ctx, int depth = 0)
    {
        if (n == null) return null;
        if (depth > MaxParamsDepth || --ctx.Budget < 0) return new JsonObject();
        switch (n)
        {
            case JsonObject o:
                if (RefOf(o) is { } r)
                {
                    if (ctx.Stack.Count >= MaxRefDepth || ctx.Stack.Contains(r) || Pointer(root, r) is not { } target) return new JsonObject();
                    ctx.Stack.Add(r);
                    try { return Inline(root, target, ctx, depth); }
                    finally { ctx.Stack.RemoveAt(ctx.Stack.Count - 1); }
                }
                var copy = new JsonObject();
                foreach (var (k, v) in o) copy[k] = Inline(root, v, ctx, depth + 1);
                return copy;
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var v in a) arr.Add(Inline(root, v, ctx, depth + 1));
                return arr;
            default:
                return n.DeepClone();
        }
    }

    // ---- operations ----------------------------------------------------------------------------------------------------------

    private static string? OperationId(JsonObject op) => op["operationId"] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static string Summary(JsonObject op)
    {
        foreach (var key in new[] { "summary", "description" })
            if (op[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s.Trim().Split('\n')[0].Trim();
        return "";
    }

    private static JsonObject BuildParams(JsonObject root, bool v2, JsonObject op, JsonObject item, HashSet<string> skipHeaders, HashSet<string> skipQueries)
    {
        var ctx = new InlineCtx();
        var props = new JsonObject();
        var required = new List<string>();
        var bodyRequired = false;
        string? bodyKind = null;
        JsonObject? body = null;
        var formProps = new JsonObject();
        var formRequired = new List<string>();

        // Operation-level parameters override path-level ones with the same name and location.
        var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var source in new[] { item["parameters"], op["parameters"] })
            if (source is JsonArray list)
                foreach (var p in list)
                    if (Deref(root, p) is JsonObject po && Str(po["name"]) is { } name && Str(po["in"]) is { } where)
                        byKey[where + ":" + name] = po;

        var taken = new HashSet<string>(StringComparer.Ordinal) { "body" }; // "body" belongs to the request body
        foreach (var po in byKey.Values)
        {
            var name = Str(po["name"])!;
            var where = Str(po["in"])!;
            if (where == "body" && v2)
            {
                if (Inline(root, po["schema"], ctx) is JsonObject bs) { body = bs; bodyKind = "json"; }
                bodyRequired = po["required"] is JsonValue br && br.TryGetValue<bool>(out var b1) && b1;
                continue;
            }
            var isRequired = po["required"] is JsonValue rv && rv.TryGetValue<bool>(out var req) && req;
            if (where == "formData" && v2)
            {
                var f = V2Schema(root, po, ctx);
                if (Str(po["description"]) is { Length: > 0 } fd) f["description"] = fd.Trim();
                formProps[name] = f;
                if (isRequired) formRequired.Add(name);
                continue;
            }
            if (where is not ("path" or "query" or "header")) continue;
            if (where == "header" && skipHeaders.Contains(name)) continue;
            if (where == "query" && skipQueries.Contains(name)) continue;

            var schema = v2 ? V2Schema(root, po, ctx) : Inline(root, po["schema"], ctx) as JsonObject ?? new JsonObject();
            var desc = Str(po["description"]);
            var key = name;
            if (taken.Contains(key))
            {
                key = $"{name}_{where}";
                while (taken.Contains(key)) key += "_";
                desc = string.IsNullOrWhiteSpace(desc) ? $"({where} parameter '{name}')" : $"{desc.Trim()} ({where} parameter '{name}')";
            }
            else desc = string.IsNullOrWhiteSpace(desc) ? $"(in {where})" : $"{desc.Trim()} (in {where})";
            taken.Add(key);
            schema["description"] = desc;
            schema["x-in"] = where;
            schema["x-name"] = name;
            props[key] = schema;
            if ((where == "path" || isRequired) && !required.Contains(key)) required.Add(key);
        }

        if (!v2 && Deref(root, op["requestBody"]) is JsonObject rb && rb["content"] is JsonObject content)
        {
            var json = content.FirstOrDefault(kv => kv.Key.Contains("json", StringComparison.OrdinalIgnoreCase));
            var form = content.FirstOrDefault(kv => kv.Key.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
                                                    || kv.Key.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase));
            var chosen = json.Key != null ? json : form;
            if (chosen.Key != null)
            {
                body = (chosen.Value is JsonObject mt ? Inline(root, mt["schema"], ctx) as JsonObject : null) ?? new JsonObject();
                bodyKind = json.Key != null ? "json" : "form";
                bodyRequired = rb["required"] is JsonValue br && br.TryGetValue<bool>(out var b2) && b2;
            }
        }
        if (body == null && v2 && formProps.Count > 0)
        {
            body = new JsonObject { ["type"] = "object", ["properties"] = formProps, ["required"] = new JsonArray(formRequired.Select(x => (JsonNode)x).ToArray()) };
            bodyKind = "form";
            bodyRequired = formRequired.Count > 0;
        }
        if (body != null)
        {
            props["body"] = body;
            if (bodyRequired) required.Add("body");
        }

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(required.Select(x => (JsonNode)x).ToArray()),
        };
        if (bodyKind != null) result["x-body"] = bodyKind;
        return result;
    }

    private static JsonObject V2Schema(JsonObject root, JsonObject param, InlineCtx ctx)
    {
        var s = new JsonObject();
        foreach (var key in new[] { "type", "format", "enum", "items", "default", "minimum", "maximum", "minLength", "maxLength", "pattern" })
            if (param[key] != null) s[key] = Inline(root, param[key], ctx);
        return s;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Whether a route (relative to the service URL) has a credential segment, as the universal floor reads it.</summary>
    internal static bool IsCredentialRoute(string route) => Classify("GET", route).Class == CommandClass.Credential;

    private static Classification Classify(string method, string route)
    {
        foreach (var seg in route.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var bare = seg.Replace("{", "").Replace("}", "");
            if (CredentialSegments.Contains(bare))
                return new Classification(CommandClass.Credential, ClassSource.Floor, $"route segment \"{bare}\" reveals or changes a sign-in: never allowed (universal credential floor)");
        }
        return method switch
        {
            "GET" or "HEAD" => new Classification(CommandClass.ReadOnly, ClassSource.Method, $"{method} only reads"),
            "DELETE" => new Classification(CommandClass.Destructive, ClassSource.Method, "DELETE removes something"),
            _ => new Classification(CommandClass.Change, ClassSource.Method, $"{method} changes something"),
        };
    }

    // ---- auth and server -----------------------------------------------------------------------------------------------------

    private const int MaxScopes = 200, MaxScopeName = 200, MaxScopeDescription = 300;

    /// <summary>The auth the spec asks for and the name of the security scheme it came from (null when none).</summary>
    private static (ApiAuth? Auth, string? Scheme) ReadAuth(JsonObject root, Uri? specUrl)
    {
        var schemes = ((root["components"] as JsonObject)?["securitySchemes"] ?? root["securityDefinitions"]) as JsonObject;
        if (schemes == null) return (null, null);
        JsonObject? scheme = null;
        string? name = null;
        if (root["security"] is JsonArray sec)
            foreach (var entry in sec)
                if (entry is JsonObject eo && eo.Count > 0)
                {
                    name = eo.First().Key;
                    scheme = Deref(root, schemes[name]) as JsonObject;
                    break;
                }
        if (scheme == null)
        {
            name = null;
            foreach (var kv in schemes)
                if (Deref(root, kv.Value) is JsonObject first) { scheme = first; name = kv.Key; break; }
        }
        if (scheme == null) return (null, null);
        var auth = SchemeAuth(scheme, specUrl);
        return (auth, auth == null ? null : name);
    }

    private static ApiAuth? SchemeAuth(JsonObject scheme, Uri? specUrl)
    {
        var description = Str(scheme["description"]);
        switch (Str(scheme["type"])?.ToLowerInvariant())
        {
            case "oauth2": return OAuth2(scheme, description, specUrl) ?? new ApiAuth("bearer", null, description);
            case "openidconnect":
                return Str(scheme["openIdConnectUrl"]) is { } oidc
                    ? new ApiAuth("oauth2", null, description, OpenIdConnectUrl: HttpsUrl(oidc, specUrl))
                    : null;
            case "basic": return new ApiAuth("basic", null, description);
            case "http":
                return Str(scheme["scheme"])?.ToLowerInvariant() switch
                {
                    "bearer" => new ApiAuth("bearer", null, description),
                    "basic" => new ApiAuth("basic", null, description),
                    _ => null,
                };
            case "apikey":
                var name = Str(scheme["name"]);
                return Str(scheme["in"])?.ToLowerInvariant() switch
                {
                    "header" => new ApiAuth("header", name, description),
                    "query" => new ApiAuth("query", name, description),
                    _ => null,
                };
            default: return null;
        }
    }

    /// <summary>An oauth2 scheme with an authorization-code flow (OpenAPI 3) or accessCode flow (Swagger 2); null when it only has other flows.</summary>
    private static ApiAuth? OAuth2(JsonObject scheme, string? description, Uri? specUrl)
    {
        JsonObject? flow = null;
        if (scheme["flows"] is JsonObject flows) flow = flows["authorizationCode"] as JsonObject;
        else if (string.Equals(Str(scheme["flow"]), "accessCode", StringComparison.OrdinalIgnoreCase)) flow = scheme;
        if (flow == null) return null;
        return new ApiAuth("oauth2", null, description, HttpsUrl(Str(flow["authorizationUrl"]), specUrl), HttpsUrl(Str(flow["tokenUrl"]), specUrl), ScopeMap(flow["scopes"]));
    }

    /// <summary>An https address (a relative one is resolved against the spec url), else null: sign-in addresses are never plain http.</summary>
    private static string? HttpsUrl(string? url, Uri? specUrl)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsWhiteSpace)) return null;
        // Only an http(s) parse counts as absolute ("/oauth/authorize" is a file: URI on Linux); anything else is relative to the spec url.
        if (!(Uri.TryCreate(url, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https"))
        {
            if (url.Contains(':') && !url.StartsWith('/')) return null; // some other scheme (ftp:, javascript:, ...)
            if (specUrl is not { IsAbsoluteUri: true } || specUrl.Scheme is not ("http" or "https") || !Uri.TryCreate(specUrl, url, out abs)) return null;
        }
        return abs.Scheme == "https" && abs.Host.Length > 0 && abs.UserInfo.Length == 0 ? abs.AbsoluteUri : null;
    }

    private static bool CleanScopeName(string s) => s.Length is > 0 and <= MaxScopeName && !s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static IReadOnlyDictionary<string, string> ScopeMap(JsonNode? n)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (n is not JsonObject o) return map;
        foreach (var (name, v) in o)
        {
            if (map.Count >= MaxScopes) break;
            if (!CleanScopeName(name)) continue;
            var d = (Str(v) ?? "").Trim().Split('\n')[0].Trim();
            map[name] = d.Length > MaxScopeDescription ? d[..MaxScopeDescription] : d;
        }
        return map;
    }

    /// <summary>The scopes an operation asks of the scheme: its own security entry (an explicit empty list means none), else the document's.</summary>
    private static IReadOnlyList<string> OperationScopes(JsonObject op, IReadOnlyList<string> top, string scheme) =>
        op["security"] is JsonArray own ? SecurityScopes(own, scheme) ?? [] : top;

    /// <summary>The scopes of the first alternative that names the scheme, or null when none does.</summary>
    private static IReadOnlyList<string>? SecurityScopes(JsonNode? security, string scheme)
    {
        if (security is not JsonArray list) return null;
        foreach (var entry in list)
            if (entry is JsonObject eo && eo[scheme] is JsonArray names)
                return names.Select(Str).OfType<string>().Where(CleanScopeName).Distinct(StringComparer.Ordinal).Take(MaxScopes).ToList();
        return null;
    }

    /// <summary>The url when it is an absolute http(s) address with a clean host, else null.</summary>
    private static string? CleanHttp(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.Host.Length > 0 && !url.Any(char.IsWhiteSpace) ? url : null;

    private static string? ServerUrl(JsonObject root, bool v2, Uri? specUrl)
    {
        if (v2)
        {
            var host = Str(root["host"]);
            var basePath = Str(root["basePath"]) ?? "";
            var scheme = Str((root["schemes"] as JsonArray)?.FirstOrDefault()) ?? "https";
            if (scheme is not ("http" or "https")) return null;
            if (host == null)
            {
                if (specUrl is not { IsAbsoluteUri: true } || specUrl.Scheme is not ("http" or "https")) return null;
                host = specUrl.Authority;
            }
            if (host.Length == 0 || host.IndexOfAny(['/', '?', '#', '@', '\\']) >= 0 || host.Any(char.IsWhiteSpace)) return null;
            if (basePath.Length > 0 && !basePath.StartsWith('/')) basePath = "/" + basePath;
            return CleanHttp($"{scheme}://{host}{basePath}");
        }

        var server = (root["servers"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var url = Str(server?["url"]) ?? "/";
        if (server?["variables"] is JsonObject vars)
            foreach (var (name, v) in vars)
                if (v is JsonObject vo && Str(vo["default"]) is { } def)
                    url = url.Replace("{" + name + "}", def);
        if (Uri.TryCreate(url, UriKind.Absolute, out _)) return CleanHttp(url);
        if (specUrl is { IsAbsoluteUri: true } && specUrl.Scheme is "http" or "https" && Uri.TryCreate(specUrl, url, out var resolved)) return CleanHttp(resolved.ToString());
        return null;
    }
}
