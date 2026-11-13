using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Yawble.Bridge;

/// <summary>
/// The relay: every newline-delimited JSON-RPC message the agent CLI writes on stdin is POSTed to
/// http://bus.yawble.internal/mcp through the run's gateway — as an absolute-form request to the HTTP proxy the
/// sandbox injected, so no DNS is needed and no address is hard-coded — with the run token as its bearer. Replies go to
/// stdout one per line. A transport failure becomes a JSON-RPC error for that request id, so the CLI never hangs.
/// AOT-safe: no reflection-based serialisation.
/// </summary>
public sealed class BusRelay : IDisposable
{
    private readonly GatewayAddress _gateway;
    private readonly HttpClient _http;
    private string? _sessionId;

    public BusRelay(GatewayAddress gateway, HttpMessageHandler? handler = null)
    {
        _gateway = gateway;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            Proxy = new WebProxy(gateway.Proxy, BypassOnLocal: false),
            UseProxy = true,
            UseCookies = false,
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    public async Task RunAsync(TextReader input, TextWriter output, TextWriter log, CancellationToken ct)
    {
        string? line;
        while ((line = await input.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var reply = await RelayAsync(line, log, ct);
            if (reply is null) continue;
            await output.WriteLineAsync(reply);
            await output.FlushAsync(ct);
        }
    }

    /// <summary>Relays one message; returns the line to print, or null when there is none (a notification).</summary>
    public async Task<string?> RelayAsync(string line, TextWriter log, CancellationToken ct)
    {
        var id = RequestId(line);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _gateway.BusEndpoint)
            {
                Content = new StringContent(line, Encoding.UTF8, "application/json"),
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _gateway.RunToken);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (_sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            using var response = await _http.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) _sessionId = sessions.FirstOrDefault();
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.Accepted) return null;
            if (!response.IsSuccessStatusCode)
            {
                await log.WriteLineAsync($"yawble-bridge: bus answered {(int)response.StatusCode}");
                return id is null ? null : Error(id, $"bus refused the call: HTTP {(int)response.StatusCode}");
            }
            var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream" ? FromEventStream(body) : body.Trim();
            return string.IsNullOrEmpty(json) ? null : json;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await log.WriteLineAsync($"yawble-bridge: the gateway could not be reached: {e.Message}");
            return id is null ? null : Error(id, "the bus could not be reached: " + e.Message);
        }
    }

    /// <summary>The JSON text of the message's id (number or string), or null for a notification or unparsable line.</summary>
    public static string? RequestId(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind is JsonValueKind.Number or JsonValueKind.String
                ? id.GetRawText()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Error(string rawId, string message)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            w.WriteRawValue(rawId);
            w.WriteStartObject("error");
            w.WriteNumber("code", -32000);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The data of an SSE body's message events, one JSON message per line.</summary>
    private static string FromEventStream(string body) =>
        string.Join('\n', body.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).Where(l => l.Length > 0));

    public void Dispose() => _http.Dispose();
}
