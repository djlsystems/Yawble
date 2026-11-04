using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A CONCIERGE SESSION AS ONE INTERVENTION: the <see cref="ConciergeSessionEnded"/> the Host hands
/// <see cref="ConciergeSessionStore"/>, which writes one <c>concierge_session</c> row per session, the
/// person its key names, measured from the spawn to the end (<c>duration_measured</c> 1). The chat
/// within it is not counted message by message, and nothing it said is stored.
/// </summary>
public static class ConciergeSessionRecord
{
    public static ConciergeSessionEnded Writing(IInterventionLedger ledger) =>
        (key, team, openedAt, endedAt, ct) => ledger.RecordAsync(
            InterventionAct.Session(key.User, team, (long)Math.Round((endedAt - openedAt).TotalSeconds)),
            endedAt,
            ct);
}
