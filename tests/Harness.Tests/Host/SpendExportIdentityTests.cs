using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// THE SPEND CSV FILES ARE READY TO BE PUT SIDE BY SIDE WITH ANOTHER INSTANCE'S, on the rows alone: every file
/// keeps its columns in name and order and appends <c>scope</c> (and, on expenses, the account's instance) last;
/// every header carries <c>instance_id</c>, <c>scope</c> and its ids; and a shared account's expense exported by two
/// instances is the same row by (<c>billing_account_instance_id</c>, <c>billing_account_id</c>, <c>expense_id</c>).
/// Nothing here aggregates two instances.
/// </summary>
public sealed class SpendExportIdentityRuleTests
{
    private static readonly DateTimeOffset March = new(2031, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset April = March.AddMonths(1);

    /// <summary>Each file's columns as they were before <c>scope</c> was appended, which must never change.</summary>
    private static readonly (IReadOnlyList<string> Columns, string[] Before, string[] Appended)[] Files =
    [
        (SpendExport.AccountColumns,
            ["instance_id", "period_start", "period_end", "billing_account_id", "name", "vendor", "billing_type",
                "fixed_or_variable", "currency", "billing_period", "recurring_amount", "effective_from", "effective_to",
                "is_active", "description", "created_by", "created_at", "updated_at"],
            ["scope"]),
        (SpendExport.ExpenseColumns,
            ["instance_id", "period_start", "period_end", "expense_id", "billing_account_id", "billing_account_name", "vendor",
                "kind", "status", "amount", "currency", "service_period_start", "service_period_end", "supersedes_expense_id",
                "superseded_by_expense_id", "is_current", "counts_in_actual_spend", "reference", "note", "recorded_by",
                "recorded_at"],
            ["scope", "billing_account_instance_id"]),
        (SpendExport.SummaryColumns,
            ["instance_id", "period_start", "period_end", "measure", "billing_type", "fixed_or_variable", "currency",
                "amount", "item_count", "agent_seconds", "agent_hours", "agent_rate", "rate_source"],
            ["scope"]),
        (SpendExport.EstimateColumns,
            ["instance_id", "period_start", "period_end", "rollup", "team_id", "team_name", "outcome_id", "outcome_name",
                "rate_source", "currency", "estimated_agent_cost", "estimated_plugin_cost", "estimated_execution_cost",
                "agent_seconds", "agent_hours", "agent_rate"],
            ["scope"]),
    ];

    [Fact]
    public void Every_export_keeps_its_columns_in_name_and_order_and_only_appends_its_identity_columns_last()
    {
        foreach (var (columns, before, appended) in Files)
        {
            Assert.Equal(before, columns.Take(before.Length));
            Assert.Equal(appended, columns.Skip(before.Length));
            Assert.Equal(columns.Count, columns.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void Every_export_header_carries_instance_id_scope_and_the_stable_ids_of_its_rows()
    {
        var empty = new SpendOverview.Overview("", "", [], [], [], [], new SpendOverview.Estimate("usd", null, 0, null, []), [], [], []);
        var period = new SpendExport.Period(March, April, "instance-1");
        var headers = new Dictionary<string, string[]>
        {
            [SpendExport.Accounts] = Header(SpendExport.AccountsCsv(period, [])),
            [SpendExport.Expenses] = Header(SpendExport.ExpensesCsv(period, [], [])),
            [SpendExport.Summary] = Header(SpendExport.SummaryCsv(period, empty, [], [])),
            [SpendExport.Estimates] = Header(SpendExport.EstimatesCsv(period, [], [], [], new Dictionary<string, long>(), "usd")),
        };

        Assert.Equal(SpendExport.Files, headers.Keys);
        foreach (var header in headers.Values)
        {
            Assert.Contains("instance_id", header);
            Assert.Equal("scope", header.Last(c => c == "scope"));
            Assert.All(header, c => Assert.Matches("^[a-z][a-z0-9_]*$", c));
        }

        Assert.Contains("billing_account_id", headers[SpendExport.Accounts]);
        Assert.Superset(
            new HashSet<string> { "expense_id", "billing_account_id", "billing_account_instance_id", "supersedes_expense_id", "superseded_by_expense_id" },
            headers[SpendExport.Expenses].ToHashSet());
        Assert.Superset(new HashSet<string> { "measure", "billing_type", "currency" }, headers[SpendExport.Summary].ToHashSet());
        Assert.Superset(new HashSet<string> { "team_id", "outcome_id", "rate_source" }, headers[SpendExport.Estimates].ToHashSet());
    }

    [Fact]
    public void A_shared_accounts_expense_exported_by_two_instances_is_one_row_by_the_accounts_instance_account_and_expense_ids()
    {
        // A shared account cannot be written yet (BillingAccountRules refuses any scope but tenant); built here as a later
        // replication would hold it on both instances, to show the shape tells it apart without aggregating.
        var shared = Account("acc-shared", "Seats", "subscription", "USD", "shared", "home-instance");
        var local = Account("acc-local", "Tokens", "metered", "USD", "tenant", "home-instance");
        var expenses = new[]
        {
            new Expense("exp-shared", shared.Id, "30", "USD", Instant.Stamp(March), Instant.Stamp(April), "subscription-charge", "confirmed", null, null, null, "p@example.test", Instant.Stamp(March)),
            new Expense("exp-local", local.Id, "4.20", "USD", Instant.Stamp(March.AddDays(2)), Instant.Stamp(March.AddDays(3)), "invoice", "confirmed", null, null, null, "p@example.test", Instant.Stamp(March)),
        };

        var home = Rows(SpendExport.ExpensesCsv(new(March, April, "home-instance"), [shared, local], expenses));
        var other = Rows(SpendExport.ExpensesCsv(new(March, April, "other-instance"), [shared], [expenses[0]]));
        var together = home.Concat(other).ToList();

        // By the exporting instance and expense id the two copies look like two rows; by the account's own identity, one.
        Assert.Equal(3, together.Select(r => (r["instance_id"], r["expense_id"])).Distinct().Count());
        var sharedRows = together.Where(r => r["scope"] == "shared").ToList();
        Assert.Equal(2, sharedRows.Count);
        Assert.Single(sharedRows.Select(r => (r["billing_account_instance_id"], r["billing_account_id"], r["expense_id"])).Distinct());
        Assert.All(together, r => Assert.Equal("home-instance", r["billing_account_instance_id"]));
        Assert.Equal(["tenant"], together.Where(r => r["expense_id"] == "exp-local").Select(r => r["scope"]));

        // The accounts file: there instance_id is the account's own, so both instances' rows of the shared account are equal.
        var accountsHome = Rows(SpendExport.AccountsCsv(new(March, April, "home-instance"), [shared, local]));
        var accountsOther = Rows(SpendExport.AccountsCsv(new(March, April, "other-instance"), [shared]));
        Assert.Equal(
            accountsHome.Single(r => r["billing_account_id"] == shared.Id).Values,
            accountsOther.Single().Values);
        Assert.Equal(["shared", "tenant"], accountsHome.Select(r => r["scope"]));

        // The summary: a spend row names the scopes it adds together; the estimate row has none.
        var overview = SpendOverview.Of([shared, local], expenses, [], "usd", March, April);
        var summary = Rows(SpendExport.SummaryCsv(new(March, April, "home-instance"), overview, [], [shared, local]));
        Assert.Equal("shared;tenant", summary.Single(r => r["measure"] == SpendExport.MeasureActual)["scope"]);
        Assert.Equal("shared", summary.Single(r => r["measure"] == SpendExport.MeasureActualByType && r["billing_type"] == "subscription")["scope"]);
        Assert.Equal("tenant", summary.Single(r => r["measure"] == SpendExport.MeasureActualByType && r["billing_type"] == "metered")["scope"]);
        Assert.Equal(SpendExport.NoScope, summary.Single(r => r["measure"] == SpendExport.MeasureEstimate)["scope"]);
    }

    /// <summary>
    /// A SUMMARY ROW'S <c>scope</c> IS ITS CURRENCY'S ACCOUNTS' SCOPES, each once, in ordinal order, <c>;</c>-joined: the
    /// accounts here are stored out of order, two share a scope, and another currency's account has a scope of its own.
    /// </summary>
    [Fact]
    public void A_summary_rows_scope_names_each_scope_of_its_currencys_accounts_once_in_order()
    {
        var first = Account("acc-1", "Tokens", "metered", "USD", "tenant", "home-instance");
        var second = Account("acc-2", "Seats", "metered", "USD", "shared", "home-instance");
        var third = Account("acc-3", "More", "metered", "usd", "tenant", "home-instance");
        var euro = Account("acc-4", "Euro", "metered", "EUR", "zz-euro", "home-instance");
        var accounts = new[] { first, second, third, euro };
        var expenses = new[]
        {
            new Expense("exp-1", first.Id, "4.20", "USD", Instant.Stamp(March.AddDays(2)), Instant.Stamp(March.AddDays(3)), "invoice", "confirmed", null, null, null, "p@example.test", Instant.Stamp(March)),
            new Expense("exp-2", euro.Id, "7", "EUR", Instant.Stamp(March.AddDays(2)), Instant.Stamp(March.AddDays(3)), "invoice", "confirmed", null, null, null, "p@example.test", Instant.Stamp(March)),
        };

        var overview = SpendOverview.Of(accounts, expenses, [], "usd", March, April);
        var summary = Rows(SpendExport.SummaryCsv(new(March, April, "home-instance"), overview, [], accounts));

        string ScopeOf(string measure, string currency, string? type = null) => summary.Single(r =>
            r["measure"] == measure && r["currency"] == currency && (type is null || r["billing_type"] == type))["scope"];

        Assert.Equal("shared;tenant", ScopeOf(SpendExport.MeasureActual, "USD"));
        Assert.Equal("shared;tenant", ScopeOf(SpendExport.MeasureActualByType, "USD", "metered"));
        Assert.Equal("zz-euro", ScopeOf(SpendExport.MeasureActual, "EUR"));
        Assert.Equal("zz-euro", ScopeOf(SpendExport.MeasureActualByType, "EUR", "metered"));
    }

    private static BillingAccount Account(string id, string name, string type, string currency, string scope, string instance) =>
        new(id, name, name, type, currency, "monthly", null, Instant.Stamp(March.AddYears(-1)), null, true, null, scope, instance,
            "p@example.test", Instant.Stamp(March), Instant.Stamp(March));

    private static string[] Header(string csv) => [.. SpendExportRuleTests.Parse(csv)[0]];

    internal static List<Dictionary<string, string>> Rows(string csv)
    {
        var parsed = SpendExportRuleTests.Parse(csv);
        return Enumerable.Range(1, parsed.Count - 1).Select(i => SpendExportRuleTests.Row(parsed, i)).ToList();
    }
}

/// <summary>
/// THE SPEND CSV FILES' IDENTITY THROUGH THE REAL HOST, over one data root started twice: <c>instance_id</c> is the
/// Host's <see cref="LedgerIdentity"/> (the stored <c>instance.id</c>), each account's <c>scope</c> and instance are its
/// stored row's, and every file - its ids included - is byte for byte the same on a second export and after a restart.
/// </summary>
public sealed class SpendExportIdentityTests : IDisposable
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private static readonly string[] Routes =
    [
        "/api/spend/export/billing-accounts", "/api/spend/export/expenses", "/api/spend/export/period-summary",
        "/api/spend/export/estimated-execution-cost",
    ];

    private const string Period = "from=2033-05-01&to=2033-06-01";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-export-identity-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Database => Path.Combine(_dataRoot, "messages.db");

    public SpendExportIdentityTests() => Directory.CreateDirectory(_dataRoot);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Every_exports_instance_scope_and_ids_equal_LedgerIdentity_and_the_stored_rows_and_hold_across_two_exports_and_a_restart()
    {
        string instance;
        Dictionary<string, string> before;
        List<JsonElement> stored;

        await using (var first = Start())
        {
            instance = first.Services.GetRequiredService<LedgerIdentity>().InstanceId!;
            Assert.True(Guid.TryParse(instance, out _));
            Assert.Equal(instance, await SettingAsync(LedgerStart.InstanceIdName));

            var registry = first.Services.GetRequiredService<TeamRegistry>();
            var agent = first.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
            var team = (await registry.CreateAsync("Ledger", agent, memberAgent: agent, ct: Ct)).Id;
            await first.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password, Ct);

            using var person = await PersonAsync(first);
            var usd = await AccountAsync(person, "Seats", "subscription", "usd");
            var eur = await AccountAsync(person, "Tokens", "metered", "eur");
            var invoice = await ExpenseAsync(person, eur, "41.20", "EUR", "2033-05-03", "2033-06-03", "invoice");
            await ExpenseAsync(person, usd, "30", "USD", "2033-05-01", "2033-06-01", "subscription-charge");
            Assert.Equal(HttpStatusCode.Created, (await person.PostAsJsonAsync($"/api/expenses/{invoice}/void", new { note = "duplicate" }, Ct)).StatusCode);
            await RunAsync(team, new DateTimeOffset(2033, 5, 9, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2033, 5, 9, 10, 30, 0, TimeSpan.Zero), "40");

            stored = (await person.GetFromJsonAsync<List<JsonElement>>("/api/billing-accounts", Ct))!;
            before = await ExportAsync(person);
            Assert.Equal(before, await ExportAsync(person));
        }

        await using (var second = Start())
        {
            Assert.Equal(instance, second.Services.GetRequiredService<LedgerIdentity>().InstanceId);
            using var person = await PersonAsync(second);
            Assert.Equal(before, await ExportAsync(person));
        }

        // instance_id on every row of every file is the stored instance.id - the account's own, on the accounts file.
        var files = before.ToDictionary(f => f.Key, f => SpendExportIdentityRuleTests.Rows(f.Value));
        Assert.All(files.Values, rows => Assert.NotEmpty(rows));
        Assert.All(files.Values.SelectMany(r => r), r => Assert.Equal(instance, r["instance_id"]));

        // Each account row's id, scope and instance are its stored row's.
        var byId = stored.ToDictionary(a => a.GetProperty("id").GetString()!);
        var accounts = files[Routes[0]];
        Assert.Equal(byId.Keys.Order(), accounts.Select(r => r["billing_account_id"]).Order());
        foreach (var row in accounts)
        {
            var account = byId[row["billing_account_id"]];
            Assert.Equal(account.GetProperty("scope").GetString(), row["scope"]);
            Assert.Equal(account.GetProperty("instanceId").GetString(), row["instance_id"]);
        }

        // Each expense row carries its account's stored scope and instance, beside the exporting instance.
        var expenses = files[Routes[1]];
        Assert.Equal(3, expenses.Count);
        foreach (var row in expenses)
        {
            var account = byId[row["billing_account_id"]];
            Assert.Equal(account.GetProperty("scope").GetString(), row["scope"]);
            Assert.Equal(account.GetProperty("instanceId").GetString(), row["billing_account_instance_id"]);
            Assert.True(Guid.TryParse(row["expense_id"], out _));
        }

        // The summary's spend rows name their accounts' scope; estimate rows, here and in the estimates file, none.
        Assert.All(files[Routes[2]], r => Assert.Equal(r["measure"] == SpendExport.MeasureEstimate ? SpendExport.NoScope : BillingAccountRules.ScopeTenant, r["scope"]));
        Assert.All(files[Routes[3]], r => Assert.Equal(SpendExport.NoScope, r["scope"]));
        Assert.Contains(files[Routes[3]], r => r["rollup"] == SpendExport.RollupTeam && r["team_id"].Length > 0);
    }

    private WebApplicationFactory<Program> Start()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning"));
        _ = factory.Services;
        return factory;
    }

    private static async Task<HttpClient> PersonAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<Dictionary<string, string>> ExportAsync(HttpClient person)
    {
        var files = new Dictionary<string, string>();
        foreach (var route in Routes)
        {
            var answer = await person.GetAsync($"{route}?{Period}", Ct);
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            files[route] = await answer.Content.ReadAsStringAsync(Ct);
        }

        return files;
    }

    private static async Task<string> AccountAsync(HttpClient person, string name, string type, string currency)
    {
        var created = await person.PostAsJsonAsync(
            "/api/billing-accounts",
            new { name, vendor = name, billingType = type, currency, billingPeriod = "monthly", effectiveFrom = "2033-01-01" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    private static async Task<string> ExpenseAsync(HttpClient person, string account, string amount, string currency, string start, string end, string kind)
    {
        var recorded = await person.PostAsJsonAsync(
            "/api/expenses", new { accountId = account, amount, currency, servicePeriodStart = start, servicePeriodEnd = end, kind }, Ct);
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        return (await recorded.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    private async Task RunAsync(string team, DateTimeOffset started, DateTimeOffset ended, string rate)
    {
        await ExecuteAsync(
            """
            INSERT INTO execution_snapshots (start_seq, correlation, team_id, team_name, member, member_kind,
                rate_amount, rate_currency, rate_source, link_id, started_at)
            VALUES (900001, 900000, $team, $team, 'Dev', 'agent', $rate, 'USD', 'member', NULL, $started)
            """,
            ("$team", team), ("$rate", rate), ("$started", started.ToString("O")));
        await ExecuteAsync(
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, member_kind, run_outcome,
                started_at, ended_at, measured, tokens_in, tokens_out, billable, start_seq)
            VALUES (900002, 900000, $team, $team, 'Dev', 'agent', 'completed', $started, $ended, 1, 10, 0, 10, 900001)
            """,
            ("$team", team), ("$started", started.ToString("O")), ("$ended", ended.ToString("O")));
    }

    private async Task<string?> SettingAsync(string name)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM tenant_settings WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return (string?)await command.ExecuteScalarAsync(Ct);
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
