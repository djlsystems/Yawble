using Harness.Contracts;
using Harness.Host.Capacity;
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
    private readonly ILogger? _log;

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
        ILogger? log = null)
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
        _log = log;
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

            default:
                throw new NotSupportedException($"A worker does not take {message.GetType().Name}.");
        }
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

        try
        {
            result = await _launcher.RunAsync(open.Start, open, open.Stop.Token);
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
            await EndAsync(open, result, fault);
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

    /// <summary>The run's output, usage and end, with it leaving the open set, as one publish.</summary>
    private async Task EndAsync(OpenRun open, AgentResult? result, RunFault? fault)
    {
        var run = open.Start.Run;

        await _publish.WaitAsync(CancellationToken.None);
        try
        {
            RunEnded ended;
            if (result is null)
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
            Dictionary<ContainerId, RunId> byMember;
            lock (_runsGate) byMember = _runs.Keys.GroupBy(r => r.Member).ToDictionary(g => g.Key, g => g.First());
            IReadOnlyList<RunId> open = [.. byMember.Values];

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

        public string? StderrTail { get; private set; }

        private RunId Run => start.Run;

        public void Started(int processId) =>
            worker.PublishAsync(new RunStarted(Run, processId, worker._clock.GetUtcNow())).GetAwaiter().GetResult();

        public void LiveViewBegun(string? transcript, string? format, string reason, bool finding) =>
            worker.PublishAsync(new RunLiveViewChanged(Run, transcript, format, reason, finding)).GetAwaiter().GetResult();

        public Task LiveViewFoundAsync(string? transcript, string? format, string reason) =>
            worker.PublishAsync(new RunLiveViewChanged(Run, transcript, format, reason, Finding: false));

        public Task ProgressAsync(string sentence) => worker.PublishAsync(new RunProgress(Run, sentence));

        public Task ChildStoppedAsync(string sentence, RunMemoryLimit limit) =>
            worker.PublishAsync(new RunChildStoppedByMemoryLimit(Run, sentence, new MemoryFigure(limit.Mb, limit.Source, limit.Set)));

        public Task DiagnosticAsync(
            DiagnosticSeverity severity, string kind, string source, string? message = null, string? detail = null,
            string? exceptionType = null) =>
            worker.PublishAsync(new RunDiagnostic(Run, severity, kind, source, message, detail, exceptionType));

        public void Stderr(string tail) => StderrTail = tail.Length == 0 ? null : tail;
    }
}
