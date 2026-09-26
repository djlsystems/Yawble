using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// One definition of "busy": running, or queued work, or accepted-but-unfinished deliveries.
/// </summary>
internal static class ContainerBusy
{
    public static bool IsBusy(AgentContainer container, IReadOnlyCollection<PendingDelivery> accepted) =>
        IsBusy(container.State, container.QueueDepth, accepted.Count);

    public static bool IsBusy(ContainerState state, int queueDepth, int acceptedCount) =>
        state == ContainerState.Running || queueDepth > 0 || acceptedCount > 0;
}
