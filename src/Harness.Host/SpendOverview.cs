using System.Globalization;
using Harness.Contracts;
using Harness.Messaging;

namespace Harness.Host;

/// <summary>
/// THE SPEND PAGE'S OVERVIEW over one period <c>[from, to)</c>, read, never stored. Three measurements are
/// kept apart and never added together:
///
/// <list type="bullet">
/// <item><b>Actual Spend</b>: the CURRENT CONFIRMED expenses - status <c>confirmed</c>, superseded by
/// nothing - whose service period STARTS in the period, each counted WHOLE in that one period (the period
/// edge rule: an expense is never split by days, and a run of adjacent periods counts it once). A voided
/// expense is counted by its current row, the void, which is not confirmed: nothing. A replaced one is
/// counted by its replacement alone. Per currency, never converted.</item>
/// <item><b>Projected</b>: <see cref="RecurringCharges"/> for the period, as S2 built it - a projection an
/// overlapping current confirmed expense covers is already dropped there. Per currency, labelled
/// projected, never in Actual Spend.</item>
/// <item><b>Estimated Execution Cost</b> and <b>Agent Hours</b>: <see cref="ExecutionCost"/>'s one pricing
/// path over every run that overlaps the period, each run's part inside it priced on its own - the
/// same figure <c>GET /api/outcomes?from=&amp;to=</c> adds up to (every outcome plus Unattributed). It is
/// an estimate: Actual Spend is never inferred from it, and it is never in Actual Spend.</item>
/// </list>
///
/// <para>
/// Fixed subscriptions are the accounts whose <c>billing_type</c> is <c>subscription</c>; metered and
/// service accounts are variable. Each is reported by billing type and currency, actual and projected
/// apart.
/// </para>
/// </summary>
public static class SpendOverview
{
    public const string Fixed = "fixed";
    public const string Variable = "variable";

    /// <summary>How many months the trend shows at least, ending with the period's last month.</summary>
    public const int TrendMonths = 6;

    /// <summary>How many months the trend shows at most, for a long custom range.</summary>
    public const int TrendLimit = 24;

    public const string PeriodRefused =
        "The period needs `from` and `to` as ISO-8601 dates (YYYY-MM-DD, midnight UTC), with `to` after `from`; `to` is exclusive.";

    public sealed record CurrencyAmount(string Currency, decimal Amount, int Count);

    public sealed record TypeAmount(string BillingType, string Group, string Currency, decimal? Actual, decimal? Projected);

    public sealed record AccountLine(
        string AccountId, string Name, string Vendor, string BillingType, string Currency, bool Active,
        decimal? Actual, decimal? Projected, int Expenses);

    public sealed record VendorLine(string Vendor, string Currency, decimal? Actual, decimal? Projected, int Accounts);

    public sealed record Projection(string AccountId, string AccountName, string PeriodStart, string PeriodEnd, decimal Amount, string Currency);

    public sealed record Estimate(string Currency, decimal? Cost, double AgentSeconds, decimal? AgentRate, IReadOnlyList<string> LegacyRates);

    public sealed record Month(string MonthStart, IReadOnlyList<CurrencyAmount> Actual, Estimate Estimate);

    public sealed record Overview(
        string From, string To,
        IReadOnlyList<CurrencyAmount> Actual,
        IReadOnlyList<CurrencyAmount> Projected,
        IReadOnlyList<Projection> Projections,
        IReadOnlyList<TypeAmount> BillingTypes,
        Estimate Estimate,
        IReadOnlyList<AccountLine> Accounts,
        IReadOnlyList<VendorLine> Vendors,
        IReadOnlyList<Month> Months);

    /// <summary>The first instant of <paramref name="at"/>'s UTC month.</summary>
    public static DateTimeOffset MonthOf(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// The period a request names: <paramref name="from"/> and <paramref name="to"/> as dates (midnight
    /// UTC), <c>to</c> exclusive; neither sent is the current UTC month at <paramref name="now"/>. Anything
    /// else - one without the other, a bad date, <c>to</c> not after <c>from</c> - is null.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To)? PeriodOf(string? from, string? to, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to))
        {
            var month = MonthOf(now);
            return (month, month.AddMonths(1));
        }

        if (Day(from) is not { } start || Day(to) is not { } end || end <= start) return null;
        return (start, end);
    }

    private static DateTimeOffset? Day(string? text) =>
        DateTimeOffset.TryParseExact(
            text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
            ? day
            : null;

    /// <summary>Whether <paramref name="expense"/> is Actual Spend at all: current and confirmed.</summary>
    public static bool Counts(Expense expense) =>
        expense.Current && string.Equals(expense.Status, ExpenseRules.StatusConfirmed, StringComparison.Ordinal);

    /// <summary>THE PERIOD EDGE RULE: whether <paramref name="expense"/> is counted, whole, in
    /// <c>[from, to)</c> - its service period starts there.</summary>
    public static bool InPeriod(Expense expense, DateTimeOffset from, DateTimeOffset to)
    {
        var start = Instant.Parse(expense.ServicePeriodStart);
        return start >= from && start < to;
    }

    /// <summary>The Actual Spend of <paramref name="expenses"/> in <c>[from, to)</c>, per currency, by code.</summary>
    public static IReadOnlyList<CurrencyAmount> ActualOf(IEnumerable<Expense> expenses, DateTimeOffset from, DateTimeOffset to) =>
        expenses
            .Where(e => Counts(e) && InPeriod(e, from, to))
            .GroupBy(e => e.Currency, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CurrencyAmount(g.Key, g.Sum(e => Amount(e.Amount)), g.Count()))
            .ToList();

    /// <summary>The overview of <paramref name="accounts"/>, <paramref name="expenses"/> and the priced
    /// <paramref name="runs"/> (every run overlapping the trend's months) over <c>[from, to)</c>.</summary>
    public static Overview Of(
        IReadOnlyList<BillingAccount> accounts, IReadOnlyList<Expense> expenses,
        IReadOnlyList<ExecutionCost.Run> runs, string currency, DateTimeOffset from, DateTimeOffset to)
    {
        var counted = expenses.Where(e => Counts(e) && InPeriod(e, from, to)).ToList();
        var byId = accounts.ToDictionary(a => a.Id, StringComparer.Ordinal);

        var projections = accounts
            .SelectMany(a => RecurringCharges.Project(a, expenses, from, to)
                .Select(c => new Projection(a.Id, a.Name, c.PeriodStart, c.PeriodEnd, Amount(c.Amount), c.Currency)))
            .ToList();

        var projected = projections
            .GroupBy(p => p.Currency, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CurrencyAmount(g.Key, g.Sum(p => p.Amount), g.Count()))
            .ToList();

        decimal? ActualOn(string account) =>
            counted.Where(e => e.AccountId == account).Select(e => (decimal?)Amount(e.Amount)).Aggregate((decimal?)null, Plus);

        decimal? ProjectedOn(string account) =>
            projections.Where(p => p.AccountId == account).Select(p => (decimal?)p.Amount).Aggregate((decimal?)null, Plus);

        // An account is in the breakdown when it is active, or something of it is in the period.
        var lines = accounts
            .Where(a => a.Active || counted.Any(e => e.AccountId == a.Id) || projections.Any(p => p.AccountId == a.Id))
            .Select(a => new AccountLine(
                a.Id, a.Name, a.Vendor, a.BillingType, a.Currency, a.Active,
                ActualOn(a.Id),
                ProjectedOn(a.Id),
                counted.Count(e => e.AccountId == a.Id)))
            .ToList();

        var vendors = lines
            .GroupBy(l => (l.Vendor, l.Currency))
            .OrderBy(g => g.Key.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
            .Select(g => new VendorLine(
                g.Key.Vendor, g.Key.Currency,
                g.Select(l => l.Actual).Aggregate((decimal?)null, Plus),
                g.Select(l => l.Projected).Aggregate((decimal?)null, Plus),
                g.Count()))
            .ToList();

        var types = lines
            .GroupBy(l => (l.BillingType, l.Currency))
            .OrderBy(g => GroupOf(g.Key.BillingType) == Fixed ? 0 : 1)
            .ThenBy(g => g.Key.BillingType, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
            .Select(g => new TypeAmount(
                g.Key.BillingType, GroupOf(g.Key.BillingType), g.Key.Currency,
                g.Select(l => l.Actual).Aggregate((decimal?)null, Plus),
                g.Select(l => l.Projected).Aggregate((decimal?)null, Plus)))
            .ToList();

        var months = TrendOf(from, to)
            .Select(m => new Month(
                DateOnly.FromDateTime(m.UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ActualOf(expenses, m, m.AddMonths(1)),
                EstimateOf(runs, currency, m, m.AddMonths(1))))
            .ToList();

        return new Overview(
            Instant.Stamp(from), Instant.Stamp(to),
            ActualOf(expenses, from, to), projected, projections, types,
            EstimateOf(runs, currency, from, to), lines, vendors, months);
    }

    /// <summary>
    /// ESTIMATED EXECUTION COST AND AGENT HOURS over <c>[from, to)</c>: each run's span inside it, priced on
    /// its own by <see cref="ExecutionCost.Tally"/>, the one path every cost figure uses. Prices nothing new.
    /// </summary>
    public static Estimate EstimateOf(IEnumerable<ExecutionCost.Run> runs, string currency, DateTimeOffset from, DateTimeOffset to)
    {
        var tally = new ExecutionCost.Tally(currency);
        foreach (var run in runs) tally.AddSpan(run, run.SecondsIn(from, to));
        return new Estimate(currency, tally.Cost, tally.AgentSeconds, tally.AgentRate, tally.LegacyRates);
    }

    /// <summary>The trend's months: at least <see cref="TrendMonths"/>, ending with the month holding the
    /// period's last instant, back to the period's first month when it is longer, at most
    /// <see cref="TrendLimit"/>.</summary>
    public static IReadOnlyList<DateTimeOffset> TrendOf(DateTimeOffset from, DateTimeOffset to)
    {
        var last = MonthOf(to.AddTicks(-1));
        var first = MonthOf(from);
        var earliest = last.AddMonths(-(TrendMonths - 1));
        if (first > earliest) first = earliest;
        if (first < last.AddMonths(-(TrendLimit - 1))) first = last.AddMonths(-(TrendLimit - 1));

        var months = new List<DateTimeOffset>();
        for (var m = first; m <= last; m = m.AddMonths(1)) months.Add(m);
        return months;
    }

    /// <summary><c>fixed</c> for a subscription; <c>variable</c> for metered and service.</summary>
    public static string GroupOf(string billingType) =>
        string.Equals(billingType, "subscription", StringComparison.Ordinal) ? Fixed : Variable;

    private static decimal? Plus(decimal? sum, decimal? next) => next is null ? sum : (sum ?? 0) + next;

    private static decimal Amount(string text) => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
}
