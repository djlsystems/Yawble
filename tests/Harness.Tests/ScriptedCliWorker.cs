using System.Collections.Concurrent;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A worker that answers control's agent CLI requests the way the test says, and records every message
/// it was sent. Its answer is handed to <see cref="Asks"/> as the worker's event would be; no answer,
/// a refused send (<see cref="Dropped"/>) or a lost worker (<see cref="Lose"/>) are the test's to choose.
/// </summary>
internal sealed class ScriptedCliWorker(string id, Func<ControlMessage, WorkerEvent?>? answer = null) : IRunWorker, IRunWorkerConnection
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _seq;

    public WorkerId Id { get; } = new(id);

    public Task Closed => _closed.Task;

    public bool Dropped { get; set; }

    public string? Lost { get; private set; }

    /// <summary>Where answers go: control's correlator.</summary>
    public WorkerAsks? Asks { get; set; }

    public ConcurrentQueue<ControlMessage> Sent { get; } = new();

    /// <summary>Completes on each message as it is sent.</summary>
    public event Action<ControlMessage>? Received;

    public async Task SendAsync(ControlMessage message, CancellationToken ct = default)
    {
        if (Dropped) throw new InvalidOperationException($"Worker {Id} is not connected.");
        if (_closed.Task.IsCompleted) throw new InvalidOperationException($"The connection to worker {Id} is closed.");

        Sent.Enqueue(message);
        Received?.Invoke(message);

        if (answer?.Invoke(message) is { } @event && Asks is { } asks)
        {
            await asks.HandleAsync(new WorkerEnvelope(Id, Interlocked.Increment(ref _seq), @event), ct);
        }
    }

    /// <summary>Answers later, by hand: what a worker still working says when it is done.</summary>
    public Task AnswerAsync(WorkerEvent @event) =>
        Asks!.HandleAsync(new WorkerEnvelope(Id, Interlocked.Increment(ref _seq), @event));

    public void Lose(string why)
    {
        Lost = why;
        _closed.TrySetResult();
    }
}
