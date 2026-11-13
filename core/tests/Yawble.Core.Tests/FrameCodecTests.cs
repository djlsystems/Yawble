using System.Buffers.Binary;
using Yawble.Core.Ipc;
using Xunit;

namespace Yawble.Core.Tests;

public class FrameCodecTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RoundTripsJsonAndPtyFrames()
    {
        var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, new Frame(FrameKind.Json, "{\"a\":1}"u8.ToArray()), Ct);
        await FrameCodec.WriteAsync(stream, new Frame(FrameKind.Pty, new byte[] { 0, 0, 0, 7, 0x1b, (byte)'[' }), Ct);
        stream.Position = 0;

        var first = await FrameCodec.ReadAsync(stream, Ct);
        var second = await FrameCodec.ReadAsync(stream, Ct);

        Assert.Equal(FrameKind.Json, first!.Value.Kind);
        Assert.Equal("{\"a\":1}"u8.ToArray(), first.Value.Payload.ToArray());
        Assert.Equal(FrameKind.Pty, second!.Value.Kind);
        Assert.Equal(6, second.Value.Payload.Length);
        Assert.Null(await FrameCodec.ReadAsync(stream, Ct));
    }

    [Fact]
    public async Task RejectsOversizedAndUnknownFrames()
    {
        var header = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(header, FrameCodec.MaxPayload + 1u);
        header[4] = 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(new MemoryStream(header), Ct));

        var unknown = new byte[] { 0, 0, 0, 0, 9 };
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(new MemoryStream(unknown), Ct));
    }

    [Fact]
    public async Task TruncatedFrameIsAnError()
    {
        var stream = new MemoryStream(new byte[] { 0, 0, 0, 4, 1, (byte)'{' });
        await Assert.ThrowsAsync<EndOfStreamException>(() => FrameCodec.ReadAsync(stream, Ct));
    }
}
