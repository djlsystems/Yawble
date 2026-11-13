using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Yawble.Core.Integrations;

/// <summary>
/// Where the browser comes back after a sign-in: the query parameters (code and state, or error) of the first redirect that carries
/// <c>expectedState</c>; redirects with another state (or none) are turned away and the wait goes on, until cancelled.
/// </summary>
public interface IRedirectReceiver : IAsyncDisposable
{
    Uri RedirectUri { get; }
    Task<IReadOnlyDictionary<string, string>> WaitAsync(string expectedState, CancellationToken ct);
}

/// <summary>
/// A loopback redirect (RFC 8252 §7.3): listens on 127.0.0.1 on a free port (bound exclusively) for GET /callback carrying the expected
/// state, answers it with a short page saying the window can be closed, and stops. A /callback with another state is answered 400, anything
/// else (a favicon, another path) 404, and the wait goes on. Never used by tests (its matching is, through <see cref="Accept"/>).
/// </summary>
public sealed class LoopbackRedirectReceiver : IRedirectReceiver
{
    private const int MaxRequestBytes = 16 * 1024;
    private readonly TcpListener _listener;

    public LoopbackRedirectReceiver()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        _listener.Start();
        RedirectUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/callback");
    }

    public Uri RedirectUri { get; }

    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(string expectedState, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            string? target;
            try { target = await ReadTargetAsync(stream, limit.Token); }
            catch (Exception e) when (e is IOException or OperationCanceledException && !ct.IsCancellationRequested) { continue; }
            switch (Accept(target, expectedState, out var query))
            {
                case 404:
                    await RespondAsync(stream, "404 Not Found", "Not found.", ct);
                    continue;
                case 400:
                    await RespondAsync(stream, "400 Bad Request", "This is not the sign-in Yawble is waiting for. Start the sign-in again from Yawble.", ct);
                    continue;
                default:
                    await RespondAsync(stream, "200 OK", "You can close this window and return to Yawble.", ct);
                    return query!;
            }
        }
    }

    /// <summary>200 with the parameters for a /callback carrying the expected state (compared in constant time); 400 for another state; 404 for anything else.</summary>
    internal static int Accept(string? target, string expectedState, out IReadOnlyDictionary<string, string>? query)
    {
        query = null;
        if (target is null || !target.StartsWith("/callback", StringComparison.Ordinal) || (target.Length > 9 && target[9] != '?')) return 404;
        var q = Parse(target.Length > 10 ? target[10..] : "");
        if (!q.TryGetValue("state", out var state)
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            return 400;
        query = q;
        return 200;
    }

    /// <summary>The request target of a GET (the headers are read up to their end and dropped), or null for anything else.</summary>
    private static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
            if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var line = text.Split("\r\n", 2)[0].Split(' ');
        return line.Length == 3 && line[0] == "GET" ? line[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string message, CancellationToken ct)
    {
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Yawble</title></head><body style=\"font-family:system-ui;margin:3em\"><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(head, ct);
            await stream.WriteAsync(body, ct);
        }
        catch (IOException) { /* the browser went away: the parameters still count */ }
    }

    internal static IReadOnlyDictionary<string, string> Parse(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
            if (!map.ContainsKey(key)) map[key] = Uri.UnescapeDataString((kv.Length > 1 ? kv[1] : "").Replace('+', ' '));
        }
        return map;
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Dispose();
        return ValueTask.CompletedTask;
    }
}
