using System.ComponentModel;

namespace Harness.Host;

/// <summary>
/// How much a session's terminal may print in one minute and still be idle: a cursor blink, a
/// spinner frame or a status line ticking at a prompt. A minute whose output EXCEEDS it is activity.
/// </summary>
/// <param name="BytesPerMinute">The floor, in bytes printed within one minute of the session's life.</param>
/// <param name="Source"><c>declared</c> when the preset the session launched with declares one;
/// <c>default</c> when it declares none and the platform's own is used. A default is never a measurement.</param>
/// <param name="MeasuredWith">What the declared floor was measured with, as the preset states it; null for the default.</param>
public sealed record OutputFloor(long BytesPerMinute, string Source, string? MeasuredWith)
{
    public const string Declared = "declared";

    public const string Default = "default";

    /// <summary>The platform's floor for a preset that declares none: a stated default, not a measurement.</summary>
    public const long DefaultBytesPerMinute = 2048;

    public static OutputFloor PlatformDefault { get; } = new(DefaultBytesPerMinute, Default, null);

    /// <summary>The floor <paramref name="preset"/> declares, or the platform default when it declares none.</summary>
    public static OutputFloor Of(AgentDefinition? preset) =>
        preset?.IdleOutput is { BytesPerMinute: > 0 } declared
            ? new OutputFloor(declared.BytesPerMinute, Declared, declared.MeasuredWith)
            : PlatformDefault;
}

/// <summary>
/// What a preset prints at an idle prompt, measured, and so the floor a session of it must pass for
/// its output to count as activity. Declared in the catalog the way the launch check is, never as a
/// name in code.
/// </summary>
public sealed record IdleOutputFloor(
    [property: Description(
        "The floor, in bytes per minute: set above the most this CLI printed in any one minute while it "
        + "sat at an idle prompt with nobody attached. A minute in which a session prints more than this "
        + "counts as activity.")]
    long BytesPerMinute,
    [property: Description(
        "What was measured: the CLI's version, how long it was watched idle, and the most it printed in "
        + "a minute - for example \"1.2.3, 15 minutes idle, at most 900 bytes a minute\".")]
    string MeasuredWith);

/// <summary>
/// WHAT ONE CONCIERGE SESSION HAS DONE, as control sees it: terminal output past its floor, a
/// viewer's keystrokes and attaches, and platform calls under the session's own credential -
/// including one still in flight. Read by the reaper, the session list and the doctor.
/// </summary>
/// <remarks>
/// <para>
/// OUTPUT IS COUNTED IN ONE-MINUTE BUCKETS aligned to the session's start. A bucket is activity once
/// its total EXCEEDS the floor; from the chunk that crosses it on, each chunk in that bucket stamps
/// the time it arrived. A bucket that never exceeds the floor stamps nothing, so output spread thinly
/// across a minute edge is two quiet minutes, not one busy one. The last
/// <see cref="MinutesKept"/> totals are kept so a floor can be measured.
/// </para>
/// <para>
/// One clock, the store's, for every stamp: the reap rule compares these with
/// <see cref="Harness.Pty.PtyAttachment.IdleSince"/>, and two clocks would make it compare unlike times.
/// </para>
/// </remarks>
public sealed class ConciergeActivity
{
    public const string Started = "started";
    public const string Output = "output";
    public const string Typed = "typed";
    public const string Attached = "attached";
    public const string Call = "call";

    /// <summary>How many minutes of output totals are kept.</summary>
    public const int MinutesKept = 60;

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly long[] _bytes = new long[MinutesKept];
    private readonly long[] _minuteOf = new long[MinutesKept];
    private DateTimeOffset _lastAt;
    private string _lastKind = Started;
    private int _inFlight;
    private bool _viewed;

    public ConciergeActivity(OutputFloor floor, TimeProvider clock)
    {
        _clock = clock;
        Floor = floor;
        StartedAt = clock.GetUtcNow();
        _lastAt = StartedAt;
        Array.Fill(_minuteOf, -1);
    }

    public DateTimeOffset StartedAt { get; }

    public OutputFloor Floor { get; }

    /// <summary>A chunk of the terminal's output.</summary>
    public void Printed(byte[] chunk)
    {
        if (chunk.Length == 0) return;

        var now = _clock.GetUtcNow();
        var minute = MinuteOf(now);
        var slot = (int)(minute % MinutesKept);

        lock (_gate)
        {
            // A slot holding an older minute is cleared on time, never summed into this one.
            if (_minuteOf[slot] != minute)
            {
                _minuteOf[slot] = minute;
                _bytes[slot] = 0;
            }

            _bytes[slot] += chunk.Length;
            if (_bytes[slot] > Floor.BytesPerMinute) StampLocked(now, Output);
        }
    }

    /// <summary>A viewer typed into the terminal.</summary>
    public void Keystrokes() => Stamp(Typed);

    /// <summary>A viewer took the terminal.</summary>
    public void Attach()
    {
        lock (_gate)
        {
            _viewed = true;
            StampLocked(_clock.GetUtcNow(), Attached);
        }
    }

    /// <summary>Whether a viewer has ever had the terminal.</summary>
    public bool EverViewed
    {
        get { lock (_gate) return _viewed; }
    }

    /// <summary>
    /// A platform call under the session's credential has begun. It is activity until the returned
    /// handle is disposed, which stamps its end; dispose it however the call ends.
    /// </summary>
    public IDisposable CallBegan()
    {
        lock (_gate)
        {
            _inFlight++;
            StampLocked(_clock.GetUtcNow(), Call);
        }

        return new CallHandle(this);
    }

    /// <summary>How many platform calls under the session's credential are in flight.</summary>
    public int CallsInFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    /// <summary>When the session last did something, and what; <paramref name="now"/> while a call is in flight.</summary>
    public (DateTimeOffset At, string Kind) Last(DateTimeOffset now)
    {
        lock (_gate) return _inFlight > 0 ? (now, Call) : (_lastAt, _lastKind);
    }

    /// <summary>Bytes printed in each of the last <see cref="MinutesKept"/> minutes of the session, oldest first, the current minute last.</summary>
    public IReadOnlyList<long> PerMinute(DateTimeOffset now)
    {
        var current = MinuteOf(now);
        var first = Math.Max(0, current - MinutesKept + 1);
        var totals = new List<long>();

        lock (_gate)
        {
            for (var minute = first; minute <= current; minute++)
            {
                var slot = (int)(minute % MinutesKept);
                totals.Add(_minuteOf[slot] == minute ? _bytes[slot] : 0);
            }
        }

        return totals;
    }

    private long MinuteOf(DateTimeOffset at) => Math.Max(0, (at - StartedAt).Ticks / Minute.Ticks);

    private void Stamp(string kind)
    {
        lock (_gate) StampLocked(_clock.GetUtcNow(), kind);
    }

    private void StampLocked(DateTimeOffset at, string kind)
    {
        if (at < _lastAt) return;

        _lastAt = at;
        _lastKind = kind;
    }

    private void CallEnded()
    {
        lock (_gate)
        {
            if (_inFlight > 0) _inFlight--;
            StampLocked(_clock.GetUtcNow(), Call);
        }
    }

    private sealed class CallHandle(ConciergeActivity owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.CallEnded();
        }
    }
}
