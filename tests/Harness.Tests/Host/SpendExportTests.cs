using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// THE SPEND PAGE'S CSV FILES, on the rows alone: the headers never change and are there with no data, a cell a
/// spreadsheet could run is neutralised, quoting is RFC 4180, and the expenses file carries every row of a chain
/// while counting a corrected expense once.
/// </summary>
public sealed class SpendExportRuleTests
{
    private static readonly DateTimeOffset March = new(2031, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset April = March.AddMonths(1);
    private static readonly SpendExport.Period Period = new(March, April, "instance-1");

    [Fact]
    public void Each_file_has_its_golden_header_row_even_with_no_data_rows()
    {
        var empty = new SpendOverview.Overview(
            "", "", [], [], [], [], new SpendOverview.Estimate("usd", null, 0, null, []), [], [], []);

        Assert.Equal(
            "instance_id,period_start,period_end,billing_account_id,name,vendor,billing_type,fixed_or_variable,currency,"
            + "billing_period,recurring_amount,effective_from,effective_to,is_active,description,created_by,created_at,updated_at,scope\r\n",
            SpendExport.AccountsCsv(Period, []));
        Assert.Equal(
            "instance_id,period_start,period_end,expense_id,billing_account_id,billing_account_name,vendor,kind,status,amount,"
            + "currency,service_period_start,service_period_end,supersedes_expense_id,superseded_by_expense_id,is_current,"
            + "counts_in_actual_spend,reference,note,recorded_by,recorded_at,scope,billing_account_instance_id\r\n",
            SpendExport.ExpensesCsv(Period, [], []));
        Assert.Equal(
            "instance_id,period_start,period_end,rollup,team_id,team_name,outcome_id,outcome_name,rate_source,currency,"
            + "estimated_agent_cost,estimated_plugin_cost,estimated_execution_cost,agent_seconds,agent_hours,agent_rate,scope\r\n",
            SpendExport.EstimatesCsv(Period, [], [], [], new Dictionary<string, long>(), "usd"));

        // The summary always has its estimate row: an estimate that priced nothing is empty, never 0.
        var summary = Lines(SpendExport.SummaryCsv(Period, empty, [], []));
        Assert.Equal(
            "instance_id,period_start,period_end,measure,billing_type,fixed_or_variable,currency,amount,item_count,"
            + "agent_seconds,agent_hours,agent_rate,rate_source,scope",
            summary[0]);
        Assert.Equal(
            "instance-1,2031-03-01T00:00:00.000Z,2031-04-01T00:00:00.000Z,estimated_execution_cost,,,USD,,,0,0,,,",
            Assert.Single(summary.Skip(1)));

        // No column calls an estimate spend or cost alone, and none names a value word.
        foreach (var column in SpendExport.AccountColumns.Concat(SpendExport.ExpenseColumns).Concat(SpendExport.SummaryColumns).Concat(SpendExport.EstimateColumns))
        {
            Assert.DoesNotMatch("^(cost|spend|spent)$|value|roi|return|saving", column);
            if (column.Contains("cost", StringComparison.Ordinal)) Assert.StartsWith("estimated_", column);
            Assert.Matches("^[a-z][a-z0-9_]*$", column);
        }
    }

    [Fact]
    public void Formula_injection_cells_are_neutralised_and_quoting_is_RFC_4180()
    {
        Assert.Equal("'=SUM(A1:A2)", SpendExport.Neutral("=SUM(A1:A2)"));
        Assert.Equal("'+1", SpendExport.Neutral("+1"));
        Assert.Equal("'-cmd", SpendExport.Neutral("-cmd"));
        Assert.Equal("'@SUM(1)", SpendExport.Neutral("@SUM(1)"));
        Assert.Equal("'\tx", SpendExport.Neutral("\tx"));
        Assert.Equal("'\rx", SpendExport.Neutral("\rx"));
        Assert.Equal("-12.50", SpendExport.Neutral("-12.50"));
        Assert.Equal("40", SpendExport.Neutral("40"));
        Assert.Equal("'-1+2", SpendExport.Neutral("-1+2"));
        Assert.Equal("", SpendExport.Neutral(null));

        Assert.Equal("plain", SpendExport.Quoted("plain"));
        Assert.Equal("\"a,b\"", SpendExport.Quoted("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", SpendExport.Quoted("say \"hi\""));
        Assert.Equal("\"two\r\nlines\"", SpendExport.Quoted("two\r\nlines"));

        // Through a file: an account whose fields would run, hold commas, quotes and a line break.
        var account = new BillingAccount(
            "acc-1", "=HYPERLINK(\"http://x\",\"y\")", "Vendor, Inc.", "metered", "USD", "monthly", null,
            Instant.Stamp(March), null, true, "line one\nline two", "tenant", "@instance", "p@example.test",
            Instant.Stamp(March), Instant.Stamp(March));

        var rows = Parse(SpendExport.AccountsCsv(Period, [account]));
        Assert.Equal(2, rows.Count);
        var row = Row(rows, 1);
        Assert.Equal("'=HYPERLINK(\"http://x\",\"y\")", row["name"]);
        Assert.Equal("Vendor, Inc.", row["vendor"]);
        Assert.Equal("line one\nline two", row["description"]);
        Assert.Equal("'@instance", row["instance_id"]);
        Assert.Equal("variable", row["fixed_or_variable"]);
        Assert.Equal("true", row["is_active"]);
        Assert.Equal("", row["recurring_amount"]);
        Assert.Equal("2031-03-01T00:00:00.000Z", row["effective_from"]);
    }

    [Fact]
    public void The_expenses_file_carries_every_row_with_its_chain_and_counts_a_voided_or_replaced_expense_once()
    {
        var accounts = new[]
        {
            new BillingAccount("a", "Seats", "Anthropic", "subscription", "USD", "monthly", "30", Instant.Stamp(March.AddMonths(-2)),
                null, true, null, "tenant", "instance-1", "p@example.test", Instant.Stamp(March), Instant.Stamp(March)),
        };

        Expense Charge(string id, string amount, DateTimeOffset start, string status = "confirmed", string? supersedes = null, string? by = null) =>
            new(id, "a", amount, "USD", Instant.Stamp(start), Instant.Stamp(start.AddMonths(1)), "invoice", status, supersedes,
                null, null, "p@example.test", Instant.Stamp(start), by);

        var expenses = new[]
        {
            // Replaced 100 -> 120.50, both in March.
            Charge("1", "100", March, by: "2"),
            Charge("2", "120.50", March, supersedes: "1"),
            // Voided.
            Charge("3", "50", March.AddDays(2), by: "4"),
            Charge("4", "50", March.AddDays(2), status: "void", supersedes: "3"),
            // Recorded for February, replaced into March: the whole chain is in March's file.
            Charge("5", "-7", March.AddMonths(-1), by: "6"),
            Charge("6", "-7.25", March.AddDays(14), supersedes: "5"),
            // February only, nothing of it in March.
            Charge("7", "9", March.AddMonths(-1)),
            // Crosses into April but starts in March: whole in March.
            Charge("8", "40", March.AddDays(20)),
        };

        var rows = Parse(SpendExport.ExpensesCsv(Period, accounts, expenses));
        var byId = rows.Skip(1).Select((_, i) => Row(rows, i + 1)).ToDictionary(r => r["expense_id"]);

        Assert.Equal(["1", "2", "3", "4", "5", "6", "8"], byId.Keys);
        Assert.Equal(("2", "false", "false"), (byId["1"]["superseded_by_expense_id"], byId["1"]["is_current"], byId["1"]["counts_in_actual_spend"]));
        Assert.Equal(("1", "true", "true", "120.50"), (byId["2"]["supersedes_expense_id"], byId["2"]["is_current"], byId["2"]["counts_in_actual_spend"], byId["2"]["amount"]));
        Assert.Equal(("void", "3", "false"), (byId["4"]["status"], byId["4"]["supersedes_expense_id"], byId["4"]["counts_in_actual_spend"]));
        Assert.Equal(("false", "2031-02-01T00:00:00.000Z"), (byId["5"]["counts_in_actual_spend"], byId["5"]["service_period_start"]));
        Assert.Equal(("true", "-7.25"), (byId["6"]["counts_in_actual_spend"], byId["6"]["amount"]));
        Assert.Equal("true", byId["8"]["counts_in_actual_spend"]);
        Assert.Equal(("Seats", "Anthropic", "instance-1"), (byId["8"]["billing_account_name"], byId["8"]["vendor"], byId["8"]["instance_id"]));

        // What counts is exactly the overview's Actual Spend: each corrected expense once, by its current row.
        var counted = byId.Values.Where(r => r["counts_in_actual_spend"] == "true").Sum(r => decimal.Parse(r["amount"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Assert.Single(SpendOverview.ActualOf(expenses, March, April)).Amount, counted);
        Assert.Equal(153.25m, counted);
    }

    /// <summary>An RFC 4180 reader: fields split on commas, double-quoted fields with doubled quotes, CRLF records.</summary>
    internal static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
                i++;
            }
            else
            {
                Assert.NotEqual('"', c);
                field.Append(c);
            }
        }

        Assert.Empty(row);
        Assert.Equal(0, field.Length);
        return rows;
    }

    internal static Dictionary<string, string> Row(List<List<string>> rows, int index)
    {
        Assert.Equal(rows[0].Count, rows[index].Count);
        return rows[0].Zip(rows[index]).ToDictionary(p => p.First, p => p.Second);
    }

    private static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// THE SPEND PAGE'S CSV FILES through the Host: the period summary is the overview's own figures, the estimates
/// file adds up to the cost service by team and by outcome to the cent, and a machine principal is refused each
/// file with the humans-only sentence, a key holding every permit included.
/// </summary>
public sealed class SpendExportTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IOutcomeStore Outcomes => host.Services.GetRequiredService<IOutcomeStore>();

    private string Database => Path.Combine(host.DataRoot, "messages.db");

    private static readonly string[] Routes =
    [
        "/api/spend/export/billing-accounts", "/api/spend/export/expenses", "/api/spend/export/period-summary",
        "/api/spend/export/estimated-execution-cost",
    ];

    [Fact]
    public async Task The_period_summary_file_equals_the_overview_for_the_same_period_per_currency_and_converts_nothing()
    {
        using var person = await host.PersonAsync();
        var usd = await AccountAsync(person, "Seats", "subscription", "usd", 30);
        await AccountAsync(person, "Desk", "subscription", "usd", 12.5m);
        var eur = await AccountAsync(person, "Tokens", "metered", "eur", null);
        await ExpenseAsync(person, eur, "41.20", "EUR", "2034-02-03", "2034-03-03", "invoice");
        await ExpenseAsync(person, eur, "-1.05", "EUR", "2034-02-10", "2034-02-11", "adjustment");
        await ExpenseAsync(person, usd, "30", "USD", "2034-02-01", "2034-03-01", "subscription-charge");
        await RunAsync(NextCorrelation(), host.Alpha, new DateTimeOffset(2034, 2, 5, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2034, 2, 5, 10, 17, 3, TimeSpan.Zero), "61.13");

        const string period = "from=2034-02-01&to=2034-03-01";
        var overview = await JsonAsync(await person.GetAsync($"/api/spend/overview?{period}", Ct));
        var answer = await person.GetAsync($"/api/spend/export/period-summary?{period}", Ct);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("text/csv", answer.Content.Headers.ContentType?.MediaType);
        Assert.Equal("spend-period-summary-2034-02-01-to-2034-02-28.csv", answer.Content.Headers.ContentDisposition?.FileNameStar ?? answer.Content.Headers.ContentDisposition?.FileName);

        var rows = Rows(await answer.Content.ReadAsStringAsync(Ct));
        Assert.All(rows, r => Assert.Equal(("2034-02-01T00:00:00.000Z", "2034-03-01T00:00:00.000Z"), (r["period_start"], r["period_end"])));
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r["instance_id"])));

        // Actual Spend and Projected per currency, exactly as the overview says, one row each: nothing converted or summed across.
        static List<(string, decimal, int)> Of(JsonElement list) =>
            list.EnumerateArray().Select(a => (a.GetProperty("currency").GetString()!, a.GetProperty("amount").GetDecimal(), a.GetProperty("count").GetInt32())).ToList();
        List<(string, decimal, int)> Measure(string measure) =>
            rows.Where(r => r["measure"] == measure).Select(r => (r["currency"], Dec(r["amount"]), int.Parse(r["item_count"]))).ToList();

        Assert.Equal(Of(overview.GetProperty("actual")), Measure(SpendExport.MeasureActual));
        Assert.Equal([("EUR", 40.15m, 2), ("USD", 30m, 1)], Measure(SpendExport.MeasureActual));
        Assert.Equal(Of(overview.GetProperty("projected")), Measure(SpendExport.MeasureProjected));
        Assert.Contains(("USD", 12.5m, 1), Measure(SpendExport.MeasureProjected));

        // Fixed vs variable by billing type and currency, the overview's own rows.
        var types = overview.GetProperty("billingTypes").EnumerateArray().ToList();
        Assert.Equal(
            types.Where(t => t.GetProperty("actual").ValueKind == JsonValueKind.Number)
                .Select(t => (t.GetProperty("billingType").GetString(), t.GetProperty("group").GetString(), t.GetProperty("currency").GetString(), t.GetProperty("actual").GetDecimal())),
            rows.Where(r => r["measure"] == SpendExport.MeasureActualByType).Select(r => ((string?)r["billing_type"], (string?)r["fixed_or_variable"], (string?)r["currency"], Dec(r["amount"]))));
        Assert.Equal(
            types.Where(t => t.GetProperty("projected").ValueKind == JsonValueKind.Number)
                .Select(t => (t.GetProperty("billingType").GetString(), t.GetProperty("group").GetString(), t.GetProperty("currency").GetString(), t.GetProperty("projected").GetDecimal())),
            rows.Where(r => r["measure"] == SpendExport.MeasureProjectedByType).Select(r => ((string?)r["billing_type"], (string?)r["fixed_or_variable"], (string?)r["currency"], Dec(r["amount"]))));

        // Estimated Execution Cost and Agent Hours: the overview's, with its rate source.
        var estimate = overview.GetProperty("estimate");
        var row = Assert.Single(rows, r => r["measure"] == SpendExport.MeasureEstimate);
        Assert.Equal(estimate.GetProperty("cost").GetDecimal(), Dec(row["amount"]));
        Assert.Equal(estimate.GetProperty("agentSeconds").GetDouble(), double.Parse(row["agent_seconds"], System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.Equal(estimate.GetProperty("agentRate").GetDecimal(), Dec(row["agent_rate"]));
        Assert.Equal(("USD", "member"), (row["currency"], row["rate_source"]));
        Assert.Equal(ExecutionCost.Price(4623, 61.13m), Dec(row["amount"]));
    }

    [Fact]
    public async Task The_estimates_file_equals_the_cost_service_by_team_and_by_outcome_to_the_cent_with_rate_source()
    {
        using var person = await host.PersonAsync();
        var outcome = (await (await person.PostAsJsonAsync("/api/outcomes", new { name = $"Export {Guid.NewGuid():N}" }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var from = new DateTimeOffset(2035, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddMonths(1);

        var linked = NextCorrelation();
        Assert.True((await Outcomes.LinkAsync(linked, outcome, host.Alpha, new OutcomeActor("person@example.test", OutcomeActorKind.Person),
            OutcomeLinkHow.Person, agentRule: false,
            new TriggerAudit(null, "person@example.test", TenantActions.WorkflowOutcomeChanged, null, null, null), Ct)).Ok);

        // Alpha: one linked run starting before the period (only its part inside counts), a tenant-rate run, a plugin.
        await RunAsync(linked, host.Alpha, from.AddSeconds(-1237), from.AddSeconds(4321), "47.37");
        await RunAsync(NextCorrelation(), host.Alpha, from.AddDays(3).AddSeconds(17), from.AddDays(3).AddSeconds(7777), "33.33", source: "tenant");
        await RunAsync(NextCorrelation(), host.Alpha, from.AddDays(4), from.AddDays(4).AddSeconds(1800), "20", kind: "plugin");
        await RunAsync(NextCorrelation(), host.Alpha, from.AddDays(5), from.AddDays(5).AddSeconds(900), null, kind: "plugin", source: "none");
        // Beta: one run ending after the period.
        await RunAsync(NextCorrelation(), host.Beta, to.AddSeconds(-999), to.AddSeconds(501), "71.11");

        var answer = await person.GetAsync("/api/spend/export/estimated-execution-cost?from=2035-07-01&to=2035-08-01", Ct);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var rows = Rows(await answer.Content.ReadAsStringAsync(Ct));

        // BY TEAM, priced by hand: each run's seconds inside the period at its rate, to the cent.
        var alpha = rows.Where(r => r["rollup"] == SpendExport.RollupTeam && r["team_id"] == host.Alpha).ToList();
        Assert.Equal(["member", "tenant", "none"], alpha.Select(r => r["rate_source"]));
        var member = alpha[0];
        Assert.Equal(ExecutionCost.Price(4321, 47.37m) + ExecutionCost.Price(1800, 20m), Dec(member["estimated_execution_cost"]));
        Assert.Equal((ExecutionCost.Price(4321, 47.37m), ExecutionCost.Price(1800, 20m)), (Dec(member["estimated_agent_cost"]), Dec(member["estimated_plugin_cost"])));
        Assert.Equal(("4321", "1.2003", "47.37"), (member["agent_seconds"], member["agent_hours"], member["agent_rate"]));
        Assert.Equal((ExecutionCost.Price(7760, 33.33m), ""), (Dec(alpha[1]["estimated_execution_cost"]), alpha[1]["estimated_plugin_cost"]));
        Assert.Equal(("", "", "", "0"), (alpha[2]["estimated_agent_cost"], alpha[2]["estimated_plugin_cost"], alpha[2]["estimated_execution_cost"], alpha[2]["agent_seconds"]));
        var beta = Assert.Single(rows, r => r["rollup"] == SpendExport.RollupTeam && r["team_id"] == host.Beta);
        Assert.Equal((ExecutionCost.Price(999, 71.11m), "USD"), (Dec(beta["estimated_execution_cost"]), beta["currency"]));

        // ...the same as ExecutionCost's own tally of each team's runs, as every team figure reads them.
        var runs = ExecutionCost.Of(await Outcomes.ReadExecutionRunsAsync(from, to, pricedOnly: false, Ct), await Outcomes.ListAsync(Ct), "usd");
        var teams = host.Services.GetRequiredService<TeamRegistry>();
        var floors = new[] { host.Alpha, host.Beta }.ToDictionary(t => t, teams.FloorFor, StringComparer.OrdinalIgnoreCase);
        foreach (var (team, mine) in TeamCost.RunsByTeam(runs, floors, from, to))
        {
            var tally = new ExecutionCost.Tally("usd");
            foreach (var run in mine) tally.AddSpan(run, run.SecondsIn(from, to));
            var listed = rows.Where(r => r["rollup"] == SpendExport.RollupTeam && r["team_id"] == team).ToList();
            Assert.Equal(tally.Cost, listed.Sum(r => DecOrZero(r["estimated_execution_cost"])));
            Assert.Equal(tally.AgentSeconds, listed.Sum(r => double.Parse(r["agent_seconds"], System.Globalization.CultureInfo.InvariantCulture)), 3);
        }

        // BY OUTCOME: the O1 rollup - GET /api/outcomes for the same window, every outcome and Unattributed - to the cent.
        var window = $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        var list = await JsonAsync(await person.GetAsync($"/api/outcomes?{window}", Ct));
        static decimal Amount(JsonElement figures) =>
            figures.GetProperty("cost").GetProperty("amount") is { ValueKind: JsonValueKind.Number } n ? n.GetDecimal() : 0m;

        var byOutcome = rows.Where(r => r["rollup"] == SpendExport.RollupOutcome).ToList();
        decimal Sum(string id) => byOutcome.Where(r => r["outcome_id"] == id).Sum(r => DecOrZero(r["estimated_execution_cost"]));

        foreach (var o in list.GetProperty("outcomes").EnumerateArray())
        {
            Assert.Equal(Amount(o.GetProperty("figures")), Sum(o.GetProperty("id").GetString()!));
        }

        var unattributed = byOutcome.Where(r => r["outcome_id"] == "").ToList();
        Assert.All(unattributed, r => Assert.Equal(OutcomeFigures.UnattributedName, r["outcome_name"]));
        Assert.Equal(Amount(list.GetProperty("noOutcome").GetProperty("figures")), Sum(""));
        Assert.Equal(ExecutionCost.Price(4321, 47.37m), Sum(outcome));
        Assert.Equal(["member"], byOutcome.Where(r => r["outcome_id"] == outcome).Select(r => r["rate_source"]));
        Assert.Equal(OutcomeFigures.UnattributedName, byOutcome[0]["outcome_name"]);

        // ExecutionCost.CostsOf for the same window agrees, and the overview's estimate is every outcome's rows added.
        var costs = ExecutionCost.CostsOf(runs, from, to, "usd");
        Assert.Equal(costs.Of(outcome), Sum(outcome));
        Assert.Equal(costs.Unattributed ?? 0, Sum(""));
        var overview = await JsonAsync(await person.GetAsync("/api/spend/overview?from=2035-07-01&to=2035-08-01", Ct));
        Assert.Equal(overview.GetProperty("estimate").GetProperty("cost").GetDecimal(), byOutcome.Sum(r => DecOrZero(r["estimated_execution_cost"])));
    }

    [Fact]
    public async Task The_billing_accounts_and_expenses_files_carry_every_account_and_every_row_of_the_period()
    {
        using var person = await host.PersonAsync();
        var account = await AccountAsync(person, "=Formula", "service", "gbp", null);
        var inactive = await AccountAsync(person, "Retired", "metered", "usd", null);
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsync($"/api/billing-accounts/{inactive}/deactivate", null, Ct)).StatusCode);

        var first = await ExpenseAsync(person, account, "12.345", "GBP", "2036-04-02", "2036-05-02", "invoice");
        var replaced = await person.PostAsJsonAsync($"/api/expenses/{first}/replace",
            new { accountId = account, amount = "12.35", currency = "GBP", servicePeriodStart = "2036-04-02", servicePeriodEnd = "2036-05-02", kind = "invoice" }, Ct);
        Assert.Equal(HttpStatusCode.Created, replaced.StatusCode);

        var accounts = Rows(await (await person.GetAsync("/api/spend/export/billing-accounts?from=2036-04-01&to=2036-05-01", Ct)).Content.ReadAsStringAsync(Ct));
        Assert.Equal("false", Assert.Single(accounts, r => r["billing_account_id"] == inactive)["is_active"]);
        Assert.StartsWith("'=Formula", Assert.Single(accounts, r => r["billing_account_id"] == account)["name"]);

        var expenses = Rows(await (await person.GetAsync("/api/spend/export/expenses?from=2036-04-01&to=2036-05-01", Ct)).Content.ReadAsStringAsync(Ct));
        var mine = expenses.Where(r => r["billing_account_id"] == account).ToList();
        Assert.Equal(2, mine.Count);
        Assert.Equal(("12.345", "false"), (mine[0]["amount"], mine[0]["counts_in_actual_spend"]));
        Assert.Equal(("12.35", "true", first), (mine[1]["amount"], mine[1]["counts_in_actual_spend"], mine[1]["supersedes_expense_id"]));

        // A bad period is one sentence, for every file.
        foreach (var route in Routes)
        {
            var refused = await person.GetAsync($"{route}?from=2036-05-01&to=2036-04-01", Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(SpendOverview.PeriodRefused, (await JsonAsync(refused)).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task A_machine_principal_without_the_Spend_permit_is_refused_each_export_even_holding_every_permit_and_a_stored_Spend()
    {
        using var person = await host.PersonAsync();
        foreach (var route in Routes) Assert.Equal(HttpStatusCode.OK, (await person.GetAsync(route, Ct)).StatusCode);

        var principals = host.Services.GetRequiredService<IPrincipalStore>();
        var apiKey = await principals.MintAsync($"apikey-{Guid.NewGuid():N}", PrincipalKind.ApiKey, null, Permits.All, ct: Ct);
        var manager = await principals.MintAsync(
            new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, host.Alpha,
            new HashSet<string>(Permits.All) { Permits.Spend }, ct: Ct);
        var concierge = await principals.MintAsync($"console:{Guid.NewGuid():N}", PrincipalKind.Concierge, host.Alpha, Permits.All, ct: Ct);

        foreach (var key in new[] { apiKey, manager, concierge, host.AlphaContainerKey })
        {
            using var machine = host.Container(key);
            foreach (var route in Routes.SelectMany(r => new[] { r, $"{r}?from=2031-01-01&to=2031-02-01" }))
            {
                var answer = await machine.GetAsync(route, Ct);
                Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
                Assert.Equal(SpendReading.Refused, (await JsonAsync(answer)).GetProperty("error").GetString());
            }
        }
    }

    private static List<Dictionary<string, string>> Rows(string csv)
    {
        var parsed = SpendExportRuleTests.Parse(csv);
        return Enumerable.Range(1, parsed.Count - 1).Select(i => SpendExportRuleTests.Row(parsed, i)).ToList();
    }

    private static decimal Dec(string text) => decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static decimal DecOrZero(string text) => text.Length == 0 ? 0m : Dec(text);

    private async Task<string> AccountAsync(HttpClient person, string name, string type, string currency, decimal? recurring)
    {
        var created = await person.PostAsJsonAsync(
            "/api/billing-accounts",
            new { name = $"{name} {Guid.NewGuid():N}", vendor = name, billingType = type, currency, billingPeriod = "monthly", recurringAmount = recurring, effectiveFrom = "2034-01-01" },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await JsonAsync(created)).GetProperty("id").GetString()!;
    }

    private async Task<string> ExpenseAsync(HttpClient person, string account, string amount, string currency, string start, string end, string kind)
    {
        var recorded = await person.PostAsJsonAsync(
            "/api/expenses", new { accountId = account, amount, currency, servicePeriodStart = start, servicePeriodEnd = end, kind }, Ct);
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        return (await JsonAsync(recorded)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static long _correlation = 18_000_000_000 + Random.Shared.Next(1_000_000);

    private static long NextCorrelation() => Interlocked.Add(ref _correlation, 1000);

    private static long _run = 19_000_000_000 + Random.Shared.Next(1_000_000);

    private async Task RunAsync(long correlation, string team, DateTimeOffset started, DateTimeOffset ended, string? rate, string kind = "agent", string source = "member")
    {
        var start = Interlocked.Increment(ref _run);
        await ExecuteAsync(
            """
            INSERT INTO execution_snapshots (start_seq, correlation, team_id, team_name, member, member_kind,
                rate_amount, rate_currency, rate_source, link_id, started_at)
            VALUES ($start, $c, $team, $team, 'Dev', $kind, $rate, 'USD', $source,
                (SELECT MAX(id) FROM workflow_outcome_links WHERE correlation = $c), $started)
            """,
            ("$start", start), ("$c", correlation), ("$team", team), ("$kind", kind), ("$rate", (object?)rate ?? DBNull.Value),
            ("$source", source), ("$started", started.ToString("O")));

        await ExecuteAsync(
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, member_kind, run_outcome,
                started_at, ended_at, measured, tokens_in, tokens_out, billable, start_seq)
            VALUES ($seq, $c, $team, $team, 'Dev', $kind, 'completed', $started, $ended, 1, 10, 0, 10, $start)
            """,
            ("$seq", Interlocked.Increment(ref _run)), ("$c", correlation), ("$team", team), ("$kind", kind),
            ("$started", started.ToString("O")), ("$ended", ended.ToString("O")), ("$start", start));
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
