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
public sealed class RunDirectory(IMemberReports? reports = null, IDiagnosticsLog? diagnostics = null, LiveRuns? live = null)
{
    private readonly ConcurrentDictionary<RunId, Open> _runs = new();

    /// <summary>
    /// Starts <paramref name="start"/> on <paramref name="worker"/> and answers with how it ended.
    /// The caller's token stopping is sent as <see cref="CancelRun"/>; the run still ends through
    /// the worker, in the words it has always ended in.
    /// </summary>
    public async Task<AgentResult> RunAsync(IRunWorker worker, StartRun start, CancellationToken ct = default)
    {
        var open = new Open(worker, start.Run);
        _runs[start.Run] = open;

        try
        {
            await worker.SendAsync(start, CancellationToken.None);

            RunEnded ended;
            using (ct.Register(() => _ = CancelAsync(open)))
            {
                ended = await open.Ended.Task;
            }

            if (ended.Fault is { } fault)
            {
                if (fault.Canceled) throw new OperationCanceledException(fault.Message, ct);
                throw new RunFaultException(fault);
            }

            return RunResults.Result(open.Output.ToString(), open.Usage, ended);
        }
        finally
        {
            _runs.TryRemove(new KeyValuePair<RunId, Open>(start.Run, open));
            open.Live?.Dispose();
        }
    }

    /// <summary>What one worker said. Called in <see cref="WorkerEnvelope.Seq"/> order.</summary>
    public async Task HandleAsync(WorkerEnvelope envelope, CancellationToken ct = default)
    {
        if (envelope.Event is not RunEvent { Run: var run } @event || !_runs.TryGetValue(run, out var open)) return;

        switch (@event)
        {
            case RunStarted:
                open.StartedSeq = envelope.Seq;
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
                open.Output.Append(output.Text);
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

    private static async Task CancelAsync(Open open)
    {
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

        public LiveRun? Live { get; set; }

        public long? StartedSeq { get; set; }

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
