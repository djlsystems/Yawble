using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The stream lane of a worker's connection: chunks go straight to the socket, never into the outbox
/// whose overflow costs a worker its runs; control drops what nobody opened, and ends a stream that
/// lost a chunk or whose worker was lost with a sentence, never by hanging.
/// </summary>
public sealed class WorkerStreamTests
{
    [Fact]
    public async Task Stream_chunks_are_not_kept_in_the_outbox()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();
        using var stream = bed.Streams.Open("live:s1", bed.Remote);

        const int Count = 10_000;
        for (var n = 1; n <= Count; n++)
        {
            Assert.True(await bed.Connection.StreamAsync(new StreamChunk("live:s1", n, Text: $"line {n}"), TestContext.Current.CancellationToken));
            Assert.Equal(0, bed.Connection.Unacknowledged);
        }

        await bed.Connection.StreamAsync(new StreamChunk("live:s1", Count + 1, End: true), TestContext.Current.CancellationToken);

        var received = new List<string>();
        await foreach (var chunk in stream.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            if (chunk.Text is { } text) received.Add(text);
        }

        Assert.Equal(Enumerable.Range(1, Count).Select(n => $"line {n}"), received);
        Assert.Equal(0, bed.Connection.Unacknowledged);
    }

    [Fact]
    public async Task A_chunk_for_an_unknown_stream_is_dropped()
    {
        var streams = new WorkerStreams();
        var worker = new FakeWorker("w1");
        using var open = streams.Open("live:mine", worker);

        streams.Deliver(new WorkerId("w1"), new StreamChunk("live:nobodys", 1, Text: "not mine"));
        streams.Deliver(new WorkerId("w2"), new StreamChunk("live:mine", 1, Text: "another worker's"));
        streams.Deliver(new WorkerId("w1"), new StreamChunk("live:mine", 1, Text: "mine"));
        streams.Close("live:mine");

        Assert.Equal(["mine", null], await Texts(open));
        Assert.False(streams.IsOpen("live:nobodys"));
    }

    [Fact]
    public async Task A_missing_chunk_number_ends_the_stream_with_a_sentence()
    {
        var streams = new WorkerStreams();
        var worker = new FakeWorker("w1");

        // A stream that must be whole ends at the gap.
        using var whole = streams.Open("read:r1", worker);
        streams.Deliver(worker.Id, new StreamChunk("read:r1", 1, Text: "one"));
        streams.Deliver(worker.Id, new StreamChunk("read:r1", 3, Text: "three"));

        var chunks = await All(whole);
        Assert.Equal(["one", null], chunks.Select(c => c.Text));
        Assert.True(chunks[^1].End);
        Assert.Equal(WorkerStreams.MissedText, chunks[^1].Gap);

        // A terminal carries on, and says what was lost.
        using var terminal = streams.Open("terminal:t1", worker, whole: false);
        streams.Deliver(worker.Id, new StreamChunk("terminal:t1", 1, Text: "one"));
        streams.Deliver(worker.Id, new StreamChunk("terminal:t1", 3, Text: "three"));
        streams.Close("terminal:t1");

        var carried = await All(terminal);
        Assert.Equal(["one", "three", null], carried.Select(c => c.Text));
        Assert.Equal(WorkerStreams.MissedText, carried[1].Gap);
    }

    [Fact]
    public async Task A_stream_on_a_lost_worker_ends_with_the_lost_sentence()
    {
        var streams = new WorkerStreams();
        var worker = new FakeWorker("w1");
        using var stream = streams.Open("live:s1", worker);
        streams.Deliver(worker.Id, new StreamChunk("live:s1", 1, Text: "before"));

        worker.Lose("Worker w1 did not come back within 30 s.");

        var chunks = await All(stream);
        Assert.Equal("before", chunks[0].Text);
        Assert.True(chunks[^1].End);
        Assert.Equal("Worker w1 did not come back within 30 s.", chunks[^1].Gap);
        Assert.Equal("Worker w1 did not come back within 30 s.", stream.Lost);
        Assert.Null(await stream.Ended);
    }

    [Fact]
    public async Task The_event_that_ends_a_stream_comes_after_its_last_chunk()
    {
        var streams = new WorkerStreams();
        var worker = new FakeWorker("w1");
        using var stream = streams.Open(ReadAgentFile.StreamOf("r1"), worker);
        streams.Deliver(worker.Id, new StreamChunk(ReadAgentFile.StreamOf("r1"), 1, Text: "text"));

        await streams.HandleAsync(new WorkerEnvelope(worker.Id, 9, new AgentFileRead("r1", AgentFileRead.Ok, null, 1)), TestContext.Current.CancellationToken);

        Assert.Equal(["text", null], (await All(stream)).Select(c => c.Text));
        Assert.Equal(new AgentFileRead("r1", AgentFileRead.Ok, null, 1), await stream.Ended);
        Assert.Equal(1, stream.Received);

        // A late end for a stream that is gone is nothing.
        await streams.HandleAsync(new WorkerEnvelope(worker.Id, 10, new TerminalEnded("terminal:gone", 0)), TestContext.Current.CancellationToken);
    }

    private static async Task<List<StreamChunk>> All(WorkerStream stream)
    {
        List<StreamChunk> chunks = [];
        using var bounded = new CancellationTokenSource(WorkerStreamBed.Bound);
        await foreach (var chunk in stream.Reader.ReadAllAsync(bounded.Token)) chunks.Add(chunk);
        return chunks;
    }

    private static async Task<List<string?>> Texts(WorkerStream stream) => [.. (await All(stream)).Select(c => c.Text)];
}

/// <summary>A worker that is connected until the test loses it.</summary>
internal sealed class FakeWorker(string id) : IRunWorker, IRunWorkerConnection, IRunWorkerInput
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorkerId Id { get; } = new(id);

    public Task Closed => _closed.Task;

    public bool Dropped { get; set; }

    public string? Lost { get; private set; }

    public List<ControlMessage> Sent { get; } = [];

    public List<StreamInput> Typed { get; } = [];

    /// <summary>What a send does besides recording it; throws to refuse.</summary>
    public Func<ControlMessage, Task>? OnSend { get; set; }

    public Task SendAsync(ControlMessage message, CancellationToken ct = default)
    {
        lock (Sent) Sent.Add(message);
        return OnSend?.Invoke(message) ?? Task.CompletedTask;
    }

    public ValueTask InputAsync(StreamInput input, CancellationToken ct = default)
    {
        if (!Dropped) lock (Typed) Typed.Add(input);
        return ValueTask.CompletedTask;
    }

    public void Lose(string why)
    {
        Lost = why;
        _closed.TrySetResult();
    }
}
