using System.Collections;
using System.Collections.Concurrent;
using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE AGENT'S FILES, READ ON THIS WORKER AS THE AGENT: a running member's transcript followed line by
/// line for its live view, and a finished run's file read whole. Each line or piece of text is a
/// stream chunk; a followed transcript's stream ends with its own last chunk, a read with a sequenced
/// <see cref="AgentFileRead"/> saying what it found and how many chunks it sent.
/// </summary>
/// <remarks>
/// A file read for a person is redacted here, before it leaves, of the set control sent and of every
/// credential variable THIS worker's environment holds: a key only the worker had is not in control's
/// set after control restarts, and it is still never served.
/// </remarks>
public sealed class WorkerTranscripts(
    IRunStreamSink sink,
    AgentLaunchUser? runAs,
    Func<WorkerEvent, Task> publish,
    Func<RunId, CancellationToken> runEnded,
    Func<IDictionary<string, string?>>? environment = null,
    ILogger? log = null,
    TimeProvider? clock = null)
{
    /// <summary>The most text one chunk of a read carries.</summary>
    public const int ChunkChars = 256 * 1024;

    /// <summary>How long a chunk that cannot be sent is tried again before a read gives up on it.</summary>
    public static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (CancellationTokenSource Stop, CancellationTokenSource Ended)> _follows = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The transcripts being followed now.</summary>
    public IReadOnlyCollection<string> Following => [.. _follows.Keys];

    /// <summary>Starts following a transcript on its own task.</summary>
    public void Follow(FollowTranscript follow)
    {
        var stop = new CancellationTokenSource();

        // The run ends here when this worker's run ends, or when control says it saw it end.
        var ended = CancellationTokenSource.CreateLinkedTokenSource(runEnded(follow.Run));
        if (!_follows.TryAdd(follow.Stream, (stop, ended)))
        {
            stop.Dispose();
            ended.Dispose();
            throw new InvalidOperationException($"Stream {follow.Stream} is already open.");
        }

        _ = Task.Run(() => FollowAsync(follow, stop, ended), CancellationToken.None);
    }

    /// <summary>Stops following: the watcher left (now), or the run ended (after its last lines).</summary>
    public void Stop(string stream, bool runEnded = false)
    {
        if (!_follows.TryGetValue(stream, out var follow)) return;

        if (runEnded) follow.Ended.Cancel();
        else follow.Stop.Cancel();
    }

    /// <summary>Stops every follow: the worker is stopping.</summary>
    public void StopAll()
    {
        foreach (var follow in _follows.Values) follow.Stop.Cancel();
    }

    /// <summary>Reads a file on its own task; the answer is an event.</summary>
    public void Read(ReadAgentFile read) => _ = Task.Run(() => ReadAsync(read), CancellationToken.None);

    private async Task FollowAsync(FollowTranscript follow, CancellationTokenSource stop, CancellationTokenSource ended)
    {
        long n = 0;
        string? refusal = LiveTranscriptReader.Refusal(runAs);
        try
        {
            if (refusal is null)
            {
                // A chunk with no text first: the follow began, and control starts the watcher's response.
                if (!await SendAsync(new StreamChunk(follow.Stream, ++n), stop.Token)) return;

                await foreach (var line in LiveTranscriptReader.LinesAsync(follow.Path, runAs, ended.Token, stop.Token))
                {
                    if (!await SendAsync(new StreamChunk(follow.Stream, ++n, Text: line), stop.Token)) return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The watcher left: nothing more to say to nobody.
            return;
        }
        catch (Exception exception)
        {
            log?.LogWarning("Following {Path} failed: {Message}", follow.Path, exception.Message);
            refusal = $"The transcript could not be followed on this worker: {exception.Message}";
        }
        finally
        {
            _follows.TryRemove(follow.Stream, out _);
        }

        // The end: a refusal (Text null, the sentence as its gap) or the run's end.
        await SendAsync(new StreamChunk(follow.Stream, ++n, End: true, Gap: refusal), stop.Token);
        stop.Dispose();
        ended.Dispose();
    }

    private async Task ReadAsync(ReadAgentFile read)
    {
        var stream = ReadAgentFile.StreamOf(read.Request);
        AgentFileRead answer;

        try
        {
            if (read.AsTranscript && LiveTranscriptReader.Refusal(runAs) is { } refusal)
            {
                answer = new AgentFileRead(read.Request, AgentFileRead.Refused, refusal, 0);
            }
            else
            {
                AgentFiles.Read found;
                try
                {
                    found = await AgentFiles.ReadAllAsync(read.Path, runAs, CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    found = new AgentFiles.Read(null, exception.Message);
                }

                if (found.Unreadable is { } why)
                {
                    answer = new AgentFileRead(read.Request, AgentFileRead.Unreadable, why, 0);
                }
                else if (found.Text is not { } text)
                {
                    answer = new AgentFileRead(read.Request, AgentFileRead.Gone, null, 0);
                }
                else
                {
                    if (read.Redact) text = Redaction(read).Apply(text);

                    long n = 0;
                    using var bound = new CancellationTokenSource(GiveUp, _clock);
                    for (var at = 0; at < text.Length; at += ChunkChars)
                    {
                        var piece = text.Substring(at, Math.Min(ChunkChars, text.Length - at));
                        if (!await SendAsync(new StreamChunk(stream, ++n, Text: piece), bound.Token)) break;
                    }

                    // The count is of what there was to send: control tells a read that lost a chunk by it.
                    answer = new AgentFileRead(read.Request, AgentFileRead.Ok, null, (text.Length + ChunkChars - 1) / ChunkChars);
                }
            }
        }
        catch (Exception exception)
        {
            answer = new AgentFileRead(read.Request, AgentFileRead.Unreadable, exception.Message, 0);
        }

        try
        {
            await publish(answer);
        }
        catch (Exception exception)
        {
            log?.LogWarning("A file read ended, and saying so failed: {Message}", exception.Message);
        }
    }

    /// <summary>Control's set, together with what this worker's own environment holds under each credential name.</summary>
    private ValueRedactor Redaction(ReadAgentFile read)
    {
        var own = ValueRedactor.OfEnvironment((environment ?? OwnEnvironment)(), read.CredentialNames, []);
        return (read.Redaction ?? ValueRedactor.Empty).With(own);
    }

    private static IDictionary<string, string?> OwnEnvironment()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) variables[(string)entry.Key] = entry.Value as string;
        return variables;
    }

    /// <summary>Sends a chunk, trying again while the connection is down, until <paramref name="ct"/> gives up.</summary>
    private async Task<bool> SendAsync(StreamChunk chunk, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                if (await sink.StreamAsync(chunk, ct)) return true;
                await Task.Delay(WorkerTerminals.Retry, _clock, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
