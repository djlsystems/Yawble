using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Yawble.Core.Ui;

/// <summary>
/// Pushes from the core to the shell: JSON frames with no id, {"event": name, "data": ...}. Each
/// subscriber (one per authenticated IPC connection) gets every event in publish order, through its own queue,
/// so a slow connection never blocks a publisher (a PTY reader or the bus).
/// </summary>
public sealed class AppEvents
{
    private readonly Lock _lock = new();
    private readonly List<Channel<JsonObject>> _subscribers = [];

    public void Publish(string name, JsonNode? data)
    {
        Channel<JsonObject>[] targets;
        lock (_lock) targets = [.. _subscribers];
        foreach (var target in targets)
        {
            // Each subscriber gets its own copy: a JsonNode has one parent.
            target.Writer.TryWrite(new JsonObject { ["event"] = name, ["data"] = data?.DeepClone() });
        }
    }

    /// <summary>Delivers events to <paramref name="send"/> one at a time until the returned handle is disposed.</summary>
    public IDisposable Subscribe(Func<JsonObject, Task> send)
    {
        var channel = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
        lock (_lock) _subscribers.Add(channel);
        _ = Task.Run(async () =>
        {
            await foreach (var message in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await send(message).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break; // the connection is gone; Dispose removes us
                }
            }
        });
        return new Subscription(() =>
        {
            lock (_lock) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) dispose();
        }
    }
}
