using System.Globalization;
using System.Text.Json;
using Harness.Messaging;

namespace Harness.Host;

/// <summary>
/// A member PATCH's own estimated hourly rate and billing account, as read from the body. Each field
/// has three states: absent leaves the stored value, null or blank clears it, and anything else is the
/// new value. <see cref="Rate"/> is canonical decimal text ("0" is explicitly not priced);
/// <see cref="Account"/> a lower-case GUID.
/// </summary>
public sealed record MemberBillingEdit(bool RateSent, string? Rate, bool AccountSent, string? Account)
{
    public static readonly MemberBillingEdit None = new(false, null, false, null);

    public bool Any => RateSent || AccountSent;
}

/// <summary>
/// Reads and refuses a member PATCH's <c>hourlyRate</c> and <c>billingAccountId</c> BEFORE anything is
/// written, so a refused request writes no row and no tenant row.
/// </summary>
public static class MemberBilling
{
    public const string RateRefused =
        "The hourly rate must be a plain number of zero or more, such as 60 or 62.5, or blank to clear it.";

    public const string AccountRefused =
        "The billing account id must be an id like 3f2b8c1e-9a4d-4e2f-b6a1-0c5d7e8f9a10, or blank to clear it.";

    /// <summary>The edit, or the one sentence that refuses the request.</summary>
    public static (MemberBillingEdit? Edit, string? Refusal) Read(JsonElement hourlyRate, JsonElement billingAccountId)
    {
        var rateSent = hourlyRate.ValueKind != JsonValueKind.Undefined;
        string? rate = null;

        if (rateSent && !IsBlank(hourlyRate))
        {
            var text = hourlyRate.ValueKind switch
            {
                JsonValueKind.Number => hourlyRate.GetRawText(),
                JsonValueKind.String => hourlyRate.GetString()!,
                _ => null,
            };

            if (text is null || !EffectiveCost.TryRate(text, out var amount) || amount < 0) return (null, RateRefused);

            rate = ExecutionSnapshotFacts.Canonical(amount);
        }

        var accountSent = billingAccountId.ValueKind != JsonValueKind.Undefined;
        string? account = null;

        if (accountSent && !IsBlank(billingAccountId))
        {
            if (billingAccountId.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(billingAccountId.GetString()!.Trim(), "D", out var id))
            {
                return (null, AccountRefused);
            }

            account = id.ToString("D", CultureInfo.InvariantCulture);
        }

        return (new MemberBillingEdit(rateSent, rate, accountSent, account), null);
    }

    private static bool IsBlank(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null
        || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));
}

/// <summary>
/// A member's rate and account as its read and PATCH answer them: the stored override, the account,
/// who last changed either and when, and the effective rate a run starting now would be snapshotted at
/// (<see cref="EffectiveCost"/>, over the stored tenant settings rows - never a configuration fallback,
/// exactly as the snapshot writer reads them).
/// </summary>
public sealed record MemberBillingView(
    string? HourlyRate, string? BillingAccountId, string? BillingSetBy, string? BillingSetAt,
    MemberBillingView.Effective EffectiveRate)
{
    /// <summary>The effective rate: <c>amount</c> canonical text or null, <c>currency</c> upper-case or
    /// null, <c>source</c> <c>member</c>, <c>tenant</c> or <c>none</c>.</summary>
    public sealed record Effective(string? Amount, string? Currency, string Source);

    public const string Described =
        "`hourlyRate` is the member's own estimated hourly rate as stored (canonical text; null is no "
        + "override, \"0\" not priced), `billingAccountId` its billing account, `billingSetBy` and "
        + "`billingSetAt` who last changed either and when (ISO-8601 UTC; kept when cleared), and "
        + "`effectiveRate` (`amount`, `currency`, `source`: `member`, `tenant` or `none`) what a run "
        + "starting now is snapshotted at. A plugin with no rate of its own is `none`, never the agent rate.\n\n";

    public static async Task<MemberBillingView> OfAsync(
        Harness.Contracts.PersistedMember member, Harness.Identity.ITenantSettingsStore settings, CancellationToken ct)
    {
        var rows = await settings.ReadAllAsync(ct);
        string? Setting(string name) => rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal))?.Value;

        var cost = EffectiveCost.Of(
            new EffectiveCost.Member(member.Agent, member.EstHourlyRate, member.BillingAccountId),
            Setting(ExecutionSnapshotFacts.AgentHourlyRateSetting),
            Setting(ExecutionSnapshotFacts.CurrencySetting));

        return new MemberBillingView(
            member.EstHourlyRate,
            member.BillingAccountId,
            member.BillingSetBy,
            member.BillingSetAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            new Effective(cost.Amount, cost.Currency, cost.Source));
    }
}
