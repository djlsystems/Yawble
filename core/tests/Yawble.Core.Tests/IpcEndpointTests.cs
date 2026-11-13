using System.IO.Pipes;
using System.Net.Sockets;
using Yawble.Core.Ipc;
using Xunit;

namespace Yawble.Core.Tests;

/// <summary>
/// The shell connects with Node's net.connect, which does not wait for a named pipe to appear: it fails at once
/// with ENOENT. So the address must accept a connection as soon as the endpoint exists (the core prints
/// YAWBLE-READY right after creating it) and again while an earlier connection is still open.
/// </summary>
public class IpcEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AcceptsAConnectionMadeWithoutWaiting_BeforeTheFirstAcceptAndBetweenAccepts()
    {
        await using var endpoint = IpcEndpoint.Create(IpcEndpoint.NewAddress(Path.GetTempPath()));

        await using var first = ConnectWithoutWaiting(endpoint.Address); // no AcceptAsync has run yet
        await using var served1 = await endpoint.AcceptAsync(Ct).WaitAsync(Limit, Ct);

        await using var second = ConnectWithoutWaiting(endpoint.Address); // the first is still open, no accept pending
        await using var served2 = await endpoint.AcceptAsync(Ct).WaitAsync(Limit, Ct);

        Assert.Equal(1, await RoundTrip(first, served1, 1));
        Assert.Equal(2, await RoundTrip(second, served2, 2));
    }

    /// <summary>The core's shutdown must not hang: disposing the endpoint ends a pending accept, even uncancelled.</summary>
    [Fact]
    public async Task DisposeEndsAPendingAccept_AndLaterAcceptsFailFast()
    {
        var endpoint = IpcEndpoint.Create(IpcEndpoint.NewAddress(Path.GetTempPath()));
        var pending = endpoint.AcceptAsync(CancellationToken.None);
        await Task.Delay(100, Ct);
        Assert.False(pending.IsCompleted);

        await endpoint.DisposeAsync().AsTask().WaitAsync(Limit, Ct);

        var e = await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(Limit, Ct));
        Assert.True(e is IOException or ObjectDisposedException or OperationCanceledException or SocketException,
            $"the pending accept ended with {e.GetType().Name}: {e.Message}");
        await Assert.ThrowsAnyAsync<Exception>(() => endpoint.AcceptAsync(CancellationToken.None).WaitAsync(Limit, Ct));
        await endpoint.DisposeAsync(); // twice is harmless
    }

    /// <summary>Cancelling a pending accept ends it, and the address still takes a connection afterwards.</summary>
    [Fact]
    public async Task CancellingAPendingAccept_EndsIt_AndTheAddressStillAcceptsAfterwards()
    {
        await using var endpoint = IpcEndpoint.Create(IpcEndpoint.NewAddress(Path.GetTempPath()));
        using (var cts = new CancellationTokenSource())
        {
            var pending = endpoint.AcceptAsync(cts.Token);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Limit, Ct));
        }

        await using var client = ConnectWithoutWaiting(endpoint.Address);
        await using var served = await endpoint.AcceptAsync(Ct).WaitAsync(Limit, Ct);
        Assert.Equal(7, await RoundTrip(client, served, 7));
    }

    /// <summary>
    /// A write is buffered: it completes before the other end reads, as on a socket. (A Windows pipe made with
    /// 0-byte buffers blocks the writer until the reader reads; that hung these tests, then timed them out.)
    /// </summary>
    private static async Task<byte> RoundTrip(Stream client, Stream served, byte value)
    {
        await client.WriteAsync(new[] { value }, Ct).AsTask().WaitAsync(Limit, Ct);
        await client.FlushAsync(Ct).WaitAsync(Limit, Ct);
        var b = new byte[1];
        Assert.Equal(1, await served.ReadAsync(b, Ct).AsTask().WaitAsync(Limit, Ct));
        return b[0];
    }

    private static Stream ConnectWithoutWaiting(string address)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", address.Replace(@"\\.\pipe\", ""), PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(timeout: 0); // like net.connect: no pipe instance now means failure, not a wait
            return pipe;
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(address));
        return new NetworkStream(socket, ownsSocket: true);
    }
}
