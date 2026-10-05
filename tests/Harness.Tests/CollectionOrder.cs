using Xunit.Sdk;
using Xunit.v3;

[assembly: TestCollectionOrderer(typeof(Harness.Tests.CollectionOrder))]

namespace Harness.Tests;

/// <summary>
/// THE WORKER-PROCESS TESTS START FIRST. Their collection runs one class at a time for about three
/// minutes; started first, it runs beside the parallel classes instead of after them, where the
/// default order (random, run to run) often put it, running alone. Every other collection keeps the
/// default order.
/// </summary>
public sealed class CollectionOrder : ITestCollectionOrderer
{
    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(
        IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : ITestCollection
    {
        var ordered = DefaultTestCollectionOrderer.Instance.OrderTestCollections(testCollections);

        return [.. ordered.Where(c => IsWorkerProcesses(c)), .. ordered.Where(c => !IsWorkerProcesses(c))];
    }

    private static bool IsWorkerProcesses(ITestCollection collection) =>
        collection.TestCollectionDisplayName == "worker processes";
}
