using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Yawble.Core.Credentials;

namespace Yawble.Core.Gateway;

/// <summary>The bus side of a run's gateway: one MCP JSON-RPC message in, the reply (or 202 for a notification) out.</summary>
public interface IBusEndpoint
{
    Task<BusReply> HandleAsync(byte[] body, CancellationToken ct);
}

public sealed record BusReply(int Status, string? Json);

/// <summary>
/// One run's gateway: the ONLY endpoint the run's sandbox can reach, set as the sandbox request's
/// runtimeConfig.networkProxy. It serves:
///   bus.yawble.internal/mcp      → this run's MCP tools, identity from the run (never from arguments);
///   model.yawble.internal        → api.anthropic.com, run token swapped for the Claude credential (Bearer or x-api-key);
///   grok.yawble.internal         → cli-chat-proxy.grok.com, run token swapped for the host Grok home's session token;
///   xai.yawble.internal          → api.x.ai, run token swapped for the xAI API key;
///   CONNECT host:443          → only hosts on the run's allowHosts, never the vendors' API or sign-in hosts.
/// Model requests are checked against a per-vhost path allowlist, forwarded over TLS with every non-hop header
/// (anthropic-beta, anthropic-version, x-xai-token-auth, x-grok-client-*) verbatim, and the response — rate-limit
/// headers included — streams back unbuffered. Every denial is written to the gateway log.
/// </summary>
public sealed class RunGateway : IAsyncDisposable
{
    private readonly GatewayHost _host;
    private readonly RunGatewaySpec _spec;
    private readonly IBusEndpoint _bus;
    private readonly IGatewayListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly Lock _lock = new();
    private Task? _accept;

    private RunGateway(GatewayHost host, RunGatewaySpec spec, RunToken token, IBusEndpoint bus)
    {
        _host = host;
        _spec = spec;
        _bus = bus;
        Token = token;
        _listener = host.Options.Listener?.Invoke() ?? new LoopbackGatewayListener();
    }

    public RunToken Token { get; }

    public int Port => _listener.Port;

    /// <summary>The proxy URL for the sandbox (127.0.0.1; the sandbox rewrites it to 10.0.2.2 inside a Linux sandbox).</summary>
    public Uri ProxyUrl => new($"http://127.0.0.1:{Port}");

    /// <summary>A client connection to this gateway, over its listener (the doctor's self-test).</summary>
    public Task<Stream> OpenClientAsync(CancellationToken ct) => _listener.ConnectAsync(ct);

    internal static RunGateway Start(GatewayHost host, RunGatewaySpec spec, RunToken token, IBusEndpoint bus)
    {
        var gateway = new RunGateway(host, spec, token, bus);
        gateway._listener.Start();
        gateway._accept = Task.Run(gateway.AcceptLoopAsync);
        return gateway;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Stream client;
            try
            {
                client = await _listener.AcceptAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            var task = Task.Run(() => ServeAsync(client));
            lock (_lock)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(task);
            }
        }
    }

    private async Task ServeAsync(Stream client)
    {
        await using var stream = client;
        var ct = _stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                WireRequest? head;
                try
                {
                    head = await HttpWire.ReadHeadAsync(stream, ct);
                }
                catch (WireException e)
                {
                    Write(GatewayEventKinds.BadRequest, false, null, null, null, e.Status, e.Message);
                    await HttpWire.RespondJsonAsync(stream, e.Status, HttpWire.Reason(e.Status), Error(e.Message), false, ct);
                    return;
                }
                if (head is null) return;
                if (head.Method == "CONNECT")
                {
                    await ConnectAsync(stream, head, ct);
                    return; // the connection was a tunnel, or refused and closed
                }
                if (!await HandleAsync(stream, head, ct)) return;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>One non-CONNECT request. Returns whether the connection may be reused.</summary>
    private async Task<bool> HandleAsync(Stream s, WireRequest head, CancellationToken ct)
    {
        string vhost, pathAndQuery;
        if (head.Target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(head.Target, UriKind.Absolute, out var uri))
        {
            vhost = uri.Host;
            pathAndQuery = uri.PathAndQuery;
        }
        else if (head.Target.StartsWith('/'))
        {
            vhost = (head.Header("Host") ?? "").Split(':')[0];
            pathAndQuery = head.Target;
        }
        else
        {
            Write(GatewayEventKinds.BadRequest, true, null, head.Method, head.Target, 400, "only absolute-form http:// or origin-form requests; https goes by CONNECT");
            await DrainAndRespondAsync(s, head, 400, "this proxy takes http:// absolute-form requests; use CONNECT for https", ct);
            return false;
        }

        if (head.Has("Expect", "100-continue")) await HttpWire.WriteAsync(s, "HTTP/1.1 100 Continue\r\n\r\n", ct);
        byte[] body;
        try
        {
            body = await HttpWire.ReadBodyAsync(s, head, _host.Options.MaxRequestBody, ct);
        }
        catch (WireException e)
        {
            Write(GatewayEventKinds.BadRequest, false, vhost, head.Method, pathAndQuery, e.Status, e.Message);
            await HttpWire.RespondJsonAsync(s, e.Status, HttpWire.Reason(e.Status), Error(e.Message), false, ct);
            return false;
        }
        var keepAlive = head.KeepAlive;
        var path = pathAndQuery.Split('?')[0];

        if (vhost.Equals(Vhosts.Bus, StringComparison.OrdinalIgnoreCase))
        {
            await BusAsync(s, head, path, body, keepAlive, ct);
            return keepAlive;
        }

        if (!_spec.ModelVhosts.Contains(vhost) || !_host.Options.Upstreams.TryGetValue(vhost, out var upstream))
        {
            Write(GatewayEventKinds.HostDenied, true, vhost, head.Method, Redact(head.Target), 403, "host not served by this run's gateway");
            // Plain http is never forwarded; a host name off the allowlist is still reported (allowed, it is reachable over https).
            // Only a real outside host is told to use https: a gateway virtual host (*.yawble.internal) has no https address.
            var outside = IsOutsideHost(vhost);
            var bare = vhost.TrimEnd('.'); // a trailing dot names the same host: it must not slip past the virtual-host test
            var told = bare.Length > 0 && !Vhosts.HostAllowed(vhost, _spec.AllowHosts) && !bare.EndsWith("." + Policy.FloorRules.ReservedSuffix, StringComparison.OrdinalIgnoreCase)
                       && !bare.Equals(Policy.FloorRules.ReservedSuffix, StringComparison.OrdinalIgnoreCase)
                ? $"{vhost} is not reachable from this agent: {await AllowlistRefusalAsync(vhost, head.Method, path, ct)}"
                : $"{vhost} is not reachable from this agent";
            if (outside) told += $". {PlainHttpNote}";
            await HttpWire.RespondJsonAsync(s, 403, "Forbidden", Error(told), keepAlive, ct);
            return keepAlive;
        }

        if (!_spec.HostSignIn)
        {
            var check = _host.Tokens.Check(_spec.RunId, Presented(head));
            if (check != TokenCheck.Valid)
            {
                Write(GatewayEventKinds.Unauthorized, true, vhost, head.Method, pathAndQuery, 401, $"{check} run token (Bearer <redacted>)");
                await HttpWire.RespondJsonAsync(s, 401, "Unauthorized", Error("invalid run token"), keepAlive, ct);
                return keepAlive;
            }
        }

        if (!Vhosts.PathAllowlists.TryGetValue(vhost, out var allowed) || !allowed.Contains((head.Method, path)))
        {
            Write(GatewayEventKinds.PathDenied, true, vhost, head.Method, pathAndQuery, 403, "path not on the allowlist");
            await HttpWire.RespondJsonAsync(s, 403, "Forbidden", Error($"{head.Method} {path} is not allowed on {vhost}"), keepAlive, ct);
            return keepAlive;
        }

        var credential = _spec.HostSignIn ? PassThrough : await CredentialForAsync(vhost);
        if (credential.Problem is not null)
        {
            Write(GatewayEventKinds.CredentialProblem, false, vhost, head.Method, pathAndQuery, 401, credential.Problem);
            await HttpWire.RespondJsonAsync(s, 401, "Unauthorized", Error(credential.Problem), keepAlive, ct);
            return keepAlive;
        }

        return await ForwardAsync(s, head, vhost, upstream, pathAndQuery, body, credential, keepAlive, ct);
    }

    /// <param name="Extra">More headers the gateway sets (and does not take from the CLI), e.g. Codex's account.</param>
    private sealed record Credential(string? Header, string? Value, string? Problem, string Kind, IReadOnlyDictionary<string, string>? Extra = null);

    /// <summary>Host sign-in: the CLI's own Authorization / x-api-key go upstream as the CLI sent them.</summary>
    private static readonly Credential PassThrough = new(null, null, null, "the CLI's own host sign-in, passed through");

    private async Task<Credential> CredentialForAsync(string vhost)
    {
        var credentials = _host.Credentials;
        if (_spec.ClaudeFromHost && vhost.Equals(Vhosts.Model, StringComparison.OrdinalIgnoreCase))
        {
            // The person's own Claude sign-in on this computer, read now (and renewed through the host's claude when it
            // is about to expire). The CLI sent the run token as its OAuth token, so it already sends the OAuth beta.
            var host = await credentials.ClaudeHostTokenAsync(CancellationToken.None);
            return host is null
                ? new(null, null, "Claude is not signed in on this computer, or its sign-in could not be renewed: run 'claude' in your own terminal and sign in", "none")
                : new("Authorization", "Bearer " + host.AccessToken, null, "bearer (host Claude sign-in)");
        }
        if (_spec.CodexFromHost && vhost.Equals(Vhosts.Codex, StringComparison.OrdinalIgnoreCase))
        {
            // The person's own Codex sign-in on this computer, read now; the account goes with it (ChatGPT-Account-Id).
            var codex = await credentials.CodexHostTokenAsync(CancellationToken.None);
            if (codex is null)
                return new(null, null, "Codex is not signed in on this computer, or its sign-in could not be renewed: run 'codex login' in your own terminal", "none");
            var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (codex.AccountId is { Length: > 0 } account) extra["ChatGPT-Account-Id"] = account;
            return new("Authorization", "Bearer " + codex.AccessToken, null, "bearer (host Codex sign-in)", extra);
        }
        if (_spec.CopilotFromHost && vhost.Equals(Vhosts.Copilot, StringComparison.OrdinalIgnoreCase))
        {
            // The person's own Copilot token, read from the OS credential store now (Copilot CLI keeps it there).
            var token = credentials.CopilotHostToken();
            return token is null
                ? new(null, null, "GitHub Copilot is not signed in on this computer: run 'copilot' in your own terminal and sign in with /login", "none")
                : new("Authorization", "Bearer " + token, null, "bearer (host Copilot sign-in)");
        }
        if (vhost.Equals(Vhosts.Model, StringComparison.OrdinalIgnoreCase))
        {
            var (mode, value) = credentials.Claude();
            if (value is null)
            {
                return new(null, null, mode == ClaudeMode.Subscription
                    ? "no Claude credential: run `claude setup-token` and paste the token in Settings"
                    : "no Anthropic API key: add one in Settings", "none");
            }
            return mode == ClaudeMode.Subscription
                ? new("Authorization", "Bearer " + value, null, "bearer (setup-token)")
                : new("x-api-key", value, null, "x-api-key");
        }
        if (vhost.Equals(Vhosts.Xai, StringComparison.OrdinalIgnoreCase))
        {
            var key = credentials.XaiApiKey();
            return key is null ? new(null, null, "no xAI API key: add one in Settings", "none") : new("Authorization", "Bearer " + key, null, "bearer (xai key)");
        }
        // grok.yawble.internal: the host Grok home's session; if it has expired, wait for the keeper.
        var session = credentials.GrokSession();
        if (session is null)
        {
            var refresh = credentials.RefreshGrokAsync(force: false);
            var done = await Task.WhenAny(refresh, Task.Delay(_host.Options.GrokRefreshWait));
            session = done == refresh && refresh.Result.Valid ? credentials.GrokSession() : null;
            if (session is null) return new(null, null, "Grok is not signed in on this computer, or its sign-in could not be renewed: run 'grok' in your own terminal and sign in", "none");
        }
        return new("Authorization", "Bearer " + session.AccessToken, null, "bearer (grok session)");
    }

    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Authorization", "x-api-key", "Content-Length", "Expect",
    };

    private async Task<bool> ForwardAsync(Stream s, WireRequest head, string vhost, Uri upstream, string pathAndQuery, byte[] body,
        Credential credential, bool keepAlive, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(head.Method), new Uri(upstream, pathAndQuery));
        if (body.Length > 0 || head.Method is "POST" or "PUT" or "PATCH") request.Content = new ByteArrayContent(body);
        foreach (var (name, value) in head.Headers)
        {
            if (HttpWire.HopByHop.Contains(name)) continue;
            if (NotForwarded.Contains(name) && !(credential.Header is null && IsCredentialHeader(name))) continue;
            if (credential.Extra?.ContainsKey(name) == true) continue;
            if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
        if (credential.Header is not null) request.Headers.TryAddWithoutValidation(credential.Header, credential.Value);
        if (credential.Extra is not null)
            foreach (var (name, value) in credential.Extra) request.Headers.TryAddWithoutValidation(name, value);

        HttpResponseMessage response;
        try
        {
            response = await _host.Upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Write(GatewayEventKinds.UpstreamError, false, vhost, head.Method, pathAndQuery, 502, e.Message);
            await HttpWire.RespondJsonAsync(s, 502, "Bad Gateway", Error("the model API could not be reached"), keepAlive, ct);
            return keepAlive;
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            Write(GatewayEventKinds.Forward, false, vhost, head.Method, pathAndQuery, status,
                credential.Header is null ? $"credential: {credential.Kind} <redacted>" : $"credential swapped: {credential.Kind} <redacted>");
            if (status == 401) UpstreamRefused(vhost, head.Method, pathAndQuery);
            if (status == 429)
                Write(GatewayEventKinds.RateLimited, false, vhost, head.Method, pathAndQuery, 429,
                    RetryAt(response, _host.Log.Now).ToString("O", System.Globalization.CultureInfo.InvariantCulture));

            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(response.ReasonPhrase ?? HttpWire.Reason(status)).Append("\r\n");
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (HttpWire.HopByHop.Contains(name) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var v in values) sb.Append(name).Append(": ").Append(v).Append("\r\n");
            }
            var noBody = head.Method == "HEAD" || status is 204 or 304 || status < 200;
            var length = response.Content.Headers.ContentLength;
            var chunked = !noBody && length is null;
            if (length is { } l) sb.Append("Content-Length: ").Append(l).Append("\r\n");
            else if (chunked) sb.Append("Transfer-Encoding: chunked\r\n");
            sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n\r\n");
            await HttpWire.WriteAsync(s, sb.ToString(), ct);
            await s.FlushAsync(ct);
            if (noBody) return keepAlive;

            // Unbuffered: every read from upstream is written and flushed at once, so SSE events arrive as they are sent.
            // A model request's bytes are also copied aside for the usage meter, read once the response has ended.
            var meter = head.Method == "POST" ? new Provenance.UsageMeter(string.Join(",", response.Content.Headers.ContentEncoding)) : null;
            await using var from = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = await from.ReadAsync(buffer, ct)) > 0)
            {
                meter?.Add(buffer.AsSpan(0, n));
                if (chunked)
                {
                    await HttpWire.WriteAsync(s, n.ToString("X") + "\r\n", ct);
                    await s.WriteAsync(buffer.AsMemory(0, n), ct);
                    await HttpWire.WriteAsync(s, "\r\n", ct);
                }
                else
                {
                    await s.WriteAsync(buffer.AsMemory(0, n), ct);
                }
                await s.FlushAsync(ct);
            }
            // Filed before the response's end reaches the CLI (a streamed one's last chunk), so whatever reads the
            // usage after a request has finished finds it.
            if (meter is not null) RecordUsage(vhost, pathAndQuery, status, meter);
            if (chunked) await HttpWire.WriteAsync(s, "0\r\n\r\n", ct);
            await s.FlushAsync(ct);
        }
        return keepAlive;
    }

    /// <summary>
    /// When a 429 says to come back: Retry-After (seconds or a date), Anthropic's unified reset (unix seconds), the
    /// OpenAI-style x-ratelimit-reset-* (seconds, "1m30s"); five minutes when none says.
    /// </summary>
    internal static DateTimeOffset RetryAt(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } d) return now + d;
            if (ra.Date is { } at) return at;
        }
        string? H(string name) => response.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
        if (H("anthropic-ratelimit-unified-reset") is { } unix && long.TryParse(unix, out var secs) && secs > 1_000_000_000)
            return DateTimeOffset.FromUnixTimeSeconds(secs);
        foreach (var name in new[] { "x-ratelimit-reset-requests", "x-ratelimit-reset-tokens", "x-ratelimit-reset" })
        {
            if (H(name) is { } v && TryDuration(v, out var span)) return now + span;
        }
        return now + TimeSpan.FromMinutes(5);
    }

    private static bool TryDuration(string v, out TimeSpan span)
    {
        span = TimeSpan.Zero;
        if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
        {
            span = TimeSpan.FromSeconds(s);
            return true;
        }
        var m = System.Text.RegularExpressions.Regex.Match(v, @"^(?:(\d+)h)?(?:(\d+)m(?!s))?(?:([\d.]+)s)?(?:(\d+)ms)?$");
        if (!m.Success || v.Length == 0) return false;
        double G(int i) => m.Groups[i].Success ? double.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        span = TimeSpan.FromHours(G(1)) + TimeSpan.FromMinutes(G(2)) + TimeSpan.FromSeconds(G(3)) + TimeSpan.FromMilliseconds(G(4));
        return span > TimeSpan.Zero;
    }

    /// <summary>Files what the request used (as reported). Never fails the run: usage is a record, not a gate.</summary>
    private void RecordUsage(string vhost, string pathAndQuery, int status, Provenance.UsageMeter meter)
    {
        try
        {
            var q = pathAndQuery.IndexOf('?');
            _host.Log.WriteUsage(_spec.RunKey, _spec.RunId, _spec.Member, vhost, q < 0 ? pathAndQuery : pathAndQuery[..q], status, meter.Read());
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            Write(GatewayEventKinds.UpstreamError, false, vhost, "POST", pathAndQuery, status, "usage not recorded: " + e.Message);
        }
    }

    /// <summary>An upstream 401: the card reason for the person, and for Grok a refresh by the keeper.</summary>
    private void UpstreamRefused(string vhost, string method, string target)
    {
        string reason;
        if (_spec.ClaudeFromHost && vhost.Equals(Vhosts.Model, StringComparison.OrdinalIgnoreCase))
        {
            // The host token was refused although it had not expired: have the host's claude renew it for the next request.
            _ = _host.Credentials.RenewClaudeHostAsync();
            reason = "the Claude sign-in on this computer was refused: the app asked Claude Code to renew it; if this repeats, run 'claude' in your own terminal and sign in";
        }
        else if (_spec.CodexFromHost && vhost.Equals(Vhosts.Codex, StringComparison.OrdinalIgnoreCase))
        {
            _ = _host.Credentials.RenewCodexHostAsync();
            reason = "the Codex sign-in on this computer was refused: the app asked Codex to renew it; if this repeats, run 'codex login' in your own terminal";
        }
        else if (_spec.CopilotFromHost && vhost.Equals(Vhosts.Copilot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "GitHub Copilot's sign-in on this computer was refused (expired or revoked): run 'copilot' in your own terminal and sign in again with /login";
        }
        else if (_spec.HostSignIn)
        {
            // Host sign-in: the app holds no credential and renews none; the person's own CLI on the host does.
            reason = vhost.Equals(Vhosts.Model, StringComparison.OrdinalIgnoreCase)
                ? "the Claude sign-in on this computer was refused or has expired: run 'claude' in your own terminal to renew it"
                : "the Grok sign-in on this computer was refused or has expired: run 'grok' in your own terminal to renew it";
        }
        else if (vhost.Equals(Vhosts.Model, StringComparison.OrdinalIgnoreCase))
        {
            reason = _host.Credentials.Claude().Mode == ClaudeMode.Subscription
                ? "Claude credential expired or revoked: reconnect"
                : "Anthropic API key refused: reconnect";
        }
        else if (vhost.Equals(Vhosts.Xai, StringComparison.OrdinalIgnoreCase))
        {
            reason = "xAI API key refused: reconnect";
        }
        else
        {
            reason = "the Grok sign-in on this computer was refused by xAI: the app asked grok to renew it; if this repeats, run 'grok' in your own terminal and sign in";
            _ = _host.Credentials.RefreshGrokAsync(force: true);
        }
        Write(GatewayEventKinds.CredentialProblem, false, vhost, method, target, 401, reason);
    }

    private async Task BusAsync(Stream s, WireRequest head, string path, byte[] body, bool keepAlive, CancellationToken ct)
    {
        var check = _host.Tokens.Check(_spec.RunId, Bearer(head));
        if (check != TokenCheck.Valid)
        {
            Write(GatewayEventKinds.Unauthorized, true, Vhosts.Bus, head.Method, path, 401, $"{check} run token (Bearer <redacted>)");
            await HttpWire.RespondJsonAsync(s, 401, "Unauthorized", Error("invalid run token"), keepAlive, ct);
            return;
        }
        if (path != "/mcp")
        {
            Write(GatewayEventKinds.PathDenied, false, Vhosts.Bus, head.Method, path, 404, "the bus serves only /mcp");
            await HttpWire.RespondJsonAsync(s, 404, "Not Found", Error("not found"), keepAlive, ct);
            return;
        }
        if (head.Method != "POST")
        {
            // Streamable HTTP: no server-initiated stream.
            await HttpWire.RespondAsync(s, 405, "Method Not Allowed", "text/plain", [], keepAlive, ct, [new("Allow", "POST")]);
            return;
        }
        var reply = await _bus.HandleAsync(body, ct);
        if (reply.Json is null)
        {
            await HttpWire.RespondAsync(s, reply.Status, HttpWire.Reason(reply.Status), "text/plain", [], keepAlive, ct);
            return;
        }
        await HttpWire.RespondJsonAsync(s, reply.Status, HttpWire.Reason(reply.Status), reply.Json, keepAlive, ct);
    }

    // ------------------------------------------------------------------ CONNECT

    private async Task ConnectAsync(Stream s, WireRequest head, CancellationToken ct)
    {
        var (host, port) = SplitAuthority(head.Target);
        async Task Refuse(string why, string? told = null)
        {
            Write(GatewayEventKinds.ConnectDenied, true, null, "CONNECT", head.Target, 403, why);
            await HttpWire.RespondJsonAsync(s, 403, "Forbidden", Error($"CONNECT {head.Target} refused: {told ?? why}"), false, ct);
        }
        if (host is null || port is null) { await Refuse("malformed authority"); return; }
        if (!_host.Options.ConnectPorts.Contains(port.Value)) { await Refuse($"port {port} is not allowed"); return; }
        if (Vhosts.IsNeverConnect(host)) { await Refuse("model vendor and sign-in hosts are never reachable directly"); return; }
        if (!Vhosts.HostAllowed(host, _spec.AllowHosts))
        {
            await Refuse(NotOnAllowlist, await AllowlistRefusalAsync(host, "CONNECT", null, ct));
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dialer.ResolveAsync(host, ct);
        }
        catch (SocketException e)
        {
            await Refuse("name did not resolve: " + e.SocketErrorCode);
            return;
        }
        if (!_host.Options.AllowPrivateConnectTargets && addresses.Any(IsPrivate))
        {
            await Refuse("host resolves to a loopback, private or link-local address");
            return;
        }
        Stream up;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_host.Options.ConnectTimeout);
            up = await Dialer.ConnectAsync(addresses, port.Value, timeout.Token);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Write(GatewayEventKinds.UpstreamError, false, null, "CONNECT", head.Target, 502, e.Message);
            await HttpWire.RespondJsonAsync(s, 502, "Bad Gateway", Error("could not connect"), false, ct);
            return;
        }
        await using var _ = up;
        Write(GatewayEventKinds.ConnectAllowed, false, null, "CONNECT", head.Target, 200, null);
        await HttpWire.WriteAsync(s, "HTTP/1.1 200 Connection Established\r\n\r\n", ct);
        await s.FlushAsync(ct);
        var a = s.CopyToAsync(up, ct);
        var b = up.CopyToAsync(s, ct);
        await Task.WhenAny(a, b);
    }

    private IConnectDialer Dialer => _host.Options.ConnectDialer ?? SocketConnectDialer.Instance;

    private static (string? Host, int? Port) SplitAuthority(string target)
    {
        string host, portText;
        if (target.StartsWith('['))
        {
            var close = target.IndexOf(']');
            if (close < 0 || close + 1 >= target.Length || target[close + 1] != ':') return (null, null);
            host = target[1..close];
            portText = target[(close + 2)..];
        }
        else
        {
            var colon = target.LastIndexOf(':');
            if (colon <= 0) return (null, null);
            host = target[..colon];
            portText = target[(colon + 1)..];
        }
        return int.TryParse(portText, out var port) && port is > 0 and < 65536 && host.Length > 0 ? (host, port) : (null, null);
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
    }

    // ------------------------------------------------------------------ helpers

    private static string? Bearer(WireRequest head)
    {
        var auth = head.Header("Authorization");
        return auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
    }

    private static bool IsCredentialHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase);

    /// <summary>The run token as the CLI presents it: Authorization: Bearer, or x-api-key.</summary>
    private static string? Presented(WireRequest head) => Unshaped(Bearer(head) ?? head.Header("x-api-key"));

    /// <summary>Copilot presents the run token GitHub-shaped (gho_&lt;run token&gt;); the token itself is what is checked.</summary>
    private static string? Unshaped(string? token) =>
        token is not null && token.StartsWith("gho_" + RunTokenRegistry.Prefix, StringComparison.Ordinal) ? token[4..] : token;

    private static string Redact(string target) => target.Length > 512 ? target[..512] : target;

    /// <summary>What a plain-http refusal adds: the gateway forwards only https (CONNECT), even for an allowed host.</summary>
    public const string PlainHttpNote = "Only https is forwarded: plain http is refused even for an allowed host; use the https:// address.";

    /// <summary>
    /// A real outside host a plain-http refusal may point at its https address: a host the person could be asked to
    /// allow (<see cref="Integrations.NetworkAskFilter"/>). Not a gateway virtual host (*.yawble.internal), an IP address,
    /// a loopback or private-network name, the app's own hosts, or a model vendor or sign-in host: none of those is
    /// reachable over https either, so pointing at https would mislead.
    /// </summary>
    internal static bool IsOutsideHost(string vhost)
    {
        var h = vhost.TrimEnd('.');
        return h.Length > 0 && Integrations.NetworkAskFilter.Problem(h) is null;
    }

    /// <summary>The gateway log's detail for an allowlist miss (GatewayVerdicts and the Logs dialog read it).</summary>
    public const string NotOnAllowlist = "host not on this agent's allowlist";

    /// <summary>
    /// Reports an allowlist miss to the network asks (never for the doctor's quiet self-test) and returns what the agent is
    /// told. The refused name is never looked up here: a lookup driven by the agent's request would carry whatever it puts
    /// in the name to that domain's DNS server, refused or not. A name that resolves only to private addresses is caught
    /// when the person answers instead (NetworkAskService.AnswerAsync).
    /// </summary>
    private Task<string> AllowlistRefusalAsync(string host, string method, string? path, CancellationToken ct)
    {
        var outcome = HostAskOutcome.None;
        if (!_spec.Quiet && _host.Refusals is { } refusals)
        {
            try
            {
                outcome = refusals.Refused(_spec.Member, host.TrimEnd('.').ToLowerInvariant(), method, path);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The ask store failing must never turn a refusal into anything else: it stays a plain refusal.
                outcome = HostAskOutcome.None;
            }
        }
        return Task.FromResult(AllowlistText(host, outcome));
    }

    /// <summary>What the agent reads in the 403 for an allowlist miss, by what became of the ask.</summary>
    public static string AllowlistText(string host, HostAskOutcome outcome) => outcome switch
    {
        HostAskOutcome.Asked or HostAskOutcome.Pending =>
            $"{NotOnAllowlist}. The person is asked to allow {host} (Needs you). Call the request_host tool (host, reason) to say what you need it for, " +
            $"then hand back blocked on {host}: a host the person allows is reachable from your next run. Do not retry in a loop or try another site.",
        HostAskOutcome.Denied =>
            $"{NotOnAllowlist}. The person denied {host} for you in the last 24 hours. Do not try another site: carry on without it, or hand back blocked on {host}.",
        HostAskOutcome.AllowedNextRun =>
            $"{NotOnAllowlist} for this run; the person has allowed {host} from your next run. Hand back blocked on {host} so the work is retried in a new run; do not retry in this one.",
        _ => NotOnAllowlist,
    };

    private static string Error(string message) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "error",
            ["error"] = new Dictionary<string, string> { ["type"] = "yawble_gateway", ["message"] = message },
        });

    private async Task DrainAndRespondAsync(Stream s, WireRequest head, int status, string message, CancellationToken ct)
    {
        try
        {
            await HttpWire.ReadBodyAsync(s, head, _host.Options.MaxRequestBody, ct);
        }
        catch (WireException)
        {
        }
        await HttpWire.RespondJsonAsync(s, status, HttpWire.Reason(status), Error(message), false, ct);
    }

    private void Write(string kind, bool guardrail, string? vhost, string? method, string? target, int? status, string? detail)
    {
        if (!_spec.Quiet)
            WriteLog(kind, guardrail, vhost, method, target, status, detail);
    }

    private void WriteLog(string kind, bool guardrail, string? vhost, string? method, string? target, int? status, string? detail) =>
        _host.Log.Write(new GatewayEvent(_spec.RunId, _spec.Member, _host.Log.Now, kind, guardrail, vhost, method, target, status, detail));

    /// <summary>Stops listening, revokes the run token and closes every connection the run had open.</summary>
    public async ValueTask DisposeAsync()
    {
        _host.Tokens.Revoke(_spec.RunId);
        await _stop.CancelAsync();
        _listener.Stop();
        Task[] open;
        lock (_lock) open = [.. _connections];
        try
        {
            await Task.WhenAll(open.Append(_accept ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
        _stop.Dispose();
    }
}
