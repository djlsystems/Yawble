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

    /// <summary>Every expense row, current and superseded, oldest recorded first, then by id; only
    /// <paramref name="accountId"/>'s when one is given.</summary>
    Task<IReadOnlyList<Expense>> ListExpensesAsync(string? accountId = null, CancellationToken ct = default);

    /// <summary>The expense row with <paramref name="id"/> (compared as a lower-case GUID), or null.</summary>
    Task<Expense?> FindExpenseAsync(string id, CancellationToken ct = default);

    /// <summary>A new confirmed expense on an existing account (a deactivated one included), in that
    /// account's currency, recorded by <paramref name="recordedBy"/>; appends <paramref name="audit"/>.</summary>
    Task<ExpenseWrite> RecordExpenseAsync(
        ExpenseFields fields, string recordedBy, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>A new void row superseding the current confirmed row <paramref name="id"/>, carrying its
    /// account, amount, currency, period and kind, with <paramref name="note"/>; appends
    /// <paramref name="audit"/>.</summary>
    Task<ExpenseWrite> VoidExpenseAsync(
        string id, string? note, string recordedBy, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>A new confirmed row superseding the current confirmed row <paramref name="id"/>, on the
    /// same account: the sent fields over the old row's; appends <paramref name="audit"/>.</summary>
    Task<ExpenseWrite> ReplaceExpenseAsync(
        string id, ExpenseFields fields, string recordedBy, TriggerAudit audit, CancellationToken ct = default);
}

/// <summary>
/// One recorded charge on a billing account (<c>expenses</c>, <c>outcome-013</c>), in that account's
/// currency, never converted. The table is APPEND-ONLY: a row is never updated or deleted, and a
/// correction is a new row that <see cref="Supersedes"/> the one it corrects - a void (status
/// <c>void</c>) or a replace (a new <c>confirmed</c> row). A row is current while nothing supersedes it.
/// </summary>
/// <param name="Amount">Canonical decimal text; zero allowed, negative only for an adjustment.</param>
/// <param name="ServicePeriodStart">An ISO-8601 UTC instant, as <see cref="Instant"/> stores one.</param>
/// <param name="ServicePeriodEnd">Exclusive, after the start.</param>
/// <param name="Kind"><c>invoice</c>, <c>subscription-charge</c> or <c>adjustment</c>.</param>
/// <param name="Status"><c>confirmed</c> or <c>void</c>.</param>
/// <param name="Supersedes">The id of the row this one corrects, or null.</param>
/// <param name="SupersededBy">Read, not stored: the id of the row that corrects this one, or null.</param>
public sealed record Expense(
    string Id,
    string AccountId,
    string Amount,
    string Currency,
    string ServicePeriodStart,
    string ServicePeriodEnd,
    string Kind,
    string Status,
    string? Supersedes,
    string? Reference,
    string? Note,
    string RecordedBy,
    string RecordedAt,
    string? SupersededBy = null)
{
    /// <summary>True while no other row supersedes this one.</summary>
    public bool Current => SupersededBy is null;
}

/// <summary>The fields a record or a replace sends, as the request gave them. On a record an absent
/// field is a missing one; on a replace an absent field is the old row's.</summary>
public sealed record ExpenseFields(
    Sent? AccountId = null,
    Sent? Amount = null,
    Sent? Currency = null,
    Sent? ServicePeriodStart = null,
    Sent? ServicePeriodEnd = null,
    Sent? Kind = null,
    Sent? Reference = null,
    Sent? Note = null);

/// <summary>An expense write's answer: the row it wrote, or a refusal with its status and one
/// sentence. Every successful expense write writes a row.</summary>
public sealed record ExpenseWrite(Expense? Expense, int Status = 201, string? Refusal = null)
{
    public bool Ok => Refusal is null;

    public static ExpenseWrite Refused(int status, string sentence) => new(null, status, sentence);
}

/// <summary>
/// A recurring charge an account would bring in one billing period, computed at read time and never
/// stored. It is labelled projected and kept apart: it is never Actual Spend and never added to an
/// estimate.
/// </summary>
public sealed record ProjectedCharge(
    string AccountId,
    string PeriodStart,
    string PeriodEnd,
    string Amount,
    string Currency)
{
    /// <summary>Always true: a projection, not a recorded expense.</summary>
    public bool Projected => true;
}
