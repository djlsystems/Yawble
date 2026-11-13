using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Yawble.Core.Integrations;

/// <summary>
/// Pushes to the app's window on this computer only. Same shape as <see cref="Ui.AppEvents"/>, but a separate type so
/// it cannot be handed to the Cloud Link by mistake: the IPC server subscribes local connections to it, the link never.
/// </summary>
public sealed class HostEvents
{
    private readonly Ui.AppEvents _inner = new();
    public void Publish(string name, JsonNode? data) => _inner.Publish(name, data);
    public IDisposable Subscribe(Func<JsonObject, Task> send) => _inner.Subscribe(send);
}

/// <summary>One step of website browsing, done by the app's window on this computer (Ops: open, read, screenshot, click, fill, submit, check, signIn, signInClose, forget).</summary>
/// <remarks>
/// <see cref="Deadline"/>: when the core stops waiting (Unix ms). The window skips an op that would start after it, and keeps each step
/// of an op under the time left, so a queued call never runs for an agent that has already been told it failed.
/// </remarks>
public sealed record WebsiteRequest(string Op, string Partition, string PageKey, string SiteUrl, JsonObject Args, long? Deadline = null);

public sealed record WebsiteReply(bool Ok, string? Error, string? Url, string? Title, string? Text, byte[]? Png, string? StoppedAt);

/// <summary>Its message is safe to show an agent.</summary>
public sealed class WebsiteException(string message) : Exception(message);

public interface IWebsiteHost
{
    Task<WebsiteReply> RunAsync(WebsiteRequest request, CancellationToken ct);
    bool Connected { get; }
}

/// <summary>Asks the app's window (over a local IPC connection that said hostBridge.hello) and waits for hostBridge.reply.</summary>
public sealed class ShellWebsiteHost(HostEvents events, TimeSpan timeout, TimeProvider? clock = null) : IWebsiteHost
{
    public const string RequestEvent = "website.request";
    public static readonly TimeSpan WebsiteTimeout = TimeSpan.FromSeconds(45);
    public const int MaxPngBytes = 4 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<WebsiteReply>> _pending = new();
    private int _bridges;

    public bool Connected => Volatile.Read(ref _bridges) > 0;

    /// <summary>A bridge connection said hello; dispose the handle when that connection closes.</summary>
    public IDisposable BridgeConnected()
    {
        Interlocked.Increment(ref _bridges);
        return new Handle(() =>
        {
            if (Interlocked.Decrement(ref _bridges) > 0) return;
            // the window is gone: nobody will answer, so do not make the agent wait out the timeout
            foreach (var waiter in _pending.Values)
                waiter.TrySetException(new WebsiteException("website actions need the app to be open on this computer"));
        });
    }

    public async Task<WebsiteReply> RunAsync(WebsiteRequest request, CancellationToken ct)
    {
        if (!Connected) throw new WebsiteException("website actions need the app to be open on this computer");
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var waiter = new TaskCompletionSource<WebsiteReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            events.Publish(RequestEvent, new JsonObject
            {
                ["id"] = id, ["op"] = request.Op, ["partition"] = request.Partition, ["pageKey"] = request.PageKey,
                ["siteUrl"] = request.SiteUrl, ["args"] = request.Args.DeepClone(),
                ["deadline"] = request.Deadline ?? ((clock ?? TimeProvider.System).GetUtcNow() + timeout).ToUnixTimeMilliseconds(),
            });
            try
            {
                return await waiter.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new WebsiteException("the app's window did not answer in time; try again");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>The window's answer. An id nobody waits for (late, or made up) is ignored.</summary>
    public void Reply(string id, JsonObject result)
    {
        if (!_pending.TryGetValue(id, out var waiter)) return;
        waiter.TrySetResult(Parse(result));
    }

    private static WebsiteReply Parse(JsonObject r)
    {
        static string? S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var ok = r["ok"] is JsonValue ov && ov.TryGetValue<bool>(out var b) && b;
        byte[]? png = null;
        if (S(r, "png") is { Length: > 0 } text)
        {
            try { png = Convert.FromBase64String(text); }
            catch (FormatException) { return new WebsiteReply(false, "the screenshot was not valid", null, null, null, null, null); }
            if (png.Length > MaxPngBytes) return new WebsiteReply(false, "the screenshot is too large", null, null, null, null, null);
        }
        return new WebsiteReply(ok, S(r, "error"), S(r, "url"), S(r, "title"), S(r, "text"), png, S(r, "stoppedAt"));
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) dispose();
        }
    }
}
