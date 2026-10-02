using System.Net.WebSockets;
using Harness.Containers;
using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// CONTROL'S SIDE OF EVERY WORKER CONNECTION. Takes a worker's WebSocket, reads its hello, and
/// either refuses it with a sentence - another build's version, a name that is connected, a Host that
/// runs its runs itself, no hello in time - or welcomes it: a worker it has not seen joins the pool,
/// a worker back on the same session within its grace carries on, and a worker back as a new process
/// loses what the old one had at once. On every welcome the runs the worker still holds are compared
/// with control's: a run control already ended is stopped, so no run exists twice.
/// </summary>
public sealed class WorkerConnections
{
    private readonly WorkerPool _pool;
    private readonly WipLedger _wip;
    private readonly Func<WorkerEnvelope, CancellationToken, Task> _control;
    private readonly Func<WorkerId, IReadOnlyCollection<RunId>> _openOn;
    private readonly string? _workerKey;
    private readonly bool _takesWorkers;
    private readonly string _version;
    private readonly Func<WorkerInfo, RunWorkerSettings> _settings;
    private readonly WorkerTimings _timings;
    private readonly TimeProvider _clock;
    private readonly IDiagnosticsLog? _diagnostics;
    private readonly ILogger? _log;
    private readonly Action<WorkerId, StreamChunk>? _streams;
    private readonly Lock _gate = new();
    private readonly Dictionary<WorkerId, RemoteWorker> _workers = [];

    public WorkerConnections(
        WorkerPool pool,
        WipLedger wip,
        Func<WorkerEnvelope, CancellationToken, Task> control,
        Func<WorkerId, IReadOnlyCollection<RunId>> openOn,
        string? workerKey,
        bool takesWorkers,
        string version,
        Func<WorkerInfo, RunWorkerSettings>? settings = null,
        WorkerTimings? timings = null,
        TimeProvider? clock = null,
        IDiagnosticsLog? diagnostics = null,
        ILogger? log = null,
        Action<WorkerId, StreamChunk>? streams = null)
    {
        _pool = pool;
        _wip = wip;
        _control = control;
        _openOn = openOn;
        _workerKey = string.IsNullOrWhiteSpace(workerKey) ? null : workerKey.Trim();
        _takesWorkers = takesWorkers;
        _version = version;
        _settings = settings ?? (_ => RunWorkerSettings.None);
        _timings = timings ?? WorkerTimings.Default;
        _clock = clock ?? TimeProvider.System;
        _diagnostics = diagnostics;
        _log = log;
        _streams = streams;
    }

    /// <summary>Raised when a worker joins or goes, after the pool and the ledger have heard.</summary>
    public event Action? Changed;

    /// <summary>Raised when a worker joins as a new session - not when one comes back on its own - after <see cref="Changed"/>.</summary>
    public event Action<WorkerId>? Joined;

    /// <summary>The workers connected or dropped and within their grace.</summary>
    public IReadOnlyList<RemoteWorker> Workers()
    {
        lock (_gate) return [.. _workers.Values];
    }

    /// <summary>Control is stopping: every worker's timers stop, and nothing more is said about any of them.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopping = true;
            foreach (var worker in _workers.Values) worker.Shutdown();
        }
    }

    private bool _stopping;

    /// <summary>Takes one worker's connection and runs it until it ends.</summary>
    public async Task AcceptAsync(WebSocket webSocket, CancellationToken ct)
    {
        using var socket = new WorkerSocket(webSocket, new WorkerFrameCodec(null));

        WorkerHello? hello;
        try
        {
            hello = await socket.ReceiveAsync(ct).WaitAsync(_timings.Hello, _clock, ct) as WorkerHello;
        }
        catch (TimeoutException)
        {
            await RefuseAsync(socket, null, WorkerSentences.NoHello, sendRefusal: false);
            socket.Abort();
            return;
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or InvalidDataException or OperationCanceledException)
        {
            _log?.LogWarning("A worker connection ended before its hello: {Message}", exception.Message);
            return;
        }

        if (hello is null)
        {
            await RefuseAsync(socket, null, "A worker's first message must be its hello.");
            return;
        }

        if (!_takesWorkers || _workerKey is null)
        {
            await RefuseAsync(socket, hello.Worker, WorkerSentences.NotControl);
            return;
        }

        if (!string.Equals(hello.Version, _version, StringComparison.Ordinal))
        {
            await RefuseAsync(socket, hello.Worker, WorkerSentences.OtherVersion(hello.Version, _version));
            return;
        }

        RemoteWorker? remote = null;
        RemoteWorker? replaced = null;
        DateTimeOffset? takenSince = null;
        var back = false;
        lock (_gate)
        {
            var existing = _workers.GetValueOrDefault(hello.Worker);
            if (existing is { Gone: false, Dropped: false })
            {
                takenSince = existing.Info.ConnectedSince;
            }
            else if (existing is { Gone: false, Dropped: true } && existing.Session == hello.Session)
            {
                remote = existing;
                back = true;
            }
            else
            {
                replaced = existing is { Gone: false } ? existing : null;
                remote = new RemoteWorker(
                    new WorkerInfo(hello.Worker, hello.Version, hello.Capacity.Cpus, hello.Capacity.MemoryLimitBytes, _clock.GetUtcNow()),
                    hello.Session, _control, _timings, Dropped, Gone, _clock, _log, _streams);
                remote.DrainingChanged += DrainingChanged;
                _workers[hello.Worker] = remote;
            }
        }

        if (remote is null)
        {
            await RefuseAsync(socket, hello.Worker, WorkerSentences.NameTaken(hello.Worker, takenSince!.Value));
            return;
        }

        // The old process's runs are gone with it, before the new one is placed on.
        replaced?.LoseAsRestarted();

        var nonce = WorkerFrameCodec.NewNonce();
        socket.Codec = new WorkerFrameCodec(WorkerFrameCodec.Key(_workerKey, nonce, hello.Nonce));

        try
        {
            await socket.SendAsync(new WorkerWelcome(nonce, remote.HandledSeq, _settings(remote.Info)), ct);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or OperationCanceledException)
        {
            _log?.LogWarning("Worker {Worker} went before its welcome: {Message}", hello.Worker, exception.Message);
            if (!back) await RunToEndAsync(remote, socket, ct);
            return;
        }

        if (back)
        {
            _pool.Back(remote.Id);
        }
        else
        {
            _pool.Join(remote, remote.Info);
        }

        // A drain the worker was asked for survives its reconnect: its hello says so.
        remote.Draining = hello.Draining;
        _pool.Drain(remote.Id, hello.Draining);

        _log?.LogInformation("Worker {Worker} {How} (version {Version}, session {Session}).", remote.Id, back ? "is back" : "connected", hello.Version, hello.Session);
        _wip.WorkersChanged();
        Changed?.Invoke();
        if (!back) Joined?.Invoke(remote.Id);

        await StopStaleAsync(remote, hello.OpenRuns);
        await RunToEndAsync(remote, socket, ct);
    }

    private static Task RunToEndAsync(RemoteWorker remote, WorkerSocket socket, CancellationToken ct) => remote.RunAsync(socket, ct);

    /// <summary>Every run the worker still holds that control no longer has open is stopped, and said.</summary>
    private async Task StopStaleAsync(RemoteWorker remote, IReadOnlyList<RunId> held)
    {
        var open = _openOn(remote.Id);
        foreach (var run in held.Where(run => !open.Contains(run)))
        {
            var sentence = $"Stopped run {run.Key} that worker {remote.Id} still held after control had ended it.";
            _log?.LogWarning("{Sentence}", sentence);
            if (_diagnostics is not null)
            {
                await _diagnostics.WriteAsync(
                    DiagnosticSeverity.Warning, DiagnosticKinds.WorkerStaleRunStopped, DiagnosticSources.Worker,
                    message: sentence, ct: CancellationToken.None);
            }

            try
            {
                await remote.SendAsync(new CancelRun(run));
            }
            catch (InvalidOperationException)
            {
                // Gone again: the run went with it.
            }
        }
    }

    /// <summary>
    /// Tells every connected worker the figures it runs by, now: a setting they are built from changed.
    /// A worker not connected at this moment gets them in its next welcome.
    /// </summary>
    public async Task SettingsChangedAsync()
    {
        foreach (var worker in Workers().Where(w => !w.Gone && !w.Dropped))
        {
            await worker.SendSettingsAsync(_settings(worker.Info));
        }
    }

    private void DrainingChanged(RemoteWorker remote)
    {
        lock (_gate)
        {
            if (_stopping || !_workers.TryGetValue(remote.Id, out var current) || !ReferenceEquals(current, remote)) return;
        }

        _pool.Drain(remote.Id, remote.Draining);
        _log?.LogInformation(remote.Draining
            ? "Worker {Worker} is draining: nothing new is placed on it, and its runs go on."
            : "Worker {Worker} is no longer draining.", remote.Id);
        Changed?.Invoke();
    }

    private void Dropped(RemoteWorker remote)
    {
        lock (_gate)
        {
            if (_stopping) return;
        }

        _pool.Drop(remote.Id);
        var sentence = $"Worker {remote.Id}'s connection dropped; it has {_timings.Grace.TotalSeconds:0} s to come back before its runs fail worker-lost.";
        _log?.LogWarning("{Sentence}", sentence);
        _ = _diagnostics?.WriteAsync(
            DiagnosticSeverity.Warning, DiagnosticKinds.WorkerDropped, DiagnosticSources.Worker, message: sentence, ct: CancellationToken.None);
    }

    private void Gone(RemoteWorker remote)
    {
        lock (_gate)
        {
            if (_stopping) return;
            if (_workers.TryGetValue(remote.Id, out var current) && ReferenceEquals(current, remote)) _workers.Remove(remote.Id);
        }

        _pool.Leave(remote);
        var sentence = $"Worker {remote.Id} is gone: {remote.Lost}";
        _log?.LogWarning("{Sentence}", sentence);
        _ = _diagnostics?.WriteAsync(
            DiagnosticSeverity.Warning, DiagnosticKinds.WorkerDropped, DiagnosticSources.Worker, message: sentence, ct: CancellationToken.None);
        _wip.WorkersChanged();
        Changed?.Invoke();
    }

    private async Task RefuseAsync(WorkerSocket socket, WorkerId? worker, string sentence, bool sendRefusal = true)
    {
        _log?.LogWarning("Refused worker {Worker}: {Sentence}", worker?.Value ?? "(no hello)", sentence);
        if (_diagnostics is not null)
        {
            await _diagnostics.WriteAsync(
                DiagnosticSeverity.Warning, DiagnosticKinds.WorkerRefused, DiagnosticSources.Worker,
                message: worker is null ? sentence : $"Worker {worker}: {sentence}", ct: CancellationToken.None);
        }

        if (sendRefusal)
        {
            try
            {
                await socket.SendAsync(new WorkerRefused(sentence));
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or OperationCanceledException)
            {
                // Gone already: the close below says nothing more.
            }
        }

        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, sentence);
    }
}
