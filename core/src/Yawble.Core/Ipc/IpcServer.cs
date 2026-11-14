using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Ipc;

/// <summary>
/// Accepts shell connections and answers JSON requests. The first request on a connection must be
/// {"method":"auth","params":{"token":"..."}} with the per-launch token; anything else closes it.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly IpcEndpoint _endpoint;
    private readonly byte[] _token;
    private readonly RpcDispatcher _dispatcher;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly Ui.AppEvents? _events;
    private readonly Integrations.HostEvents? _host;
    private readonly Integrations.ShellWebsiteHost? _websites;
    private readonly Action<long>? _signedIn;

    /// <summary>Window calls that run for minutes (installers): each is answered when it finishes, off the connection's read loop.</summary>
    internal static readonly HashSet<string> LongRunning = ["tools.install", "git.install"];

    private IpcServer(IpcEndpoint endpoint, string token, RpcDispatcher dispatcher, Ui.AppEvents? events = null,
        Integrations.HostEvents? host = null, Integrations.ShellWebsiteHost? websites = null, Action<long>? signedIn = null)
    {
        _signedIn = signedIn;
        _events = events;
        _host = host;
        _websites = websites;
        _endpoint = endpoint;
        _token = Encoding.UTF8.GetBytes(token);
        _dispatcher = dispatcher;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string Address => _endpoint.Address;

    public static IpcServer Start(IpcEndpoint endpoint, string token, RpcDispatcher dispatcher) =>
        new(endpoint, token, dispatcher);

    /// <summary>Also pushes <paramref name="events"/> to every authenticated connection (frames with no id).</summary>
    public static IpcServer Start(IpcEndpoint endpoint, string token, RpcDispatcher dispatcher, Ui.AppEvents events) =>
        new(endpoint, token, dispatcher, events);

    /// <summary>
    /// Also pushes <paramref name="host"/> events, which only local connections ever see (the Cloud Link is never given them), and
    /// answers hostBridge.hello / hostBridge.reply for <paramref name="websites"/> itself: it knows which connection is the bridge, and a
    /// reply carries page text and screenshots that must not reach the audit log.
    /// </summary>
    public static IpcServer Start(IpcEndpoint endpoint, string token, RpcDispatcher dispatcher, Ui.AppEvents events,
        Integrations.HostEvents? host, Integrations.ShellWebsiteHost? websites = null, Action<long>? websiteSignedIn = null) =>
        new(endpoint, token, dispatcher, events, host, websites, websiteSignedIn);

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
            _ = Task.Run(() => ServeAsync(stream));
        }
    }

    private async Task ServeAsync(Stream stream)
    {
        await using var _ = stream;
        var writeLock = new SemaphoreSlim(1, 1);
        var authenticated = false;
        IDisposable? subscription = null;
        IDisposable? hostSubscription = null;
        IDisposable? bridge = null;
        try
        {
            while (await FrameCodec.ReadAsync(stream, _stop.Token).ConfigureAwait(false) is { } frame)
            {
                if (frame.Kind != FrameKind.Json) return; // the server takes JSON frames only
                JsonObject? request;
                try
                {
                    request = JsonNode.Parse(frame.Payload.Span) as JsonObject;
                }
                catch (JsonException)
                {
                    return;
                }
                if (request is null) return;
                var id = request["id"]?.DeepClone();
                var method = request["method"]?.GetValue<string>() ?? "";
                var parameters = request["params"] as JsonObject;

                JsonObject response;
                if (!authenticated)
                {
                    authenticated = method == "auth" && TokenMatches(parameters?["token"]?.GetValue<string>());
                    response = authenticated
                        ? Ok(id, new JsonObject { ["authenticated"] = true })
                        : Error(id, "unauthorized", "the first request must be auth with this launch's token");
                    await SendAsync(stream, writeLock, response).ConfigureAwait(false);
                    if (!authenticated) return;
                    subscription = _events?.Subscribe(message => SendAsync(stream, writeLock, message));
                    continue;
                }

                if (method == "hostBridge.hello" && _websites is not null)
                {
                    // host events (website requests) go to the bridge only, never to the window's main connection; subscribed before the
                    // answer, so a request pushed right after hello is not missed
                    hostSubscription ??= _host?.Subscribe(message => SendAsync(stream, writeLock, message));
                    bridge ??= _websites.BridgeConnected();
                    await SendAsync(stream, writeLock, Ok(id, new JsonObject { ["ok"] = true })).ConfigureAwait(false);
                    continue;
                }
                if (method == "hostBridge.reply" && _websites is not null)
                {
                    // non-blocking: hand the answer to the waiting request and return
                    if (bridge is null)
                        response = Error(id, "forbidden", "not the bridge");
                    else if (parameters?["id"] is JsonValue idv && idv.TryGetValue<string>(out var replyId) && replyId.Length > 0)
                    {
                        _websites.Reply(replyId, parameters);
                        response = Ok(id, new JsonObject { ["ok"] = true });
                    }
                    else
                        response = Error(id, "invalid", "hostBridge.reply needs an id");
                    await SendAsync(stream, writeLock, response).ConfigureAwait(false);
                    continue;
                }

                if (method == "hostBridge.signedIn" && _websites is not null)
                {
                    // the window closed a sign-in: the check runs in the background (its reply comes back on this very connection), so return at once
                    if (bridge is null)
                        response = Error(id, "forbidden", "not the bridge");
                    else if (parameters?["connectionId"] is JsonValue cv && Ui.UiRpc.WholeNumber(cv, out var signedInId))
                    {
                        _signedIn?.Invoke(signedInId);
                        response = Ok(id, new JsonObject { ["ok"] = true });
                    }
                    else
                        response = Error(id, "invalid", "hostBridge.signedIn needs a connectionId");
                    await SendAsync(stream, writeLock, response).ConfigureAwait(false);
                    continue;
                }

                if (LongRunning.Contains(method))
                {
                    // Minutes-long (an installer): answered on its own when it finishes, so the window's other calls on
                    // this connection are not held behind it. Replies carry their id; the write lock keeps frames whole.
                    var (m, p, i) = (method, parameters, id);
                    var answering = Task.Run(async () =>
                    {
                        JsonObject reply;
                        try { reply = Ok(i, await _dispatcher.DispatchAsAsync(Provenance.Actor.Window, m, p, _stop.Token).ConfigureAwait(false)); }
                        catch (RpcException e) { reply = Error(i, e.Code, e.Message); }
                        catch (Exception e) { reply = Error(i, "internal", $"{e.GetType().Name}: {e.Message}"); }
                        try { await SendAsync(stream, writeLock, reply).ConfigureAwait(false); }
                        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { /* the window went away */ }
                    });
                    continue;
                }

                try
                {
                    response = Ok(id, await _dispatcher.DispatchAsAsync(Provenance.Actor.Window, method, parameters, _stop.Token).ConfigureAwait(false));
                }
                catch (RpcException e)
                {
                    response = Error(id, e.Code, e.Message);
                }
                catch (Exception e)
                {
                    response = Error(id, "internal", $"{e.GetType().Name}: {e.Message}");
                }
                await SendAsync(stream, writeLock, response).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        {
        }
        finally
        {
            subscription?.Dispose();
            hostSubscription?.Dispose();
            bridge?.Dispose();
        }
    }

    private bool TokenMatches(string? presented) =>
        presented is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _token);

    private static JsonObject Ok(JsonNode? id, JsonNode? result) => new() { ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, string code, string message) =>
        new() { ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private async Task SendAsync(Stream stream, SemaphoreSlim writeLock, JsonObject message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await writeLock.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(stream, new Frame(FrameKind.Json, bytes), _stop.Token).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _endpoint.DisposeAsync().ConfigureAwait(false);
        try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}

public sealed class RpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
