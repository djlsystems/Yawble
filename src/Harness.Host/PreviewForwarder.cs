using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace Harness.Host;

/// <summary>
/// THE PREVIEW FORWARDER. A request that arrived on a published preview port (see
/// <see cref="PreviewPorts"/>) is not the Host's: it is sent on to the app listening behind that port
/// on the worker's loopback, and the app's answer comes back as the app wrote it. Nothing else in the
/// pipeline sees it - no authentication, no gate, no security header, no cookie of the Host's.
///
/// <para>
/// EVERY COOKIE THE HOST ISSUES IS REMOVED FIRST. A cookie is not scoped by port (RFC 6265 §8.5), so
/// the browser sends the person's session cookie to <c>host:8081</c> exactly as to <c>host:8080</c>;
/// forwarded as it came, code a team wrote would receive a live session. The session cookie by its
/// <see cref="InstanceIdentity"/> name goes, and with it every cookie under
/// <see cref="InstanceIdentity.CookiePrefix"/>: its chunks, and another instance's on this machine.
/// Every other cookie is the app's own and passes.
/// </para>
///
/// <para>
/// Nothing listening behind the port answers <see cref="NoPreviewPage"/>, which IS the Host's own, so it
/// carries the Host's security headers.
/// </para>
/// </summary>
public sealed class PreviewForwarder : IDisposable
{
    /// <summary>The page a preview port answers when nothing listens behind it.</summary>
    public const string NoPreviewSentence =
        "No preview is running on this port. Ask the Concierge to start the app you want to try.";

    /// <summary>The whole page, short on purpose: one sentence and the port.</summary>
    public static string NoPreviewPage(int port) =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>No preview on port "
        + port + "</title></head><body><p>" + NoPreviewSentence + "</p></body></html>";

    /// <summary>Headers that describe one connection, not the message, so are never carried across.</summary>
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    /// <summary>The longest response head an upgrade's answer may have.</summary>
    private const int MaxHead = 64 * 1024;

    private readonly IReadOnlySet<int> _ports;
    private readonly string _sessionCookie;
    private readonly string _behindHost;
    private readonly HttpMessageInvoker _client;

    /// <param name="ports">The preview ports this Host listens on.</param>
    /// <param name="sessionCookie">This instance's session cookie name.</param>
    /// <param name="behindHost">Where the apps listen: the worker's loopback.</param>
    public PreviewForwarder(IReadOnlySet<int> ports, string sessionCookie, string behindHost = "127.0.0.1")
    {
        _ports = ports;
        _sessionCookie = sessionCookie;
        _behindHost = behindHost;

        // NOTHING OF ITS OWN: no cookie jar, no redirect followed, no body decompressed, no proxy.
        // What the app answers is what the browser gets.
        _client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ActivityHeadersPropagator = null,
        });
    }

    /// <summary>Whether a cookie of this name is one the Host issues, and so never forwarded.</summary>
    public bool IsHostCookie(string name) =>
        name.Equals(_sessionCookie, StringComparison.Ordinal)
        || name.StartsWith(InstanceIdentity.CookiePrefix, StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="header"/> without the Host's cookies, or null when none is left. Each pair is
    /// kept exactly as the browser sent it.
    /// </summary>
    public string? WithoutHostCookies(string header)
    {
        var kept = header
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(pair => !IsHostCookie(pair.Split('=', 2)[0].Trim()))
            .ToList();

        return kept.Count == 0 ? null : string.Join("; ", kept);
    }

    /// <summary>Forwards preview-port requests; everything else goes on to the Host. Before
    /// <see cref="SecurityHeaders.Use"/>, so a forwarded answer carries nothing of the Host's.</summary>
    public void Use(IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var port = context.Connection.LocalPort;
        if (!_ports.Contains(port))
        {
            await next();
            return;
        }

        await ForwardAsync(context, port);
    });

    private async Task ForwardAsync(HttpContext context, int port)
    {
        // The app decides how large a body it takes, not the Host's limit for its own routes.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }

        var behind = PreviewPorts.BehindPort(port);

        try
        {
            if (context.Features.Get<IHttpUpgradeFeature>() is { IsUpgradableRequest: true } upgrade)
            {
                await UpgradeAsync(context, upgrade, behind);
            }
            else
            {
                await SendAsync(context, behind);
            }
        }
        catch (Exception e) when (NothingBehind(e) && !context.Response.HasStarted)
        {
            await NoPreviewAsync(context, port);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or HttpRequestException
                                  && context.RequestAborted.IsCancellationRequested)
        {
            // The browser went away; there is nobody to answer.
        }
    }

    private static bool NothingBehind(Exception e) =>
        e is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError }
            or SocketException
            or HttpRequestException { InnerException: SocketException }
        || (e is TaskCanceledException { InnerException: TimeoutException });

    private static async Task NoPreviewAsync(HttpContext context, int port)
    {
        context.Response.Clear();
        SecurityHeaders.Stamp(context);
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(NoPreviewPage(port), context.RequestAborted);
    }

    private async Task SendAsync(HttpContext context, int behind)
    {
        var request = context.Request;
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw && raw[0] == '/'
            ? raw
            : $"{request.PathBase}{request.Path}{request.QueryString}";

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), $"http://{_behindHost}:{behind}{target}")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? request.ContentLength > 0)
        {
            message.Content = new StreamContent(request.Body);
        }

        foreach (var (name, values) in request.Headers)
        {
            if (HopByHop.Contains(name) || name.StartsWith(':')) continue;

            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                if (WithoutHostCookies(string.Join("; ", values.ToArray())) is { } cookies)
                {
                    message.Headers.TryAddWithoutValidation("Cookie", cookies);
                }

                continue;
            }

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                // The address the browser used, so the app builds its own links on it.
                message.Headers.Host = values.ToString();
                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(name, values.ToArray()))
            {
                message.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }

        using var answer = await _client.SendAsync(message, context.RequestAborted);

        var response = context.Response;
        response.StatusCode = (int)answer.StatusCode;

        foreach (var (name, values) in answer.Headers.Concat(answer.Content.Headers))
        {
            if (HopByHop.Contains(name)) continue;
            response.Headers[name] = values.ToArray();
        }

        await using var body = await answer.Content.ReadAsStreamAsync(context.RequestAborted);
        await body.CopyToAsync(response.Body, context.RequestAborted);
    }

    /// <summary>
    /// A WebSocket or any other upgrade: the request head is written to the app over a plain
    /// connection, and once the app answers 101 the two connections are joined byte for byte.
    /// </summary>
    private async Task UpgradeAsync(HttpContext context, IHttpUpgradeFeature upgrade, int behind)
    {
        var request = context.Request;
        var ct = context.RequestAborted;
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw && raw[0] == '/'
            ? raw
            : $"{request.PathBase}{request.Path}{request.QueryString}";

        using var socket = new TcpClient();
        await socket.ConnectAsync(_behindHost, behind, ct);
        await using var app = socket.GetStream();

        var head = new StringBuilder($"{request.Method} {target} HTTP/1.1\r\n");
        foreach (var (name, values) in request.Headers)
        {
            if (name.StartsWith(':')) continue;

            var value = name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                ? WithoutHostCookies(string.Join("; ", values.ToArray()))
                : values.ToString();

            if (value is null || value.Contains('\r') || value.Contains('\n')) continue;
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        head.Append("\r\n");
        await app.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), ct);

        var (status, headers, rest) = await ReadHeadAsync(app, ct);

        context.Response.StatusCode = status;
        foreach (var (name, value) in headers)
        {
            if (status == StatusCodes.Status101SwitchingProtocols
                    ? name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                    : HopByHop.Contains(name))
            {
                continue;
            }

            context.Response.Headers.Append(name, value);
        }

        if (status != StatusCodes.Status101SwitchingProtocols)
        {
            // Refused by the app: its answer, with the body it announced.
            await context.Response.Body.WriteAsync(rest, ct);
            if (context.Response.ContentLength is { } length && length > rest.Length)
            {
                await CopyAsync(app, context.Response.Body, length - rest.Length, ct);
            }

            return;
        }

        await using var browser = await upgrade.UpgradeAsync();
        await browser.WriteAsync(rest, ct);

        using var either = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = app.CopyToAsync(browser, either.Token);
        var down = browser.CopyToAsync(app, either.Token);
        await Task.WhenAny(up, down);
        await either.CancelAsync();

        try { await Task.WhenAll(up, down); }
        catch (Exception e) when (e is OperationCanceledException or IOException) { }
    }

    private static async Task CopyAsync(Stream from, Stream to, long count, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (count > 0)
        {
            var read = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct);
            if (read == 0) return;
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
            count -= read;
        }
    }

    /// <summary>The app's status line and headers, and whatever bytes came after them.</summary>
    private static async Task<(int Status, List<(string Name, string Value)> Headers, byte[] After)> ReadHeadAsync(
        Stream from, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int end;

        while ((end = IndexOfHeadEnd(buffer.GetBuffer(), (int)buffer.Length)) < 0)
        {
            if (buffer.Length > MaxHead) throw new IOException("The app's answer has no end to its head.");
            var read = await from.ReadAsync(chunk, ct);
            if (read == 0) throw new IOException("The app closed the connection before answering.");
            buffer.Write(chunk, 0, read);
        }

        var all = buffer.ToArray();
        var lines = Encoding.Latin1.GetString(all, 0, end).Split("\r\n");
        var statusParts = lines[0].Split(' ', 3);
        if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out var status))
        {
            throw new IOException("The app's answer has no status line.");
        }

        var headers = lines.Skip(1)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => (parts[0].Trim(), parts[1].Trim()))
            .ToList();

        return (status, headers, all[(end + 4)..]);
    }

    private static int IndexOfHeadEnd(byte[] bytes, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n') return i;
        }

        return -1;
    }

    public void Dispose() => _client.Dispose();
}
