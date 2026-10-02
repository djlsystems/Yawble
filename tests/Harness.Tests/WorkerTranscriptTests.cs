using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Harness.Tests;

/// <summary>
/// THE AGENT'S FILES ARE READ ON A WORKER: a running member's transcript is followed there and a
/// finished run's file read there, over a real connection; with no worker, or a read that did not all
/// arrive, control says so in a sentence and never serves part of it. A re-read is redacted of what
/// the worker's own environment holds; in one process the text is what it was.
/// </summary>
public sealed class WorkerTranscriptTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("worker-transcripts-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_finished_transcript_is_read_whole_on_the_worker()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();
        var path = Write("t.jsonl", "{\"a\":1}\n{\"b\":2}\n");

        var read = await bed.Reads.ReadAsync(path, [], ValueRedactor.Empty, Ct);

        Assert.Equal(new FileRead(FileReadKind.Ok, "{\"a\":1}\n{\"b\":2}\n", null), read);
    }

    [Fact]
    public async Task A_read_longer_than_one_chunk_arrives_whole_and_in_order()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();
        var text = string.Concat(Enumerable.Range(0, 3 * WorkerTranscripts.ChunkChars / 10).Select(i => $"{i % 10:D1}........."));
        var path = Write("long.jsonl", text);

        var read = await bed.Reads.ReadAsync(path, [], null, Ct);

        Assert.Equal(FileReadKind.Ok, read.Kind);
        Assert.Equal(text, read.Text);
    }

    [Fact]
    public async Task No_worker_reads_a_finished_transcript_as_a_sentence()
    {
        var pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0, TimeProvider.System));
        var reads = new WorkerReads(pool, new WorkerStreams());

        var read = await reads.ReadAsync(Write("t.jsonl", "x\n"), [], ValueRedactor.Empty, Ct);

        Assert.Equal(new FileRead(FileReadKind.NoWorker, null, WorkerReads.NoWorkerText), read);
        Assert.Equal(
            "No worker is connected, and a run's transcript is read as the agent on a worker; it can be read once a worker connects.",
            WorkerReads.NoWorkerText);
    }

    [Fact]
    public async Task Gone_and_unreadable_are_answered_as_before()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();

        var gone = await bed.Reads.ReadAsync(Path.Combine(_folder, "never-written.jsonl"), [], ValueRedactor.Empty, Ct);
        Assert.Equal(new FileRead(FileReadKind.Gone, null, null), gone);

        // A folder where the file should be: there, and not readable as a file.
        var unreadable = await bed.Reads.ReadAsync(_folder, [], ValueRedactor.Empty, Ct);
        Assert.Equal(FileReadKind.Unreadable, unreadable.Kind);
        Assert.False(string.IsNullOrWhiteSpace(unreadable.Why));
    }

    [Fact]
    public async Task A_read_with_a_missing_chunk_is_incomplete_never_part_of_the_text()
    {
        var (reads, worker, streams) = Faked();
        worker.OnSend = message =>
        {
            if (message is ReadAgentFile read)
            {
                // Two chunks were sent; one arrived.
                streams.Deliver(worker.Id, new StreamChunk(ReadAgentFile.StreamOf(read.Request), 1, Text: "first half"));
                _ = streams.HandleAsync(new WorkerEnvelope(worker.Id, 1, new AgentFileRead(read.Request, AgentFileRead.Ok, null, 2)));
            }

            return Task.CompletedTask;
        };

        var answer = await reads.ReadAsync("/t.jsonl", [], ValueRedactor.Empty, Ct);

        Assert.Equal(new FileRead(FileReadKind.Incomplete, null, WorkerReads.IncompleteText), answer);
    }

    [Fact]
    public async Task A_worker_lost_during_a_read_is_incomplete()
    {
        var (reads, worker, _) = Faked();
        worker.OnSend = _ =>
        {
            worker.Lose("Worker w1 did not come back within 30 s.");
            return Task.CompletedTask;
        };

        var answer = await reads.ReadAsync("/t.jsonl", [], ValueRedactor.Empty, Ct);

        Assert.Equal(FileReadKind.Incomplete, answer.Kind);
        Assert.Equal(WorkerReads.IncompleteText, answer.Why);
    }

    [Fact]
    public async Task The_tail_refusal_reads_as_before()
    {
        const string Refusal = "tail is not in a root-owned system directory (/usr/bin, /bin), so the transcript cannot be read as the agent.";
        var (reads, worker, streams) = Faked();
        worker.OnSend = message =>
        {
            if (message is ReadAgentFile read)
            {
                Assert.True(read.AsTranscript);
                _ = streams.HandleAsync(new WorkerEnvelope(worker.Id, 1, new AgentFileRead(read.Request, AgentFileRead.Refused, Refusal, 0)));
            }

            return Task.CompletedTask;
        };

        var answer = await reads.ReadAsync("/t.jsonl", [], ValueRedactor.Empty, Ct, transcript: true);

        Assert.Equal(new FileRead(FileReadKind.Refused, null, Refusal), answer);
    }

    [Fact]
    public async Task A_reread_is_redacted_of_a_key_only_the_workers_environment_held()
    {
        const string WorkerOnly = "sk-worker-only-Kp4v9Zr2";
        await using var bed = new WorkerStreamBed(environment: () => new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = WorkerOnly });
        await bed.StartAsync();
        var path = Write("t.jsonl", $"{{\"key\":\"{WorkerOnly}\"}}\n");
        Assert.Contains(WorkerOnly, await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);

        // Control's own set is empty: it never held this key.
        var read = await bed.Reads.ReadAsync(path, ["ANTHROPIC_API_KEY"], ValueRedactor.Empty, Ct);

        Assert.Equal(FileReadKind.Ok, read.Kind);
        Assert.DoesNotContain(WorkerOnly, read.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Kp4v9", read.Text, StringComparison.Ordinal);
        Assert.Contains(DiagnosticRedaction.Placeholder, read.Text, StringComparison.Ordinal);

        // Read for control's own use, it is the file as it is.
        var raw = await bed.Reads.ReadAsync(path, ["ANTHROPIC_API_KEY"], null, Ct);
        Assert.Contains(WorkerOnly, raw.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_one_process_the_text_is_what_it_was()
    {
        const string Secret = "issued-value-Hq2m8Wt";
        var streams = new WorkerStreams();
        var runAs = AgentLaunchUser.Same("tester", "the test runs everything as itself");
        var heartbeat = new RunHeartbeat();
        var local = InProcessWorker.Connect(
            WorkerId.Local,
            events => new WorkerHost(
                WorkerId.Local, events, new RunLauncher(heartbeat, reports: false, lookup: LaunchLookup.Once), heartbeat,
                streaming: new WorkerStreaming((IRunStreamSink)events, new FakePtyEngine(), runAs, () => new Dictionary<string, string?>())),
            streams.HandleAsync,
            streams.Deliver);
        var pool = new WorkerPool([(WorkerId.Local, new HeadroomGate(() => 80, () => 0, TimeProvider.System))]);
        pool.Connect(local.Worker);
        var reads = new WorkerReads(pool, streams);

        var raw = $"{{\"type\":\"assistant\",\"text\":\"the key is {Secret}\"}}\n{{\"type\":\"user\"}}\n";
        var path = Write("t.jsonl", raw);
        var redactor = ValueRedactor.For([Secret]);

        var read = await reads.ReadAsync(path, ["ANTHROPIC_API_KEY"], redactor, Ct, transcript: true);

        // What control did itself before: the set applied to the file's text.
        Assert.Equal(new FileRead(FileReadKind.Ok, redactor.Apply(raw), null), read);
        local.Close();
    }

    [Fact]
    public async Task A_followed_transcript_streams_from_its_first_line_and_ends_after_the_run_ends()
    {
        await using var bed = new WorkerStreamBed();
        await bed.StartAsync();
        var path = Write("live.jsonl", "one\ntwo\n");
        using var stream = bed.Streams.Open("live:f1", bed.Remote);
        await bed.Remote.SendAsync(new FollowTranscript("live:f1", new RunId(new ContainerId("alpha", "Dev"), "n"), path, LiveView.ClaudeJsonl), Ct);

        var chunks = new List<StreamChunk>();
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 3; i++) chunks.Add(await stream.Reader.ReadAsync(bounded.Token));

        // Written last, before the run's end is said: read before the stream ends.
        await File.AppendAllTextAsync(path, "three\n", Ct);
        await bed.Remote.SendAsync(new StopStream("live:f1", RunEnded: true), Ct);
        await foreach (var chunk in stream.Reader.ReadAllAsync(bounded.Token)) chunks.Add(chunk);

        Assert.Null(chunks[0].Text);
        Assert.False(chunks[0].End);
        Assert.Equal(["one", "two", "three"], chunks.Skip(1).Where(c => !c.End).Select(c => c.Text));
        Assert.True(chunks[^1].End);
        Assert.True(chunks[^1].N > 0);
        Assert.Null(chunks[^1].Gap);
    }

    [Fact]
    public async Task Leaving_the_live_view_stops_the_workers_tail()
    {
        var sent = new List<StreamChunk>();
        var sink = new ListSink(sent);
        var transcripts = new WorkerTranscripts(
            sink, AgentLaunchUser.Same("tester", "the test runs everything as itself"), _ => Task.CompletedTask, _ => CancellationToken.None);
        var path = Write("live.jsonl", "one\n");

        transcripts.Follow(new FollowTranscript("live:f2", new RunId(new ContainerId("alpha", "Dev"), "n"), path, LiveView.ClaudeJsonl));
        await WorkerStreamBed.Until(() => sink.Count >= 2);
        Assert.Equal(["live:f2"], transcripts.Following);

        transcripts.Stop("live:f2");

        await WorkerStreamBed.Until(() => transcripts.Following.Count == 0);
    }

    [Fact]
    public async Task With_two_workers_a_live_view_is_asked_of_the_runs_own_worker()
    {
        var (directory, pool, first, second) = TwoWorkers();
        var member = new ContainerId("alpha", "Dev");
        var start = StartOf(member);
        var running = directory.RunAsync(second, start, Ct);
        await WorkerStreamBed.Until(() => second.Sent.Count > 0);

        // Any worker would be the first; the run is on the second.
        Assert.Same(first, pool.Worker(null));

        Assert.Null(LiveViewEndpoints.Place(directory, pool, member, out var placed));
        Assert.Same(second, placed.Worker);
        Assert.Equal(start.Run, placed.Run);

        second.Lose("the test is over");
        await running.WaitAsync(WorkerStreamBed.Bound, Ct);
    }

    [Fact]
    public async Task A_dropped_workers_live_view_answers_503_with_a_sentence()
    {
        var (directory, pool, first, second) = TwoWorkers();
        var member = new ContainerId("alpha", "Dev");
        var running = directory.RunAsync(second, StartOf(member), Ct);
        await WorkerStreamBed.Until(() => second.Sent.Count > 0);
        second.Dropped = true;

        // Not followed on the connected first worker instead: the run's own worker says, or nobody does.
        var refused = Assert.IsType<ProblemHttpResult>(LiveViewEndpoints.Place(directory, pool, member, out _));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, refused.StatusCode);
        Assert.Equal(LiveViewEndpoints.NotConnectedText(second.Id), refused.ProblemDetails.Detail);
        Assert.Equal(
            "The worker running this run (w2) is not connected, so its live view cannot be read; it comes back if the worker reconnects within its grace.",
            refused.ProblemDetails.Detail);
        Assert.Empty(first.Sent);

        second.Lose("the test is over");
        await running.WaitAsync(WorkerStreamBed.Bound, Ct);
    }

    private static (RunDirectory Directory, WorkerPool Pool, FakeWorker First, FakeWorker Second) TwoWorkers()
    {
        var first = new FakeWorker("w1");
        var second = new FakeWorker("w2");
        var pool = new WorkerPool([
            (first.Id, new HeadroomGate(() => 80, () => 0, TimeProvider.System)),
            (second.Id, new HeadroomGate(() => 80, () => 0, TimeProvider.System)),
        ]);
        pool.Connect(first);
        pool.Connect(second);
        return (new RunDirectory(), pool, first, second);
    }

    private static StartRun StartOf(ContainerId member) =>
        new(RunId.For(member), "probe", "You are a probe.", "hello", "", "/", new Dictionary<string, string>(), null, null, null, null, null);

    private (WorkerReads Reads, FakeWorker Worker, WorkerStreams Streams) Faked()
    {
        var streams = new WorkerStreams();
        var worker = new FakeWorker("w1");
        var pool = new WorkerPool([(worker.Id, new HeadroomGate(() => 80, () => 0, TimeProvider.System))]);
        pool.Connect(worker);
        return (new WorkerReads(pool, streams), worker, streams);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private sealed class ListSink(List<StreamChunk> sent) : IRunStreamSink
    {
        public int Count
        {
            get
            {
                lock (sent) return sent.Count;
            }
        }

        public ValueTask<bool> StreamAsync(StreamChunk chunk, CancellationToken ct = default)
        {
            lock (sent) sent.Add(chunk);
            return ValueTask.FromResult(true);
        }
    }
}
