using Harness.Contracts;
using Harness.Host.Capacity;
using Harness.Pty;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// A RUNTIME WORKER: applies what control sends and says what its runs do, as run protocol records.
/// A start runs the launch on its own task; a cancel stops it; a lease move raises or lowers the
/// runs' memory allowances; a report pushes a run's idle clock out; a sample measures each run's
/// process group and then the worker's own cgroup.
/// </summary>
/// <remarks>
/// <para>
/// ONE PUBLISH AT A TIME, AND IN <see cref="WorkerEnvelope.Seq"/> ORDER. Assigning a sequence
/// number and delivering it happen together, under one lock; so do a sample's list of open runs
/// with the measurements it publishes, and a run leaving the open set with its
/// <see cref="RunEnded"/>. A sample that comes after a run's start and does not list it therefore
/// means the end was lost on the way, never that it raced the sample: control's lost-run
/// detection rests on this. Control's handlers never wait for a publish of this worker, so the
/// lock cannot deadlock.
/// </para>
/// <para>
/// A run joins the open set when its start is applied, outside the publish lock, so control may
/// send a start from inside a handler.
/// </para>
/// </remarks>
public sealed class WorkerHost
{
    private readonly WorkerId _id;
    private readonly IRunEvents _events;
    private readonly RunLauncher _launcher;
    private readonly RunHeartbeat _heartbeat;
    private readonly RunAllowances? _allowances;
    private readonly CgroupReader? _cgroup;
    private readonly ProcessGroupReader? _processes;
    private readonly RunProcessGroups _groups;
    private readonly TimeProvider _clock;
    private readonly WorkerAgentCli? _cli;
    private readonly ILogger? _log;
    private readonly WorkerTerminals? _terminals;
    private readonly WorkerTranscripts? _transcripts;

    private readonly SemaphoreSlim _publish = new(1, 1);
    private long _seq;

    private readonly Lock _runsGate = new();
    private readonly Dictionary<RunId, OpenRun> _runs = [];
    private volatile IReadOnlyList<string> _heavy = [];

    public WorkerHost(
        WorkerId id,
        IRunEvents events,
        RunLauncher launcher,
        RunHeartbeat heartbeat,
        RunAllowances? allowances = null,
        CgroupReader? cgroup = null,
        ProcessGroupReader? processes = null,
        RunProcessGroups? groups = null,
        TimeProvider? clock = null,
        WorkerAgentCli? cli = null,
        ILogger? log = null,
        WorkerStreaming? streaming = null)
    {
        _id = id;
        _events = events;
        _launcher = launcher;
        _heartbeat = heartbeat;
        _allowances = allowances;
        _cgroup = cgroup;
        _processes = processes;
        _groups = groups ?? RunProcessGroups.Shared;
        _clock = clock ?? TimeProvider.System;
        _cli = cli;
        _log = log;

        if (streaming is not null)
        {
            _terminals = new WorkerTerminals(streaming.Sink, streaming.Pty, streaming.RunAs, e => PublishAsync(e), log, _clock);
            _transcripts = new WorkerTranscripts(
                streaming.Sink, streaming.RunAs, e => PublishAsync(e), RunEnded, streaming.Environment, log, _clock);
        }
    }

    public WorkerId Id => _id;

    /// <summary>The launch this worker runs members with; the launch check asks it directly.</summary>
    public RunLauncher Launcher => _launcher;

    /// <summary>Who holds the <c>heavy</c> lease, as control last said: what this worker's allowances read.</summary>
    public IReadOnlyCollection<string> HeavyHolders => _heavy;

    /// <summary>The runs this worker has started and not yet ended.</summary>
    public IReadOnlyList<RunId> OpenRuns()
    {
        lock (_runsGate) return [.. _runs.Keys];
    }

    /// <summary>Says this worker takes runs. Sent once, when the connection opens.</summary>
    public Task ReadyAsync(CancellationToken ct = default) => PublishAsync(new WorkerReady(_id), ct);

    /// <summary>Applies one message. Completes once it has been applied.</summary>
    public async Task ApplyAsync(ControlMessage message, CancellationToken ct = default)
    {
        switch (message)
        {
            case AgentCliCommand request:
                // On its own task, as a launch check is: the answer is an event, under the request's id.
                (_cli ?? throw new NotSupportedException($"Worker {_id} answers no {request.GetType().Name}."))
                    .Apply(request, answer => PublishAsync(answer));
                break;

            case StartRun start:
                Start(start);
                break;

            case CancelRun cancel:
                OpenRun? open;
                lock (_runsGate) open = _runs.GetValueOrDefault(cancel.Run);
                if (open is not null) await open.Stop.CancelAsync();
                break;

            case ChangeRunMemoryAllowance change:
                _heavy = [.. change.HeavyHolders];
                if (_allowances is not null) await _allowances.ReconcileAsync(CancellationToken.None);
                break;

            case HoldIdleClock hold:
                _heartbeat.Hold(hold.Member, hold.Held);
                break;

            case TouchIdleClock touch:
                _heartbeat.Touch(touch.Member);
                break;

            case SampleCapacity:
                await SampleAsync(ct);
                break;

            case CheckLaunch check:
                // On its own task, as a run is: the answer is an event, under the check's request.
                _ = Task.Run(() => CheckAsync(check), CancellationToken.None);
                break;

            case TerminalCommand command:
                await ApplyAsync(command);
                break;

            default:
                throw new NotSupportedException($"A worker does not take {message.GetType().Name}.");
        }
    }

    /// <summary>
    /// A terminal's or a file's command. A start spawns at once and throws when it cannot; a follow, a
    /// read and a stop run on their own task, so nothing behind them waits.
    /// </summary>
    private async Task ApplyAsync(TerminalCommand command)
    {
        if (_terminals is null || _transcripts is null)
        {
            throw new NotSupportedException($"This worker has no terminals and reads no files ({command.GetType().Name}).");
        }

        switch (command)
        {
            case StartTerminal start:
                await _terminals.StartAsync(start);
                break;

            case ResizeTerminal resize:
                _terminals.Resize(resize);
                break;

            case StopTerminal stop:
                _ = Task.Run(() => _terminals.StopAsync(stop.Session), CancellationToken.None);
                break;

            case FollowTranscript follow:
                _transcripts.Follow(follow);
                break;

            case StopStream stop:
                _transcripts.Stop(stop.Stream, stop.RunEnded);
                break;

            case ReadAgentFile read:
                _transcripts.Read(read);
                break;

            default:
                throw new NotSupportedException($"A worker does not take {command.GetType().Name}.");
        }
    }

    /// <summary>A person's keystrokes for a terminal on this worker, typed as they arrive.</summary>
    public void Input(StreamInput input) => _terminals?.Input(input);

    /// <summary>The terminals open on this worker.</summary>
    public IReadOnlyCollection<string> Terminals => _terminals?.Sessions ?? [];

    /// <summary>Ends every terminal and every follow: the worker is stopping.</summary>
    public async Task StopStreamsAsync()
    {
        _transcripts?.StopAll();
        if (_terminals is not null) await _terminals.StopAllAsync();
    }

    /// <summary>
    /// Fires when <paramref name="run"/> ends on this worker. A run this worker does not have open never
    /// fires here: control says when it ended.
    /// </summary>
    private CancellationToken RunEnded(RunId run)
    {
        lock (_runsGate) return _runs.GetValueOrDefault(run)?.Ended.Token ?? CancellationToken.None;
    }

    private void Start(StartRun start)
    {
        var open = new OpenRun(this, start);
        lock (_runsGate)
        {
            if (!_runs.TryAdd(start.Run, open)) throw new InvalidOperationException($"Run {start.Run} is already open.");
        }

        _ = Task.Run(() => RunAsync(open));
    }

    private async Task RunAsync(OpenRun open)
    {
        AgentResult? result = null;
        RunFault? fault = null;
        RunEnded? processEnd = null;

        try
        {
            if (open.Start.Process is { } process)
            {
                var (exitCode, processId, outcome) = await _launcher.RunProcessAsync(open.Start, process, open, open.Stop.Token);
                processEnd = new RunEnded(open.Start.Run, exitCode, processId, null, null, null, null, null, Process: outcome);
            }
            else
            {
                result = await _launcher.RunAsync(open.Start, open, open.Stop.Token);
            }
        }
        catch (OperationCanceledException exception)
        {
            fault = new RunFault(true, exception.GetType().FullName!, exception.Message);
        }
        catch (Exception exception)
        {
            fault = new RunFault(false, exception.GetType().FullName!, exception.Message);
        }

        try
        {
            await EndAsync(open, result, fault, processEnd);
        }
        catch (Exception exception)
        {
            _log?.LogWarning("Run {Run} ended, and saying so failed: {Message}", open.Start.Run, exception.Message);
        }
        finally
        {
            open.Stop.Dispose();
        }
    }

    /// <summary>Runs a launch check with this worker's own launch and says what it found.</summary>
    private async Task CheckAsync(CheckLaunch check)
    {
        AgentLaunchReport report;
        try
        {
            report = await _launcher.CheckLaunchAsync(
                check.Launch, check.Check, check.UpdateArguments, check.Environment, check.Memory, CancellationToken.None,
                check.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, check.Credential, check.Redaction, check.TempRoot);
        }
        catch (Exception exception)
        {
            report = AgentLaunchReport.Unchecked($"The launch check failed on worker {_id}: {exception.Message}");
        }

        try
        {
            await PublishAsync(new LaunchChecked(check.Request, report.Result, report.ExitCode, report.StderrTail, report.Detail));
        }
        catch (Exception exception)
        {
            _log?.LogWarning("A launch check ended, and saying so failed: {Message}", exception.Message);
        }
    }

    /// <summary>The run's output, usage and end, with it leaving the open set, as one publish.</summary>
    private async Task EndAsync(OpenRun open, AgentResult? result, RunFault? fault, RunEnded? processEnd)
    {
        var run = open.Start.Run;

        await _publish.WaitAsync(CancellationToken.None);
        try
        {
            RunEnded ended;
            if (processEnd is not null)
            {
                // A program's output crossed line by line as it ran.
                ended = processEnd;
            }
            else if (result is null)
            {
                ended = new RunEnded(run, -1, null, null, null, null, open.StderrTail, null, fault);
            }
            else
            {
                var (output, usage, end) = RunResults.Events(run, result, open.StderrTail);
                await DeliverLockedAsync(output);
                if (usage is not null) await DeliverLockedAsync(usage);
                ended = end;
            }

            lock (_runsGate) _runs.Remove(run);
            open.Ended.Cancel();
            await DeliverLockedAsync(ended);
        }
        finally
        {
            _publish.Release();
        }
    }

    /// <summary>Each open run's process groups, then the worker's cgroup with the runs it has open.</summary>
    private async Task SampleAsync(CancellationToken ct)
    {
        await _publish.WaitAsync(ct);
        try
        {
            // The cgroup first, then the process groups: the order the capacity sample always read them in.
            var at = _clock.GetUtcNow();
            var cgroup = _cgroup?.Read() ?? WorkerCapacity.NotMeasured;
            // Every open run is listed; a process group is registered by member, so its measurement
            // is charged to one open run of that member.
            IReadOnlyList<RunId> open;
            lock (_runsGate) open = [.. _runs.Keys];
            var byMember = open.GroupBy(r => r.Member).ToDictionary(g => g.Key, g => g.First());

            if (_processes is not null)
            {
                var registered = _groups.Snapshot();
                var figures = _processes.Read(registered.Keys.ToArray());
                foreach (var (group, member) in registered)
                {
                    if (figures.GetValueOrDefault(group) is not { } measured) continue;

                    var owner = byMember.GetValueOrDefault(member) ?? new RunId(member, "");
                    await DeliverLockedAsync(new RunMeasured(
                        owner, group, measured.Processes, measured.ResidentBytes, measured.CpuTicks, at));
                }
            }

            await DeliverLockedAsync(new WorkerCapacitySampled(at, WorkerCapacity.ToFigures(cgroup), open));
        }
        finally
        {
            _publish.Release();
        }
    }

    private async Task PublishAsync(WorkerEvent @event, CancellationToken ct = default)
    {
        await _publish.WaitAsync(ct);
        try
        {
            await DeliverLockedAsync(@event);
        }
        finally
        {
            _publish.Release();
        }
    }

    private Task DeliverLockedAsync(WorkerEvent @event) =>
        _events.PublishAsync(new WorkerEnvelope(_id, ++_seq, @event));

    /// <summary>One run in flight: its start, its stop, and what it says while it runs.</summary>
    private sealed class OpenRun(WorkerHost worker, StartRun start) : IRunSink
    {
        public StartRun Start => start;

        public CancellationTokenSource Stop { get; } = new();

        /// <summary>Fired when the run has left the open set: what a followed transcript stops on.</summary>
        public CancellationTokenSource Ended { get; } = new();

        public string? StderrTail { get; private set; }

        private RunId Run => start.Run;

        public void Started(int processId) =>
            worker.PublishAsync(new RunStarted(Run, processId, worker._clock.GetUtcNow())).GetAwaiter().GetResult();

        public void LiveViewBegun(string? transcript, string? format, string reason, bool finding) =>
            worker.PublishAsync(new RunLiveViewChanged(Run, transcript, format, reason, finding)).GetAwaiter().GetResult();

        public Task LiveViewFoundAsync(string? transcript, string? format, string reason) =>
            worker.PublishAsync(new RunLiveViewChanged(Run, transcript, format, reason, Finding: false));

        public Task ProgressAsync(string sentence) => worker.PublishAsync(new RunProgress(Run, sentence));

        public Task CredentialAppliedAsync(ValueRedactor redaction) => worker.PublishAsync(new RunCredentialApplied(Run, redaction));

        public Task ChildStoppedAsync(string sentence, RunMemoryLimit limit) =>
            worker.PublishAsync(new RunChildStoppedByMemoryLimit(Run, sentence, new MemoryFigure(limit.Mb, limit.Source, limit.Set)));

        public Task DiagnosticAsync(
            DiagnosticSeverity severity, string kind, string source, string? message = null, string? detail = null,
            string? exceptionType = null) =>
            worker.PublishAsync(new RunDiagnostic(Run, severity, kind, source, message, detail, exceptionType));

        public void Stderr(string tail) => StderrTail = tail.Length == 0 ? null : tail;

        public Task OutputLineAsync(string line) => worker.PublishAsync(new RunOutput(Run, line));
    }
}

/// <summary>
/// What a worker needs for terminals and the agent's files: where its stream chunks go, the engine a
/// terminal is spawned with, who the agent runs as, and (for a test) the environment its own
/// credential variables are read from.
/// </summary>
public sealed record WorkerStreaming(
    IRunStreamSink Sink, IPtyEngine Pty, AgentLaunchUser? RunAs, Func<IDictionary<string, string?>>? Environment = null);
