using System.ComponentModel;
using System.Globalization;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// WHAT A TEAM COST, BY OUTCOME, OVER THE LAST EIGHT WEEKS: the outcome figures
/// (<see cref="OutcomeFigures"/>) cut by team, for the team page's Cost tile and the teams list.
///
/// <para>
/// Nothing here has its own rule. A workflow serves the outcome its newest link names, followed
/// through <c>merged_into</c> (<see cref="OutcomeFigures.Open"/>); a link to an outcome that no longer
/// exists, an unlink, or no link at all is No outcome. The weeks are <see cref="OutcomeFigures.WeekStarts"/>
/// and each week is <see cref="OutcomeFigures.Weekly"/>. Cost is <see cref="OutcomeFigures.Pricing.CostOf"/>:
/// agent hours times the rate, null - never 0 - with no rate. A run that reported no usage is counted
/// in <c>unmeasuredRuns</c> and adds nothing to <c>billable</c>.
/// </para>
///
/// <para>
/// A TEAM'S WORK is its runs: the ledger rows carrying its id, above the team's floor
/// (<see cref="TeamRegistry.FloorFor"/>, as the Tokens tile reads them, so a team recreated under the
/// same id never shows its predecessor's spend) that ended in the eight weeks. An outcome is listed for
/// a team when at least one such run served it.
/// </para>
///
/// <para>
/// THE ORDER IS FIXED: No outcome first when the team has any, then the outcomes by when each was
/// created (<see cref="Outcome.CreatedAt"/>), oldest first, ties by id - so a chart's series keep their
/// place from one week, and one team, to the next.
/// </para>
///
/// <para>
/// <c>outcomeTotalCost</c> is the outcome's own cost - every team's work on it, all time, the figure
/// <c>GET /api/outcomes</c> answers as its <c>cost</c> with no window - because a budget is shared by
/// every team serving the outcome and is measured against all of it.
/// </para>
/// </summary>
public static class TeamCost
{
    /// <summary>The name of the entry carrying a team's unlinked work.</summary>
    public const string NoOutcomeName = "No outcome";

    public sealed record MoneyShape(string Currency, int? AgentHourlyRate);

    public sealed record Totals(double AgentSeconds, long Billable, int MeasuredRuns, int UnmeasuredRuns, decimal? Cost);

    public sealed record OutcomeEntry(
        string? Id, string Name, decimal? Value,
        IReadOnlyList<OutcomeFigures.Week> Weekly, Totals Totals, decimal? OutcomeTotalCost);

    public sealed record TeamEntry(string Team, IReadOnlyList<OutcomeEntry> Outcomes, Totals Totals);

    /// <summary>One read of everything every team's figures need: the whole ledger once, the
    /// outcomes once, and each outcome's all-teams cost summed in one pass.</summary>
    public sealed class Reading
    {
        public required OutcomeFigures.Pricing Pricing { get; init; }
        public required DateTimeOffset Now { get; init; }
        public required IReadOnlyList<DateTimeOffset> Weeks { get; init; }
        public required OutcomeFigures.Book Book { get; init; }
        public required IReadOnlyDictionary<string, Outcome> OutcomesById { get; init; }
        public required IReadOnlyDictionary<string, decimal?> OutcomeTotalCost { get; init; }

        public MoneyShape Money => new(Pricing.Currency, Pricing.AgentHourlyRate > 0 ? Pricing.AgentHourlyRate : null);

        public static async Task<Reading> ReadAsync(IOutcomeStore store, TenantSettings settings, DateTimeOffset now, CancellationToken ct)
        {
            var outcomes = await store.ListAsync(ct);
            var book = OutcomeFigures.Open(await store.ReadLedgerAsync(null, null, ct), outcomes, null, null);
            var pricing = OutcomeFigures.Pricing.Of(settings);

            // EVERY RUN OF EVERY TEAM, BY THE OUTCOME IT SERVES NOW: what OutcomeFigures.For sums as an
            // outcome's agentSeconds with no window, read in one pass instead of one per outcome.
            var seconds = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var run in book.Rows.Runs)
            {
                if (book.OutcomeOf.TryGetValue(run.Correlation, out var outcome))
                {
                    seconds[outcome] = seconds.GetValueOrDefault(outcome) + OutcomeFigures.AgentSeconds(run);
                }
            }

            return new Reading
            {
                Pricing = pricing,
                Now = now,
                Weeks = OutcomeFigures.WeekStarts(now),
                Book = book,
                OutcomesById = outcomes.ToDictionary(o => o.Id, StringComparer.Ordinal),
                OutcomeTotalCost = seconds.ToDictionary(e => e.Key, e => pricing.CostOf(e.Value), StringComparer.Ordinal),
            };
        }

        /// <summary>Every team's runs in the eight weeks, above its floor, in one pass over the ledger.</summary>
        public IReadOnlyDictionary<string, List<UsageLedgerRow>> RunsByTeam(IReadOnlyDictionary<string, long> floors)
        {
            var from = Weeks[0];
            var to = Weeks[^1].AddDays(7);
            var byTeam = new Dictionary<string, List<UsageLedgerRow>>(StringComparer.OrdinalIgnoreCase);

            foreach (var run in Book.Rows.Runs)
            {
                if (run.TeamId is not { } team || !floors.TryGetValue(team, out var floor)) continue;
                if (run.RunSeq <= floor || run.EndedAt < from || run.EndedAt >= to) continue;

                if (!byTeam.TryGetValue(team, out var list)) byTeam[team] = list = [];
                list.Add(run);
            }

            return byTeam;
        }

        /// <summary>One team's entry from its runs in the eight weeks.</summary>
        public TeamEntry Team(string team, IReadOnlyList<UsageLedgerRow> runs)
        {
            var entries = runs
                .GroupBy(r => Book.OutcomeOf.GetValueOrDefault(r.Correlation))
                .Select(g => (Outcome: g.Key is null ? null : OutcomesById.GetValueOrDefault(g.Key), Runs: g.ToList()))
                .OrderBy(e => e.Outcome is null ? 0 : 1)
                .ThenBy(e => e.Outcome?.CreatedAt)
                .ThenBy(e => e.Outcome?.Id, StringComparer.Ordinal)
                .Select(e => new OutcomeEntry(
                    e.Outcome?.Id,
                    e.Outcome?.Name ?? NoOutcomeName,
                    BudgetOf(e.Outcome),
                    OutcomeFigures.Weekly(e.Runs, Pricing, Now),
                    TotalsOf(e.Runs),
                    e.Outcome is null ? null : OutcomeTotalCost.GetValueOrDefault(e.Outcome.Id, Pricing.CostOf(0))))
                .ToList();

            return new TeamEntry(team, entries, TotalsOf(runs));
        }

        private Totals TotalsOf(IReadOnlyList<UsageLedgerRow> runs)
        {
            var seconds = runs.Sum(OutcomeFigures.AgentSeconds);
            return new Totals(
                seconds, runs.Where(r => r.Measured).Sum(r => r.Billable ?? 0),
                runs.Count(r => r.Measured), runs.Count(r => !r.Measured), Pricing.CostOf(seconds));
        }

        /// <summary>An outcome's budget as a number: its <see cref="Outcome.Value"/>, kept canonical
        /// (<see cref="OutcomeValue"/>); null when none is set.</summary>
        private static decimal? BudgetOf(Outcome? outcome) =>
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
                var runs = reading.RunsByTeam(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [stored] = teams.FloorFor(stored) });
                var entry = reading.Team(stored, runs.GetValueOrDefault(stored) ?? []);

                return Results.Ok(new
                {
                    team = entry.Team,
                    money = reading.Money,
                    weeks = reading.Weeks,
                    outcomes = entry.Outcomes,
                    totals = entry.Totals,
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

                return Results.Ok(new
                {
                    money = reading.Money,
                    weeks = reading.Weeks,
                    teams = reached.Select(t => reading.Team(t, runs.GetValueOrDefault(t) ?? [])).ToList(),
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
    }

    private const string Description =
        "The last 8 weeks, Monday 00:00 UTC on, oldest first - the same weeks as an outcome's `weekly` - "
        + "of the team's runs that ended in them, by the outcome each workflow serves now (its newest link, "
        + "followed through `mergedInto`).\n\n"
        + "`money`: `currency` and `agentHourlyRate` (null when no rate is set). `weeks`: the 8 week starts. "
        + "`outcomes`: one entry per outcome the team's runs served in the weeks, and `No outcome` "
        + "(`id` null) for its unlinked work, listed FIRST and only when there is some; the outcomes follow "
        + "in the order they were created, oldest first. Each entry: `id`, `name`, `value` (its budget as a "
        + "number, null when none is set), `weekly` (8 of `weekStart`, `agentSeconds`, `billable`, "
        + "`measuredRuns`, `unmeasuredRuns`, `cost`), `totals` (the team's own over the 8 weeks: "
        + "`agentSeconds`, `billable`, `measuredRuns`, `unmeasuredRuns`, `cost`) and `outcomeTotalCost` - "
        + "the outcome's cost across EVERY team and all time, what `GET /api/outcomes` gives as its `cost`, "
        + "the figure its budget is measured against; null for No outcome. `totals`: the team's over all "
        + "of it.\n\n"
        + "Cost is agent hours times `outcomes.agentHourlyRate`, rounded to the cent, and null - never 0 - "
        + "when no rate is set. `billable` sums measured runs only; a run that reported no usage is counted "
        + "in `unmeasuredRuns`, never as zero tokens.";
}
