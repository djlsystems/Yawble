using System.Collections.Concurrent;
using System.Threading.Channels;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// CONTROL'S END OF EVERY STREAM A WORKER SENDS: a terminal's output, a followed transcript, a file
/// read. Whoever asks a worker for a stream opens it here first, under the stream's id, and reads its
/// chunks in order from <see cref="WorkerStream.Reader"/>. Every stream ends with one chunk whose
/// <see cref="StreamChunk.End"/> is set, whatever ended it: the worker's own last chunk, the sequenced
/// event that ends it (<see cref="HandleAsync"/>), a chunk that went missing, its worker being lost, or
/// <see cref="Close"/>.
/// </summary>
/// <remarks>
/// A chunk for a stream nobody opened, or from another worker than the one it was opened on, is
/// dropped. Chunks are numbered per stream: one missing means the connection dropped while the worker
/// sent it, which ends a stream that must be whole (a transcript) and is said in a line on one that
/// carries on (a terminal).
/// </remarks>
public sealed class WorkerStreams
{
    /// <summary>The chunks one stream may hold that its reader has not taken; past it the stream ends.</summary>
    public const int Capacity = 10_000;

    /// <summary>What a stream that lost a chunk on the way says.</summary>
    public const string MissedText = "The worker's connection dropped while it was sending; what it sent is incomplete.";

    /// <summary>What a stream whose reader fell too far behind ends with.</summary>
    public const string BehindText = "This stream fell too far behind its reader and was stopped.";

    private readonly ConcurrentDictionary<string, WorkerStream> _open = new(StringComparer.Ordinal);

    /// <summary>Whether a stream by this id is open now.</summary>
    public bool IsOpen(string stream) => _open.ContainsKey(stream);

    /// <summary>
    /// Opens <paramref name="stream"/> for chunks from <paramref name="worker"/>. <paramref name="whole"/>
    /// says a missing chunk ends it; otherwise the next chunk carries <see cref="MissedText"/> as its gap.
    /// </summary>
    public WorkerStream Open(string stream, IRunWorker worker, bool whole = true)
    {
        var open = new WorkerStream(stream, worker, whole, this);
        if (!_open.TryAdd(stream, open)) throw new InvalidOperationException($"Stream {stream} is already open.");

        _ = worker.Closed.ContinueWith(
            _ => open.Finish(null, worker is IRunWorkerConnection { Lost: { } why } ? why : $"The connection to worker {worker.Id} closed.", lost: true),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return open;
    }

    /// <summary>A chunk from <paramref name="worker"/>. Never blocks: called as the connection reads it.</summary>
    public void Deliver(WorkerId worker, StreamChunk chunk)
    {
        if (_open.TryGetValue(chunk.Stream, out var open) && open.Worker.Id == worker) open.Write(chunk);
    }

    /// <summary>The events that end a stream, in sequence order: they come after the stream's last chunk.</summary>
    public Task HandleAsync(WorkerEnvelope envelope, CancellationToken ct = default)
    {
        if (Ending(envelope.Event) is { } stream && _open.TryGetValue(stream, out var open) && open.Worker.Id == envelope.Worker)
        {
            open.Finish(envelope.Event, null);
        }

        return Task.CompletedTask;
    }

    /// <summary>Ends <paramref name="stream"/> here: what arrives for it after this is dropped.</summary>
    public void Close(string stream)
    {
        if (_open.TryGetValue(stream, out var open)) open.Finish(null, null);
    }

    internal void Forget(WorkerStream stream) => _open.TryRemove(new KeyValuePair<string, WorkerStream>(stream.Id, stream));

    /// <summary>The stream a sequenced event ends, or null for an event that ends none.</summary>
    private static string? Ending(WorkerEvent @event) =>
        @event switch
        {
            TerminalEnded ended => ended.Session,
            AgentFileRead read => ReadAgentFile.StreamOf(read.Request),
            _ => null,
        };
}

/// <summary>One open stream: its chunks in order, how it ended, and why when its worker was lost.</summary>
public sealed class WorkerStream : IDisposable
{
    private readonly Channel<StreamChunk> _chunks =
        Channel.CreateUnbounded<StreamChunk>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly TaskCompletionSource<WorkerEvent?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _whole;
    private readonly WorkerStreams _owner;
    private readonly Lock _gate = new();
    private long _last;
    private bool _finished;

    internal WorkerStream(string id, IRunWorker worker, bool whole, WorkerStreams owner)
    {
        Id = id;
        Worker = worker;
        _whole = whole;
        _owner = owner;
    }

    public string Id { get; }

    /// <summary>The worker the stream is on.</summary>
    public IRunWorker Worker { get; }

    /// <summary>Every chunk, in order; the last has <see cref="StreamChunk.End"/> set.</summary>
    public ChannelReader<StreamChunk> Reader => _chunks.Reader;

    /// <summary>The sequenced event that ended the stream, or null when something else did.</summary>
    public Task<WorkerEvent?> Ended => _ended.Task;

    /// <summary>Why the stream's worker was lost, when that is what ended it.</summary>
    public string? Lost { get; private set; }

    /// <summary>How many chunks arrived with the worker's numbering, not counting the end control adds.</summary>
    public long Received
    {
        get
        {
            lock (_gate) return _last;
        }
    }

    internal void Write(StreamChunk chunk)
    {
        lock (_gate)
        {
            if (_finished || chunk.N <= _last) return;

            if (chunk.N > _last + 1)
            {
                if (_whole)
                {
                    EndLocked(null, WorkerStreams.MissedText);
                    return;
                }

                chunk = chunk with { Gap = chunk.Gap ?? WorkerStreams.MissedText };
            }

            _last = chunk.N;
            if (_chunks.Reader.Count >= WorkerStreams.Capacity)
            {
                EndLocked(null, WorkerStreams.BehindText);
                return;
            }

            _chunks.Writer.TryWrite(chunk);
            if (chunk.End)
            {
                // Still open here, so the event that ends it after its last chunk is still recorded.
                _finished = true;
                _chunks.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// Ends the stream after what it already holds: with <paramref name="ended"/>, the event that ended
    /// it, or with <paramref name="gap"/>, a sentence saying why it ended early. An event that ends a
    /// stream the worker already ended is still recorded as its end.
    /// </summary>
    internal void Finish(WorkerEvent? ended, string? gap, bool lost = false)
    {
        lock (_gate)
        {
            if (lost && !_finished) Lost = gap;
            EndLocked(ended, gap);
        }

        _owner.Forget(this);
    }

    private void EndLocked(WorkerEvent? ended, string? gap)
    {
        _ended.TrySetResult(ended);
        if (_finished) return;

        _finished = true;
        _chunks.Writer.TryWrite(new StreamChunk(Id, -1, End: true, Gap: gap));
        _chunks.Writer.TryComplete();
    }

    public void Dispose() => Finish(null, null);
}
