using System.Net.WebSockets;
using Xunit;
using Yawble.Core.Link;

namespace Yawble.Core.Tests.Link;

/// <summary>The Diagnostics line when the Cloud Link connection ends: the exception, how long it lasted, which connection and the retry.</summary>
public class ConnectionEndedLineTests
{
    [Fact]
    public void SaysTheExceptionTheTimeConnectedTheConnectionAndTheDelay() =>
        Assert.Equal("Cloud Link: connection ended (WebSocketException) after 42s, connection 3, reconnecting in 1.5s: reset by peer",
            CloudLink.ConnectionEndedText(new WebSocketException("reset by peer"), TimeSpan.FromSeconds(42.2), 3, TimeSpan.FromSeconds(1.5)));

    [Fact]
    public void WhenTheAppStops_ItSaysNothingIsRetried() =>
        Assert.Equal("Cloud Link: connection ended (OperationCanceledException) after 7s, connection 1, not reconnecting (stopping): stopped",
            CloudLink.ConnectionEndedText(new OperationCanceledException("stopped"), TimeSpan.FromSeconds(7), 1, null));
}
