using System.Globalization;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// WHAT A MEMBER'S RUN IS PRICED AT, decided once: the snapshot writers (<c>LedgerRows</c>,
/// <c>SqlServerLedgerRows</c>) call <see cref="Of"/> with the member's roster row and the tenant's
/// stored settings, read in a run's start transaction, and the member read answers the same thing so a
/// person can see what the next run will record.
///
/// <list type="bullet">
/// <item>A positive own rate (<c>team_members.est_hourly_rate</c>), agent or plugin, is
/// <see cref="ExecutionSnapshotFacts.RateSourceMember"/> at that amount, in the tenant currency.</item>
/// <item>An own rate of "0" is explicitly not priced: <see cref="ExecutionSnapshotFacts.RateSourceNone"/>,
/// never a priced zero, and it does not fall through to the tenant rate.</item>
/// <item>An agent with no own rate takes the tenant default; a missing, zero or unreadable tenant rate is
/// not priced.</item>
/// <item>A plugin with no own rate is not priced, even when the tenant agent rate is set.</item>
/// <item>A member not on the roster is not priced.</item>
/// </list>
///
/// The tenant's settings are the stored <c>tenant_settings</c> rows only, never a configuration
/// fallback: what a snapshot records is what is in the database the run started against.
/// </summary>
/// <param name="Amount">The rate as canonical decimal text (<see cref="ExecutionSnapshotFacts.Canonical"/>), or null.</param>
/// <param name="Currency">The ISO 4217 code, upper-case, only with an amount.</param>
/// <param name="Source"><c>member</c>, <c>tenant</c> or <c>none</c>.</param>
/// <param name="BillingAccountId">The member's billing account id, or null.</param>
/// <param name="Preset">The member's <c>agent</c> column (a preset, or <c>plugin:&lt;id&gt;</c>), or null off the roster.</param>
/// <param name="Kind"><see cref="MemberRef.AgentKind"/> or <see cref="MemberRef.PluginKind"/>, or null off the roster.</param>
public sealed record EffectiveCost(
    string? Amount, string? Currency, string Source, string? BillingAccountId, string? Preset, string? Kind)
{
    /// <summary>A member's roster row as far as pricing reads it.</summary>
    public sealed record Member(string Agent, string? EstHourlyRate, string? BillingAccountId);

    /// <summary>
    /// The effective cost of a run of <paramref name="member"/> (null when it is not on the roster),
    /// from the tenant's <paramref name="rateSetting"/> and <paramref name="currencySetting"/> rows as
    /// stored (null when there is no row).
    /// </summary>
    public static EffectiveCost Of(Member? member, string? rateSetting, string? currencySetting)
    {
        if (member is null) return new EffectiveCost(null, null, ExecutionSnapshotFacts.RateSourceNone, null, null, null);

        var kind = MemberRef.KindOf(member.Agent);
        var account = string.IsNullOrWhiteSpace(member.BillingAccountId) ? null : member.BillingAccountId;

        EffectiveCost Priced(ExecutionSnapshotFacts.Rate rate) =>
            new(rate.Amount, rate.Currency, rate.Source, account, member.Agent, kind);

        // THE MEMBER'S OWN RATE, when it has one, decides: positive is priced, anything else (an
        // explicit "0", or text the PATCH would never have stored) is not, and neither falls through.
        if (member.EstHourlyRate is { } own)
        {
            if (!TryRate(own, out var amount) || amount <= 0) return Priced(None);

            return Priced(new ExecutionSnapshotFacts.Rate(
                ExecutionSnapshotFacts.Canonical(amount), CurrencyOf(currencySetting), ExecutionSnapshotFacts.RateSourceMember));
        }

        return Priced(ExecutionSnapshotFacts.RateOf(kind, rateSetting, currencySetting));
    }

    private static readonly ExecutionSnapshotFacts.Rate None = new(null, null, ExecutionSnapshotFacts.RateSourceNone);

    /// <summary>A rate as written: a plain invariant decimal, no exponent, no thousands separator.</summary>
    public static bool TryRate(string text, out decimal amount) =>
        decimal.TryParse(
            text.Trim(),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            CultureInfo.InvariantCulture,
            out amount);

    private static string CurrencyOf(string? currencySetting) =>
        (string.IsNullOrWhiteSpace(currencySetting) ? ExecutionSnapshotFacts.DefaultCurrency : currencySetting.Trim()).ToUpperInvariant();
}
