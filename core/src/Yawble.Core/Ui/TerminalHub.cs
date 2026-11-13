using System.Text;
using System.Text.Json.Nodes;

namespace Yawble.Core.Ui;

/// <summary>What a terminal window shows above its output: the session that owns the channel now, if any.</summary>
public sealed record TerminalSession(
    string Kind,            // "headless" (a member run), "interactive" (Open interactive CLI), "host" (a host pane)
    long? RunId,
    string StartedAt,
    bool InputEnabled,
    string? Command,
    bool Ended = false,
    int? ExitCode = null,
    string? EndReason = null);

/// <summary>Where a session's keyboard input and resizes go (the PTY of a sandbox or of a host pane).</summary>
public interface ITerminalInput
{
    void Write(ReadOnlySpan<byte> data);
    void Resize(int cols, int rows);
    Task StopAsync();
}

/// <summary>
/// The terminal channels: one per member ("member:&lt;id&gt;") and one per host pane
/// ("host:&lt;kind&gt;"). Each keeps a 1 MiB replay buffer, whether or not a window is
/// open, and pushes new output and state as events. A window attaches by reading the replay, then streams.
/// The PTY itself is not here: the launcher (or a fake) writes output in and takes input out.
/// </summary>
public sealed class TerminalHub(AppEvents events)
{
    public const int ReplayBytes = 1 << 20;

    public const string OutputEvent = "terminal.output";
    public const string StateEvent = "terminal.state";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _channels = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public readonly ReplayBuffer Replay = new(ReplayBytes);
        public TerminalSession? Session;
        public ITerminalInput? Input;
        public long Sequence; // bytes ever written: a window drops pushes it already has from the replay
    }

    /// <summary>The pushes this hub publishes on (others with a session on a channel publish theirs here too).</summary>
    public AppEvents Events => events;

    /// <summary>
    /// Raised with the channel on activity: output written to it, or the person's input reaching a live session's PTY
    /// (AgentTestSessions' idle end stamps the time; no allocation per chunk).
    /// </summary>
    public event Action<string>? Activity;

    /// <summary>Raised with the channel once a session has begun on it (its input, and so its stop, is in place).</summary>
    public event Action<string>? Began;

    public static string MemberChannel(long memberId) => $"member:{memberId}";
    public static string HostChannel(string kind) => $"host:{kind}";

    public void Write(string channel, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        long end;
        lock (_lock)
        {
            var e = Get(channel);
            e.Replay.Append(data);
            e.Sequence += data.Length;
            end = e.Sequence;
        }
        Activity?.Invoke(channel);
        events.Publish(OutputEvent, new JsonObject
        {
            ["channel"] = channel,
            ["data"] = Convert.ToBase64String(data),
            ["end"] = end,
        });
    }

    public void Write(string channel, string text) => Write(channel, Encoding.UTF8.GetBytes(text));

    /// <summary>Starts a session on the channel. <paramref name="clearReplay"/>: a host pane starts clean.</summary>
    public void Begin(string channel, TerminalSession session, ITerminalInput? input, bool clearReplay = false)
    {
        lock (_lock)
        {
            var e = Get(channel);
            if (clearReplay) e.Replay.Clear();
            e.Session = session;
            e.Input = input;
        }
        PublishState(channel);
        Began?.Invoke(channel);
    }

    public void End(string channel, int? exitCode, string? reason)
    {
        lock (_lock)
        {
            var e = Get(channel);
            if (e.Session is null || e.Session.Ended) return;
            e.Session = e.Session with { Ended = true, ExitCode = exitCode, EndReason = reason, InputEnabled = false };
            e.Input = null;
        }
        PublishState(channel);
    }

    public TerminalSession? SessionOf(string channel)
    {
        lock (_lock) return _channels.TryGetValue(channel, out var e) ? e.Session : null;
    }

    public bool IsLive(string channel) => SessionOf(channel) is { Ended: false };

    /// <summary>The replay and the session, at one instant; output pushed after it carries a larger "end".</summary>
    public JsonObject Attach(string channel)
    {
        lock (_lock)
        {
            var e = Get(channel);
            return new JsonObject
            {
                ["channel"] = channel,
                ["replay"] = Convert.ToBase64String(e.Replay.ToArray()),
                ["end"] = e.Sequence,
                ["session"] = SessionJson(e.Session),
            };
        }
    }

    public bool Input(string channel, ReadOnlySpan<byte> data)
    {
        ITerminalInput? input;
        lock (_lock) input = _channels.TryGetValue(channel, out var e) && e.Session is { InputEnabled: true } ? e.Input : null;
        if (input is null) return false;
        input.Write(data);
        Activity?.Invoke(channel);
        return true;
    }

    public void Resize(string channel, int cols, int rows)
    {
        ITerminalInput? input;
        lock (_lock) input = _channels.TryGetValue(channel, out var e) ? e.Input : null;
        input?.Resize(Math.Clamp(cols, 2, 1000), Math.Clamp(rows, 2, 500));
    }

    public async Task<bool> StopAsync(string channel)
    {
        ITerminalInput? input;
        lock (_lock) input = _channels.TryGetValue(channel, out var e) && e.Session is { Ended: false } ? e.Input : null;
        if (input is null) return false;
        await input.StopAsync().ConfigureAwait(false);
        return true;
    }

    private void PublishState(string channel)
    {
        TerminalSession? s;
        lock (_lock) s = Get(channel).Session;
        events.Publish(StateEvent, new JsonObject { ["channel"] = channel, ["session"] = SessionJson(s) });
    }

    private static JsonNode? SessionJson(TerminalSession? s) => s is null ? null : new JsonObject
    {
        ["kind"] = s.Kind,
        ["runId"] = s.RunId,
        ["startedAt"] = s.StartedAt,
        ["inputEnabled"] = s.InputEnabled,
        ["command"] = s.Command,
        ["ended"] = s.Ended,
        ["exitCode"] = s.ExitCode,
        ["endReason"] = s.EndReason,
    };

    private Entry Get(string channel)
    {
        if (!_channels.TryGetValue(channel, out var e)) _channels[channel] = e = new Entry();
        return e;
    }
}

/// <summary>A byte ring that keeps the newest <c>capacity</c> bytes (a channel's replay buffer).</summary>
public sealed class ReplayBuffer(int capacity)
{
    private readonly byte[] _buffer = new byte[capacity];
    private int _start;
    private int _count;

    public int Count => _count;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length >= capacity)
        {
            data[^capacity..].CopyTo(_buffer);
            _start = 0;
            _count = capacity;
            return;
        }
        foreach (var b in data)
        {
            var at = (_start + _count) % capacity;
            _buffer[at] = b;
            if (_count < capacity) _count++;
            else _start = (_start + 1) % capacity;
        }
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
    }

    public byte[] ToArray()
    {
        var result = new byte[_count];
        var first = Math.Min(_count, capacity - _start);
        Array.Copy(_buffer, _start, result, 0, first);
        Array.Copy(_buffer, 0, result, first, _count - first);
        return result;
    }
}
