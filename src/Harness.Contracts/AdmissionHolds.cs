namespace Harness.Contracts;

/// <summary>What a hold waited for, in one word: the run limit, memory in use, memory pressure, or
/// no worker connected.</summary>
public static class AdmissionHoldKinds
{
    public const string Slot = "slot";
    public const string Memory = "memory";
    public const string Pressure = "pressure";
    public const string Worker = "worker";
}

/// <summary>
/// One stretch a member's run waited for admission with one reason. <see cref="ReleasedAt"/> is null
/// while it is still held; <see cref="Unfinished"/> marks a row a Host start closed because the Host
/// that opened it went down before it saw the hold end.
/// </summary>
public sealed record AdmissionHoldRow(
    long Id,
    string TeamId,
    string? TeamName,
    string Member,
    long? DeliverySeq,
    DateTimeOffset HeldAt,
    DateTimeOffset? ReleasedAt,
    string ReasonKind,
    string Reason,
    bool Unfinished);

/// <summary>
/// <c>admission_holds</c>: every admission hold, kept apart from the log so Reset and log retention
/// never take it. Append-only: a row is opened, closed once, and never deleted.
/// </summary>
public interface IAdmissionHoldLedger
{
    /// <summary>Opens a row for a hold that began at <paramref name="heldAt"/>.</summary>
    Task OpenAsync(
        string team, string member, long? deliverySeq, DateTimeOffset heldAt, string reasonKind, string reason,
        CancellationToken ct = default);

    /// <summary>Closes the member's open row, when it has one.</summary>
    Task CloseAsync(string team, string member, DateTimeOffset releasedAt, CancellationToken ct = default);

    /// <summary>Closes every row still open at <paramref name="at"/>, marked unfinished; answers how many.</summary>
    Task<int> CloseUnfinishedAsync(DateTimeOffset at, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="team"/>'s holds that began at or after <paramref name="since"/> (the team's
    /// creation) and overlap [<paramref name="from"/>, <paramref name="to"/>): held before its end and
    /// released at or after its start, or still held. Ordered by when they were held.
    /// </summary>
    Task<IReadOnlyList<AdmissionHoldRow>> ReadTeamAsync(
        string team, DateTimeOffset since, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
