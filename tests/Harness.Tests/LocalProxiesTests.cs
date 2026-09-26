using System.Net;
using Harness.Host;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Harness.Tests;

public sealed class LocalProxiesTests
{
    // The container's own eth0 address, which is where pasta delivers ngrok's connection from.
    private static readonly IPAddress Own = IPAddress.Parse("172.31.242.154");

    [Theory]
    [InlineData("172.31.242.154")]
    [InlineData("::ffff:172.31.242.154")]
    [InlineData("127.0.0.1")]
    public async Task A_tunnel_arriving_from_this_machine_is_https(string peer)
    {
        Assert.Equal("https", await SchemeSeen(peer));
    }

    [Fact]
    public async Task A_peer_that_is_not_this_machine_cannot_claim_https()
    {
        Assert.Equal("http", await SchemeSeen("172.31.240.1"));
    }

    private static async Task<string> SchemeSeen(string peer)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        string? seen = null;
        var middleware = new ForwardedHeadersMiddleware(
            next: c => { seen = c.Request.Scheme; return Task.CompletedTask; },
            loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            options: Options.Create(LocalProxies.Options([Own])));

        await middleware.Invoke(context);
        return seen!;
    }
}
