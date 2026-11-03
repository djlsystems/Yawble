using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Harness.SqlServer;
using Harness.SqlServer.Ledger;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="ISpendStore"/>, the billing accounts (<c>outcome-009</c>), as each provider must keep
/// them: the store reads and refuses every field itself, assigns the id, copies <c>instance.id</c> once,
/// writes each real change with its tenant row in one transaction, writes nothing for a change that is
/// none, and the table refuses a delete.
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
