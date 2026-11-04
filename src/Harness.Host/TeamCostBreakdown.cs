using System.ComponentModel;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// ONE TEAM'S ESTIMATED EXECUTION COST, BROKEN DOWN: the team page's Cost tile, its dialog, the Activity
/// tile's executions, Agent Hours and active members, and the one line a workflow's details carry.
/// Read, never stored.
///
/// <para>
/// NOTHING HERE PRICES A RUN. The team's own figures over a period are the Teams list's row for it
/// (<see cref="TeamCostPeriod.Of"/>, over the same reached teams, so its ranks and colours are the list's);
/// every breakdown - by member, by workflow - is one <see cref="TeamCostPeriod.Tally"/> per group over the
/// period, each run's part inside it priced from its own snapshot by <see cref="ExecutionCost"/>, the tally
/// <c>GET /api/spend/overview</c> makes. Each run's part inside the period is priced once, the same in the
/// team's total and in its group, and every breakdown splits the team's runs with none left over and none twice -
/// so each breakdown's rows add up to the team's total exactly. Only the BUCKETS are priced apart (a run crossing a
/// bucket's edge is priced per bucket), so a chart's bars can add up to a cent away from the total, as the weekly
/// reads already say of a run crossing a Monday.
/// </para>
///
/// <para>
/// AN EXECUTION IS ONE RUN THAT STARTED: one <c>usage_ledger</c> row with a start whose span meets the period
/// (<see cref="ExecutionCost.Run.Overlaps"/>), counted once in every period it touches. A member is active in
/// the period when it has an execution in it.
/// </para>
/// </summary>
public static class TeamCostBreakdown
{
    /// <summary>The team's figures over the period, with the tokens of the runs that ended in it and its executions.</summary>
    public sealed record Totals(
        double AgentSeconds, decimal? Cost, decimal? AgentRate, IReadOnlyList<string> LegacyRates,
        long Billable, int MeasuredRuns, int UnmeasuredRuns, int Executions, int ActiveMembers);

    /// <summary>One group's figures: agent seconds, the money (null - never 0 - when none was priced), the
    /// weighted agent rate, the footnote, executions, and the tokens of its runs that ended in the period.</summary>
    public sealed record Figures(
        double AgentSeconds, decimal? Cost, decimal? AgentRate, IReadOnlyList<string> LegacyRates, int Executions,
        long Billable, int MeasuredRuns, int UnmeasuredRuns);

    /// <summary>One outcome (or Unattributed, <c>id</c> null) the team's runs served: the list row's entry,
    /// with its budget as a number and its all-team, all-time total - the figures its budget is read against.</summary>
    public sealed record OutcomeEntry(
        string? Id, string Name, int? Rank, TeamCostPeriod.Amount Totals, IReadOnlyList<TeamCostPeriod.Bucket> Buckets,
        decimal? Value, decimal? OutcomeTotalCost, Figures Figures);

    /// <summary>
    /// One member's runs in the period, by the kind its snapshot names: <c>agent</c> or <c>plugin</c>.
    /// <paramref name="Unpriced"/> is written for a PLUGIN member whose period has no money only, and says
    /// why: <see cref="NotPriced"/> when one of its runs started with no rate of its own (a plugin is never
    /// given the agent default), <see cref="Unavailable"/> when every run is from before snapshots (never
    /// reconstructed from the agent rate). Absent - not null - on every other entry, so an agent member's
    /// entry is as it was.
    /// </summary>
    public sealed record MemberEntry(
        string Member, string Kind, Figures Figures,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Unpriced = null);

    /// <summary>A plugin member's unpriced period: a run started with no rate of its own.</summary>
    public const string NotPriced = "not-priced";

    /// <summary>A plugin member's unpriced period: every run is from before snapshots.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Why a member's runs in the period have no money, for a plugin member only: see <see cref="MemberEntry"/>.</summary>
    public static string? UnpricedOf(string kind, IReadOnlyList<ExecutionCost.Run> runs, Figures figures) =>
        kind != MemberRef.PluginKind || figures.Cost is not null ? null
        : runs.Any(r => r.Snapshotted) ? NotPriced
        : Unavailable;

    /// <summary>One workflow's runs, by its number (its correlation).</summary>
    public sealed record WorkflowEntry(long Workflow, Figures Figures);

    public sealed record Answer(
        string Team, TeamCost.MoneyShape Money, TeamCostPeriod.PeriodShape Period, IReadOnlyList<string> Buckets,
        Totals Totals, IReadOnlyList<TeamCostPeriod.Bucket> TotalBuckets, IReadOnlyList<OutcomeEntry> Outcomes,
        IReadOnlyList<MemberEntry> Members, IReadOnlyList<WorkflowEntry> Workflows);

    public sealed record WorkflowsAnswer(string Team, TeamCost.MoneyShape Money, IReadOnlyList<WorkflowEntry> Workflows);

    /// <summary>Whether a run executed: it has a start. A row with none never ran and is no execution.</summary>
    public static bool Executed(ExecutionCost.Run run) => run.StartedAt is not null;

    /// <summary>
    /// <paramref name="runs"/> over <c>[from, to)</c> - a null window is all time, each run whole - as one
    /// group's figures: one tally of their parts inside it, their tokens when they ended in it.
    /// </summary>
    public static Figures FiguresOf(IReadOnlyCollection<ExecutionCost.Run> runs, string currency, DateTimeOffset? from, DateTimeOffset? to)
    {
        var tally = new ExecutionCost.Tally(currency);
        foreach (var run in runs)
        {
            tally.AddSpan(run, run.SecondsIn(from, to));
            if ((from is null || run.EndedAt >= from) && (to is null || run.EndedAt < to)) tally.AddEnd(run);
        }

        return new Figures(
            tally.AgentSeconds, tally.Cost, tally.AgentRate, tally.LegacyRates, runs.Count(Executed),
            tally.Billable, tally.MeasuredRuns, tally.UnmeasuredRuns);
    }

    /// <summary>
    /// THE ANSWER for <paramref name="team"/>: its entry of <paramref name="list"/> (the Teams list's read over
    /// the same period and reached teams) and its runs of <paramref name="byTeam"/>, broken down.
    /// </summary>
    public static Answer Of(
        string team, TeamCostPeriod.Answer list, IReadOnlyList<ExecutionCost.Run> mine, IReadOnlyList<Outcome> outcomes,
        IReadOnlyDictionary<string, decimal> outcomeTotalCost, DateTimeOffset from, DateTimeOffset to)
    {
        var currency = list.Money.Currency;
        var entry = list.Teams.Single(t => string.Equals(t.Team, team, StringComparison.OrdinalIgnoreCase));
        var byId = outcomes.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var whole = FiguresOf(mine, currency, from, to);

        var outcomeEntries = entry.Outcomes.Select(o =>
        {
            var served = mine.Where(r => r.OutcomeId == o.Id).ToList();
            var outcome = o.Id is null ? null : byId.GetValueOrDefault(o.Id);
            return new OutcomeEntry(
                o.Id, o.Name, o.Rank, o.Totals, o.Buckets,
                TeamCost.Reading.BudgetOf(outcome),
                o.Id is not null && outcomeTotalCost.TryGetValue(o.Id, out var total) ? total : null,
                FiguresOf(served, currency, from, to));
        }).ToList();

        // AGENT MEMBERS FIRST, then plugin members, each by name: one entry per member and kind.
        var members = mine
            .GroupBy(r => (r.Ledger.Member, Kind: r.IsAgent ? MemberRef.AgentKind : MemberRef.PluginKind))
            .OrderBy(g => g.Key.Kind == MemberRef.AgentKind ? 0 : 1)
            .ThenBy(g => g.Key.Member, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var runs = g.ToList();
                var figures = FiguresOf(runs, currency, from, to);
                return new MemberEntry(g.Key.Member, g.Key.Kind, figures, UnpricedOf(g.Key.Kind, runs, figures));
            })
            .ToList();

        var active = mine.Where(Executed).Select(r => r.Ledger.Member).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        return new Answer(
            entry.Team, list.Money, list.Period, list.Buckets,
            new Totals(
                entry.Totals.AgentSeconds, entry.Totals.Cost, entry.Totals.AgentRate, entry.Totals.LegacyRates,
                whole.Billable, whole.MeasuredRuns, whole.UnmeasuredRuns, whole.Executions, active),
            entry.Buckets, outcomeEntries, members, WorkflowsOf(mine, currency, from, to));
    }

    /// <summary>Each workflow of <paramref name="runs"/>, newest first, over the window (null: all time).</summary>
    public static IReadOnlyList<WorkflowEntry> WorkflowsOf(
        IEnumerable<ExecutionCost.Run> runs, string currency, DateTimeOffset? from, DateTimeOffset? to) =>
        runs.GroupBy(r => r.Ledger.Correlation)
            .OrderByDescending(g => g.Key)
            .Select(g => new WorkflowEntry(g.Key, FiguresOf(g.ToList(), currency, from, to)))
            .ToList();

    public static void Map(WebApplication app)
    {
        // ONE TEAM OVER A PERIOD, for its page. `{team}` is gated as every team route is: a machine principal
        // reaches only its own team's figures.
        app.MapGet("/api/teams/{team}/cost/period", async (
                [Description(Describe.Team)] string team,
                [Description("The period's first day, YYYY-MM-DD (midnight UTC). Sent with `to`; neither sent is the previous and the current UTC month.")] string? from,
                [Description("The day after the period's last, YYYY-MM-DD (midnight UTC, exclusive).")] string? to,
                [Description("`day` (the default) or `month`.")] string? bucket,
                HttpContext context, TeamRegistry teams, TeamAccess access, IOutcomeStore outcomes,
                TenantSettings settings, CancellationToken ct) =>
            {
                if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
                if (teams.ExistingName(team) is not { } stored) return Refusals.NoTeam(team);

                var by = string.IsNullOrWhiteSpace(bucket) ? TeamCostPeriod.Day : bucket.Trim();
                if (by is not (TeamCostPeriod.Day or TeamCostPeriod.Month)) return Refused(TeamCostPeriod.BucketRefused);
                if (TeamCostPeriod.PeriodOf(from, to, DateTimeOffset.UtcNow) is not { } period) return Refused(TeamCostPeriod.PeriodRefused);
                if (TeamCostPeriod.BucketsOf(period.From, period.To, by) is null) return Refused(TeamCostPeriod.TooLongRefused);

                // THE SAME REACHED TEAMS the Teams list reads, so this team's entry - and its outcomes' ranks - are its row's.
                var effective = await access.EffectiveTeamsAsync(principal, ct);
                var reached = teams.All().Where(t => effective.Contains(t.Id)).Select(t => t.Id).ToList();
                if (!reached.Contains(stored, StringComparer.OrdinalIgnoreCase)) return Refusals.NoTeam(team);
                var floors = reached.ToDictionary(t => t, teams.FloorFor, StringComparer.OrdinalIgnoreCase);

                var all = await outcomes.ListAsync(ct);
                var pricing = OutcomeFigures.Pricing.Of(settings);
                var money = new TeamCost.MoneyShape(pricing.Currency, pricing.AgentHourlyRate > 0 ? pricing.AgentHourlyRate : null);
                var length = period.To - period.From;

                var runs = ExecutionCost.Of(
                    await outcomes.ReadExecutionRunsAsync(period.From - length, period.To, pricedOnly: false, ct), all, pricing.Currency);
                var priced = ExecutionCost.Of(await outcomes.ReadExecutionRunsAsync(null, null, pricedOnly: true, ct), all, pricing.Currency);
                var totals = ExecutionCost.CostsOf(priced, null, null, pricing.Currency);

                var list = TeamCostPeriod.Of(runs, all, totals.ByOutcome, money, reached, floors, period.From, period.To, by);
                var mine = TeamCost.RunsByTeam(runs, floors, period.From, period.To).GetValueOrDefault(stored) ?? [];

                return Results.Ok(Of(stored, list, mine, all, totals.ByOutcome, period.From, period.To));
            })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("One team's Estimated Execution Cost and Agent Hours over a period, by outcome, member and workflow")
            .WithDescription(
                "The team page's period: `?from=YYYY-MM-DD&to=YYYY-MM-DD` and `?bucket=day|month`, read and refused exactly "
                + "as `GET /api/teams/cost/period` reads and refuses them. 404 for an unknown team.\n\n"
                + "`team`, `money`, `period` and `buckets` as there. `totals`: `{ agentSeconds, cost, agentRate, legacyRates }` - "
                + "this team's entry of `GET /api/teams/cost/period` for the same period - with `billable`, `measuredRuns` "
                + "and `unmeasuredRuns` (the tokens of the runs that ended in the period, a run that reported none counted "
                + "in `unmeasuredRuns`), `executions` (its runs that started and whose span meets the period) and "
                + "`activeMembers` (the members with one). `totalBuckets`: that entry's `buckets`.\n\n"
                + "`outcomes`: that entry's `outcomes` (Unattributed first, `id` null), each with `value` (its budget as a "
                + "number, null when none), `outcomeTotalCost` (all teams, all time) and `figures`. `members`: agent members "
                + "first, then plugin members, each by name, `{ member, kind, figures }`; a plugin member's `agentSeconds` "
                + "is 0 and its `cost` null when it has no rate of its own (not priced). A plugin member with no money in the "
                + "period also has `unpriced`: `not-priced` when a run started with no rate of its own, `unavailable` when "
                + "every run is from before snapshots; no other entry has the field. `workflows`: newest first, "
                + "`{ workflow, figures }`. Each `figures` is `{ agentSeconds, cost, agentRate, legacyRates, executions, "
                + "billable, measuredRuns, unmeasuredRuns }` for that group over the period.\n\n"
                + "Every figure is one tally over its span, each run's part inside it priced from its own snapshot as "
                + "`GET /api/spend/overview` prices its Estimated Execution Cost. Each run's part inside the period is priced "
                + "once, so the rows of each breakdown add up to `totals` exactly; a run crossing a bucket's edge is priced "
                + "per bucket, so `totalBuckets` can add up to a cent away from it. "
                + "Money is in the instance currency only and never converted. It is an estimate, never money paid.");

        // EACH WORKFLOW OF A TEAM, ALL TIME: the one line the Workflows dialog's rows and a card's details carry.
        app.MapGet("/api/teams/{team}/cost/workflows", async (
                [Description(Describe.Team)] string team,
                [Description("One workflow's number (its correlation); omitted, every workflow the team has run.")] long? workflow,
                TeamRegistry teams, IOutcomeStore outcomes, TenantSettings settings, CancellationToken ct) =>
            {
                if (teams.ExistingName(team) is not { } stored) return Refusals.NoTeam(team);

                var all = await outcomes.ListAsync(ct);
                var pricing = OutcomeFigures.Pricing.Of(settings);
                var money = new TeamCost.MoneyShape(pricing.Currency, pricing.AgentHourlyRate > 0 ? pricing.AgentHourlyRate : null);
                var floors = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [stored] = teams.FloorFor(stored) };

                var runs = ExecutionCost.Of(await outcomes.ReadExecutionRunsAsync(null, null, pricedOnly: false, ct), all, pricing.Currency);
                var mine = (TeamCost.RunsByTeam(runs, floors, DateTimeOffset.MinValue, DateTimeOffset.MaxValue).GetValueOrDefault(stored) ?? [])
                    .Where(r => workflow is null || r.Ledger.Correlation == workflow);

                return Results.Ok(new WorkflowsAnswer(stored, money, WorkflowsOf(mine, pricing.Currency, null, null)));
            })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("Each workflow's Agent Hours and Estimated Execution Cost, all time")
            .WithDescription(
                "`?workflow=N` for one workflow (its number); omitted, every workflow the team has run since its floor, "
                + "newest first. 404 for an unknown team. `team`, `money`, and `workflows`: `{ workflow, figures }`, "
                + "`figures` as `GET /api/teams/{team}/cost/period` gives them, over every run of the workflow on this team, "
                + "each run whole and priced from its own snapshot. A workflow with no runs is absent. It is an estimate, "
                + "never money paid.");
    }

    private static IResult Refused(string sentence) => Results.Json(new { error = sentence }, statusCode: 400);
}
