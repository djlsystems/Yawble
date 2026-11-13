using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Yawble.Core.Integrations;

/// <summary>
/// A client of one remote MCP server over streamable HTTP: every message is a POST to the one URL; the reply is a JSON object or an event stream
/// that carries the response. Initializes on first use, keeps the session id the server gave, and starts a new session (once) if the server says it expired.
/// The headers (an access token, for one) are asked for on every request so a refreshed token applies at once.
/// A redirect is an error (whatever the handler does about redirects): the reply must come from the URL that was asked.
/// Ordinary requests run concurrently; only initializing and replacing a session are serialized.
/// </summary>
public sealed class McpHttpClient(Uri url, Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> headers, HttpMessageHandler handler, Action<string>? log = null) : IMcpClient
{
    public const int MaxReplyBytes = 4 * 1024 * 1024;
    private const string ProtocolVersion = "2025-06-18";
    private static readonly HashSet<string> Reserved = new(["Accept", "Content-Type", "Mcp-Session-Id", "MCP-Protocol-Version", "Host", "Content-Length"], StringComparer.OrdinalIgnoreCase);

    /// <summary>What an initialized connection is: the session id the server gave (if any) and the protocol version to speak.</summary>
    private sealed record Session(string? Id, string Protocol);

    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _gate = new(1, 1); // initialize / replace session only; never disposed, so a waiter cannot meet a disposed semaphore
    private readonly CancellationTokenSource _closing = new();
    private volatile Session? _session;
    private int _disposed;
    private long _nextId;
    private volatile string _lastError = "";

    public async Task<JsonArray> ListToolsAsync(CancellationToken ct)
    {
        var result = await RequestAsync("tools/list", new JsonObject(), ct);
        return result["tools"] switch { null => [], JsonArray a => a, _ => throw new McpException("the server's tool list was not a list") };
    }

    public async Task<(string Text, bool IsError)> CallToolAsync(string tool, JsonObject arguments, CancellationToken ct)
    {
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() }, ct);
        var parts = new List<string>();
        if (result["content"] is { } content)
        {
            if (content is not JsonArray items) throw new McpException("the server's reply had content that was not a list");
            foreach (var item in items.OfType<JsonObject>())
            {
                var type = Str(item["type"]);
                parts.Add(type == "text" ? Str(item["text"]) ?? "" : $"[{type ?? "content"}]");
            }
        }
        var isError = result["isError"] switch
        {
            null => false,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _ => throw new McpException("the server's reply had a malformed isError"),
        };
        return (string.Join("\n", parts), isError);
    }

    public string LastError() => _lastError.Length > 0 ? _lastError : "no message";

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Server-provided text made safe for a message or log: secrets scrubbed, one line, clipped.</summary>
    private static string Clean(string? text, int max = 200)
    {
        var s = CliRunner.Redact(text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > max ? s[..max] : s;
    }

    private void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(McpHttpClient)); }

    private async Task<JsonObject> RequestAsync(string method, JsonObject p, CancellationToken ct)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closing.Token);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var session = await EnsureAsync(linked.Token);
                try
                {
                    return (await SendAsync(method, p, session, linked.Token)).Result;
                }
                catch (SessionExpiredException) when (attempt == 0)
                {
                    await DropAsync(session, linked.Token);
                    log?.Invoke($"MCP server {url.Host}: session expired; starting a new one");
                }
                catch (SessionExpiredException)
                {
                    throw new McpException("the server keeps saying the session expired");
                }
            }
        }
        catch (McpException e)
        {
            _lastError = e.Message;
            throw;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(McpHttpClient));
        }
    }

    private async Task<Session> EnsureAsync(CancellationToken ct)
    {
        if (_session is { } s) return s;
        await _gate.WaitAsync(ct);
        try
        {
            if (_session is { } again) return again;
            var made = await InitializeAsync(ct);
            ThrowIfDisposed(); // disposed while connecting: do not keep a session nobody will end
            _session = made;
            return made;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DropAsync(Session expired, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { if (ReferenceEquals(_session, expired)) _session = null; }
        finally { _gate.Release(); }
    }

    /// <summary>initialize, then notifications/initialized; the connection counts as made only once both worked.</summary>
    private async Task<Session> InitializeAsync(CancellationToken ct)
    {
        var (init, sid) = await SendAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "yawble", ["version"] = CoreInfo.Version },
        }, null, ct);
        if (sid is not null && !HeaderSafe(sid)) throw new McpException("the server gave a session id that cannot be sent back");
        var offered = Str(init["protocolVersion"]);
        var session = new Session(sid, offered is not null && HeaderSafe(offered) ? offered : ProtocolVersion);
        try { await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, null, session, ct); }
        catch (SessionExpiredException) { throw new McpException("the server forgot the session it had just started"); }
        log?.Invoke($"MCP server {url.Host}: connected ({Clean(Str((init["serverInfo"] as JsonObject)?["name"]) ?? "?", 80)})");
        return session;
    }

    private static bool HeaderSafe(string value) => value.Length is > 0 and <= 256 && value.All(c => c is >= '!' and <= '~');

    private async Task<(JsonObject Result, string? Session)> SendAsync(string method, JsonObject p, Session? session, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var (reply, sid) = await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p.DeepClone() }, id, session, ct);
        if (reply is null) throw new McpException("the server sent no reply");
        if (reply["error"] is { } err)
            throw new McpException(Clean(err is JsonObject eo ? Str(eo["message"]) : null, 600) is { Length: > 0 } m ? m : "the server refused the call");
        return (reply["result"] as JsonObject ?? throw new McpException("the server's reply had no result object"), sid);
    }

    /// <summary>Posts one message. With an id, returns the JSON-RPC response carrying it; without, expects only an acknowledgement.
    /// The session id the server sent is returned only when this is the initialize call (no session yet) and the answer was a success.</summary>
    private async Task<(JsonObject? Reply, string? Session)> PostAsync(JsonObject message, long? id, Session? session, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(McpClient.CallTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(message.ToJsonString(), new UTF8Encoding(false), "application/json") };
            await DecorateAsync(request, session, limit.Token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            CheckNotRedirected(response);
            var status = (int)response.StatusCode;
            if (status is 401 or 403)
            {
                var www = response.Headers.WwwAuthenticate.Count > 0 ? string.Join(", ", response.Headers.WwwAuthenticate.Select(a => a.ToString())) : null;
                throw new McpUnauthorizedException($"the server refused the sign-in ({status})", www, status);
            }
            if (status is >= 300 and < 400) throw new McpException("the server redirected; check the URL");
            if (status == 404 && session?.Id is not null) throw new SessionExpiredException();
            if (!response.IsSuccessStatusCode)
                throw new McpException($"the server answered {status} {Clean(response.ReasonPhrase, 80)}".TrimEnd() + await ErrorSnippet(response, limit.Token));
            string? sid = null;
            if (session is null && response.Headers.TryGetValues("Mcp-Session-Id", out var sids)) sid = sids.FirstOrDefault() is { Length: > 0 } s ? s : null;
            if (id is null) return (null, sid);
            return (await ReadReplyAsync(response, id.Value, limit.Token), sid);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException($"the server did not answer within {McpClient.CallTimeout.TotalMinutes:0} minutes");
        }
        catch (HttpRequestException e)
        {
            throw new McpException("could not reach the server: " + Clean(e.Message));
        }
        catch (IOException e)
        {
            throw new McpException("the connection to the server broke: " + Clean(e.Message));
        }
    }

    /// <summary>A handler that follows redirects hides them: the answer must come from the URL asked, or the token may have gone elsewhere.</summary>
    private void CheckNotRedirected(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri is not { } got || got.AbsoluteUri != url.AbsoluteUri)
            throw new McpException("the server redirected; check the URL");
    }

    /// <param name="closing">Ending the session: if the headers cannot be given (the sign-in is no longer the one this client was made for), none are sent.</param>
    private async Task DecorateAsync(HttpRequestMessage request, Session? session, CancellationToken ct, bool closing = false)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        IReadOnlyDictionary<string, string> given;
        try { given = await headers(ct); }
        catch (McpException) when (closing) { given = new Dictionary<string, string>(); }
        foreach (var (k, v) in given)
        {
            if (string.IsNullOrEmpty(k) || k.Any(c => c is '\r' or '\n' or ':' or ' ')) throw new McpException("a header name for the server is not valid");
            if (Reserved.Contains(k)) throw new McpException($"the header {k} is set by Yawble and cannot be overridden");
            if (v is null || v.Any(c => c is '\r' or '\n')) throw new McpException($"the value of the header {k} is not valid");
            if (!request.Headers.TryAddWithoutValidation(k, v)) throw new McpException($"the header {k} could not be sent");
        }
        if (session is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", session.Protocol);
        if (session?.Id is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session.Id);
    }

    private static async Task<string> ErrorSnippet(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(new LimitedStream(await response.Content.ReadAsStreamAsync(ct), 2048, false), Encoding.UTF8);
            var text = CliRunner.Redact(await reader.ReadToEndAsync(ct)).Trim();
            return text.Length > 0 ? ": " + (text.Length > 300 ? text[..300] : text) : "";
        }
        catch (Exception e) when (e is IOException or McpException or HttpRequestException) { return ""; }
    }

    private static async Task<JsonObject> ReadReplyAsync(HttpResponseMessage response, long id, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxReplyBytes) throw TooBig();
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        await using var stream = new LimitedStream(await response.Content.ReadAsStreamAsync(ct), MaxReplyBytes, true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (type.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var data = new StringBuilder();
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null || line.Length == 0)
                {
                    if (data.Length > 0 && Match(data.ToString(), id) is { } found) return found;
                    data.Clear();
                    if (line is null) throw new McpException("the server's event stream ended without a reply");
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart(' '));
                }
            }
        }
        var body = await reader.ReadToEndAsync(ct);
        return Match(body, id) ?? throw new McpException("the server's reply was not the answer to the request");
    }

    /// <summary>The JSON-RPC response with this id, or null for anything else (a request, a notification, another id, not JSON).</summary>
    private static JsonObject? Match(string json, long id)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject o && o["method"] is null && o["id"] is JsonValue v && v.TryGetValue<long>(out var got) && got == id) return o;
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    private static McpException TooBig() => new($"the server's reply was larger than {MaxReplyBytes / (1024 * 1024)} MB");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var session = _session;
        _session = null;
        _closing.Cancel(); // in-flight calls stop and report ObjectDisposedException
        if (session?.Id is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                await DecorateAsync(request, session, cts.Token, closing: true);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                CheckNotRedirected(response); // a redirected DELETE has already gone elsewhere; nothing more to do, the error is ignored like any other
            }
            catch (Exception) { } // best effort: the server may not support ending a session
        }
    }

    private sealed class SessionExpiredException : Exception;

    /// <summary>Passes reads through, failing (or stopping quietly) once more than <c>limit</c> bytes have been read.</summary>
    private sealed class LimitedStream(Stream inner, long limit, bool fail) : Stream
    {
        private long _read;
        /// <summary>At the cap: more data is an error (or just the end, when not failing); no more data is a clean end.</summary>
        private int Over(int probe) => probe == 0 || !fail ? 0 : throw TooBig();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var room = limit - _read;
            if (room <= 0) return Over(inner.Read(new byte[1]));
            var n = inner.Read(buffer[..(int)Math.Min(buffer.Length, room)]);
            _read += n;
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var room = limit - _read;
            if (room <= 0) return Over(await inner.ReadAsync(new byte[1], cancellationToken));
            var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, room)], cancellationToken);
            _read += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); await base.DisposeAsync(); }
    }
}

/// <summary>The server refused the sign-in (401/403); <see cref="WwwAuthenticate"/> is what it said to do about it, if anything.</summary>
public class McpUnauthorizedException(string message, string? wwwAuthenticate, int status = 401) : McpException(message)
{
    /// <summary>401 (not signed in) or 403 (signed in, but not allowed this call).</summary>
    public int Status { get; } = status;
    public string? WwwAuthenticate { get; } = wwwAuthenticate;
}
