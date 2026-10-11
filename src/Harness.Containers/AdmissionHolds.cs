namespace Harness.Containers;

/// <summary>
/// Where <see cref="WipLedger"/> reports each admission hold it opens and ends. Called under the
/// ledger's lock, so an implementation only takes note and returns: no I/O, no waiting. Admission
/// never depends on it - a call that throws is passed over.
/// </summary>
public interface IAdmissionHolds
{
    /// <summary>A waiter was held at <paramref name="at"/> for one reason (a kind from
    /// <see cref="Contracts.AdmissionHoldKinds"/>, and its sentence).</summary>
    void Held(string team, string member, long? deliverySeq, DateTimeOffset at, string reasonKind, string reason);

    /// <summary>The member's hold ended at <paramref name="at"/>: it started, withdrew, or its reason
    /// changed kind (a new hold follows at once).</summary>
    void Released(string team, string member, DateTimeOffset at);
}
