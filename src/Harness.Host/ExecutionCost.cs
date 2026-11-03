using System.Globalization;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHAT A RUN'S EXECUTION IS ESTIMATED TO HAVE COST, priced once, for every figure: the team cost
/// routes (<see cref="TeamCost"/>) and the outcome figures (<see cref="OutcomeFigures"/>) read it, and
/// neither prices a run another way.
///
/// <para>
/// A RUN IS PRICED FROM ITS SNAPSHOT (<c>execution_snapshots</c>, written at start): its seconds
/// (<c>ended_at - started_at</c>, 0 with no start) times the snapshot's <c>rate_amount</c> an hour,
/// rounded to the cent (<see cref="Price"/>), in the snapshot's <c>rate_currency</c>. A snapshot with no
/// rate, or a run from before snapshots, is UNAVAILABLE: no amount - never 0, never today's tenant rate,
/// never a rate rebuilt from history. A rate changed after a run started never reaches it.
/// </para>
///
/// <para>
/// AGENT HOURS ARE AGENT TIME: the seconds of agent-kind runs, priced or not. A plugin's seconds are
/// never Agent Hours; a plugin with its own snapshotted rate adds its estimate to the money and nothing
/// to the hours, and one with none adds nothing at all. The kind is the snapshot's, else - before
/// snapshots - the ledger row's own; anything that is not a plugin is agent time.
/// </para>
///
/// <para>
/// A RUN SERVES ONE OUTCOME OR NONE. A snapshotted run serves the outcome its <c>link_id</c> named at
/// start, followed through <c>merged_into</c>; no link, an unlink, or an outcome that is gone is no
/// outcome, and a later link does not move it. A run with no snapshot has no <c>link_id</c> and keeps
/// the workflow's newest link (<see cref="OutcomeFigures.CurrentOutcomes"/>).
/// </para>
///
/// <para>
/// A RUN THAT CROSSES A WEEK IS SPLIT BY OVERLAP: each week gets the part of [started_at, ended_at)
/// inside it, priced on its own and rounded to the cent the same way. Tokens are reported when a run
/// ends, so they stay in the week it ended. ONE CURRENCY PER FIGURE: money is summed only in the
/// figure's currency; a run priced in another is left out of the money, never converted or added.
/// </para>
/// </summary>
public static class ExecutionCost
{
    /// <summary>One run as every figure reads it.</summary>
    public sealed record Run(
        UsageLedgerRow Ledger,
        double Seconds,
        string Kind,
        decimal? Rate,
        string? Currency,
        string? RateSource,
        string? OutcomeId)
    {
        /// <summary>Whether its seconds are Agent Hours: everything that is not a plugin.</summary>
        public bool IsAgent => Kind != MemberRef.PluginKind;

        /// <summary>The whole run's estimate, or null when it is unavailable.</summary>
        public decimal? Estimate => Rate is { } rate ? Price(Seconds, rate) : null;

        /// <summary>Its start, when it has one: a run with none has no span and adds no seconds.</summary>
        public DateTimeOffset? StartedAt => Ledger.StartedAt;

        public DateTimeOffset EndedAt => Ledger.EndedAt;

        /// <summary>How much of [started_at, ended_at) lies in [<paramref name="from"/>, <paramref name="to"/>).</summary>
        public double SecondsIn(DateTimeOffset? from, DateTimeOffset? to)
        {
            if (StartedAt is not { } started) return 0;

            var start = from is { } f && f > started ? f : started;
            var end = to is { } t && t < EndedAt ? t : EndedAt;
            return end > start ? (end - start).TotalSeconds : 0;
        }

        /// <summary>Whether it is in [<paramref name="from"/>, <paramref name="to"/>): its span meets it, or -
        /// never started - it ended in it.</summary>
        public bool Overlaps(DateTimeOffset from, DateTimeOffset to) =>
            StartedAt is { } started
                ? (started < to && EndedAt > from) || (EndedAt >= from && EndedAt < to)
                : EndedAt >= from && EndedAt < to;
    }

    /// <summary>
    /// SECONDS AT A RATE, TO THE CENT: <c>seconds / 3600 x rate</c>, rounded as <see cref="decimal.Round(decimal, int)"/>
    /// rounds - to even at a half cent. The one rounding every estimate, whole run or week, is made with.
    /// </summary>
    public static decimal Price(double seconds, decimal rate) => decimal.Round((decimal)seconds / 3600m * rate, 2);

    /// <summary>Each row of <paramref name="rows"/> as a <see cref="Run"/>: its kind, its snapshot rate and
    /// the outcome it serves, with <paramref name="outcomes"/> as they are now.</summary>
    public static IReadOnlyList<Run> Of(ExecutionRunRows rows, IReadOnlyList<Outcome> outcomes)
    {
        var resolution = OutcomeFigures.Resolution(outcomes);
        var newest = OutcomeFigures.CurrentOutcomes(rows.CurrentLinks, resolution);

        return rows.Runs.Select(row => Of(row, resolution, newest)).ToList();
    }

    /// <summary>One row as a <see cref="Run"/>, with the outcomes resolved and each workflow's newest link's
    /// outcome given.</summary>
    public static Run Of(
        ExecutionRunRow row, IReadOnlyDictionary<string, string> resolution, IReadOnlyDictionary<long, string> newest)
    {
        var ledger = row.Run;
        var seconds = OutcomeFigures.AgentSeconds(ledger);

        if (row.Snapshot is not { } snapshot)
        {
            return new Run(
                ledger, seconds, ledger.MemberKind ?? MemberRef.AgentKind, null, null, null,
                newest.GetValueOrDefault(ledger.Correlation));
        }

        var rate = snapshot.RateAmount is { } text
            && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            && amount > 0
                ? amount
                : (decimal?)null;

        var outcome = snapshot.LinkFound && snapshot.LinkOutcomeId is { } named && resolution.TryGetValue(named, out var holder)
            ? holder
            : null;

        return new Run(
            ledger, seconds, snapshot.MemberKind ?? ledger.MemberKind ?? MemberRef.AgentKind,
            rate, rate is null ? null : snapshot.RateCurrency?.ToUpperInvariant(), snapshot.RateSource, outcome);
    }

    /// <summary>
    /// A FIGURE IN THE MAKING: agent seconds, the money of the priced parts in the figure's currency,
    /// the priced agent parts behind the weighted rate, and the tokens of the runs that ended in it.
    /// </summary>
    public sealed class Tally(string currency)
    {
        public string Currency { get; } = currency;
        public double AgentSeconds { get; private set; }
        public long Billable { get; private set; }
        public int MeasuredRuns { get; private set; }
        public int UnmeasuredRuns { get; private set; }

        private decimal _money;
        private bool _priced;
        private decimal _agentMoney;
        private double _agentPricedSeconds;

        /// <summary>The money, or null - never 0 - when no part of it was priced.</summary>
        public decimal? Cost => _priced ? _money : null;

        /// <summary>
        /// THE WEIGHTED AGENT RATE: the priced agent parts' money over their hours, to the cent; null when
        /// no agent time in it was priced. Never the tenant rate, and never hours times an average.
        /// </summary>
        public decimal? AgentRate =>
            _agentPricedSeconds > 0 ? decimal.Round(_agentMoney / ((decimal)_agentPricedSeconds / 3600m), 2) : null;

        /// <summary><paramref name="seconds"/> of <paramref name="run"/>'s span, priced on their own.</summary>
        public void AddSpan(Run run, double seconds)
        {
            if (seconds <= 0) return;
            if (run.IsAgent) AgentSeconds += seconds;

            if (run.Rate is not { } rate || !string.Equals(run.Currency, Currency, StringComparison.OrdinalIgnoreCase)) return;

            var money = Price(seconds, rate);
            _money += money;
            _priced = true;

            if (!run.IsAgent) return;
            _agentMoney += money;
            _agentPricedSeconds += seconds;
        }

        /// <summary>The tokens <paramref name="run"/> reported when it ended.</summary>
        public void AddEnd(Run run)
        {
            if (run.Ledger.Measured)
            {
                Billable += run.Ledger.Billable ?? 0;
                MeasuredRuns++;
            }
            else
            {
                UnmeasuredRuns++;
            }
        }

        /// <summary>Another tally's parts added to this one: the same as adding its runs' parts here.</summary>
        public void Add(Tally other)
        {
            AgentSeconds += other.AgentSeconds;
            Billable += other.Billable;
            MeasuredRuns += other.MeasuredRuns;
            UnmeasuredRuns += other.UnmeasuredRuns;
            _money += other._money;
            _priced |= other._priced;
            _agentMoney += other._agentMoney;
            _agentPricedSeconds += other._agentPricedSeconds;
        }
    }

    /// <summary>
    /// <paramref name="runs"/> over the weeks starting at <paramref name="weekStarts"/> (seven days each),
    /// oldest first: each run's span split by overlap, its tokens in the week it ended. A run outside
    /// every week adds nothing.
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset WeekStart, Tally Tally)> Weeks(
        IEnumerable<Run> runs, IReadOnlyList<DateTimeOffset> weekStarts, string currency)
    {
        var weeks = weekStarts.Select(start => (WeekStart: start, Tally: new Tally(currency))).ToList();

        foreach (var run in runs)
        {
            foreach (var (start, tally) in weeks)
            {
                var end = start.AddDays(7);
                tally.AddSpan(run, run.SecondsIn(start, end));
                if (run.EndedAt >= start && run.EndedAt < end) tally.AddEnd(run);
            }
        }

        return weeks;
    }

    /// <summary>The weeks summed: what a figure over all of them says.</summary>
    public static Tally Total(IEnumerable<(DateTimeOffset WeekStart, Tally Tally)> weeks, string currency)
    {
        var total = new Tally(currency);
        foreach (var (_, tally) in weeks) total.Add(tally);
        return total;
    }

    /// <summary>The weeks as the routes answer them.</summary>
    public static IReadOnlyList<OutcomeFigures.Week> Weekly(IEnumerable<Run> runs, IReadOnlyList<DateTimeOffset> weekStarts, string currency) =>
        Weeks(runs, weekStarts, currency)
            .Select(w => new OutcomeFigures.Week(
                w.WeekStart, w.Tally.AgentSeconds, w.Tally.Billable, w.Tally.MeasuredRuns, w.Tally.UnmeasuredRuns, w.Tally.Cost))
            .ToList();

    /// <summary>
    /// EACH OUTCOME'S MONEY over [<paramref name="from"/>, <paramref name="to"/>) - all time with neither -
    /// from <paramref name="runs"/>, each run once, under the outcome it serves; the runs serving none
    /// under <see cref="Costs.Unattributed"/>. With no window a run's whole estimate is summed; with one,
    /// the part of its span inside it. An outcome no priced run served is absent: its cost is unavailable.
    /// </summary>
    public static Costs CostsOf(IEnumerable<Run> runs, DateTimeOffset? from, DateTimeOffset? to, string currency)
    {
        var byOutcome = new Dictionary<string, decimal>(StringComparer.Ordinal);
        decimal? unattributed = null;
        var windowed = from is not null || to is not null;

        foreach (var run in runs)
        {
            if (run.Rate is not { } rate || !string.Equals(run.Currency, currency, StringComparison.OrdinalIgnoreCase)) continue;

            var seconds = windowed ? run.SecondsIn(from, to) : run.Seconds;
            if (seconds <= 0) continue;

            var money = Price(seconds, rate);
            if (run.OutcomeId is { } outcome) byOutcome[outcome] = byOutcome.GetValueOrDefault(outcome) + money;
            else unattributed = (unattributed ?? 0) + money;
        }

        return new Costs(byOutcome, unattributed);
    }

    /// <summary>Money by outcome, and the money of the runs that served none; null - never 0 - when none was priced.</summary>
    public sealed record Costs(IReadOnlyDictionary<string, decimal> ByOutcome, decimal? Unattributed)
    {
        public decimal? Of(string? outcome) =>
            outcome is null ? Unattributed : ByOutcome.TryGetValue(outcome, out var money) ? money : null;
    }
}
