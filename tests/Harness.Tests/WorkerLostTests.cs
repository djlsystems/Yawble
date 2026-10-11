using System.Collections.Concurrent;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A run whose worker stopped fails <c>worker-lost</c>, never <c>interrupted</c> and never an agent
/// fault. While the worker's connection is down and may come back, nothing else ends the run: not a
/// sample, not the stop backstop. <c>interrupted</c> keeps its meaning for an end lost while the
/// worker was there.
/// </summary>
public sealed class WorkerLostTests
{
    private static readonly ContainerId Member = new("alpha", "Developer");

    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch.AddDays(1);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_on_a_worker_gone_past_its_grace_fails_worker_lost()
    {
        var directory = new RunDirectory();
        var worker = new Scripted(directory);
        var running = directory.RunAsync(worker, StartOf(Member), Ct);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        worker.Drop();
        await Task.Delay(50, Ct);
        Assert.False(running.IsCompleted);

        var why = RunDirectory.WorkerLostText(worker.Id, Start, TimeSpan.FromSeconds(30));
        worker.Lose(why);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(FailureClasses.WorkerLost, result.FailureClass);
        Assert.NotEqual(FailureClasses.AgentFault, result.FailureClass);
        Assert.Equal(why, result.LaunchError);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task A_worker_back_within_its_grace_keeps_its_runs()
    {
        var directory = new RunDirectory();
        var worker = new Scripted(directory);
        var start = StartOf(Member);
        var running = directory.RunAsync(worker, start, Ct);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        worker.Drop();
        worker.Reconnect();
        await worker.EndAsync(start.Run, "done");

        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("done", result.Output);
        Assert.Null(result.FailureClass);
    }

    [Fact]
    public async Task A_sample_gap_on_a_connected_worker_is_still_interrupted()
    {
        var directory = new RunDirectory();
        var worker = new Scripted(directory);
        var running = directory.RunAsync(worker, StartOf(Member), Ct);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Connected, and a sample after the start does not list the run: its end was lost on the way.
        await worker.SampleAsync([]);

        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(FailureClasses.Interrupted, result.FailureClass);
        Assert.Equal(RunDirectory.LostRunText, result.LaunchError);
    }

    [Fact]
    public async Task A_run_on_a_dropped_worker_is_not_interrupted_by_a_missing_sample()
    {
        var clock = new ManualTime(Start);
        var directory = new RunDirectory(clock: clock);
        var worker = new Scripted(directory);
        var running = directory.RunAsync(worker, StartOf(Member), Ct);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        worker.Drop();

        // A sample that omits the run, as one taken while the connection was down would, and the
        // sampler's own interval passing: neither ends the run.
        await worker.SampleAsync([]);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50, Ct);
        Assert.False(running.IsCompleted);

        worker.Lose(RunDirectory.WorkerLostText(worker.Id, Start, TimeSpan.FromSeconds(30)));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(FailureClasses.WorkerLost, result.FailureClass);
    }

    [Fact]
    public async Task A_run_lost_with_its_worker_ends_once()
    {
        var directory = new RunDirectory();
        var worker = new Scripted(directory);
        var start = StartOf(Member);
        var running = directory.RunAsync(worker, start, Ct);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        worker.Drop();
        worker.Lose(RunDirectory.WorkerRestartedText(worker.Id));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // The worker's own end, replayed late, goes nowhere: the run already ended, once.
        await worker.EndAsync(start.Run, "late");

        Assert.Equal(FailureClasses.WorkerLost, result.FailureClass);
        Assert.Equal(RunDirectory.WorkerRestartedText(worker.Id), result.LaunchError);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task An_unanswered_stop_waits_for_the_grace_while_the_worker_is_dropped()
    {
        var clock = new ManualTime(Start);
        var rows = new Rows();
        var directory = new RunDirectory(diagnostics: rows, clock: clock);
        var worker = new Scripted(directory);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = directory.RunAsync(worker, StartOf(Member), stop.Token);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        worker.Drop();
        await stop.CancelAsync();

        // Past the backstop, twice over: the run stays open while the worker may still come back.
        clock.Advance(RunDirectory.StopBackstop + TimeSpan.FromSeconds(1));
        clock.Advance(RunDirectory.StopBackstop + TimeSpan.FromSeconds(1));
        await Task.Delay(50, Ct);
        Assert.False(running.IsCompleted);
        Assert.Empty(rows.Written);

        worker.Lose(RunDirectory.WorkerLostText(worker.Id, Start, TimeSpan.FromSeconds(30)));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(FailureClasses.WorkerLost, result.FailureClass);
    }

    [Fact]
    public async Task An_unanswered_stop_on_a_connected_worker_is_interrupted_and_names_the_worker()
    {
        var clock = new ManualTime(Start);
        var rows = new Rows();
        var directory = new RunDirectory(diagnostics: rows, clock: clock);
        var worker = new Scripted(directory);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = directory.RunAsync(worker, StartOf(Member), stop.Token);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await stop.CancelAsync();
        clock.Advance(RunDirectory.StopBackstop + TimeSpan.FromSeconds(1));

        var result = await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(FailureClasses.Interrupted, result.FailureClass);
        var row = Assert.Single(rows.Written);
        Assert.Equal(DiagnosticKinds.WorkerRunUnanswered, row.Kind);
        Assert.Contains("Worker w1 did not end the stopped run alpha/Developer", row.Message);
    }

    [Fact]
    public void Worker_lost_is_never_resumed_and_is_in_every_class()
    {
        Assert.Equal("worker-lost", FailureClasses.WorkerLost);
        Assert.Contains(FailureClasses.WorkerLost, FailureClasses.All);
        Assert.True(FailureClasses.IsKnown(FailureClasses.WorkerLost));
        Assert.False(FailureClasses.ResumesAutomatically(FailureClasses.WorkerLost));
        Assert.NotNull(FailureClasses.Sentence(FailureClasses.WorkerLost));
    }

    [Fact]
    public void Worker_lost_words_say_the_worker_stopped_and_re_sending_runs_it_elsewhere()
    {
        var text = RunDirectory.WorkerLostText(new WorkerId("w1"), Start.AddHours(13).AddMinutes(5), TimeSpan.FromSeconds(30));

        Assert.Equal(
            "The worker running this run (w1) stopped: its connection dropped at 1970-01-02 13:05:00 UTC and did not come back "
            + "within 30 s. Re-sending the instruction runs it on another worker.",
            text);
        Assert.StartsWith("The worker running this run (w1) stopped: it came back as a new process", RunDirectory.WorkerRestartedText(new WorkerId("w1")));

        var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [PayloadFields.ExitCode] = -1,
            [PayloadFields.Output] = string.Empty,
            [PayloadFields.LaunchError] = text,
            [PayloadFields.FailureClass] = FailureClasses.WorkerLost,
        });
        var said = MessageText.Of(new Message(7, MessageTypes.Failed, payload, "alpha/Developer", 1, 6, 1, DateTimeOffset.UtcNow));

        Assert.StartsWith("Developer FAILED. [worker-lost] ", said);
        Assert.Contains("worker running this run stopped", said);
        Assert.DoesNotContain("agent itself failed", said);
    }

    [Fact]
    public void The_manager_prompt_and_skill_re_send_worker_lost_and_escalate_the_second()
    {
        var prompt = BuiltInPrompts.For(SkillRoles.Manager);
        Assert.Contains(
            "A member run that failed `[worker-lost]` was cut off because the worker running it stopped.\n"
            + "        Re-send the instruction; escalate to a person only if it has already happened twice.",
            BuiltInPromptsSource(), StringComparison.Ordinal);
        Assert.Contains("`[worker-lost]` was cut off because the worker running it stopped.", prompt);
        Assert.Contains("Re-send the instruction; escalate to a person only if it has already happened twice.", prompt);

        var skill = BuiltInSkills.Find("manager")!.Body;
        Assert.Contains("## A run lost with its worker is re-sent, not escalated", skill);
        Assert.Contains("A run that failed with `[worker-lost]` was cut off because the worker running it", skill);
        Assert.Contains("already been lost this way twice in this workflow", skill);

        // The launch-missing sentences stand as they were.
        Assert.Contains("## A program missing at launch is re-sent, not escalated", skill);
        Assert.Contains("- A launch-missing failure is re-sent once, then raised to the person.", BuiltInSkills.Find("running-the-backlog")!.Body);
        Assert.Contains("- A worker-lost failure is re-sent once, then raised to the person.", BuiltInSkills.Find("running-the-backlog")!.Body);
    }

    /// <summary>The prompt as written in the source, with its own line breaks.</summary>
    private static string BuiltInPromptsSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Harness.Host", "BuiltInPrompts.cs"));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private static StartRun StartOf(ContainerId member) =>
        new(RunId.For(member), "probe", "You are a probe.", "hello", "", "/", new Dictionary<string, string>(), null, null, null, null, null);

    /// <summary>
    /// A worker over a connection, scripted: it starts what it is sent, and the test drops its
    /// connection, brings it back, or loses it.
    /// </summary>
    private sealed class Scripted(RunDirectory control) : IRunWorker, IRunWorkerConnection
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _seq;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<ControlMessage> Sent { get; } = new();

        public WorkerId Id { get; } = new("w1");

        public Task Closed => _closed.Task;

        public bool Dropped { get; private set; }

        public string? Lost { get; private set; }

        public void Drop() => Dropped = true;

        public void Reconnect() => Dropped = false;

        public void Lose(string why)
        {
            Lost = why;
            _closed.TrySetResult();
        }

        public async Task SendAsync(ControlMessage message, CancellationToken ct = default)
        {
            Sent.Enqueue(message);
            if (message is StartRun start)
            {
                await control.HandleAsync(new WorkerEnvelope(Id, ++_seq, new RunStarted(start.Run, 4242, Start)), ct);
                Started.TrySetResult();
            }
        }

        public async Task EndAsync(RunId run, string output)
        {
            await control.HandleAsync(new WorkerEnvelope(Id, ++_seq, new RunOutput(run, output)));
            await control.HandleAsync(new WorkerEnvelope(Id, ++_seq, new RunEnded(run, 0, 4242, null, null, null, null, null)));
        }

        public Task SampleAsync(IReadOnlyList<RunId> open) =>
            control.HandleAsync(new WorkerEnvelope(
                Id, ++_seq, new WorkerCapacitySampled(Start, Harness.Host.Capacity.WorkerCapacity.ToFigures(Harness.Host.Capacity.WorkerCapacity.NotMeasured), open)));
    }

    private sealed class Rows : IDiagnosticsLog
    {
        public ConcurrentQueue<(string Kind, string? Message)> Written { get; } = new();

        public Task WriteAsync(
            DiagnosticSeverity severity, string kind, string? source = null, string? route = null, int? status = null,
            string? exceptionType = null, string? message = null, string? detail = null, CancellationToken ct = default)
        {
            Written.Enqueue((kind, message));
            return Task.CompletedTask;
        }

        public Task<DiagnosticsPage> ReadAsync(DiagnosticsFilter? filter = null, long? before = null, int take = 50, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool?> HasAnyAsync(CancellationToken ct = default) => Task.FromResult<bool?>(!Written.IsEmpty);

        public Task TrimAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
