using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A PLUGIN RUN COSTS ZERO, NOT "NOT MEASURED". A plugin runs no model, so its token cost is known:
/// its terminal row says so (`tokensSource: "none"`, no figures), and the trigger's spent-today, the
/// member's recent cost and the workflow spend count it as a measured run of 0 billable tokens. An
/// AGENT run that reports no usage is unchanged: not measured, never a zero.
///
/// On the real Host: the real sample-echo plugin (Echo) and an agent member (Dev, a fake agent).
/// </summary>
public sealed class PluginRunCostsZeroTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-zero-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Echo => new(_team, "Echo");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);

        // Every agent run reports NO usage.
        _agents.Behaviour = invocation => Task.FromResult(new AgentResult(0, $"noted by {invocation.Container.Name}"));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Zero", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        var echo = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo",
            agent = "plugin:sample-echo",
            config = new { mode = "upper" },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);

        var dev = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct);
        Assert.Equal(HttpStatusCode.OK, dev.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>An every-5-minutes schedule with a cap on <paramref name="member"/>, swept as due;
    /// returns the trigger's id and the member's terminal row for that fire.</summary>
    private async Task<(string Id, Message Row)> FireScheduleAsync(string member)
    {
        var created = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", new
        {
            name = $"Poll {member}",
            kind = "every",
            container = member,
            intervalSeconds = 300,
            idleOnly = false,
            instruction = "something new",
            wakeManager = WakeManagerPolicy.Never,
            dailyTokenCap = 1000,
        }, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;

        var after = await Log.HighestSeqAsync(Ct);
        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(DateTimeOffset.UtcNow.AddSeconds(301), Ct);

        var source = new ContainerId(_team, member).ToString();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var row = (await Log.ReadAfterAsync(after, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
                .FirstOrDefault(m => m.Source == source);
            if (row is not null) return (id, row);
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"No terminal row for {member}.");
    }

    private async Task<JsonElement> SpentTodayAsync(string member, string id)
    {
        var rows = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/containers/{member}/triggers", Ct);
        return rows.EnumerateArray().Single(r => r.GetProperty("id").GetString() == id).GetProperty("spentToday");
    }

    [Fact]
    public async Task A_plugin_run_says_it_ran_no_model_and_is_measured_at_zero_in_every_spend_query()
    {
        var (id, row) = await FireScheduleAsync("Echo");

        Assert.Equal(MessageTypes.Completed, row.Type);
        var payload = JsonDocument.Parse(row.Payload).RootElement;
        Assert.Equal(UsageSource.NoModel, payload.GetProperty(PayloadFields.TokensSource).GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("tokensIn").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("tokensOut").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("tokensTotal").ValueKind);

        // The trigger's spent-today.
        var spent = await SpentTodayAsync("Echo", id);
        Assert.Equal(0, spent.GetProperty("billableTokens").GetInt64());
        Assert.Equal(1, spent.GetProperty("measuredRuns").GetInt32());
        Assert.Equal(0, spent.GetProperty("unmeasuredRuns").GetInt32());

        // The member's recent-cost line.
        var cost = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/containers/Echo/cost", Ct);
        Assert.Equal(1, cost.GetProperty("lastRuns").GetInt32());
        Assert.Equal(1, cost.GetProperty("measuredRuns").GetInt32());
        Assert.Equal(0, cost.GetProperty("unmeasuredRuns").GetInt32());
        Assert.Equal(0, cost.GetProperty("medianBillableTokens").GetInt64());
        Assert.Equal("plugin", cost.GetProperty("kind").GetString());

        // The workflow spend.
        Assert.Equal(new WorkflowSpend(0, 1, 0), await Log.GetWorkflowSpendAsync(row.CorrelationId, Ct));
    }

    [Fact]
    public async Task An_agent_run_that_reports_no_usage_stays_unmeasured_in_every_spend_query()
    {
        var (id, row) = await FireScheduleAsync("Dev");

        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.Equal(JsonValueKind.Null,
            JsonDocument.Parse(row.Payload).RootElement.GetProperty(PayloadFields.TokensSource).ValueKind);

        var spent = await SpentTodayAsync("Dev", id);
        Assert.Equal(0, spent.GetProperty("measuredRuns").GetInt32());
        Assert.Equal(1, spent.GetProperty("unmeasuredRuns").GetInt32());

        var cost = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/containers/Dev/cost", Ct);
        Assert.Equal(0, cost.GetProperty("measuredRuns").GetInt32());
        Assert.Equal(1, cost.GetProperty("unmeasuredRuns").GetInt32());
        Assert.Equal(JsonValueKind.Null, cost.GetProperty("medianBillableTokens").ValueKind);

        Assert.Equal(new WorkflowSpend(0, 0, 1), await Log.GetWorkflowSpendAsync(row.CorrelationId, Ct));
    }

    [Fact]
    public void A_row_that_ran_no_model_bills_zero_and_one_that_reported_nothing_bills_nothing()
    {
        Assert.Equal(0, TriggerCost.BillableOf("""{"tokensIn":null,"tokensOut":null,"tokensSource":"none"}"""));
        Assert.Null(TriggerCost.BillableOf("""{"tokensIn":null,"tokensOut":null,"tokensSource":null}"""));
        Assert.Equal(0, InvocationUsage.NoModel.BillableTokens());
    }
}
