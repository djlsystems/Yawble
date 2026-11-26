using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Yawble.Core.Ui;

/// <summary>
/// Pushes from the core to the shell: JSON frames with no id, {"event": name, "data": ...}. Each
/// subscriber (one per authenticated IPC connection) gets the events in publish order, through its own queue,
/// so a slow connection never blocks a publisher (a PTY reader or the bus).
///
/// A queue holds at most <see cref="QueueLimit"/> events. A subscriber that far behind loses its oldest waiting
/// events (a warning says so, at most every ten seconds per subscriber) and keeps the newest: the core's memory does
/// not grow with a connection that has stopped reading. A subscriber whose send fails is taken off at once.
/// </summary>
public sealed class AppEvents
{
    /// <summary>How many events may wait for one subscriber.</summary>
    public const int QueueLimit = 2048;

    private readonly Lock _lock = new();
    private readonly List<Channel<JsonObject>> _subscribers = [];

    /// <summary>How many subscribers get events now (tests read it).</summary>
    internal int Subscribers
    {
        get { lock (_lock) return _subscribers.Count; }
    }

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
        long dropped = 0, warnedAt = 0;
        var channel = Channel.CreateBounded<JsonObject>(
            new BoundedChannelOptions(QueueLimit) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
            _ =>
            {
                var n = Interlocked.Increment(ref dropped);
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref warnedAt);
                if (last != 0 && now - last < 10_000) return;
                if (Interlocked.CompareExchange(ref warnedAt, now == 0 ? 1 : now, last) != last) return;
                Logs.Diag.Warning("events", $"a connection is not keeping up with the core's events: {n} of the oldest waiting ones were dropped so far",
                    ("dropped", n), ("limit", QueueLimit));
            });
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
                    // The connection is gone: nothing more is queued for it (Dispose finds it already off).
                    lock (_lock) _subscribers.Remove(channel);
                    channel.Writer.TryComplete();
                    break;
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
