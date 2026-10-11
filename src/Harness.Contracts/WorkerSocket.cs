using System.Net.WebSockets;
using System.Text;

namespace Harness.Contracts;

/// <summary>
/// One end of a worker connection: frames over a WebSocket, one UTF-8 text message each, written
/// one at a time and read in order. A message is reassembled from its fragments and refused past
/// <see cref="WorkerFrameCodec.MaxBytes"/>. Used the same way at both ends.
/// </summary>
public sealed class WorkerSocket(WebSocket socket, WorkerFrameCodec codec) : IDisposable
{
    private readonly SemaphoreSlim _send = new(1, 1);

    /// <summary>The codec frames are written and read with; replaced once the connection's key is made.</summary>
    public WorkerFrameCodec Codec { get; set; } = codec;

    public WebSocketState State => socket.State;

    /// <summary>Writes one frame. Throws when the connection is gone.</summary>
    public async Task SendAsync(WorkerFrame frame, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(Codec.Write(frame));
        await _send.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>
    /// The next frame, or null when the other end closed. Throws <see cref="InvalidDataException"/>
    /// with a sentence for a message that is not a frame, too large, or does not open.
    /// </summary>
    public async Task<WorkerFrame?> ReceiveAsync(CancellationToken ct = default)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;

            message.Write(buffer, 0, result.Count);
            if (message.Length > WorkerFrameCodec.MaxBytes) throw new InvalidDataException(WorkerFrameCodec.TooLargeText);
            if (result.EndOfMessage) break;
        }

        return Codec.Read(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
    }

    /// <summary>Closes the connection with <paramref name="reason"/>, its first 120 bytes, without waiting long for the other end.</summary>
    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        var bytes = Encoding.UTF8.GetBytes(reason);
        var trimmed = bytes.Length <= 120 ? reason : Encoding.UTF8.GetString(bytes, 0, 120).TrimEnd('�');

        try
        {
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(status, trimmed, bounded.Token);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            // Already gone: nothing to close.
        }
    }

    /// <summary>Drops the connection at once.</summary>
    public void Abort() => socket.Abort();

    public void Dispose()
    {
        socket.Dispose();
        _send.Dispose();
    }
}
