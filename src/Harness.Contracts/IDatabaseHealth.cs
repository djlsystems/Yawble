namespace Harness.Contracts;

/// <summary>
/// WHETHER THE DATABASE ANSWERS, the one check of <c>/healthz</c> that depends on the provider. The
/// data-root and pump checks stay in the Host and are shared by every provider, so the three cannot
/// disagree about what "can serve" means.
/// </summary>
public interface IDatabaseHealth
{
    /// <summary>True when the database opens and answers a trivial query. False, never a throw, when it
    /// cannot: a probe reports a failure, it does not create or repair anything.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}
