using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
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
        await RunAsync(onAlpha, alpha, now.AddHours(-2), now.AddHours(-1), billable: 400, rate: "60");
        await RunAsync(onBeta, beta, now.AddHours(-3), now.AddHours(-1), billable: 100, rate: "60");
        // OUTSIDE THE EIGHT WEEKS: in the outcome's total, never in the team's.
        await RunAsync(onAlpha, alpha, now.AddDays(-70).AddHours(-1), now.AddDays(-70), billable: 1000, rate: "60");

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
            Assert.Equal(60m, alphaCost.GetProperty("totals").GetProperty("agentRate").GetDecimal());

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
            // A week with no priced run has no money, never 0.
            Assert.Equal(60m, weekly.Sum(w => w.GetProperty("cost") is { ValueKind: JsonValueKind.Number } c ? c.GetDecimal() : 0m));
            Assert.Contains(weekly, w => w.GetProperty("cost").ValueKind == JsonValueKind.Null);
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
        Assert.Equal("Unattributed", none.GetProperty("name").GetString());
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

        await RunAsync(await LinkedAsync(shared.Id, alpha), alpha, now.AddDays(-20).AddHours(-1), now.AddDays(-20), billable: 30, rate: "24");
        await RunAsync(await LinkedAsync(own.Id, alpha), alpha, now.AddHours(-4), now.AddHours(-1), billable: 40, rate: "24");
        await RunAsync(NextCorrelation(), alpha, now.AddHours(-2), now.AddHours(-1), measured: false, rate: "24");
        await RunAsync(await LinkedAsync(shared.Id, beta), beta, now.AddDays(-9).AddHours(-2), now.AddDays(-9), billable: 50, rate: "24");

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
            // NOTHING PRICED IS NO MONEY, NEVER 0.
            Assert.Equal(JsonValueKind.Null, quietEntry.GetProperty("totals").GetProperty("cost").ValueKind);

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

    // ---- priced from the snapshot ----

    [Fact]
    public async Task A_run_is_priced_from_its_snapshot_and_a_later_rate_or_link_change_does_not_move_it()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Cost snapshot");
        var first = await CreateAsync(person, "Snapshot first");
        var second = await CreateAsync(person, "Snapshot second");
        var now = DateTimeOffset.UtcNow;

        // One workflow, linked to the first outcome: a run snapshotted at 60 then, and one from before snapshots.
        var workflow = await LinkedAsync(first.Id, team);
        await RunAsync(workflow, team, now.AddHours(-3), now.AddHours(-2), rate: "60");
        await RunAsync(workflow, team, now.AddHours(-2), now.AddHours(-1));

        try
        {
            // THE TENANT RATE CHANGES AND THE WORKFLOW MOVES, BOTH AFTER THE RUNS STARTED.
            await SetOkAsync(person, TenantSettings.OutcomesAgentHourlyRateName, 90);
            await RelinkAsync(workflow, second.Id, team);

            var read = await CostAsync(person, team);
            var byId = read.GetProperty("outcomes").EnumerateArray().ToDictionary(o => o.GetProperty("id").GetString()!);

            // The snapshotted run stays on the outcome its link named at start, at the rate it started with.
            Assert.Equal(3600, byId[first.Id].GetProperty("totals").GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(60m, byId[first.Id].GetProperty("totals").GetProperty("cost").GetDecimal());
            Assert.Equal(60m, byId[first.Id].GetProperty("outcomeTotalCost").GetDecimal());

            // The run from before snapshots keeps the newest-link rule, and is time without money.
            Assert.Equal(3600, byId[second.Id].GetProperty("totals").GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(JsonValueKind.Null, byId[second.Id].GetProperty("totals").GetProperty("cost").ValueKind);
            Assert.Equal(JsonValueKind.Null, byId[second.Id].GetProperty("outcomeTotalCost").ValueKind);

            // Each run in one bucket: the team's figure is the two runs, once each, priced as they started.
            var totals = read.GetProperty("totals");
            Assert.Equal(7200, totals.GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(60m, totals.GetProperty("cost").GetDecimal());
            Assert.Equal(60m, totals.GetProperty("agentRate").GetDecimal());
            Assert.Equal(90, read.GetProperty("money").GetProperty("agentHourlyRate").GetInt32());

            // The outcomes dashboard reads the same run in the same place.
            var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
            decimal? CostOf(string id) => list.GetProperty("outcomes").EnumerateArray()
                .Single(o => o.GetProperty("id").GetString() == id).GetProperty("figures").GetProperty("cost").GetProperty("amount")
                is { ValueKind: JsonValueKind.Number } amount ? amount.GetDecimal() : null;
            Assert.Equal(60m, CostOf(first.Id));
            Assert.Null(CostOf(second.Id));

            // AND ITS TIME WITH ITS MONEY: each run's hours under the outcome it serves, once - while the
            // workflow itself, counted by its newest link, is the second outcome's.
            JsonElement FiguresOf(string id) => list.GetProperty("outcomes").EnumerateArray()
                .Single(o => o.GetProperty("id").GetString() == id).GetProperty("figures");
            Assert.Equal(3600, FiguresOf(first.Id).GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(3600, FiguresOf(second.Id).GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(0, FiguresOf(first.Id).GetProperty("workflows").GetProperty("total").GetInt32());
            Assert.Equal(1, FiguresOf(second.Id).GetProperty("workflows").GetProperty("total").GetInt32());
        }
        finally
        {
            await SetAsync(person, TenantSettings.OutcomesAgentHourlyRateName, null);
        }
    }

    [Fact]
    public async Task A_plugin_without_its_own_rate_is_not_priced_nor_Agent_Hours_and_one_with_its_own_rate_is_in_the_headline_only()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Cost plugin");
        var now = DateTimeOffset.UtcNow;

        try
        {
            // A tenant agent rate is set: no plugin is priced at it.
            await SetOkAsync(person, TenantSettings.OutcomesAgentHourlyRateName, 60);

            await RunAsync(NextCorrelation(), team, now.AddHours(-3), now.AddHours(-1), rate: "60");
            await RunAsync(NextCorrelation(), team, now.AddHours(-2), now.AddHours(-1), kind: MemberRef.PluginKind, snapshot: true);
            await RunAsync(NextCorrelation(), team, now.AddHours(-2), now.AddHours(-1), kind: MemberRef.PluginKind);
            await RunAsync(NextCorrelation(), team, now.AddHours(-2), now.AddHours(-1), kind: MemberRef.PluginKind, rate: "15");
            // An agent hour with no price: Agent Hours, and no money.
            await RunAsync(NextCorrelation(), team, now.AddHours(-2), now.AddHours(-1), snapshot: true);

            var totals = (await CostAsync(person, team)).GetProperty("totals");

            Assert.Equal(3 * 3600, totals.GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(135m, totals.GetProperty("cost").GetDecimal());
            Assert.Equal(60m, totals.GetProperty("agentRate").GetDecimal());
        }
        finally
        {
            await SetAsync(person, TenantSettings.OutcomesAgentHourlyRateName, null);
        }
    }

    /// <summary>
    /// A PRICED PLUGIN RUN SERVES ITS WORKFLOW'S OUTCOME: its money reaches the outcome's estimate across teams, on the
    /// outcomes read and on each team's own entry, and its seconds are never Agent Hours.
    /// </summary>
    [Fact]
    public async Task A_priced_plugin_run_adds_its_money_to_the_outcome_its_workflow_serves_and_never_Agent_Hours()
    {
        var person = await host.PersonAsync();
        var alpha = await NewTeamAsync("Plugin outcome a");
        var beta = await NewTeamAsync("Plugin outcome b");
        var outcome = await CreateAsync(person, "Plugin served");
        var now = DateTimeOffset.UtcNow;

        var onAlpha = await LinkedAsync(outcome.Id, alpha);
        var onBeta = await LinkedAsync(outcome.Id, beta);
        await RunAsync(onAlpha, alpha, now.AddHours(-3), now.AddHours(-2), rate: "60");
        await RunAsync(onBeta, beta, now.AddHours(-3), now.AddHours(-1), rate: "15", kind: MemberRef.PluginKind, member: "Mailer");

        var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
        var figures = list.GetProperty("outcomes").EnumerateArray()
            .Single(o => o.GetProperty("id").GetString() == outcome.Id).GetProperty("figures");
        Assert.Equal(90m, figures.GetProperty("cost").GetProperty("amount").GetDecimal());
        Assert.Equal(3600, figures.GetProperty("agentSeconds").GetDouble(), 3);
        Assert.Equal(60m, figures.GetProperty("cost").GetProperty("agentRate").GetDecimal());

        var betaOutcome = Single(await CostAsync(person, beta));
        Assert.Equal(outcome.Id, betaOutcome.GetProperty("id").GetString());
        Assert.Equal(30m, betaOutcome.GetProperty("totals").GetProperty("cost").GetDecimal());
        Assert.Equal(0, betaOutcome.GetProperty("totals").GetProperty("agentSeconds").GetDouble());
        Assert.Equal(90m, betaOutcome.GetProperty("outcomeTotalCost").GetDecimal());
    }

    /// <summary>
    /// THE ALL-TEAMS COST READS ARE BOUNDED TO THE CALLER'S TEAMS: a member's key reads its own team on
    /// <c>/api/teams/cost</c> and <c>/api/teams/cost/summary</c>, never another team's run, and is refused another team's
    /// eight-week read with the gate's sentence.
    /// </summary>
    [Fact]
    public async Task A_members_key_reads_only_its_own_team_on_the_all_teams_cost_reads()
    {
        var person = await host.PersonAsync();
        var other = await NewTeamAsync("Cost unreached");
        var now = DateTimeOffset.UtcNow;
        await RunAsync(NextCorrelation(), other, now.AddHours(-2), now.AddHours(-1), rate: "60");

        using var member = host.Container(host.AlphaContainerKey);
        var all = await JsonAsync(await member.GetAsync("/api/teams/cost", Ct));
        Assert.Equal([host.Alpha], all.GetProperty("teams").EnumerateArray().Select(t => t.GetProperty("team").GetString()));
        Assert.Contains(other, (await JsonAsync(await person.GetAsync("/api/teams/cost", Ct))).GetProperty("teams").EnumerateArray().Select(t => t.GetProperty("team").GetString()));

        var mine = await JsonAsync(await member.GetAsync("/api/teams/cost/summary", Ct));
        var theirs = await JsonAsync(await person.GetAsync("/api/teams/cost/summary", Ct));
        Assert.True(Seconds(theirs, "total") - Seconds(mine, "total") >= 3600 - 0.001);

        var refused = await member.GetAsync($"/api/teams/{other}/cost", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(TeamGate.NoSuchTeamMessage, JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync(Ct)).GetProperty("error").GetString());
    }

    /// <summary>
    /// A PLUGIN IS PRICED ONLY FROM ITS OWN RATE, ON EVERY TEAM-PAGE READ: with the tenant agent rate set and in the
    /// rate's history, a plugin run from before snapshots is never reconstructed from it (unavailable), a plugin run that
    /// started with no rate of its own is not priced, and one with its own rate is priced by duration at it - while an
    /// agent run from before snapshots is still reconstructed. Each unpriced plugin says which on its member entry.
    /// </summary>
    [Fact]
    public async Task A_plugin_is_priced_only_from_its_own_rate_and_says_whether_it_is_not_priced_or_unavailable()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Cost plugin legacy");

        try
        {
            await SetOkAsync(person, TenantSettings.OutcomesAgentHourlyRateName, 60);
            var start = DateTimeOffset.UtcNow.AddSeconds(1);

            // FROM BEFORE SNAPSHOTS: an agent run (reconstructed at 60) and a plugin run (never priced).
            await RunAsync(NextCorrelation(), team, start, start.AddHours(1), member: "Dev");
            await RunAsync(NextCorrelation(), team, start, start.AddHours(1), kind: MemberRef.PluginKind, member: "Old");
            // ONE PLUGIN WITH BOTH: a run from before snapshots and one that started with no rate of its own.
            await RunAsync(NextCorrelation(), team, start, start.AddHours(1), kind: MemberRef.PluginKind, member: "Mixed");
            await RunAsync(NextCorrelation(), team, start, start.AddHours(1), kind: MemberRef.PluginKind, member: "Mixed", snapshot: true);
            // A PLUGIN WITH ITS OWN RATE: two hours at 15, by duration.
            await RunAsync(NextCorrelation(), team, start, start.AddHours(2), kind: MemberRef.PluginKind, member: "Paid", rate: "15");

            var query = $"from={Day(start)}&to={Day(start.AddDays(2))}&bucket=day";
            var read = await JsonAsync(await person.GetAsync($"/api/teams/{team}/cost/period?{query}", Ct));
            var members = read.GetProperty("members").EnumerateArray()
                .ToDictionary(m => m.GetProperty("member").GetString()!, m => m);

            Assert.Equal(60m, members["Dev"].GetProperty("figures").GetProperty("cost").GetDecimal());
            Assert.Equal(["reconstructed"], members["Dev"].GetProperty("figures").GetProperty("legacyRates").EnumerateArray().Select(r => r.GetString()));
            Assert.False(members["Dev"].TryGetProperty("unpriced", out _));

            Assert.Equal(JsonValueKind.Null, members["Old"].GetProperty("figures").GetProperty("cost").ValueKind);
            Assert.Equal(TeamCostBreakdown.Unavailable, members["Old"].GetProperty("unpriced").GetString());

            Assert.Equal(JsonValueKind.Null, members["Mixed"].GetProperty("figures").GetProperty("cost").ValueKind);
            Assert.Equal(TeamCostBreakdown.NotPriced, members["Mixed"].GetProperty("unpriced").GetString());

            Assert.Equal(30m, members["Paid"].GetProperty("figures").GetProperty("cost").GetDecimal());
            Assert.False(members["Paid"].TryGetProperty("unpriced", out _));

            // NO PLUGIN SECOND IS AGENT HOURS, and the headline is the agent's 60 and Paid's 30 - never a plugin at 60.
            var totals = read.GetProperty("totals");
            Assert.Equal(3600, totals.GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(90m, totals.GetProperty("cost").GetDecimal());
            Assert.All(members.Where(m => m.Key != "Dev"), m => Assert.Equal(0, m.Value.GetProperty("figures").GetProperty("agentSeconds").GetDouble()));

            // THE LEDGER EXPORT SAYS THE SAME PER RUN: Old's run is `none` with no estimate, never the agent rate.
            var ledger = await JsonAsync(await person.GetAsync("/api/ledger/export?take=200", Ct));
            var old = ledger.GetProperty("runs").EnumerateArray()
                .Single(r => r.GetProperty("teamId").GetString() == team && r.GetProperty("member").GetString() == "Old");
            Assert.Equal("none", old.GetProperty("rateSource").GetString());
            Assert.Equal(JsonValueKind.Null, old.GetProperty("estimate").GetProperty("amount").ValueKind);
        }
        finally
        {
            await SetAsync(person, TenantSettings.OutcomesAgentHourlyRateName, null);
        }
    }

    [Fact]
    public async Task A_run_crossing_a_week_is_split_between_the_weeks_it_overlaps()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Cost split");
        var monday = OutcomeFigures.WeekOf(DateTimeOffset.UtcNow);

        await RunAsync(NextCorrelation(), team, monday.AddHours(-1), monday.AddHours(2), rate: "60");

        var read = await CostAsync(person, team);
        var weekly = Single(read).GetProperty("weekly").EnumerateArray().ToList();

        Assert.Equal(3600, weekly[^2].GetProperty("agentSeconds").GetDouble(), 3);
        Assert.Equal(60m, weekly[^2].GetProperty("cost").GetDecimal());
        Assert.Equal(7200, weekly[^1].GetProperty("agentSeconds").GetDouble(), 3);
        Assert.Equal(120m, weekly[^1].GetProperty("cost").GetDecimal());
        // Tokens are reported when a run ends: in the week it ended.
        Assert.Equal(0, weekly[^2].GetProperty("measuredRuns").GetInt32());
        Assert.Equal(1, weekly[^1].GetProperty("measuredRuns").GetInt32());
        Assert.Equal(180m, read.GetProperty("totals").GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task An_unknown_team_is_not_found()
    {
        var person = await host.PersonAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await person.GetAsync("/api/teams/no-such-team/cost", Ct)).StatusCode);
    }

    // ---- the teams list's period ----

    /// <summary>
    /// THE TEAMS LIST'S PERIOD READ is ExecutionCost over the period: each team's total and Agent Hours are its runs'
    /// parts inside the period priced as the Spend page prices them; the instance's total is the Spend overview's
    /// Estimated Execution Cost for the same period; daily buckets split a run crossing midnight; Unattributed is its
    /// own entry; a team with no runs reads zero seconds and no money, never 0.
    /// </summary>
    [Fact]
    public async Task The_period_read_answers_each_team_as_ExecutionCost_and_the_Spend_overview_do_for_the_same_window()
    {
        var person = await host.PersonAsync();
        var busy = await NewTeamAsync("Period busy");
        var other = await NewTeamAsync("Period other");
        var idle = await NewTeamAsync("Period idle");
        var outcome = await CreateAsync(person, "Period outcome");
        var from = new DateTimeOffset(2013, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2013, 12, 1, 0, 0, 0, TimeSpan.Zero);

        // Linked, two hours at 60 on Oct 3; unlinked, crossing midnight into Nov 1 at 90; an unpriced run from before
        // snapshots; the other team's linked run at 45; and a run in the month before the period, for `prior`.
        await RunAsync(await LinkedAsync(outcome.Id, busy), busy, from.AddDays(2).AddHours(9), from.AddDays(2).AddHours(11), rate: "60");
        await RunAsync(NextCorrelation(), busy, to.AddMonths(-1).AddHours(-1), to.AddMonths(-1).AddHours(2), rate: "90");
        await RunAsync(NextCorrelation(), busy, from.AddDays(10), from.AddDays(10).AddMinutes(30));
        await RunAsync(await LinkedAsync(outcome.Id, other), other, from.AddDays(40), from.AddDays(40).AddMinutes(40), rate: "45");
        await RunAsync(NextCorrelation(), busy, from.AddDays(-5), from.AddDays(-5).AddHours(1), rate: "60");

        var daily = await JsonAsync(await person.GetAsync($"/api/teams/cost/period?from={Day(from)}&to={Day(to)}&bucket=day", Ct));
        var monthly = await JsonAsync(await person.GetAsync($"/api/teams/cost/period?from={Day(from)}&to={Day(to)}&bucket=month", Ct));
        var overview = await JsonAsync(await person.GetAsync($"/api/spend/overview?from={Day(from)}&to={Day(to)}", Ct));

        Assert.Equal(61, daily.GetProperty("buckets").GetArrayLength());
        Assert.Equal(["2013-10-01", "2013-11-01"], monthly.GetProperty("buckets").EnumerateArray().Select(b => b.GetString()!));

        // EXECUTIONCOST ITSELF over the same window, team by team, through the store the routes read.
        var all = await Outcomes.ListAsync(Ct);
        var runs = ExecutionCost.Of(await Outcomes.ReadExecutionRunsAsync(from, to, pricedOnly: false, Ct), all, "USD");
        decimal? ServiceCost(string team) => TeamCostPeriod.Tally(runs.Where(r => r.Ledger.TeamId == team), "USD", from, to).Cost;
        double ServiceSeconds(string team) => TeamCostPeriod.Tally(runs.Where(r => r.Ledger.TeamId == team), "USD", from, to).AgentSeconds;

        foreach (var read in new[] { daily, monthly })
        {
            JsonElement TeamOf(string id) => read.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == id);

            var b = TeamOf(busy).GetProperty("totals");
            Assert.Equal(120m + 270m, b.GetProperty("cost").GetDecimal());
            Assert.Equal(ServiceCost(busy), b.GetProperty("cost").GetDecimal());
            Assert.Equal(2 * 3600 + 3 * 3600 + 1800, b.GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(ServiceSeconds(busy), b.GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(30m, TeamOf(other).GetProperty("totals").GetProperty("cost").GetDecimal());

            // UNATTRIBUTED FIRST, then the outcome; the unpriced run adds hours and no money.
            var outcomes = TeamOf(busy).GetProperty("outcomes").EnumerateArray().ToList();
            Assert.Equal(JsonValueKind.Null, outcomes[0].GetProperty("id").ValueKind);
            Assert.Equal("Unattributed", outcomes[0].GetProperty("name").GetString());
            Assert.Equal(270m, outcomes[0].GetProperty("totals").GetProperty("cost").GetDecimal());
            Assert.Equal(outcome.Id, outcomes[1].GetProperty("id").GetString());
            Assert.Equal(120m, outcomes[1].GetProperty("totals").GetProperty("cost").GetDecimal());

            // A TEAM WITH NO RUNS: zero seconds, no money - never 0 - and no outcomes.
            var none = TeamOf(idle);
            Assert.Equal(0, none.GetProperty("totals").GetProperty("agentSeconds").GetDouble());
            Assert.Equal(JsonValueKind.Null, none.GetProperty("totals").GetProperty("cost").ValueKind);
            Assert.Empty(none.GetProperty("outcomes").EnumerateArray());

            // THE INSTANCE'S TOTAL IS THE SPEND OVERVIEW'S ESTIMATE for the same period, and the teams' totals summed.
            Assert.Equal(overview.GetProperty("estimate").GetProperty("cost").GetDecimal(), read.GetProperty("total").GetProperty("cost").GetDecimal());
            Assert.Equal(overview.GetProperty("estimate").GetProperty("agentSeconds").GetDouble(),
                read.GetProperty("total").GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(420m, read.GetProperty("total").GetProperty("cost").GetDecimal());
            Assert.Equal(60m, read.GetProperty("prior").GetProperty("cost").GetDecimal());

            // WHERE IT GOES: Unattributed and the outcome, adding up to the total.
            var served = read.GetProperty("outcomes").EnumerateArray().ToList();
            Assert.Equal("Unattributed", served[0].GetProperty("name").GetString());
            Assert.Equal(270m, served[0].GetProperty("cost").GetDecimal());
            Assert.Equal(150m, served.Single(o => o.GetProperty("id").GetString() == outcome.Id).GetProperty("cost").GetDecimal());
        }

        // THE RUN CROSSING MIDNIGHT is split between Oct 31 and Nov 1 by overlap, each part priced on its own.
        var busyDays = daily.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == busy).GetProperty("buckets");
        var oct31 = busyDays.EnumerateArray().Single(d => d.GetProperty("start").GetString() == "2013-10-31");
        var nov1 = busyDays.EnumerateArray().Single(d => d.GetProperty("start").GetString() == "2013-11-01");
        Assert.Equal(90m, oct31.GetProperty("cost").GetDecimal());
        Assert.Equal(180m, nov1.GetProperty("cost").GetDecimal());
        Assert.Equal(JsonValueKind.Null, busyDays[5].GetProperty("cost").ValueKind);
        var instanceDays = daily.GetProperty("totalBuckets").EnumerateArray().ToList();
        Assert.Equal(90m, instanceDays.Single(d => d.GetProperty("start").GetString() == "2013-10-31").GetProperty("cost").GetDecimal());
        Assert.Equal(30m, instanceDays.Single(d => d.GetProperty("start").GetString() == "2013-11-10").GetProperty("cost").GetDecimal());

        var busyMonths = monthly.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == busy).GetProperty("buckets");
        Assert.Equal(120m + 90m, busyMonths[0].GetProperty("cost").GetDecimal());
        Assert.Equal(180m, busyMonths[1].GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task The_period_read_refuses_a_bad_period_bucket_or_length_with_one_sentence()
    {
        var person = await host.PersonAsync();

        foreach (var (query, sentence) in new[]
        {
            ("from=2013-10-01", TeamCostPeriod.PeriodRefused),
            ("from=2013-10-02&to=2013-10-01", TeamCostPeriod.PeriodRefused),
            ("from=2013-10-01&to=2013-11-01&bucket=week", TeamCostPeriod.BucketRefused),
            ("from=2010-01-01&to=2013-01-01&bucket=day", TeamCostPeriod.TooLongRefused),
        })
        {
            var response = await person.GetAsync($"/api/teams/cost/period?{query}", Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(sentence, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        }

        // NEITHER DATE: the previous and the current UTC month, by day.
        var read = await JsonAsync(await person.GetAsync("/api/teams/cost/period", Ct));
        var (from, to) = TeamCostPeriod.DefaultPeriod(DateTimeOffset.UtcNow);
        Assert.Equal(Day(from), read.GetProperty("period").GetProperty("from").GetString());
        Assert.Equal(Day(to), read.GetProperty("period").GetProperty("to").GetString());
        Assert.Equal("day", read.GetProperty("period").GetProperty("bucket").GetString());
        Assert.Equal((to - from).Days, read.GetProperty("buckets").GetArrayLength());
    }

    /// <summary>
    /// THE EXISTING READS ARE UNCHANGED: called as before, the team, all-teams and summary reads answer the fields
    /// they always have and nothing of the period read's; and a period's parameters sent to them change nothing.
    /// </summary>
    [Fact]
    public async Task The_existing_cost_reads_answer_as_before_without_any_new_parameter()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Unchanged reads");
        var now = DateTimeOffset.UtcNow;
        await RunAsync(NextCorrelation(), team, now.AddHours(-3), now.AddHours(-2), rate: "60");

        static string[] Keys(JsonElement e) => e.EnumerateObject().Select(p => p.Name).ToArray();

        var one = await CostAsync(person, team);
        Assert.Equal(["team", "money", "weeks", "outcomes", "totals", "human"], Keys(one));

        var all = await JsonAsync(await person.GetAsync("/api/teams/cost", Ct));
        Assert.Equal(["money", "weeks", "teams"], Keys(all));
        Assert.Equal(8, all.GetProperty("weeks").GetArrayLength());

        var summary = await SummaryAsync(person);
        Assert.Equal(
            ["rate", "weeks", "periodStart", "periodEnd", "total", "prior", "outcomes", "rightNow", "human"], Keys(summary));

        var withPeriod = await JsonAsync(await person.GetAsync("/api/teams/cost?from=2013-10-01&to=2013-12-01&bucket=month", Ct));
        Assert.Equal(all.GetProperty("weeks").GetRawText(), withPeriod.GetProperty("weeks").GetRawText());
        Assert.Equal(
            all.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == team).GetRawText(),
            withPeriod.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == team).GetRawText());
    }

    // ---- one team's period, broken down (the team page) ----

    /// <summary>
    /// THE TEAM PAGE'S READ IS THE TEAMS LIST'S ROW, AND EXECUTIONCOST BY GROUP: for the same window, by day and by month,
    /// the team's totals, buckets and outcomes are its entry of <c>GET /api/teams/cost/period</c> byte for byte; each
    /// member (agent and plugin apart, an unpriced plugin null - never 0), outcome and workflow is ExecutionCost's own
    /// tally of its runs over the window; executions and active members count the runs that started; and a team with
    /// no runs answers zeros, nulls and empty breakdowns.
    /// </summary>
    [Fact]
    public async Task The_team_period_read_is_the_teams_list_row_and_ExecutionCost_by_member_outcome_and_workflow()
    {
        var person = await host.PersonAsync();
        var busy = await NewTeamAsync("Team period busy");
        var idle = await NewTeamAsync("Team period idle");
        var outcome = await CreateAsync(person, "Team period outcome", value: "1000");
        var from = new DateTimeOffset(2014, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2014, 12, 1, 0, 0, 0, TimeSpan.Zero);

        var linked = await LinkedAsync(outcome.Id, busy);
        var crossing = NextCorrelation();
        var edge = NextCorrelation();
        await RunAsync(linked, busy, from.AddDays(2).AddHours(9), from.AddDays(2).AddHours(11), rate: "60", member: "Dev");
        await RunAsync(crossing, busy, to.AddMonths(-1).AddHours(-1), to.AddMonths(-1).AddHours(2), rate: "90", member: "Ana");
        await RunAsync(edge, busy, from.AddHours(-1), from.AddHours(1), rate: "60", member: "Ana");
        await RunAsync(NextCorrelation(), busy, from.AddDays(4), from.AddDays(4).AddHours(1), rate: "30", kind: MemberRef.PluginKind, member: "Tool");
        await RunAsync(NextCorrelation(), busy, from.AddDays(5), from.AddDays(5).AddHours(1), snapshot: true, kind: MemberRef.PluginKind, member: "Bare");
        await RunAsync(NextCorrelation(), busy, from.AddDays(10), from.AddDays(10).AddMinutes(30), member: "Dev");

        var all = await Outcomes.ListAsync(Ct);
        var runs = ExecutionCost.Of(await Outcomes.ReadExecutionRunsAsync(from, to, pricedOnly: false, Ct), all, "USD")
            .Where(r => r.Ledger.TeamId == busy).ToList();
        ExecutionCost.Tally Service(Func<ExecutionCost.Run, bool> which) => TeamCostPeriod.Tally(runs.Where(which), "USD", from, to);

        foreach (var bucket in new[] { "day", "month" })
        {
            var query = $"from={Day(from)}&to={Day(to)}&bucket={bucket}";
            var read = await JsonAsync(await person.GetAsync($"/api/teams/{busy}/cost/period?{query}", Ct));
            var list = await JsonAsync(await person.GetAsync($"/api/teams/cost/period?{query}", Ct));
            var row = list.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == busy);

            // THE LIST'S ROW, BYTE FOR BYTE: totals, buckets, and each outcome's name, rank, totals and buckets.
            var totals = read.GetProperty("totals");
            foreach (var field in new[] { "agentSeconds", "cost", "agentRate", "legacyRates" })
            {
                Assert.Equal(row.GetProperty("totals").GetProperty(field).GetRawText(), totals.GetProperty(field).GetRawText());
            }
            Assert.Equal(row.GetProperty("buckets").GetRawText(), read.GetProperty("totalBuckets").GetRawText());
            Assert.Equal(list.GetProperty("buckets").GetRawText(), read.GetProperty("buckets").GetRawText());
            Assert.Equal(list.GetProperty("period").GetRawText(), read.GetProperty("period").GetRawText());
            var listed = row.GetProperty("outcomes").EnumerateArray().ToList();
            var outcomes = read.GetProperty("outcomes").EnumerateArray().ToList();
            Assert.Equal(listed.Count, outcomes.Count);
            for (var i = 0; i < listed.Count; i++)
            {
                foreach (var field in new[] { "id", "name", "rank", "totals", "buckets" })
                {
                    Assert.Equal(listed[i].GetProperty(field).GetRawText(), outcomes[i].GetProperty(field).GetRawText());
                }
            }

            // THE TEAM: Dev 120, Ana 270 + the hour of the edge run inside the period, Tool 30; Bare and the run from
            // before snapshots add no money. Agent Hours leave both plugins out.
            Assert.Equal(480m, totals.GetProperty("cost").GetDecimal());
            Assert.Equal(Service(_ => true).Cost, totals.GetProperty("cost").GetDecimal());
            Assert.Equal(7200 + 10800 + 3600 + 1800, totals.GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(6, totals.GetProperty("executions").GetInt32());
            Assert.Equal(4, totals.GetProperty("activeMembers").GetInt32());
            Assert.Equal(60, totals.GetProperty("billable").GetInt64());
            Assert.Equal(6, totals.GetProperty("measuredRuns").GetInt32());

            // BY OUTCOME: Unattributed first; the budget and the all-time total beside the outcome.
            Assert.Equal(JsonValueKind.Null, outcomes[0].GetProperty("id").ValueKind);
            Assert.Equal(360m, outcomes[0].GetProperty("figures").GetProperty("cost").GetDecimal());
            Assert.Equal(Service(r => r.OutcomeId is null).Cost, outcomes[0].GetProperty("figures").GetProperty("cost").GetDecimal());
            Assert.Equal(outcome.Id, outcomes[1].GetProperty("id").GetString());
            Assert.Equal(120m, outcomes[1].GetProperty("figures").GetProperty("cost").GetDecimal());
            Assert.Equal(1000m, outcomes[1].GetProperty("value").GetDecimal());
            Assert.Equal(120m, outcomes[1].GetProperty("outcomeTotalCost").GetDecimal());
            Assert.Equal(1, outcomes[1].GetProperty("figures").GetProperty("executions").GetInt32());

            // BY MEMBER: agents first, then plugins, each by name, each ExecutionCost's tally of its runs.
            var members = read.GetProperty("members").EnumerateArray().ToList();
            Assert.Equal(
                ["Ana agent", "Dev agent", "Bare plugin", "Tool plugin"],
                members.Select(m => $"{m.GetProperty("member").GetString()} {m.GetProperty("kind").GetString()}"));
            foreach (var member in members)
            {
                var name = member.GetProperty("member").GetString();
                var service = Service(r => r.Ledger.Member == name);
                var figures = member.GetProperty("figures");
                Assert.Equal(service.AgentSeconds, figures.GetProperty("agentSeconds").GetDouble(), 3);
                Assert.Equal(service.Cost is null ? "null" : service.Cost.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    figures.GetProperty("cost").GetRawText());
            }
            Assert.Equal(330m, members[0].GetProperty("figures").GetProperty("cost").GetDecimal());
            Assert.Equal(82.5m, members[0].GetProperty("figures").GetProperty("agentRate").GetDecimal());
            Assert.Equal(2, members[0].GetProperty("figures").GetProperty("executions").GetInt32());
            Assert.Equal(120m, members[1].GetProperty("figures").GetProperty("cost").GetDecimal());
            Assert.Equal(9000, members[1].GetProperty("figures").GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(JsonValueKind.Null, members[2].GetProperty("figures").GetProperty("cost").ValueKind);
            Assert.Equal(0, members[2].GetProperty("figures").GetProperty("agentSeconds").GetDouble());
            Assert.Equal(1, members[2].GetProperty("figures").GetProperty("executions").GetInt32());
            Assert.Equal(30m, members[3].GetProperty("figures").GetProperty("cost").GetDecimal());
            Assert.Equal(0, members[3].GetProperty("figures").GetProperty("agentSeconds").GetDouble());
            Assert.Equal(JsonValueKind.Null, members[3].GetProperty("figures").GetProperty("agentRate").ValueKind);

            // WHY A PLUGIN HAS NO MONEY, on that plugin's entry alone: Bare started with no rate of its own. No other
            // entry - an agent's, or a priced plugin's - carries the field at all.
            Assert.Equal(TeamCostBreakdown.NotPriced, members[2].GetProperty("unpriced").GetString());
            Assert.All([members[0], members[1], members[3]], m => Assert.False(m.TryGetProperty("unpriced", out _)));

            // BY WORKFLOW: newest first; the edge run's workflow is its hour inside the period, never the whole run.
            var workflows = read.GetProperty("workflows").EnumerateArray().ToList();
            Assert.Equal(6, workflows.Count);
            Assert.Equal(workflows.Select(w => w.GetProperty("workflow").GetInt64()).OrderByDescending(w => w), workflows.Select(w => w.GetProperty("workflow").GetInt64()));
            JsonElement WorkflowOf(long n) => workflows.Single(w => w.GetProperty("workflow").GetInt64() == n).GetProperty("figures");
            Assert.Equal(60m, WorkflowOf(edge).GetProperty("cost").GetDecimal());
            Assert.Equal(Service(r => r.Ledger.Correlation == edge).Cost, WorkflowOf(edge).GetProperty("cost").GetDecimal());
            Assert.Equal(270m, WorkflowOf(crossing).GetProperty("cost").GetDecimal());

            // EACH BREAKDOWN ADDS UP TO THE TEAM'S TOTAL EXACTLY: each run's part is priced once, in one group of each.
            static decimal Sum(IEnumerable<JsonElement> rows) =>
                rows.Select(r => r.GetProperty("figures").GetProperty("cost")).Where(c => c.ValueKind == JsonValueKind.Number).Sum(c => c.GetDecimal());
            static double Hours(IEnumerable<JsonElement> rows) => rows.Sum(r => r.GetProperty("figures").GetProperty("agentSeconds").GetDouble());
            Assert.Equal(480m, Sum(members));
            Assert.Equal(480m, Sum(outcomes));
            Assert.Equal(480m, Sum(workflows));
            Assert.Equal(totals.GetProperty("agentSeconds").GetDouble(), Hours(members), 3);
            Assert.Equal(totals.GetProperty("agentSeconds").GetDouble(), Hours(workflows), 3);
            Assert.Equal(6, members.Sum(m => m.GetProperty("figures").GetProperty("executions").GetInt32()));

            // A TEAM WITH NO RUNS: zero seconds, no money - never 0 - and nothing to break down.
            var none = await JsonAsync(await person.GetAsync($"/api/teams/{idle}/cost/period?{query}", Ct));
            Assert.Equal(0, none.GetProperty("totals").GetProperty("agentSeconds").GetDouble());
            Assert.Equal(JsonValueKind.Null, none.GetProperty("totals").GetProperty("cost").ValueKind);
            Assert.Equal(0, none.GetProperty("totals").GetProperty("executions").GetInt32());
            Assert.Equal(0, none.GetProperty("totals").GetProperty("activeMembers").GetInt32());
            Assert.Empty(none.GetProperty("outcomes").EnumerateArray());
            Assert.Empty(none.GetProperty("members").EnumerateArray());
            Assert.Empty(none.GetProperty("workflows").EnumerateArray());
        }

        // THE CROSSING RUN by day: Oct 31 and Nov 1, each part priced on its own.
        var daily = await JsonAsync(await person.GetAsync($"/api/teams/{busy}/cost/period?from={Day(from)}&to={Day(to)}&bucket=day", Ct));
        var days = daily.GetProperty("totalBuckets").EnumerateArray().ToList();
        Assert.Equal(90m, days.Single(d => d.GetProperty("start").GetString() == "2014-10-31").GetProperty("cost").GetDecimal());
        Assert.Equal(180m, days.Single(d => d.GetProperty("start").GetString() == "2014-11-01").GetProperty("cost").GetDecimal());

        // EACH WORKFLOW ALL TIME: the edge run whole, as ExecutionCost prices it; one workflow alone when asked.
        var everyRun = ExecutionCost.Of(await Outcomes.ReadExecutionRunsAsync(null, null, pricedOnly: false, Ct), all, "USD");
        var whole = await JsonAsync(await person.GetAsync($"/api/teams/{busy}/cost/workflows", Ct));
        Assert.Equal(6, whole.GetProperty("workflows").GetArrayLength());
        var one = await JsonAsync(await person.GetAsync($"/api/teams/{busy}/cost/workflows?workflow={edge}", Ct));
        var only = Assert.Single(one.GetProperty("workflows").EnumerateArray().ToList());
        Assert.Equal(edge, only.GetProperty("workflow").GetInt64());
        Assert.Equal(120m, only.GetProperty("figures").GetProperty("cost").GetDecimal());
        Assert.Equal(everyRun.Single(r => r.Ledger.Correlation == edge).Estimate, only.GetProperty("figures").GetProperty("cost").GetDecimal());
        Assert.Equal(7200, only.GetProperty("figures").GetProperty("agentSeconds").GetDouble(), 3);
        Assert.Empty((await JsonAsync(await person.GetAsync($"/api/teams/{idle}/cost/workflows", Ct))).GetProperty("workflows").EnumerateArray());
    }

    /// <summary>
    /// WHAT THE CALLER CANNOT REACH IS REFUSED: an unknown team is 404; another team's member key holding every permit
    /// is refused both team reads with the team gate's own sentence; and a bad period, bucket or length is refused with
    /// the Teams list read's sentence.
    /// </summary>
    [Fact]
    public async Task The_team_period_and_workflow_reads_refuse_an_unknown_or_unreached_team_and_a_bad_period()
    {
        var person = await host.PersonAsync();
        var mine = await NewTeamAsync("Reach mine");
        var theirs = await NewTeamAsync("Reach theirs");
        await RunAsync(NextCorrelation(), theirs, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1), rate: "60");

        foreach (var route in new[] { "cost/period", "cost/workflows" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await person.GetAsync($"/api/teams/no-such-team-{Guid.NewGuid():N}/{route}", Ct)).StatusCode);
        }

        var principals = host.Services.GetRequiredService<IPrincipalStore>();
        var key = await principals.MintAsync(new ContainerId(mine, "Dev").ToString(), PrincipalKind.Container, mine, Permits.All, ct: Ct);
        using var machine = host.Container(key);
        foreach (var route in new[] { "cost/period", "cost/workflows" })
        {
            var refused = await machine.GetAsync($"/api/teams/{theirs}/{route}", Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(Harness.Host.Auth.TeamGate.NoSuchTeamMessage, (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
            Assert.Equal(HttpStatusCode.OK, (await machine.GetAsync($"/api/teams/{mine}/{route}", Ct)).StatusCode);
        }

        foreach (var (query, sentence) in new[]
        {
            ("from=2013-10-01", TeamCostPeriod.PeriodRefused),
            ("from=2013-10-02&to=2013-10-01", TeamCostPeriod.PeriodRefused),
            ("from=2013-10-01&to=2013-11-01&bucket=week", TeamCostPeriod.BucketRefused),
            ("from=2010-01-01&to=2013-01-01&bucket=day", TeamCostPeriod.TooLongRefused),
        })
        {
            var response = await person.GetAsync($"/api/teams/{mine}/cost/period?{query}", Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(sentence, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        }

        // NEITHER DATE: the Teams list's default period, by day.
        var read = await JsonAsync(await person.GetAsync($"/api/teams/{mine}/cost/period", Ct));
        var (from, to) = TeamCostPeriod.DefaultPeriod(DateTimeOffset.UtcNow);
        Assert.Equal(Day(from), read.GetProperty("period").GetProperty("from").GetString());
        Assert.Equal(Day(to), read.GetProperty("period").GetProperty("to").GetString());
        Assert.Equal("day", read.GetProperty("period").GetProperty("bucket").GetString());
    }

    /// <summary>
    /// THE TEAM PAGE'S EXISTING READS ANSWER BYTE FOR BYTE ALIKE: the eight-week team read, the Teams list's period read
    /// and the activity read give the same bytes before and after the new reads are called, and the new reads'
    /// parameters sent to them change nothing.
    /// </summary>
    [Fact]
    public async Task The_existing_team_reads_answer_byte_for_byte_alike_beside_the_team_period_reads()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Byte alike");
        var now = DateTimeOffset.UtcNow;
        await RunAsync(NextCorrelation(), team, now.AddHours(-3), now.AddHours(-2), rate: "60");

        async Task<string> RawAsync(string url) => await (await person.GetAsync(url, Ct)).Content.ReadAsStringAsync(Ct);
        var window = $"from={Day(now.AddDays(-3))}&to={Day(now.AddDays(1))}";

        var cost = await RawAsync($"/api/teams/{team}/cost");
        var period = await RawAsync($"/api/teams/cost/period?{window}&bucket=day");
        var activity = await RawAsync($"/api/teams/{team}/activity");

        await RawAsync($"/api/teams/{team}/cost/period?{window}&bucket=month");
        await RawAsync($"/api/teams/{team}/cost/workflows");

        Assert.Equal(cost, await RawAsync($"/api/teams/{team}/cost"));
        Assert.Equal(cost, await RawAsync($"/api/teams/{team}/cost?{window}&bucket=month&workflow=1"));
        Assert.Equal(period, await RawAsync($"/api/teams/cost/period?{window}&bucket=day"));
        Assert.Equal(period, await RawAsync($"/api/teams/cost/period?{window}&bucket=day&workflow=1"));

        static string WithoutClock(string raw)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();
            node.Remove("serverNow");
            return node.ToJsonString();
        }
        Assert.Equal(WithoutClock(activity), WithoutClock(await RawAsync($"/api/teams/{team}/activity")));
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

            await RunAsync(NextCorrelation(), team, now.AddHours(-2), now.AddHours(-1), rate: "30");
            // THE EIGHT WEEKS BEFORE: in prior, never in total.
            await RunAsync(NextCorrelation(), team, now.AddDays(-80).AddHours(-2), now.AddDays(-80), rate: "30");
            // OLDER STILL: in neither.
            await RunAsync(NextCorrelation(), team, now.AddDays(-150).AddHours(-4), now.AddDays(-150), rate: "30");

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

        await RunAsync(await LinkedAsync(shared.Id, alpha), alpha, now.AddHours(-2), now.AddHours(-1), rate: "60");
        await RunAsync(await LinkedAsync(shared.Id, beta), beta, now.AddHours(-3), now.AddHours(-1), rate: "60");
        // OUTSIDE THE EIGHT WEEKS: in what the budget is measured against, never in the weeks.
        await RunAsync(await LinkedAsync(shared.Id, alpha), alpha, now.AddDays(-70).AddHours(-1), now.AddDays(-70), rate: "60");

        try
        {
            await SetOkAsync(person, TenantSettings.OutcomesAgentHourlyRateName, 60);
            var summary = await SummaryAsync(person);

            var entry = OutcomeOf(summary, shared.Id);
            Assert.Equal(shared.Name, entry.GetProperty("name").GetString());
            Assert.Equal(10800, entry.GetProperty("agentSeconds").GetDouble(), 3);
            Assert.Equal(180m, entry.GetProperty("cost").GetDecimal());
            Assert.Equal(500m, entry.GetProperty("budget").GetDecimal());
            Assert.Equal(240m, entry.GetProperty("outcomeTotalCost").GetDecimal());

            // THE SAME FIGURES COST 1 GIVES: the outcomes dashboard's cost, and the all-teams read's
            // total cost and rank.
            var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
            var figures = list.GetProperty("outcomes").EnumerateArray()
                .Single(o => o.GetProperty("id").GetString() == shared.Id).GetProperty("figures");
            Assert.Equal(figures.GetProperty("cost").GetProperty("amount").GetDecimal(), entry.GetProperty("outcomeTotalCost").GetDecimal());
            var perTeam = (await CostAsync(person, alpha)).GetProperty("outcomes").EnumerateArray()
                .Single(o => o.GetProperty("id").GetString() == shared.Id);
            Assert.Equal(perTeam.GetProperty("outcomeTotalCost").GetDecimal(), entry.GetProperty("outcomeTotalCost").GetDecimal());
            Assert.Equal(perTeam.GetProperty("rank").GetInt32(), entry.GetProperty("rank").GetInt32());

            // A BUDGET WITH NO WORK IN THE WEEKS IS STILL LISTED, with no estimate - never 0.
            var idle = OutcomeOf(summary, unspent.Id);
            Assert.Equal(0, idle.GetProperty("agentSeconds").GetDouble());
            Assert.Equal(JsonValueKind.Null, idle.GetProperty("cost").ValueKind);
            Assert.Equal(75m, idle.GetProperty("budget").GetDecimal());
            Assert.Equal(JsonValueKind.Null, idle.GetProperty("outcomeTotalCost").ValueKind);
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
        Assert.Equal("Unattributed", none.GetProperty("name").GetString());
        Assert.Equal(5400, none.GetProperty("agentSeconds").GetDouble() - before, 3);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("rank").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("budget").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("outcomeTotalCost").ValueKind);
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

        // The instance-wide totals carry the other tests' priced runs on this shared host; a summary
        // with no priced run at all is ExecutionCostTests'. Here: the unpriced outcome and the rate.
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("rate").ValueKind);
        Assert.True(Seconds(summary, "total") >= 7200);
        Assert.True(Seconds(summary, "prior") >= 3600);
        var entry = OutcomeOf(summary, outcome.Id);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("cost").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("outcomeTotalCost").ValueKind);
        Assert.Equal(3600, entry.GetProperty("agentSeconds").GetDouble(), 3);
        Assert.Equal(100m, entry.GetProperty("budget").GetDecimal());
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

    // ---- what the estimate is called ----

    /// <summary>
    /// THE ESTIMATE IS NEVER NAMED SPEND. Every money figure these routes answer is agent hours times
    /// the current rate - an estimate of execution cost, never money paid - so no field of theirs is
    /// named spend or spent. The per-workflow token budget's fields (<c>tokensSpent</c>, a workflow's
    /// <c>spend</c>) are the runaway safeguard, answered by other routes, and keep their names.
    /// </summary>
    [Fact]
    public async Task No_cost_or_estimate_route_names_a_money_field_spend()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Estimate names");
        var outcome = await CreateAsync(person, "Estimate names", value: "500");
        var now = DateTimeOffset.UtcNow;

        await RunAsync(await LinkedAsync(outcome.Id, team), team, now.AddHours(-2), now.AddHours(-1));
        await RunAsync(NextCorrelation(), team, now.AddHours(-3), now.AddHours(-2));

        try
        {
            await SetOkAsync(person, TenantSettings.OutcomesAgentHourlyRateName, 60);

            foreach (var route in new[]
            {
                $"/api/teams/{team}/cost", "/api/teams/cost", "/api/teams/cost/summary", "/api/teams/cost/period",
                "/api/outcomes", $"/api/outcomes/{outcome.Id}",
            })
            {
                var named = new List<string>();
                NamesOf(await JsonAsync(await person.GetAsync(route, Ct)), "", named);

                // THE ROUTE ANSWERED MONEY: a walk that found no cost field proves nothing.
                Assert.Contains(named, n => n.EndsWith(".cost", StringComparison.Ordinal) || n.EndsWith("Cost", StringComparison.Ordinal));
                Assert.DoesNotContain(named, n => SpendWord.IsMatch(n));
            }
        }
        finally
        {
            await SetAsync(person, TenantSettings.OutcomesAgentHourlyRateName, null);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SpendWord =
        new("spen[dt]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Every property name in a JSON answer, as a dotted path.</summary>
    private static void NamesOf(JsonElement element, string path, List<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var at = $"{path}.{property.Name}";
                    into.Add(at);
                    NamesOf(property.Value, at, into);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) NamesOf(item, path, into);
                break;
        }
    }

    private static JsonElement Single(JsonElement read) => Assert.Single(read.GetProperty("outcomes").EnumerateArray().ToList());

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<JsonElement> SummaryAsync(HttpClient person) =>
        await JsonAsync(await person.GetAsync("/api/teams/cost/summary", Ct));

    private static double Seconds(JsonElement summary, string amount) => summary.GetProperty(amount).GetProperty("agentSeconds").GetDouble();

    /// <summary>A summary amount's cost, 0 when it has none: for differences across a shared host.</summary>
    private static decimal Cost(JsonElement summary, string amount) =>
        summary.GetProperty(amount).GetProperty("cost") is { ValueKind: JsonValueKind.Number } cost ? cost.GetDecimal() : 0m;

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

    /// <summary>A person's move of an already-linked workflow to <paramref name="outcome"/>: a newer link row.</summary>
    private async Task RelinkAsync(long correlation, string outcome, string team) =>
        Assert.True((await Outcomes.LinkAsync(correlation, outcome, team, new OutcomeActor(Email, OutcomeActorKind.Person),
            OutcomeLinkHow.Person, agentRule: false,
            new TriggerAudit(null, Email, TenantActions.WorkflowOutcomeChanged, null, null, null), Ct)).Ok);

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

    /// <summary>
    /// A ledger row for a run of <paramref name="kind"/>. With <paramref name="snapshot"/> (implied by a
    /// <paramref name="rate"/>), it names an execution snapshot written as the writer writes one at start:
    /// that rate (none without one) and the workflow's newest link then. Without, it is a run from before
    /// snapshots.
    /// </summary>
    private async Task RunAsync(
        long correlation, string team, DateTimeOffset started, DateTimeOffset ended, long billable = 10, bool measured = true,
        string? rate = null, string kind = MemberRef.AgentKind, bool snapshot = false, string currency = "USD", string member = "Dev")
    {
        object start = DBNull.Value;
        if (rate is not null || snapshot)
        {
            start = Interlocked.Increment(ref _run);
            await ExecuteAsync(
                """
                INSERT INTO execution_snapshots (start_seq, correlation, team_id, team_name, member, member_kind,
                    rate_amount, rate_currency, rate_source, link_id, started_at)
                VALUES ($start, $c, $team, $team, $member, $kind, $rate, $currency, $source,
                    (SELECT MAX(id) FROM workflow_outcome_links WHERE correlation = $c), $started)
                """,
                ("$start", start), ("$c", correlation), ("$team", team), ("$kind", kind), ("$member", member),
                ("$rate", rate ?? (object)DBNull.Value), ("$currency", rate is null ? DBNull.Value : currency),
                ("$source", rate is null ? "none" : "member"), ("$started", started.ToString("O")));
        }

        await ExecuteAsync(
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, member_kind, run_outcome,
                started_at, ended_at, measured, tokens_in, tokens_out, billable, start_seq)
            VALUES ($seq, $c, $team, $team, $member, $kind, 'completed', $started, $ended, $measured, $in, $out, $billable, $start)
            """,
            ("$seq", Interlocked.Increment(ref _run)), ("$c", correlation), ("$team", team), ("$kind", kind), ("$member", member),
            ("$started", started.ToString("O")), ("$ended", ended.ToString("O")), ("$measured", measured ? 1 : 0),
            ("$in", measured ? billable : (object)DBNull.Value), ("$out", measured ? 0L : (object)DBNull.Value),
            ("$billable", measured ? billable : (object)DBNull.Value), ("$start", start));
    }

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
