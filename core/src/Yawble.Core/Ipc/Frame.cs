using System.Buffers.Binary;

namespace Yawble.Core.Ipc;

/// <summary>
/// The wire unit between the shell and the core: JSON frames and binary PTY frames.
/// Layout: [u32 big-endian payload length][u8 kind][payload]. No TCP port is ever opened.
/// </summary>
public enum FrameKind : byte
{
    /// <summary>UTF-8 JSON: a request {id, method, params}, a response {id, result | error} or an event {event, data}.</summary>
    Json = 1,

    /// <summary>Reserved for terminal output and input: [u32 session id][raw bytes].</summary>
    Pty = 2,
}

public readonly record struct Frame(FrameKind Kind, ReadOnlyMemory<byte> Payload);

public static class FrameCodec
{
    public const int HeaderSize = 5;
    public const int MaxPayload = 16 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, Frame frame, CancellationToken ct)
    {
        if (frame.Payload.Length > MaxPayload) throw new InvalidDataException("frame too large");
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)frame.Payload.Length);
        header[4] = (byte)frame.Kind;
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(frame.Payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame, or null at a clean end of stream between frames.</summary>
    public static Task<Frame?> ReadAsync(Stream stream, CancellationToken ct) => ReadAsync(stream, ct, MaxPayload);

    /// <summary>
    /// As <see cref="ReadAsync(Stream, CancellationToken)"/> with a smaller limit: a header announcing more than
    /// <paramref name="maxPayload"/> bytes throws <see cref="InvalidDataException"/> before any of the payload is read.
    /// </summary>
    public static async Task<Frame?> ReadAsync(Stream stream, CancellationToken ct, int maxPayload)
    {
        if (maxPayload is < 0 or > MaxPayload) throw new ArgumentOutOfRangeException(nameof(maxPayload));
        var header = new byte[HeaderSize];
        if (!await ReadExactlyOrEndAsync(stream, header, ct).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > maxPayload) throw new InvalidDataException($"frame of {length} bytes exceeds the {maxPayload} limit");
        var kind = (FrameKind)header[4];
        if (kind is not (FrameKind.Json or FrameKind.Pty)) throw new InvalidDataException($"unknown frame kind {header[4]}");
        var payload = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, payload, ct).ConfigureAwait(false) && length > 0)
            throw new EndOfStreamException("stream ended inside a frame");
        return new Frame(kind, payload);
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("stream ended inside a frame");
            }
            read += n;
        }
        return true;
    }
}
