using System.Net;
using System.Net.Sockets;

namespace Yawble.Core.Gateway;

/// <summary>
/// Where a run's gateway takes its connections. Production: <see cref="LoopbackGatewayListener"/>, a TCP listener on
/// 127.0.0.1:&lt;ephemeral&gt; (IPv4 only; never 0.0.0.0). The seam exists so the default test run can serve
/// the same gateway in memory and bind no port (<see cref="GatewayHostOptions.Listener"/>).
/// </summary>
public interface IGatewayListener : IDisposable
{
    void Start();

    /// <summary>The port the sandbox is told to use (runtimeConfig.networkProxy).</summary>
    int Port { get; }

    /// <summary>The next client connection, as a duplex stream the gateway owns.</summary>
    Task<Stream> AcceptAsync(CancellationToken ct);

    /// <summary>Opens a client connection to this listener (the doctor's self-test speaks to the gateway this way).</summary>
    Task<Stream> ConnectAsync(CancellationToken ct);

    void Stop();
}

/// <summary>The production listener: TCP on 127.0.0.1, an ephemeral port.</summary>
public sealed class LoopbackGatewayListener : IGatewayListener
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public void Start() => _listener.Start();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task<Stream> AcceptAsync(CancellationToken ct)
    {
        var socket = await _listener.AcceptSocketAsync(ct);
        socket.NoDelay = true;
        return new NetworkStream(socket, ownsSocket: true);
    }

    public async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Stop() => _listener.Stop();

    public void Dispose() => _listener.Stop();
}

/// <summary>
/// How a CONNECT reaches an allowlisted host. Production: <see cref="SocketConnectDialer"/> (DNS, then TCP dual-mode).
/// Failures are reported as <see cref="SocketException"/> (or <see cref="OperationCanceledException"/> on the timeout),
/// which the gateway turns into its 403 / 502 answers.
/// </summary>
public interface IConnectDialer
{
    /// <summary>The addresses a host name resolves to (an IP literal is never passed here).</summary>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);

    /// <summary>A connected duplex stream to one of <paramref name="addresses"/> on <paramref name="port"/>.</summary>
    Task<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken ct);
}

/// <summary>The production dialer: the system resolver and a TCP connection.</summary>
public sealed class SocketConnectDialer : IConnectDialer
{
    public static readonly SocketConnectDialer Instance = new();

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);

    public async Task<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken ct)
    {
        var upstream = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        upstream.Client.DualMode = true;
        try
        {
            await upstream.ConnectAsync(addresses, port, ct);
            return new NetworkStream(upstream.Client, ownsSocket: true);
        }
        catch
        {
            upstream.Dispose();
            throw;
        }
    }
}
