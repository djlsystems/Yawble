using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harness.Host;

/// <summary>What one request to a web address MCP server came to: its <see cref="Result"/>, the server's
/// <see cref="Error"/> answer, no answer in time (<see cref="NoAnswer"/>), or a <see cref="Failure"/> sentence.</summary>
public sealed record McpForwarded(JsonObject? Result, string? Error, bool NoAnswer, string? Failure)
{
    public static McpForwarded Answered(JsonObject result) => new(result, null, false, null);

    public static McpForwarded Refused(string error) => new(null, error, false, null);

    public static McpForwarded Silent => new(null, null, true, null);

    public static McpForwarded Failed(string sentence) => new(null, null, false, sentence);
}

/// <summary>
/// THE HOST TALKING TO A TEAM'S WEB ADDRESS MCP SERVER (MCP 2), with the header its entry's template makes on
/// every request, so no credential leaves the Host. Each list or call is its own short session over streamable
/// HTTP: initialize, say it is initialized, then the one request. A call that has no answer within
/// <see cref="CallTimeout"/> (120 seconds unless a test says otherwise) is <see cref="McpForwarded.NoAnswer"/>.
/// Nothing here logs, and no sentence carries the URL: only its host.
/// </summary>
public sealed class McpForwarding(HttpMessageHandler? handler = null, TimeSpan? callTimeout = null, TimeSpan? listTimeout = null)
{
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(120);

    /// <summary>How long a run's start waits for one server's tools: a server down must not hold the run.</summary>
    public static readonly TimeSpan DefaultListTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The most of one answer read: a 20 MB binary result as base64, with room to say so.</summary>
    private const int MaximumBody = 64 * 1024 * 1024;

    private const int MaximumPages = 20;

    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: handler is null)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public TimeSpan CallTimeout { get; } = callTimeout ?? DefaultCallTimeout;

    public TimeSpan ListTimeout { get; } = listTimeout ?? DefaultListTimeout;

    /// <summary>Every tool the server lists, as it gives them, page by page; or why not.</summary>
    public async Task<(IReadOnlyList<JsonObject>? Tools, string? Failure)> ListAsync(McpServers.McpTarget target, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ListTimeout);

        var tools = new List<JsonObject>();
        var answer = await SessionAsync(target, async (session, token) =>
        {
            string? cursor = null;
            for (var page = 0; page < MaximumPages; page++)
            {
                var parameters = new JsonObject();
                if (cursor is not null) parameters["cursor"] = cursor;

                var listed = await session.CallAsync(2 + page, "tools/list", parameters, token);
                if (listed.Result is not { } result) return listed;
                if (result["tools"] is not JsonArray array) return McpForwarded.Failed($"{Host(target)} answered the tools list without any tools.");

                tools.AddRange(array.OfType<JsonObject>().Where(t => Text(t["name"]) is { Length: > 0 }));

                cursor = Text(result["nextCursor"]);
                if (string.IsNullOrEmpty(cursor)) break;
            }

            return McpForwarded.Answered(new JsonObject());
        }, limit.Token, ct);

        if (answer.Result is not null) return (tools, null);
        return (null, answer.Failure ?? (answer.NoAnswer ? $"{Host(target)} did not answer." : $"{Host(target)} would not list its tools: {answer.Error}"));
    }

    /// <summary>One <c>tools/call</c>, within <see cref="CallTimeout"/>.</summary>
    public async Task<McpForwarded> CallAsync(McpServers.McpTarget target, string tool, JsonObject? arguments, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(CallTimeout);

        return await SessionAsync(target, (session, token) => session.CallAsync(2, "tools/call", new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = arguments?.DeepClone() ?? new JsonObject(),
        }, token), limit.Token, ct);
    }

    private async Task<McpForwarded> SessionAsync(
        McpServers.McpTarget target, Func<Session, CancellationToken, Task<McpForwarded>> work, CancellationToken limit, CancellationToken ct)
    {
        try
        {
            var session = new Session(_http, target);

            var initialize = await session.CallAsync(1, "initialize", new JsonObject
            {
                ["protocolVersion"] = McpServerCheck.ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "Harness", ["version"] = "1" },
            }, limit);
            if (initialize.Result is not { } initialized) return initialize;

            if (Text(initialized["protocolVersion"]) is { Length: > 0 } version) session.Version = version;

            if (await session.NotifyAsync("notifications/initialized", limit) is { } notified) return notified;

            return await work(session, limit);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return McpForwarded.Silent;
        }
        catch (HttpRequestException)
        {
            return McpForwarded.Failed($"Could not reach {Host(target)}.");
        }
        catch (IOException)
        {
            return McpForwarded.Failed($"{Host(target)} closed the connection before it answered.");
        }
    }

    private static string Host(McpServers.McpTarget target) => Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) ? uri.Host : "The server";

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>One list's or call's requests: the session id the server gave, and the revision it answered.</summary>
    private sealed class Session(HttpClient http, McpServers.McpTarget target)
    {
        private string? _id;

        public string? Version { get; set; }

        public async Task<McpForwarded> CallAsync(int id, string method, JsonObject parameters, CancellationToken ct)
        {
            using var request = Request(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (Refusal(response) is { } refused) return refused;

            if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids) && ids.FirstOrDefault() is { Length: > 0 } session) _id ??= session;

            var message = await ReadAsync(response, id, ct);
            if (message is not JsonObject answer) return McpForwarded.Failed($"{Host(target)} did not answer as an MCP server.");

            if (answer["error"] is JsonObject error) return McpForwarded.Refused(Text(error["message"]) is { Length: > 0 } said ? said : "no reason given");

            return answer["result"] is JsonObject result ? McpForwarded.Answered(result) : McpForwarded.Failed($"{Host(target)} answered without a result.");
        }

        public async Task<McpForwarded?> NotifyAsync(string method, CancellationToken ct)
        {
            using var request = Request(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            return Refusal(response);
        }

        private HttpRequestMessage Request(JsonObject body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, target.Url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (_id is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _id);
            if (Version is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", Version);
            foreach (var (name, value) in target.Headers) request.Headers.TryAddWithoutValidation(name, value);
            return request;
        }

        private McpForwarded? Refusal(HttpResponseMessage response)
        {
            var status = (int)response.StatusCode;

            return status switch
            {
                401 or 403 => McpForwarded.Failed($"{Host(target)} refused the credential (HTTP {status})."),
                >= 300 and < 400 => McpForwarded.Failed($"{Host(target)} answered with a redirect (HTTP {status})."),
                < 200 or >= 300 => McpForwarded.Failed($"{Host(target)} answered HTTP {status}."),
                _ => null,
            };
        }

        /// <summary>The JSON-RPC answer to request <paramref name="id"/>: the body, or the event carrying that id.</summary>
        private async Task<JsonNode?> ReadAsync(HttpResponseMessage response, int id, CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            if (response.Content.Headers.ContentType?.MediaType is not "text/event-stream")
            {
                var body = new StringBuilder();
                var buffer = new char[81920];
                while (await reader.ReadAsync(buffer.AsMemory(), ct) is var n and > 0)
                {
                    body.Append(buffer, 0, n);
                    if (body.Length > MaximumBody) return null;
                }

                return Parse(body.ToString());
            }

            var data = new StringBuilder();
            var total = 0L;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                total += line.Length;
                if (total > MaximumBody) return null;

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line.AsSpan(5).TrimStart(' ')).Append('\n');
                    continue;
                }

                if (line.Length > 0 || data.Length == 0) continue;

                if (Parse(data.ToString()) is JsonObject message && message["id"] is JsonValue value
                    && value.TryGetValue<int>(out var answered) && answered == id)
                {
                    return message;
                }

                data.Clear();
            }

            return data.Length > 0 ? Parse(data.ToString()) : null;
        }

        private static JsonNode? Parse(string text)
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
