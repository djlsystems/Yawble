using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Integrations;

/// <summary>
/// The sign-in requests (metadata, registration, tokens): one request at a time, no redirect followed (a 3xx, or an answer from another
/// address, is refused), 30 s each, the reply capped at <see cref="OAuthFlow.MaxReplyBytes"/>. Every address must be https (or http to this computer).
/// </summary>
internal sealed class OAuthHttp(HttpMessageHandler handler) : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Disposes the client only; the handler belongs to the caller.</summary>
    public void Dispose() => _http.Dispose();

    /// <summary>A metadata document: the JSON object, or null when the address has none (4xx); anything else is an error.</summary>
    public async Task<JsonObject?> GetJsonAsync(Uri url, CancellationToken ct)
    {
        Checked(url, "sign-in information address");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        var (status, body) = await SendAsync(request, url, ct);
        if (status is >= 400 and < 500) return null;
        if (status is < 200 or >= 300) throw new McpException($"the sign-in information at {url.Host} could not be read: the server answered {status}");
        return body ?? throw new McpException($"the sign-in information at {url.Host} was not JSON");
    }

    public async Task<JsonObject> SendJsonAsync(Uri url, JsonObject body, string what, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json") };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        var (status, reply) = await SendAsync(request, url, ct);
        if (status is < 200 or >= 300) throw new McpException($"the server refused Yawble's {what} ({status}{ErrorCode(reply)})");
        return reply ?? throw new McpException($"the server's {what} answer was not JSON");
    }

    public async Task<(int Status, JsonObject? Body)> PostFormAsync(Uri url, IEnumerable<(string, string)> form, CancellationToken ct)
    {
        Checked(url, "token address");
        var text = string.Join('&', form.Select(f => Uri.EscapeDataString(f.Item1) + "=" + Uri.EscapeDataString(f.Item2)));
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(text, new UTF8Encoding(false), "application/x-www-form-urlencoded") };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return await SendAsync(request, url, ct);
    }

    private async Task<(int Status, JsonObject? Body)> SendAsync(HttpRequestMessage request, Uri url, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400 || response.RequestMessage?.RequestUri is not { } got || got.AbsoluteUri != url.AbsoluteUri)
                throw new McpException($"the sign-in address at {url.Host} redirected elsewhere; Yawble does not follow that");
            if (response.Content.Headers.ContentLength > OAuthFlow.MaxReplyBytes) throw TooBig();
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await stream.ReadAsync(chunk, limit.Token)) > 0)
            {
                if (buffer.Length + n > OAuthFlow.MaxReplyBytes) throw TooBig();
                buffer.Write(chunk, 0, n);
            }
            JsonObject? body = null;
            try { body = buffer.Length == 0 ? null : JsonNode.Parse(buffer.ToArray()) as JsonObject; }
            catch (JsonException) { /* not JSON: the caller says so */ }
            return (status, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException($"the sign-in address at {url.Host} did not answer in time");
        }
        catch (HttpRequestException)
        {
            throw new McpException($"could not reach the sign-in address at {url.Host}");
        }
        catch (IOException)
        {
            throw new McpException($"the connection to the sign-in address at {url.Host} broke");
        }
    }

    private static McpException TooBig() => new($"the sign-in server's answer was larger than {OAuthFlow.MaxReplyBytes / (1024 * 1024)} MB");

    // ------------------------------------------------------------------ helpers

    /// <summary>A metadata document's endpoint: present, absolute, https (or http to this computer).</summary>
    public static Uri Endpoint(JsonObject meta, string key, string what)
    {
        if (Str(meta[key]) is not { Length: > 0 } text || !Uri.TryCreate(text, UriKind.Absolute, out var u))
            throw new McpException($"the sign-in server gave no valid {what}");
        return Checked(u, what);
    }

    /// <summary>https, or plain http only to this computer; no user info in the address.</summary>
    public static Uri Checked(Uri u, string what)
    {
        var loopback = u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        if (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && loopback))
            return u.UserInfo.Length == 0 ? u : throw new McpException($"the sign-in {what} carries a user name, so Yawble will not use it");
        throw new McpException($"the sign-in {what} at {u.Host} must start with https://, so Yawble will not use it");
    }

    public static bool SameUrl(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub)
        && string.Equals(ua.AbsoluteUri.TrimEnd('/'), ub.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);

    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>An OAuth error code as it may be shown: letters, digits and _ only, short.</summary>
    public static string Code(string? error)
    {
        var clean = new string((error ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(40).ToArray());
        return clean.Length > 0 ? clean : "no reason given";
    }

    public static string ErrorCode(JsonObject? o) => Str(o?["error"]) is { } e ? ": " + Code(e) : "";
}
