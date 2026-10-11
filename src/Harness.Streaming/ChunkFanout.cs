using System.Threading.Channels;

namespace Harness.Streaming;

/// <summary>
/// A replay of everything buffered so far, plus the live tail.
///
/// Carries the whole <see cref="Channel{T}"/>, not just its reader, so a caller that stops
/// reading (e.g. an aborted SSE connection) can hand it back to
/// <see cref="ChunkFanout{TEvent}.Unsubscribe"/> to be deregistered by reference.
/// </summary>
public sealed record Subscription<TEvent>(byte[] Replay, Channel<TEvent> Live);

/// <summary>
/// A bounded byte transcript plus a set of live subscribers, shared by RunRecord (per run) and
/// TerminalRecord (per terminal).
///
/// This type exists so the snapshot-and-register race is solved in exactly one place. Getting
/// it wrong is invisible until it corrupts somebody's transcript, which is precisely the kind
/// of logic that must not be copy-pasted.
///
/// TEvent is what subscribers receive. It is separate from the buffered bytes because a
/// terminal also publishes status changes, which belong on the same ordered stream as its
/// output but must not enter the replay buffer.
/// </summary>
public sealed class ChunkFanout<TEvent>(int maxBufferBytes = 1_048_576)
{
    private readonly TranscriptBuffer _buffer = new(maxBufferBytes);
    private readonly List<Channel<TEvent>> _subscribers = [];
    private readonly Lock _lock = new();
    private long _totalAppended;
    private bool _completed;

    /// <summary>True once the bounded buffer has dropped bytes a later subscriber will never see.</summary>
    public bool Truncated
    {
        get
        {
            using (_lock.EnterScope())
            {
                return _totalAppended > _buffer.ByteCount;
            }
        }
    }

    /// <summary>
    /// Buffers <paramref name="bytes"/> for future replay and fans <paramref name="live"/> out to
    /// current subscribers.
    ///
    /// Called from TerminalSession's ChunkAppended, which raises while holding its own _emitLock.
    /// Every write here is non-blocking (unbounded channels, TryWrite) precisely because blocking
    /// in that callback would stall every terminal in the process.
    /// </summary>
    public void Append(byte[] bytes, TEvent live)
    {
        using (_lock.EnterScope())
        {
            _totalAppended += bytes.Length;
            _buffer.Append(bytes);
            Fan(live);
        }
    }

    /// <summary>Fans out without buffering — for events that are not transcript bytes.</summary>
    public void Publish(TEvent live)
    {
        using (_lock.EnterScope())
        {
            Fan(live);
        }
    }

    /// <summary>
    /// Snapshot and registration happen under ONE lock, so an event appended concurrently can
    /// neither be missed by both the replay and the live tail, nor delivered twice.
    /// </summary>
    public Subscription<TEvent> Subscribe()
    {
        using (_lock.EnterScope())
        {
            // AllowSynchronousContinuations defaults to false, but is set explicitly here: it is
            // what makes TryWrite/TryComplete safe to call under _lock. If a reader continuation
            // ran inline, it would execute arbitrary consumer code while holding this lock and
            // (via Append) the agent runtime's emit lock.
            var channel = Channel.CreateUnbounded<TEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                AllowSynchronousContinuations = false,
            });

            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel);
            }

            return new Subscription<TEvent>(_buffer.Snapshot(), channel);
        }
    }

    /// <summary>
    /// Deregisters one subscriber and completes its writer, so a disconnected client stops being
    /// a target of Append's TryWrite calls instead of accumulating events nobody reads. Safe
    /// after CompleteAll, and safe to call more than once for the same channel.
    /// </summary>
    public void Unsubscribe(Channel<TEvent> channel)
    {
        using (_lock.EnterScope())
        {
            _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    /// <summary>Ends every current stream, and every future one immediately on subscribe.</summary>
    public void CompleteAll()
    {
        using (_lock.EnterScope())
        {
            _completed = true;

            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    /// <summary>Caller must hold the lock.</summary>
    private void Fan(TEvent live)
    {
        foreach (var subscriber in _subscribers)
        {
            subscriber.Writer.TryWrite(live);
        }
    }
}
