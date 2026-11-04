using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// What a billing account may hold and the sentences that refuse anything else, shared by both
/// providers' <see cref="ISpendStore"/> so the store - not only the HTTP layer - holds the closed sets.
///
/// <para>
/// The account is a definition, like an outcome: edited in place, never deleted. <c>scope</c> is only
/// ever <c>tenant</c> here, refused otherwise, with no CHECK on the column, so a later scope needs no
/// edit to a shipped step.
/// </para>
/// </summary>
public static class BillingAccountRules
{
    public const string ScopeTenant = "tenant";

    /// <summary>The <c>tenant_settings</c> row a create copies its instance id from: the GUID the ledger
    /// start writes once (<c>LedgerStart</c>), never <c>InstanceIdentity</c>.</summary>
    public const string InstanceIdSetting = "instance.id";

    public static readonly IReadOnlyList<string> BillingTypes = ["subscription", "metered", "service"];

    public static readonly IReadOnlyList<string> BillingPeriods = ["monthly", "annual"];

    /// <summary>The longest description kept: a short note, never a document.</summary>
    public const int DescriptionLimit = 500;

    public const string Columns =
        "id, name, vendor, billing_type, currency, billing_period, recurring_amount, effective_from, "
        + "effective_to, active, description, scope, instance_id, created_by, created_at, updated_at";

    public const string NameRequired = "A billing account needs a name.";
    public const string VendorRequired = "A billing account needs a vendor.";
    public const string BillingTypeRefused = "The billing type must be subscription, metered or service.";
    public const string CurrencyRefused = "The currency must be a three-letter code, such as USD.";
    public const string BillingPeriodRefused = "The billing period must be monthly or annual.";

    public const string RecurringAmountRefused =
        "The recurring amount must be a plain number of zero or more, such as 20 or 49.99, or blank to clear it.";

    public const string EffectiveFromRefused =
        "The effective-from date must be an ISO-8601 date or time, such as 2026-11-01 or 2026-11-01T00:00:00Z.";

    public const string EffectiveToRefused =
        "The effective-to date must be an ISO-8601 date or time, such as 2026-12-31, or blank to clear it.";

    public const string EffectiveToBeforeFrom = "The effective-to date cannot be before the effective-from date.";

    public const string ScopeRefused = "A billing account's scope can only be tenant.";

    public static readonly string DescriptionTooLong =
        $"A billing account's description is a short note of at most {DescriptionLimit} characters.";

    public const string NoInstanceId =
        "This instance has no instance id yet, so a billing account cannot be created; restart the platform and try again.";

    public static string Missing(string id) => $"No billing account has the id {id}.";

    /// <summary>What an account will hold after a create or an edit, every field already read.</summary>
    public sealed record Draft(
        string Name,
        string Vendor,
        string BillingType,
        string Currency,
        string BillingPeriod,
        string? RecurringAmount,
        string EffectiveFrom,
        string? EffectiveTo,
        string? Description);

    /// <summary>
    /// The sent fields read over <paramref name="current"/> (null for a create, when every required
    /// field must be sent), or the one sentence that refuses them. Nothing here writes.
    /// </summary>
    public static (Draft? Draft, string? Refusal) Read(BillingAccountFields fields, BillingAccount? current)
    {
        if (fields.Scope is { } scope
            && !string.Equals(scope.Value?.Trim(), current?.Scope ?? ScopeTenant, StringComparison.Ordinal))
        {
            return (null, ScopeRefused);
        }

        if (!Required(fields.Name, current?.Name, NameRequired, out var name, out var refusal)) return (null, refusal);
        if (!Required(fields.Vendor, current?.Vendor, VendorRequired, out var vendor, out refusal)) return (null, refusal);

        if (!OneOf(fields.BillingType, current?.BillingType, BillingTypes, BillingTypeRefused, out var type, out refusal)) return (null, refusal);
        if (!OneOf(fields.BillingPeriod, current?.BillingPeriod, BillingPeriods, BillingPeriodRefused, out var period, out refusal)) return (null, refusal);

        var currency = current?.Currency;
        if (fields.Currency is not null || current is null)
        {
            var text = fields.Currency?.Value?.Trim() ?? "";
            if (text.Length != 3 || !text.All(char.IsAsciiLetter)) return (null, CurrencyRefused);
            currency = text.ToUpperInvariant();
        }

        var amount = current?.RecurringAmount;
        if (fields.RecurringAmount is { } sentAmount)
        {
            if (string.IsNullOrWhiteSpace(sentAmount.Value)) amount = null;
            else if (!EffectiveCost.TryRate(sentAmount.Value, out var value) || value < 0) return (null, RecurringAmountRefused);
            else amount = ExecutionSnapshotFacts.Canonical(value);
        }

        var from = current?.EffectiveFrom;
        if (fields.EffectiveFrom is not null || current is null)
        {
            if (Moment(fields.EffectiveFrom?.Value) is not { } at) return (null, EffectiveFromRefused);
            from = Instant.Stamp(at);
        }

        var to = current?.EffectiveTo;
        if (fields.EffectiveTo is { } sentTo)
        {
            if (string.IsNullOrWhiteSpace(sentTo.Value)) to = null;
            else if (Moment(sentTo.Value) is not { } at) return (null, EffectiveToRefused);
            else to = Instant.Stamp(at);
        }

        if (to is not null && Instant.Parse(to) < Instant.Parse(from!)) return (null, EffectiveToBeforeFrom);

        var description = current?.Description;
        if (fields.Description is { } sentDescription)
        {
            description = string.IsNullOrWhiteSpace(sentDescription.Value) ? null : sentDescription.Value.Trim();
            if (description is { Length: > DescriptionLimit }) return (null, DescriptionTooLong);
        }

        return (new Draft(name!, vendor!, type!, currency!, period!, amount, from!, to, description), null);
    }

    /// <summary>The account <paramref name="draft"/> makes of <paramref name="current"/>.</summary>
    public static BillingAccount Apply(BillingAccount current, Draft draft, string updatedAt) =>
        current with
        {
            Name = draft.Name,
            Vendor = draft.Vendor,
            BillingType = draft.BillingType,
            Currency = draft.Currency,
            BillingPeriod = draft.BillingPeriod,
            RecurringAmount = draft.RecurringAmount,
            EffectiveFrom = draft.EffectiveFrom,
            EffectiveTo = draft.EffectiveTo,
            Description = draft.Description,
            UpdatedAt = updatedAt,
        };

    /// <summary>True when <paramref name="draft"/> would store exactly what <paramref name="current"/> holds.</summary>
    public static bool Unchanged(BillingAccount current, Draft draft) =>
        Apply(current, draft, current.UpdatedAt) == current;

    /// <summary>
    /// The tenant row's detail: the account id and each field that differs between
    /// <paramref name="before"/> (null for a create) and <paramref name="after"/>, as old and new.
    /// </summary>
    public static string Detail(BillingAccount? before, BillingAccount after)
    {
        var changes = new Dictionary<string, object?>();

        void Field(string name, object? old, object? @new)
        {
            if (!Equals(old, @new)) changes[name] = new { old, @new };
        }

        Field("name", before?.Name, after.Name);
        Field("vendor", before?.Vendor, after.Vendor);
        Field("billingType", before?.BillingType, after.BillingType);
        Field("currency", before?.Currency, after.Currency);
        Field("billingPeriod", before?.BillingPeriod, after.BillingPeriod);
        Field("recurringAmount", before?.RecurringAmount, after.RecurringAmount);
        Field("effectiveFrom", before?.EffectiveFrom, after.EffectiveFrom);
        Field("effectiveTo", before?.EffectiveTo, after.EffectiveTo);
        Field("description", before?.Description, after.Description);
        Field("active", before?.Active, after.Active);
        Field("scope", before?.Scope, after.Scope);
        Field("instanceId", before?.InstanceId, after.InstanceId);

        return JsonSerializer.Serialize(new { account = after.Id, changes });
    }

    /// <summary>An id as the table keys it: a GUID in lower-case "D" form, or null when it is not one.</summary>
    public static string? KeyOf(string? id) =>
        id is not null && Guid.TryParse(id.Trim(), out var guid) ? guid.ToString("D", CultureInfo.InvariantCulture) : null;

    /// <summary>One row, read in <see cref="Columns"/> order.</summary>
    public static BillingAccount ReadRow(DbDataReader reader)
    {
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

        return new BillingAccount(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), Text(6), reader.GetString(7), Text(8), Convert.ToInt64(reader.GetValue(9), CultureInfo.InvariantCulture) != 0,
            Text(10), reader.GetString(11), reader.GetString(12), reader.GetString(13), reader.GetString(14), reader.GetString(15));
    }

    public static async Task<List<BillingAccount>> ReadRowsAsync(DbCommand command, CancellationToken ct)
    {
        var accounts = new List<BillingAccount>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) accounts.Add(ReadRow(reader));
        return accounts;
    }

    /// <summary>The parameters an insert or an update binds, by name without the prefix.</summary>
    public static IEnumerable<(string Name, object Value)> Values(BillingAccount account) =>
    [
        ("id", account.Id), ("name", account.Name), ("vendor", account.Vendor), ("type", account.BillingType),
        ("currency", account.Currency), ("period", account.BillingPeriod),
        ("amount", (object?)account.RecurringAmount ?? DBNull.Value), ("from", account.EffectiveFrom),
        ("to", (object?)account.EffectiveTo ?? DBNull.Value), ("active", account.Active ? 1 : 0),
        ("description", (object?)account.Description ?? DBNull.Value), ("scope", account.Scope),
        ("instance", account.InstanceId), ("createdBy", account.CreatedBy), ("createdAt", account.CreatedAt),
        ("updatedAt", account.UpdatedAt),
    ];

    private static bool Required(Sent? sent, string? stored, string sentence, out string? value, out string? refusal)
    {
        refusal = null;
        value = stored;
        if (sent is null && stored is not null) return true;

        value = sent?.Value?.Trim();
        if (!string.IsNullOrEmpty(value)) return true;

        refusal = sentence;
        return false;
    }

    private static bool OneOf(
        Sent? sent, string? stored, IReadOnlyList<string> allowed, string sentence, out string? value, out string? refusal)
    {
        refusal = null;
        value = stored;
        if (sent is null && stored is not null) return true;

        var text = sent?.Value?.Trim().ToLowerInvariant();
        value = allowed.FirstOrDefault(a => string.Equals(a, text, StringComparison.Ordinal));
        if (value is not null) return true;

        refusal = sentence;
        return false;
    }

    /// <summary>An ISO-8601 date (midnight UTC) or date and time (UTC when it names no offset), or null.</summary>
    internal static DateTimeOffset? Moment(string? text)
    {
        var value = text?.Trim();
        if (value is null || value.Length < 10 || value[4] != '-' || value[7] != '-') return null;

        if (value.Length == 10)
        {
            return DateTimeOffset.TryParseExact(
                value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var day)
                ? day.ToUniversalTime()
                : null;
        }

        if (value[10] != 'T') return null;

        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at.ToUniversalTime()
            : null;
    }
}
