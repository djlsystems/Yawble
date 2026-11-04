namespace Harness.Contracts;

/// <summary>
/// WHICH MEANINGFUL ACT A PERSON DID, as one word from a closed set: <c>intervention_ledger.kind</c>.
/// Modelled on <see cref="FailureClasses"/> and <see cref="InterruptionReasons"/>, and the one place
/// these words are spelled.
///
/// <para>
/// WRITTEN BY THE ROUTE, NEVER PUBLISHED. A row is written server side, in the same transaction as the
/// log row the act went with, from the request's authenticated principal (<see cref="InterventionAct"/>);
/// no public append, message, tell, member tool or key can write one or choose its kind, actor or
/// duration. A kind outside this set is refused by <see cref="Require"/> before anything is written.
/// </para>
/// </summary>
public static class InterventionKinds
{
    /// <summary>A person dispatched a backlog item to a team.</summary>
    public const string Dispatch = "dispatch";

    /// <summary>A person told a member something, or started a workflow by telling one.</summary>
    public const string Instruction = "instruction";

    /// <summary>A person answered a member's <c>needs_decision</c>: told into a workflow a decision is open in.</summary>
    public const string DecisionAnswered = "decision_answered";

    /// <summary>A person released a paused workflow, or a paused team.</summary>
    public const string Resume = "resume";

    /// <summary>A person nudged a stalled workflow's Manager back to it.</summary>
    public const string Nudge = "nudge";

    /// <summary>A person closed a workflow.</summary>
    public const string Close = "close";

    /// <summary>A person set, changed or cleared a workflow's outcome link.</summary>
    public const string OutcomeChanged = "outcome_changed";

    /// <summary>A person saved a connection's value or a secret a member runs with. Never the value.</summary>
    public const string CredentialSupplied = "credential_supplied";

    /// <summary>A person granted access a run was waiting on. In the set for later; nothing writes it yet.</summary>
    public const string AccessResolved = "access_resolved";

    /// <summary>One Concierge session, open to close: the one kind with a measured duration.</summary>
    public const string ConciergeSession = "concierge_session";

    /// <summary>Every kind, for a test or a reader that wants to enumerate them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Dispatch, Instruction, DecisionAnswered, Resume, Nudge, Close, OutcomeChanged,
        CredentialSupplied, AccessResolved, ConciergeSession,
    ];

    /// <summary>The kinds whose rows carry a measured <c>duration_seconds</c>; every other kind's is NULL.</summary>
    public static IReadOnlyList<string> Measured { get; } = [ConciergeSession];

    /// <summary>Whether <paramref name="kind"/> is one of <see cref="All"/>, spelled exactly.</summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);

    /// <summary><paramref name="kind"/>, when it is one of <see cref="All"/>; refused otherwise, with one sentence.</summary>
    public static string Require(string? kind) =>
        IsKnown(kind)
            ? kind!
            : throw new ArgumentException(
                $"'{kind}' is not an intervention kind; the kinds are {string.Join(", ", All)}.", nameof(kind));
}

/// <summary>
/// ONE PERSON'S ACT, as the server records it in <c>intervention_ledger</c>: built only by
/// <see cref="Of"/> from a request's principal (an <see cref="AppendContext"/>), or by
/// <see cref="Session"/> for a Concierge session the Host ended - never from a body, a payload or a
/// header. Handed to the log beside its row (<see cref="AppendContext.Intervention"/>) or to the tenant
/// log beside its row (<see cref="TriggerAudit.Intervention"/>), whose one writer lands it in the same
/// transaction.
/// </summary>
/// <param name="Kind">One of <see cref="InterventionKinds.All"/>.</param>
/// <param name="ActorId">The person: their user id, or the owner of the key they used.</param>
/// <param name="ActorEmail">Their email when the ticket carried it; else read from <c>users</c> at write.</param>
/// <param name="Team">The team the act was on, or null.</param>
/// <param name="Correlation">The workflow, when the act's log row is not a message (a tenant row); a
/// message's own correlation is used otherwise.</param>
/// <param name="DurationSeconds">Measured open to close; only a <see cref="InterventionKinds.Measured"/> kind has one.</param>
public sealed record InterventionAct(
    string Kind,
    string ActorId,
    string? ActorEmail,
    string? Team,
    long? Correlation = null,
    long? DurationSeconds = null)
{
    /// <summary>Whether the duration was measured: 1 on the row, else 0.</summary>
    public bool DurationMeasured => DurationSeconds is not null;

    /// <summary>
    /// THE ACTOR RULE: a person signed in is their id and email; an API key is its owner, whose email the
    /// writer reads at write time, so the row still reads once the key is revoked and the owner deleted.
    /// A member, a member's key, a Concierge, a trigger, a schedule, a wake or the Host is no person, and
    /// writes no row: null.
    /// </summary>
    public static InterventionAct? Of(string kind, AppendContext? principal, string? team, long? correlation = null)
    {
        InterventionKinds.Require(kind);
        if (InterventionKinds.Measured.Contains(kind, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{kind}' is measured; record it through {nameof(Session)}.", nameof(kind));
        }

        return principal switch
        {
            { Kind: PrincipalKind.User } user => new InterventionAct(kind, user.PrincipalId, user.Email, team, correlation),
            { Kind: PrincipalKind.ApiKey, OwnerUserId: { } owner } => new InterventionAct(kind, owner, null, team, correlation),
            _ => null,
        };
    }

    /// <summary>
    /// A person's act a store records for them, when the store holds only their id and email (a
    /// credential's save): null when there is no person's id.
    /// </summary>
    public static InterventionAct? ForPerson(string kind, string? userId, string? email, string? team)
    {
        InterventionKinds.Require(kind);
        return string.IsNullOrEmpty(userId) ? null : new InterventionAct(kind, userId, email, team);
    }

    /// <summary>One Concierge session of <paramref name="userId"/>, <paramref name="seconds"/> long, measured.</summary>
    public static InterventionAct Session(string userId, string? team, long seconds) =>
        new(InterventionKinds.ConciergeSession, userId, null, team, null, Math.Max(0, seconds));

    /// <summary>This act, checked: its kind is in the set and only a measured kind carries a duration.</summary>
    public InterventionAct Checked()
    {
        InterventionKinds.Require(Kind);
        var measured = InterventionKinds.Measured.Contains(Kind, StringComparer.Ordinal);
        if (measured != DurationMeasured)
        {
            throw new ArgumentException(
                measured
                    ? $"A '{Kind}' row carries its measured duration."
                    : $"A '{Kind}' row carries no duration; only {string.Join(", ", InterventionKinds.Measured)} is measured.");
        }

        if (string.IsNullOrEmpty(ActorId)) throw new ArgumentException("An intervention names the person who acted.");
        return this;
    }
}

/// <summary>
/// AN ACT THE HOST RECORDS ON ITS OWN, with no log row to go with: a Concierge session, ended. Each
/// provider writes it through its one insert (<see cref="InterventionLedgerRow"/> and its SQL Server
/// twin) in a transaction of its own. Not reachable from any route: there is no write route for
/// <c>intervention_ledger</c>.
/// </summary>
public interface IInterventionLedger
{
    /// <summary>Writes <paramref name="act"/>, stamped <paramref name="at"/>.</summary>
    Task RecordAsync(InterventionAct act, DateTimeOffset at, CancellationToken ct = default);
}
