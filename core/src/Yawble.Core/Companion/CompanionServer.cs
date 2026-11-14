using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Concierge;
using Yawble.Core.Ipc;

namespace Yawble.Core.Companion;

/// <summary>
/// yawble-companion's control endpoint: its own pipe or socket (never the window's, never handed to the Cloud Link), the
/// same <see cref="FrameCodec"/> frames. Before auth a connection may send only <see cref="CompanionProtocol.Hello"/>,
/// <see cref="CompanionProtocol.PairRequest"/> or <see cref="CompanionProtocol.Auth"/>{client, key}, each frame at most
/// <see cref="CompanionProtocol.MaxUnauthenticatedFrame"/> bytes and within a short idle limit; anything else is answered
/// "unauthorized" (or not at all) and closed. The shell's per-launch token is no key here: only a paired client's key is.
/// After auth only the four tool-level methods are served, all through <see cref="CompanionService"/>; nothing ever reaches
/// the <see cref="RpcDispatcher"/>. Revoking a client closes every session it has open.
/// </summary>
public sealed partial class CompanionServer : IAsyncDisposable
{
    /// <summary>
    /// How long an unauthenticated connection may wait between frames (<paramref name="PreAuthIdle"/>) and in all, hellos
    /// included (<paramref name="PreAuthTotal"/>; a pairing's wait for the person is not counted); how many may be
    /// unauthenticated at once (<paramref name="MaxPreAuth"/>, more are closed on accept); how long one write may take
    /// before the connection is given up (<paramref name="WriteDeadline"/>: a client that stops reading).
    /// </summary>
    internal sealed record Limits(TimeSpan PreAuthIdle, TimeSpan PreAuthTotal, int MaxPreAuth, TimeSpan WriteDeadline)
    {
        public static readonly Limits Default = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), 8, TimeSpan.FromSeconds(10));
    }

    private readonly IpcEndpoint _endpoint;
    private readonly CompanionService _service;
    private readonly Limits _limits;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Session, byte> _sessions = new();
    private readonly Task _acceptLoop;
    private int _preAuth;
    private int _unexpectedFaults;

    private CompanionServer(IpcEndpoint endpoint, CompanionService service, Limits limits)
    {
        _endpoint = endpoint;
        _service = service;
        _limits = limits;
        _service.Revoked += OnRevoked;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string Address => _endpoint.Address;

    public static CompanionServer Start(IpcEndpoint endpoint, CompanionService service) => new(endpoint, service, Limits.Default);

    /// <summary>For tests: other limits.</summary>
    internal static CompanionServer Start(IpcEndpoint endpoint, CompanionService service, Limits limits) => new(endpoint, service, limits);

    /// <summary>For tests: open sessions.</summary>
    internal int SessionCount => _sessions.Count;

    /// <summary>For tests: sessions that ended on an exception nothing expected (each also logged, type only).</summary>
    internal int UnexpectedFaults => Volatile.Read(ref _unexpectedFaults);

    /// <summary>For tests: runs after a pairing's outcome is known and before its reply is written (the pairing id).</summary>
    internal Action<string>? BeforePairReply { get; set; }

    /// <summary>A fresh, unguessable control address for one launch: \\.\pipe\yawble-ctl-&lt;16 hex&gt;, or a socket in <paramref name="runtimeDir"/>.</summary>
    public static string NewAddress(string runtimeDir)
    {
        var id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        return OperatingSystem.IsWindows() ? $@"\\.\pipe\yawble-ctl-{id}" : Path.Combine(runtimeDir, $"yawble-ctl-{id}.sock");
    }

    /// <summary>One connection. Closed by its own loop, by the server's shutdown, or (from any thread) by its client's revocation.</summary>
    internal sealed class Session
    {
        private readonly CancellationTokenSource _cts;
        private int _closed;

        public Session(Stream stream, CancellationToken serverStop)
        {
            Stream = stream;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(serverStop);
            Cancellation = _cts.Token;
        }

        public Stream Stream { get; }
        public CancellationToken Cancellation { get; }

        private int _preAuthSlot = 1;

        /// <summary>True once, for whoever gives back this session's unauthenticated slot (auth or the session's end).</summary>
        public bool LeavePreAuth() => Interlocked.Exchange(ref _preAuthSlot, 0) == 1;

        /// <summary>For tests: runs inside <see cref="Close"/> right after it claimed the close.</summary>
        internal Action? AfterClaim { get; set; }

        /// <summary>The authenticated client's name (null before auth).</summary>
        public volatile string? Client;

        /// <summary>Cancels whatever the session waits on and closes its stream. Safe from any thread, any number of times.</summary>
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            AfterClaim?.Invoke();
            // The session's own loop may Release (dispose the source) between the claim above and this cancel.
            try { _cts.Cancel(); } catch (Exception e) when (e is AggregateException or ObjectDisposedException) { }
            try { Stream.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }

        /// <summary>Only by the session's own loop, after <see cref="Close"/> (a later Close returns before touching the source).</summary>
        public void Release()
        {
            Close();
            _cts.Dispose();
        }
    }

    private void OnRevoked(string name)
    {
        foreach (var session in _sessions.Keys)
            if (session.Client == name) session.Close();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Stream stream;
            try
            {
                stream = await _endpoint.AcceptAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                if (_stop.IsCancellationRequested) return;
                continue;
            }
            if (Interlocked.Increment(ref _preAuth) > _limits.MaxPreAuth)
            {
                // Too many connections are unauthenticated already: turned away unread.
                Interlocked.Decrement(ref _preAuth);
                try { await stream.DisposeAsync().ConfigureAwait(false); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                continue;
            }
            var session = new Session(stream, _stop.Token);
            _sessions[session] = 0;
            if (_stop.IsCancellationRequested) session.Close(); // shut down while this one was being accepted
            _ = Task.Run(() => ServeAsync(session));
        }
    }

    private async Task ServeAsync(Session s)
    {
        try
        {
            var client = await PreAuthAsync(s).ConfigureAwait(false);
            if (client is null) return;
            if (s.LeavePreAuth()) Interlocked.Decrement(ref _preAuth);
            // Set before the check: a revocation raised before this line is seen by IsActive, one raised after it finds the name.
            s.Client = client.Name;
            if (!_service.IsActive(client)) return;
            await ServeAuthenticatedAsync(s, client).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        {
        }
        catch (Exception e)
        {
            // Nothing else is expected; the connection ends either way. Logged by type only: a message could carry a key.
            Interlocked.Increment(ref _unexpectedFaults);
            Console.Error.WriteLine($"companion: a control connection ended on an unexpected {e.GetType().Name}");
        }
        finally
        {
            _sessions.TryRemove(s, out _);
            if (s.LeavePreAuth()) Interlocked.Decrement(ref _preAuth);
            s.Release();
        }
    }

    // ------------------------------------------------------------------ before auth

    /// <summary>Hello any number of times, then pairing (which ends the connection) or auth. Null: the connection ends.</summary>
    private async Task<CompanionClient?> PreAuthAsync(Session s)
    {
        // Hellos do not extend it; a pairing's wait for the person (PairAsync, on the session's own token) is not under it.
        using var total = CancellationTokenSource.CreateLinkedTokenSource(s.Cancellation);
        total.CancelAfter(_limits.PreAuthTotal);
        while (true)
        {
            Frame? frame;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token))
            {
                idle.CancelAfter(_limits.PreAuthIdle);
                frame = await FrameCodec.ReadAsync(s.Stream, idle.Token, CompanionProtocol.MaxUnauthenticatedFrame).ConfigureAwait(false);
            }
            if (Parse(frame) is not { } request) return null;
            var (id, method, parameters) = request;
            switch (method)
            {
                case CompanionProtocol.Hello:
                    await SendAsync(s, Ok(id, new JsonObject
                    {
                        ["product"] = CompanionProtocol.Product, ["version"] = CoreInfo.Version, ["protocol"] = CompanionProtocol.Version,
                    })).ConfigureAwait(false);
                    continue;
                case CompanionProtocol.PairRequest:
                    await PairAsync(s, id, parameters).ConfigureAwait(false);
                    return null;
                case CompanionProtocol.Auth:
                {
                    var name = Text(parameters, "client");
                    var key = Text(parameters, "key");
                    var client = name is null || key is null ? null : _service.Authenticate(name, key);
                    if (client is null)
                    {
                        await SendAsync(s, Error(id, "unauthorized", "unknown client or wrong key (pair again with: yawble-companion pair --name <name>)")).ConfigureAwait(false);
                        return null;
                    }
                    await SendAsync(s, Ok(id, new JsonObject
                    {
                        ["authenticated"] = true, ["client"] = client.Name, ["level"] = CompanionLevels.Name(client.Level),
                    })).ConfigureAwait(false);
                    return client;
                }
                default:
                    await SendAsync(s, Error(id, "unauthorized", "before auth only hello, pair.request or auth is answered")).ConfigureAwait(false);
                    return null;
            }
        }
    }

    /// <summary>
    /// pair.request{name, level}: pushes the code, waits for the person's decision in the window, answers with the key (once)
    /// and ends. If the client hangs up (or sends anything) while waiting, the wait stops: the key is never taken for nobody.
    /// </summary>
    private async Task PairAsync(Session s, JsonNode? id, JsonObject? parameters)
    {
        var name = Text(parameters, "name");
        var levelText = Text(parameters, "level") ?? (parameters?["level"] is null ? "read" : null);
        if (name is null || !CompanionLevels.TryParse(levelText, out var level))
        {
            await SendAsync(s, Error(id, "bad_params", "pair.request needs a name and a level of read, act or destructive")).ConfigureAwait(false);
            return;
        }
        PairingTicket ticket;
        try
        {
            ticket = _service.BeginPairing(name, level);
        }
        catch (RpcException e)
        {
            await SendAsync(s, Error(id, e.Code, Safe(e.Message))).ConfigureAwait(false);
            return;
        }
        await SendAsync(s, new JsonObject
        {
            ["event"] = CompanionProtocol.PairCode,
            ["data"] = new JsonObject
            {
                ["id"] = ticket.Id, ["code"] = ticket.Code, ["expires"] = ticket.Expires.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            },
        }).ConfigureAwait(false);

        using var hangUp = CancellationTokenSource.CreateLinkedTokenSource(s.Cancellation);
        var watch = WatchHangUpAsync(s.Stream, hangUp);
        PairingOutcome outcome;
        try
        {
            outcome = await _service.WaitPairingAsync(ticket.Id, hangUp.Token).ConfigureAwait(false);
        }
        finally
        {
            await hangUp.CancelAsync().ConfigureAwait(false);
            await watch.ConfigureAwait(false);
        }
        var result = new JsonObject { ["approved"] = outcome.Approved, ["reason"] = outcome.Reason };
        if (outcome.Key is { } key) result["key"] = key;
        if (outcome.Level is { } granted) result["level"] = CompanionLevels.Name(granted);
        BeforePairReply?.Invoke(ticket.Id);
        try
        {
            await SendAsync(s, Ok(id, result)).ConfigureAwait(false);
        }
        catch when (outcome.Key is not null)
        {
            // The key was taken from the service and could not be written: nobody holds it now, so its row is revoked
            // (by id) and the name is free to pair again.
            _service.PairingUndelivered(ticket.Id);
            throw;
        }
    }

    /// <summary>Reads while a pairing waits: the end of the stream, an error or any frame cancels the wait.</summary>
    private static async Task WatchHangUpAsync(Stream stream, CancellationTokenSource hangUp)
    {
        try
        {
            await FrameCodec.ReadAsync(stream, hangUp.Token, CompanionProtocol.MaxUnauthenticatedFrame).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        {
        }
        try { await hangUp.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
    }

    // ------------------------------------------------------------------ after auth

    private async Task ServeAuthenticatedAsync(Session s, CompanionClient client)
    {
        while (Parse(await FrameCodec.ReadAsync(s.Stream, s.Cancellation, CompanionProtocol.MaxFrame).ConfigureAwait(false)) is { } request)
        {
            var (id, method, parameters) = request;
            JsonObject response;
            try
            {
                response = Ok(id, await DispatchAsync(client, method, parameters, s.Cancellation).ConfigureAwait(false));
            }
            catch (RpcException e)
            {
                response = Error(id, e.Code, Safe(e.Message));
            }
            catch (OperationCanceledException) when (s.Cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Anything a call lets escape (a git failure while preparing a plan, a fault in the app's route) is this
                // request's error, never a dropped connection or a crashed core.
                response = Error(id, "failed", $"the app could not complete this call ({e.GetType().Name}): {Safe(e.Message)}");
            }
            await SendAsync(s, response).ConfigureAwait(false);
        }
    }

    private async Task<JsonNode?> DispatchAsync(CompanionClient client, string method, JsonObject? parameters, CancellationToken ct)
    {
        switch (method)
        {
            case CompanionProtocol.WhoAmI:
                return new JsonObject
                {
                    ["client"] = client.Name, ["level"] = CompanionLevels.Name(client.Level), ["levelDescription"] = CompanionLevels.Describe(client.Level),
                };
            case CompanionProtocol.Tools:
                return new JsonObject { ["tools"] = CompanionTools.Definitions(client.Level) };
            case CompanionProtocol.Call:
            {
                var tool = Text(parameters, "tool") ?? throw new RpcException("bad_params", "companion.call needs a tool");
                var args = parameters?["args"] switch
                {
                    null => [],
                    JsonObject o => o,
                    _ => throw new RpcException("bad_params", "companion.call args must be an object"),
                };
                var r = await _service.CallAsync(client, tool, args, ct).ConfigureAwait(false);
                var result = new JsonObject { ["text"] = r.Text, ["isError"] = r.IsError };
                if (r.RequestId is { } requestId) result["requestId"] = requestId;
                return result;
            }
            case CompanionProtocol.Request:
            {
                var id = Text(parameters, "id") ?? throw new RpcException("bad_params", "companion.request needs an id");
                var wait = 0;
                if (parameters?["waitSeconds"] is { } w)
                {
                    if (w is not JsonValue v || v.GetValueKind() != JsonValueKind.Number || !v.TryGetValue(out wait) || wait < 0 || wait > CompanionProtocol.MaxWaitSeconds)
                        throw new RpcException("bad_params", $"waitSeconds is a whole number from 0 to {CompanionProtocol.MaxWaitSeconds}");
                }
                var status = wait > 0
                    ? await _service.WaitAsync(client, id, TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false)
                    : _service.Status(client, id);
                if (status is null) throw new RpcException("not_found", $"there is no request '{id}' of this client");
                return new JsonObject
                {
                    ["id"] = status.Id, ["state"] = status.State.ToString().ToLowerInvariant(), ["plan"] = status.Plan,
                    ["result"] = status.Result, ["nextId"] = status.NextId,
                };
            }
            default:
                throw new RpcException("unknown_method", $"'{method}' is not a companion method (companion.whoami, companion.tools, companion.call, companion.request)");
        }
    }

    // ------------------------------------------------------------------ frames

    /// <summary>(id, method, params) of a JSON request frame; null for the end of the stream or anything that is not one.</summary>
    private static (JsonNode? Id, string Method, JsonObject? Params)? Parse(Frame? frame)
    {
        if (frame is not { Kind: FrameKind.Json } f) return null;
        try
        {
            // A duplicate key anywhere (a nested params or args object too) is refused while parsing, and every object is
            // walked here so nothing lazily parsed later can throw on one.
            if (JsonNode.Parse(f.Payload.Span, null, Strict) is not JsonObject request) return null;
            Walk(request);
            if (Text(request, "method") is not { } method) return null;
            return (request["id"]?.DeepClone(), method, request["params"] as JsonObject);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    private static void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, child) in o) Walk(child);
                break;
            case JsonArray a:
                foreach (var child in a) Walk(child);
                break;
        }
    }

    private static string? Text(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.TryGetValue<string>(out var s) ? s : null;

    [GeneratedRegex("\\b[0-9a-fA-F]{16,}\\b")]
    private static partial Regex HexRun();

    /// <summary>An exception's text as a client may see it: scrubbed of secrets, and no token-shaped hex.</summary>
    private static string Safe(string text) => HexRun().Replace(ConciergeReadTools.Scrub(text), "[withheld]");

    private static JsonObject Ok(JsonNode? id, JsonNode? result) => new() { ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, string code, string message) =>
        new() { ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    /// <summary>
    /// Writes one message within the write deadline (a client that stops reading cannot hold the session forever: the
    /// cancelled write ends it). Only a session's own loop writes, one message at a time.
    /// </summary>
    private async Task SendAsync(Session s, JsonObject message)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(s.Cancellation);
        deadline.CancelAfter(_limits.WriteDeadline);
        await FrameCodec.WriteAsync(s.Stream, new Frame(FrameKind.Json, Encoding.UTF8.GetBytes(message.ToJsonString())), deadline.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _service.Revoked -= OnRevoked;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _endpoint.DisposeAsync().ConfigureAwait(false);
        foreach (var session in _sessions.Keys) session.Close();
        try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
