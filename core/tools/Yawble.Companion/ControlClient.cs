using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Companion;
using Yawble.Core.Ipc;

namespace Yawble.Companion;

/// <summary>One connection to the running app's companion endpoint: requests {id, method, params}, events pushed in between.</summary>
internal interface IControlChannel : IAsyncDisposable
{
    /// <summary>
    /// Sends one request and returns its reply's result. Events that arrive first go to <paramref name="onEvent"/>. An error
    /// reply throws <see cref="ControlErrorException"/>; a connection that ends throws <see cref="BusyException"/> (nothing
    /// was ever heard on it) or <see cref="ConnectionLostException"/>.
    /// </summary>
    Task<JsonObject> CallAsync(string method, JsonObject? p, Action<JsonObject>? onEvent, CancellationToken ct);
}

internal interface IControlConnector
{
    /// <summary>A fresh, unauthenticated connection; <see cref="NotRunningException"/> when no app is there.</summary>
    Task<IControlChannel> ConnectAsync(CancellationToken ct);
}

/// <summary>Anything that ends a yawble command with a clear sentence and its exit code.</summary>
internal abstract class ClientException(int exitCode, string code, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
    public string Code { get; } = code;
}

internal sealed class NotRunningException() : ClientException(ExitCodes.NotRunning, "not_running",
    "Yawble is not running on this computer (or its companion endpoint is off). Start the Yawble app, then try again.");

internal sealed class BusyException() : ClientException(ExitCodes.Failed, "busy",
    "Yawble is busy; try again in a moment (too many connections are signing in to it right now).");

internal sealed class NotPairedException(string message) : ClientException(ExitCodes.NotPaired, "not_paired", message);

internal sealed class UsageException(string message) : ClientException(ExitCodes.Usage, "usage", message);

/// <summary>A home folder yawble may not use (a populated YAWBLE_HOME, no access).</summary>
internal sealed class HomeException(string message) : ClientException(ExitCodes.Failed, "home", message);

internal sealed class TimedOutException() : ClientException(ExitCodes.Failed, "timeout", "Yawble did not answer in time; try again.");

/// <summary>The connection ended after it had worked. <see cref="Sent"/>: the request may have reached the app.</summary>
internal sealed class ConnectionLostException(bool sent) : ClientException(ExitCodes.Failed, "connection_lost",
    sent ? "The connection to Yawble ended during the call (the app may have closed); it may or may not have run, so check before trying again."
         : "The connection to Yawble ended (the app may have closed or restarted); try again.")
{
    public bool Sent { get; } = sent;
}

/// <summary>The app no longer knows a request: it restarted (waiting requests live in memory only) or the request was cleared.</summary>
internal sealed class RequestLostException(string id) : ClientException(ExitCodes.Failed, "lost",
    $"Request {id} is gone: the app restarted (or cleared it), and a waiting request does not survive that. Nothing was done; ask again.");

/// <summary>An error reply from the app: {code, message}.</summary>
internal sealed class ControlErrorException(string code, string message) : ClientException(code switch
{
    "unauthorized" => ExitCodes.NotPaired,
    "bad_params" => ExitCodes.Usage,
    "forbidden" => ExitCodes.Refused,
    _ => ExitCodes.Failed,
}, code, message);

/// <summary>
/// Connects to the address the running core wrote to control.json: a named pipe on Windows (CurrentUserOnly, so a pipe some
/// other account created under that name is refused), the unix socket elsewhere. Bounded: no app within 3 s is "not running".
/// </summary>
internal sealed class PipeConnector(string home) : IControlConnector
{
    private const string PipePrefix = @"\\.\pipe\";
    internal static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(3);

    public async Task<IControlChannel> ConnectAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(home) || ControlDiscovery.Read(home) is not { } file) throw new NotRunningException();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(ConnectLimit);
        try
        {
            return new StreamChannel(await OpenAsync(file.Address, bounded.Token).ConfigureAwait(false));
        }
        catch (UnauthorizedAccessException)
        {
            throw new ControlErrorException("not_yours",
                "The endpoint named in Yawble's control.json belongs to another account on this computer, so yawble will not talk to it. Restart the Yawble app.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NotRunningException();
        }
        catch (Exception e) when (e is IOException or TimeoutException or SocketException or ArgumentException or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new NotRunningException();
        }
    }

    private static async Task<Stream> OpenAsync(string address, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!address.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase) || address.Length == PipePrefix.Length)
                throw new ArgumentException("not a pipe address");
            var pipe = new NamedPipeClientStream(".", address[PipePrefix.Length..], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync((int)ConnectLimit.TotalMilliseconds, ct).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        if (!Path.IsPathFullyQualified(address)) throw new ArgumentException("not a socket path");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(address), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// The protocol over one stream (FrameCodec frames of JSON). Writes are one at a time; one reader hands each reply to the
/// call waiting for its id, so a caller that stops waiting (cancelled, timed out) leaves the connection intact: the late
/// reply is dropped when it comes. Only a stream that fails breaks the channel; from then on every call throws
/// <see cref="ConnectionLostException"/>. A stream that ends before anything was heard on it is the core turning the
/// connection away unread (too many signing in): <see cref="BusyException"/>.
/// </summary>
internal sealed class StreamChannel(Stream stream) : IControlChannel
{
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    /// <summary>A write the other end does not take within this breaks the connection (it no longer reads).</summary>
    internal static readonly TimeSpan WriteLimit = TimeSpan.FromSeconds(10);

    private sealed record Pending(TaskCompletionSource<JsonObject> Reply, Action<JsonObject>? OnEvent);

    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly Lock _gate = new();
    private Task? _reader;
    private int _nextId;
    private volatile bool _heard;
    private volatile bool _broken;

    /// <summary>Whether the stream failed or ended (every later call throws <see cref="ConnectionLostException"/>).</summary>
    public bool IsBroken => _broken;

    public async Task<JsonObject> CallAsync(string method, JsonObject? p, Action<JsonObject>? onEvent, CancellationToken ct)
    {
        if (_broken) throw new ConnectionLostException(sent: false);
        var id = Interlocked.Increment(ref _nextId);
        var pending = new Pending(new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously), onEvent);
        _pending[id] = pending;
        try
        {
            await _write.WaitAsync(ct).ConfigureAwait(false); // cancelled here: nothing was written, nothing is broken
            try
            {
                if (_broken) throw new ConnectionLostException(sent: false);
                var request = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = p?.DeepClone() };
                // Not the caller's token: a write cut off midway would leave the stream mid-frame.
                using var deadline = new CancellationTokenSource(WriteLimit);
                try
                {
                    await FrameCodec.WriteAsync(stream, new Frame(FrameKind.Json, Encoding.UTF8.GetBytes(request.ToJsonString())), deadline.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
                {
                    Break();
                    throw _heard ? new ConnectionLostException(sent: false) : new BusyException();
                }
                StartReader();
                if (_broken) pending.Reply.TrySetException(new ConnectionLostException(sent: true)); // the reader ended just now
            }
            finally
            {
                _write.Release();
            }
            return await pending.Reply.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(new KeyValuePair<int, Pending>(id, pending));
        }
    }

    private void StartReader()
    {
        lock (_gate) _reader ??= Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        while (true)
        {
            Frame? frame;
            try
            {
                frame = await FrameCodec.ReadAsync(stream, CancellationToken.None, CompanionProtocol.MaxFrame).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or InvalidDataException or OperationCanceledException)
            {
                frame = null;
            }
            if (frame is not { Kind: FrameKind.Json } f)
            {
                var heard = _heard;
                Break();
                foreach (var (_, waiting) in _pending)
                    waiting.Reply.TrySetException(heard ? new ConnectionLostException(sent: true) : new BusyException());
                return;
            }
            _heard = true;
            JsonObject message;
            try
            {
                if (JsonNode.Parse(f.Payload.Span, null, Strict) is not JsonObject o) continue;
                message = o;
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
            {
                continue;
            }
            if (message["id"] is null)
            {
                if (message["event"] is not null)
                    foreach (var (_, waiting) in _pending)
                    {
                        try
                        {
                            waiting.OnEvent?.Invoke(message);
                        }
                        catch (Exception e)
                        {
                            // A callback's bug must not end the reader (every later reply would be lost). Type only: no data.
                            Console.Error.WriteLine($"yawble: an event handler failed ({e.GetType().Name})");
                        }
                    }
                continue;
            }
            if (message["id"] is not JsonValue v || !v.TryGetValue<int>(out var replyTo) || !_pending.TryRemove(replyTo, out var to)) continue; // stale
            if (message["error"] is JsonObject error)
                to.Reply.TrySetException(new ControlErrorException(Text(error["code"]) ?? "failed", Text(error["message"]) ?? "the app refused the call"));
            else
                to.Reply.TrySetResult(message["result"] as JsonObject ?? []);
        }
    }

    private void Break() => _broken = true;

    internal static string? Text(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.TryGetValue<string>(out var s) ? s : null;

    public async ValueTask DisposeAsync()
    {
        Break();
        try { await stream.DisposeAsync().ConfigureAwait(false); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        foreach (var (_, waiting) in _pending) waiting.Reply.TrySetException(new ConnectionLostException(sent: true));
        Task? reader;
        lock (_gate) reader = _reader;
        if (reader is not null) await reader.ConfigureAwait(false);
    }
}

/// <summary>Opening an authenticated connection with this computer's key for a client name.</summary>
internal static class ControlSession
{
    internal static readonly TimeSpan AuthLimit = TimeSpan.FromSeconds(30);

    /// <summary>The client to act as: <paramref name="client"/>, else the only name paired here.</summary>
    public static string ResolveClient(KeyFiles keys, string? client)
    {
        if (client is not null)
        {
            if (!KeyFiles.ValidName(client)) throw new UsageException($"'{client}' is not a client name (lowercase letters, digits and '-', 1 to 32 characters)");
            return client;
        }
        var names = keys.Names();
        return names.Count switch
        {
            1 => names[0],
            0 => throw new NotPairedException(NotPairedText),
            _ => throw new UsageException($"several clients are paired on this computer ({string.Join(", ", names)}); choose one with --client <name>"),
        };
    }

    public const string NotPairedText =
        "This computer is not paired with Yawble yet. Run: yawble-companion pair --name <name> (for example claude-code), then approve the code in the Yawble window.";

    public static async Task<IControlChannel> OpenAsync(IControlConnector connector, KeyFiles keys, string? client, CancellationToken ct)
    {
        var name = ResolveClient(keys, client);
        var key = keys.Load(name) ?? throw new NotPairedException(
            $"There is no key for '{name}' on this computer. Pair it: yawble-companion pair --name {name}, then approve the code in the Yawble window.");
        var channel = await connector.ConnectAsync(ct).ConfigureAwait(false);
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(AuthLimit);
            try
            {
                await channel.CallAsync(CompanionProtocol.Auth, new JsonObject { ["client"] = name, ["key"] = key }, null, bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimedOutException();
            }
            return channel;
        }
        catch (ControlErrorException e) when (e.Code == "unauthorized")
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw new NotPairedException(
                $"The Yawble app does not accept this computer's key for '{name}' (it was revoked, or the key is out of date). " +
                $"Pair again: yawble-companion forget --client {name}, then yawble-companion pair --name {name}.");
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
