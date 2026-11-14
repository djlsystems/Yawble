using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Yawble.Core.Gateway;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// The tests' stand-in for loopback TCP: gateways listen on in-memory "ports" and clients reach them through
/// <see cref="ConnectAsync"/> (or an HttpClient whose ConnectCallback does), so the default test run binds no port.
/// A port is a number handed out here, never bound: a stray real connection to it is refused (or reaches whatever
/// else holds that number on this computer), so a test that forgets the in-memory client fails rather than passes.
/// </summary>
internal static class InMemoryNetwork
{
    private static readonly ConcurrentDictionary<int, InMemoryGatewayListener> Listeners = new();
    private static int _nextPort = 20000;

    /// <summary>A fresh port number (not bound to anything).</summary>
    public static int AllocatePort() => Interlocked.Increment(ref _nextPort);

    internal static void Register(InMemoryGatewayListener listener) => Listeners[listener.Port] = listener;

    internal static void Unregister(InMemoryGatewayListener listener) => Listeners.TryRemove(new KeyValuePair<int, InMemoryGatewayListener>(listener.Port, listener));

    /// <summary>Whether an in-memory gateway is listening on <paramref name="port"/>.</summary>
    public static bool IsListening(int port) => Listeners.ContainsKey(port);

    /// <summary>A connection to the in-memory listener on <paramref name="port"/>; refused (as TCP would be) if there is none.</summary>
    public static Task<Stream> ConnectAsync(int port, CancellationToken ct = default) =>
        Listeners.TryGetValue(port, out var listener)
            ? listener.ConnectAsync(ct)
            : throw new SocketException((int)SocketError.ConnectionRefused);

    /// <summary>
    /// The client handler a CLI's HTTP client is in the tests: <paramref name="proxy"/> as its HTTP proxy, and the
    /// connection to it in memory when an in-memory gateway holds that port (a real socket otherwise: the opt-in
    /// real-loopback tests).
    /// </summary>
    public static SocketsHttpHandler ProxyHandler(Uri proxy)
    {
        var handler = new SocketsHttpHandler { Proxy = new WebProxy(proxy), UseProxy = true, UseCookies = false };
        if (IsListening(proxy.Port))
            handler.ConnectCallback = async (context, ct) => await ConnectAsync(context.DnsEndPoint.Port, ct);
        return handler;
    }
}

/// <summary>An in-memory <see cref="IGatewayListener"/>: each connection is a pair of pipes.</summary>
internal sealed class InMemoryGatewayListener : IGatewayListener
{
    private readonly Channel<Stream> _pending = Channel.CreateUnbounded<Stream>();
    private volatile bool _listening;

    public int Port { get; } = InMemoryNetwork.AllocatePort();

    public void Start()
    {
        _listening = true;
        InMemoryNetwork.Register(this);
    }

    public async Task<Stream> AcceptAsync(CancellationToken ct) => await _pending.Reader.ReadAsync(ct);

    public Task<Stream> ConnectAsync(CancellationToken ct)
    {
        if (!_listening) throw new SocketException((int)SocketError.ConnectionRefused);
        var (client, server) = DuplexPipeStream.CreatePair();
        if (!_pending.Writer.TryWrite(server))
        {
            client.Dispose();
            server.Dispose();
            throw new SocketException((int)SocketError.ConnectionRefused);
        }
        return Task.FromResult<Stream>(client);
    }

    public void Stop()
    {
        _listening = false;
        InMemoryNetwork.Unregister(this);
        _pending.Writer.TryComplete();
        // Connections queued but never accepted are reset, as a closed listener's backlog is.
        while (_pending.Reader.TryRead(out var s)) s.Dispose();
    }

    public void Dispose() => Stop();
}

/// <summary>
/// A test <see cref="IConnectDialer"/>: names resolve from a table (no DNS), and a connection to (address, port)
/// reaches the in-memory server registered for that port (no socket). Anything else fails as the network would.
/// </summary>
internal sealed class FakeConnectDialer : IConnectDialer
{
    private readonly ConcurrentDictionary<string, IPAddress[]> _names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["localhost"] = [IPAddress.Loopback],
    };

    private readonly ConcurrentDictionary<int, Func<Stream, Task>> _servers = new();

    public ConcurrentQueue<(string Host, int Port)> Dialed { get; } = new();

    public void AddName(string host, params IPAddress[] addresses) => _names[host] = addresses;

    /// <summary>Serves connections to <paramref name="port"/> (on any address) with <paramref name="serve"/>, which gets the server end.</summary>
    public void Serve(int port, Func<Stream, Task> serve) => _servers[port] = serve;

    /// <summary>Every name looked up, in order.</summary>
    public ConcurrentQueue<string> Resolved { get; } = new();

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        Resolved.Enqueue(host);
        return _names.TryGetValue(host, out var addresses)
            ? Task.FromResult(addresses)
            : throw new SocketException((int)SocketError.HostNotFound);
    }

    public Task<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken ct)
    {
        Dialed.Enqueue((string.Join(",", addresses.Select(a => a.ToString())), port));
        if (!_servers.TryGetValue(port, out var serve)) throw new SocketException((int)SocketError.ConnectionRefused);
        var (client, server) = DuplexPipeStream.CreatePair();
        _ = Task.Run(async () =>
        {
            await using (server) await serve(server);
        }, CancellationToken.None);
        return Task.FromResult<Stream>(client);
    }
}

/// <summary>
/// One end of an in-memory full-duplex connection. Disposing it closes both directions: the peer's reads end (0) and
/// its writes fail with an <see cref="IOException"/>, as a closed TCP connection's would.
/// </summary>
internal sealed class DuplexPipeStream : Stream
{
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly Stream _in;
    private int _disposed;

    private DuplexPipeStream(PipeReader reader, PipeWriter writer)
    {
        _reader = reader;
        _writer = writer;
        _in = reader.AsStream(leaveOpen: true);
    }

    public static (DuplexPipeStream A, DuplexPipeStream B) CreatePair()
    {
        var options = new PipeOptions(useSynchronizationContext: false);
        var ab = new Pipe(options);
        var ba = new Pipe(options);
        return (new DuplexPipeStream(ba.Reader, ab.Writer), new DuplexPipeStream(ab.Reader, ba.Writer));
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        try
        {
            return await _in.ReadAsync(buffer, ct);
        }
        catch (InvalidOperationException) when (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(DuplexPipeStream));
        }
        catch (OperationCanceledException) when (_disposed != 0 && !ct.IsCancellationRequested)
        {
            throw new IOException("the connection was closed");
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var result = await _writer.WriteAsync(buffer, ct);
        if (result.IsCompleted) throw new IOException("the other end closed the connection");
        if (result.IsCanceled) throw new OperationCanceledException(ct);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async Task FlushAsync(CancellationToken ct)
    {
        if (_disposed != 0) return;
        var result = await _writer.FlushAsync(ct);
        if (result.IsCompleted) throw new IOException("the other end closed the connection");
    }

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _writer.Complete(); } catch (InvalidOperationException) { }
            try { _reader.CancelPendingRead(); } catch (InvalidOperationException) { }
            try { _reader.Complete(); } catch (InvalidOperationException) { }
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
