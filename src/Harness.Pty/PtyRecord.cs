using System.Threading.Channels;

namespace Harness.Pty;

/// <summary>
/// One event on a PTY's stream. Chunks and the exit code share an ordered stream, mirroring
/// TerminalEvent, so a client never renders output attributed to the wrong lifecycle state.
/// </summary>
public sealed record PtyEvent(string Kind, byte[]? Chunk, int? ExitCode)
{
    public static PtyEvent ForChunk(byte[] chunk) => new("chunk", chunk, null);

    public static PtyEvent ForExit(int exitCode) => new("exit", null, exitCode);
}

/// <summary>Everything buffered so far, plus the live tail.</summary>
public sealed record PtySubscription(byte[] Replay, Channel<PtyEvent> Live);

/// <summary>
/// The API's mirror of one interactive PTY session's transcript, mirroring TerminalRecord's
/// shape over the same ChunkFanout that solves the snapshot-and-subscribe race in one place.
///
/// PtySessionStore.StartAsync subscribes this to the session's Output before the session can
/// produce anything (see PortaPtySession's replay-on-first-subscribe latch), so the record is
/// complete from birth exactly the same way TerminalRecord is.
/// </summary>
public sealed class PtyRecord(string key, int maxBufferBytes = 1_048_576)
{
    private readonly ChunkFanout<PtyEvent> _fanout = new(maxBufferBytes);
    private readonly Lock _lock = new();
    private bool _exited;
    private long _appended;
    private long _validFrom;

    public string Key { get; } = key;

    public bool Exited
    {
        get
        {
            using (_lock.EnterScope())
            {
                return _exited;
            }
        }
    }

    /// <summary>
    /// Called from the PTY session's Output event, which runs under the engine's _outputLock.
    /// Never blocks: ChunkFanout.Append is non-blocking by construction.
    /// </summary>
    public void Append(byte[] chunk)
    {
        using (_lock.EnterScope())
        {
            _appended += chunk.Length;
            _fanout.Append(chunk, PtyEvent.ForChunk(chunk));
        }
    }

    /// <summary>
    /// Declares everything recorded so far unreplayable, because the child has been resized.
    ///
    /// A terminal transcript is not text, it is bytes with a width baked into them: a TUI wraps its
    /// own lines and positions its cursor absolutely, so output written for 40 columns replayed into
    /// a 165-column terminal is not merely narrow, it is scrambled - overlapping boxes, sentences
    /// broken mid-word, an unreachable prompt. That is what a device replaying another device's
    /// history would get, and no amount of repainting fixes the part that has already scrolled.
    ///
    /// So a resize draws a line: bytes after it are laid out for the size the child now has and are
    /// replayed; bytes before it are dropped from the replay. Scrollback is lost across a size
    /// change, which is the honest price - the alternative is showing it corrupted.
    /// </summary>
    public void Reflowed()
    {
        using (_lock.EnterScope())
        {
            _validFrom = _appended;
        }
    }

    /// <summary>Publishes the exit event, then ends every stream, now and in future.</summary>
    public void Complete(int exitCode)
    {
        using (_lock.EnterScope())
        {
            if (_exited)
            {
                return;
            }

            _exited = true;
        }

        _fanout.Publish(PtyEvent.ForExit(exitCode));
        _fanout.CompleteAll();
    }

    public PtySubscription Subscribe()
    {
        using (_lock.EnterScope())
        {
            var subscription = _fanout.Subscribe();

            // The snapshot is the TAIL of the transcript: the bounded buffer may already have dropped
            // older bytes, so it covers [_appended - length, _appended). Anything before _validFrom
            // was written at a different width and is dropped from the replay - see Reflowed.
            var skip = _validFrom - (_appended - subscription.Replay.Length);
            var replay = skip <= 0
                ? subscription.Replay
                : subscription.Replay[(int)Math.Min(skip, subscription.Replay.Length)..];

            return new PtySubscription(replay, subscription.Live);
        }
    }

    public void Unsubscribe(Channel<PtyEvent> channel) => _fanout.Unsubscribe(channel);
}
