using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// CONTROL'S ONE HANDLE ON EVERY CONNECTED WORKER, for the parts of control that talk to "the
/// worker" - the leases, the reports, the runner, the plugin runner. A run's messages go to the
/// worker its member was placed on (<see cref="WipLedger.PlacedOn"/>); a member with no placement has
/// no run to tell, so a report or a lease's hold for it goes nowhere; a lease move
/// (<see cref="ChangeRunMemoryAllowance"/>) goes to every worker, each of which raises or lowers its
/// own runs. It never closes: each worker's own connection is what a run watches.
/// </summary>
public sealed class RunWorkers(WorkerPool pool, Func<ContainerId, WorkerId?> placedOn) : IRunWorker, IRunWorkerRouter
{
    private readonly TaskCompletionSource _never = new();

    public WorkerId Id { get; } = new("control");

    public Task Closed => _never.Task;

    public IRunWorker For(ContainerId member) => pool.Worker(placedOn(member));

    public async Task SendAsync(ControlMessage message, CancellationToken ct = default)
    {
        switch (message)
        {
            case RunCommand command:
                await For(command.Run.Member).SendAsync(message, ct);
                break;

            case MemberCommand command:
                if (pool.For(placedOn(command.Member)) is { } worker) await worker.SendAsync(message, ct);
                break;

            default:
                await Task.WhenAll(pool.Connected().Select(w => Quietly(w.SendAsync(message, ct))));
                break;
        }
    }

    /// <summary>A worker that cannot be told now is told when it is back, or is gone with its runs.</summary>
    private static async Task Quietly(Task send)
    {
        try
        {
            await send;
        }
        catch (InvalidOperationException)
        {
        }
    }
}
