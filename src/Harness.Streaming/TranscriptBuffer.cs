namespace Harness.Streaming;

/// <summary>
/// The append-only source of truth for one terminal's history (or, via <c>RunRecord</c>, for one
/// run's own bounded slice of it), so a late subscriber can be replayed from the beginning before
/// joining the live tail. In Harness this read path is live: <c>RunRecord.Subscribe</c> takes a
/// <see cref="Snapshot"/> as the SSE replay and then streams further appends, so a client that
/// connects mid-run or after it finished still sees the whole transcript.
///
/// Bounded so a long session cannot grow without limit. Trimming lands on a line boundary,
/// because replaying a transcript that starts mid-escape-sequence would corrupt the first screen.
/// Thread-safe: the process pumps append while a subscriber snapshots.
/// </summary>
public sealed class TranscriptBuffer
{
    public const int DefaultMaxBytes = 1_048_576;

    private readonly Lock _lock = new();
    private readonly List<byte> _bytes;
    private readonly int _maxBytes;

    public TranscriptBuffer(int maxBytes = DefaultMaxBytes)
    {
        _maxBytes = Math.Max(1, maxBytes);
        _bytes = new List<byte>(Math.Min(_maxBytes, 8192));
    }

    public int ByteCount
    {
        get
        {
            using (_lock.EnterScope())
            {
                return _bytes.Count;
            }
        }
    }

    public void Append(ReadOnlySpan<byte> chunk)
    {
        using (_lock.EnterScope())
        {
            // Bulk copy, not element-by-element: this runs twice per chunk (session transcript
            // plus per-run buffer) while TerminalSession._emitLock is held, so a 4 KiB chunk
            // otherwise cost 8192 individual list adds on the path that serialises every
            // terminal.
            _bytes.AddRange(chunk);

            TrimIfNeeded();
        }
    }

    /// <summary>A copy, so the caller can hand it across threads without tearing.</summary>
    public byte[] Snapshot()
    {
        using (_lock.EnterScope())
        {
            return _bytes.ToArray();
        }
    }

    /// <summary>Caller must hold the lock.</summary>
    private void TrimIfNeeded()
    {
        if (_bytes.Count <= _maxBytes)
        {
            return;
        }

        var excess = _bytes.Count - _maxBytes;

        // Advance past the excess to the next newline so the surviving text starts at a line
        // start. If there is no newline in the remainder, drop the excess as-is rather than
        // the whole buffer - a newline-free stream (a giant JSON blob, a \r-only progress
        // bar) must still retain its tail, not be emptied on every trim.
        var cut = excess;
        while (cut < _bytes.Count && _bytes[cut] != (byte)'\n')
        {
            cut++;
        }

        if (cut < _bytes.Count)
        {
            cut++;   // consume the newline itself
        }
        else
        {
            cut = excess;
        }

        _bytes.RemoveRange(0, Math.Min(cut, _bytes.Count));
    }
}
