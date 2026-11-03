namespace Harness.Contracts;

/// <summary>
/// A named vendor account the instance is billed through (<c>billing_accounts</c>,
/// <c>outcome-009</c>): a subscription, a metered account or a service, in its OWN currency, which is
/// never converted and need not be <c>outcomes.currency</c>. It is not a connection and holds no
/// credential. Nothing computes Actual Spend from it yet: <see cref="RecurringAmount"/> is stored and is
/// neither projected nor added to an estimate.
/// </summary>
/// <param name="RecurringAmount">Canonical decimal text, 0 or more, or null when none was given. A zero
/// is a zero, not "unavailable".</param>
/// <param name="EffectiveFrom">An ISO-8601 UTC instant, as <see cref="Instant"/> stores one.</param>
/// <param name="EffectiveTo">Null, or an instant at or after <see cref="EffectiveFrom"/>.</param>
/// <param name="Scope">Only <c>tenant</c> is written today.</param>
/// <param name="InstanceId">The instance's <c>instance.id</c> setting, copied once at create.</param>
/// <param name="CreatedBy">The person who created it, as an outcome create records its actor.</param>
public sealed record BillingAccount(
    string Id,
    string Name,
    string Vendor,
    string BillingType,
    string Currency,
    string BillingPeriod,
    string? RecurringAmount,
    string EffectiveFrom,
    string? EffectiveTo,
    bool Active,
    string? Description,
    string Scope,
    string InstanceId,
    string CreatedBy,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// One field of an edit: null when the request did not send it (the stored value stays), otherwise the
/// value it sent, whose <see cref="Value"/> may itself be null (a clear).
/// </summary>
public sealed record Sent(string? Value);

/// <summary>
/// The fields a create or an edit sends, as the request gave them, before the store reads them. On a
/// create an absent field is a missing one; on an edit an absent field stays as stored.
/// </summary>
public sealed record BillingAccountFields(
    Sent? Name = null,
    Sent? Vendor = null,
    Sent? BillingType = null,
    Sent? Currency = null,
    Sent? BillingPeriod = null,
    Sent? RecurringAmount = null,
    Sent? EffectiveFrom = null,
    Sent? EffectiveTo = null,
    Sent? Description = null,
    Sent? Scope = null);

/// <summary>A billing account write's answer: the account as it is now, or a refusal with its status
/// and one sentence. <see cref="Written"/> is false for a write that changed nothing.</summary>
public sealed record SpendWrite(BillingAccount? Account, int Status = 200, string? Refusal = null, bool Written = false)
{
    public bool Ok => Refusal is null;

    public static SpendWrite Refused(int status, string sentence) => new(null, status, sentence);
}

/// <summary>
/// The billing accounts, on each provider. EVERY REAL WRITE AND ITS TENANT ROW ARE ONE TRANSACTION, as
/// an outcome write is: the row is written through the audit the store was given, and when it cannot be
/// written the account write does not happen. A write that changes nothing writes no row and no tenant
/// row, and answers the account as it is. There is no delete: the table refuses one.
/// </summary>
public interface ISpendStore
{
    /// <summary>Every account, active and deactivated, oldest created first, then by id.</summary>
    Task<IReadOnlyList<BillingAccount>> ListAccountsAsync(CancellationToken ct = default);

    /// <summary>The account with <paramref name="id"/> (compared as a lower-case GUID), or null.</summary>
    Task<BillingAccount?> FindAccountAsync(string id, CancellationToken ct = default);

    /// <summary>A new active, tenant-scoped account with a store-assigned id, its instance id copied from
    /// the <c>instance.id</c> setting (refused when that is missing), created by
    /// <paramref name="createdBy"/>; appends <paramref name="audit"/> with its detail filled in.</summary>
    Task<SpendWrite> CreateAccountAsync(
        BillingAccountFields fields, string createdBy, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>The sent fields changed; <paramref name="audit"/> appended only when something did.</summary>
    Task<SpendWrite> EditAccountAsync(
        string id, BillingAccountFields fields, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>Active set to <paramref name="active"/>; <paramref name="audit"/> appended only when it
    /// moved.</summary>
    Task<SpendWrite> SetAccountActiveAsync(string id, bool active, TriggerAudit audit, CancellationToken ct = default);
}
