using System.Globalization;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// WHAT AN EXECUTION SNAPSHOT SAYS, decided once for both providers: <c>LedgerRows</c> and
/// <c>SqlServerLedgerRows</c> read the member's roster row and the tenant's settings in the append
/// transaction of a run's <c>agentContainer.started</c> row and hand them here (see
/// <c>OutcomeSchema</c>'s <c>outcome-008</c>).
///
/// <para>
/// THE CONFIGURATION AT START COVERS THE WHOLE RUN. A snapshot is written once, at start, and the
/// triggers refuse every UPDATE and DELETE, so a rate changed while the run is under way prices
/// only the next run, and a link written after start does not move the run's <c>link_id</c>.
/// </para>
///
/// <para>
/// NO RATE IS NEVER A PRICED ZERO. A member's own positive rate is <see cref="RateSourceMember"/>; an
/// own rate of "0" is not priced. An agent with no own rate takes the tenant default; a missing or zero
/// rate is <see cref="RateSourceNone"/> with no amount. A plugin with no own rate is never priced at the
/// agent rate. A member that is not on the roster has no kind and is not priced either. The member
/// rules are <see cref="EffectiveCost"/>'s; <see cref="RateOf"/> is its tenant-default arm.
/// </para>
/// </summary>
public static class ExecutionSnapshotFacts
{
    /// <summary>The member's own positive rate, <c>team_members.est_hourly_rate</c> (<c>auth-034</c>).</summary>
    public const string RateSourceMember = "member";

    /// <summary>The tenant default, <see cref="AgentHourlyRateSetting"/>.</summary>
    public const string RateSourceTenant = "tenant";

    /// <summary>Not priced: <c>rate_amount</c> and <c>rate_currency</c> are NULL.</summary>
    public const string RateSourceNone = "none";

    /// <summary>The tenant default rate's <c>tenant_settings</c> row: whole currency units an agent hour.</summary>
    public const string AgentHourlyRateSetting = "outcomes.agentHourlyRate";

    /// <summary>The tenant currency's <c>tenant_settings</c> row: an ISO 4217 code, kept lower-case.</summary>
    public const string CurrencySetting = "outcomes.currency";

    /// <summary>The currency when no row names one: the setting's built-in default.</summary>
    public const string DefaultCurrency = "usd";

    /// <summary>A snapshot's rate: the amount as canonical decimal text, its currency upper-case, and where it came from.</summary>
    public sealed record Rate(string? Amount, string? Currency, string Source);

    /// <summary>
    /// The rate a run of a member of <paramref name="memberKind"/> is snapshotted at, from the tenant's
    /// <paramref name="rateSetting"/> and <paramref name="currencySetting"/> rows as stored (null when
    /// there is no row).
    /// </summary>
    public static Rate RateOf(string? memberKind, string? rateSetting, string? currencySetting)
    {
        var none = new Rate(null, null, RateSourceNone);

        if (memberKind != MemberRef.AgentKind) return none;

        if (rateSetting is null
            || !decimal.TryParse(rateSetting.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)
            || rate <= 0)
        {
            return none;
        }

        var currency = string.IsNullOrWhiteSpace(currencySetting) ? DefaultCurrency : currencySetting.Trim();

        return new Rate(Canonical(rate), currency.ToUpperInvariant(), RateSourceTenant);
    }

    /// <summary>The decimal with no trailing zeros and no exponent, in the invariant culture: 60, 62.5.</summary>
    public static string Canonical(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
