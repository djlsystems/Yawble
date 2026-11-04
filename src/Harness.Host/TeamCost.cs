using System.ComponentModel;
using System.Globalization;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;
using Harness.Host.Solutions;
using Harness.Kanban;

namespace Harness.Host;

/// <summary>
/// WHAT A TEAM'S EXECUTION IS ESTIMATED TO HAVE COST, BY OUTCOME, OVER THE LAST EIGHT WEEKS, for the team
/// page's tile and the teams list. Every run is priced by <see cref="ExecutionCost"/>, and nothing here
/// prices one another way.
///
/// <para>
/// A RUN IS PRICED FROM THE SNAPSHOT TAKEN AT ITS START, never from the rate set now: a rate changed
/// later does not move a past run, and a snapshot with no rate - a plugin with no rate of its own -
/// adds no money, never 0. A run from before snapshots is priced from the tenant rate in effect at its
/// start (<c>reconstructed</c>) or the rate captured at upgrade (<c>fallback-at-upgrade</c>), a plugin's
/// never; <c>legacyRates</c> says when a figure used either. <c>agentSeconds</c> is agent-kind time
/// only; <c>cost</c> is every priced run, agent and plugin; <c>agentRate</c> is the priced agent time's
/// weighted rate. The weeks are <see cref="OutcomeFigures.WeekStarts"/>, and a run crossing one is split
/// by overlap. A run that reported no usage is counted in <c>unmeasuredRuns</c> and adds nothing to
/// <c>billable</c>.
/// </para>
///
/// <para>
/// A RUN SERVES ONE OUTCOME: a snapshotted run the one its link named at start, followed through
/// <c>merged_into</c>; a run from before snapshots its workflow's newest link. No link, an unlink, or
/// an outcome that is gone is Unattributed.
/// </para>
///
/// <para>
/// A TEAM'S WORK is its runs: the ledger rows carrying its id, above the team's floor
/// (<see cref="TeamRegistry.FloorFor"/>, as the Tokens tile reads them, so a team recreated under the
/// same id never shows its predecessor's work) that overlap the eight weeks. An outcome is listed for a
/// team when at least one such run served it.
/// </para>
///
/// <para>
/// THE ORDER IS FIXED: Unattributed first when the team has any, then the outcomes by when each was
/// created (<see cref="Outcome.CreatedAt"/>), oldest first, ties by id - so a chart's series keep their
/// place from one week, and one team, to the next.
/// </para>
///
/// <para>
/// THE RANK IS GLOBAL: each outcome entry carries <c>rank</c>, its 1-based place in that same order
/// among every outcome that ANY team's runs served in the eight weeks, so one outcome has one rank -
/// and the web one colour - on every team, whichever of its outcomes a team happens to list.
/// </para>
///
/// <para>
/// <c>outcomeTotalCost</c> is the outcome's own estimate - every team's priced runs on it, all time, the
/// figure <c>GET /api/outcomes</c> answers as its <c>cost</c> with no window - because a budget is shared
/// by every team serving the outcome and is measured against all of it.
/// </para>
/// </summary>
public static class TeamCost
{
    /// <summary>The name of the entry carrying a team's work that serves no outcome.</summary>
    public const string UnattributedName = OutcomeFigures.UnattributedName;

    public sealed record MoneyShape(string Currency, int? AgentHourlyRate);

    /// <summary>A figure's totals. <c>agentSeconds</c> is agent-kind time only; <c>cost</c> is every priced run's
    /// estimate, agent and plugin, null - never 0 - when none was priced; <c>agentRate</c> is the weighted rate
    /// of the priced agent time, null when there was none. <c>legacyRates</c> is the figure's footnote:
    /// <c>reconstructed</c> and <c>fallback-at-upgrade</c>, each when a run from before snapshots was priced into
    /// it that way; empty when none was.</summary>
    public sealed record Totals(
        double AgentSeconds, long Billable, int MeasuredRuns, int UnmeasuredRuns, decimal? Cost, decimal? AgentRate,
        IReadOnlyList<string> LegacyRates);

    public sealed record OutcomeEntry(
        string? Id, string Name, decimal? Value,
        IReadOnlyList<OutcomeFigures.Week> Weekly, Totals Totals, decimal? OutcomeTotalCost, int? Rank)
    {
        /// <summary>The team's Human Interventions and Human Attention Time (measured only) under this entry's outcome
        /// (Unattributed's: those serving none) over the eight weeks (<see cref="HumanFigures"/>). Never priced.</summary>
        public HumanFigures.Figures Human { get; init; } = HumanFigures.Figures.None;
    }

    public sealed record Amount(double AgentSeconds, decimal? Cost, decimal? AgentRate, IReadOnlyList<string> LegacyRates);

    public sealed record SummaryWeek(string WeekStart, double AgentSeconds, decimal? Cost);

    public sealed record SummaryOutcome(
        string? Id, string Name, int? Rank, double AgentSeconds, decimal? Cost, decimal? Budget, decimal? OutcomeTotalCost,
        IReadOnlyList<string> LegacyRates)
    {
        /// <summary>The reached teams' Human Interventions and Human Attention Time under this outcome over the eight weeks.</summary>
        public HumanFigures.Figures Human { get; init; } = HumanFigures.Figures.None;
    }

    public sealed record RightNow(int OpenWorkflows, int MembersRunning, int WaitingOnYou);

    public sealed record Summary(
        int? Rate, IReadOnlyList<SummaryWeek> Weeks, string PeriodStart, string PeriodEnd,
        Amount Total, Amount Prior, IReadOnlyList<SummaryOutcome> Outcomes, RightNow RightNow)
    {
        /// <summary>The reached teams' human figures over the eight weeks, Unattributed's count and Waiting on a person
        /// beside them, never added in (<see cref="HumanFigures.Team"/>).</summary>
        public HumanFigures.Team? Human { get; init; }
    }

    /// <summary>One team's entry. <see cref="Human"/> is its human figures over the eight weeks, with Waiting on a
    /// person beside them.</summary>
    public sealed record TeamEntry(string Team, IReadOnlyList<OutcomeEntry> Outcomes, Totals Totals)
    {
        public HumanFigures.Team? Human { get; init; }
    }

    /// <summary>One read of everything every team's figures need: the runs that overlap the weeks (and,
    /// for the summary, the eight before them) once, priced by <see cref="ExecutionCost"/>; the outcomes
    /// once; and each outcome's all-time, all-teams estimate from the priced runs alone.</summary>
    public sealed class Reading
    {
        public required OutcomeFigures.Pricing Pricing { get; init; }
        public required DateTimeOffset Now { get; init; }
        public required IReadOnlyList<DateTimeOffset> Weeks { get; init; }
        public required IReadOnlyList<ExecutionCost.Run> Runs { get; init; }
        public required IReadOnlyDictionary<string, Outcome> OutcomesById { get; init; }
        public required IReadOnlyDictionary<string, decimal> OutcomeTotalCost { get; init; }

        /// <summary>Every person's act whose <c>occurred_at</c> is in the eight weeks (<see cref="HumanFigures"/>).</summary>
        public IReadOnlyList<InterventionEntry> Interventions { get; init; } = [];

        /// <summary>Every outcome id followed through <c>merged_into</c>: where an act counts.</summary>
        public IReadOnlyDictionary<string, string> Resolution { get; init; } = new Dictionary<string, string>();

        /// <summary>The whole ledger, for Waiting on a person: a blocked span may start before the weeks.</summary>
        public OutcomeLedgerRows? Ledger { get; init; }

        /// <summary>Each outcome any team's runs served in the eight weeks, by its 1-based place in the
        /// fixed order (created, oldest first, ties by id): the same for every team.</summary>
        public required IReadOnlyDictionary<string, int> Ranks { get; init; }

        /// <summary>The currency and the TENANT DEFAULT rate as set now - what the Outcomes dialog edits. No
        /// figure here is that rate times hours: each run is priced from its own snapshot.</summary>
        public MoneyShape Money => new(Pricing.Currency, Pricing.AgentHourlyRate > 0 ? Pricing.AgentHourlyRate : null);

        private string Currency => Pricing.Currency;

        /// <summary>
        /// The reading at <paramref name="now"/>: the runs overlapping the eight weeks - from the eight
        /// before them too with <paramref name="withPrior"/> - in one windowed read, and the priced runs of
        /// all time in another, for each outcome's total.
        /// </summary>
        public static async Task<Reading> ReadAsync(
            IOutcomeStore store, TenantSettings settings, DateTimeOffset now, CancellationToken ct, bool withPrior = false)
        {
            var outcomes = await store.ListAsync(ct);
            var pricing = OutcomeFigures.Pricing.Of(settings);

            var weeks = OutcomeFigures.WeekStarts(now);
            var from = weeks[0];
            var to = weeks[^1].AddDays(7);
            var readFrom = withPrior ? from.AddDays(-7 * OutcomeFigures.Weeks) : from;

            var runs = ExecutionCost.Of(await store.ReadExecutionRunsAsync(readFrom, to, pricedOnly: false, ct), outcomes, pricing.Currency);

            // EVERY PRICED RUN OF EVERY TEAM, ALL TIME, BY THE OUTCOME IT SERVES: what an outcome's budget is
            // measured against, and what GET /api/outcomes answers as its cost with no window.
            var priced = ExecutionCost.Of(await store.ReadExecutionRunsAsync(null, null, pricedOnly: true, ct), outcomes, pricing.Currency);
            var totals = ExecutionCost.CostsOf(priced, null, null, pricing.Currency);

            // THE GLOBAL ORDER: every outcome served in the weeks by any team's run, whatever team asks.
            var byId = outcomes.ToDictionary(o => o.Id, StringComparer.Ordinal);
            var ranks = runs
                .Where(r => r.Ledger.TeamId is not null && r.Overlaps(from, to))
                .Select(r => r.OutcomeId)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Select(id => byId.GetValueOrDefault(id))
                .OfType<Outcome>()
                .OrderBy(o => o.CreatedAt)
                .ThenBy(o => o.Id, StringComparer.Ordinal)
                .Select((o, index) => (o.Id, Rank: index + 1))
                .ToDictionary(e => e.Id, e => e.Rank, StringComparer.Ordinal);

            return new Reading
            {
                Pricing = pricing,
                Now = now,
                Weeks = weeks,
                Runs = runs,
                OutcomesById = byId,
                Ranks = ranks,
                OutcomeTotalCost = totals.ByOutcome,
                Interventions = await store.ReadInterventionsAsync(from, to, ct),
                Resolution = OutcomeFigures.Resolution(outcomes),
                Ledger = await store.ReadLedgerAsync(null, null, ct),
            };
        }

        /// <summary>Every team's runs in the eight weeks, above its floor.</summary>
        public IReadOnlyDictionary<string, List<ExecutionCost.Run>> RunsByTeam(IReadOnlyDictionary<string, long> floors) =>
            RunsByTeam(floors, Weeks[0], Weeks[^1].AddDays(7));

        /// <summary>Every team's runs above its floor that overlap [<paramref name="from"/>,
        /// <paramref name="to"/>), in one pass.</summary>
        public IReadOnlyDictionary<string, List<ExecutionCost.Run>> RunsByTeam(
            IReadOnlyDictionary<string, long> floors, DateTimeOffset from, DateTimeOffset to)
        {
            var byTeam = new Dictionary<string, List<ExecutionCost.Run>>(StringComparer.OrdinalIgnoreCase);

            foreach (var run in Runs)
            {
                if (run.Ledger.TeamId is not { } team || !floors.TryGetValue(team, out var floor)) continue;
                if (run.Ledger.RunSeq <= floor || !run.Overlaps(from, to)) continue;

                if (!byTeam.TryGetValue(team, out var list)) byTeam[team] = list = [];
                list.Add(run);
            }

            return byTeam;
        }

        /// <summary>
        /// WAITING ON A PERSON over the eight weeks, per team in <paramref name="floors"/>: the blocked spans of its
        /// runs above its floor (<see cref="OutcomeFigures.BlockedByTeam"/>). Carried beside the human figures, never
        /// added to Human Attention Time.
        /// </summary>
        public IReadOnlyDictionary<string, double> Waiting(IReadOnlyDictionary<string, long> floors) =>
            Ledger is null
                ? new Dictionary<string, double>()
                : OutcomeFigures.BlockedByTeam(Ledger, Now, Weeks[0], Weeks[^1].AddDays(7), floors);

        /// <summary>One team's entry from its runs in the eight weeks, its acts and <paramref name="waitingSeconds"/>.</summary>
        public TeamEntry Team(string team, IReadOnlyList<ExecutionCost.Run> runs, double waitingSeconds = 0)
        {
            var acts = HumanFigures.OfTeam(Interventions, team).ToList();

            var entries = runs
                .GroupBy(r => r.OutcomeId)
                .Select(g => (Outcome: g.Key is null ? null : OutcomesById.GetValueOrDefault(g.Key), Runs: g.ToList()))
                .OrderBy(e => e.Outcome is null ? 0 : 1)
                .ThenBy(e => e.Outcome?.CreatedAt)
                .ThenBy(e => e.Outcome?.Id, StringComparer.Ordinal)
                .Select(e =>
                {
                    var weeks = ExecutionCost.Weeks(e.Runs, Weeks, Currency);
                    return new OutcomeEntry(
                        e.Outcome?.Id,
                        e.Outcome?.Name ?? UnattributedName,
                        BudgetOf(e.Outcome),
                        WeeklyOf(weeks),
                        TotalsOf(ExecutionCost.Total(weeks, Currency)),
                        e.Outcome is null ? null : TotalCostOf(e.Outcome.Id),
                        e.Outcome is null ? null : Ranks.GetValueOrDefault(e.Outcome.Id))
                    {
                        Human = HumanFigures.Of(HumanFigures.Serving(acts, e.Outcome?.Id, Resolution)),
                    };
                })
                .ToList();

            return new TeamEntry(team, entries, TotalsOf(ExecutionCost.Total(ExecutionCost.Weeks(runs, Weeks, Currency), Currency)))
            {
                Human = HumanFigures.ForTeam(acts, team, Resolution, waitingSeconds),
            };
        }

        /// <summary>
        /// THE INSTANCE'S SUMMARY over the teams in <paramref name="floors"/>: the eight weeks summed
        /// across them, the same over the eight weeks before, each outcome's share of the weeks, and
        /// <paramref name="rightNow"/> as given. Every figure is the per-team entries' own runs, summed.
        /// </summary>
        public Summary SummaryOf(IReadOnlyDictionary<string, long> floors, RightNow rightNow)
        {
            var acts = Interventions.Where(r => r.TeamId is not null && floors.ContainsKey(r.TeamId)).ToList();

            var runs = RunsByTeam(floors).Values.SelectMany(r => r).ToList();
            var priorTo = Weeks[0];
            var priorWeeks = Weeks.Select(w => w.AddDays(-7 * OutcomeFigures.Weeks)).ToList();
            var prior = RunsByTeam(floors, priorTo.AddDays(-7 * OutcomeFigures.Weeks), priorTo).Values.SelectMany(r => r).ToList();

            var weeks = ExecutionCost.Weeks(runs, Weeks, Currency);
            var weekly = weeks.Select(w => new SummaryWeek(Day(w.WeekStart), w.Tally.AgentSeconds, w.Tally.Cost)).ToList();

            var served = runs
                .GroupBy(r => r.OutcomeId ?? "")
                .ToDictionary(g => g.Key, g => ExecutionCost.Total(ExecutionCost.Weeks(g, Weeks, Currency), Currency), StringComparer.Ordinal);

            // AN OUTCOME WITH A BUDGET STAYS, spent in the weeks or not, while it is live - so Budgets
            // can measure it; an outcome merged away carries no budget of its own.
            var budgeted = OutcomesById.Values
                .Where(o => o.MergedInto is null && o.IsLive && BudgetOf(o) is not null)
                .Select(o => o.Id);

            var outcomes = served.Keys.Where(k => k.Length > 0).Concat(budgeted)
                .Distinct(StringComparer.Ordinal)
                .Select(id => OutcomesById[id])
                .OrderBy(o => o.CreatedAt)
                .ThenBy(o => o.Id, StringComparer.Ordinal)
                .Select(o =>
                {
                    var tally = served.GetValueOrDefault(o.Id);
                    return new SummaryOutcome(
                        o.Id, o.Name, Ranks.TryGetValue(o.Id, out var rank) ? rank : null, tally?.AgentSeconds ?? 0, tally?.Cost,
                        BudgetOf(o), TotalCostOf(o.Id), tally?.LegacyRates ?? [])
                    {
                        Human = HumanFigures.Of(HumanFigures.Serving(acts, o.Id, Resolution)),
                    };
                })
                .ToList();

            if (served.TryGetValue("", out var unlinked))
            {
                outcomes.Insert(0, new SummaryOutcome(null, UnattributedName, null, unlinked.AgentSeconds, unlinked.Cost, null, null, unlinked.LegacyRates)
                {
                    Human = HumanFigures.Of(HumanFigures.Serving(acts, null, Resolution)),
                });
            }

            // THE REACHED TEAMS' ACTS, each row once (by its team_id; a Concierge session has none), and their waiting.
            var all = HumanFigures.Of(acts);
            var human = new HumanFigures.Team(
                all.Interventions, all.MeasuredInterventions, all.AttentionSeconds,
                HumanFigures.Of(HumanFigures.Serving(acts, null, Resolution)), Waiting(floors).Values.Sum());

            return new Summary(
                Money.AgentHourlyRate,
                weekly,
                Day(Weeks[0]),
                Day(Now),
                AmountOf(ExecutionCost.Total(weeks, Currency)),
                AmountOf(ExecutionCost.Total(ExecutionCost.Weeks(prior, priorWeeks, Currency), Currency)),
                outcomes,
                rightNow)
            {
                Human = human,
            };
        }

        /// <summary>An outcome's estimate across every team and all time; null - never 0 - when no priced run served it.</summary>
        private decimal? TotalCostOf(string outcome) => OutcomeTotalCost.TryGetValue(outcome, out var cost) ? cost : null;

        private static IReadOnlyList<OutcomeFigures.Week> WeeklyOf(IEnumerable<(DateTimeOffset WeekStart, ExecutionCost.Tally Tally)> weeks) =>
            weeks.Select(w => new OutcomeFigures.Week(
                    w.WeekStart, w.Tally.AgentSeconds, w.Tally.Billable, w.Tally.MeasuredRuns, w.Tally.UnmeasuredRuns, w.Tally.Cost))
                .ToList();

        private static Amount AmountOf(ExecutionCost.Tally tally) => new(tally.AgentSeconds, tally.Cost, tally.AgentRate, tally.LegacyRates);

        private static string Day(DateTimeOffset at) =>
            at.ToUniversalTime().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static Totals TotalsOf(ExecutionCost.Tally tally) =>
            new(tally.AgentSeconds, tally.Billable, tally.MeasuredRuns, tally.UnmeasuredRuns, tally.Cost, tally.AgentRate, tally.LegacyRates);

        /// <summary>An outcome's budget as a number: its <see cref="Outcome.Value"/>, kept canonical
        /// (<see cref="OutcomeValue"/>); null when none is set.</summary>
        public static decimal? BudgetOf(Outcome? outcome) =>
            outcome?.Value is { } text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/teams/{team}/cost", async (
                [Description(Describe.Team)] string team,
                TeamRegistry teams, IOutcomeStore outcomes, TenantSettings settings, CancellationToken ct) =>
            {
                if (teams.ExistingName(team) is not { } stored) return Refusals.NoTeam(team);

                var reading = await Reading.ReadAsync(outcomes, settings, DateTimeOffset.UtcNow, ct);
                var floors = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [stored] = teams.FloorFor(stored) };
                var runs = reading.RunsByTeam(floors);
                var entry = reading.Team(stored, runs.GetValueOrDefault(stored) ?? [], reading.Waiting(floors).GetValueOrDefault(stored));

                return Results.Ok(new
                {
                    team = entry.Team,
                    money = reading.Money,
                    weeks = reading.Weeks,
                    outcomes = entry.Outcomes,
                    totals = entry.Totals,
                    human = entry.Human,
                });
            })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("What a team cost over the last eight weeks, by outcome")
            .WithDescription(Description + "\n\n404 for an unknown team.");

        // EVERY REACHABLE TEAM AT ONCE, for the teams list: one ledger read for all of them, never one
        // per row. It declares no `{team}`, so - as `/api/teams/rollup` - it resolves access itself.
        app.MapGet("/api/teams/cost", async (
                HttpContext context, TeamRegistry teams, TeamAccess access, IOutcomeStore outcomes,
                TenantSettings settings, CancellationToken ct) =>
            {
                if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

                var effective = await access.EffectiveTeamsAsync(principal, ct);
                var reached = teams.All().Where(t => effective.Contains(t.Id)).Select(t => t.Id).ToList();

                var reading = await Reading.ReadAsync(outcomes, settings, DateTimeOffset.UtcNow, ct);
                var floors = reached.ToDictionary(t => t, teams.FloorFor, StringComparer.OrdinalIgnoreCase);
                var runs = reading.RunsByTeam(floors);
                var waiting = reading.Waiting(floors);

                return Results.Ok(new
                {
                    money = reading.Money,
                    weeks = reading.Weeks,
                    teams = reached.Select(t => reading.Team(t, runs.GetValueOrDefault(t) ?? [], waiting.GetValueOrDefault(t))).ToList(),
                });
            })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("What every reachable team cost over the last eight weeks, by outcome")
            .WithDescription(
                "`GET /api/teams/{team}/cost` for every team the caller reaches, archived ones included, "
                + "read in one pass: `money`, `weeks`, and `teams`, one `{ team, outcomes, totals }` each, "
                + "the same entries the per-team route answers. A team with no work in the weeks has empty "
                + "`outcomes` and zero totals. An empty list means the caller reaches no team.\n\n" + Description);
        // THE TEAMS LIST'S SUMMARY: the all-teams read above summed, the eight weeks before it, and the
        // counts the rollup and Needs you answer.
        app.MapGet("/api/teams/cost/summary", async (
                HttpContext context, TeamRegistry teams, TeamAccess access, IOutcomeStore outcomes,
                TenantSettings settings, RollupCache rollup, ContainerHost host, WipLedger wip,
                IBacklogStore backlog, IMessageLog log, KanbanStore kanban, BacklogLandedCache landedCache,
                UnfinishedRemovalRetry removals, ITeamSolutionStore solutions, SolutionInstaller installer,
                SolutionWait waiting, PluginSetup setup, CancellationToken ct) =>
            {
                if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

                var effective = await access.EffectiveTeamsAsync(principal, ct);
                var reached = teams.All().Where(t => effective.Contains(t.Id)).ToList();

                // RIGHT NOW AS THE TEAMS LIST READS IT: the rollup's open count and run slots over the
                // active teams the caller reaches, and the Needs you list's own count, both parts.
                var open = 0;
                var active = reached.Where(t => !t.Archived).Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var team in active)
                {
                    var (_, workflows) = await rollup.ReadAsync(team, TeamEndpoints.RollupFloor(teams, host, team), ct);
                    open += workflows.OpenCount;
                }
                var running = wip.View().Running.Count(hold => active.Contains(hold.Team));

                var names = teams.All().ToDictionary(t => t.Id, t => t.Name, StringComparer.OrdinalIgnoreCase);
                var needsYou = (await NeedsYou.LiveAsync(names, host, backlog, solutions, installer, waiting, ct, setup)).Count
                    + (await NeedsYou.StoredAsync(names, teams, host, backlog, log, kanban, landedCache, removals, ct)).Count;

                var reading = await Reading.ReadAsync(outcomes, settings, DateTimeOffset.UtcNow, ct, withPrior: true);
                var floors = reached.ToDictionary(t => t.Id, t => teams.FloorFor(t.Id), StringComparer.OrdinalIgnoreCase);

                return Results.Ok(reading.SummaryOf(floors, new RightNow(open, running, needsYou)));
            })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("What every reachable team cost over the last eight weeks, summed, with what needs a person now")
            .WithDescription(
                "The teams list's summary, over every team the caller reaches (archived ones included for "
                + "spend), read in one pass from the same ledger rows `GET /api/teams/cost` answers.\n\n"
                + "`rate`: the tenant default agent rate as set now, null when none is set; no figure is that rate "
                + "times hours. `weeks`: 8 of `{ weekStart, agentSeconds, "
                + "cost }`, oldest first, Monday 00:00 UTC on, `weekStart` as `YYYY-MM-DD`, the last the current "
                + "week - the same weeks as an outcome's `weekly`. `periodStart`: the first week's Monday; "
                + "`periodEnd`: today (UTC). `total`: `{ agentSeconds, cost, agentRate, legacyRates }` over the 8 weeks; `prior`: the same "
                + "over the 8 weeks before them.\n\n"
                + "`outcomes`: `{ id, name, rank, agentSeconds, cost, budget, outcomeTotalCost, legacyRates }` for each outcome the "
                + "runs served in the weeks, and each live outcome with a budget even with none; `Unattributed` "
                + "(`id` null) first for unlinked work when there is some, then the outcomes in the order they "
                + "were created. `rank` is the global rank `GET /api/teams/cost` gives (null for Unattributed and "
                + "for an outcome no team served in the weeks); `budget` is the outcome's `value` as a number; "
                + "`outcomeTotalCost` is its cost across every team and all time, what its budget is measured against "
                + "(the same field there) - both null for Unattributed.\n\n"
                + "`rightNow`: `openWorkflows` and `membersRunning`, the sums of `GET /api/teams/rollup`'s "
                + "`openWorkflows.openCount` and `running` over the active teams reached, and `waitingOnYou`, "
                + "the number of items `GET /api/needs-you` lists.\n\n"
                + "Cost is an estimate, never money paid: each run priced from the snapshot taken at its start, as "
                + "`GET /api/teams/cost` describes, and null - never 0 - when no run was priced.\n\n"
                + "`human`: the reached teams' Human Interventions, Human Attention Time and Waiting on a person over the 8 weeks, "
                + "as `GET /api/teams/cost` gives each team's, with `unattributed`; each outcome in `outcomes` carries its own "
                + "`human`. A Concierge session names no team, so it is in none of these counts.");
    }

    private const string Description =
        "The last 8 weeks, Monday 00:00 UTC on, oldest first - the same weeks as an outcome's `weekly` - "
        + "of the team's runs that overlap them, a run that crosses a week split by overlap. A run with a "
        + "snapshot serves the outcome its link named when it started, followed through `mergedInto`; a run "
        + "from before snapshots serves its workflow's newest link.\n\n"
        + "`money`: `currency`, and `agentHourlyRate`, the tenant default rate as set now (null when none is "
        + "set) - no figure is that rate times hours. `weeks`: the 8 week starts. "
        + "`outcomes`: one entry per outcome the team's runs served in the weeks, and `Unattributed` "
        + "(`id` null) for its unlinked work, listed FIRST and only when there is some; the outcomes follow "
        + "in the order they were created, oldest first. Each entry: `id`, `name`, `value` (its budget as a "
        + "number, null when none is set), `weekly` (8 of `weekStart`, `agentSeconds`, `billable`, "
        + "`measuredRuns`, `unmeasuredRuns`, `cost`), `totals` (the team's own over the 8 weeks: "
        + "`agentSeconds`, `billable`, `measuredRuns`, `unmeasuredRuns`, `cost`, `agentRate`, `legacyRates`) and `outcomeTotalCost` - "
        + "the outcome's estimate across EVERY team and all time, what `GET /api/outcomes` gives as its `cost`, "
        + "the figure its budget is measured against; null for Unattributed and when no priced run served it - "
        + "and `rank`, the outcome's 1-based place in that same order among every outcome ANY team served in "
        + "the weeks, the same on every team (the web's colour for it); null for Unattributed. `totals`: the "
        + "team's over all of it.\n\n"
        + "Each run is priced from the snapshot taken at its start: its hours times the snapshot's rate, rounded "
        + "to the cent, a week's part priced on its own. A snapshot with no rate - a plugin with no rate of "
        + "its own - adds no money. An agent run from before snapshots is priced from the tenant rate in effect "
        + "at its start (`reconstructed`), or - before every recorded rate change - the rate captured when the "
        + "rate history began (`fallback-at-upgrade`); a zero or missing rate, and every plugin run from before "
        + "snapshots, adds no money. `legacyRates` on `totals` names which of the two a figure used, empty when "
        + "neither. `cost` is every priced run, agent and "
        + "plugin, and null - never 0 - when none was priced. `agentSeconds` is agent-kind time only, priced or "
        + "not. `agentRate` is the priced agent time's money over its hours, null when none was priced. "
        + "`billable` sums measured runs only, in the week each ended; a run that reported no usage is counted "
        + "in `unmeasuredRuns`, never as zero tokens.\n\n"
        + HumanDescription;

    /// <summary>The human figures' part of each cost read's description.</summary>
    private const string HumanDescription =
        "PEOPLE'S ACTS, NEVER PRICED. Each outcome entry carries `human` - `{ interventions, measuredInterventions, "
        + "attentionSeconds }`, the team's Human Interventions under that outcome over the 8 weeks (each "
        + "`intervention_ledger` row once, by its `team_id`) and Human Attention Time, the sum of measured durations "
        + "only, null - no measured time - when none was measured. The team carries `human`: `interventions`, "
        + "`measuredInterventions`, `attentionSeconds`, `unattributed` (the same three for its acts serving no outcome) "
        + "and `waitingSeconds`, Waiting on a person - time its work waited on a person, kept beside the others and "
        + "never added to `attentionSeconds`. A Concierge session names no team, so a team's counts leave it out.";
}
