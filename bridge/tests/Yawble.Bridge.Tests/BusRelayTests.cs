using System.Net;
using System.Net.Sockets;
using Yawble.Bridge;
using Xunit;

namespace Yawble.Bridge.Tests;

public class BusRelayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":7,"method":"ping"}""", "7")]
    [InlineData("""{"jsonrpc":"2.0","id":"a-1","method":"ping"}""", "\"a-1\"")]
    [InlineData("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", null)]
    [InlineData("not json", null)]
    public void RequestId_IsTheRawIdOrNullForNotifications(string line, string? expected) =>
        Assert.Equal(expected, BusRelay.RequestId(line));

    [Fact]
    public async Task AnUnreachableGateway_AnswersEachRequestWithAJsonRpcError_AndNotificationsWithNothing()
    {
        // A port nothing listens on: the bridge must answer, never hang the CLI.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var gateway = new GatewayAddress(new Uri($"http://127.0.0.1:{port}"), new Uri(GatewayAddress.BusUrl), "yawrt_test");
        using var relay = new BusRelay(gateway);
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}

            {"jsonrpc":"2.0","id":"two","method":"tools/list"}
            """);
        var output = new StringWriter();
        var log = new StringWriter();
        await relay.RunAsync(input, output, log, Ct);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("""{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"the bus could not be reached""", lines[0]);
        Assert.StartsWith("""{"jsonrpc":"2.0","id":"two","error":""", lines[1]);
        Assert.Contains("could not be reached", log.ToString());
        Assert.DoesNotContain("yawrt_test", output + log.ToString());
    }

    [Fact]
    public async Task TheGatewayIsReachedOnAPlainIPv4Socket_NeverADualModeOne()
    {
        // macOS: the sandbox's loopback rule does not match ::ffff:127.0.0.1, which a dual-mode socket connects as.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync(Ct);
            await using var stream = await BusRelay.ConnectAsync(new DnsEndPoint("127.0.0.1", port), Ct);
            var socket = Assert.IsType<NetworkStream>(stream).Socket;
            Assert.Equal(AddressFamily.InterNetwork, socket.AddressFamily);
            Assert.Equal(IPAddress.Loopback, ((IPEndPoint)socket.RemoteEndPoint!).Address);
            using var _ = await accept;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ABusCall_GoesThroughTheProxyOnTheIPv4Connect()
    {
        // A one-request stand-in proxy on 127.0.0.1: it reads the absolute-form POST and answers one JSON-RPC result.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var served = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(Ct);
                var s = client.GetStream();
                var buffer = new byte[8192];
                var text = "";
                while (!text.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await s.ReadAsync(buffer, Ct);
                    if (n == 0) break;
                    text += System.Text.Encoding.ASCII.GetString(buffer, 0, n);
                }
                var body = """{"jsonrpc":"2.0","id":3,"result":{}}""";
                var reply = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply), Ct);
                return text;
            }, Ct);
            var gateway = new GatewayAddress(new Uri($"http://127.0.0.1:{port}"), new Uri(GatewayAddress.BusUrl), "yawrt_test");
            using var relay = new BusRelay(gateway);
            var reply = await relay.RelayAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""", new StringWriter(), Ct);
            Assert.Equal("""{"jsonrpc":"2.0","id":3,"result":{}}""", reply);
            Assert.StartsWith($"POST {GatewayAddress.BusUrl}", await served);
        }
        finally
        {
            listener.Stop();
        }
    }
}
