using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Ipc;

namespace Yawble.Core.Link;

/// <summary>What the core keeps about the Cloud Link it is attached to (data dir, cloud-link.json).</summary>
public sealed record LinkSettings(
    string Url,
    string InstanceId,
    string Secret,
    string? InstanceName,
    string? Org,
    string? Workgroup,
    bool AllowHostTerminals = false,
    // "Show members' code to cloud viewers" (Settings > Cloud Link). On by default.
    bool ShowCode = true);

/// <summary>
/// The Cloud Link: an optional, generic connection from this instance to a cloud site of the person's
/// choosing (any URL; nothing is built in), so they can see and drive this instance from a browser elsewhere. The app
/// works fully without it.
/// <list type="bullet">
/// <item>Attaching: the core asks the site for a short pairing code and shows it; the person types it on the site (in
///   the group the instance should join). Nothing is pasted into the app. The site answers the core's poll with this
///   instance's id and secret, kept in the data folder.</item>
/// <item>Connected: ONE outbound WebSocket (no listening port). The site relays each viewer's JSON-RPC call
///   ({type:"call", session, id, method, params, user}); the core answers it through the same dispatcher as the
///   desktop window, after <see cref="RemoteAccess"/>. While anyone is viewing, the core's pushes (terminal output,
///   state) go up as {type:"event"}.</item>
/// <item>Every remote call that changes something is written to cloud-link-audit.log with who made it.</item>
/// </list>
/// </summary>
public sealed class CloudLink : IAsyncDisposable
{
    public const string StateEvent = "link.state";
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _settingsFile;
    private readonly string _auditFile;
    private readonly Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> _dispatch;
    private readonly Ui.AppEvents _events;
    private readonly Action<string>? _log;
    private readonly Lock _lock = new();
    private readonly Lock _auditLock = new();

    private LinkSettings? _settings;
    private string _state = "off";
    private string? _error;
    private (string Url, string Code, string ExpiresAt)? _pairing;
    private string[] _viewers = [];
    private string? _cloudVersion;
    private CancellationTokenSource? _work;
    private Task _running = Task.CompletedTask;

    /// <summary>Whether a CLI name is already an integration here (set by the host); null: none is, for the Cloud Link's checks.</summary>
    public Func<string, bool>? IsIntegration { get; set; }

    /// <summary>Whether an MCP server's name is a remote (http) one (set by the host); a viewer may not discover it.</summary>
    public Func<string, bool>? IsRemoteMcp { get; set; }

    public CloudLink(string dataDir, Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> dispatch, Ui.AppEvents events,
        Action<string>? log = null)
    {
        _settingsFile = Path.Combine(dataDir, "cloud-link.json");
        _auditFile = Path.Combine(dataDir, "cloud-link-audit.log");
        _dispatch = dispatch;
        _events = events;
        _log = log;
        // Before the rename to Cloud Link these files were management-link.json and management-link-audit.log.
        MoveOld(Path.Combine(dataDir, "management-link.json"), _settingsFile);
        MoveOld(Path.Combine(dataDir, "management-link-audit.log"), _auditFile);
        try
        {
            if (File.Exists(_settingsFile)) _settings = JsonSerializer.Deserialize<LinkSettings>(File.ReadAllText(_settingsFile), Json);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Cloud Link: settings unreadable, ignored: {e.Message}");
        }
    }

    public string AuditFile => _auditFile;

    private void MoveOld(string from, string to)
    {
        try { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log?.Invoke($"Cloud Link: {Path.GetFileName(from)} not moved: {e.Message}"); }
    }

    /// <summary>Connects when this instance was attached before. Does nothing otherwise.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_settings is null) return;
            Restart(ct => ConnectLoopAsync(ct));
        }
    }

    /// <summary>The cloud site the dialog offers first. The person can type another (a site of their own, or a local one).</summary>
    public const string DefaultSiteUrl = "https://cloud.yawble.ai/";

    /// <summary>The site's page that finishes pairing in the person's browser: sign in, choose a workgroup, attach.</summary>
    public static string PairPageUrl(string site, string code) => new Uri(new Uri(site), $"pair?code={Uri.EscapeDataString(code)}").ToString();

    /// <summary>
    /// The cloud site's version (yyyy.mm.dd.N, or its dev build), from its "welcome" after this computer's hello; null
    /// until a site says it (an older site never does) and once the link is not connected.
    /// </summary>
    public string? CloudVersion
    {
        get { lock (_lock) return _state == "connected" ? _cloudVersion : null; }
    }

    public JsonObject Status()
    {
        lock (_lock)
        {
            return new JsonObject
            {
                ["state"] = _state, // off | pairing | connecting | connected | offline
                ["url"] = _settings?.Url ?? _pairing?.Url,
                ["defaultUrl"] = DefaultSiteUrl,
                ["pairUrl"] = _pairing is { } p ? PairPageUrl(p.Url, p.Code) : null,
                ["machineName"] = Environment.MachineName,
                ["code"] = _pairing?.Code,
                ["codeExpiresAt"] = _pairing?.ExpiresAt,
                ["instanceId"] = _settings?.InstanceId,
                ["instanceName"] = _settings?.InstanceName,
                ["org"] = _settings?.Org,
                ["workgroup"] = _settings?.Workgroup,
                ["allowHostTerminals"] = _settings?.AllowHostTerminals ?? false,
                ["showCode"] = _settings?.ShowCode ?? true,
                ["viewers"] = new JsonArray(_viewers.Select(v => (JsonNode?)v).ToArray()),
                ["cloudVersion"] = _state == "connected" ? _cloudVersion : null,
                ["error"] = _error,
            };
        }
    }

    /// <summary>The site's address as the person typed it: https, or http to this computer only (a local site).</summary>
    public static Uri ParseSiteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            throw new RpcException("bad_params", "enter the cloud site's address, for example https://manage.example.com");
        if (uri.Scheme == Uri.UriSchemeHttps) { }
        else if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) { }
        else throw new RpcException("bad_params", "the cloud site must use https (http only for a site on this computer)");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new RpcException("bad_params", "the address must not carry a user name or password");
        var text = uri.GetLeftPart(UriPartial.Path);
        return new Uri(text.EndsWith('/') ? text : text + "/");
    }

    /// <summary>Asks the site for a pairing code. The person finishes on the site (its pair page, opened with the code filled in,
    /// or by typing the code); this polls until they have. Returns the status with the code and the pair page.</summary>
    public async Task<JsonObject> StartPairingAsync(string? url, CancellationToken ct)
    {
        var site = ParseSiteUrl(url);
        lock (_lock)
        {
            if (_settings is not null) throw new RpcException("busy", "this computer is already attached; detach it first");
        }
        using var response = await Http.PostAsJsonAsync(new Uri(site, "api/link/pair"), new JsonObject
        {
            ["name"] = Environment.MachineName,
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["version"] = CoreInfo.Version,
        }, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new RpcException("link_failed", $"the cloud site refused the pairing request ({(int)response.StatusCode})");
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false)
                   ?? throw new RpcException("link_failed", "the cloud site sent no pairing code");
        var pairingId = body["pairingId"]!.GetValue<string>();
        var pollSecret = body["pollSecret"]!.GetValue<string>();
        var code = body["code"]!.GetValue<string>();
        var expires = body["expiresAt"]?.GetValue<string>() ?? "";
        lock (_lock)
        {
            _pairing = (site.ToString(), code, expires);
            _error = null;
            _state = "pairing";
            Restart(token => PollPairingAsync(site, pairingId, pollSecret, token));
        }
        PublishState();
        return Status();
    }

    public void CancelPairing()
    {
        lock (_lock)
        {
            if (_state != "pairing") return;
            _work?.Cancel();
            _pairing = null;
            _state = "off";
        }
        PublishState();
    }

    /// <summary>Detaches this computer: closes the link, tells the site (best effort), forgets the id and secret.</summary>
    public async Task ForgetAsync(CancellationToken ct)
    {
        LinkSettings? s;
        lock (_lock)
        {
            s = _settings;
            _work?.Cancel();
        }
        try { await _running.ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (s is not null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(s.Url), "api/link/detach"));
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {s.InstanceId}.{s.Secret}");
                using var _ = await Http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                _log?.Invoke($"Cloud Link: the site was not told about the detach: {e.Message}");
            }
        }
        lock (_lock)
        {
            _settings = null;
            _pairing = null;
            _viewers = [];
            _state = "off";
            _error = null;
            TryDelete(_settingsFile);
        }
        PublishState();
    }

    public void SetAllowHostTerminals(bool allow)
    {
        lock (_lock)
        {
            if (_settings is null) throw new RpcException("bad_state", "this computer is not attached to a cloud site");
            _settings = _settings with { AllowHostTerminals = allow };
            Save(_settings);
        }
        PublishState();
    }

    /// <summary>"Show members' code to cloud viewers": off refuses every code. call from the link and keeps code. pushes on this computer.</summary>
    public void SetShowCode(bool show)
    {
        lock (_lock)
        {
            if (_settings is null) throw new RpcException("bad_state", "this computer is not attached to a cloud site");
            _settings = _settings with { ShowCode = show };
            Save(_settings);
        }
        PublishState();
    }

    /// <summary>Whether cloud viewers may use the code browser now.</summary>
    public bool ShowCode
    {
        get { lock (_lock) return _settings?.ShowCode ?? true; }
    }

    private void Restart(Func<CancellationToken, Task> work)
    {
        _work?.Cancel();
        var cts = _work = new CancellationTokenSource();
        var previous = _running;
        _running = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); } catch (Exception) { }
            await work(cts.Token).ConfigureAwait(false);
        });
    }

    private async Task PollPairingAsync(Uri site, string pairingId, string pollSecret, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollEvery, ct).ConfigureAwait(false);
                HttpResponseMessage response;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(site, $"api/link/pair/{Uri.EscapeDataString(pairingId)}"));
                    request.Headers.TryAddWithoutValidation("X-Poll-Secret", pollSecret);
                    response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException e)
                {
                    SetState("pairing", $"cannot reach the cloud site: {e.Message}");
                    continue;
                }
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.Accepted) continue;
                    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    {
                        lock (_lock) _pairing = null;
                        SetState("off", "the pairing code expired before it was entered; connect again for a new one");
                        return;
                    }
                    if (!response.IsSuccessStatusCode) continue;
                    var body = (await response.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false))!;
                    var settings = new LinkSettings(site.ToString(), body["instanceId"]!.GetValue<string>(), body["secret"]!.GetValue<string>(),
                        body["instanceName"]?.GetValue<string>(), body["org"]?.GetValue<string>(), body["workgroup"]?.GetValue<string>());
                    lock (_lock)
                    {
                        _settings = settings;
                        _pairing = null;
                        Save(settings);
                    }
                    _log?.Invoke($"Cloud Link: attached to {site} as {settings.InstanceName} ({settings.Workgroup})");
                    await ConnectLoopAsync(ct).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            LinkSettings? s;
            lock (_lock) s = _settings;
            if (s is null) return;
            SetState("connecting", null);
            using var ws = new ClientWebSocket();
            ws.Options.CollectHttpResponseDetails = true;
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            ws.Options.SetRequestHeader("Authorization", $"Bearer {s.InstanceId}.{s.Secret}");
            try
            {
                await ws.ConnectAsync(WebSocketUri(s.Url), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException)
            {
                if (ws.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    lock (_lock)
                    {
                        _settings = null;
                        TryDelete(_settingsFile);
                    }
                    SetState("off", "the cloud site no longer knows this computer (it was detached there)");
                    return;
                }
                SetState("offline", $"cannot reach the cloud site: {e.Message}");
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                continue;
            }
            delay = TimeSpan.FromSeconds(1);
            SetState("connected", null);
            await ServeAsync(ws, s, ct).ConfigureAwait(false);
            lock (_lock) { _viewers = []; _cloudVersion = null; }
            if (ct.IsCancellationRequested) return;
            SetState("offline", "the connection to the cloud site closed; reconnecting");
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ServeAsync(ClientWebSocket ws, LinkSettings s, CancellationToken ct)
    {
        var sendLock = new SemaphoreSlim(1, 1);
        var watching = 0;
        async Task Send(JsonObject message)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        using var subscription = _events.Subscribe(async message =>
        {
            if (Volatile.Read(ref watching) == 0 || ws.State != WebSocketState.Open) return;
            if (!Forwards(message, ShowCode)) return;
            message["type"] = "event";
            await Send(message).ConfigureAwait(false);
        });
        try
        {
            await Send(new JsonObject
            {
                ["type"] = "hello",
                ["name"] = Environment.MachineName,
                ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ["version"] = CoreInfo.Version,
            }).ConfigureAwait(false);
            while (!ct.IsCancellationRequested && await ReceiveAsync(ws, ct).ConfigureAwait(false) is { } text)
            {
                if (JsonNode.Parse(text) is not JsonObject message) continue;
                switch (message["type"]?.GetValue<string>())
                {
                    case "call":
                        _ = Task.Run(() => AnswerAsync(message, Send, ct), ct);
                        break;
                    // The site's answer to the hello (newer sites only): its version, shown in Settings > System > About.
                    case "welcome":
                        lock (_lock) _cloudVersion = WelcomeVersion(message);
                        PublishState();
                        break;
                    case "viewers":
                        var users = (message["users"] as JsonArray ?? []).Select(u => u?.GetValue<string>() ?? "?").ToArray();
                        Volatile.Write(ref watching, users.Length);
                        lock (_lock) _viewers = users;
                        PublishState();
                        break;
                    case "detached":
                        lock (_lock)
                        {
                            _settings = null;
                            TryDelete(_settingsFile);
                        }
                        SetState("off", "this computer was detached on the cloud site");
                        _work?.Cancel();
                        return;
                    case "renamed":
                        lock (_lock)
                        {
                            if (_settings is null) break;
                            _settings = _settings with
                            {
                                InstanceName = message["instanceName"]?.GetValue<string>() ?? _settings.InstanceName,
                                Org = message["org"]?.GetValue<string>() ?? _settings.Org,
                                Workgroup = message["workgroup"]?.GetValue<string>() ?? _settings.Workgroup,
                            };
                            Save(_settings);
                        }
                        PublishState();
                        break;
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException or IOException)
        {
            _log?.Invoke($"Cloud Link: connection ended: {e.Message}");
        }
    }

    private async Task AnswerAsync(JsonObject call, Func<JsonObject, Task> send, CancellationToken ct)
    {
        var method = call["method"]?.GetValue<string>() ?? "";
        var parameters = call["params"] as JsonObject;
        var user = call["user"]?.GetValue<string>() ?? "unknown";
        var reply = new JsonObject { ["type"] = "result", ["session"] = call["session"]?.DeepClone(), ["id"] = call["id"]?.DeepClone() };
        bool allowHost, showCode;
        lock (_lock)
        {
            allowHost = _settings?.AllowHostTerminals ?? false;
            showCode = _settings?.ShowCode ?? true;
        }
        var refusal = RemoteAccess.Refusal(method, parameters, allowHost, IsIntegration, IsRemoteMcp, showCode);
        if (refusal is not null)
        {
            reply["error"] = new JsonObject { ["code"] = "remote_refused", ["message"] = refusal };
        }
        else
        {
            try
            {
                parameters?.Parent?.AsObject().Remove("params");
                // The call runs as this viewer: what it records says the cloud, and who.
                using var acting = Provenance.Actor.Use(Provenance.Actor.CloudViewer(user));
                reply["result"] = await _dispatch(method, parameters, ct).ConfigureAwait(false);
            }
            catch (RpcException e)
            {
                reply["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                reply["error"] = new JsonObject { ["code"] = "internal", ["message"] = $"{e.GetType().Name}: {e.Message}" };
            }
        }
        Audit(user, method, refusal);
        try { await send(reply).ConfigureAwait(false); } catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void Audit(string user, string method, string? refusal)
    {
        if (refusal is null && Provenance.AuditLog.IsReadOnly(method)) return;
        var line = $"{DateTimeOffset.UtcNow:O}\t{user.Replace('\t', ' ')}\t{method}\t{(refusal is null ? "done" : "refused: " + refusal)}{Environment.NewLine}";
        lock (_auditLock)
        {
            try { File.AppendAllText(_auditFile, line); }
            catch (IOException e) { _log?.Invoke($"Cloud Link: audit log not written: {e.Message}"); }
        }
    }

    private static async Task<string?> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, r.Count);
            if (message.Length > 16 * 1024 * 1024) throw new IOException("a message from the cloud site is too large");
            if (r.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    private static Uri WebSocketUri(string site)
    {
        var b = new UriBuilder(new Uri(new Uri(site), "api/link/ws"));
        b.Scheme = b.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        return b.Uri;
    }

    private void SetState(string state, string? error)
    {
        lock (_lock)
        {
            _state = state;
            _error = error;
        }
        PublishState();
    }

    /// <summary>
    /// Whether a push from the core goes up to the site: all but yawble-companion's (local only) and the agents'
    /// Terminal / Test sessions starting and ending (<see cref="Agents.AgentTestSessions.ChangedEvent"/>: window only).
    /// </summary>
    internal static bool Forwards(string? eventName) =>
        eventName?.StartsWith("companion.", StringComparison.Ordinal) != true && eventName != Agents.AgentTestSessions.ChangedEvent;

    /// <summary>
    /// Whether this push goes up: <see cref="Forwards(string?)"/>, and never an agent's Terminal / Test session's
    /// output or state (agent-test:&lt;cli&gt;): those are opened in the window only and stay on this computer
    /// (<see cref="RemoteAccess.IsWindowOnlyChannel"/>).
    /// </summary>
    internal static bool Forwards(JsonObject message, bool showCode = true) =>
        Forwards(message["event"]?.GetValue<string>())
        // code.changed (a path and its lines) goes up only for a session a cloud viewer opened (marked cloud: true),
        // and never with "Show members' code to cloud viewers" off: the window's sessions stay on this computer.
        && (message["event"]?.GetValue<string>()?.StartsWith("code.", StringComparison.Ordinal) != true
            || (showCode && (message["data"] as JsonObject)?["cloud"] is JsonValue cloud && cloud.TryGetValue<bool>(out var up) && up))
        && !((message["data"] as JsonObject)?["channel"] is JsonValue v && v.TryGetValue<string>(out var channel) && RemoteAccess.IsWindowOnlyChannel(channel));

    /// <summary>The site's version from its welcome message: a short string, or null when missing or not a string.</summary>
    internal static string? WelcomeVersion(JsonObject message) =>
        message["version"] is JsonValue v && v.TryGetValue<string>(out var text) && text.Trim() is { Length: > 0 and <= 80 } t ? t : null;

    private void PublishState() => _events.Publish(StateEvent, Status());

    private void Save(LinkSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        File.WriteAllText(_settingsFile, JsonSerializer.Serialize(s, Json));
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock) _work?.Cancel();
        try { await _running.ConfigureAwait(false); } catch (Exception) { }
    }
}
