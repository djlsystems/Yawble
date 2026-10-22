using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Harness.Tests.Host;

/// <summary>
/// A TINY APP for the preview forwarder to reach: it answers every request with the headers it
/// received, one <c>name: value</c> per line, and marks its answer as its own with a header, a cookie
/// and a CSP of its own, so a test can see the app's answer arrive untouched. <c>/ws</c> is a WebSocket
/// that sends back what it is sent.
/// </summary>
public sealed class EchoHeadersServer : IAsyncDisposable
{
    /// <summary>The app's own CSP, nothing like the Host's.</summary>
    public const string AppPolicy = "default-src * 'unsafe-inline'";

    private readonly WebApplication _app;

    private EchoHeadersServer(WebApplication app) => _app = app;

    public static async Task<EchoHeadersServer> StartAsync(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

        var app = builder.Build();
        app.UseWebSockets();

        app.Run(async context =>
        {
            if (context.Request.Path == "/ws" && context.WebSockets.IsWebSocketRequest)
            {
                await EchoAsync(context);
                return;
            }

            context.Response.Headers["X-From-App"] = "echo";
            context.Response.Headers.ContentSecurityPolicy = AppPolicy;
            context.Response.Headers.SetCookie = "app-set=1; Path=/";
            context.Response.ContentType = "text/plain";

            var lines = context.Request.Headers.Select(h => $"{h.Key}: {h.Value}");
            await context.Response.WriteAsync($"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}\n" + string.Join('\n', lines));
        });

        await app.StartAsync();
        return new EchoHeadersServer(app);
    }

    private static async Task EchoAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[4096];
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
            if (received.MessageType == WebSocketMessageType.Close) break;
            await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, context.RequestAborted);
        }

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
