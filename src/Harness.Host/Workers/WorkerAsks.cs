using System.Collections.Concurrent;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// CONTROL ASKS A WORKER ABOUT ITS AGENT CLIS, and waits for the answer: a sign-in probe, commands
/// run, a removal as the agent. The request goes to one worker - the connected one with the most
/// measured headroom, as a run placed now would go - and its answer is the event under the same
/// request id. No worker, a dropped worker (the send is refused at once), a worker lost while it
/// works, or no answer within the bound: the answer is null, with <see cref="Asked.Why"/> saying
/// which in a sentence. Nothing is ever taken as done that no worker did.
/// </summary>
public sealed class WorkerAsks(Func<IRunWorker> any, TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WorkerEvent>> _waiting = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>What no worker connected reads as, where a caller has nothing more particular to say.</summary>
    public const string NoWorker = "No worker is connected.";

    /// <summary>One answer, or why there is none; <see cref="Worker"/> is who was asked, null when nobody was.</summary>
    public sealed record Asked<T>(T? Answer, WorkerId? Worker, string? Why) where T : WorkerEvent;

    /// <summary>The worker a request goes to now, or null when none is connected.</summary>
    public IRunWorker? Pick()
    {
        try
        {
            return any();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Sends <paramref name="request"/> to the worker <see cref="Pick"/> names and waits for its answer.</summary>
    public Task<Asked<T>> AskAsync<T>(AgentCliCommand request, TimeSpan bound, CancellationToken ct = default) where T : WorkerEvent =>
        Pick() is { } worker
            ? AskAsync<T>(worker, request, bound, ct)
            : Task.FromResult(new Asked<T>(null, null, NoWorker));

    /// <summary>Sends <paramref name="request"/> to <paramref name="worker"/> and waits for its answer.</summary>
    public async Task<Asked<T>> AskAsync<T>(IRunWorker worker, AgentCliCommand request, TimeSpan bound, CancellationToken ct = default)
        where T : WorkerEvent
    {
        var answer = new TaskCompletionSource<WorkerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[request.Request] = answer;
        try
        {
            try
            {
                await worker.SendAsync(request, ct);
            }
            catch (InvalidOperationException)
            {
                return new Asked<T>(null, worker.Id, $"Worker {worker.Id} is not connected.");
            }

            var done = await Task.WhenAny(answer.Task, worker.Closed, Task.Delay(bound, _clock, ct));
            if (done == answer.Task && await answer.Task is T found) return new Asked<T>(found, worker.Id, null);

            return new Asked<T>(null, worker.Id, done == worker.Closed
                ? $"Worker {worker.Id} stopped before it answered."
                : $"Worker {worker.Id} did not answer.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Asked<T>(null, worker.Id, $"Worker {worker.Id} did not answer.");
        }
        finally
        {
            _waiting.TryRemove(request.Request, out _);
        }
    }

    /// <summary>An answer arrived: whoever waits for its request has it. One for nobody is dropped.</summary>
    public Task HandleAsync(WorkerEnvelope envelope, CancellationToken ct = default)
    {
        var request = envelope.Event switch
        {
            SignInProbed probed => probed.Request,
            AgentCommandsRan ran => ran.Request,
            RemovedAsAgent removed => removed.Request,
            _ => null,
        };

        if (request is not null && _waiting.TryGetValue(request, out var waiting)) waiting.TrySetResult(envelope.Event);
        return Task.CompletedTask;
    }

    /// <summary>A new request id.</summary>
    public static string NewRequest() => Guid.NewGuid().ToString("N");
}
