using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harness.Host;

/// <summary>
/// THE CHROME DEVTOOLS PROTOCOL OVER CHROMIUM'S OWN PIPE (<c>--remote-debugging-pipe</c>): messages
/// are JSON, each ended by a NUL byte, written to the browser's fd 3 and read from its fd 4. No port is
/// opened, so nothing else on the worker - an agent least of all - can reach the browser and read its
/// cookies. Commands are answered by id; events go to <see cref="Event"/>, with the flat session id
/// they came from (null for the browser's own).
/// </summary>
public sealed class CdpPipe : IAsyncDisposable
{
    /// <summary>The longest one message may be; a screencast frame is the largest thing Chromium sends.</summary>
    public const int MaxMessage = 32 * 1024 * 1024;

    private readonly Stream _write;
    private readonly Stream _read;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _waiting = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _id;

    public CdpPipe(Stream write, Stream read)
    {
        _write = write;
        _read = read;
        _ = Task.Run(ReadAsync);
    }

    /// <summary>One event: its method, its params, and the session it came from (null for the browser).</summary>
    public event Action<string, JsonElement, string?>? Event;

    /// <summary>Completes when the pipe closed: Chromium exited, or this was disposed.</summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// Sends <paramref name="method"/> and waits for its answer's <c>result</c>. Throws
    /// <see cref="CdpException"/> with Chromium's message when it answers an error, and
    /// <see cref="IOException"/> when the pipe closed first.
    /// </summary>
    public async Task<JsonElement> SendAsync(string method, object? parameters = null, string? session = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _id);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = answer;

        var message = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = JsonSerializer.SerializeToNode(parameters);
        if (session is not null) message["sessionId"] = session;

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _writeGate.WaitAsync(ct);
        try
        {
            if (_closed.Task.IsCompleted) throw new IOException("The browser's pipe is closed.");
            await _write.WriteAsync(bytes, ct);
            _write.WriteByte(0);
            await _write.FlushAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not IOException)
        {
            _waiting.TryRemove(id, out _);
            throw new IOException("The browser's pipe is closed.", exception);
        }
        catch
        {
            _waiting.TryRemove(id, out _);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }

        using var registration = ct.Register(() => answer.TrySetCanceled(ct));
        return await answer.Task;
    }

    /// <summary>Sends <paramref name="method"/> and does not wait; an error is dropped (the browser may be going).</summary>
    public void Post(string method, object? parameters = null, string? session = null) =>
        _ = SendAsync(method, parameters, session).ContinueWith(
            t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await _read.ReadAsync(buffer, _stop.Token);
                if (read == 0) break;

                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != 0) continue;

                    message.Write(buffer, start, i - start);
                    Dispatch(message.ToArray());
                    message.SetLength(0);
                    start = i + 1;
                }

                message.Write(buffer, start, read - start);
                if (message.Length > MaxMessage) break;
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _closed.TrySetResult();
            foreach (var waiting in _waiting.Values) waiting.TrySetException(new IOException("The browser's pipe closed."));
            _waiting.Clear();
        }
    }

    private void Dispatch(byte[] text)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        var session = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
        {
            if (!_waiting.TryRemove(id, out var answer)) return;

            if (root.TryGetProperty("error", out var error))
            {
                // Chromium's own sentence about the call, never page content.
                answer.TrySetException(new CdpException(error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : ""));
            }
            else
            {
                answer.TrySetResult(root.TryGetProperty("result", out var result) ? result : default);
            }

            return;
        }

        if (root.TryGetProperty("method", out var method))
        {
            try
            {
                Event?.Invoke(method.GetString() ?? "", root.TryGetProperty("params", out var p) ? p : default, session);
            }
            catch (Exception)
            {
                // A handler's failure is its own; the pipe keeps reading.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { _write.Dispose(); } catch (IOException) { }
        try { _read.Dispose(); } catch (IOException) { }
        _closed.TrySetResult();
    }
}

/// <summary>Chromium answered a command with an error.</summary>
public sealed class CdpException(string message) : Exception(message);
