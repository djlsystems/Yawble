using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// THE SPEND PAGE'S RULES, on the rows alone: Actual Spend is the current confirmed expenses whose service
/// period starts in the period, whole, per currency and never converted; Projected is apart and drops once
/// a confirmed expense covers it; neither is inferred from, or added to, an estimate.
/// </summary>
public sealed class SpendOverviewRuleTests
{
    private static readonly DateTimeOffset March = new(2031, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset April = March.AddMonths(1);

    private static BillingAccount Account(string id, string currency = "USD", string type = "subscription", string? recurring = null, bool active = true) =>
        new(id, $"Account {id}", type == "subscription" ? "Anthropic" : "OpenAI", type, currency, "monthly", recurring,
            Instant.Stamp(March.AddMonths(-2)), null, active, null, "tenant", "instance", "p@example.test",
            Instant.Stamp(March.AddMonths(-2)), Instant.Stamp(March.AddMonths(-2)));

    private static Expense Row(
        string id, string account, string amount, string currency = "USD", DateTimeOffset? start = null, DateTimeOffset? end = null,
        string status = "confirmed", string? supersedes = null, string? supersededBy = null, string kind = "invoice") =>
        new(id, account, amount, currency, Instant.Stamp(start ?? March), Instant.Stamp(end ?? April), kind, status,
            supersedes, null, null, "p@example.test", Instant.Stamp(March), supersededBy);

    private static SpendOverview.Overview Of(IReadOnlyList<BillingAccount> accounts, IReadOnlyList<Expense> expenses) =>
        SpendOverview.Of(accounts, expenses, [], "USD", March, April);

    [Fact]
    public void Actual_Spend_counts_only_current_confirmed_expenses_per_currency_and_never_converts()
    {
        var accounts = new[] { Account("a", "USD"), Account("b", "EUR", "metered") };
        var expenses = new[]
        {
            Row("1", "a", "100"),
            Row("2", "a", "-12.5", kind: "adjustment"),
            Row("3", "b", "40", "EUR"),
            Row("4", "b", "9.99", "EUR", kind: "subscription-charge"),
        };

        var overview = Of(accounts, expenses);

        Assert.Equal(
            [("EUR", 49.99m, 2), ("USD", 87.5m, 2)],
            overview.Actual.Select(a => (a.Currency, a.Amount, a.Count)));
        Assert.Equal(
            [("Anthropic", "USD", (decimal?)87.5m), ("OpenAI", "EUR", (decimal?)49.99m)],
            overview.Vendors.Select(v => (v.Vendor, v.Currency, v.Actual)));
        Assert.Equal(
            [("subscription", SpendOverview.Fixed, "USD", (decimal?)87.5m), ("metered", SpendOverview.Variable, "EUR", (decimal?)49.99m)],
            overview.BillingTypes.Select(t => (t.BillingType, t.Group, t.Currency, t.Actual)));
    }

    [Fact]
    public void A_voided_or_replaced_expense_is_counted_once_by_its_current_row()
    {
        var accounts = new[] { Account("a") };
        var expenses = new[]
        {
            // Replaced: 100 -> 120. Only 120 counts.
            Row("1", "a", "100", supersededBy: "2"),
            Row("2", "a", "120", supersedes: "1"),
            // Voided: 50 -> void. Nothing counts.
            Row("3", "a", "50", supersededBy: "4"),
            Row("4", "a", "50", status: "void", supersedes: "3"),
            // Replaced, then the replacement voided: nothing counts.
            Row("5", "a", "70", supersededBy: "6"),
            Row("6", "a", "75", supersedes: "5", supersededBy: "7"),
            Row("7", "a", "75", status: "void", supersedes: "6"),
        };

        var actual = Assert.Single(Of(accounts, expenses).Actual);

        Assert.Equal(("USD", 120m, 1), (actual.Currency, actual.Amount, actual.Count));
    }

    [Fact]
    public void Projected_charges_are_never_in_Actual_Spend_and_drop_once_a_confirmed_expense_covers_the_period()
    {
        var uncovered = Account("sub-open", recurring: "30");
        var covered = Account("sub-paid", recurring: "20");
        var voidedCover = Account("sub-void", recurring: "10");
        var accounts = new[] { uncovered, covered, voidedCover };
        var expenses = new[]
        {
            Row("1", "sub-paid", "20", kind: "subscription-charge"),
            Row("2", "sub-void", "10", kind: "subscription-charge", supersededBy: "3"),
            Row("3", "sub-void", "10", status: "void", supersedes: "2"),
        };

        var overview = Of(accounts, expenses);

        // Only the paid account's charge is Actual Spend; the projections are the other two, apart.
        Assert.Equal([("USD", 20m, 1)], overview.Actual.Select(a => (a.Currency, a.Amount, a.Count)));
        Assert.Equal([("USD", 40m, 2)], overview.Projected.Select(p => (p.Currency, p.Amount, p.Count)));
        Assert.Equal(["sub-open", "sub-void"], overview.Projections.Select(p => p.AccountId));
        Assert.DoesNotContain(overview.Projections, p => p.AccountId == "sub-paid");

        var lines = overview.Accounts.ToDictionary(a => a.AccountId);
        Assert.Equal(((decimal?)null, (decimal?)30m), (lines["sub-open"].Actual, lines["sub-open"].Projected));
        Assert.Equal(((decimal?)20m, (decimal?)null), (lines["sub-paid"].Actual, lines["sub-paid"].Projected));

        // With no expense at all there is no Actual Spend - an empty list, never an estimate or a projection in its place.
        var none = Of(accounts, []);
        Assert.Empty(none.Actual);
        Assert.Equal(60m, Assert.Single(none.Projected).Amount);
    }

    [Fact]
    public void An_expense_crossing_the_period_edge_is_counted_whole_in_the_period_its_service_period_starts()
    {
        var accounts = new[] { Account("a", type: "metered") };

        // 15 March to 15 April: whole in March, nothing in April. 15 February to 15 March: whole in February.
        var expenses = new[]
        {
            Row("1", "a", "90", start: March.AddDays(14), end: April.AddDays(14)),
            Row("2", "a", "31", start: March.AddMonths(-1).AddDays(14), end: March.AddDays(14)),
        };

        Assert.Equal(90m, Assert.Single(SpendOverview.ActualOf(expenses, March, April)).Amount);
        Assert.Empty(SpendOverview.ActualOf(expenses, April, April.AddMonths(1)));
        Assert.Equal(31m, Assert.Single(SpendOverview.ActualOf(expenses, March.AddMonths(-1), March)).Amount);

        // A custom range over both months counts each once, never split by days.
        Assert.Equal(121m, Assert.Single(SpendOverview.ActualOf(expenses, March.AddMonths(-1), April.AddMonths(1))).Amount);

        // And the trend puts each in its start month.
        var months = SpendOverview.Of(accounts, expenses, [], "USD", March, April).Months.ToDictionary(m => m.MonthStart);
        Assert.Equal(90m, Assert.Single(months["2031-03-01"].Actual).Amount);
        Assert.Equal(31m, Assert.Single(months["2031-02-01"].Actual).Amount);
    }

    [Fact]
    public void The_period_is_the_current_UTC_month_by_default_and_a_custom_range_is_two_dates()
    {
        var now = new DateTimeOffset(2031, 3, 17, 23, 30, 0, TimeSpan.FromHours(-5));

        // 17 March 23:30 at -5 is 18 March 04:30 UTC: March.
        Assert.Equal((March, April), SpendOverview.PeriodOf(null, null, now));
        Assert.Equal((new DateTimeOffset(2031, 3, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2031, 4, 1, 0, 0, 0, TimeSpan.Zero)),
            SpendOverview.PeriodOf(null, null, new DateTimeOffset(2031, 3, 31, 23, 59, 0, TimeSpan.Zero)));
        Assert.Equal((March.AddDays(4), March.AddDays(20)), SpendOverview.PeriodOf("2031-03-05", "2031-03-21", now));

        Assert.Null(SpendOverview.PeriodOf("2031-03-05", null, now));
        Assert.Null(SpendOverview.PeriodOf("2031-03-05", "2031-03-05", now));
        Assert.Null(SpendOverview.PeriodOf("March", "2031-04-01", now));

        // The trend: six months to the period's last month, back to its first when it is longer.
        Assert.Equal(6, SpendOverview.TrendOf(March, April).Count);
        Assert.Equal(new DateTimeOffset(2030, 10, 1, 0, 0, 0, TimeSpan.Zero), SpendOverview.TrendOf(March, April)[0]);
        Assert.Equal(10, SpendOverview.TrendOf(March.AddMonths(-9), April).Count);
        Assert.Equal(SpendOverview.TrendLimit, SpendOverview.TrendOf(March.AddYears(-5), April).Count);
    }
}

/// <summary>
/// THE SPEND PAGE'S READS through the Host: the overview's Estimated Execution Cost is the existing cost
/// service's for the same period, the currencies stay apart, and a machine principal is refused each new
/// read with the humans-only sentence, a key holding every permit included.
/// </summary>
public sealed class SpendOverviewTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IOutcomeStore Outcomes => host.Services.GetRequiredService<IOutcomeStore>();

    private string Database => Path.Combine(host.DataRoot, "messages.db");

    [Fact]
    public async Task Estimated_Execution_Cost_on_the_page_equals_the_existing_cost_service_for_the_same_period()
    {
        using var person = await host.PersonAsync();
        var outcome = (await (await person.PostAsJsonAsync("/api/outcomes", new { name = $"Spend page {Guid.NewGuid():N}" }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        var team = host.Alpha;
        var from = new DateTimeOffset(2032, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddMonths(1);

        // Odd seconds at odd rates; one run starts before the period and one ends after it, so each is priced
        // by its part inside; one serves an outcome, the rest none.
        var linked = NextCorrelation();
        Assert.True((await Outcomes.LinkAsync(linked, outcome, team, new OutcomeActor("person@example.test", OutcomeActorKind.Person),
            OutcomeLinkHow.Person, agentRule: false,
            new TriggerAudit(null, "person@example.test", TenantActions.WorkflowOutcomeChanged, null, null, null), Ct)).Ok);
        await RunAsync(linked, team, from.AddSeconds(-1237), from.AddSeconds(4321), "47.37");
        await RunAsync(NextCorrelation(), team, from.AddDays(3).AddSeconds(17), from.AddDays(3).AddSeconds(7777), "33.33");
        await RunAsync(NextCorrelation(), team, to.AddSeconds(-999), to.AddSeconds(501), "71.11");
        await RunAsync(NextCorrelation(), team, from.AddDays(9), from.AddDays(9).AddSeconds(600), null, kind: "plugin");

        var overview = await JsonAsync(await person.GetAsync("/api/spend/overview?from=2032-05-01&to=2032-06-01", Ct));
        var estimate = overview.GetProperty("estimate");

        // THE EXISTING COST SERVICE: every outcome plus Unattributed for the same window, as GET /api/outcomes answers it.
        var window = $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        var list = await JsonAsync(await person.GetAsync($"/api/outcomes?{window}", Ct));
        static decimal Amount(JsonElement figures) =>
            figures.GetProperty("cost").GetProperty("amount") is { ValueKind: JsonValueKind.Number } n ? n.GetDecimal() : 0m;
        var service = list.GetProperty("outcomes").EnumerateArray().Sum(o => Amount(o.GetProperty("figures")))
            + Amount(list.GetProperty("noOutcome").GetProperty("figures"));

        // ...and ExecutionCost itself, each run's part in the window priced on its own.
        var runs = ExecutionCost.Of(await Outcomes.ReadExecutionRunsAsync(from, to, pricedOnly: false, Ct), await Outcomes.ListAsync(Ct), "USD");
        var tally = new ExecutionCost.Tally("USD");
        foreach (var run in runs) tally.AddSpan(run, run.SecondsIn(from, to));

        Assert.True(service > 0);
        Assert.Equal(service, estimate.GetProperty("cost").GetDecimal());
        Assert.Equal(tally.Cost, estimate.GetProperty("cost").GetDecimal());
        Assert.Equal(tally.AgentSeconds, estimate.GetProperty("agentSeconds").GetDouble(), 3);
        Assert.Equal(tally.AgentRate, estimate.GetProperty("agentRate").GetDecimal());
        Assert.Equal("USD", estimate.GetProperty("currency").GetString());

        // The trend's month for the period says the same, and there is no Actual Spend: the estimate is not one.
        var month = overview.GetProperty("months").EnumerateArray().Single(m => m.GetProperty("monthStart").GetString() == "2032-05-01");
        Assert.Equal(service, month.GetProperty("estimate").GetProperty("cost").GetDecimal());
        Assert.Equal(0, overview.GetProperty("actual").GetArrayLength());
        Assert.Equal(0, month.GetProperty("actual").GetArrayLength());
    }

    [Fact]
    public async Task The_overview_keeps_two_currencies_apart_and_its_projection_drops_once_a_charge_is_recorded()
    {
        using var person = await host.PersonAsync();
        var usd = await AccountAsync(person, "Seats", "subscription", "usd", 30);
        var eur = await AccountAsync(person, "Tokens", "metered", "eur", null);
        await ExpenseAsync(person, eur, "41.20", "EUR", "2033-02-03", "2033-03-03", "invoice");

        var before = await JsonAsync(await person.GetAsync("/api/spend/overview?from=2033-02-01&to=2033-03-01", Ct));
        Assert.Equal([("EUR", 41.2m)], before.GetProperty("actual").EnumerateArray().Select(a => (a.GetProperty("currency").GetString(), a.GetProperty("amount").GetDecimal())));
        Assert.Equal([("USD", 30m)], before.GetProperty("projected").EnumerateArray().Select(a => (a.GetProperty("currency").GetString(), a.GetProperty("amount").GetDecimal())));

        await ExpenseAsync(person, usd, "30", "USD", "2033-02-01", "2033-03-01", "subscription-charge");

        var after = await JsonAsync(await person.GetAsync("/api/spend/overview?from=2033-02-01&to=2033-03-01", Ct));
        Assert.Equal(
            [("EUR", 41.2m), ("USD", 30m)],
            after.GetProperty("actual").EnumerateArray().Select(a => (a.GetProperty("currency").GetString(), a.GetProperty("amount").GetDecimal())));
        Assert.Equal(0, after.GetProperty("projected").GetArrayLength());

        // A bad period is one sentence, nothing else.
        var refused = await person.GetAsync("/api/spend/overview?from=2033-03-01&to=2033-02-01", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(SpendOverview.PeriodRefused, (await JsonAsync(refused)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_machine_principal_without_the_Spend_permit_is_refused_each_spend_page_read_even_holding_every_permit_and_a_stored_Spend()
    {
        using var person = await host.PersonAsync();
        Assert.Equal(HttpStatusCode.OK, (await person.GetAsync("/api/spend/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await person.GetAsync("/api/spend/linked-members", Ct)).StatusCode);

        var principals = host.Services.GetRequiredService<IPrincipalStore>();
        var apiKey = await principals.MintAsync($"apikey-{Guid.NewGuid():N}", PrincipalKind.ApiKey, null, Permits.All, ct: Ct);
        // A KEY MINTED WITH SPEND holds nothing more: the permit is set at authentication, never read from the row.
        var manager = await principals.MintAsync(
            new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, host.Alpha,
            new HashSet<string>(Permits.All) { Permits.Spend }, ct: Ct);

        foreach (var key in new[] { apiKey, manager, host.AlphaContainerKey })
        {
            using var machine = host.Container(key);
            foreach (var route in new[] { "/api/spend/overview", "/api/spend/overview?from=2031-01-01&to=2031-02-01", "/api/spend/linked-members" })
            {
                var answer = await machine.GetAsync(route, Ct);
                Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
                Assert.Equal(SpendReading.Refused, (await JsonAsync(answer)).GetProperty("error").GetString());
            }
        }
    }

    [Fact]
    public async Task The_members_linked_to_an_account_are_listed_read_only()
    {
        using var person = await host.PersonAsync();
        var account = await AccountAsync(person, "Linked", "service", "usd", null);
        var member = "Linked";
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/containers", new { name = member, systemPrompt = "Work." }, Ct)).StatusCode);

        var patched = await person.PatchAsJsonAsync($"/api/teams/{host.Alpha}/containers/{member}", new { billingAccountId = account }, Ct);
        Assert.True(patched.IsSuccessStatusCode, await patched.Content.ReadAsStringAsync(Ct));

        var linked = (await JsonAsync(await person.GetAsync("/api/spend/linked-members", Ct))).EnumerateArray()
            .Where(l => l.GetProperty("accountId").GetString() == account).ToList();

        var only = Assert.Single(linked);
        Assert.Equal((host.Alpha, member), (only.GetProperty("team").GetString(), only.GetProperty("member").GetString()));
    }

    /// <summary>
    /// MANY MEMBERS ON ONE SUBSCRIPTION: three members of two teams share one account, and its recurring amount is
    /// projected once for the period and, once charged, counted once in Actual Spend - never once per member.
    /// </summary>
    [Fact]
    public async Task Members_sharing_one_subscription_never_multiply_its_projection_or_its_charge()
    {
        using var person = await host.PersonAsync();
        var account = await AccountAsync(person, "Shared seats", "subscription", "nok", 20);
        var members = new[] { (host.Alpha, "SeatOne"), (host.Alpha, "SeatTwo"), (host.Beta, "SeatThree") };
        foreach (var (team, member) in members)
        {
            Assert.Equal(HttpStatusCode.OK, (await person.PostAsJsonAsync($"/api/teams/{team}/containers", new { name = member, systemPrompt = "Work." }, Ct)).StatusCode);
            var patched = await person.PatchAsJsonAsync($"/api/teams/{team}/containers/{member}", new { billingAccountId = account }, Ct);
            Assert.True(patched.IsSuccessStatusCode, await patched.Content.ReadAsStringAsync(Ct));
        }

        var linked = (await JsonAsync(await person.GetAsync("/api/spend/linked-members", Ct))).EnumerateArray()
            .Where(l => l.GetProperty("accountId").GetString() == account)
            .Select(l => (l.GetProperty("team").GetString()!, l.GetProperty("member").GetString()!))
            .Order().ToList();
        Assert.Equal(members.Order(), linked);

        const string Period = "/api/spend/overview?from=2033-05-01&to=2033-06-01";
        static IEnumerable<(string?, decimal)> Nok(JsonElement amounts) => amounts.EnumerateArray()
            .Where(a => a.GetProperty("currency").GetString() == "NOK")
            .Select(a => (a.GetProperty("currency").GetString(), a.GetProperty("amount").GetDecimal()));

        var before = await JsonAsync(await person.GetAsync(Period, Ct));
        Assert.Equal([("NOK", 20m)], Nok(before.GetProperty("projected")));
        Assert.Empty(Nok(before.GetProperty("actual")));
        Assert.Single(before.GetProperty("projections").EnumerateArray(), p => p.GetProperty("accountId").GetString() == account);
        var line = Assert.Single(before.GetProperty("accounts").EnumerateArray(), a => a.GetProperty("accountId").GetString() == account);
        Assert.Equal(20m, line.GetProperty("projected").GetDecimal());

        await ExpenseAsync(person, account, "20", "NOK", "2033-05-01", "2033-06-01", "subscription-charge");

        var after = await JsonAsync(await person.GetAsync(Period, Ct));
        Assert.Equal([("NOK", 20m)], Nok(after.GetProperty("actual")));
        Assert.Empty(Nok(after.GetProperty("projected")));
        line = Assert.Single(after.GetProperty("accounts").EnumerateArray(), a => a.GetProperty("accountId").GetString() == account);
        Assert.Equal((20m, 1), (line.GetProperty("actual").GetDecimal(), line.GetProperty("expenses").GetInt32()));
    }

    private async Task<string> AccountAsync(HttpClient person, string name, string type, string currency, decimal? recurring)
    {
        var created = await person.PostAsJsonAsync(
            "/api/billing-accounts",
            new { name = $"{name} {Guid.NewGuid():N}", vendor = name, billingType = type, currency, billingPeriod = "monthly", recurringAmount = recurring, effectiveFrom = "2033-01-01" },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await JsonAsync(created)).GetProperty("id").GetString()!;
    }

    private async Task ExpenseAsync(HttpClient person, string account, string amount, string currency, string start, string end, string kind)
    {
        var recorded = await person.PostAsJsonAsync(
            "/api/expenses", new { accountId = account, amount, currency, servicePeriodStart = start, servicePeriodEnd = end, kind }, Ct);
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static long _correlation = 8_000_000_000 + Random.Shared.Next(1_000_000);

    private static long NextCorrelation() => Interlocked.Add(ref _correlation, 1000);

    private static long _run = 9_000_000_000 + Random.Shared.Next(1_000_000);

    private async Task RunAsync(long correlation, string team, DateTimeOffset started, DateTimeOffset ended, string? rate, string kind = "agent")
    {
        var start = Interlocked.Increment(ref _run);
        await ExecuteAsync(
            """
            INSERT INTO execution_snapshots (start_seq, correlation, team_id, team_name, member, member_kind,
                rate_amount, rate_currency, rate_source, link_id, started_at)
            VALUES ($start, $c, $team, $team, 'Dev', $kind, $rate, 'USD', 'member',
                (SELECT MAX(id) FROM workflow_outcome_links WHERE correlation = $c), $started)
            """,
            ("$start", start), ("$c", correlation), ("$team", team), ("$kind", kind), ("$rate", (object?)rate ?? DBNull.Value),
            ("$started", started.ToString("O")));

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
