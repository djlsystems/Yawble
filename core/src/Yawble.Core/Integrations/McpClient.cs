using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Yawble.Core.Integrations;

/// <summary>
/// A client of one local MCP server over stdio (newline-delimited JSON-RPC): started on this computer when first needed,
/// initialized, then asked for its tools and to call them. Kept running between calls; started again if it exits.
/// The server is the person's own program; its env entries (plain values, and secrets the person gave) are set on its process only.
/// </summary>
public sealed class McpClient(string command, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env = null, Action<string>? log = null) : IMcpClient
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _waiting = new();
    private Process? _process;
    private long _nextId;
    private readonly StringBuilder _stderr = new();

    /// <summary>The server's tools as it lists them (name, description, inputSchema).</summary>
    public async Task<JsonArray> ListToolsAsync(CancellationToken ct)
    {
        var result = await RequestAsync("tools/list", new JsonObject(), ct);
        return result["tools"] as JsonArray ?? [];
    }

    /// <summary>Calls a tool: its content as text, and whether the server said it failed.</summary>
    public async Task<(string Text, bool IsError)> CallToolAsync(string tool, JsonObject arguments, CancellationToken ct)
    {
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() }, ct);
        var text = string.Join("\n", (result["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => c["type"]?.GetValue<string>() == "text" ? c["text"]?.GetValue<string>() ?? "" : $"[{c["type"]?.GetValue<string>() ?? "content"}]"));
        return (text, result["isError"]?.GetValue<bool>() == true);
    }

    private async Task<JsonObject> RequestAsync(string method, JsonObject p, CancellationToken ct)
    {
        await EnsureStartedAsync(ct);
        return await SendAsync(method, p, ct);
    }

    private async Task<JsonObject> SendAsync(string method, JsonObject p, CancellationToken ct)
    {
        var process = _process ?? throw new InvalidOperationException("the MCP server is not running");
        var id = Interlocked.Increment(ref _nextId);
        var waiting = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = waiting;
        var line = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p }.ToJsonString();
        lock (process) { process.StandardInput.WriteLine(line); process.StandardInput.Flush(); }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(CallTimeout);
        try
        {
            var reply = await waiting.Task.WaitAsync(limit.Token);
            if (reply["error"] is JsonObject err) throw new McpException(CliRunner.Redact(err["message"]?.GetValue<string>() ?? "the server refused the call"));
            return reply["result"] as JsonObject ?? [];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException($"the server did not answer {method} within {CallTimeout.TotalMinutes:0} minutes");
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false }) return;
        await _start.WaitAsync(ct);
        try
        {
            if (_process is { HasExited: false }) return;
            var psi = new ProcessStartInfo(CliRunner.Find(command) ?? command)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } l) lock (_stderr) { if (_stderr.Length < 8000) _stderr.AppendLine(l); } };
            process.Exited += (_, _) =>
            {
                foreach (var w in _waiting.Values) w.TrySetException(new McpException("the MCP server exited: " + LastError()));
            };
            try { process.Start(); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new McpException($"could not start {command}: {e.Message}");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            var init = await SendAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "yawble", ["version"] = CoreInfo.Version },
            }, ct);
            lock (process)
            {
                process.StandardInput.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString());
                process.StandardInput.Flush();
            }
            log?.Invoke($"MCP server {command}: started ({init["serverInfo"]?["name"]?.GetValue<string>() ?? "?"})");
        }
        finally
        {
            _start.Release();
        }
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        JsonObject? message;
        try { message = JsonNode.Parse(line) as JsonObject; }
        catch (System.Text.Json.JsonException) { return; } // a server writing logs to stdout: not ours
        if (message?["id"] is JsonValue idv && idv.TryGetValue<long>(out var id) && message["method"] is null && _waiting.TryGetValue(id, out var w))
            w.TrySetResult(message);
    }

    public string LastError()
    {
        lock (_stderr) return CliRunner.Redact(_stderr.ToString()).Trim() is { Length: > 0 } s ? (s.Length > 600 ? s[^600..] : s) : "no message";
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false } p)
        {
            try { p.StandardInput.Close(); } catch (IOException) { }
            try { await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token); } catch (OperationCanceledException) { }
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        _process?.Dispose();
    }
}

public class McpException(string message) : Exception(message);
