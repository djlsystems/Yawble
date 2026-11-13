using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Companion;

namespace Yawble.Companion;

/// <summary>
/// `yawble mcp`: an MCP server over stdio for Claude Code. Newline-delimited JSON-RPC 2.0: stdout carries protocol messages
/// only (every diagnostic goes to <c>log</c>, stderr); notifications are never answered. Requests are served concurrently.
/// The app is reached lazily (on the first tools/list or tools/call, never on initialize) over one shared connection, made
/// again when the app has closed it (a restart). While the app cannot be reached, tools/list offers one tool,
/// <see cref="StatusTool"/>, whose description says why; once it can, the client is told the list changed
/// (notifications/tools/list_changed), as it is after a reconnect that finds other tools (another level). A destructive
/// call's request is long-polled, on a connection of its own so other calls go on meanwhile, up to <c>approvalWait</c>
/// (YAWBLE_MCP_WAIT, default 60 s); still undecided, it is reported as waiting for the person's approval in the Yawble
/// window. Never a confirmation token: the app does not hand one out.
/// </summary>
internal sealed class McpStdio(IControlConnector connector, KeyFiles keys, string? client, TextReader input, TextWriter output, TextWriter log,
    TimeSpan? approvalWait = null, TimeSpan? callLimit = null, TimeSpan? probeInterval = null)
{
    /// <summary>As the core's McpBusServer.SupportedProtocolVersions (newest first; a test checks they agree).</summary>
    public static readonly string[] SupportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(60);

    /// <summary>The one tool offered while the app cannot be reached: its call says where things stand now.</summary>
    public const string StatusTool = "yawble_status";

    private const string StatusOnly = "status-only";
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };
    private readonly Lock _write = new();
    private readonly SemaphoreSlim _connect = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new();
    private IControlChannel? _channel;
    private readonly ConcurrentBag<IControlChannel> _retired = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _probe;

    /// <summary>What the client was last given by tools/list (the tools' JSON, or <see cref="StatusOnly"/>); null before any.</summary>
    private string? _served;

    /// <summary>How long one call may take (a hung one retires its connection); how often an unreachable app is looked for again.</summary>
    private TimeSpan CallLimit => callLimit ?? Cli.CallLimit;

    public static readonly TimeSpan DefaultProbeInterval = TimeSpan.FromSeconds(30);

    private TimeSpan ProbeInterval => probeInterval ?? DefaultProbeInterval;

    private int WaitSeconds => (int)Math.Ceiling(Math.Max(0, (approvalWait ?? DefaultWait).TotalSeconds));

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var running = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await input.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                running.RemoveAll(t => t.IsCompleted);
                if (Accept(line, ct) is { } task) running.Add(task);
            }
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        finally
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            Task? probe;
            lock (_write) probe = _probe;
            if (probe is not null) await probe.ConfigureAwait(false);
            if (_channel is { } open) await open.DisposeAsync().ConfigureAwait(false);
            foreach (var old in _retired) await old.DisposeAsync().ConfigureAwait(false);
        }
        return 0;
    }

    /// <summary>
    /// One line from the client: a request is registered (so a cancel right behind it finds it) and run as a task; a
    /// notification is handled here; anything else is answered at once.
    /// </summary>
    private Task? Accept(string line, CancellationToken ct)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line, null, Strict);
            Walk(parsed);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            Send(Error(null, -32700, "parse error"));
            return null;
        }
        if (parsed is not JsonObject message)
        {
            Send(Error(null, -32600, "invalid request (batches are not supported)"));
            return null;
        }
        var method = StreamChannel.Text(message["method"]);
        if (!message.ContainsKey("id"))
        {
            if (method is not null) Notification(method, message["params"] as JsonObject);
            return null;
        }
        var id = message["id"]?.DeepClone();
        if (method is null)
        {
            Send(Error(id, -32600, "invalid request: no method (yawble sends no requests, so it expects no responses)"));
            return null;
        }
        var key = id?.ToJsonString() ?? "null";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_inFlight.TryAdd(key, cts))
        {
            cts.Dispose();
            Send(Error(id, -32600, $"a request with id {key} is still in progress"));
            return null;
        }
        var p = message["params"] as JsonObject;
        return Task.Run(() => HandleAsync(id, key, cts, method, p), CancellationToken.None);
    }

    private void Notification(string method, JsonObject? p)
    {
        if (method == "notifications/cancelled" && p?["requestId"] is { } requestId && _inFlight.TryGetValue(requestId.ToJsonString(), out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task HandleAsync(JsonNode? id, string key, CancellationTokenSource cts, string method, JsonObject? p)
    {
        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(p),
                "ping" => new JsonObject(),
                "tools/list" => await ToolsListAsync(cts.Token).ConfigureAwait(false),
                "tools/call" => await ToolsCallAsync(p, cts.Token).ConfigureAwait(false),
                _ => throw new McpError(-32601, $"method not found: {method}"),
            };
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (McpError e)
        {
            Send(Error(id, e.Code, e.Message));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Cancelled by the client (or yawble is ending): no reply.
        }
        catch (Exception e)
        {
            log.WriteLine($"yawble mcp: {method} failed ({e.GetType().Name})");
            Send(Error(id, -32603, "yawble could not complete this request"));
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cts));
            cts.Dispose();
        }
    }

    private static JsonObject Initialize(JsonObject? p)
    {
        var requested = StreamChannel.Text(p?["protocolVersion"]);
        return new JsonObject
        {
            ["protocolVersion"] = requested is not null && SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
            ["serverInfo"] = new JsonObject { ["name"] = CompanionProtocol.Product, ["version"] = Cli.Version },
            ["instructions"] = "The Yawble app's teams on this computer, through the running app, at the level the person paired this client for. " +
                               "Risky actions (merging, deleting, pushing, restoring) wait for the person's approval in the Yawble window: such a call " +
                               "returns the plan and a request id; check it later with request_status.",
        };
    }

    private async Task<JsonNode> ToolsListAsync(CancellationToken ct)
    {
        try
        {
            var r = await CallAppAsync(CompanionProtocol.Tools, null, retrySent: true, CallLimit, ct).ConfigureAwait(false);
            var tools = r["tools"] as JsonArray ?? [];
            lock (_write) _served = tools.ToJsonString();
            return new JsonObject { ["tools"] = tools.DeepClone() };
        }
        catch (ClientException e)
        {
            lock (_write)
            {
                _served = StatusOnly;
                _probe ??= Task.Run(ProbeAsync, CancellationToken.None);
            }
            return new JsonObject
            {
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["name"] = StatusTool,
                    ["description"] = e.Message + " Call this tool to check again; once Yawble can be reached, its tools are listed.",
                    ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
                }),
            };
        }
    }

    private async Task<JsonNode> ToolsCallAsync(JsonObject? p, CancellationToken ct)
    {
        var name = StreamChannel.Text(p?["name"]) ?? throw new McpError(-32602, "tools/call needs a tool name");
        var args = p?["arguments"] switch
        {
            null => new JsonObject(),
            JsonObject o => o.DeepClone().AsObject(),
            _ => throw new McpError(-32602, "tools/call arguments must be an object"),
        };
        if (name == StatusTool) return await StatusAsync(ct).ConfigureAwait(false);
        JsonObject r;
        try
        {
            // Never repeated once sent: an act call that reached the app may have run.
            r = await CallAppAsync(CompanionProtocol.Call, new JsonObject { ["tool"] = name, ["args"] = args }, retrySent: false, CallLimit, ct).ConfigureAwait(false);
        }
        catch (ClientException e)
        {
            return ToolResult(e.Message, true);
        }
        var text = StreamChannel.Text(r["text"]) ?? "";
        var isError = r["isError"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        if (StreamChannel.Text(r["requestId"]) is not { } requestId) return ToolResult(text, isError);
        return await AwaitApprovalAsync(requestId, ct).ConfigureAwait(false);
    }

    private async Task<JsonNode> StatusAsync(CancellationToken ct)
    {
        try
        {
            var r = await CallAppAsync(CompanionProtocol.WhoAmI, null, retrySent: true, CallLimit, ct).ConfigureAwait(false);
            return ToolResult($"Connected to the Yawble app as '{StreamChannel.Text(r["client"])}' with the {StreamChannel.Text(r["level"])} level: " +
                              $"{StreamChannel.Text(r["levelDescription"])}", false);
        }
        catch (ClientException e)
        {
            return ToolResult(e.Message, true);
        }
    }

    /// <summary>
    /// Long-polls the request (steps of at most 60 s) for the configured wait, then reports where it stands. On a connection
    /// of its own, closed afterwards: the app serves one request at a time per connection, and other calls must go on.
    /// </summary>
    private async Task<JsonNode> AwaitApprovalAsync(string id, CancellationToken ct)
    {
        JsonObject? status = null;
        string? trouble = null;
        IControlChannel? poll = null;
        var reopened = false;
        try
        {
            var left = WaitSeconds;
            do
            {
                var step = Math.Min(left, CompanionProtocol.MaxWaitSeconds);
                while (true)
                {
                    poll ??= await ControlSession.OpenAsync(connector, keys, client, ct).ConfigureAwait(false);
                    try
                    {
                        status = await PollAsync(poll, id, step, ct).ConfigureAwait(false);
                        break;
                    }
                    catch (ConnectionLostException) when (!reopened)
                    {
                        reopened = true; // the app restarted mid-wait: look again on a fresh connection
                        await poll.DisposeAsync().ConfigureAwait(false);
                        poll = null;
                    }
                }
                left -= step;
            } while (left > 0 && StreamChannel.Text(status!["state"]) is ("pending" or "running"));
        }
        catch (RequestLostException e)
        {
            return ToolResult(e.Message, true);
        }
        catch (ClientException e)
        {
            trouble = e.Message;
        }
        finally
        {
            if (poll is not null) await poll.DisposeAsync().ConfigureAwait(false);
        }
        var state = status is null ? "pending" : StreamChannel.Text(status["state"]) ?? "pending";
        var plan = status is null ? null : StreamChannel.Text(status["plan"]);
        var result = status is null ? null : StreamChannel.Text(status["result"]);
        var nextId = status is null ? null : StreamChannel.Text(status["nextId"]);
        var check = $"check it with request_status (id \"{id}\")";
        return state switch
        {
            "done" => ToolResult(result ?? "Done.", false),
            "failed" => ToolResult(result ?? $"Request {id} was approved but did not succeed.", true),
            "denied" => ToolResult($"The person denied request {id} in the Yawble window; nothing was done.", true),
            "expired" => ToolResult($"Request {id} expired before the person decided; nothing was done. Ask again if it is still wanted.", true),
            "changed" => ToolResult($"NOT DONE: what request {id} would do changed before it was approved, so it did not run. The Yawble window now shows " +
                                    $"a new request {nextId} with the current list; check it with request_status (id \"{nextId}\").", false),
            "running" => ToolResult($"Approved in the Yawble window and still running; {check}.", false),
            _ => ToolResult($"NOT DONE YET: waiting for the person's approval in the Yawble window (request {id}). " +
                            (plan is null ? "" : $"What it will do: {plan} ") +
                            $"Nothing runs until they approve it there; {check} later." + (trouble is null ? "" : $" ({trouble})"), false),
        };
    }

    private async Task<JsonObject> PollAsync(IControlChannel poll, string id, int waitSeconds, CancellationToken ct)
    {
        var p = new JsonObject { ["id"] = id };
        if (waitSeconds > 0) p["waitSeconds"] = waitSeconds;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(waitSeconds) + CallLimit);
        try
        {
            return await poll.CallAsync(CompanionProtocol.Request, p, null, bounded.Token).ConfigureAwait(false);
        }
        catch (ControlErrorException e) when (e.Code == "not_found")
        {
            throw new RequestLostException(id);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimedOutException();
        }
    }

    /// <summary>
    /// One call on the shared connection, made (and authenticated) when first needed. Only a connection that broke (the app
    /// closed it: a restart) is dropped and made again, never one a call merely stopped waiting on; the call is repeated
    /// only if it never reached the app, or when it only reads.
    /// </summary>
    private async Task<JsonObject> CallAppAsync(string method, JsonObject? p, bool retrySent, TimeSpan limit, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var channel = await ChannelAsync(ct).ConfigureAwait(false);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(limit);
            try
            {
                return await channel.CallAsync(method, p, null, bounded.Token).ConfigureAwait(false);
            }
            catch (ConnectionLostException e)
            {
                await DropAsync(channel).ConfigureAwait(false);
                if (attempt == 0 && (!e.Sent || retrySent))
                {
                    log.WriteLine("yawble mcp: the connection to Yawble ended; connecting again");
                    continue;
                }
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The app works one request at a time per connection and is still on this one: later calls get a fresh
                // connection, and this one is closed once the hung call has had time to end (or when yawble ends).
                await RetireAsync(channel).ConfigureAwait(false);
                throw new TimedOutException();
            }
        }
    }

    /// <summary>The shared connection; a new one is checked for a changed tool list (announced to the client).</summary>
    private async Task<IControlChannel> ChannelAsync(CancellationToken ct)
    {
        await _connect.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_channel is { } open) return open;
            var fresh = await ControlSession.OpenAsync(connector, keys, client, ct).ConfigureAwait(false);
            string? served;
            lock (_write) served = _served;
            if (served is not null)
            {
                string now;
                try
                {
                    using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    bounded.CancelAfter(CallLimit);
                    now = (await fresh.CallAsync(CompanionProtocol.Tools, null, null, bounded.Token).ConfigureAwait(false))["tools"]?.ToJsonString() ?? "[]";
                }
                catch
                {
                    await fresh.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                var changed = false;
                lock (_write)
                {
                    if (_served != now)
                    {
                        _served = now;
                        changed = true;
                    }
                }
                if (changed) Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" });
            }
            return _channel = fresh;
        }
        finally
        {
            _connect.Release();
        }
    }

    private async Task RetireAsync(IControlChannel channel)
    {
        await _connect.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_channel, channel)) _channel = null;
        }
        finally
        {
            _connect.Release();
        }
        _retired.Add(channel);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(CallLimit, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            await channel.DisposeAsync().ConfigureAwait(false);
        }, CancellationToken.None);
    }

    /// <summary>
    /// While the client holds only the status tool: looks for the app every <see cref="ProbeInterval"/> (a connect and auth,
    /// which also compares the tool list and announces the change) until it is reachable or yawble ends.
    /// </summary>
    private async Task ProbeAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(ProbeInterval, _stop.Token).ConfigureAwait(false);
                lock (_write)
                {
                    if (_served != StatusOnly)
                    {
                        _probe = null;
                        return;
                    }
                }
                try
                {
                    await ChannelAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (ClientException)
                {
                    continue; // still not reachable (or not paired): look again later
                }
                lock (_write)
                {
                    if (_served != StatusOnly)
                    {
                        _probe = null;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            log.WriteLine($"yawble mcp: looking for the app failed ({e.GetType().Name})");
        }
    }

    private async Task DropAsync(IControlChannel channel)
    {
        await _connect.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_channel, channel)) _channel = null;
        }
        finally
        {
            _connect.Release();
        }
        await channel.DisposeAsync().ConfigureAwait(false);
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    /// <summary>One protocol message, one line on stdout.</summary>
    private void Send(JsonObject message)
    {
        var text = message.ToJsonString();
        lock (_write)
        {
            output.Write(text);
            output.Write('\n');
            output.Flush();
        }
    }

    private static void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, child) in o) Walk(child);
                break;
            case JsonArray a:
                foreach (var child in a) Walk(child);
                break;
        }
    }

    private sealed class McpError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
