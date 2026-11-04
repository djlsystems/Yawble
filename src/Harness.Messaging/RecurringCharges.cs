using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// PROJECTED RECURRING CHARGES, computed when they are read and never stored. An account that is
/// active and has a recurring amount brings one charge per billing period: the periods step from
/// <c>effective_from</c> by one month (monthly) or one year (annual) - each counted from
/// <c>effective_from</c> itself, so a day past a short month's end is that month's last day and the
/// next period is back on the original day - and stop at <c>effective_to</c> (a period that starts at
/// or after it is not one; the last period ends there). Each period whose start falls in
/// <c>[from, to)</c> is one projected charge of the recurring amount, in the account's currency; a zero
/// amount projects a zero charge.
///
/// <para>
/// A projection is dropped when any CURRENT CONFIRMED expense on that account has a service period
/// that overlaps it: a voided expense (its row superseded by a void) no longer covers its period, and
/// a replaced one covers by its replacement's period. A projection is returned on its own, labelled
/// projected: it is never Actual Spend, never summed into it, and never added to an estimate. Each
/// account projects once, however many members point at it.
/// </para>
/// </summary>
public static class RecurringCharges
{
    /// <summary><paramref name="account"/>'s projected charges in <c>[from, to)</c>, less those a current
    /// confirmed expense in <paramref name="expenses"/> covers.</summary>
    public static IReadOnlyList<ProjectedCharge> Project(
        BillingAccount account, IEnumerable<Expense> expenses, DateTimeOffset from, DateTimeOffset to)
    {
        if (!account.Active || account.RecurringAmount is null || to <= from) return [];

        var covering = expenses
            .Where(e => e.Current && e.Status == ExpenseRules.StatusConfirmed
                && string.Equals(e.AccountId, account.Id, StringComparison.Ordinal))
            .Select(e => (Start: Instant.Parse(e.ServicePeriodStart), End: Instant.Parse(e.ServicePeriodEnd)))
            .ToList();

        var origin = Instant.Parse(account.EffectiveFrom).ToUniversalTime();
        DateTimeOffset? stop = account.EffectiveTo is null ? null : Instant.Parse(account.EffectiveTo).ToUniversalTime();
        var annual = string.Equals(account.BillingPeriod, "annual", StringComparison.Ordinal);

        DateTimeOffset Step(int k) => annual ? origin.AddYears(k) : origin.AddMonths(k);

        var charges = new List<ProjectedCharge>();
        for (var k = 0; ; k++)
        {
            var start = Step(k);
            if (start >= to || (stop is { } end && start >= end)) break;

            var periodEnd = Step(k + 1);
            if (stop is { } last && periodEnd > last) periodEnd = last;

            if (start < from) continue;
            if (covering.Any(c => c.Start < periodEnd && start < c.End)) continue;

            charges.Add(new ProjectedCharge(account.Id, Instant.Stamp(start), Instant.Stamp(periodEnd), account.RecurringAmount, account.Currency));
        }

        return charges;
    }

    /// <summary>Every account's projected charges in <c>[from, to)</c>, read from
    /// <paramref name="spend"/>: oldest account first, each account's periods in order.</summary>
    public static async Task<IReadOnlyList<ProjectedCharge>> ProjectAsync(
        ISpendStore spend, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var accounts = await spend.ListAccountsAsync(ct);
        var expenses = await spend.ListExpensesAsync(ct: ct);
        return [.. accounts.SelectMany(a => Project(a, expenses, from, to))];
    }
}
