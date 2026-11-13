namespace Yawble.Bridge;

/// <summary>
/// Where the bridge sends MCP: the per-run gateway, reached ONLY through the proxy address the sandbox injects.
/// Linux rewrites 127.0.0.1 to the slirp gateway 10.0.2.2, so the address is always read
/// from the environment and never hard-coded.
/// </summary>
public sealed record GatewayAddress(Uri Proxy, Uri BusEndpoint, string RunToken)
{
    public const string BusUrl = "http://bus.yawble.internal/mcp";

    public static GatewayAddress? FromEnvironment(Func<string, string?> env, out string? error)
    {
        var proxy = First(env, "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy");
        var token = env("YAWBLE_RUN_TOKEN");
        error = proxy is null ? "no HTTP_PROXY in the environment: the bridge only runs inside a sandbox with a gateway"
            : string.IsNullOrEmpty(token) ? "no YAWBLE_RUN_TOKEN in the environment"
            : !Uri.TryCreate(proxy, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ? $"HTTP_PROXY is not an http:// URL: {proxy}"
            : null;
        return error is null ? new GatewayAddress(new Uri(proxy!), new Uri(BusUrl), token!) : null;
    }

    private static string? First(Func<string, string?> env, params string[] names) =>
        names.Select(env).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
