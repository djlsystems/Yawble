namespace Harness.Host.Capacity;

/// <summary>
/// THE SEAM FOR THE <c>heavy</c> LEASE'S HOLDERS AND QUEUE in the capacity sample. The lease itself
/// (the <c>lease</c> tool, instance-wide, at most <c>leases.heavy.holders</c> at once) is read through
/// <see cref="HeavyLeaseFromLeases"/>. Where no lease is registered, <see cref="NoHeavyLease"/> answers
/// null and the sample's <c>heavyLease</c> is null, which a reader shows as "not available", never as
/// "nobody holds it".
/// </summary>
public interface IHeavyLeaseView
{
    /// <summary>Who holds the <c>heavy</c> lease and who is queued for it, now; null when there is no lease.</summary>
    HeavyLeaseSnapshot? Read();
}

/// <summary>Nothing registered a lease.</summary>
public sealed class NoHeavyLease : IHeavyLeaseView
{
    public HeavyLeaseSnapshot? Read() => null;
}

/// <summary>
/// The instance's leases (<see cref="ILeaseState"/>) as the sample sees them: the <c>heavy</c> lease's
/// limit, holders and queue, read under the lease's own lock on every sample. Null only when no lease
/// of that name exists.
/// </summary>
public sealed class HeavyLeaseFromLeases(ILeaseState leases) : IHeavyLeaseView
{
    public HeavyLeaseSnapshot? Read()
    {
        var heavy = leases.Leases().FirstOrDefault(l => l.Name == InstanceLeases.Heavy);
        return heavy is null
            ? null
            : new HeavyLeaseSnapshot(heavy.Limit, heavy.Holders.Select(Party).ToList(), heavy.Queue.Select(Party).ToList());
    }

    private static LeaseParty Party(LeaseHolding h) => new(h.Team, h.Member, h.Since);
}

/// <summary><c>Holders</c> is how many may hold it at once; <c>Queued</c> is in order, position 1 first.</summary>
public sealed record HeavyLeaseSnapshot(
    int Holders, IReadOnlyList<LeaseParty> Holding, IReadOnlyList<LeaseParty> Queued);

/// <summary>
/// One holder of, or waiter for, the lease. <c>Team</c> is null for the Concierge, which has none;
/// <c>Since</c> is when it took it or joined the queue.
/// </summary>
public sealed record LeaseParty(string? Team, string Member, DateTimeOffset Since);
