using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// What an expense may hold and the sentences that refuse anything else, shared by both providers'
/// <see cref="ISpendStore"/> so the store - not only the HTTP layer - holds the closed sets.
///
/// <para>
/// The table is APPEND-ONLY. A correction is a new row whose <c>supersedes</c> names the row it
/// corrects: a void (status <c>void</c>, carrying the old row's account, amount, currency, period and
/// kind) or a replace (a new <c>confirmed</c> row on the same account). A void row is final, and a row
/// already superseded cannot be corrected again; the unique index on <c>supersedes</c> holds that even
/// when two corrections race. <c>kind</c> has no CHECK on the column, so a later kind
/// (<c>provider-reported</c>) needs no edit to a shipped step; the store refuses it today.
/// </para>
/// </summary>
public static class ExpenseRules
{
    public const string KindInvoice = "invoice";
    public const string KindSubscriptionCharge = "subscription-charge";
    public const string KindAdjustment = "adjustment";

    public static readonly IReadOnlyList<string> Kinds = [KindInvoice, KindSubscriptionCharge, KindAdjustment];

    public const string StatusConfirmed = "confirmed";
    public const string StatusVoid = "void";

    /// <summary>The longest reference or note kept: a short note, as an account description is.</summary>
    public const int NoteLimit = BillingAccountRules.DescriptionLimit;

    public const string Columns =
        "id, account_id, amount, currency, service_period_start, service_period_end, kind, status, supersedes, "
        + "reference, note, recorded_by, recorded_at";

    /// <summary>Every row with the id of the row that supersedes it, if any, as the last column.</summary>
    public const string Select =
        "SELECT e.id, e.account_id, e.amount, e.currency, e.service_period_start, e.service_period_end, e.kind, "
        + "e.status, e.supersedes, e.reference, e.note, e.recorded_by, e.recorded_at, s.id "
        + "FROM expenses e LEFT JOIN expenses s ON s.supersedes = e.id";

    public const string AccountRequired = "An expense needs the id of the billing account it was charged on.";
    public const string AmountRefused = "The amount must be a plain number, such as 20 or 49.99.";
    public const string NegativeRefused = "Only an adjustment may have a negative amount.";
    public const string CurrencyRefused = BillingAccountRules.CurrencyRefused;
    public const string KindRefused = "The kind must be invoice, subscription-charge or adjustment.";

    public const string StartRefused =
        "The service period start must be an ISO-8601 date or time, such as 2026-11-01 or 2026-11-01T00:00:00Z.";

    public const string EndRefused =
        "The service period end must be an ISO-8601 date or time, such as 2026-12-01 or 2026-12-01T00:00:00Z.";

    public const string EndNotAfterStart = "The service period end must be after its start; the end is exclusive.";

    public const string OtherAccount =
        "A replacement stays on the same billing account; void this expense and record a new one on the other account.";

    public const string VoidIsFinal = "A void expense cannot be voided or replaced.";

    public const string AlreadyCorrected =
        "This expense has already been voided or replaced, so it cannot be corrected again; correct the current row instead.";

    public static readonly string ReferenceTooLong = $"An expense's reference is a short note of at most {NoteLimit} characters.";

    public static readonly string NoteTooLong = $"An expense's note is a short note of at most {NoteLimit} characters.";

    public static string Missing(string id) => $"No expense has the id {id}.";

    public static string WrongCurrency(string currency) =>
        $"An expense is in its billing account's currency, {currency}; it is never converted.";

    /// <summary>What a recorded or replacing row will hold, every field already read.</summary>
    public sealed record Draft(
        string AccountId,
        string Amount,
        string Currency,
        string Start,
        string End,
        string Kind,
        string? Reference,
        string? Note);

    /// <summary>
    /// The sent fields read over <paramref name="old"/> (null for a record, when every required field
    /// must be sent; the row being replaced otherwise), or the one sentence that refuses them. The
    /// account is not looked up here: <see cref="Against"/> checks the draft against it.
    /// </summary>
    public static (Draft? Draft, string? Refusal) Read(ExpenseFields fields, Expense? old)
    {
        string? account = old?.AccountId;
        if (fields.AccountId is not null || old is null)
        {
            var text = fields.AccountId?.Value?.Trim();
            if (string.IsNullOrEmpty(text)) return (null, AccountRequired);
            account = BillingAccountRules.KeyOf(text) ?? text;
            if (old is not null && !string.Equals(account, old.AccountId, StringComparison.Ordinal)) return (null, OtherAccount);
        }

        var kind = old?.Kind;
        if (fields.Kind is not null || old is null)
        {
            var text = fields.Kind?.Value?.Trim().ToLowerInvariant();
            kind = Kinds.FirstOrDefault(k => string.Equals(k, text, StringComparison.Ordinal));
            if (kind is null) return (null, KindRefused);
        }

        var amount = old?.Amount;
        if (fields.Amount is not null || old is null)
        {
            var text = fields.Amount?.Value;
            if (string.IsNullOrWhiteSpace(text) || !EffectiveCost.TryRate(text, out var value)) return (null, AmountRefused);
            amount = ExecutionSnapshotFacts.Canonical(value == 0 ? 0m : value);
        }

        if (amount!.StartsWith('-') && kind != KindAdjustment) return (null, NegativeRefused);

        var currency = old?.Currency;
        if (fields.Currency is not null || old is null)
        {
            var text = fields.Currency?.Value?.Trim() ?? "";
            if (text.Length != 3 || !text.All(char.IsAsciiLetter)) return (null, CurrencyRefused);
            currency = text.ToUpperInvariant();
        }

        var start = old?.ServicePeriodStart;
        if (fields.ServicePeriodStart is not null || old is null)
        {
            if (BillingAccountRules.Moment(fields.ServicePeriodStart?.Value) is not { } at) return (null, StartRefused);
            start = Instant.Stamp(at);
        }

        var end = old?.ServicePeriodEnd;
        if (fields.ServicePeriodEnd is not null || old is null)
        {
            if (BillingAccountRules.Moment(fields.ServicePeriodEnd?.Value) is not { } at) return (null, EndRefused);
            end = Instant.Stamp(at);
        }

        if (Instant.Parse(end!) <= Instant.Parse(start!)) return (null, EndNotAfterStart);

        if (!Short(fields.Reference, old?.Reference, ReferenceTooLong, out var reference, out var refusal)) return (null, refusal);
        if (!Short(fields.Note, old?.Note, NoteTooLong, out var note, out refusal)) return (null, refusal);

        return (new Draft(account!, amount, currency!, start!, end!, kind!, reference, note), null);
    }

    /// <summary>The refusal of <paramref name="draft"/> against the account it names: missing, or in
    /// another currency. A deactivated account is an account and may take an expense.</summary>
    public static string? Against(Draft draft, BillingAccount? account) =>
        account is null ? BillingAccountRules.Missing(draft.AccountId)
        : !string.Equals(account.Currency, draft.Currency, StringComparison.Ordinal) ? WrongCurrency(account.Currency)
        : null;

    /// <summary>Why <paramref name="target"/> cannot be voided or replaced, with its status, or null when
    /// it can: only a current confirmed row may be corrected.</summary>
    public static (int Status, string Sentence)? Uncorrectable(Expense target) =>
        target.Status == StatusVoid ? (409, VoidIsFinal)
        : target.SupersededBy is not null ? (409, AlreadyCorrected)
        : null;

    /// <summary>A note sent with a void, read as a record's note is, or the sentence that refuses it.</summary>
    public static (string? Note, string? Refusal) VoidNote(string? note)
    {
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return text is { Length: > NoteLimit } ? (null, NoteTooLong) : (text, null);
    }

    /// <summary>A new row of <paramref name="draft"/>, with a store-assigned id.</summary>
    public static Expense New(Draft draft, string status, string? supersedes, string recordedBy) =>
        new(
            Guid.NewGuid().ToString("D"), draft.AccountId, draft.Amount, draft.Currency, draft.Start, draft.End,
            draft.Kind, status, supersedes, draft.Reference, draft.Note, recordedBy, Instant.Now());

    /// <summary>The void row of <paramref name="target"/>: its account, amount, currency, period and kind.</summary>
    public static Expense Voiding(Expense target, string? note, string recordedBy) =>
        New(
            new Draft(target.AccountId, target.Amount, target.Currency, target.ServicePeriodStart, target.ServicePeriodEnd,
                target.Kind, null, note),
            StatusVoid, target.Id, recordedBy);

    /// <summary>The tenant row's detail: the expense id, its account id, the row it supersedes (a void
    /// or a replace), and what it records. No secret: an expense holds none.</summary>
    public static string Detail(Expense row) =>
        JsonSerializer.Serialize(new
        {
            expense = row.Id,
            account = row.AccountId,
            supersedes = row.Supersedes,
            status = row.Status,
            kind = row.Kind,
            amount = row.Amount,
            currency = row.Currency,
            servicePeriodStart = row.ServicePeriodStart,
            servicePeriodEnd = row.ServicePeriodEnd,
        });

    /// <summary>The tenant row for <paramref name="row"/>, on <paramref name="account"/>.</summary>
    public static TriggerAudit Row(TriggerAudit audit, Expense row, BillingAccount account) =>
        audit with { Subject = row.Id, SubjectName = account.Name, Detail = Detail(row) };

    /// <summary>One row, read in <see cref="Select"/> order.</summary>
    public static Expense ReadRow(DbDataReader reader)
    {
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

        return new Expense(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7), Text(8), Text(9), Text(10),
            reader.GetString(11), reader.GetString(12), Text(13));
    }

    public static async Task<List<Expense>> ReadRowsAsync(DbCommand command, CancellationToken ct)
    {
        var rows = new List<Expense>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadRow(reader));
        return rows;
    }

    /// <summary>The parameters an insert binds, by name without the prefix.</summary>
    public static IEnumerable<(string Name, object Value)> Values(Expense row) =>
    [
        ("id", row.Id), ("account", row.AccountId), ("amount", row.Amount), ("currency", row.Currency),
        ("start", row.ServicePeriodStart), ("end", row.ServicePeriodEnd), ("kind", row.Kind), ("status", row.Status),
        ("supersedes", (object?)row.Supersedes ?? DBNull.Value), ("reference", (object?)row.Reference ?? DBNull.Value),
        ("note", (object?)row.Note ?? DBNull.Value), ("recordedBy", row.RecordedBy), ("recordedAt", row.RecordedAt),
    ];

    /// <summary>The insert, with <paramref name="p"/> as the provider's parameter prefix.</summary>
    public static string Insert(char p) =>
        $"INSERT INTO expenses ({Columns}) VALUES ({p}id, {p}account, {p}amount, {p}currency, {p}start, {p}end, "
        + $"{p}kind, {p}status, {p}supersedes, {p}reference, {p}note, {p}recordedBy, {p}recordedAt)";

    private static bool Short(Sent? sent, string? stored, string sentence, out string? value, out string? refusal)
    {
        refusal = null;
        value = stored;
        if (sent is null) return true;

        value = string.IsNullOrWhiteSpace(sent.Value) ? null : sent.Value.Trim();
        if (value is not { Length: > NoteLimit }) return true;

        refusal = sentence;
        return false;
    }
}
