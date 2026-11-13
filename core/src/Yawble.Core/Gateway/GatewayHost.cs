using System.Net;
using Yawble.Core.Credentials;

namespace Yawble.Core.Gateway;

/// <summary>Where the gateway gets the real credentials, read fresh on every request (a reconnect needs no restart).</summary>
public interface IGatewayCredentials
{
    (ClaudeMode Mode, string? Value) Claude();

    string? XaiApiKey();

    /// <summary>The host Grok home's current, unexpired session; null when there is none.</summary>
    GrokSession? GrokSession();

    /// <summary>Asks the Grok keeper to refresh (xAI's CLI on the host does it).</summary>
    Task<GrokRefreshResult> RefreshGrokAsync(bool force);

    /// <summary>
    /// The person's own Claude sign-in on this computer, good for a few minutes at least (renewed through the host's
    /// claude when it is about to expire); null when there is none. For runs whose gateway holds Claude's sign-in.
    /// </summary>
    Task<ClaudeHostToken?> ClaudeHostTokenAsync(CancellationToken ct) => Task.FromResult<ClaudeHostToken?>(null);

    /// <summary>After Anthropic refused the host token: ask the host's claude to renew it (in the background).</summary>
    Task<bool> RenewClaudeHostAsync() => Task.FromResult(false);

    /// <summary>The person's own Copilot CLI token from the OS credential store, read now; null when there is none.</summary>
    string? CopilotHostToken() => null;

    /// <summary>The person's own Codex sign-in on this computer (renewed through the host's codex near expiry); null when none.</summary>
    Task<CodexHostToken?> CodexHostTokenAsync(CancellationToken ct) => Task.FromResult<CodexHostToken?>(null);

    /// <summary>After an upstream 401: has the host's codex renew its sign-in.</summary>
    Task<bool> RenewCodexHostAsync() => Task.FromResult(false);
}

/// <summary>
/// The production credentials: the credential service for the Claude and xAI keys, and the host Grok home for the
/// session (read directly, so the gateway works even before a keeper is running), refreshed by the Grok keeper.
/// </summary>
public sealed class GatewayCredentials(CredentialService credentials, GrokHome grokHome, GrokKeeper? keeper, TimeProvider? clock = null,
    ClaudeHostSession? claudeHost = null, Func<string?>? copilotToken = null, CodexHostSession? codexHost = null) : IGatewayCredentials
{
    public Task<ClaudeHostToken?> ClaudeHostTokenAsync(CancellationToken ct) =>
        claudeHost?.CurrentAsync(ct) ?? Task.FromResult<ClaudeHostToken?>(null);

    public Task<bool> RenewClaudeHostAsync() => claudeHost?.RefreshAsync(force: true) ?? Task.FromResult(false);

    public Task<CodexHostToken?> CodexHostTokenAsync(CancellationToken ct) =>
        codexHost?.CurrentAsync(ct) ?? Task.FromResult<CodexHostToken?>(null);

    public Task<bool> RenewCodexHostAsync() => codexHost?.RefreshAsync(force: true) ?? Task.FromResult(false);

    public string? CopilotHostToken() => (copilotToken ?? Agents.SignInCatalog.CopilotToken)();

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public (ClaudeMode Mode, string? Value) Claude() => credentials.ClaudeCredential();

    public string? XaiApiKey() => credentials.Get(CredentialSlot.XaiApiKey);

    public GrokSession? GrokSession()
    {
        // The keeper starts with the first Grok request: from then on it renews the sign-in before it expires.
        keeper?.Start();
        return grokHome.ReadSession() is { } session && !session.ExpiredAt(_clock.GetUtcNow()) ? session : null;
    }

    public Task<GrokRefreshResult> RefreshGrokAsync(bool force) =>
        keeper?.RefreshAsync(force) ?? Task.FromResult(new GrokRefreshResult(false, false, "Grok login expired: sign in again (no Grok keeper is running)", null));
}

public sealed class GatewayHostOptions
{
    /// <summary>Upstream base per vhost. Production: <see cref="Vhosts.ProductionUpstreams"/>; tests point them at a fake.</summary>
    public IReadOnlyDictionary<string, Uri> Upstreams { get; init; } = Vhosts.ProductionUpstreams;

    /// <summary>CONNECT is allowed only to these ports (host:443).</summary>
    public IReadOnlySet<int> ConnectPorts { get; init; } = new HashSet<int> { 443 };

    /// <summary>
    /// When false (production), a CONNECT whose host resolves to a loopback, private or link-local address is refused,
    /// so an allowlisted name pointed at 127.0.0.1 cannot reach the host's own ports (another run's gateway, the core).
    /// </summary>
    public bool AllowPrivateConnectTargets { get; init; }

    public long MaxRequestBody { get; init; } = 64L * 1024 * 1024;

    /// <summary>How long a Grok request waits for the keeper when the session has expired.</summary>
    public TimeSpan GrokRefreshWait { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // Transport seams. Production leaves all three null: a TCP listener on 127.0.0.1, a SocketsHttpHandler to the
    // vendors, and DNS + TCP for CONNECT. The tests put the same gateway on in-memory streams, so they bind no port.

    /// <summary>Makes each run's listener; null = <see cref="LoopbackGatewayListener"/>.</summary>
    public Func<IGatewayListener>? Listener { get; init; }

    /// <summary>The handler under the upstream HTTP client; null = the production SocketsHttpHandler. Not disposed by the host.</summary>
    public HttpMessageHandler? UpstreamHandler { get; init; }

    /// <summary>How CONNECT resolves and reaches its target; null = <see cref="SocketConnectDialer"/>.</summary>
    public IConnectDialer? ConnectDialer { get; init; }
}

/// <summary>What became of a host refusal: the person was asked (or already is), or it stays a plain refusal.</summary>
public enum HostAskOutcome
{
    /// <summary>Not a host the person is asked about (an IP, loopback, a private or app host, a floor-rule host), or no ask store.</summary>
    None,

    /// <summary>A new ask for the person.</summary>
    Asked,

    /// <summary>The same host already waits for the person: only its count went up.</summary>
    Pending,

    /// <summary>The person denied this host for this member within the last 24 hours.</summary>
    Denied,

    /// <summary>The host is already allowed for the member's next run (the person allowed it after this run started).</summary>
    AllowedNextRun,
}

/// <summary>Where a run's gateway reports a host it refused because it is not on the agent's allowlist (the network asks).</summary>
public interface IHostRefusals
{
    /// <param name="member">The run's member, "Team/Name".</param>
    /// <param name="host">The host refused (lower case, no port).</param>
    /// <param name="method">CONNECT, or the plain-http request's method.</param>
    /// <param name="path">A plain-http request's path without its query; null for CONNECT.</param>
    HostAskOutcome Refused(string member, string host, string method, string? path);
}

/// <summary>What one run's gateway serves. Only the vhosts the run's CLI and mode need are on (least exposure).</summary>
public sealed record RunGatewaySpec(
    long RunId,
    string Member,
    IReadOnlySet<string> ModelVhosts,
    IReadOnlyList<string> AllowHosts,
    TimeSpan TokenLifetime)
{
    /// <summary>
    /// Host sign-in: the CLI signs its model requests with the host's own sign-in, which the gateway forwards unchanged
    /// (it has no credential to swap in and holds none). The run token is not asked for on the model vhost then: the
    /// CLI cannot carry it there, and a request without the CLI's own credential gets nothing the caller does not
    /// already hold. The bus vhost still requires the run token.
    /// </summary>
    public bool HostSignIn { get; init; }

    /// <summary>The doctor's self-test gateway: its denials are not written to the gateway log (they are not a run's).</summary>
    public bool Quiet { get; init; }

    /// <summary>The launcher's run key (run_records.run_key): what usage_events are filed under, unique for every run
    /// (the run number is the bus run's id, or a local counter for runs that are not bus runs).</summary>
    public string? RunKey { get; init; }

    /// <summary>
    /// Claude with the gateway holding the sign-in: the CLI presents the run token (as its OAuth token)
    /// and the gateway puts the person's own host Claude sign-in on the request instead. The sandbox never sees it.
    /// </summary>
    public bool ClaudeFromHost { get; init; }

    /// <summary>The vhosts a CLI uses, by preset and mode: Claude → model; Grok login → grok; Grok key → xai.</summary>
    /// <summary>
    /// Copilot with the gateway holding the sign-in: the gateway puts the person's own Copilot token from
    /// the OS credential store on each request to copilot.yawble.internal.
    /// </summary>
    public bool CopilotFromHost { get; init; }

    /// <summary>Codex with the gateway holding the sign-in: the person's own ~/.codex sign-in per request.</summary>
    public bool CodexFromHost { get; init; }

    public static IReadOnlySet<string> VhostsFor(string cli, GrokMode grokMode) => cli switch
    {
        "grok" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { grokMode == GrokMode.ApiKey ? Vhosts.Xai : Vhosts.Grok },
        // Codex, Copilot, Antigravity: their own hosts by CONNECT (AgentPresets.PassthroughHosts); no model vhost.
        "codex" or "copilot" or "antigravity" => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Vhosts.Model },
    };
}

/// <summary>
/// Shared by every run's gateway: the upstream HTTP client, the run-token registry, the gateway log and the
/// credentials. One <see cref="RunGateway"/> is started per run.
/// </summary>
public sealed class GatewayHost : IDisposable
{
    public GatewayHost(IGatewayCredentials credentials, GatewayLog log, RunTokenRegistry tokens, GatewayHostOptions? options = null)
    {
        Credentials = credentials;
        Log = log;
        Tokens = tokens;
        Options = options ?? new GatewayHostOptions();
        Upstream = Options.UpstreamHandler is { } handler ? new HttpClient(handler, disposeHandler: false) : new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None, // bytes pass through as the vendor sent them
            UseCookies = false,
            ConnectTimeout = Options.ConnectTimeout,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
        });
        Upstream.Timeout = Timeout.InfiniteTimeSpan; // model streams run for minutes; the run's own limits stop them
    }

    public IGatewayCredentials Credentials { get; }
    public GatewayLog Log { get; }
    public RunTokenRegistry Tokens { get; }
    public GatewayHostOptions Options { get; }
    internal HttpClient Upstream { get; }

    /// <summary>The network asks: a host refused for not being on the allowlist is reported here (null: plain refusals only). Set by the host at start.</summary>
    public IHostRefusals? Refusals { get; set; }

    /// <summary>
    /// Issues the run's token and binds 127.0.0.1:&lt;ephemeral&gt; (IPv4 only; never 0.0.0.0) for it.
    /// <paramref name="bus"/> serves bus.yawble.internal/mcp for this run.
    /// </summary>
    public RunGateway Start(RunGatewaySpec spec, IBusEndpoint bus)
    {
        var token = Tokens.Issue(spec.RunId, spec.TokenLifetime);
        return RunGateway.Start(this, spec, token, bus);
    }

    public void Dispose() => Upstream.Dispose();
}
