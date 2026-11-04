using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;

namespace Harness.Tests.Host;

/// <summary>
/// <see cref="ExecutionCost"/>: every estimate the cost figures answer. A run is priced from the snapshot
/// taken at its start and from nothing else; a plugin without its own rate is not priced and is never
/// Agent Hours; a run that crosses a week is split by overlap and each part is rounded on its own; a
/// snapshotted run serves the outcome its link named at start; nothing priced is no money, never 0.
/// Both providers hand the same rows to this one function, so these pin both.
/// </summary>
public sealed class ExecutionCostTests
{
    private const string Usd = "USD";

    // A Monday, so the weeks are easy to read: weeks start 2026-09-07 ... 2026-10-26.
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Monday = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<DateTimeOffset> Weeks => OutcomeFigures.WeekStarts(Now);

    [Fact]
    public void A_run_is_priced_from_its_snapshot_rate_rounded_to_the_cent_as_a_whole_run_always_was()
    {
        // 45 s at 10/h is 0.125: to even, 0.12. 135 s at 10/h is 0.375: 0.38. An hour at 62.5 is 62.5.
        Assert.Equal(0.12m, Run(Monday, Monday.AddSeconds(45), rate: "10").Estimate);
        Assert.Equal(0.38m, Run(Monday, Monday.AddSeconds(135), rate: "10").Estimate);
        Assert.Equal(62.5m, Run(Monday, Monday.AddHours(1), rate: "62.5").Estimate);
        Assert.Equal(0.12m, ExecutionCost.Price(45, 10m));

        var priced = Run(Monday, Monday.AddHours(1), rate: "62.5");
        Assert.Equal(ExecutionSnapshotFacts.RateSourceMember, priced.RateSource);
        Assert.Equal(Usd, priced.Currency);
    }

    [Fact]
    public void A_snapshot_with_no_rate_is_unavailable_never_zero_and_never_filled_from_the_rate_history()
    {
        // A history that would price an agent run at 90 at that moment, and an epoch at 50.
        var history = Timeline(epoch: "50", (Monday.AddDays(-30), "90"));

        var none = Of(Row(Monday, Monday.AddHours(1)), Snapshot(rate: null), history);
        // A stored zero is never written; were one read, it is still no money.
        var zero = Of(Row(Monday, Monday.AddHours(1)), Snapshot(rate: "0"), history);
        // From before snapshots with no history at all: nothing to price it from.
        var legacy = Of(Row(Monday, Monday.AddHours(1)), snapshot: null);

        foreach (var run in new[] { none, zero, legacy })
        {
            Assert.Null(run.Rate);
            Assert.Null(run.Estimate);
            Assert.False(run.IsRebuilt);
            Assert.Equal(3600, run.Seconds, 3);
        }

        Assert.Equal(ExecutionSnapshotFacts.RateSourceNone, none.RateSource);
        Assert.Equal(ExecutionSnapshotFacts.RateSourceNone, legacy.RateSource);

        var total = ExecutionCost.Total(ExecutionCost.Weeks([none, zero, legacy], Weeks, Usd), Usd);
        Assert.Null(total.Cost);
        Assert.Null(total.AgentRate);
        Assert.Empty(total.LegacyRates);
        Assert.Equal(3 * 3600, total.AgentSeconds, 3);
    }

    [Fact]
    public void A_legacy_agent_run_is_priced_at_the_tenant_rate_in_effect_at_its_start_and_a_later_change_does_not_move_it()
    {
        // 40 from a month back, 60 from two hours into Monday, 90 set today, after both runs.
        var history = Timeline(epoch: "25", (Monday.AddDays(-30), "40"), (Monday.AddHours(2), "60"), (Now, "90"));

        var early = Of(Row(Monday, Monday.AddHours(1)), snapshot: null, history, currency: "usd");
        var atChange = Of(Row(Monday.AddHours(2), Monday.AddHours(3)), snapshot: null, history);

        Assert.Equal(40m, early.Rate);
        Assert.Equal(40m, early.Estimate);
        Assert.Equal(RateEpochFacts.RateSourceReconstructed, early.RateSource);
        // The instance's currency now, upper-case, as a snapshot's.
        Assert.Equal(Usd, early.Currency);
        // A change AT the start is in effect at it.
        Assert.Equal(60m, atChange.Estimate);
        Assert.True(early.IsRebuilt);
    }

    [Fact]
    public void A_zero_an_empty_value_or_a_reset_in_effect_at_start_is_unavailable_and_never_falls_through_to_the_epoch()
    {
        foreach (var value in new[] { "0", "", null })
        {
            var history = Timeline(epoch: "50", (Monday.AddDays(-1), value));
            var run = Of(Row(Monday, Monday.AddHours(1)), snapshot: null, history);

            Assert.Null(run.Rate);
            Assert.Null(run.Estimate);
            Assert.Equal(ExecutionSnapshotFacts.RateSourceNone, run.RateSource);
        }
    }

    [Fact]
    public void A_legacy_agent_run_before_every_recorded_change_takes_the_epoch_rate_and_none_without_one()
    {
        var withEpoch = Timeline(epoch: "50", (Monday.AddDays(1), "70"));
        var noEpochRate = Timeline(epoch: null, (Monday.AddDays(1), "70"));
        var noEpoch = new RateTimeline(null, [new RateChange(Monday.AddDays(1), 1, "70")]);

        var fallback = Of(Row(Monday, Monday.AddHours(1)), snapshot: null, withEpoch);
        Assert.Equal(50m, fallback.Estimate);
        Assert.Equal(RateEpochFacts.RateSourceFallback, fallback.RateSource);
        Assert.True(fallback.IsRebuilt);

        // A later row is never read for what came before it: no epoch rate is no money, never 70.
        foreach (var history in new[] { noEpochRate, noEpoch })
        {
            var run = Of(Row(Monday, Monday.AddHours(1)), snapshot: null, history);
            Assert.Null(run.Estimate);
            Assert.Equal(ExecutionSnapshotFacts.RateSourceNone, run.RateSource);
        }
    }

    [Fact]
    public void A_legacy_plugin_run_is_never_priced_and_never_Agent_Hours_and_a_legacy_run_with_no_start_adds_nothing()
    {
        var history = Timeline(epoch: "50", (Monday.AddDays(-1), "60"));

        var plugin = Of(Row(Monday, Monday.AddHours(2), kind: MemberRef.PluginKind), snapshot: null, history);
        var unstarted = Of(Row(null, Monday.AddHours(1)), snapshot: null, history);
        var agent = Of(Row(Monday, Monday.AddHours(1)), snapshot: null, history);

        Assert.Null(plugin.Rate);
        Assert.Equal(ExecutionSnapshotFacts.RateSourceNone, plugin.RateSource);
        Assert.Null(unstarted.Rate);

        var total = ExecutionCost.Total(ExecutionCost.Weeks([plugin, unstarted, agent], Weeks, Usd), Usd);
        Assert.Equal(3600, total.AgentSeconds, 3);
        Assert.Equal(60m, total.Cost);
        Assert.Equal(60m, total.AgentRate);
    }

    [Fact]
    public void A_rebuilt_run_crossing_a_week_is_split_at_its_one_rate_and_the_figure_names_how_it_was_priced()
    {
        // Sunday 23:59:15 to Monday 00:00:30 at a reconstructed 10/h; an hour at the epoch's 30 on Monday.
        var history = Timeline(epoch: "30", (Monday.AddDays(-1), "10"));
        var crossing = Of(Row(Monday.AddSeconds(-45), Monday.AddSeconds(30)), snapshot: null, history);
        var old = Of(Row(Monday.AddDays(-2), Monday.AddDays(-2).AddHours(1)), snapshot: null, history);
        var snapshotted = Run(Monday, Monday.AddHours(1), rate: "100");

        var weeks = ExecutionCost.Weeks([crossing], Weeks, Usd);
        Assert.Equal(0.12m, weeks[^2].Tally.Cost);
        Assert.Equal(0.08m, weeks[^1].Tally.Cost);
        Assert.Equal(crossing.Seconds, weeks.Sum(w => w.Tally.AgentSeconds), 3);
        Assert.Equal([RateEpochFacts.RateSourceReconstructed], weeks[^1].Tally.LegacyRates);

        var total = ExecutionCost.Total(ExecutionCost.Weeks([crossing, old, snapshotted], Weeks, Usd), Usd);
        Assert.Equal([RateEpochFacts.RateSourceReconstructed, RateEpochFacts.RateSourceFallback], total.LegacyRates);
        // The weighted rate takes the rebuilt agent hours in: (0.20 + 30 + 100) over 75 s and 2 h.
        Assert.Equal(130.20m, total.Cost);
        Assert.Equal(decimal.Round(130.20m / ((decimal)(75 + 7200) / 3600m), 2), total.AgentRate);

        // A figure of snapshots alone has no footnote.
        Assert.Empty(ExecutionCost.Total(ExecutionCost.Weeks([snapshotted], Weeks, Usd), Usd).LegacyRates);

        var costs = ExecutionCost.CostsOf([crossing, old, snapshotted], null, null, Usd);
        Assert.Equal([RateEpochFacts.RateSourceReconstructed, RateEpochFacts.RateSourceFallback], costs.LegacyRatesOf(null));
    }

    [Fact]
    public void A_plugin_without_its_own_rate_adds_no_money_and_no_Agent_Hours_and_one_with_its_own_adds_money_only()
    {
        var agent = Run(Monday, Monday.AddHours(2), rate: "60");
        var unpricedAgent = Run(Monday, Monday.AddHours(1), rate: null);
        var plugin = Run(Monday, Monday.AddHours(3), rate: null, kind: MemberRef.PluginKind);
        var legacyPlugin = Of(Row(Monday, Monday.AddHours(4), kind: MemberRef.PluginKind), snapshot: null);
        var pricedPlugin = Run(Monday, Monday.AddHours(1), rate: "15", kind: MemberRef.PluginKind);

        Assert.False(plugin.IsAgent);
        Assert.False(legacyPlugin.IsAgent);

        var total = ExecutionCost.Total(ExecutionCost.Weeks([agent, unpricedAgent, plugin, legacyPlugin, pricedPlugin], Weeks, Usd), Usd);

        // Agent Hours: the agents' three hours, priced or not; no plugin hour.
        Assert.Equal(3 * 3600, total.AgentSeconds, 3);
        // The headline: 120 for the agent and 15 for the priced plugin.
        Assert.Equal(135m, total.Cost);
        // The rate: the priced agent time's own, 120 over 2 h - not 135 over 3 h, and no plugin in it.
        Assert.Equal(60m, total.AgentRate);
    }

    [Fact]
    public void The_weighted_agent_rate_is_the_priced_agent_moneys_over_their_hours_never_an_average_of_rates()
    {
        var tenant = Run(Monday, Monday.AddHours(3), rate: "60", source: ExecutionSnapshotFacts.RateSourceTenant);
        var member = Run(Monday, Monday.AddHours(1), rate: "100");

        var total = ExecutionCost.Total(ExecutionCost.Weeks([tenant, member], Weeks, Usd), Usd);

        Assert.Equal(280m, total.Cost);
        Assert.Equal(70m, total.AgentRate);
    }

    [Fact]
    public void A_run_crossing_a_week_is_split_by_overlap_and_each_part_is_priced_and_rounded_on_its_own()
    {
        // Sunday 23:59:15 to Monday 00:00:30 at 10/h: 45 s before the boundary, 30 s after.
        var run = Run(Monday.AddSeconds(-45), Monday.AddSeconds(30), rate: "10");

        var weeks = ExecutionCost.Weeks([run], Weeks, Usd);
        var before = weeks[^2].Tally;
        var after = weeks[^1].Tally;

        Assert.Equal(45, before.AgentSeconds, 3);
        Assert.Equal(30, after.AgentSeconds, 3);
        Assert.Equal(run.Seconds, weeks.Sum(w => w.Tally.AgentSeconds), 3);

        // 0.125 -> 0.12 and 0.0833 -> 0.08: 0.20, where the whole run is 0.2083 -> 0.21.
        Assert.Equal(0.12m, before.Cost);
        Assert.Equal(0.08m, after.Cost);
        Assert.Equal(0.21m, run.Estimate);
        Assert.Equal(0.20m, ExecutionCost.Total(weeks, Usd).Cost);

        // Its tokens are reported when it ends: the week it ended, alone.
        Assert.Equal(0, before.MeasuredRuns);
        Assert.Equal(1, after.MeasuredRuns);

        // Every other week has nothing - no money, never 0.
        Assert.All(weeks.Take(weeks.Count - 2), w => Assert.Null(w.Tally.Cost));
    }

    [Fact]
    public void A_run_starting_before_the_eight_weeks_counts_only_its_part_inside_them()
    {
        var run = Run(Weeks[0].AddHours(-1), Weeks[0].AddHours(1), rate: "60");

        var total = ExecutionCost.Total(ExecutionCost.Weeks([run], Weeks, Usd), Usd);

        Assert.Equal(3600, total.AgentSeconds, 3);
        Assert.Equal(60m, total.Cost);
        Assert.True(run.Overlaps(Weeks[0], Weeks[^1].AddDays(7)));
    }

    [Fact]
    public void A_run_with_no_start_cannot_be_split_and_adds_no_seconds_and_no_money_but_its_tokens_count()
    {
        var run = Of(Row(null, Monday.AddHours(1)), Snapshot(rate: "60"));

        var weeks = ExecutionCost.Weeks([run], Weeks, Usd);
        var total = ExecutionCost.Total(weeks, Usd);

        Assert.Equal(0, total.AgentSeconds);
        Assert.Null(total.Cost);
        Assert.Equal(1, total.MeasuredRuns);
        Assert.Equal(10, total.Billable);
    }

    /// <summary>
    /// A ZERO-LENGTH RUN is a run: it falls in the period it is in and its tokens count, but it adds no seconds and no
    /// money - a figure with nothing else priced has no amount, as with no priced run at all, never a made-up figure - and
    /// a run whose end is before its start (a clock step) adds no time, never negative time. Neither divides by zero:
    /// with no priced hour there is no weighted rate, and beside a priced hour the rate is that hour's alone.
    /// </summary>
    [Fact]
    public void A_zero_length_run_adds_no_seconds_and_no_money_one_ending_before_it_starts_adds_no_negative_time_and_neither_divides_by_zero()
    {
        var zero = Run(Monday.AddHours(5), Monday.AddHours(5), rate: "60");
        var backwards = Run(Monday.AddHours(6), Monday.AddHours(5), rate: "60");

        Assert.True(zero.Overlaps(Monday, Monday.AddDays(1)));
        Assert.Equal(0, zero.SecondsIn(null, null));
        Assert.Equal(0, backwards.SecondsIn(null, null));
        Assert.Equal((0d, 0m), (backwards.Seconds, backwards.Estimate));

        var alone = ExecutionCost.Total(ExecutionCost.Weeks([zero, backwards], Weeks, Usd), Usd);
        Assert.Equal(0, alone.AgentSeconds);
        Assert.Null(alone.Cost);
        Assert.Null(alone.AgentRate);
        Assert.Equal(2, alone.MeasuredRuns);

        var beside = ExecutionCost.Total(ExecutionCost.Weeks([zero, backwards, Run(Monday, Monday.AddHours(1), rate: "90")], Weeks, Usd), Usd);
        Assert.Equal(3600, beside.AgentSeconds, 3);
        Assert.Equal(90m, beside.Cost);
        Assert.Equal(90m, beside.AgentRate);

        var costs = ExecutionCost.CostsOf([zero, backwards], Monday, Monday.AddDays(1), Usd);
        Assert.Null(costs.Of(null));
        Assert.Null(costs.AgentRateOf(null));
    }

    [Fact]
    public void A_snapshotted_run_serves_the_outcome_its_link_named_at_start_and_a_run_without_one_keeps_the_newest_link()
    {
        var older = Outcome("older");
        var merged = Outcome("merged") with { Status = OutcomeStatus.Merged, MergedInto = "older" };
        var newer = Outcome("newer");
        var outcomes = new[] { older, merged, newer };

        // The workflow's newest link now names "newer".
        var rows = new ExecutionRunRows(
            [new OutcomeLink(9, 1, "newer", "alpha", "Alpha", "newer", "p", "person", Monday, "person")],
            [
                new ExecutionRunRow(Row(Monday, Monday.AddHours(1)), 100, Snapshot(rate: "60", linkId: 3, linkOutcome: "merged")),
                new ExecutionRunRow(Row(Monday, Monday.AddHours(1)), 101, Snapshot(rate: "60", linkId: 4, linkOutcome: null)),
                new ExecutionRunRow(Row(Monday, Monday.AddHours(1)), 102, Snapshot(rate: "60", linkId: null)),
                new ExecutionRunRow(Row(Monday, Monday.AddHours(1)), 103, Snapshot(rate: "60", linkId: 5, linkOutcome: "deleted")),
                new ExecutionRunRow(Row(Monday, Monday.AddHours(1)), null, null),
            ]);

        var runs = ExecutionCost.Of(rows, outcomes, Usd);

        // Linked at start to an outcome merged since: where its figures live now, not the newer link.
        Assert.Equal("older", runs[0].OutcomeId);
        // An unlink, no link at start, an outcome that is gone: no outcome, whatever was linked later.
        Assert.Null(runs[1].OutcomeId);
        Assert.Null(runs[2].OutcomeId);
        Assert.Null(runs[3].OutcomeId);
        // From before snapshots: the newest link, as before.
        Assert.Equal("newer", runs[4].OutcomeId);

        // EACH RUN ONCE: the money by outcome sums to every priced run.
        var costs = ExecutionCost.CostsOf(runs, null, null, Usd);
        Assert.Equal(60m, costs.Of("older"));
        Assert.Equal(180m, costs.Of(null));
        Assert.Null(costs.Of("newer"));
    }

    [Fact]
    public void Money_in_another_currency_is_left_out_never_added_to_the_figures_currency()
    {
        var usd = Run(Monday, Monday.AddHours(1), rate: "60");
        var eur = Of(Row(Monday, Monday.AddHours(1)), Snapshot(rate: "50", currency: "EUR"));

        var total = ExecutionCost.Total(ExecutionCost.Weeks([usd, eur], Weeks, Usd), Usd);

        Assert.Equal(60m, total.Cost);
        Assert.Equal(60m, total.AgentRate);
        Assert.Equal(7200, total.AgentSeconds, 3);
        Assert.Equal(60m, ExecutionCost.CostsOf([usd, eur], null, null, Usd).Of(null));
        Assert.Null(ExecutionCost.CostsOf([eur], null, null, Usd).Of(null));
    }

    [Fact]
    public void A_summary_with_no_priced_run_answers_hours_and_no_money_never_zero_even_with_a_tenant_rate_set()
    {
        var run = Of(Row(Monday, Monday.AddHours(2)), snapshot: null);
        var reading = Reading([run], tenantRate: 60);

        var summary = reading.SummaryOf(new Dictionary<string, long> { ["alpha"] = 0 }, new TeamCost.RightNow(0, 0, 0));

        Assert.Equal(60, summary.Rate);
        Assert.Equal(7200, summary.Total.AgentSeconds, 3);
        Assert.Null(summary.Total.Cost);
        Assert.Null(summary.Total.AgentRate);
        Assert.Null(summary.Prior.Cost);
        Assert.All(summary.Weeks, w => Assert.Null(w.Cost));
        Assert.All(summary.Outcomes, o => Assert.Null(o.Cost));
    }

    [Fact]
    public void A_member_rate_prices_its_runs_when_the_tenant_rate_is_unset()
    {
        var run = Run(Monday, Monday.AddHours(1), rate: "45");
        var reading = Reading([run], tenantRate: 0);

        var entry = reading.Team("alpha", reading.RunsByTeam(new Dictionary<string, long> { ["alpha"] = 0 })["alpha"]);

        Assert.Null(reading.Money.AgentHourlyRate);
        Assert.Equal(45m, entry.Totals.Cost);
        Assert.Equal(45m, entry.Totals.AgentRate);
    }

    [Fact]
    public void A_run_at_or_below_the_teams_floor_is_not_the_teams()
    {
        var below = Of(Row(Monday, Monday.AddHours(1), seq: 5), Snapshot(rate: "60"));
        var above = Of(Row(Monday, Monday.AddHours(1), seq: 50), Snapshot(rate: "60"));
        var reading = Reading([below, above], tenantRate: 0);

        var runs = reading.RunsByTeam(new Dictionary<string, long> { ["alpha"] = 10 })["alpha"];

        Assert.Equal([50L], runs.Select(r => r.Ledger.RunSeq));
    }

    // ---- fixtures ----

    private static TeamCost.Reading Reading(IReadOnlyList<ExecutionCost.Run> runs, int tenantRate) => new()
    {
        Pricing = new OutcomeFigures.Pricing(Usd, tenantRate, OutcomeFigures.Pricing.DeclaredAchieved),
        Now = Now,
        Weeks = Weeks,
        Runs = runs,
        OutcomesById = new Dictionary<string, Outcome>(),
        OutcomeTotalCost = new Dictionary<string, decimal>(),
        Ranks = new Dictionary<string, int>(),
    };

    private static ExecutionCost.Run Run(
        DateTimeOffset started, DateTimeOffset ended, string? rate, string kind = MemberRef.AgentKind,
        string source = ExecutionSnapshotFacts.RateSourceMember) =>
        Of(Row(started, ended, kind), Snapshot(rate, kind, source: rate is null ? ExecutionSnapshotFacts.RateSourceNone : source));

    private static ExecutionCost.Run Of(
        UsageLedgerRow row, ExecutionRunSnapshot? snapshot, RateTimeline? timeline = null, string currency = Usd) =>
        ExecutionCost.Of(new ExecutionRunRow(row, snapshot is null ? null : row.RunSeq, snapshot),
            new Dictionary<string, string>(), new Dictionary<long, string>(), timeline ?? RateTimeline.Empty, currency);

    /// <summary>A rate history: the epoch's rate (null: none) and each change's time and new value (null: a reset).</summary>
    private static RateTimeline Timeline(string? epoch, params (DateTimeOffset At, string? Value)[] changes) =>
        new(
            new RateEpoch(Monday.AddDays(-365), epoch, epoch is null ? null : Usd, epoch is null ? ExecutionSnapshotFacts.RateSourceNone : RateEpochFacts.SourceTenant),
            [.. changes.Select((c, i) => new RateChange(c.At, i + 1, c.Value))]);

    private static ExecutionRunSnapshot Snapshot(
        string? rate, string kind = MemberRef.AgentKind, string currency = Usd, string? source = null,
        long? linkId = null, string? linkOutcome = null) =>
        new(kind, rate, rate is null ? null : currency,
            source ?? (rate is null ? ExecutionSnapshotFacts.RateSourceNone : ExecutionSnapshotFacts.RateSourceMember),
            linkId, linkId is not null, linkOutcome);

    private static long _seq = 1000;

    private static UsageLedgerRow Row(DateTimeOffset? started, DateTimeOffset ended, string? kind = MemberRef.AgentKind, long? seq = null) =>
        new(seq ?? Interlocked.Increment(ref _seq), 1, "alpha", "Alpha", "Dev", kind, "claude", "completed",
            null, started, ended, true, 10, null, null, 0, null, 10, null, null, false);

    private static Outcome Outcome(string id) =>
        new(id, id, "", OutcomeStatus.Active, null, "local", null, null, null, "p", "person", Monday, Monday, "p", Monday, null);
}
