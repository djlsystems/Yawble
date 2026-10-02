namespace Harness.Contracts;

// STREAMS: a second lane on a worker's connection, beside the run protocol's commands and events.
// What a worker sends a lot of and need not keep - a terminal's bytes, a followed transcript's lines,
// a file's text - crosses as StreamChunks: numbered per stream, never kept in the worker's outbox,
// never acknowledged. What must not be lost - a terminal's end, a read's answer - is still a
// sequenced event, sent after the stream's last chunk.

/// <summary>
/// One piece of a stream, worker to control. <see cref="N"/> rises by one per chunk of
/// <see cref="Stream"/>, from 1, so control can tell when one went missing. <see cref="Bytes"/>
/// carries a terminal's output, <see cref="Text"/> a transcript's line or a file's text;
/// <see cref="End"/> says the stream has no more; <see cref="Gap"/> is a sentence saying what was
/// lost just before this chunk.
/// </summary>
public sealed record StreamChunk(
    string Stream, long N, byte[]? Bytes = null, string? Text = null, bool End = false, string? Gap = null);

/// <summary>What a person typed into a terminal, control to worker. Nothing is kept: input to a worker that is not connected is dropped.</summary>
public sealed record StreamInput(string Stream, byte[] Bytes);

/// <summary>A worker's way to send stream chunks to control.</summary>
public interface IRunStreamSink
{
    /// <summary>Sends <paramref name="chunk"/> now. False when it could not be: the connection is down, and the caller keeps it or drops it.</summary>
    ValueTask<bool> StreamAsync(StreamChunk chunk, CancellationToken ct = default);
}

/// <summary>Control's way to send a person's keystrokes to a worker.</summary>
public interface IRunWorkerInput
{
    /// <summary>Sends <paramref name="input"/> now, outside the command queue. Dropped when the worker is not connected.</summary>
    ValueTask InputAsync(StreamInput input, CancellationToken ct = default);
}
