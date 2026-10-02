using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Capacity;

namespace Harness.Host;

/// <summary>
/// THE WORKERS CONTROL PLACES RUNS ON, in order, each with the headroom gate its own capacity
/// samples feed. Admission (<see cref="WipLedger"/>) asks it which worker has room; the runner asks
/// it for the worker a run was placed on. There is one today, the Host's own.
/// </summary>
public sealed class WorkerPool(IReadOnlyList<(WorkerId Id, HeadroomGate Gate)> workers) : IRunPlacement
{
    private readonly Dictionary<WorkerId, IRunWorker> _connected = [];

    public IReadOnlyList<WorkerId> Workers { get; } = [.. workers.Select(w => w.Id)];

    public string? HeadroomReason(WorkerId worker) => Gate(worker).Reason();

    /// <summary>The headroom gate <paramref name="worker"/>'s capacity samples feed.</summary>
    public HeadroomGate Gate(WorkerId worker) => workers.First(w => w.Id == worker).Gate;

    /// <summary>The connection to one of this pool's workers, once it has one.</summary>
    public void Connect(IRunWorker worker)
    {
        lock (_connected) _connected[worker.Id] = worker;
    }

    /// <summary>The worker <paramref name="placed"/> names, or the first when a run was placed nowhere.</summary>
    public IRunWorker Worker(WorkerId? placed)
    {
        lock (_connected)
        {
            return placed is not null && _connected.TryGetValue(placed, out var worker)
                ? worker
                : _connected[Workers[0]];
        }
    }
}
