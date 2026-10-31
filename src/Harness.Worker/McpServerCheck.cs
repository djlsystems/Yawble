using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// AN MCP SERVER'S CHECK, on this worker (MCP 1): what an entry's save and its Test both do. Over
/// streamable HTTP it initializes, says it is initialized, and lists the server's tools, page by page,
/// with the probe's headers on every request. Answers ok with the tools' names, refused (HTTP 401 or 403:
/// the server said no to the credential) or failed (not reached, not answering, or not answering as an
/// MCP server) in one sentence that names the host - never the URL, whose query may hold a key. Nothing
/// here logs, and the caller takes every value the probe holds out of the sentence.
/// </summary>
public sealed class McpServerCheck(HttpClient http, TimeSpan timeout)
{
    /// <summary>The MCP revision this check asks for. A server answering another one it supports is fine.</summary>
    public const string ProtocolVersion = "2025-06-18";

    /// <summary>The most of one answer read: a long tool list fits, a server that never stops does not.</summary>
    private const int MaximumBody = 1024 * 1024;

    /// <summary>The most pages of tools followed, so a server whose cursor never ends cannot hold the check.</summary>
    private const int MaximumPages = 20;

    private const int MaximumReason = 300;

    public async Task<(string Outcome, string Sentence, IReadOnlyList<string>? Tools)> CheckAsync(McpProbe probe, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        try
        {
            var session = new Session(probe);

            var initialize = await session.CallAsync(http, 1, "initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "Yawble", ["version"] = "1" },
            }, "initialize", limit.Token);
            if (initialize.Failure is { } notInitialized) return (notInitialized.Outcome, notInitialized.Sentence, null);

            if (Text(initialize.Result?["protocolVersion"]) is { Length: > 0 } version) session.Version = version;

            var initialized = await session.NotifyAsync(http, "notifications/initialized", limit.Token);
            if (initialized is { } notified) return (notified.Outcome, notified.Sentence, null);

            var tools = new List<string>();
            string? cursor = null;
            for (var page = 0; page < MaximumPages; page++)
            {
                var parameters = new JsonObject();
                if (cursor is not null) parameters["cursor"] = cursor;

                var listed = await session.CallAsync(http, 2 + page, "tools/list", parameters, "list its tools", limit.Token);
                if (listed.Failure is { } notListed) return (notListed.Outcome, notListed.Sentence, null);

                if (listed.Result?["tools"] is not JsonArray array)
                {
                    return (ConnectionTested.Failed, $"{probe.Host} answered the tools list without any tools. Check that the address is an MCP server.", null);
                }

                tools.AddRange(array.Select(t => Text(t?["name"])).OfType<string>().Where(n => n.Length > 0));

                cursor = Text(listed.Result["nextCursor"]);
                if (string.IsNullOrEmpty(cursor)) break;
            }

            return (ConnectionTested.Ok, $"Connected: {probe.Name} listed {tools.Count} tool{(tools.Count == 1 ? "" : "s")}.", tools);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (ConnectionTested.Failed, $"{probe.Host} did not answer in time.", null);
        }
        catch (HttpRequestException)
        {
            return (ConnectionTested.Failed, $"Could not reach {probe.Host}.", null);
        }
        catch (IOException)
        {
            return (ConnectionTested.Failed, $"{probe.Host} closed the connection before it answered.", null);
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private sealed record Failure(string Outcome, string Sentence);

    private sealed record Answer(JsonNode? Result, Failure? Failure);

    /// <summary>One check's requests: the session id the server gave, and the revision it answered.</summary>
    private sealed class Session(McpProbe probe)
    {
        private string? _id;

        public string? Version { get; set; }

        public async Task<Answer> CallAsync(HttpClient http, int id, string method, JsonObject parameters, string what, CancellationToken ct)
        {
            using var request = Request(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (Refusal(response, what) is { } refused) return new Answer(null, refused);

            if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids) && ids.FirstOrDefault() is { Length: > 0 } session) _id ??= session;

            var message = await ReadAsync(response, id, ct);
            if (message is not JsonObject answer)
            {
                return new Answer(null, new Failure(ConnectionTested.Failed,
                    $"{probe.Host} did not answer as an MCP server when asked to {what}. Check the address."));
            }

            if (answer["error"] is JsonObject error)
            {
                var reason = Text(error["message"]) is { Length: > 0 } said ? Cut(said) : "no reason given";
                return new Answer(null, new Failure(ConnectionTested.Failed, $"{probe.Host} would not {what}: {reason.TrimEnd('.')}."));
            }

            return new Answer(answer["result"], null);
        }

        public async Task<Failure?> NotifyAsync(HttpClient http, string method, CancellationToken ct)
        {
            using var request = Request(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            return Refusal(response, "take the initialized notice");
        }

        private HttpRequestMessage Request(JsonObject body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, probe.Url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (_id is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _id);
            if (Version is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", Version);
            foreach (var (name, value) in probe.Headers) request.Headers.TryAddWithoutValidation(name, value);
            return request;
        }

        /// <summary>A status that ends the check: a refusal, a redirect or any other failure. Null for a success.</summary>
        private Failure? Refusal(HttpResponseMessage response, string what)
        {
            var status = (int)response.StatusCode;

            return status switch
            {
                401 or 403 => new Failure(ConnectionTested.Refused, $"{probe.Host} refused the credential (HTTP {status})."),
                >= 300 and < 400 => new Failure(ConnectionTested.Failed,
                    $"{probe.Host} answered with a redirect (HTTP {status}) when asked to {what}. Name the server's own address."),
                < 200 or >= 300 => new Failure(ConnectionTested.Failed, $"{probe.Host} answered HTTP {status} when asked to {what}."),
                _ => null,
            };
        }

        /// <summary>
        /// The JSON-RPC answer to request <paramref name="id"/>: the body itself, or - from an event
        /// stream - the first event carrying that id, read no further than it.
        /// </summary>
        private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, int id, CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            if (response.Content.Headers.ContentType?.MediaType is not "text/event-stream")
            {
                var buffer = new char[MaximumBody];
                var read = 0;
                while (read < buffer.Length && await reader.ReadAsync(buffer.AsMemory(read), ct) is var n and > 0) read += n;

                return Parse(new string(buffer, 0, read));
            }

            var data = new StringBuilder();
            var total = 0;
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

                // THE END OF ONE EVENT: the answer when it carries this id; a server's own notice otherwise.
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

        private static string Cut(string text)
        {
            var line = text.ReplaceLineEndings(" ");
            return line.Length <= MaximumReason ? line : line[..MaximumReason].TrimEnd() + "…";
        }
    }
}
