using System.Collections.Concurrent;
using System.Text;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// CONTROL'S SIDE OF THE RUNS IN FLIGHT. Starts a run on a worker and waits for its end, and
/// handles what workers say about their runs: a progress sentence goes on the member's card, a
/// diagnostics row into the instance's log, the live view into <see cref="LiveRuns"/> for the live
/// route, and the output, usage and end back into the <see cref="AgentResult"/> the caller has
/// always been given.
/// </summary>
/// <remarks>
/// A LOST RUN IS AN END, NEVER A HANG. A run whose end does not arrive is ended here, as
/// <see cref="FailureClasses.Interrupted"/> with <see cref="LostRunText"/>, when its worker's
/// connection closes, when a capacity sample taken after the run was open on its worker no longer
/// lists it, when its start cannot be sent, or when a stop goes unanswered for
/// <see cref="StopBackstop"/>. Like a Host restart, it is never resumed by itself.
///
/// The sample rule rests on the worker publishing a run's end and dropping it from its open runs
/// in one step, in sequence, so only an end lost on the way leaves the gap. "Open on its worker"
/// is known from the run's <see cref="RunStarted"/>, or for a run that ends before its process
/// starts, from the first sample delivered after its start was applied: that sample may have been
/// measured just before, and every later one after.
/// </remarks>
public sealed class RunDirectory
{
    /// <summary>What a run whose end never arrived says.</summary>
    public const string LostRunText =
        "This run's worker stopped answering before the run ended, so how far it got is not known.";

    /// <summary>
    /// How long a stopped run may take to end before it is taken as lost: longer than anything a
    /// stopped run still does - the child's drain grace, the last look for its transcript, its rows.
    /// </summary>
    public static readonly TimeSpan StopBackstop = ChildProcess.DrainGrace + TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<RunId, Open> _runs = new();
    private readonly ConcurrentDictionary<IRunWorker, bool> _watched = new(ReferenceEqualityComparer.Instance);
    private readonly Func<IMemberReports?> _reports;
    private readonly Func<IDiagnosticsLog?> _diagnostics;
    private readonly LiveRuns? _live;
    private readonly TimeProvider _clock;

    public RunDirectory(IMemberReports? reports = null, IDiagnosticsLog? diagnostics = null, LiveRuns? live = null, TimeProvider? clock = null)
        : this(() => reports, () => diagnostics, live, clock)
    {
    }

    /// <summary>The reports and the diagnostics log are asked for when first needed, not at composition.</summary>
    public RunDirectory(Func<IMemberReports?> reports, Func<IDiagnosticsLog?> diagnostics, LiveRuns? live, TimeProvider? clock = null)
    {
        _reports = reports;
        _diagnostics = diagnostics;
        _live = live;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Starts <paramref name="start"/> on <paramref name="worker"/> and answers with how it ended.
    /// The caller's token stopping is sent as <see cref="CancelRun"/>; the run still ends through
    /// the worker, in the words it has always ended in.
    /// </summary>
    public async Task<AgentResult> RunAsync(IRunWorker worker, StartRun start, CancellationToken ct = default)
    {
        var (ended, output, usage, lost) = await EndOfAsync(worker, start, null, ct);
        return lost ? Lost() : RunResults.Result(output, usage, ended);
    }

    /// <summary>
    /// Starts a program run (<see cref="StartRun.Process"/>) and answers with its end, or
    /// <c>Lost</c> when its end never arrived. Each line it prints is handed to
    /// <paramref name="onLine"/>, in order, before the worker reads the next.
    /// </summary>
    public async Task<(RunEnded Ended, bool Lost)> RunProcessAsync(
        IRunWorker worker, StartRun start, Func<string, Task> onLine, CancellationToken ct = default)
    {
        var (ended, _, _, lost) = await EndOfAsync(worker, start, onLine, ct);
        return (ended, lost);
    }

    private async Task<(RunEnded Ended, string Output, UsageFigures? Usage, bool Lost)> EndOfAsync(
        IRunWorker worker, StartRun start, Func<string, Task>? onLine, CancellationToken ct)
    {
        var open = new Open(worker, start.Run) { OnLine = onLine };
        _runs[start.Run] = open;
        Watch(worker);

        try
        {
            try
            {
                await worker.SendAsync(start, CancellationToken.None);
                open.Applied = true;
            }
            catch (Exception)
            {
                open.Lose();
            }

            if (worker.Closed.IsCompleted) open.Lose();

            RunEnded ended;
            using (ct.Register(() => _ = CancelAsync(open)))
            {
                ended = await open.Ended.Task;
            }

            if (ReferenceEquals(ended, open.LostEnd)) return (ended, string.Empty, null, true);

            if (ended.Fault is { } fault)
            {
                if (fault.Canceled) throw new OperationCanceledException(fault.Message, ct);
                throw new RunFaultException(fault);
            }

            return (ended, open.Output.ToString(), open.Usage, false);
        }
        finally
        {
            _runs.TryRemove(new KeyValuePair<RunId, Open>(start.Run, open));
            open.Live?.Dispose();
            open.Backstop?.Dispose();
        }
    }

    /// <summary>A lost run's result: nothing it did is known.</summary>
    public static AgentResult Lost() =>
        new(-1, string.Empty, LostRunText, FailureClass: FailureClasses.Interrupted);

    /// <summary>Every run open on a worker whose connection closes is lost, once it does.</summary>
    private void Watch(IRunWorker worker)
    {
        if (!_watched.TryAdd(worker, true)) return;

        _ = worker.Closed.ContinueWith(
            _ =>
            {
                foreach (var open in _runs.Values.Where(o => ReferenceEquals(o.Worker, worker))) open.Lose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>What one worker said. Called in <see cref="WorkerEnvelope.Seq"/> order.</summary>
    public async Task HandleAsync(WorkerEnvelope envelope, CancellationToken ct = default)
    {
        if (envelope.Event is WorkerCapacitySampled sampled)
        {
            // A run that was open on the worker before this sample and is not in it ended there,
            // and its end did not arrive.
            foreach (var candidate in _runs.Values.Where(o => o.Worker.Id == envelope.Worker))
            {
                if (candidate.Since is null)
                {
                    if (candidate.Applied) candidate.Since = envelope.Seq;
                }
                else if (candidate.Since < envelope.Seq && !sampled.OpenRuns.Contains(candidate.Run))
                {
                    candidate.Lose();
                }
            }

            return;
        }

        if (envelope.Event is not RunEvent { Run: var run } @event || !_runs.TryGetValue(run, out var open)) return;
        var reports = _reports();
        var diagnostics = _diagnostics();
        var live = _live;

        switch (@event)
        {
            case RunStarted:
                open.Since ??= envelope.Seq;
                break;

            case RunProgress progress:
                if (reports is not null) await reports.ProgressAsync(run.Member, progress.Sentence, CancellationToken.None);
                break;

            case RunChildStoppedByMemoryLimit stopped:
                if (reports is not null) await reports.ProgressAsync(run.Member, stopped.Sentence, CancellationToken.None);
                break;

            case RunDiagnostic row:
                if (diagnostics is not null)
                {
                    await diagnostics.WriteAsync(
                        row.Severity, row.Kind, row.Source, exceptionType: row.ExceptionType, message: row.Message,
                        detail: row.Detail, ct: CancellationToken.None);
                }

                break;

            case RunLiveViewChanged view:
                if (live is null) break;
                if (open.Live is null) open.Live = live.Begin(run.Member, view.Transcript, view.Format, view.Reason, view.Finding);
                else open.Live.Found(view.Transcript, view.Reason);
                break;

            case RunOutput output:
                if (open.OnLine is { } onLine) await onLine(output.Text);
                else open.Output.Append(output.Text);
                break;

            case RunUsage usage:
                open.Usage = usage.Usage;
                break;

            case RunEnded ended:
                open.Live?.Dispose();
                open.Ended.TrySetResult(ended);
                break;
        }
    }

    private async Task CancelAsync(Open open)
    {
        // The backstop for a worker that is there but no longer answers: a stop it never ends.
        open.Backstop ??= _clock.CreateTimer(_ => open.Lose(), null, StopBackstop, Timeout.InfiniteTimeSpan);

        try
        {
            await open.Worker.SendAsync(new CancelRun(open.Run), CancellationToken.None);
        }
        catch (Exception)
        {
            // A worker that cannot be told is a worker whose run will not end by itself: what
            // happens then is the lost-run path's.
        }
    }

    /// <summary>One run this side has started and not yet seen end.</summary>
    private sealed class Open(IRunWorker worker, RunId run)
    {
        public IRunWorker Worker => worker;

        public RunId Run => run;

        public TaskCompletionSource<RunEnded> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StringBuilder Output { get; } = new();

        public UsageFigures? Usage { get; set; }

        /// <summary>Where a program run's lines go, as they arrive.</summary>
        public Func<string, Task>? OnLine { get; init; }

        public LiveRun? Live { get; set; }

        /// <summary>Set once its start has been applied.</summary>
        public volatile bool Applied;

        /// <summary>A sequence number by which the run was open on its worker.</summary>
        public long? Since { get; set; }

        public ITimer? Backstop { get; set; }

        /// <summary>The end a lost run is given, told apart from a real one by reference.</summary>
        public RunEnded LostEnd { get; } = new(run, -1, null, LostRunText, FailureClasses.Interrupted, null, null, null);

        /// <summary>Ends the run as lost, unless its real end came first.</summary>
        public void Lose() => Ended.TrySetResult(LostEnd);
    }
}

/// <summary>
/// A run whose launch threw on the worker rather than answering: the worker's exception, said
/// again on this side with its message, which is all a caller has ever read from it.
/// </summary>
public sealed class RunFaultException(RunFault fault) : Exception(fault.Message)
{
    /// <summary>The worker's exception type.</summary>
    public string WorkerType => fault.Type;
}
