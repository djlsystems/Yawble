using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE RUN PROTOCOL INSIDE ONE PROCESS. A message is delivered by awaiting the other side's handler
/// in place, so a send completes once the worker has applied it and an event once control has
/// handled it - the order every direct call had before the boundary existed. Closing it completes
/// <see cref="Closed"/>; after that a send fails and an event goes nowhere, as it would on a
/// connection that dropped. Stream chunks and keystrokes are handed over by direct call too.
/// </summary>
public sealed class InProcessTransport(WorkerId id) : IRunWorker, IRunEvents, IRunStreamSink, IRunWorkerInput, IDisposable
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<ControlMessage, CancellationToken, Task>? _worker;
    private Func<WorkerEnvelope, CancellationToken, Task>? _control;
    private Action<WorkerId, StreamChunk>? _streams;
    private Action<StreamInput>? _input;

    public WorkerId Id => id;

    public Task Closed => _closed.Task;

    /// <summary>Joins the two ends: what applies control's messages, and what handles the worker's events.</summary>
    public void Connect(Func<ControlMessage, CancellationToken, Task> worker, Func<WorkerEnvelope, CancellationToken, Task> control)
    {
        _worker = worker;
        _control = control;
    }

    public Task SendAsync(ControlMessage message, CancellationToken ct = default)
    {
        if (_closed.Task.IsCompleted || _worker is not { } worker)
        {
            return Task.FromException(new InvalidOperationException($"The connection to worker {id} is closed."));
        }

        return worker(message, ct);
    }

    /// <summary>Joins the streams: what takes the worker's chunks on control's side, and what takes keystrokes on the worker's.</summary>
    public void ConnectStreams(Action<WorkerId, StreamChunk>? control, Action<StreamInput>? worker)
    {
        _streams = control;
        _input = worker;
    }

    public ValueTask<bool> StreamAsync(StreamChunk chunk, CancellationToken ct = default)
    {
        if (_closed.Task.IsCompleted || _streams is not { } streams) return ValueTask.FromResult(false);

        streams(id, chunk);
        return ValueTask.FromResult(true);
    }

    public ValueTask InputAsync(StreamInput input, CancellationToken ct = default)
    {
        if (!_closed.Task.IsCompleted) _input?.Invoke(input);
        return ValueTask.CompletedTask;
    }

    public Task PublishAsync(WorkerEnvelope envelope, CancellationToken ct = default) =>
        _closed.Task.IsCompleted || _control is not { } control ? Task.CompletedTask : control(envelope, ct);

    public void Dispose() => _closed.TrySetResult();
}
