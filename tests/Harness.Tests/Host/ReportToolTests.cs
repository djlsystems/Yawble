using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// WHO HOLDS SPEND, on the rule alone: an agent while the setting is on, never a person's API key or the
/// worker key, and never from a stored permit.
/// </summary>
public sealed class SpendReadingRuleTests
{
    private static Principal Of(PrincipalKind kind, params string[] permits) =>
        new("p", kind, new HashSet<string>(permits, StringComparer.Ordinal));

    [Fact]
    public void Only_an_agent_holds_Spend_and_only_while_the_setting_is_on_and_a_stored_Spend_is_taken_away()
    {
        foreach (var kind in new[] { PrincipalKind.Container, PrincipalKind.Concierge, PrincipalKind.TenantConcierge })
        {
            Assert.True(SpendReading.Apply(Of(kind, Permits.Read), agentsMayReadSpend: true).May(Permits.Spend));
            Assert.False(SpendReading.Apply(Of(kind, Permits.Read), agentsMayReadSpend: false).May(Permits.Spend));
            Assert.False(SpendReading.Apply(Of(kind, Permits.Read, Permits.Spend), agentsMayReadSpend: false).May(Permits.Spend));
        }

        foreach (var kind in new[] { PrincipalKind.ApiKey, PrincipalKind.Worker })
        {
            Assert.False(SpendReading.Apply(Of(kind, Permits.Read), agentsMayReadSpend: true).May(Permits.Spend));
            Assert.False(SpendReading.Apply(Of(kind, Permits.Read, Permits.Spend), agentsMayReadSpend: true).May(Permits.Spend));
        }

        // Nothing else moves, and Spend is never a permit a key can be minted with.
        Assert.Equal([Permits.Read, Permits.Spend], SpendReading.Apply(Of(PrincipalKind.Container, Permits.Read), true).Permits.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Permits.Spend, Permits.All);
    }
}

/// <summary>
/// The Spend permit and the <c>report</c> tool on the real Host: a person grants Spend by turning on
/// <c>reporting.agentsMayReadSpend</c>; an agent holding it reads the Spend page's reads and nothing writes;
/// the tool's figures are the routes' own, bounded to the caller's teams, in the product's words.
/// </summary>
public sealed class ReportToolTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Setting = TenantSettings.ReportingAgentsMayReadSpendName;

    private IOutcomeStore Outcomes => host.Services.GetRequiredService<IOutcomeStore>();

    private string Database => Path.Combine(host.DataRoot, "messages.db");

    /// <summary>Every read the Spend permit opens, as a person's screen calls it.</summary>
    private static readonly string[] SpendReads =
    [
        "/api/billing-accounts",
        "/api/expenses",
        "/api/spend/overview",
        "/api/spend/overview?from=2035-03-01&to=2035-04-01",
        "/api/spend/linked-members",
        "/api/spend/export/billing-accounts?from=2035-03-01&to=2035-04-01",
        "/api/spend/export/expenses?from=2035-03-01&to=2035-04-01",
        "/api/spend/export/period-summary?from=2035-03-01&to=2035-04-01",
        "/api/spend/export/estimated-execution-cost?from=2035-03-01&to=2035-04-01",
    ];

    [Fact]
    public async Task An_agent_holds_the_Spend_permit_only_while_a_person_has_turned_on_the_setting_read_on_every_request()
    {
        using var person = await host.PersonAsync();
        var principals = host.Services.GetRequiredService<IPrincipalStore>();
        var apiKey = await principals.MintAsync($"apikey-{Guid.NewGuid():N}", PrincipalKind.ApiKey, null, Permits.All, ct: Ct);
        var concierge = await principals.MintAsync(
            ConciergeLaunchFactory.PrincipalId(await MeAsync(person)), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: (await MeAsync(person)), ct: Ct);

        try
        {
            using var agent = host.Container(host.AlphaContainerKey);
            using var tenantConcierge = host.Container(concierge);
            using var key = host.Container(apiKey);

            foreach (var machine in new[] { agent, tenantConcierge, key }) await AssertRefusedAsync(machine);

            await SetAsync(person, "on");
            foreach (var route in SpendReads)
            {
                Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync(route, Ct)).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await tenantConcierge.GetAsync(route, Ct)).StatusCode);
            }

            // A person's API key is a person's outside tool, not an agent: it never holds Spend.
            await AssertRefusedAsync(key);

            // Turned off, the very next request is refused: nothing was stored with the key.
            await SetAsync(person, "off");
            await AssertRefusedAsync(agent);
            await AssertRefusedAsync(tenantConcierge);
        }
        finally
        {
            await SetAsync(person, "off");
        }
    }

    [Fact]
    public async Task No_agent_can_turn_the_setting_on_itself()
    {
        using var person = await host.PersonAsync();
        var principals = host.Services.GetRequiredService<IPrincipalStore>();
        var concierge = await principals.MintAsync(
            ConciergeLaunchFactory.PrincipalId(await MeAsync(person)), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: (await MeAsync(person)), ct: Ct);

        foreach (var credential in new[] { host.AlphaContainerKey, concierge })
        {
            using var machine = host.Container(credential);
            var answer = await machine.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object> { [Setting] = "on" }, Ct);

            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.Equal(PermitGate.HumansOnlyMessage, (await JsonAsync(answer)).GetProperty("error").GetString());
        }

        Assert.False(host.Services.GetRequiredService<TenantSettings>().ReportingAgentsMayReadSpend);
    }

    [Fact]
    public async Task An_agent_holding_Spend_is_still_refused_every_spend_rate_member_cost_and_outcome_value_write()
    {
        using var person = await host.PersonAsync();
        var account = await AccountAsync(person, "Held");
        var expense = (await JsonAsync(await person.PostAsJsonAsync("/api/expenses",
            new { accountId = account, amount = "30", currency = "USD", servicePeriodStart = "2035-03-01", servicePeriodEnd = "2035-04-01", kind = "invoice" }, Ct)))
            .GetProperty("id").GetString()!;
        var outcome = (await JsonAsync(await person.PostAsJsonAsync("/api/outcomes", new { name = $"Valued {Guid.NewGuid():N}" }, Ct)))
            .GetProperty("id").GetString()!;
        var settings = host.Services.GetRequiredService<TenantSettings>();
        var rate = settings.OutcomesAgentHourlyRate;

        try
        {
            await SetAsync(person, "on");
            using var agent = host.Container(host.AlphaContainerKey);
            Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/billing-accounts", Ct)).StatusCode);

            var member = $"/api/teams/{host.Alpha}/containers/{TeamRegistry.DefaultManagerName}";
            var writes = new[]
            {
                await agent.PostAsJsonAsync("/api/billing-accounts", NewAccount("Agent's"), Ct),
                await agent.PostAsJsonAsync($"/api/billing-accounts/{account}", new { name = "Agent's" }, Ct),
                await agent.PostAsync($"/api/billing-accounts/{account}/deactivate", null, Ct),
                await agent.PostAsync($"/api/billing-accounts/{account}/reactivate", null, Ct),
                await agent.PostAsJsonAsync("/api/expenses",
                    new { accountId = account, amount = "1", currency = "USD", servicePeriodStart = "2035-03-01", servicePeriodEnd = "2035-04-01", kind = "invoice" }, Ct),
                await agent.PostAsJsonAsync($"/api/expenses/{expense}/void", new { note = "agent" }, Ct),
                await agent.PostAsJsonAsync($"/api/expenses/{expense}/replace", new { amount = 1 }, Ct),
                await agent.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object> { [TenantSettings.OutcomesAgentHourlyRateName] = rate + 7 }, Ct),
                await agent.PatchAsJsonAsync(member, new { hourlyRate = 99 }, Ct),
                await agent.PatchAsJsonAsync(member, new { billingAccountId = account }, Ct),
                await agent.PatchAsJsonAsync($"/api/outcomes/{outcome}", new { value = 5 }, Ct),
            };

            foreach (var answer in writes)
            {
                Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
                Assert.Equal(PermitGate.HumansOnlyMessage, (await JsonAsync(answer)).GetProperty("error").GetString());
            }

            // Nothing moved.
            var stored = await person.GetFromJsonAsync<JsonElement>($"/api/billing-accounts/{account}", Ct);
            Assert.Equal((true, $"Held"), (stored.GetProperty("active").GetBoolean(), stored.GetProperty("vendor").GetString()));
            Assert.True((await person.GetFromJsonAsync<JsonElement>($"/api/expenses/{expense}", Ct)).GetProperty("current").GetBoolean());
            Assert.Equal(rate, settings.OutcomesAgentHourlyRate);
            var manager = await person.GetFromJsonAsync<JsonElement>(member, Ct);
            Assert.True(manager.GetProperty("hourlyRate").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
            Assert.True(manager.GetProperty("billingAccountId").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
            Assert.Null((await Outcomes.FindAsync(outcome, Ct))!.Value);
        }
        finally
        {
            await SetAsync(person, "off");
        }
    }

    [Fact]
    public async Task With_the_setting_on_a_persons_reads_answer_byte_for_byte_as_before_and_an_agent_reads_the_same_figures()
    {
        using var person = await host.PersonAsync();
        await AccountAsync(person, "Bytes");
        await SeedAsync();

        var before = new Dictionary<string, string>();
        foreach (var route in SpendReads) before[route] = await person.GetStringAsync(route, Ct);

        try
        {
            await SetAsync(person, "on");
            using var agent = host.Container(host.AlphaContainerKey);

            foreach (var route in SpendReads)
            {
                Assert.Equal(before[route], await person.GetStringAsync(route, Ct));
            }

            // THE TENANT'S OWN reads are the same bytes to an agent: one set of accounts, expenses and overview.
            foreach (var route in new[] { "/api/billing-accounts", "/api/expenses", "/api/spend/overview?from=2035-03-01&to=2035-04-01",
                         "/api/spend/export/billing-accounts?from=2035-03-01&to=2035-04-01", "/api/spend/export/expenses?from=2035-03-01&to=2035-04-01",
                         "/api/spend/export/period-summary?from=2035-03-01&to=2035-04-01" })
            {
                Assert.Equal(before[route], await agent.GetStringAsync(route, Ct));
            }
        }
        finally
        {
            await SetAsync(person, "off");
        }
    }

    [Fact]
    public async Task The_report_tools_figures_equal_the_routes_and_the_cost_service_for_a_seeded_period()
    {
        using var person = await host.PersonAsync();
        var (from, to) = await SeedAsync();
        var tools = AgentTools(host.Alpha);
        using var agent = host.Container(_agentKey!);

        // EACH ACTION IS ITS ROUTE, byte for byte, under one heading line.
        var pairs = new (string Action, string Route)[]
        {
            ("teams", "/api/teams/cost/period?from=2035-03-01&to=2035-04-01"),
            ("team", $"/api/teams/{host.Alpha}/cost/period?from=2035-03-01&to=2035-04-01"),
            ("workflows", $"/api/teams/{host.Alpha}/cost/workflows"),
            ("outcomes", "/api/outcomes?from=2035-03-01T00%3A00%3A00Z&to=2035-04-01T00%3A00%3A00Z"),
        };
        foreach (var (action, route) in pairs)
        {
            var (status, heading, body) = Parts(await tools.Report(action, from: "2035-03-01", to: "2035-04-01", cancellationToken: Ct));
            Assert.Equal("HTTP 200", status);
            Assert.Contains("Estimated Execution Cost", heading, StringComparison.Ordinal);
            Assert.Contains("never Actual Spend", heading, StringComparison.Ordinal);
            Assert.Equal(await agent.GetStringAsync(route, Ct), body);
        }

        var human = Parts(await tools.Report("human", cancellationToken: Ct));
        Assert.Contains("Human Interventions and Human Attention Time", human.Heading, StringComparison.Ordinal);
        Assert.Contains("last eight weeks", human.Heading, StringComparison.Ordinal);
        Assert.Equal(JsonDocument.Parse(await agent.GetStringAsync($"/api/teams/{host.Alpha}/cost", Ct)).RootElement.GetProperty("human").GetRawText(),
            JsonDocument.Parse(human.Body).RootElement.GetProperty("human").GetRawText());

        // ...AND THE FIGURES ARE THE SCREENS': the team's row on a person's Teams list, and ExecutionCost itself.
        var team = JsonDocument.Parse(Parts(await tools.Report("team", from: "2035-03-01", to: "2035-04-01", cancellationToken: Ct)).Body).RootElement;
        var list = await person.GetFromJsonAsync<JsonElement>("/api/teams/cost/period?from=2035-03-01&to=2035-04-01", Ct);
        var row = list.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == host.Alpha).GetProperty("totals");

        var runs = ExecutionCost.Of(await Outcomes.ReadExecutionRunsAsync(from, to, pricedOnly: false, Ct), await Outcomes.ListAsync(Ct), "USD")
            .Where(r => string.Equals(r.Ledger.TeamId, host.Alpha, StringComparison.OrdinalIgnoreCase));
        var tally = new ExecutionCost.Tally("USD");
        foreach (var run in runs) tally.AddSpan(run, run.SecondsIn(from, to));

        Assert.True(tally.Cost > 0);
        Assert.Equal(tally.Cost, team.GetProperty("totals").GetProperty("cost").GetDecimal());
        Assert.Equal(row.GetProperty("cost").GetDecimal(), team.GetProperty("totals").GetProperty("cost").GetDecimal());
        Assert.Equal(row.GetProperty("agentSeconds").GetDouble(), team.GetProperty("totals").GetProperty("agentSeconds").GetDouble(), 3);
        Assert.Equal(row.GetProperty("agentRate").GetDecimal(), team.GetProperty("totals").GetProperty("agentRate").GetDecimal());

        // The plugin member with no rate of its own reads not priced, never the agent default and never 0.
        var plugin = team.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("kind").GetString() == "plugin");
        Assert.Equal(JsonValueKind.Null, plugin.GetProperty("figures").GetProperty("cost").ValueKind);
        Assert.Equal("not-priced", plugin.GetProperty("unpriced").GetString());

        // SPEND, granted: the Spend page's overview for the same period, with the sentence.
        try
        {
            await SetAsync(person, "on");
            var spend = Parts(await AgentTools(host.Alpha).Report("spend", from: "2035-03-01", to: "2035-04-01", cancellationToken: Ct));
            Assert.Equal("HTTP 200", spend.Status);
            Assert.Contains(PlatformMcpTools.SpendSentence, spend.Heading, StringComparison.Ordinal);
            Assert.Equal(await person.GetStringAsync("/api/spend/overview?from=2035-03-01&to=2035-04-01", Ct), spend.Body);

            var accounts = Parts(await AgentTools(host.Alpha).Report("accounts", cancellationToken: Ct));
            Assert.Equal(await person.GetStringAsync("/api/billing-accounts", Ct), accounts.Body);
        }
        finally
        {
            await SetAsync(person, "off");
        }
    }

    [Fact]
    public async Task The_report_tool_reaches_only_the_callers_teams()
    {
        await SeedAsync();
        var tools = AgentTools(host.Alpha);

        var teams = JsonDocument.Parse(Parts(await tools.Report("teams", from: "2035-03-01", to: "2035-04-01", cancellationToken: Ct)).Body).RootElement;
        Assert.Equal([host.Alpha], teams.GetProperty("teams").EnumerateArray().Select(t => t.GetProperty("team").GetString()));

        foreach (var action in new[] { "team", "workflows", "human" })
        {
            var refused = await tools.Report(action, team: host.Beta, from: "2035-03-01", to: "2035-04-01", cancellationToken: Ct);
            Assert.StartsWith($"Refused: the report tool's {action}: ", refused, StringComparison.Ordinal);
            Assert.DoesNotContain("agentSeconds", refused, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_report_tool_refuses_Actual_Spend_without_the_permit_in_one_sentence_and_refuses_a_bad_action_or_period()
    {
        var tools = AgentTools(host.Alpha);

        foreach (var action in new[] { "spend", "accounts" })
        {
            Assert.Equal($"Refused: the report tool's {action}: {SpendReading.Refused}", await tools.Report(action, cancellationToken: Ct));
        }

        Assert.Equal($"Refused: the report tool's action is {PlatformMcpTools.ReportActions}.", await tools.Report("write", cancellationToken: Ct));
        Assert.StartsWith("Refused: the report tool's period", await tools.Report("teams", from: "2035-03-01", cancellationToken: Ct), StringComparison.Ordinal);
        Assert.StartsWith("Refused: the report tool's period", await tools.Report("teams", from: "2035-04-01", to: "2035-03-01", cancellationToken: Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_estimate_field_the_report_tool_answers_is_named_spend()
    {
        await SeedAsync();
        var tools = AgentTools(host.Alpha);

        foreach (var action in new[] { "teams", "team", "workflows", "outcomes", "human" })
        {
            var answer = Parts(await tools.Report(action, from: "2035-03-01", to: "2035-04-01", cancellationToken: Ct));
            Assert.Equal("HTTP 200", answer.Status);

            var named = Names(JsonDocument.Parse(answer.Body).RootElement).Where(n => n.Contains("spend", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(named.Count == 0, $"{action}: {string.Join(", ", named)}");

            // The heading names the estimate in its own words and never calls it Spend, Spent or Cost alone.
            Assert.DoesNotMatch(@"\bSpent\b", answer.Heading);
            Assert.DoesNotContain("value", answer.Heading, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ROI", answer.Heading, StringComparison.Ordinal);
            Assert.DoesNotContain("savings", answer.Heading, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_report_tools_sentence_is_the_Spend_pages_word_for_word()
    {
        var spend = File.ReadAllText(Path.Combine(RepoRoot(), "web", "src", "lib", "spend.ts"));
        Assert.Contains($"'{PlatformMcpTools.SpendSentence}'", spend, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private string? _agentKey;

    /// <summary>The tools as <paramref name="team"/>'s Manager calls them, its principal made as authentication makes it.</summary>
    private PlatformMcpTools AgentTools(string team)
    {
        var id = new ContainerId(team, TeamRegistry.DefaultManagerName).ToString();
        _agentKey = host.Services.GetRequiredService<IPrincipalStore>()
            .MintAsync(id, PrincipalKind.Container, team, TeamRegistry.ManagerPermits, ct: Ct).GetAwaiter().GetResult();
        var principal = SpendReading.Apply(
            new Principal(id, PrincipalKind.Container, TeamRegistry.ManagerPermits),
            host.Services.GetRequiredService<TenantSettings>().ReportingAgentsMayReadSpend);

        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = _agentKey;
        context.User = PrincipalClaims.ToClaimsPrincipal(principal, "test");

        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context },
            host.Services.GetRequiredService<IPrincipalStore>(),
            host.Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(host.ServerHandler()));
    }

    private static (string Status, string Heading, string Body) Parts(string answer)
    {
        var lines = answer.Split(Environment.NewLine, 3);
        Assert.True(lines.Length == 3, answer);
        return (lines[0], lines[1], lines[2]);
    }

    private static IEnumerable<string> Names(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Names(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Names),
        _ => [],
    };

    private async Task AssertRefusedAsync(HttpClient machine)
    {
        foreach (var route in SpendReads)
        {
            var answer = await machine.GetAsync(route, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.Equal(SpendReading.Refused, (await JsonAsync(answer)).GetProperty("error").GetString());
        }
    }

    private async Task SetAsync(HttpClient person, string value)
    {
        var answer = await person.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object> { [Setting] = value }, Ct);
        Assert.True(answer.IsSuccessStatusCode, await answer.Content.ReadAsStringAsync(Ct));
        Assert.Equal(value == "on", host.Services.GetRequiredService<TenantSettings>().ReportingAgentsMayReadSpend);
    }

    private async Task<string> MeAsync(HttpClient person) =>
        (await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct))!.Id;

    private static object NewAccount(string name) =>
        new { name = $"{name} {Guid.NewGuid():N}", vendor = name, billingType = "metered", currency = "usd", billingPeriod = "monthly", effectiveFrom = "2035-01-01" };

    private async Task<string> AccountAsync(HttpClient person, string name)
    {
        var created = await person.PostAsJsonAsync("/api/billing-accounts", NewAccount(name), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await JsonAsync(created)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private bool _seeded;

    /// <summary>March 2035 on both teams: priced agent runs (one crossing the period's start), a plugin with no rate.</summary>
    private async Task<(DateTimeOffset From, DateTimeOffset To)> SeedAsync()
    {
        var from = new DateTimeOffset(2035, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddMonths(1);
        if (_seeded) return (from, to);
        _seeded = true;

        await RunAsync(NextCorrelation(), host.Alpha, "Dev", from.AddSeconds(-1237), from.AddSeconds(4321), "47.37");
        await RunAsync(NextCorrelation(), host.Alpha, "Dev", from.AddDays(3).AddSeconds(17), from.AddDays(3).AddSeconds(7777), "33.33");
        await RunAsync(NextCorrelation(), host.Alpha, "Billing", from.AddDays(9), from.AddDays(9).AddSeconds(600), null, kind: "plugin");
        await RunAsync(NextCorrelation(), host.Beta, "Dev", from.AddDays(4), from.AddDays(4).AddSeconds(3600), "50");
        return (from, to);
    }

    private static long _correlation = 7_000_000_000 + Random.Shared.Next(1_000_000);

    private static long NextCorrelation() => Interlocked.Add(ref _correlation, 1000);

    private static long _run = 7_500_000_000 + Random.Shared.Next(1_000_000);

    private async Task RunAsync(long correlation, string team, string member, DateTimeOffset started, DateTimeOffset ended, string? rate, string kind = "agent")
    {
        var start = Interlocked.Increment(ref _run);
        await ExecuteAsync(
            """
            INSERT INTO execution_snapshots (start_seq, correlation, team_id, team_name, member, member_kind,
                rate_amount, rate_currency, rate_source, link_id, started_at)
            VALUES ($start, $c, $team, $team, $member, $kind, $rate, 'USD', $source, NULL, $started)
            """,
            ("$start", start), ("$c", correlation), ("$team", team), ("$member", member), ("$kind", kind),
            ("$rate", (object?)rate ?? DBNull.Value), ("$source", rate is null ? "none" : "member"), ("$started", started.ToString("O")));

        await ExecuteAsync(
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, member_kind, run_outcome,
                started_at, ended_at, measured, tokens_in, tokens_out, billable, start_seq)
            VALUES ($seq, $c, $team, $team, $member, $kind, 'completed', $started, $ended, 1, 10, 0, 10, $start)
            """,
            ("$seq", Interlocked.Increment(ref _run)), ("$c", correlation), ("$team", team), ("$member", member), ("$kind", kind),
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

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
