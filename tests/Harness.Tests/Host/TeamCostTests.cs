using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// WHAT A TEAM COST, BY OUTCOME: <c>GET /api/teams/{team}/cost</c> and <c>GET /api/teams/cost</c>.
/// A team's totals are its own runs; an outcome's total cost is every team's work on it, the figure
/// its budget is measured against; unlinked work is No outcome, first; no rate prices nothing, never
/// 0; an unmeasured run is counted, never zero tokens; and the all-teams read answers what each
/// team's own read does.
/// </summary>
public sealed class TeamCostTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Email = "person@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IOutcomeStore Outcomes => host.Services.GetRequiredService<IOutcomeStore>();

    private string Database => Path.Combine(host.DataRoot, "messages.db");

    [Fact]
    public async Task An_outcome_shared_by_two_teams_measures_its_total_across_both_while_each_team_totals_its_own()
    {
        var person = await host.PersonAsync();
        var alpha = await NewTeamAsync("Cost alpha");
        var beta = await NewTeamAsync("Cost beta");
        var shared = await CreateAsync(person, "Shared", value: "500");

        var now = DateTimeOffset.UtcNow;
        var onAlpha = await LinkedAsync(shared.Id, alpha);
        var onBeta = await LinkedAsync(shared.Id, beta);
        await RunAsync(onAlpha, alpha, now.AddHours(-2), now.AddHours(-1), billable: 400);
        await RunAsync(onBeta, beta, now.AddHours(-3), now.AddHours(-1), billable: 100);
        // OUTSIDE THE EIGHT WEEKS: in the outcome's total, never in the team's.
        await RunAsync(onAlpha, alpha, now.AddDays(-70).AddHours(-1), now.AddDays(-70), billable: 1000);

        try
        {
            await SetOkAsync(person, TenantSettings.OutcomesAgentHourlyRateName, 60);

            var alphaCost = await CostAsync(person, alpha);
            var betaCost = await CostAsync(person, beta);

            Assert.Equal(60, alphaCost.GetProperty("money").GetProperty("agentHourlyRate").GetInt32());
            Assert.Equal("USD", alphaCost.GetProperty("money").GetProperty("currency").GetString());

            var alphaShared = Single(alphaCost);
            Assert.Equal(shared.Id, alphaShared.GetProperty("id").GetString());
            Assert.Equal(shared.Name, alphaShared.GetProperty("name").GetString());
            Assert.Equal(500m, alphaShared.GetProperty("value").GetDecimal());
            Assert.Equal(3600, alphaShared.GetProperty("totals").GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(400, alphaShared.GetProperty("totals").GetProperty("billable").GetInt64());
            Assert.Equal(60m, alphaShared.GetProperty("totals").GetProperty("cost").GetDecimal());
            Assert.Equal(240m, alphaShared.GetProperty("outcomeTotalCost").GetDecimal());
            Assert.Equal(60m, alphaCost.GetProperty("totals").GetProperty("cost").GetDecimal());

            var betaShared = Single(betaCost);
            Assert.Equal(120m, betaShared.GetProperty("totals").GetProperty("cost").GetDecimal());
            Assert.Equal(240m, betaShared.GetProperty("outcomeTotalCost").GetDecimal());

            // THE SAME FIGURE THE OUTCOMES DASHBOARD GIVES AS THE OUTCOME'S COST.
            var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
            var figures = list.GetProperty("outcomes").EnumerateArray()
                .Single(o => o.GetProperty("id").GetString() == shared.Id).GetProperty("figures");
            Assert.Equal(figures.GetProperty("cost").GetProperty("amount").GetDecimal(), alphaShared.GetProperty("outcomeTotalCost").GetDecimal());

            // EIGHT MONDAY-START WEEKS, OLDEST FIRST, THE SAME AS AN OUTCOME'S WEEKLY.
            var weeks = alphaCost.GetProperty("weeks").EnumerateArray().Select(w => w.GetDateTimeOffset()).ToList();
            Assert.Equal(figures.GetProperty("weekly").EnumerateArray().Select(w => w.GetProperty("weekStart").GetDateTimeOffset()), weeks);
            Assert.Equal(OutcomeFigures.WeekOf(DateTimeOffset.UtcNow), weeks[^1]);
            var weekly = alphaShared.GetProperty("weekly").EnumerateArray().ToList();
            Assert.Equal(weeks, weekly.Select(w => w.GetProperty("weekStart").GetDateTimeOffset()));
            Assert.Equal(60m, weekly.Sum(w => w.GetProperty("cost").GetDecimal()));
            Assert.Equal(400, weekly.Sum(w => w.GetProperty("billable").GetInt64()));
        }
        finally
        {
            await SetAsync(person, TenantSettings.OutcomesAgentHourlyRateName, null);
        }
    }

    [Fact]
    public async Task Unlinked_work_is_a_No_outcome_entry_listed_first_and_outcomes_follow_in_the_order_they_were_created()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Cost unlinked");
        var older = await CreateAsync(person, "Older");
        var newer = await CreateAsync(person, "Newer");

        var now = DateTimeOffset.UtcNow;
        // Run on the newer outcome first, so the order is not the order work arrived in.
        await RunAsync(await LinkedAsync(newer.Id, team), team, now.AddHours(-5), now.AddHours(-4));
        await RunAsync(await LinkedAsync(older.Id, team), team, now.AddHours(-3), now.AddHours(-2));

        var never = NextCorrelation();
        await RunAsync(never, team, now.AddHours(-2), now.AddHours(-1));
        var unlinked = await LinkedAsync(older.Id, team);
        Assert.True((await Outcomes.UnlinkAsync(unlinked, team, new OutcomeActor(Email, OutcomeActorKind.Person),
            new TriggerAudit(null, Email, TenantActions.WorkflowOutcomeChanged, null, null, null), Ct)).Ok);
        await RunAsync(unlinked, team, now.AddHours(-1), now.AddMinutes(-30));

        var outcomes = (await CostAsync(person, team)).GetProperty("outcomes").EnumerateArray().ToList();

        Assert.Equal([null, older.Id, newer.Id], outcomes.Select(o => o.GetProperty("id").GetString()));
        var none = outcomes[0];
        Assert.Equal("No outcome", none.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("outcomeTotalCost").ValueKind);
        Assert.Equal(5400, none.GetProperty("totals").GetProperty("agentSeconds").GetDouble(), 3);
        Assert.Equal(2, none.GetProperty("totals").GetProperty("measuredRuns").GetInt32());

        // ONLY WHEN THERE IS UNLINKED WORK.
        var linkedOnly = await NewTeamAsync("Cost linked only");
        await RunAsync(await LinkedAsync(older.Id, linkedOnly), linkedOnly, now.AddHours(-2), now.AddHours(-1));
        Assert.Equal([older.Id], (await CostAsync(person, linkedOnly)).GetProperty("outcomes").EnumerateArray()
            .Select(o => o.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task No_rate_prices_nothing_anywhere_and_never_answers_zero()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Cost unpriced");
        var outcome = await CreateAsync(person, "Unpriced");
        var now = DateTimeOffset.UtcNow;
        await RunAsync(await LinkedAsync(outcome.Id, team), team, now.AddHours(-2), now.AddHours(-1));
        await RunAsync(NextCorrelation(), team, now.AddHours(-2), now.AddHours(-1));

        foreach (var read in new[] { await CostAsync(person, team), await AllTeamAsync(person, team, withMoney: true) })
        {
            Assert.Equal(JsonValueKind.Null, read.GetProperty("money").GetProperty("agentHourlyRate").ValueKind);
            Assert.Equal(JsonValueKind.Null, read.GetProperty("totals").GetProperty("cost").ValueKind);

            var entries = read.GetProperty("outcomes").EnumerateArray().ToList();
            Assert.Equal(2, entries.Count);
            foreach (var entry in entries)
            {
                Assert.Equal(JsonValueKind.Null, entry.GetProperty("outcomeTotalCost").ValueKind);
                Assert.Equal(JsonValueKind.Null, entry.GetProperty("totals").GetProperty("cost").ValueKind);
                Assert.All(entry.GetProperty("weekly").EnumerateArray(),
                    w => Assert.Equal(JsonValueKind.Null, w.GetProperty("cost").ValueKind));
            }

            Assert.Equal(7200, read.GetProperty("totals").GetProperty("agentSeconds").GetDouble(), 3);
        }
    }

    [Fact]
    public async Task An_unmeasured_run_is_counted_and_adds_no_tokens()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Cost unmeasured");
        var outcome = await CreateAsync(person, "Measured or not");
        var workflow = await LinkedAsync(outcome.Id, team);
        var now = DateTimeOffset.UtcNow;
        await RunAsync(workflow, team, now.AddHours(-3), now.AddHours(-2), billable: 250);
        await RunAsync(workflow, team, now.AddHours(-2), now.AddHours(-1), measured: false);

        var read = await CostAsync(person, team);
        var totals = Single(read).GetProperty("totals");
        Assert.Equal(250, totals.GetProperty("billable").GetInt64());
        Assert.Equal(1, totals.GetProperty("measuredRuns").GetInt32());
        Assert.Equal(1, totals.GetProperty("unmeasuredRuns").GetInt32());
        // Its agent time is on the ledger, so it counts.
        Assert.Equal(7200, totals.GetProperty("agentSeconds").GetDouble(), 3);

        var weekly = Single(read).GetProperty("weekly").EnumerateArray().ToList();
        Assert.Equal(1, weekly.Sum(w => w.GetProperty("unmeasuredRuns").GetInt32()));
        Assert.Equal(1, read.GetProperty("totals").GetProperty("unmeasuredRuns").GetInt32());
    }

    [Fact]
    public async Task The_all_teams_read_answers_what_each_teams_own_read_does()
    {
        var person = await host.PersonAsync();
        var alpha = await NewTeamAsync("Cost all a");
        var beta = await NewTeamAsync("Cost all b");
        var quiet = await NewTeamAsync("Cost all quiet");
        var shared = await CreateAsync(person, "Both", value: "1000");
        var own = await CreateAsync(person, "Alpha's own");
        var now = DateTimeOffset.UtcNow;

        await RunAsync(await LinkedAsync(shared.Id, alpha), alpha, now.AddDays(-20).AddHours(-1), now.AddDays(-20), billable: 30);
        await RunAsync(await LinkedAsync(own.Id, alpha), alpha, now.AddHours(-4), now.AddHours(-1), billable: 40);
        await RunAsync(NextCorrelation(), alpha, now.AddHours(-2), now.AddHours(-1), measured: false);
        await RunAsync(await LinkedAsync(shared.Id, beta), beta, now.AddDays(-9).AddHours(-2), now.AddDays(-9), billable: 50);

        try
        {
            await SetOkAsync(person, TenantSettings.OutcomesAgentHourlyRateName, 24);

            var all = await JsonAsync(await person.GetAsync("/api/teams/cost", Ct));
            foreach (var team in new[] { alpha, beta, quiet })
            {
                var one = await CostAsync(person, team);
                var entry = all.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == team);

                Assert.Equal(one.GetProperty("team").GetString(), entry.GetProperty("team").GetString());
                Assert.Equal(one.GetProperty("outcomes").GetRawText(), entry.GetProperty("outcomes").GetRawText());
                Assert.Equal(one.GetProperty("totals").GetRawText(), entry.GetProperty("totals").GetRawText());
                Assert.Equal(one.GetProperty("weeks").GetRawText(), all.GetProperty("weeks").GetRawText());
                Assert.Equal(one.GetProperty("money").GetRawText(), all.GetProperty("money").GetRawText());
            }

            var quietEntry = all.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == quiet);
            Assert.Empty(quietEntry.GetProperty("outcomes").EnumerateArray());
            Assert.Equal(0m, quietEntry.GetProperty("totals").GetProperty("cost").GetDecimal());

            var alphaOutcomes = all.GetProperty("teams").EnumerateArray()
                .Single(t => t.GetProperty("team").GetString() == alpha).GetProperty("outcomes").EnumerateArray().ToList();
            Assert.Equal([null, shared.Id, own.Id], alphaOutcomes.Select(o => o.GetProperty("id").GetString()));
            Assert.Equal(72m, alphaOutcomes[2].GetProperty("totals").GetProperty("cost").GetDecimal());
            Assert.Equal(72m, alphaOutcomes[1].GetProperty("outcomeTotalCost").GetDecimal());
        }
        finally
        {
            await SetAsync(person, TenantSettings.OutcomesAgentHourlyRateName, null);
        }
    }

    [Fact]
    public async Task An_outcome_has_one_rank_on_every_team_whatever_else_each_team_lists()
    {
        var person = await host.PersonAsync();
        var wide = await NewTeamAsync("Rank wide");
        var narrow = await NewTeamAsync("Rank narrow");
        var first = await CreateAsync(person, "Rank A");
        var second = await CreateAsync(person, "Rank B");
        var third = await CreateAsync(person, "Rank C");

        var now = DateTimeOffset.UtcNow;
        foreach (var outcome in new[] { first, second, third })
        {
            await RunAsync(await LinkedAsync(outcome.Id, wide), wide, now.AddHours(-2), now.AddHours(-1));
        }
        await RunAsync(await LinkedAsync(third.Id, narrow), narrow, now.AddHours(-2), now.AddHours(-1));
        await RunAsync(NextCorrelation(), narrow, now.AddHours(-2), now.AddHours(-1));

        static Dictionary<string, int?> Ranks(JsonElement outcomes) => outcomes.EnumerateArray().ToDictionary(
            o => o.GetProperty("id").GetString() ?? "none",
            o => o.GetProperty("rank").ValueKind == JsonValueKind.Null ? (int?)null : o.GetProperty("rank").GetInt32());

        var wideRanks = Ranks((await CostAsync(person, wide)).GetProperty("outcomes"));
        var narrowRanks = Ranks((await CostAsync(person, narrow)).GetProperty("outcomes"));

        // THIRD ON ONE TEAM, THE ONLY OUTCOME ON THE OTHER: THE SAME RANK ON BOTH, IN CREATION ORDER.
        Assert.True(wideRanks[first.Id] < wideRanks[second.Id]);
        Assert.True(wideRanks[second.Id] < wideRanks[third.Id]);
        Assert.Equal(wideRanks[third.Id], narrowRanks[third.Id]);
        Assert.Null(narrowRanks["none"]);

        // THE ALL-TEAMS READ CARRIES THE SAME RANKS.
        var all = await JsonAsync(await person.GetAsync("/api/teams/cost", Ct));
        var allNarrow = Ranks(all.GetProperty("teams").EnumerateArray()
            .Single(t => t.GetProperty("team").GetString() == narrow).GetProperty("outcomes"));
        Assert.Equal(narrowRanks[third.Id], allNarrow[third.Id]);
    }

    [Fact]
    public async Task An_unknown_team_is_not_found()
    {
        var person = await host.PersonAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await person.GetAsync("/api/teams/no-such-team/cost", Ct)).StatusCode);
    }

    // ---- the teams list's summary ----

    [Fact]
    public async Task The_summary_sums_eight_weeks_and_the_eight_before_them_from_the_ledger()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Summary prior");
        var now = DateTimeOffset.UtcNow;

        try
        {
            await SetOkAsync(person, TenantSettings.OutcomesAgentHourlyRateName, 30);
            var before = await SummaryAsync(person);

            await RunAsync(NextCorrelation(), team, now.AddHours(-2), now.AddHours(-1));
            // THE EIGHT WEEKS BEFORE: in prior, never in total.
            await RunAsync(NextCorrelation(), team, now.AddDays(-80).AddHours(-2), now.AddDays(-80));
            // OLDER STILL: in neither.
            await RunAsync(NextCorrelation(), team, now.AddDays(-150).AddHours(-4), now.AddDays(-150));

            var after = await SummaryAsync(person);

            Assert.Equal(30, after.GetProperty("rate").GetInt32());
            Assert.Equal(3600, Seconds(after, "total") - Seconds(before, "total"), 3);
            Assert.Equal(7200, Seconds(after, "prior") - Seconds(before, "prior"), 3);
            Assert.Equal(30m, Cost(after, "total") - Cost(before, "total"));
            Assert.Equal(60m, Cost(after, "prior") - Cost(before, "prior"));

            // EIGHT MONDAY-START WEEKS, OLDEST FIRST, THE CURRENT ONE LAST, SUMMING TO THE TOTAL.
            var weeks = after.GetProperty("weeks").EnumerateArray().ToList();
            Assert.Equal(8, weeks.Count);
            var starts = OutcomeFigures.WeekStarts(now).Select(Day).ToList();
            Assert.Equal(starts, weeks.Select(w => w.GetProperty("weekStart").GetString()));
            Assert.Equal(Day(OutcomeFigures.WeekOf(now)), weeks[^1].GetProperty("weekStart").GetString());
            Assert.Equal(Seconds(after, "total"), weeks.Sum(w => w.GetProperty("agentSeconds").GetDouble()), 3);
            Assert.Equal(starts[0], after.GetProperty("periodStart").GetString());
            Assert.Equal(Day(DateTimeOffset.UtcNow), after.GetProperty("periodEnd").GetString());

            // THE SAME RUNS THE PER-TEAM FIGURES ARE: the total is the teams' totals summed.
            var all = await JsonAsync(await person.GetAsync("/api/teams/cost", Ct));
            Assert.Equal(
                all.GetProperty("teams").EnumerateArray().Sum(t => t.GetProperty("totals").GetProperty("agentSeconds").GetDouble()),
                Seconds(after, "total"), 3);
        }
        finally
        {
            await SetAsync(person, TenantSettings.OutcomesAgentHourlyRateName, null);
        }
    }

    [Fact]
    public async Task The_summary_measures_a_budget_shared_across_teams_against_its_total_and_keeps_an_unspent_budget()
    {
        var person = await host.PersonAsync();
        var alpha = await NewTeamAsync("Summary share a");
        var beta = await NewTeamAsync("Summary share b");
        var shared = await CreateAsync(person, "Summary shared", value: "500");
        var unspent = await CreateAsync(person, "Summary unspent", value: "75");
        var now = DateTimeOffset.UtcNow;

        await RunAsync(await LinkedAsync(shared.Id, alpha), alpha, now.AddHours(-2), now.AddHours(-1));
        await RunAsync(await LinkedAsync(shared.Id, beta), beta, now.AddHours(-3), now.AddHours(-1));
        // OUTSIDE THE EIGHT WEEKS: in what the budget is measured against, never in the weeks.
        await RunAsync(await LinkedAsync(shared.Id, alpha), alpha, now.AddDays(-70).AddHours(-1), now.AddDays(-70));

        try
        {
            await SetOkAsync(person, TenantSettings.OutcomesAgentHourlyRateName, 60);
            var summary = await SummaryAsync(person);

            var entry = OutcomeOf(summary, shared.Id);
            Assert.Equal(shared.Name, entry.GetProperty("name").GetString());
            Assert.Equal(10800, entry.GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(180m, entry.GetProperty("cost").GetDecimal());
            Assert.Equal(500m, entry.GetProperty("budget").GetDecimal());
            Assert.Equal(240m, entry.GetProperty("spentTotal").GetDecimal());

            // THE SAME FIGURES COST 1 GIVES: the outcomes dashboard's cost, and the all-teams read's
            // total cost and rank.
            var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
            var figures = list.GetProperty("outcomes").EnumerateArray()
                .Single(o => o.GetProperty("id").GetString() == shared.Id).GetProperty("figures");
            Assert.Equal(figures.GetProperty("cost").GetProperty("amount").GetDecimal(), entry.GetProperty("spentTotal").GetDecimal());
            var perTeam = (await CostAsync(person, alpha)).GetProperty("outcomes").EnumerateArray()
                .Single(o => o.GetProperty("id").GetString() == shared.Id);
            Assert.Equal(perTeam.GetProperty("outcomeTotalCost").GetDecimal(), entry.GetProperty("spentTotal").GetDecimal());
            Assert.Equal(perTeam.GetProperty("rank").GetInt32(), entry.GetProperty("rank").GetInt32());

            // A BUDGET WITH NO SPEND IN THE WEEKS IS STILL LISTED, at nothing.
            var idle = OutcomeOf(summary, unspent.Id);
            Assert.Equal(0, idle.GetProperty("agentSeconds").GetDouble());
            Assert.Equal(0m, idle.GetProperty("cost").GetDecimal());
            Assert.Equal(75m, idle.GetProperty("budget").GetDecimal());
            Assert.Equal(0m, idle.GetProperty("spentTotal").GetDecimal());
            Assert.Equal(JsonValueKind.Null, idle.GetProperty("rank").ValueKind);
        }
        finally
        {
            await SetAsync(person, TenantSettings.OutcomesAgentHourlyRateName, null);
        }
    }

    [Fact]
    public async Task The_summary_puts_unlinked_work_under_No_outcome_first()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Summary unlinked");
        var outcome = await CreateAsync(person, "Summary linked");
        var now = DateTimeOffset.UtcNow;

        // SOME UNLINKED WORK, so No outcome is there before as well as after.
        await RunAsync(NextCorrelation(), team, now.AddHours(-1), now.AddMinutes(-30));
        var before = NoOutcome(await SummaryAsync(person)).GetProperty("agentSeconds").GetDouble();

        await RunAsync(NextCorrelation(), team, now.AddHours(-2), now.AddHours(-1));
        var unlinked = await LinkedAsync(outcome.Id, team);
        Assert.True((await Outcomes.UnlinkAsync(unlinked, team, new OutcomeActor(Email, OutcomeActorKind.Person),
            new TriggerAudit(null, Email, TenantActions.WorkflowOutcomeChanged, null, null, null), Ct)).Ok);
        await RunAsync(unlinked, team, now.AddHours(-1), now.AddMinutes(-30));
        await RunAsync(await LinkedAsync(outcome.Id, team), team, now.AddHours(-1), now.AddMinutes(-30));

        var summary = await SummaryAsync(person);
        var none = NoOutcome(summary);
        Assert.Equal(none.GetRawText(), summary.GetProperty("outcomes").EnumerateArray().First().GetRawText());
        Assert.Equal("No outcome", none.GetProperty("name").GetString());
        Assert.Equal(5400, none.GetProperty("agentSeconds").GetDouble() - before, 3);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("rank").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("budget").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("spentTotal").ValueKind);
        Assert.Equal(1800, OutcomeOf(summary, outcome.Id).GetProperty("agentSeconds").GetDouble(), 3);
    }

    [Fact]
    public async Task The_summary_with_no_rate_prices_nothing_and_never_answers_zero()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Summary unpriced");
        var outcome = await CreateAsync(person, "Summary unpriced", value: "100");
        var now = DateTimeOffset.UtcNow;
        await RunAsync(await LinkedAsync(outcome.Id, team), team, now.AddHours(-2), now.AddHours(-1));
        await RunAsync(NextCorrelation(), team, now.AddHours(-2), now.AddHours(-1));
        await RunAsync(NextCorrelation(), team, now.AddDays(-80).AddHours(-1), now.AddDays(-80));

        var summary = await SummaryAsync(person);

        Assert.Equal(JsonValueKind.Null, summary.GetProperty("rate").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("total").GetProperty("cost").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("prior").GetProperty("cost").ValueKind);
        Assert.True(Seconds(summary, "total") >= 7200);
        Assert.True(Seconds(summary, "prior") >= 3600);
        Assert.All(summary.GetProperty("weeks").EnumerateArray(),
            w => Assert.Equal(JsonValueKind.Null, w.GetProperty("cost").ValueKind));
        Assert.All(summary.GetProperty("outcomes").EnumerateArray(), o =>
        {
            Assert.Equal(JsonValueKind.Null, o.GetProperty("cost").ValueKind);
            Assert.Equal(JsonValueKind.Null, o.GetProperty("spentTotal").ValueKind);
        });
        Assert.Equal(100m, OutcomeOf(summary, outcome.Id).GetProperty("budget").GetDecimal());
    }

    [Fact]
    public async Task The_summary_counts_right_now_as_the_rollup_and_Needs_you_do()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Summary needs");
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        await registry.SetPausedAsync(team, true, Ct);

        var manager = host.Services.GetRequiredService<ContainerHost>().Find(new ContainerId(team, TeamRegistry.DefaultManagerName))!;
        var log = host.Services.GetRequiredService<IMessageLog>();
        var row = await log.AppendAsync(new NewMessage(
            MessageTypes.Blocked, """{"reason":"The clone is missing."}""", manager.Id.ToString()), Ct);
        manager.MarkBlocked("The clone is missing.");
        manager.NoteMarkRow(row);

        var summary = await SummaryAsync(person);
        var needsYou = await JsonAsync(await person.GetAsync("/api/needs-you", Ct));
        var rollup = await JsonAsync(await person.GetAsync("/api/teams/rollup", Ct));

        var rightNow = summary.GetProperty("rightNow");
        var waiting = rightNow.GetProperty("waitingOnYou").GetInt32();
        Assert.True(waiting >= 1);
        Assert.Equal(needsYou.GetProperty("items").GetArrayLength(), waiting);

        var rows = rollup.GetProperty("teams").EnumerateArray().ToList();
        Assert.Equal(rows.Sum(r => r.GetProperty("openWorkflows").GetProperty("openCount").GetInt32()),
            rightNow.GetProperty("openWorkflows").GetInt32());
        Assert.Equal(rows.Sum(r => r.GetProperty("running").GetInt32()), rightNow.GetProperty("membersRunning").GetInt32());
    }

    // ---- fixtures ----

    private static JsonElement Single(JsonElement read) => Assert.Single(read.GetProperty("outcomes").EnumerateArray().ToList());

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<JsonElement> SummaryAsync(HttpClient person) =>
        await JsonAsync(await person.GetAsync("/api/teams/cost/summary", Ct));

    private static double Seconds(JsonElement summary, string amount) => summary.GetProperty(amount).GetProperty("agentSeconds").GetDouble();

    private static decimal Cost(JsonElement summary, string amount) => summary.GetProperty(amount).GetProperty("cost").GetDecimal();

    private static string Day(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonElement OutcomeOf(JsonElement summary, string id) =>
        summary.GetProperty("outcomes").EnumerateArray().Single(o => o.GetProperty("id").GetString() == id);

    private static JsonElement NoOutcome(JsonElement summary) =>
        summary.GetProperty("outcomes").EnumerateArray().Single(o => o.GetProperty("id").ValueKind == JsonValueKind.Null);

    private async Task<JsonElement> CostAsync(HttpClient person, string team) =>
        await JsonAsync(await person.GetAsync($"/api/teams/{team}/cost", Ct));

    /// <summary>A team's entry from the all-teams read, with its money beside it.</summary>
    private async Task<JsonElement> AllTeamAsync(HttpClient person, string team, bool withMoney)
    {
        var all = await JsonAsync(await person.GetAsync("/api/teams/cost", Ct));
        var entry = all.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == team);
        var merged = new Dictionary<string, JsonElement>
        {
            ["outcomes"] = entry.GetProperty("outcomes"),
            ["totals"] = entry.GetProperty("totals"),
        };
        if (withMoney) merged["money"] = all.GetProperty("money");
        return JsonSerializer.SerializeToElement(merged);
    }

    private async Task<string> NewTeamAsync(string label)
    {
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        return (await host.Services.GetRequiredService<TeamRegistry>()
            .CreateAsync($"{label} {Guid.NewGuid():N}"[..24], agent, memberAgent: agent, ct: Ct)).Id;
    }

    private async Task<Outcome> CreateAsync(HttpClient person, string name, string? value = null)
    {
        var created = await person.PostAsJsonAsync("/api/outcomes", new { name = $"{name} {Guid.NewGuid():N}", value }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Outcomes.FindAsync((await JsonAsync(created)).GetProperty("id").GetString()!, Ct))!;
    }

    private async Task<long> LinkedAsync(string outcome, string team)
    {
        var correlation = NextCorrelation();
        Assert.True((await Outcomes.LinkAsync(correlation, outcome, team, new OutcomeActor(Email, OutcomeActorKind.Person),
            OutcomeLinkHow.Person, agentRule: false,
            new TriggerAudit(null, Email, TenantActions.WorkflowOutcomeChanged, null, null, null), Ct)).Ok);
        return correlation;
    }

    private Task<HttpResponseMessage> SetAsync(HttpClient person, string name, object? value) =>
        person.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object?> { [name] = value }, Ct);

    private async Task SetOkAsync(HttpClient person, string name, object value)
    {
        var response = await SetAsync(person, name, value);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static long _correlation = 8_000_000_000 + Random.Shared.Next(1_000_000);

    private static long NextCorrelation() => Interlocked.Add(ref _correlation, 1000);

    private static long _run = 9_000_000_000 + Random.Shared.Next(1_000_000);

    private Task RunAsync(
        long correlation, string team, DateTimeOffset started, DateTimeOffset ended, long billable = 10, bool measured = true) =>
        ExecuteAsync(
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, run_outcome,
                started_at, ended_at, measured, tokens_in, tokens_out, billable)
            VALUES ($seq, $c, $team, $team, 'Dev', 'completed', $started, $ended, $measured, $in, $out, $billable)
            """,
            ("$seq", Interlocked.Increment(ref _run)), ("$c", correlation), ("$team", team),
            ("$started", started.ToString("O")), ("$ended", ended.ToString("O")), ("$measured", measured ? 1 : 0),
            ("$in", measured ? billable : (object)DBNull.Value), ("$out", measured ? 0L : (object)DBNull.Value),
            ("$billable", measured ? billable : (object)DBNull.Value));

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
