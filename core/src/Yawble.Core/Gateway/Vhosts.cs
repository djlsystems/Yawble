namespace Yawble.Core.Gateway;

/// <summary>
/// The *.yawble.internal names the gateway serves. The CLI never resolves them: it sends an
/// absolute-form request to the injected HTTP_PROXY, which is this gateway.
/// </summary>
public static class Vhosts
{
    public const string Bus = "bus.yawble.internal";

    /// <summary>Claude Code (both modes) → https://api.anthropic.com.</summary>
    public const string Model = "model.yawble.internal";

    /// <summary>Grok in login mode → https://cli-chat-proxy.grok.com, with the session from the host Grok home.</summary>
    public const string Grok = "grok.yawble.internal";

    /// <summary>Grok with the xAI API key fallback → https://api.x.ai.</summary>
    public const string Xai = "xai.yawble.internal";

    /// <summary>GitHub Copilot with its sign-in held by the gateway → https://api.githubcopilot.com.</summary>
    public const string Copilot = "copilot.yawble.internal";

    /// <summary>Codex with its sign-in held by the gateway → https://chatgpt.com (ChatGPT's backend API).</summary>
    public const string Codex = "codex.yawble.internal";

    public static readonly IReadOnlyDictionary<string, Uri> ProductionUpstreams = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
    {
        [Model] = new("https://api.anthropic.com"),
        [Grok] = new("https://cli-chat-proxy.grok.com"),
        [Xai] = new("https://api.x.ai"),
        [Copilot] = new("https://api.githubcopilot.com"),
        [Codex] = new("https://chatgpt.com"),
    };

    /// <summary>
    /// Path allowlists per vhost (method, exact path without query). Anything else is 403 and logged, so paths a new
    /// CLI version adds show up in the gateway log.
    /// grok and xai: the account and model paths plus inference (Responses and Chat Completions).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<(string Method, string Path)>> PathAllowlists =
        new Dictionary<string, IReadOnlySet<(string, string)>>(StringComparer.OrdinalIgnoreCase)
        {
            [Model] = new HashSet<(string, string)>
            {
                ("POST", "/v1/messages"), ("POST", "/v1/messages/count_tokens"), ("GET", "/v1/models"), ("HEAD", "/api/hello"),
            },
            [Grok] = new HashSet<(string, string)>
            {
                ("GET", "/"), ("GET", "/v1/user"), ("GET", "/v1/settings"), ("GET", "/v1/models"), ("GET", "/v1/bundle/archive"),
                ("POST", "/v1/responses"), ("POST", "/v1/chat/completions"),
            },
            [Xai] = new HashSet<(string, string)>
            {
                ("GET", "/"), ("GET", "/v1/models"), ("GET", "/v1/api-key"), ("POST", "/v1/responses"), ("POST", "/v1/chat/completions"),
            },
            // Copilot CLI: the account's meta, the GitHub MCP server, the model list, the
            // automatic model choice and the model call (Responses; Chat Completions for older models). Its WebSocket
            // attempt (GET /responses) is refused here, and it falls back to POST.
            [Copilot] = new HashSet<(string, string)>
            {
                ("GET", "/meta"), ("POST", "/mcp/readonly"), ("GET", "/models"), ("POST", "/auto"), ("POST", "/responses"),
                ("POST", "/chat/completions"),
            },
            // codex-cli: the model list, the model call, and the account and settings checks.
            // Not its plugin catalog, its analytics or ChatGPT's MCP server: refused here and logged.
            [Codex] = new HashSet<(string, string)>
            {
                ("GET", "/backend-api/codex/models"), ("POST", "/backend-api/codex/responses"),
                ("GET", "/backend-api/wham/accounts/check"), ("GET", "/backend-api/wham/settings/user"),
            },
        };

    /// <summary>
    /// The floor: the model vendors' API and sign-in hosts are NEVER reachable by CONNECT, whatever the
    /// policy's allowHosts says. Model traffic goes only through the vhosts, where the gateway holds the credential.
    /// </summary>
    public static readonly IReadOnlyList<string> NeverConnect =
    [
        "api.anthropic.com", "platform.claude.com", "claude.ai", "claude.com", "console.anthropic.com", "anthropic.com",
        "api.x.ai", "cli-chat-proxy.grok.com", "code.grok.com", "auth.x.ai", "accounts.x.ai", "grok.com", "x.ai",
    ];

    public static bool IsNeverConnect(string host) =>
        NeverConnect.Any(n => host.Equals(n, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + n, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// allowHosts matching: an exact host name, or "*.example.com" for any subdomain (not the apex). IP literals
    /// match only if listed literally.
    /// </summary>
    public static bool HostAllowed(string host, IEnumerable<string> allowHosts)
    {
        if (IsNeverConnect(host)) return false;
        foreach (var pattern in allowHosts)
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                if (host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) && host.Length > pattern.Length - 1) return true;
            }
            else if (host.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
