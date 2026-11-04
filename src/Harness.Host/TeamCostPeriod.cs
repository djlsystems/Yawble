using System.ComponentModel;
using System.Globalization;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// THE TEAMS LIST'S PERIOD: every reachable team's Estimated Execution Cost and Agent Hours over one period
/// <c>[from, to)</c> the person picks, by day or by month, with the instance's total and its outcomes over
/// the same period - for the list's column and the first and third summary tiles. Read, never stored.
///
/// <para>
/// NOTHING HERE PRICES A RUN: every figure is <see cref="ExecutionCost"/>'s, each run's span inside the
/// period (or inside a bucket) priced on its own by <see cref="ExecutionCost.Tally"/> - the same path
/// <c>GET /api/spend/overview</c> takes for its Estimated Execution Cost, so a team's figure over a period
/// is that period's runs of that team, priced exactly as the Spend page prices them.
/// </para>
///
/// <para>
/// A FIGURE OVER THE PERIOD IS ONE TALLY OVER THE PERIOD, never the sum of its buckets: a run crossing a
/// bucket's edge is priced per bucket for the chart and once for the total, so the bars of a row can add
/// up to a cent away from its total, as the weekly reads already say of a run crossing a Monday.
/// </para>
///
/// <para>
/// A TEAM'S RUNS are the ones carrying its id above its floor (<see cref="TeamCost.RunsByTeam"/>), as
/// every team figure reads them. A run serves one outcome or none (Unattributed), as
/// <see cref="ExecutionCost"/> resolves it. Currencies are never converted: money is the instance
/// currency's, as every cost figure's is.
/// </para>
/// </summary>
public static class TeamCostPeriod
{
    public const string Day = "day";
    public const string Month = "month";

    /// <summary>The most buckets one read answers: a year and a day of days, five years of months.</summary>
    public const int MaxDays = 366;
    public const int MaxMonths = 60;

    public const string PeriodRefused =
        "The period needs `from` and `to` as ISO-8601 dates (YYYY-MM-DD, midnight UTC), with `to` after `from`; `to` is exclusive.";

    public const string BucketRefused = "`bucket` is `day` or `month`.";

    public static readonly string TooLongRefused =
        $"A period is at most {MaxDays} days by day, or {MaxMonths} months by month.";

    /// <summary>A period's figure: agent seconds, the money (null - never 0 - when nothing was priced), the
    /// priced agent time's weighted rate, and the footnote naming any rate from before snapshots.</summary>
    public sealed record Amount(double AgentSeconds, decimal? Cost, decimal? AgentRate, IReadOnlyList<string> LegacyRates);

    /// <summary>One bucket of one series: its first day, its agent seconds and its money.</summary>
    public sealed record Bucket(string Start, double AgentSeconds, decimal? Cost);

    /// <summary>One outcome a team's runs served in the period, or Unattributed (<c>id</c> null).</summary>
    public sealed record TeamOutcome(string? Id, string Name, int? Rank, Amount Totals, IReadOnlyList<Bucket> Buckets);

    public sealed record TeamEntry(string Team, Amount Totals, IReadOnlyList<Bucket> Buckets, IReadOnlyList<TeamOutcome> Outcomes);

    /// <summary>One outcome across the reached teams over the period: the summary tile's shape
    /// (<see cref="TeamCost.SummaryOutcome"/>), its budget and all-time total as that read gives them.</summary>
    public sealed record SummaryOutcome(
        string? Id, string Name, int? Rank, double AgentSeconds, decimal? Cost, decimal? Budget, decimal? OutcomeTotalCost,
        IReadOnlyList<string> LegacyRates);

    public sealed record PeriodShape(string From, string To, string Bucket);

    public sealed record Answer(
        TeamCost.MoneyShape Money, PeriodShape Period, IReadOnlyList<string> Buckets, IReadOnlyList<TeamEntry> Teams,
        Amount Total, IReadOnlyList<Bucket> TotalBuckets, Amount Prior, IReadOnlyList<SummaryOutcome> Outcomes);

    /// <summary>
    /// The default period: the previous UTC month and the current one, <c>[first of last month, first of next)</c>.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) DefaultPeriod(DateTimeOffset now)
    {
        var month = SpendOverview.MonthOf(now);
        return (month.AddMonths(-1), month.AddMonths(1));
    }

    /// <summary>The period a request names, the default when neither date is sent; null when it names none.</summary>
    public static (DateTimeOffset From, DateTimeOffset To)? PeriodOf(string? from, string? to, DateTimeOffset now) =>
        string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to)
            ? DefaultPeriod(now)
            : SpendOverview.PeriodOf(from, to, now);

    /// <summary>
    /// The buckets of <c>[from, to)</c>: each UTC day, or each calendar month cut to the period (a period
    /// starting on the 15th has a first bucket from the 15th to the month's end). Null when the period
    /// has more than the most a read answers.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)>? BucketsOf(DateTimeOffset from, DateTimeOffset to, string bucket)
    {
        var buckets = new List<(DateTimeOffset, DateTimeOffset)>();
        var limit = bucket == Day ? MaxDays : MaxMonths;

        for (var start = from; start < to;)
        {
            var next = bucket == Day ? start.AddDays(1) : SpendOverview.MonthOf(start).AddMonths(1);
            if (next > to) next = to;
            buckets.Add((start, next));
            if (buckets.Count > limit) return null;
            start = next;
        }

        return buckets;
    }

    /// <summary>
    /// THE ANSWER over <c>[from, to)</c> for the teams in <paramref name="floors"/>, from
    /// <paramref name="runs"/> (every run overlapping the period and the same length before it),
    /// <paramref name="outcomes"/> as they are now and each outcome's all-time estimate.
    /// </summary>
    public static Answer Of(
        IReadOnlyList<ExecutionCost.Run> runs, IReadOnlyList<Outcome> outcomes, IReadOnlyDictionary<string, decimal> outcomeTotalCost,
        TeamCost.MoneyShape money, IReadOnlyList<string> teamOrder, IReadOnlyDictionary<string, long> floors,
        DateTimeOffset from, DateTimeOffset to, string bucket)
    {
        var currency = money.Currency;
        var buckets = BucketsOf(from, to, bucket) ?? [];
        var byId = outcomes.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var byTeam = TeamCost.RunsByTeam(runs, floors, from, to);

        // THE PERIOD'S ORDER, as the eight-week reads keep theirs: every outcome any reached team's run served in
        // the period, by when it was created, ties by id - one rank, and so one colour, on every row.
        var ranks = byTeam.Values.SelectMany(r => r)
            .Select(r => r.OutcomeId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Select(id => byId.GetValueOrDefault(id))
            .OfType<Outcome>()
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.Id, StringComparer.Ordinal)
            .Select((o, index) => (o.Id, Rank: index + 1))
            .ToDictionary(e => e.Id, e => e.Rank, StringComparer.Ordinal);

        var teams = teamOrder.Select(team =>
        {
            var mine = byTeam.GetValueOrDefault(team) ?? [];
            var entries = mine
                .GroupBy(r => r.OutcomeId)
                .Select(g => (Outcome: g.Key is null ? null : byId.GetValueOrDefault(g.Key), Runs: g.ToList()))
                .OrderBy(e => e.Outcome is null ? 0 : 1)
                .ThenBy(e => e.Outcome?.CreatedAt)
                .ThenBy(e => e.Outcome?.Id, StringComparer.Ordinal)
                .Select(e => new TeamOutcome(
                    e.Outcome?.Id,
                    e.Outcome?.Name ?? TeamCost.UnattributedName,
                    e.Outcome is null ? null : ranks.GetValueOrDefault(e.Outcome.Id),
                    AmountOf(Tally(e.Runs, currency, from, to)),
                    BucketsFor(e.Runs, buckets, currency)))
                .ToList();

            return new TeamEntry(team, AmountOf(Tally(mine, currency, from, to)), BucketsFor(mine, buckets, currency), entries);
        }).ToList();

        var reached = byTeam.Values.SelectMany(r => r).ToList();
        var length = to - from;
        var prior = TeamCost.RunsByTeam(runs, floors, from - length, from).Values.SelectMany(r => r);

        var served = reached
            .GroupBy(r => r.OutcomeId ?? "")
            .ToDictionary(g => g.Key, g => Tally(g, currency, from, to), StringComparer.Ordinal);

        var summary = served.Keys.Where(k => k.Length > 0)
            .Select(id => byId[id])
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.Id, StringComparer.Ordinal)
            .Select(o =>
            {
                var tally = served[o.Id];
                return new SummaryOutcome(
                    o.Id, o.Name, ranks.GetValueOrDefault(o.Id), tally.AgentSeconds, tally.Cost,
                    TeamCost.Reading.BudgetOf(o), outcomeTotalCost.TryGetValue(o.Id, out var total) ? total : null, tally.LegacyRates);
            })
            .ToList();

        if (served.TryGetValue("", out var unlinked))
        {
            summary.Insert(0, new SummaryOutcome(
                null, TeamCost.UnattributedName, null, unlinked.AgentSeconds, unlinked.Cost, null, null, unlinked.LegacyRates));
        }

        return new Answer(
            money,
            new PeriodShape(DayOf(from), DayOf(to), bucket),
            buckets.Select(b => DayOf(b.From)).ToList(),
            teams,
            AmountOf(Tally(reached, currency, from, to)),
            BucketsFor(reached, buckets, currency),
            AmountOf(Tally(prior, currency, from - length, from)),
            summary);
    }

    /// <summary>
    /// <paramref name="runs"/> over <c>[from, to)</c>: each run's span inside it, priced on its own - the
    /// tally <see cref="SpendOverview.EstimateOf"/> makes for the same period.
    /// </summary>
    public static ExecutionCost.Tally Tally(IEnumerable<ExecutionCost.Run> runs, string currency, DateTimeOffset from, DateTimeOffset to)
    {
        var tally = new ExecutionCost.Tally(currency);
        foreach (var run in runs) tally.AddSpan(run, run.SecondsIn(from, to));
        return tally;
    }

    private static IReadOnlyList<Bucket> BucketsFor(
        IReadOnlyList<ExecutionCost.Run> runs, IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> buckets, string currency) =>
        buckets.Select(b =>
            {
                var tally = Tally(runs, currency, b.From, b.To);
                return new Bucket(DayOf(b.From), tally.AgentSeconds, tally.Cost);
            })
            .ToList();

    private static Amount AmountOf(ExecutionCost.Tally tally) => new(tally.AgentSeconds, tally.Cost, tally.AgentRate, tally.LegacyRates);

    private static string DayOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static void Map(WebApplication app)
    {
        // EVERY REACHABLE TEAM AT ONCE, as `GET /api/teams/cost` reads them: one read for the whole list, never
        // one per row. It declares no `{team}`, so it resolves access itself, with the same permit.
        app.MapGet("/api/teams/cost/period", async (
                [Description("The period's first day, YYYY-MM-DD (midnight UTC). Sent with `to`; neither sent is the previous and the current UTC month.")] string? from,
                [Description("The day after the period's last, YYYY-MM-DD (midnight UTC, exclusive).")] string? to,
                [Description("`day` (the default) or `month`.")] string? bucket,
                HttpContext context, TeamRegistry teams, TeamAccess access, IOutcomeStore outcomes,
                TenantSettings settings, CancellationToken ct) =>
            {
                if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

                var by = string.IsNullOrWhiteSpace(bucket) ? Day : bucket.Trim();
                if (by is not (Day or Month)) return Refused(BucketRefused);
                if (PeriodOf(from, to, DateTimeOffset.UtcNow) is not { } period) return Refused(PeriodRefused);
                if (BucketsOf(period.From, period.To, by) is null) return Refused(TooLongRefused);

                var effective = await access.EffectiveTeamsAsync(principal, ct);
                var reached = teams.All().Where(t => effective.Contains(t.Id)).Select(t => t.Id).ToList();
                var floors = reached.ToDictionary(t => t, teams.FloorFor, StringComparer.OrdinalIgnoreCase);

                var all = await outcomes.ListAsync(ct);
                var pricing = OutcomeFigures.Pricing.Of(settings);
                var money = new TeamCost.MoneyShape(pricing.Currency, pricing.AgentHourlyRate > 0 ? pricing.AgentHourlyRate : null);
                var length = period.To - period.From;

                var runs = ExecutionCost.Of(
                    await outcomes.ReadExecutionRunsAsync(period.From - length, period.To, pricedOnly: false, ct), all, pricing.Currency);
                var priced = ExecutionCost.Of(await outcomes.ReadExecutionRunsAsync(null, null, pricedOnly: true, ct), all, pricing.Currency);
                var totals = ExecutionCost.CostsOf(priced, null, null, pricing.Currency);

                return Results.Ok(Of(runs, all, totals.ByOutcome, money, reached, floors, period.From, period.To, by));
            })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("Every reachable team's Estimated Execution Cost and Agent Hours over a period, by day or month")
            .WithDescription(
                "The teams list's period: `?from=YYYY-MM-DD&to=YYYY-MM-DD` (midnight UTC, `to` exclusive; neither sent is the "
                + "previous and the current UTC month) and `?bucket=day|month` (`day` when not sent). A period of more than "
                + $"{MaxDays} days by day or {MaxMonths} months by month, a bad date, `to` not after `from`, or another bucket "
                + "is refused (400) with one sentence.\n\n"
                + "`money`: `currency` and `agentHourlyRate`, the tenant default as set now (null when none) - no figure is "
                + "that rate times hours. `period`: `{ from, to, bucket }`. `buckets`: each bucket's first day, oldest first - "
                + "a UTC day, or a calendar month cut to the period.\n\n"
                + "`teams`: one entry per team the caller reaches, archived ones included, in the registry's order: "
                + "`{ team, totals, buckets, outcomes }`. `totals` is `{ agentSeconds, cost, agentRate, legacyRates }` over "
                + "the whole period: Agent Hours are agent-kind time only, `cost` every priced run's part inside the period "
                + "(agent and plugin), null - never 0 - when none was priced, and `legacyRates` names `reconstructed` or "
                + "`fallback-at-upgrade` when a run from before snapshots was priced into it. `buckets` is "
                + "`{ start, agentSeconds, cost }` per bucket. `outcomes`: Unattributed (`id` null) first when the team has "
                + "unlinked work, then each outcome its runs served, oldest first, each `{ id, name, rank, totals, buckets }`; "
                + "`rank` is the outcome's place among every outcome any reached team served in the period. A team with no "
                + "runs in the period has zero seconds, a null `cost` and no outcomes.\n\n"
                + "`total`: the reached teams' figure over the period; `totalBuckets`: theirs per bucket; `prior`: the same over the same length of time just "
                + "before it. `outcomes`: `{ id, name, rank, agentSeconds, cost, budget, outcomeTotalCost, legacyRates }` for "
                + "Unattributed and each outcome served in the period, as `GET /api/teams/cost/summary` gives them; `budget` "
                + "and `outcomeTotalCost` (all teams, all time) are not the period's.\n\n"
                + "Every figure is one tally over its span, each run's part inside it priced from its own snapshot, the way "
                + "`GET /api/spend/overview` prices its Estimated Execution Cost; a total is never the sum of its buckets, so "
                + "bars can add up to a cent away from it. Money is in the instance currency only and never converted. "
                + "It is an estimate, never money paid.");
    }

    private static IResult Refused(string sentence) => Results.Json(new { error = sentence }, statusCode: 400);
}
