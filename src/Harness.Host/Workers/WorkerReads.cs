using System.Text;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE AGENT'S FILES, READ ON A WORKER FOR CONTROL: a finished run's transcript for a person, and the
/// files a run's tool check reads. Control holds no agent's files itself; it asks the connected worker
/// with the most measured headroom (the files are on the shared volume) and waits, bounded, for the
/// text as a stream and the answer as an event.
/// </summary>
public sealed class WorkerReads(WorkerPool pool, WorkerStreams streams, TimeProvider? clock = null)
{
    /// <summary>How long a read may take on its worker before control stops waiting.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>What a read with no worker connected answers.</summary>
    public const string NoWorkerText =
        "No worker is connected, and a run's transcript is read as the agent on a worker; it can be read once a worker connects.";

    /// <summary>What a read whose text did not all arrive answers.</summary>
    public const string IncompleteText = "The worker's connection dropped while it read this transcript; try again.";

    /// <summary>What a read its worker did not answer in time answers.</summary>
    public static string NotAnsweredText(WorkerId worker) =>
        $"Worker {worker} did not answer the read of this transcript within {Bound.TotalSeconds:0} s; try again.";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The workers a read or a follow goes to.</summary>
    public WorkerPool Workers => pool;

    /// <summary>
    /// Reads <paramref name="path"/> as the agent on a worker. With <paramref name="redaction"/> the text
    /// is redacted on the worker of that set and of every credential variable in
    /// <paramref name="credentialNames"/> the worker's own environment holds; without, it is the file's
    /// text as it is, for control's own use. <paramref name="transcript"/> is a person's read of a run's
    /// transcript, refused where the live view would be.
    /// </summary>
    public async Task<FileRead> ReadAsync(
        string path, IReadOnlyList<string> credentialNames, ValueRedactor? redaction, CancellationToken ct = default,
        bool transcript = false)
    {
        IRunWorker worker;
        try
        {
            worker = pool.Worker(null);
        }
        catch (InvalidOperationException)
        {
            return new FileRead(FileReadKind.NoWorker, null, NoWorkerText);
        }

        var request = Guid.NewGuid().ToString("N");
        using var stream = streams.Open(ReadAgentFile.StreamOf(request), worker);

        try
        {
            await worker.SendAsync(new ReadAgentFile(request, path, credentialNames, redaction is not null, redaction, transcript), ct);
        }
        catch (InvalidOperationException)
        {
            return new FileRead(FileReadKind.Incomplete, null, IncompleteText);
        }

        using var timer = new CancellationTokenSource(Bound, _clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);

        var text = new StringBuilder();
        StreamChunk? end = null;
        try
        {
            await foreach (var chunk in stream.Reader.ReadAllAsync(bounded.Token))
            {
                if (chunk.End)
                {
                    end = chunk;
                    break;
                }

                text.Append(chunk.Text);
            }

            // The answer comes after the last chunk; a worker that sent its text answers at once.
            var answer = await stream.Ended.WaitAsync(bounded.Token);
            if (answer is not AgentFileRead found || end?.Gap is not null)
            {
                return new FileRead(FileReadKind.Incomplete, null, IncompleteText);
            }

            return found.Result switch
            {
                AgentFileRead.Ok when stream.Received >= found.Chunks => new FileRead(FileReadKind.Ok, text.ToString(), null),
                AgentFileRead.Ok => new FileRead(FileReadKind.Incomplete, null, IncompleteText),
                AgentFileRead.Gone => new FileRead(FileReadKind.Gone, null, null),
                AgentFileRead.Refused => new FileRead(FileReadKind.Refused, null, found.Detail),
                _ => new FileRead(FileReadKind.Unreadable, null, found.Detail ?? "the worker did not say why"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FileRead(FileReadKind.Incomplete, null, NotAnsweredText(worker.Id));
        }
    }
}

/// <summary>How a file read on a worker came out.</summary>
public enum FileReadKind
{
    /// <summary>Read whole: <see cref="FileRead.Text"/> is the text.</summary>
    Ok,

    /// <summary>The file is not there.</summary>
    Gone,

    /// <summary>The file is there and could not be read; <see cref="FileRead.Why"/> says why.</summary>
    Unreadable,

    /// <summary>The worker cannot read the agent's files at all; <see cref="FileRead.Why"/> says why.</summary>
    Refused,

    /// <summary>No worker is connected to read it.</summary>
    NoWorker,

    /// <summary>Not all of it arrived, or the worker did not answer.</summary>
    Incomplete,
}

/// <summary>What a file read on a worker found.</summary>
public sealed record FileRead(FileReadKind Kind, string? Text, string? Why);
