using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Harness.SqlServer;
using Harness.SqlServer.Ledger;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="ISpendStore"/>, the billing accounts (<c>outcome-009</c>) and their expenses
/// (<c>outcome-013</c>), as each provider must keep them: the store reads and refuses every field itself,
/// assigns the id, copies <c>instance.id</c> once, writes each real change with its tenant row in one
/// transaction, writes nothing for a change that is none, and the tables refuse a delete. Expenses are
/// append-only: a correction is a new row superseding the old, at most once.
/// </summary>
public abstract class SpendStoreContract : ProviderContract
{
    private const string Email = "person@example.test";

    private static readonly TriggerAudit Created = new(null, Email, TenantActions.BillingAccountCreated, null, null, null);
    private static readonly TriggerAudit Changed = new(null, Email, TenantActions.BillingAccountChanged, null, null, null);
    private static readonly TriggerAudit Deactivated = new(null, Email, TenantActions.BillingAccountDeactivated, null, null, null);
    private static readonly TriggerAudit Reactivated = new(null, Email, TenantActions.BillingAccountReactivated, null, null, null);

    /// <summary>This provider's store over <paramref name="stores"/>' database, with a tenant-row writer
    /// that always fails.</summary>
    protected abstract ISpendStore FailingAudit(ContractStores stores);

    private static BillingAccountFields Fields(
        string? name = "Anthropic API", string? vendor = "Anthropic", string? type = "metered", string? currency = "usd",
        string? period = "monthly", string? amount = null, string? from = "2026-11-01", string? to = null,
        string? description = null, string? scope = null) =>
        new(
            new Sent(name), new Sent(vendor), new Sent(type), new Sent(currency), new Sent(period),
            amount is null ? null : new Sent(amount), new Sent(from), to is null ? null : new Sent(to),
            description is null ? null : new Sent(description), scope is null ? null : new Sent(scope));

    private async Task<(ContractStores Stores, ISpendStore Spend, string InstanceId)> ReadyAsync()
    {
        var stores = await StoresAsync();
        var start = await stores.LedgerStart.RunAsync(Ct);
        return (stores, stores.Spend, start.InstanceId);
    }

    [Fact]
    public async Task A_create_reads_every_field_assigns_a_lower_case_id_and_copies_the_instance_id()
    {
        var (stores, spend, instanceId) = await ReadyAsync();

        var write = await spend.CreateAccountAsync(
            Fields(name: "  Claude Team  ", vendor: " Anthropic ", type: " Subscription ", currency: "eur",
                period: "ANNUAL", amount: "30.00", from: "2026-11-01", to: "2027-10-31T12:00:00Z", description: "  Seats  "),
            Email, Created, Ct);

        Assert.Equal((201, true), (write.Status, write.Written));
        var account = write.Account!;
        Assert.True(Guid.TryParseExact(account.Id, "D", out _));
        Assert.Equal(account.Id.ToLowerInvariant(), account.Id);
        Assert.Equal(
            ("Claude Team", "Anthropic", "subscription", "EUR", "annual", "30", "Seats"),
            (account.Name, account.Vendor, account.BillingType, account.Currency, account.BillingPeriod, account.RecurringAmount, account.Description));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), Instant.Parse(account.EffectiveFrom));
        Assert.Equal(TimeSpan.Zero, Instant.Parse(account.EffectiveFrom).Offset);
        Assert.Equal(new DateTimeOffset(2027, 10, 31, 12, 0, 0, TimeSpan.Zero), Instant.Parse(account.EffectiveTo!));
        Assert.Equal((true, BillingAccountRules.ScopeTenant, instanceId, Email), (account.Active, account.Scope, account.InstanceId, account.CreatedBy));
        Assert.Equal(account.CreatedAt, account.UpdatedAt);

        Assert.Equal(account, await spend.FindAccountAsync(account.Id.ToUpperInvariant(), Ct));
        Assert.Null(await spend.FindAccountAsync(Guid.NewGuid().ToString(), Ct));
        Assert.Null(await spend.FindAccountAsync("not-an-id", Ct));

        // A zero is a zero, stored as one, and two accounts may share a name and a vendor.
        var zero = await spend.CreateAccountAsync(Fields(name: "Claude Team", amount: "0.00"), Email, Created, Ct);
        Assert.Equal("0", zero.Account!.RecurringAmount);

        Assert.Equal([account.Id, zero.Account.Id], (await spend.ListAccountsAsync(Ct)).Select(a => a.Id));

        var row = Assert.Single(await TenantRowsAsync(stores, account.Id));
        Assert.Equal((TenantActions.BillingAccountCreated, "Claude Team", Email), (row.Action, row.SubjectName, row.Email));
        using var detail = JsonDocument.Parse(row.Detail!);
        Assert.Equal(account.Id, detail.RootElement.GetProperty("account").GetString());
        var name = detail.RootElement.GetProperty("changes").GetProperty("name");
        Assert.Equal((JsonValueKind.Null, "Claude Team"), (name.GetProperty("old").ValueKind, name.GetProperty("new").GetString()));
    }

    [Fact]
    public async Task The_store_refuses_a_field_outside_its_set_and_writes_nothing()
    {
        var (stores, spend, _) = await ReadyAsync();

        (BillingAccountFields Fields, string Refusal)[] refused =
        [
            (Fields(name: "  "), BillingAccountRules.NameRequired),
            (Fields(name: null), BillingAccountRules.NameRequired),
            (Fields() with { Vendor = null }, BillingAccountRules.VendorRequired),
            (Fields(type: "prepaid"), BillingAccountRules.BillingTypeRefused),
            (Fields(currency: "US"), BillingAccountRules.CurrencyRefused),
            (Fields(currency: "U$D"), BillingAccountRules.CurrencyRefused),
            (Fields(period: "weekly"), BillingAccountRules.BillingPeriodRefused),
            (Fields(amount: "-1"), BillingAccountRules.RecurringAmountRefused),
            (Fields(amount: "twenty"), BillingAccountRules.RecurringAmountRefused),
            (Fields(from: "11/01/2026"), BillingAccountRules.EffectiveFromRefused),
            (Fields(from: null), BillingAccountRules.EffectiveFromRefused),
            (Fields(to: "soon"), BillingAccountRules.EffectiveToRefused),
            (Fields(from: "2026-11-02", to: "2026-11-01"), BillingAccountRules.EffectiveToBeforeFrom),
            (Fields(scope: "shared"), BillingAccountRules.ScopeRefused),
            (Fields(description: new string('x', BillingAccountRules.DescriptionLimit + 1)), BillingAccountRules.DescriptionTooLong),
        ];

        foreach (var (fields, refusal) in refused)
        {
            var write = await spend.CreateAccountAsync(fields, Email, Created, Ct);
            Assert.Equal((400, refusal), (write.Status, write.Refusal));
        }

        Assert.Empty(await spend.ListAccountsAsync(Ct));
        Assert.Equal(0, await stores.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE action LIKE 'billing.%'"));

        // An effective-to equal to effective-from is allowed; scope tenant may be sent.
        Assert.True((await spend.CreateAccountAsync(Fields(from: "2026-11-01", to: "2026-11-01T00:00:00Z", scope: "tenant"), Email, Created, Ct)).Ok);
    }

    [Fact]
    public async Task A_create_with_no_instance_id_is_refused_and_writes_nothing()
    {
        var stores = await StoresAsync();

        var write = await stores.Spend.CreateAccountAsync(Fields(), Email, Created, Ct);

        Assert.Equal((409, BillingAccountRules.NoInstanceId), (write.Status, write.Refusal));
        Assert.Empty(await stores.Spend.ListAccountsAsync(Ct));
        Assert.Equal(0, await stores.LongAsync("SELECT COUNT(*) FROM tenant_events WHERE action LIKE 'billing.%'"));
    }

    [Fact]
    public async Task An_edit_changes_only_the_sent_fields_and_one_that_changes_nothing_writes_nothing()
    {
        var (stores, spend, _) = await ReadyAsync();
        var account = (await spend.CreateAccountAsync(
            Fields(amount: "20", to: "2027-01-01", description: "Seats"), Email, Created, Ct)).Account!;

        var edited = await spend.EditAccountAsync(
            account.Id, new BillingAccountFields(Vendor: new Sent("Anthropic PBC"), RecurringAmount: new Sent("25.50")), Changed, Ct);
        Assert.True(edited.Written);
        Assert.Equal(account with { Vendor = "Anthropic PBC", RecurringAmount = "25.5", UpdatedAt = edited.Account!.UpdatedAt }, edited.Account);
        Assert.True(string.CompareOrdinal(edited.Account.UpdatedAt, account.UpdatedAt) >= 0);

        // Null clears only description, effective_to and recurring_amount.
        var cleared = await spend.EditAccountAsync(
            account.Id, new BillingAccountFields(Description: new Sent(null), EffectiveTo: new Sent(null), RecurringAmount: new Sent(null)), Changed, Ct);
        Assert.Equal((null, null, null), (cleared.Account!.Description, cleared.Account.EffectiveTo, cleared.Account.RecurringAmount));

        foreach (var (fields, refusal) in new (BillingAccountFields, string)[]
        {
            (new(Name: new Sent(null)), BillingAccountRules.NameRequired),
            (new(Vendor: new Sent(" ")), BillingAccountRules.VendorRequired),
            (new(BillingType: new Sent(null)), BillingAccountRules.BillingTypeRefused),
            (new(Currency: new Sent(null)), BillingAccountRules.CurrencyRefused),
            (new(BillingPeriod: new Sent(null)), BillingAccountRules.BillingPeriodRefused),
            (new(EffectiveFrom: new Sent(null)), BillingAccountRules.EffectiveFromRefused),
            (new(EffectiveTo: new Sent("2026-10-31")), BillingAccountRules.EffectiveToBeforeFrom),
            (new(Scope: new Sent("shared")), BillingAccountRules.ScopeRefused),
        })
        {
            var write = await spend.EditAccountAsync(account.Id, fields, Changed, Ct);
            Assert.Equal((400, refusal), (write.Status, write.Refusal));
        }

        // The same values again, in other spellings, change nothing.
        var same = await spend.EditAccountAsync(
            account.Id,
            new BillingAccountFields(Name: new Sent(" Anthropic API "), Currency: new Sent("usd"), BillingType: new Sent("METERED"), EffectiveFrom: new Sent("2026-11-01T00:00:00Z")),
            Changed, Ct);
        Assert.False(same.Written);
        Assert.Equal(cleared.Account, same.Account);
        Assert.Equal(cleared.Account, await spend.FindAccountAsync(account.Id, Ct));

        var rows = await TenantRowsAsync(stores, account.Id);
        Assert.Equal(
            [TenantActions.BillingAccountCreated, TenantActions.BillingAccountChanged, TenantActions.BillingAccountChanged],
            rows.Select(r => r.Action));
        using var detail = JsonDocument.Parse(rows[1].Detail!);
        var changes = detail.RootElement.GetProperty("changes");
        Assert.Equal(["vendor", "recurringAmount"], changes.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("20", "25.5"), (changes.GetProperty("recurringAmount").GetProperty("old").GetString(), changes.GetProperty("recurringAmount").GetProperty("new").GetString()));

        // Created by and created at never move; a missing id is 404.
        Assert.Equal((account.CreatedBy, account.CreatedAt, account.InstanceId), (same.Account!.CreatedBy, same.Account.CreatedAt, same.Account.InstanceId));
        Assert.Equal(404, (await spend.EditAccountAsync(Guid.NewGuid().ToString(), new BillingAccountFields(Name: new Sent("X")), Changed, Ct)).Status);
    }

    [Fact]
    public async Task Deactivate_and_reactivate_move_active_and_a_repeat_writes_nothing()
    {
        var (stores, spend, _) = await ReadyAsync();
        var account = (await spend.CreateAccountAsync(Fields(), Email, Created, Ct)).Account!;

        var off = await spend.SetAccountActiveAsync(account.Id, false, Deactivated, Ct);
        Assert.Equal((true, false), (off.Written, off.Account!.Active));

        var again = await spend.SetAccountActiveAsync(account.Id, false, Deactivated, Ct);
        Assert.Equal((false, off.Account), (again.Written, again.Account));

        // A deactivated account is still listed, and may be edited.
        Assert.False(Assert.Single(await spend.ListAccountsAsync(Ct)).Active);

        var on = await spend.SetAccountActiveAsync(account.Id, true, Reactivated, Ct);
        Assert.True(on.Account!.Active);
        Assert.False((await spend.SetAccountActiveAsync(account.Id, true, Reactivated, Ct)).Written);

        Assert.Equal(
            [TenantActions.BillingAccountCreated, TenantActions.BillingAccountDeactivated, TenantActions.BillingAccountReactivated],
            (await TenantRowsAsync(stores, account.Id)).Select(r => r.Action));
        Assert.Equal(404, (await spend.SetAccountActiveAsync(Guid.NewGuid().ToString(), false, Deactivated, Ct)).Status);
    }

    [Fact]
    public async Task A_write_whose_tenant_row_cannot_be_written_does_not_happen()
    {
        var (stores, spend, _) = await ReadyAsync();
        var account = (await spend.CreateAccountAsync(Fields(), Email, Created, Ct)).Account!;
        var failing = FailingAudit(stores);

        await Assert.ThrowsAnyAsync<Exception>(() => failing.CreateAccountAsync(Fields(name: "Lost"), Email, Created, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => failing.EditAccountAsync(account.Id, new BillingAccountFields(Name: new Sent("Lost")), Changed, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => failing.SetAccountActiveAsync(account.Id, false, Deactivated, Ct));

        Assert.Equal(account, Assert.Single(await spend.ListAccountsAsync(Ct)));
        Assert.Single(await TenantRowsAsync(stores, account.Id));
    }

    [Fact]
    public async Task The_table_refuses_a_delete_and_allows_an_update()
    {
        var (stores, spend, _) = await ReadyAsync();
        var account = (await spend.CreateAccountAsync(Fields(), Email, Created, Ct)).Account!;

        await Assert.ThrowsAnyAsync<Exception>(() => stores.ExecuteAsync($"DELETE FROM billing_accounts WHERE id = '{account.Id}'"));
        Assert.Equal(1, await stores.ExecuteAsync($"UPDATE billing_accounts SET description = 'raw' WHERE id = '{account.Id}'"));
        Assert.Equal("raw", (await spend.FindAccountAsync(account.Id, Ct))!.Description);
    }

    // ---- expenses ----

    private static readonly TriggerAudit Recorded = new(null, Email, TenantActions.BillingExpenseRecorded, null, null, null);
    private static readonly TriggerAudit Voided = new(null, Email, TenantActions.BillingExpenseVoided, null, null, null);
    private static readonly TriggerAudit Replaced = new(null, Email, TenantActions.BillingExpenseReplaced, null, null, null);

    private static ExpenseFields Charge(
        string? account, string? amount = "20", string? currency = "USD", string? start = "2026-11-01",
        string? end = "2026-12-01", string? kind = "invoice", string? reference = null, string? note = null) =>
        new(
            account is null ? null : new Sent(account), amount is null ? null : new Sent(amount),
            currency is null ? null : new Sent(currency), start is null ? null : new Sent(start), end is null ? null : new Sent(end),
            kind is null ? null : new Sent(kind), reference is null ? null : new Sent(reference), note is null ? null : new Sent(note));

    private async Task<(ContractStores Stores, ISpendStore Spend, BillingAccount Account)> AccountAsync(string? amount = null)
    {
        var (stores, spend, _) = await ReadyAsync();
        var account = (await spend.CreateAccountAsync(Fields(amount: amount), Email, Created, Ct)).Account!;
        return (stores, spend, account);
    }

    [Fact]
    public async Task A_recorded_expense_reads_every_field_and_writes_its_tenant_row()
    {
        var (stores, spend, account) = await AccountAsync();

        var write = await spend.RecordExpenseAsync(
            Charge(account.Id.ToUpperInvariant(), amount: "20.50", currency: "usd", start: "2026-11-01", end: "2026-12-01T00:00:00Z",
                kind: " Invoice ", reference: "  INV-1  ", note: " November "),
            Email, Recorded, Ct);

        Assert.Equal((201, null), (write.Status, write.Refusal));
        var row = write.Expense!;
        Assert.True(Guid.TryParseExact(row.Id, "D", out _));
        Assert.Equal(row.Id.ToLowerInvariant(), row.Id);
        Assert.Equal(
            (account.Id, "20.5", "USD", "invoice", ExpenseRules.StatusConfirmed, (string?)null, "INV-1", "November", Email),
            (row.AccountId, row.Amount, row.Currency, row.Kind, row.Status, row.Supersedes, row.Reference, row.Note, row.RecordedBy));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), Instant.Parse(row.ServicePeriodStart));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), Instant.Parse(row.ServicePeriodEnd));
        Assert.True(row.Current);

        Assert.Equal(row, await spend.FindExpenseAsync(row.Id.ToUpperInvariant(), Ct));
        Assert.Null(await spend.FindExpenseAsync(Guid.NewGuid().ToString(), Ct));
        Assert.Equal([row], await spend.ListExpensesAsync(ct: Ct));
        Assert.Equal([row], await spend.ListExpensesAsync(account.Id, Ct));
        Assert.Empty(await spend.ListExpensesAsync(Guid.NewGuid().ToString(), Ct));

        // A zero is a charge; an adjustment may be negative; a deactivated account still takes one.
        Assert.Equal("0", (await spend.RecordExpenseAsync(Charge(account.Id, amount: "0.00"), Email, Recorded, Ct)).Expense!.Amount);
        Assert.Equal("-5", (await spend.RecordExpenseAsync(Charge(account.Id, amount: "-5.00", kind: "adjustment"), Email, Recorded, Ct)).Expense!.Amount);
        await spend.SetAccountActiveAsync(account.Id, false, Deactivated, Ct);
        Assert.True((await spend.RecordExpenseAsync(Charge(account.Id, kind: "subscription-charge"), Email, Recorded, Ct)).Ok);

        var tenant = Assert.Single(await TenantRowsAsync(stores, row.Id));
        Assert.Equal((TenantActions.BillingExpenseRecorded, account.Name, Email), (tenant.Action, tenant.SubjectName, tenant.Email));
        using var detail = JsonDocument.Parse(tenant.Detail!);
        Assert.Equal((row.Id, account.Id, JsonValueKind.Null), (
            detail.RootElement.GetProperty("expense").GetString(), detail.RootElement.GetProperty("account").GetString(),
            detail.RootElement.GetProperty("supersedes").ValueKind));
    }

    [Fact]
    public async Task The_store_refuses_an_expense_outside_its_rules_and_writes_nothing()
    {
        var (stores, spend, account) = await AccountAsync();
        var missing = Guid.NewGuid().ToString();

        (ExpenseFields Fields, string Refusal)[] refused =
        [
            (Charge(null), ExpenseRules.AccountRequired),
            (Charge("  "), ExpenseRules.AccountRequired),
            (Charge(missing), BillingAccountRules.Missing(missing)),
            (Charge(account.Id, amount: null), ExpenseRules.AmountRefused),
            (Charge(account.Id, amount: "twenty"), ExpenseRules.AmountRefused),
            (Charge(account.Id, amount: "-1"), ExpenseRules.NegativeRefused),
            (Charge(account.Id, amount: "-1", kind: "subscription-charge"), ExpenseRules.NegativeRefused),
            (Charge(account.Id, currency: null), ExpenseRules.CurrencyRefused),
            (Charge(account.Id, currency: "US"), ExpenseRules.CurrencyRefused),
            (Charge(account.Id, currency: "EUR"), ExpenseRules.WrongCurrency("USD")),
            (Charge(account.Id, kind: null), ExpenseRules.KindRefused),
            (Charge(account.Id, kind: "provider-reported"), ExpenseRules.KindRefused),
            (Charge(account.Id, kind: "refund"), ExpenseRules.KindRefused),
            (Charge(account.Id, start: null), ExpenseRules.StartRefused),
            (Charge(account.Id, start: "11/01/2026"), ExpenseRules.StartRefused),
            (Charge(account.Id, end: "soon"), ExpenseRules.EndRefused),
            (Charge(account.Id, start: "2026-11-01", end: "2026-11-01"), ExpenseRules.EndNotAfterStart),
            (Charge(account.Id, start: "2026-11-02", end: "2026-11-01"), ExpenseRules.EndNotAfterStart),
            (Charge(account.Id, reference: new string('x', ExpenseRules.NoteLimit + 1)), ExpenseRules.ReferenceTooLong),
            (Charge(account.Id, note: new string('x', ExpenseRules.NoteLimit + 1)), ExpenseRules.NoteTooLong),
        ];

        foreach (var (fields, refusal) in refused)
        {
            var write = await spend.RecordExpenseAsync(fields, Email, Recorded, Ct);
            Assert.Equal((400, refusal), (write.Status, write.Refusal));
        }

        Assert.Empty(await spend.ListExpensesAsync(ct: Ct));
        Assert.Equal(0, await stores.LongAsync("SELECT COUNT(*) FROM tenant_events WHERE action LIKE 'billing.expense-%'"));
    }

    [Fact]
    public async Task A_void_is_a_new_row_carrying_the_old_and_a_void_or_superseded_row_cannot_be_corrected_again()
    {
        var (stores, spend, account) = await AccountAsync();
        var original = (await spend.RecordExpenseAsync(Charge(account.Id, kind: "adjustment", amount: "-3", reference: "INV-9"), Email, Recorded, Ct)).Expense!;

        var voided = await spend.VoidExpenseAsync(original.Id, "  Entered twice  ", Email, Voided, Ct);

        Assert.Equal(201, voided.Status);
        var row = voided.Expense!;
        Assert.NotEqual(original.Id, row.Id);
        Assert.Equal(
            (original.AccountId, original.Amount, original.Currency, original.ServicePeriodStart, original.ServicePeriodEnd, original.Kind),
            (row.AccountId, row.Amount, row.Currency, row.ServicePeriodStart, row.ServicePeriodEnd, row.Kind));
        Assert.Equal((ExpenseRules.StatusVoid, original.Id, "Entered twice", (string?)null), (row.Status, row.Supersedes, row.Note, row.Reference));

        // The voided row is kept, unchanged but for who supersedes it; the void is current.
        var listed = await spend.ListExpensesAsync(ct: Ct);
        Assert.Equal([original with { SupersededBy = row.Id }, row], listed);
        Assert.False(listed[0].Current);
        Assert.True(listed[1].Current);

        Assert.Equal((409, ExpenseRules.AlreadyCorrected), Refusal(await spend.VoidExpenseAsync(original.Id, null, Email, Voided, Ct)));
        Assert.Equal((409, ExpenseRules.AlreadyCorrected), Refusal(await spend.ReplaceExpenseAsync(original.Id, Charge(null), Email, Replaced, Ct)));
        Assert.Equal((409, ExpenseRules.VoidIsFinal), Refusal(await spend.VoidExpenseAsync(row.Id, null, Email, Voided, Ct)));
        Assert.Equal((409, ExpenseRules.VoidIsFinal), Refusal(await spend.ReplaceExpenseAsync(row.Id, Charge(null), Email, Replaced, Ct)));
        Assert.Equal(404, (await spend.VoidExpenseAsync(Guid.NewGuid().ToString(), null, Email, Voided, Ct)).Status);
        Assert.Equal(404, (await spend.ReplaceExpenseAsync("not-an-id", Charge(null), Email, Replaced, Ct)).Status);

        var other = (await spend.RecordExpenseAsync(Charge(account.Id), Email, Recorded, Ct)).Expense!;
        Assert.Equal((400, ExpenseRules.NoteTooLong), Refusal(await spend.VoidExpenseAsync(other.Id, new string('x', ExpenseRules.NoteLimit + 1), Email, Voided, Ct)));

        Assert.Equal(3, (await spend.ListExpensesAsync(ct: Ct)).Count);
        var tenant = Assert.Single(await TenantRowsAsync(stores, row.Id));
        Assert.Equal(TenantActions.BillingExpenseVoided, tenant.Action);
        using var detail = JsonDocument.Parse(tenant.Detail!);
        Assert.Equal((row.Id, account.Id, original.Id), (
            detail.RootElement.GetProperty("expense").GetString(), detail.RootElement.GetProperty("account").GetString(),
            detail.RootElement.GetProperty("supersedes").GetString()));
    }

    [Fact]
    public async Task A_replace_is_a_new_confirmed_row_on_the_same_account_over_the_old_fields()
    {
        var (stores, spend, account) = await AccountAsync();
        var other = (await spend.CreateAccountAsync(Fields(name: "Other"), Email, Created, Ct)).Account!;
        var original = (await spend.RecordExpenseAsync(Charge(account.Id, reference: "INV-1", note: "First"), Email, Recorded, Ct)).Expense!;

        Assert.Equal((400, ExpenseRules.OtherAccount), Refusal(await spend.ReplaceExpenseAsync(original.Id, Charge(other.Id, kind: null), Email, Replaced, Ct)));
        Assert.Equal((400, ExpenseRules.WrongCurrency("USD")), Refusal(await spend.ReplaceExpenseAsync(original.Id, Charge(null, amount: null, currency: "EUR", start: null, end: null, kind: null), Email, Replaced, Ct)));
        Assert.Equal((400, ExpenseRules.AmountRefused), Refusal(await spend.ReplaceExpenseAsync(original.Id, new ExpenseFields(Amount: new Sent(null)), Email, Replaced, Ct)));
        Assert.Equal((400, ExpenseRules.EndNotAfterStart), Refusal(await spend.ReplaceExpenseAsync(original.Id, new ExpenseFields(ServicePeriodEnd: new Sent("2026-10-01")), Email, Replaced, Ct)));
        Assert.Equal((400, ExpenseRules.NegativeRefused), Refusal(await spend.ReplaceExpenseAsync(original.Id, new ExpenseFields(Amount: new Sent("-1")), Email, Replaced, Ct)));
        Assert.Single(await spend.ListExpensesAsync(ct: Ct));

        var replaced = await spend.ReplaceExpenseAsync(
            original.Id,
            new ExpenseFields(AccountId: new Sent(account.Id), Amount: new Sent("25"), ServicePeriodEnd: new Sent("2026-11-16"), Note: new Sent(null)),
            Email, Replaced, Ct);

        var row = replaced.Expense!;
        Assert.Equal(
            (account.Id, "25", "USD", original.ServicePeriodStart, "invoice", ExpenseRules.StatusConfirmed, original.Id, "INV-1", (string?)null),
            (row.AccountId, row.Amount, row.Currency, row.ServicePeriodStart, row.Kind, row.Status, row.Supersedes, row.Reference, row.Note));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), Instant.Parse(row.ServicePeriodEnd));

        // The replacement is current and may itself be corrected; the original may not.
        Assert.Equal([original with { SupersededBy = row.Id }, row], await spend.ListExpensesAsync(ct: Ct));
        Assert.Equal((409, ExpenseRules.AlreadyCorrected), Refusal(await spend.ReplaceExpenseAsync(original.Id, new ExpenseFields(), Email, Replaced, Ct)));
        Assert.True((await spend.VoidExpenseAsync(row.Id, null, Email, Voided, Ct)).Ok);

        var tenant = Assert.Single(await TenantRowsAsync(stores, row.Id));
        Assert.Equal(TenantActions.BillingExpenseReplaced, tenant.Action);
        using var detail = JsonDocument.Parse(tenant.Detail!);
        Assert.Equal(original.Id, detail.RootElement.GetProperty("supersedes").GetString());
    }

    [Fact]
    public async Task Two_corrections_of_one_expense_at_once_land_one_and_refuse_the_other()
    {
        var (_, spend, account) = await AccountAsync();
        var original = (await spend.RecordExpenseAsync(Charge(account.Id), Email, Recorded, Ct)).Expense!;

        var writes = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => i % 2 == 0
                ? spend.VoidExpenseAsync(original.Id, $"void {i}", Email, Voided, Ct)
                : spend.ReplaceExpenseAsync(original.Id, new ExpenseFields(Amount: new Sent($"{i}")), Email, Replaced, Ct)));

        Assert.Single(writes, w => w.Ok);
        Assert.All(writes.Where(w => !w.Ok), w => Assert.Equal((409, ExpenseRules.AlreadyCorrected), Refusal(w)));
        Assert.Equal(2, (await spend.ListExpensesAsync(ct: Ct)).Count);
    }

    [Fact]
    public async Task An_expense_write_whose_tenant_row_cannot_be_written_does_not_happen()
    {
        var (stores, spend, account) = await AccountAsync();
        var original = (await spend.RecordExpenseAsync(Charge(account.Id), Email, Recorded, Ct)).Expense!;
        var failing = FailingAudit(stores);

        await Assert.ThrowsAnyAsync<Exception>(() => failing.RecordExpenseAsync(Charge(account.Id, amount: "99"), Email, Recorded, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => failing.VoidExpenseAsync(original.Id, null, Email, Voided, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => failing.ReplaceExpenseAsync(original.Id, new ExpenseFields(Amount: new Sent("1")), Email, Replaced, Ct));

        Assert.Equal([original], await spend.ListExpensesAsync(ct: Ct));
        Assert.Equal(1, await stores.LongAsync("SELECT COUNT(*) FROM tenant_events WHERE action LIKE 'billing.expense-%'"));

        // The original is still current, so a correction through the real store still lands.
        Assert.True((await spend.VoidExpenseAsync(original.Id, null, Email, Voided, Ct)).Ok);
    }

    [Fact]
    public async Task The_expenses_table_refuses_every_update_and_delete_and_a_second_row_superseding_one()
    {
        var (stores, spend, account) = await AccountAsync();
        var original = (await spend.RecordExpenseAsync(Charge(account.Id), Email, Recorded, Ct)).Expense!;
        var voided = (await spend.VoidExpenseAsync(original.Id, null, Email, Voided, Ct)).Expense!;

        await Assert.ThrowsAnyAsync<Exception>(() => stores.ExecuteAsync($"UPDATE expenses SET amount = '1' WHERE id = '{original.Id}'"));
        await Assert.ThrowsAnyAsync<Exception>(() => stores.ExecuteAsync($"UPDATE expenses SET status = 'confirmed' WHERE id = '{voided.Id}'"));
        await Assert.ThrowsAnyAsync<Exception>(() => stores.ExecuteAsync($"DELETE FROM expenses WHERE id = '{voided.Id}'"));
        await Assert.ThrowsAnyAsync<Exception>(() => stores.ExecuteAsync(
            $"""
             INSERT INTO expenses ({ExpenseRules.Columns})
             VALUES ('{Guid.NewGuid()}', '{account.Id}', '1', 'USD', '{original.ServicePeriodStart}', '{original.ServicePeriodEnd}',
                 'invoice', 'confirmed', '{original.Id}', NULL, NULL, 'raw', '{Instant.Now()}')
             """));

        Assert.Equal([original with { SupersededBy = voided.Id }, voided], await spend.ListExpensesAsync(ct: Ct));
    }

    [Fact]
    public async Task A_voided_expense_no_longer_covers_its_period_and_a_replaced_one_covers_by_its_new_period()
    {
        var (_, spend, account) = await AccountAsync(amount: "30");
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero);

        async Task<string[]> ProjectedStarts() =>
            [.. (await RecurringCharges.ProjectAsync(spend, from, to, Ct)).Select(c => c.PeriodStart[..10])];

        Assert.Equal(["2026-11-01", "2026-12-01", "2027-01-01"], await ProjectedStarts());

        // November's invoice covers November.
        var november = (await spend.RecordExpenseAsync(Charge(account.Id, start: "2026-11-01", end: "2026-12-01"), Email, Recorded, Ct)).Expense!;
        Assert.Equal(["2026-12-01", "2027-01-01"], await ProjectedStarts());

        // Voided, it covers nothing, and November is projected again.
        await spend.VoidExpenseAsync(november.Id, null, Email, Voided, Ct);
        Assert.Equal(["2026-11-01", "2026-12-01", "2027-01-01"], await ProjectedStarts());

        // Replaced, the replacement's period is what covers: December, not November.
        var december = (await spend.RecordExpenseAsync(Charge(account.Id, start: "2026-11-01", end: "2026-12-01"), Email, Recorded, Ct)).Expense!;
        Assert.Equal(["2026-12-01", "2027-01-01"], await ProjectedStarts());
        await spend.ReplaceExpenseAsync(december.Id, new ExpenseFields(ServicePeriodStart: new Sent("2026-12-01"), ServicePeriodEnd: new Sent("2027-01-01")), Email, Replaced, Ct);
        Assert.Equal(["2026-11-01", "2027-01-01"], await ProjectedStarts());

        // Nothing projected was stored.
        Assert.Equal(4, (await spend.ListExpensesAsync(ct: Ct)).Count);
    }

    private static (int, string?) Refusal(ExpenseWrite write) => (write.Status, write.Refusal);

    private sealed record TenantRow(string Action, string? SubjectName, string? Email, string? Detail);

    private static async Task<List<TenantRow>> TenantRowsAsync(ContractStores stores, string subject) =>
        [.. (await stores.RowsAsync(
                $"SELECT action, subject_name, actor_email, detail FROM tenant_events WHERE subject = '{subject}' ORDER BY seq"))
            .Select(r => new TenantRow((string)r[0]!, r[1] as string, r[2] as string, r[3] as string))];
}

public sealed class SqliteSpendStoreTests : SpendStoreContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());

    protected override ISpendStore FailingAudit(ContractStores stores) =>
        new SqliteSpendStore(((SqliteContractStores)stores).Path, (_, _, _, _) => throw new InvalidOperationException("no tenant row"));
}

public sealed class SqlServerSpendStoreTests : SpendStoreContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();

    protected override ISpendStore FailingAudit(ContractStores stores) =>
        new SqlServerSpendStore(((SqlServerContractStores)stores).ConnectionString, (_, _, _, _) => throw new InvalidOperationException("no tenant row"));
}
