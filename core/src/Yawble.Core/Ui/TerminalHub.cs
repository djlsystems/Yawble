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
/// ("host:&lt;kind&gt;"). Each keeps the newest output, up to 1 MiB, as its replay, whether or not a window is
/// open, and pushes new output and state as events. A window attaches by reading the replay, then streams.
/// The PTY itself is not here: the launcher (or a fake) writes output in and takes input out.
///
/// Memory: a replay takes what its channel has printed, not 1 MiB from the start, and a channel nobody wrote to
/// takes none. A live terminal always keeps its replay (a window that attaches late reads it). The replays of
/// terminals that have ended are kept, so a window opened afterwards still shows what the run printed, until together
/// they pass <see cref="EndedReplayBudget"/>: then the ones ended, or last attached to, longest ago are freed first.
/// A freed channel keeps its byte count, so a window that is still open goes on from where it was.
/// </summary>
public sealed class TerminalHub(AppEvents events)
{
    public const int ReplayBytes = 1 << 20;

    /// <summary>How many bytes the replays of ended terminals may hold together (64 MiB: 64 full replays).</summary>
    public long EndedReplayBudget { get; init; } = 64L << 20;

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
        public long Used;     // when the channel last ended or was attached to (a count, larger is later)

        public bool Live => Session is { Ended: false };
    }

    private long _uses;

    /// <summary>The bytes all replays hold now (tests read it).</summary>
    internal long ReplayBytesHeld
    {
        get { lock (_lock) return _channels.Values.Sum(e => (long)e.Replay.Held); }
    }

    /// <summary>Under the lock: frees the replays of ended terminals, least recently used first, while they hold more than the budget.</summary>
    private void KeepEndedWithinBudget()
    {
        var ended = _channels.Values.Where(e => !e.Live && e.Replay.Held > 0).ToList();
        var held = ended.Sum(e => (long)e.Replay.Held);
        if (held <= EndedReplayBudget) return;
        foreach (var e in ended.OrderBy(e => e.Used))
        {
            held -= e.Replay.Held;
            e.Replay.Free();
            if (held <= EndedReplayBudget) return;
        }
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
            e.Used = ++_uses;
            KeepEndedWithinBudget();
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
            e.Used = ++_uses; // looked at: among the ended, it is freed last
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

/// <summary>
/// A byte ring that keeps the newest <c>capacity</c> bytes (a channel's replay buffer). It holds no memory until the
/// first byte and grows with what is written, up to the capacity.
/// </summary>
public sealed class ReplayBuffer(int capacity)
{
    private const int FirstSize = 4096;

    // Until the array has grown to the capacity the bytes lie from 0 (nothing has wrapped): _start is 0.
    private byte[] _buffer = [];
    private int _start;
    private int _count;

    public int Count => _count;

    /// <summary>The bytes of memory the buffer holds now (0 before the first write and after <see cref="Free"/>).</summary>
    public int Held => _buffer.Length;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        if (data.Length >= capacity)
        {
            if (_buffer.Length < capacity) _buffer = new byte[capacity];
            data[^capacity..].CopyTo(_buffer);
            _start = 0;
            _count = capacity;
            return;
        }
        if (_count + data.Length > _buffer.Length && _buffer.Length < capacity)
            Array.Resize(ref _buffer, (int)Math.Min(capacity, Math.Max(Math.Max(_buffer.Length * 2L, _count + data.Length), FirstSize)));
        var size = _buffer.Length;
        var at = (_start + _count) % size;
        var first = Math.Min(data.Length, size - at);
        data[..first].CopyTo(_buffer.AsSpan(at));
        data[first..].CopyTo(_buffer);
        var total = _count + data.Length;
        if (total > size)
        {
            _start = (_start + (total - size)) % size; // the oldest bytes were written over
            _count = size;
        }
        else
        {
            _count = total;
        }
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
    }

    /// <summary>Forgets the bytes and gives their memory back.</summary>
    public void Free()
    {
        _buffer = [];
        Clear();
    }

    public byte[] ToArray()
    {
        var result = new byte[_count];
        if (_count == 0) return result;
        var first = Math.Min(_count, _buffer.Length - _start);
        Array.Copy(_buffer, _start, result, 0, first);
        Array.Copy(_buffer, 0, result, first, _count - first);
        return result;
    }
}
