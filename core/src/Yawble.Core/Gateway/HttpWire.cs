using System.Text;

namespace Yawble.Core.Gateway;

/// <summary>An HTTP/1.1 request head as the gateway reads it off the socket.</summary>
public sealed record WireRequest(string Method, string Target, string Version, IReadOnlyList<KeyValuePair<string, string>> Headers)
{
    public string? Header(string name) => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public bool Has(string name, string token) =>
        Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Any(h => h.Value.Split(',').Any(v => v.Trim().Equals(token, StringComparison.OrdinalIgnoreCase)));

    public bool KeepAlive => Version == "HTTP/1.1" ? !Has("Connection", "close") : Has("Connection", "keep-alive");
}

public sealed class WireException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Just enough HTTP/1.1 for a forward proxy: request heads, bodies (length or chunked) and responses.</summary>
public static class HttpWire
{
    public const int MaxHeadBytes = 64 * 1024;

    /// <summary>Hop-by-hop headers (RFC 9110 §7.6.1) and proxy headers: never forwarded in either direction.</summary>
    public static readonly IReadOnlySet<string> HopByHop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade",
    };

    /// <summary>Reads one request head, or null at a clean end of stream before any byte.</summary>
    public static async Task<WireRequest?> ReadHeadAsync(Stream s, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one, ct);
            if (n == 0)
            {
                if (buffer.Length == 0) return null;
                throw new WireException(400, "connection closed inside a request head");
            }
            buffer.WriteByte(one[0]);
            var len = buffer.Length;
            if (len >= 4)
            {
                var b = buffer.GetBuffer();
                if (b[len - 4] == '\r' && b[len - 3] == '\n' && b[len - 2] == '\r' && b[len - 1] == '\n') break;
            }
            if (len > MaxHeadBytes) throw new WireException(431, "request head too large");
        }
        var text = Encoding.Latin1.GetString(buffer.GetBuffer(), 0, (int)buffer.Length - 4);
        var lines = text.Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length != 3 || !start[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new WireException(400, "malformed request line");
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new WireException(400, "malformed header line");
            headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return new WireRequest(start[0].ToUpperInvariant(), start[1], start[2], headers);
    }

    /// <summary>Reads the body announced by the head, up to <paramref name="max"/> bytes.</summary>
    public static async Task<byte[]> ReadBodyAsync(Stream s, WireRequest head, long max, CancellationToken ct)
    {
        if (head.Has("Transfer-Encoding", "chunked"))
        {
            var body = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadLineAsync(s, ct);
                var size = Convert.ToInt64(sizeLine.Split(';')[0].Trim(), 16);
                if (size == 0)
                {
                    while ((await ReadLineAsync(s, ct)).Length > 0) { } // trailers
                    return body.ToArray();
                }
                if (body.Length + size > max) throw new WireException(413, "request body too large");
                var chunk = new byte[size];
                await s.ReadExactlyAsync(chunk, ct);
                body.Write(chunk);
                await ReadLineAsync(s, ct);
            }
        }
        var lengthText = head.Header("Content-Length");
        if (lengthText is null) return [];
        if (!long.TryParse(lengthText, out var length) || length < 0) throw new WireException(400, "bad Content-Length");
        if (length > max) throw new WireException(413, "request body too large");
        var bytes = new byte[length];
        await s.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    private static async Task<string> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one, ct) == 0) throw new WireException(400, "connection closed inside a chunked body");
            if (one[0] == '\n') return sb.ToString();
            if (one[0] != '\r') sb.Append((char)one[0]);
            if (sb.Length > 1024) throw new WireException(400, "chunk line too long");
        }
    }

    public static Task WriteAsync(Stream s, string text, CancellationToken ct) => s.WriteAsync(Encoding.Latin1.GetBytes(text), ct).AsTask();

    /// <summary>A small complete response the gateway itself makes (errors, bus replies).</summary>
    public static async Task RespondAsync(Stream s, int status, string reason, string contentType, byte[] body, bool keepAlive, CancellationToken ct,
        IEnumerable<KeyValuePair<string, string>>? extra = null)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        if (body.Length > 0 || status is not (204 or 304)) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        foreach (var (k, v) in extra ?? []) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n\r\n");
        await WriteAsync(s, sb.ToString(), ct);
        if (body.Length > 0) await s.WriteAsync(body, ct);
        await s.FlushAsync(ct);
    }

    public static Task RespondJsonAsync(Stream s, int status, string reason, string json, bool keepAlive, CancellationToken ct) =>
        RespondAsync(s, status, reason, "application/json", Encoding.UTF8.GetBytes(json), keepAlive, ct);

    public static string Reason(int status) => status switch
    {
        200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 413 => "Content Too Large", 431 => "Request Header Fields Too Large", 502 => "Bad Gateway",
        503 => "Service Unavailable", _ => "Status",
    };
}
