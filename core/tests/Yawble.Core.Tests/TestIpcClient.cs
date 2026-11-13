using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Ipc;

namespace Yawble.Core.Tests;

/// <summary>A minimal client speaking the shell's side of the protocol, as shell/src/main/core-client.ts does.</summary>
internal sealed class TestIpcClient : IAsyncDisposable
{
    // Every connect and call is bounded, so a core that never answers fails the test by name instead of hanging the run.
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly Stream _stream;
    private int _nextId;

    private TestIpcClient(Stream stream) => _stream = stream;

    public static async Task<TestIpcClient> ConnectAsync(string address, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", address.Replace(@"\\.\pipe\", ""), PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)Limit.TotalMilliseconds, ct).WaitAsync(Limit, ct);
            return new TestIpcClient(pipe);
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(address), ct).AsTask().WaitAsync(Limit, ct);
        return new TestIpcClient(new NetworkStream(socket, ownsSocket: true));
    }

    public async Task<JsonObject?> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var request = new JsonObject { ["id"] = ++_nextId, ["method"] = method, ["params"] = parameters };
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(Limit);
        await FrameCodec.WriteAsync(_stream, new Frame(FrameKind.Json, JsonSerializer.SerializeToUtf8Bytes(request)), bounded.Token)
            .WaitAsync(Limit, ct);
        // The core also pushes events (frames with no id) to an authenticated connection, as
        // core-client.ts expects: skip them and return the reply to this request.
        while (true)
        {
            var frame = await FrameCodec.ReadAsync(_stream, bounded.Token).WaitAsync(Limit, ct);
            if (frame is null) return null;
            var message = JsonNode.Parse(frame.Value.Payload.Span)!.AsObject();
            if (message["id"] is { } id && id.GetValueKind() == JsonValueKind.Number && id.GetValue<int>() == _nextId) return message;
        }
    }

    /// <summary>The next pushed event (a frame with no id), skipping nothing else: null when the connection closed.</summary>
    public async Task<JsonObject?> ReadEventAsync(CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(Limit);
        while (true)
        {
            var frame = await FrameCodec.ReadAsync(_stream, bounded.Token).WaitAsync(Limit, ct);
            if (frame is null) return null;
            var message = JsonNode.Parse(frame.Value.Payload.Span)!.AsObject();
            if (message["event"] is not null) return message;
        }
    }

    /// <summary>Sends a request without waiting for its reply (a reply that only comes later, e.g. after a decision); returns its id.</summary>
    public async Task<int> SendAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var request = new JsonObject { ["id"] = ++_nextId, ["method"] = method, ["params"] = parameters };
        await WriteRawAsync(FrameKind.Json, JsonSerializer.SerializeToUtf8Bytes(request), ct);
        return _nextId;
    }

    /// <summary>The reply to request <paramref name="id"/>, skipping pushed events: null when the connection closed.</summary>
    public async Task<JsonObject?> ReadReplyAsync(int id, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(Limit);
        while (true)
        {
            var frame = await FrameCodec.ReadAsync(_stream, bounded.Token).WaitAsync(Limit, ct);
            if (frame is null) return null;
            var message = JsonNode.Parse(frame.Value.Payload.Span)!.AsObject();
            if (message["id"] is { } got && got.GetValueKind() == JsonValueKind.Number && got.GetValue<int>() == id) return message;
        }
    }

    /// <summary>Writes one frame of any kind and payload as is (an oversized or malformed frame).</summary>
    public async Task WriteRawAsync(FrameKind kind, byte[] payload, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(Limit);
        await FrameCodec.WriteAsync(_stream, new Frame(kind, payload), bounded.Token).WaitAsync(Limit, ct);
    }

    /// <summary>True once the other end has closed: the next read ends the stream (or breaks), within the limit.</summary>
    public async Task<bool> IsClosedAsync(CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(Limit);
        try
        {
            while (await FrameCodec.ReadAsync(_stream, bounded.Token).WaitAsync(Limit, ct) is not null) { }
            return true;
        }
        catch (IOException) { return true; }
        catch (InvalidDataException) { return true; }
    }

    public Task<JsonObject?> AuthAsync(string token, CancellationToken ct) =>
        CallAsync("auth", new JsonObject { ["token"] = token }, ct);

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
