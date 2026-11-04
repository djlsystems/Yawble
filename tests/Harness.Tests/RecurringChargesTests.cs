using Harness.Contracts;
using Harness.Messaging;

namespace Harness.Tests;

/// <summary>
/// PROJECTED RECURRING CHARGES, the read-time rule (<see cref="RecurringCharges"/>): periods step from
/// the account's effective-from by a month or a year, each counted from effective-from so a month-end
/// day clamps and comes back, stop at effective-to, and a projection a current confirmed expense covers
/// is dropped. The store-backed half - a void uncovers, a replace covers by its new period - is
/// <c>SpendStoreContract.A_voided_expense_no_longer_covers_its_period_and_a_replaced_one_covers_by_its_new_period</c>.
/// </summary>
public sealed class RecurringChargesTests
{
    private const string Id = "3f2b8c1e-9a4d-4e2f-b6a1-0c5d7e8f9a10";

    private static DateTimeOffset At(int year, int month, int day) => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private static BillingAccount Account(
        string period = "monthly", string? amount = "30", string from = "2026-01-31", string? to = null, bool active = true) =>
        new(
            Id, "Claude Team", "Anthropic", "subscription", "EUR", period, amount, Instant.Stamp(DateTimeOffset.Parse(from + "T00:00:00Z")),
            to is null ? null : Instant.Stamp(DateTimeOffset.Parse(to + "T00:00:00Z")), active, null, "tenant", "instance", "p", "x", "x");

    private static Expense Paid(string start, string end, string status = ExpenseRules.StatusConfirmed, string? supersededBy = null, string account = Id) =>
        new(
            Guid.NewGuid().ToString(), account, "30", "EUR", Instant.Stamp(DateTimeOffset.Parse(start + "T00:00:00Z")),
            Instant.Stamp(DateTimeOffset.Parse(end + "T00:00:00Z")), "subscription-charge", status, null, null, null, "p", Instant.Now(), supersededBy);

    private static string[] Starts(IReadOnlyList<ProjectedCharge> charges) => [.. charges.Select(c => c.PeriodStart[..10])];

    [Fact]
    public void Monthly_periods_step_from_effective_from_and_a_month_end_day_clamps_then_comes_back()
    {
        var charges = RecurringCharges.Project(Account(), [], At(2026, 1, 1), At(2026, 6, 1));

        Assert.Equal(["2026-01-31", "2026-02-28", "2026-03-31", "2026-04-30", "2026-05-31"], Starts(charges));
        Assert.Equal("2026-03-31", charges[1].PeriodEnd[..10]);
        Assert.All(charges, c => Assert.Equal(("30", "EUR", Id, true), (c.Amount, c.Currency, c.AccountId, c.Projected)));
    }

    [Fact]
    public void Annual_periods_step_by_a_year_and_a_leap_day_clamps_to_the_last_of_february()
    {
        var charges = RecurringCharges.Project(Account(period: "annual", from: "2028-02-29"), [], At(2028, 1, 1), At(2033, 1, 1));

        Assert.Equal(["2028-02-29", "2029-02-28", "2030-02-28", "2031-02-28", "2032-02-29"], Starts(charges));
    }

    [Fact]
    public void Only_a_period_whose_start_is_in_the_range_is_projected()
    {
        var charges = RecurringCharges.Project(Account(from: "2026-01-15"), [], At(2026, 3, 1), At(2026, 5, 15));

        // February's period runs into March but starts before it; May 15 is the range's exclusive end.
        Assert.Equal(["2026-03-15", "2026-04-15"], Starts(charges));
        Assert.Empty(RecurringCharges.Project(Account(), [], At(2026, 3, 1), At(2026, 3, 1)));
    }

    [Fact]
    public void Periods_stop_at_effective_to_and_the_last_one_ends_there()
    {
        var charges = RecurringCharges.Project(Account(from: "2026-01-01", to: "2026-03-15"), [], At(2026, 1, 1), At(2027, 1, 1));

        Assert.Equal(["2026-01-01", "2026-02-01", "2026-03-01"], Starts(charges));
        Assert.Equal("2026-03-15", charges[^1].PeriodEnd[..10]);

        // A period that starts exactly at effective-to is not one.
        Assert.Equal(["2026-01-01"], Starts(RecurringCharges.Project(Account(from: "2026-01-01", to: "2026-02-01"), [], At(2026, 1, 1), At(2027, 1, 1))));
    }

    [Fact]
    public void An_inactive_account_or_one_with_no_recurring_amount_projects_nothing_and_a_zero_projects_a_zero()
    {
        Assert.Empty(RecurringCharges.Project(Account(active: false), [], At(2026, 1, 1), At(2027, 1, 1)));
        Assert.Empty(RecurringCharges.Project(Account(amount: null), [], At(2026, 1, 1), At(2027, 1, 1)));

        var zero = RecurringCharges.Project(Account(amount: "0"), [], At(2026, 1, 1), At(2026, 3, 1));
        Assert.Equal(["0", "0"], zero.Select(c => c.Amount));
    }

    [Fact]
    public void A_current_confirmed_expense_whose_period_overlaps_drops_the_projection()
    {
        var account = Account(from: "2026-01-01");

        // A charge for part of February covers February; one ending exactly where March starts does not cover March.
        var charges = RecurringCharges.Project(account, [Paid("2026-02-10", "2026-03-01")], At(2026, 1, 1), At(2026, 4, 1));
        Assert.Equal(["2026-01-01", "2026-03-01"], Starts(charges));

        // A void row, a superseded row and another account's row cover nothing.
        var notCovering = new[]
        {
            Paid("2026-01-01", "2026-04-01", status: ExpenseRules.StatusVoid),
            Paid("2026-01-01", "2026-04-01", supersededBy: Guid.NewGuid().ToString()),
            Paid("2026-01-01", "2026-04-01", account: Guid.NewGuid().ToString()),
        };
        Assert.Equal(["2026-01-01", "2026-02-01", "2026-03-01"], Starts(RecurringCharges.Project(account, notCovering, At(2026, 1, 1), At(2026, 4, 1))));
    }
}
