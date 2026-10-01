namespace Harness.Host.Capacity;

/// <summary>
/// THE SEAM FOR THE <c>heavy</c> LEASE'S HOLDERS AND QUEUE in the capacity sample. The lease itself
/// (the <c>lease</c> tool, instance-wide, at most <c>leases.heavy.holders</c> at once) registers its
/// own implementation as a singleton; until one is registered, <see cref="NoHeavyLease"/> answers null
/// and the sample's <c>heavyLease</c> is null, which a reader shows as "not available", never as
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

/// <summary><c>Holders</c> is how many may hold it at once; <c>Queued</c> is in order, position 1 first.</summary>
public sealed record HeavyLeaseSnapshot(
    int Holders, IReadOnlyList<LeaseParty> Holding, IReadOnlyList<LeaseParty> Queued);

/// <summary>One holder of, or waiter for, the lease. <c>Since</c> is when it took it or joined the queue.</summary>
public sealed record LeaseParty(string Team, string Member, DateTimeOffset Since);
