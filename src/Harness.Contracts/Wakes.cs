using System.Data.Common;

namespace Harness.Contracts;

/// <summary>
/// A wake a member asked for and that has not fired or been cancelled: one instruction to that
/// member, in the workflow it was asked in, at <see cref="DueAt"/>. Stored (<c>wakes</c>,
/// <c>messages-003</c>) so it survives a Host restart; the row goes when it fires, is skipped or is
/// cancelled, and the log keeps what happened.
/// </summary>
/// <param name="Id">The wake's id, the number <c>wake:&lt;id&gt;</c> and every refusal name.</param>
/// <param name="Team">The member's team, stored spelling.</param>
/// <param name="Member">The member's identifier within its team.</param>
/// <param name="Workflow">The workflow (correlation) the wake was asked in, and fires into.</param>
/// <param name="AskedSeq">The <c>wake.asked</c> row: the causation of everything the wake writes.</param>
/// <param name="AskedAt">When it was asked.</param>
/// <param name="DueAt">When it fires.</param>
/// <param name="Note">What the member said to look at when woken.</param>
public sealed record PendingWake(
    long Id,
    string Team,
    string Member,
    long Workflow,
    long AskedSeq,
    DateTimeOffset AskedAt,
    DateTimeOffset DueAt,
    string Note)
{
    public ContainerId Container => new(Team, Member);

    /// <summary>What a person's screen and a member's card are given.</summary>
    public WakeView View() => new(Id, Member, Workflow, DueAt.ToUniversalTime(), AskedAt.ToUniversalTime(), Note);
}

/// <summary>
/// A pending wake as the routes answer it: on the member's snapshot (<c>wakes</c>), on a Kanban card
/// (<c>wakes</c>), on a team workflow row (<c>wakes</c>) and from <c>GET /api/teams/{team}/wakes</c>.
/// Every instant is UTC; a screen says it on the reader's clock.
/// </summary>
public sealed record WakeView(
    long Id,
    string Member,
    long Workflow,
    DateTimeOffset DueAt,
    DateTimeOffset AskedAt,
    string Note);

/// <summary>
/// The pending wakes. Reads open their own connection; the writes that must land with a log row
/// (<c>wake.asked</c>, <c>wake.cancelled</c>, the firing instruction) take the connection and
/// transaction <see cref="IMessageLog.AppendWithinAsync"/> hands its callbacks, so the row and the
/// wake commit or roll back together and a wake is never fired or cancelled twice.
/// </summary>
public interface IWakeStore
{
    /// <summary>Every pending wake, earliest due first.</summary>
    Task<IReadOnlyList<PendingWake>> AllAsync(CancellationToken ct = default);

    /// <summary>One team's pending wakes, earliest due first.</summary>
    Task<IReadOnlyList<PendingWake>> ForTeamAsync(string team, CancellationToken ct = default);

    /// <summary>The pending wakes asked in <paramref name="workflow"/>, earliest due first.</summary>
    Task<IReadOnlyList<PendingWake>> ForWorkflowAsync(long workflow, CancellationToken ct = default);

    Task<PendingWake?> FindAsync(long id, CancellationToken ct = default);

    /// <summary>The wakes due at or before <paramref name="now"/>, earliest first.</summary>
    Task<IReadOnlyList<PendingWake>> DueAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>The earliest due instant, or null with nothing pending.</summary>
    Task<DateTimeOffset?> EarliestDueAsync(CancellationToken ct = default);

    /// <summary>Removes every wake of <paramref name="team"/>, for its deletion. Answers how many.</summary>
    Task<int> DeleteForTeamAsync(string team, CancellationToken ct = default);

    /// <summary>One member's pending wakes inside the caller's transaction, earliest due first.</summary>
    Task<IReadOnlyList<PendingWake>> ForMemberWithinAsync(
        DbConnection connection, DbTransaction transaction, ContainerId member, CancellationToken ct = default);

    /// <summary>Stores a wake inside the caller's transaction and answers it with its id.</summary>
    Task<PendingWake> InsertWithinAsync(
        DbConnection connection, DbTransaction transaction, PendingWake wake, CancellationToken ct = default);

    /// <summary>Records the <c>wake.asked</c> row of a wake stored in the same transaction: the row
    /// carries the wake's id, so the wake is stored first and pointed at its row once it has a seq.</summary>
    Task SetAskedWithinAsync(
        DbConnection connection, DbTransaction transaction, long id, long askedSeq, CancellationToken ct = default);

    /// <summary>Removes one wake inside the caller's transaction, answering it as it was, or null
    /// when it was already gone - which is what makes a fire or a cancel happen once.</summary>
    Task<PendingWake?> TakeWithinAsync(
        DbConnection connection, DbTransaction transaction, long id, CancellationToken ct = default);
}
