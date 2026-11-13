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
}
