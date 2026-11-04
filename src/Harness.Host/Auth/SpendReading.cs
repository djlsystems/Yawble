using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// WHO HOLDS <see cref="Permits.Spend"/>: an agent, while a person has turned on
/// <c>reporting.agentsMayReadSpend</c>, and nobody else.
///
/// Applied where a key becomes a principal, after <see cref="ConciergeLaunchFactory.Strip"/>, on every
/// request. So the permit is never stored or minted: a key carrying it by any road (a member's permits,
/// a person's API key, a hand-edited row) holds nothing, and turning the setting off refuses the agent's
/// very next request. An agent cannot grant it to itself: the setting is written only through
/// <c>PUT /api/tenant/settings</c>, which is <c>HumansOnly</c>.
///
/// An agent is a team's container (its Manager or a member) or a Concierge. A person's API key is a
/// person's outside tool, not an agent, and the worker key holds no permit at all.
/// </summary>
public static class SpendReading
{
    /// <summary>
    /// The refusal of a Spend read to a machine principal without the permit: NOT
    /// <see cref="PermitGate.Missing"/>, whose fix is a wider credential. There is none - the fix is a
    /// person's setting, so this names it and where it lives.
    /// </summary>
    public const string Refused =
        "Reading Actual Spend is a person's, and an agent's only while a person has turned on "
        + TenantSettings.ReportingAgentsMayReadSpendName + " in " + TenantSettings.SpendTab
        + "; no credential changes that, so report what you need and ask a person.";

    public static bool IsAgent(Principal principal) =>
        principal.Kind is PrincipalKind.Container or PrincipalKind.Concierge or PrincipalKind.TenantConcierge;

    public static Principal Apply(Principal principal, bool agentsMayReadSpend)
    {
        var holds = agentsMayReadSpend && IsAgent(principal);
        if (holds == principal.May(Permits.Spend)) return principal;

        var permits = new HashSet<string>(principal.Permits, StringComparer.Ordinal);
        if (holds) permits.Add(Permits.Spend);
        else permits.Remove(Permits.Spend);

        return principal with { Permits = permits };
    }
}
