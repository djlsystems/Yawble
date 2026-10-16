using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE RETENTION PASS IS MEMORY, NOT ACCOUNTING. On the real Host, a scheduled run of Dev hands back
/// and wakes the Manager, the Manager declares the workflow and a person links it to an outcome;
/// the workflow's rows are then 100 days old and the Host's own pass removes what it may. A
/// workflow's spend, the trigger's spend today and the outcome's figures read the same before and
/// after, and no ledger, tenant row or hold moves but the pass's own row.
/// </summary>
public sealed class LogRetentionLedgerTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string TeamLabel = "Retention books";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-log-retention-ledger-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Manager => new(_team, TeamRegistry.DefaultManagerName);

    private string Database => Path.Combine(_dataRoot, "messages.db");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _agents.Behaviour = async invocation =>
        {
            if (invocation.Container.Name == "Dev")
            {
                await Services.GetRequiredService<MemberReports>().HandbackAsync(invocation.Container, "found one", Ct);
                return new AgentResult(0, "handed back", Usage: new InvocationUsage(1000, 500, "test", cachedIn: 2000));
            }

            return new AgentResult(0, $"noted by {invocation.Container.Name}", Usage: new InvocationUsage(600, 100, "test"));
        };

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync(TeamLabel, "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        // Built at run time: a test sign-in, never a stored credential.
        var password = $"pw-{Guid.NewGuid():N}";
        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct)).StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_pass_leaves_the_workflow_spend_the_trigger_spend_today_the_outcome_figures_and_every_ledger_row_as_they_were()
    {
        var trigger = await CreateScheduleAsync();
        var after = await Log.HighestSeqAsync(Ct);
        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(DateTimeOffset.UtcNow.AddSeconds(301), Ct);

        var dev = await AwaitRowAsync(m => m.Seq > after && m.Source == Dev.ToString(), "Dev's terminal row");
        var manager = await AwaitRowAsync(
            m => m.Seq > after && m.Source == Manager.ToString() && m.CorrelationId == dev.CorrelationId,
            "the Manager's terminal row");
        await SettleAsync();

        var workflow = dev.CorrelationId;
        await Log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", Manager.ToString(), manager.Seq), Ct);

        var created = await _person.PostAsJsonAsync("/api/outcomes", new { name = "Books balanced" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var outcome = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await _person.PutAsJsonAsync(
            $"/api/teams/{_team}/workflows/{workflow}/outcome", new { outcome }, Ct)).StatusCode);

        // The whole workflow is 100 days old.
        var rowsBefore = await CountAsync($"SELECT COUNT(*) FROM messages WHERE correlation_id = {workflow}");
        await ExecuteAsync(
            $"UPDATE messages SET occurred_at = '{DateTimeOffset.UtcNow.AddDays(-100).ToString("O", CultureInfo.InvariantCulture)}' WHERE correlation_id = {workflow}");

        var spendBefore = await Log.GetWorkflowSpendAsync(workflow, Ct);
        var sinceNudgeBefore = await Log.GetSpendSinceNudgeAsync(workflow, Ct);
        var todayBefore = await SpentTodayAsync(trigger);
        var figuresBefore = await _person.GetStringAsync($"/api/outcomes/{outcome}", Ct);
        var listBefore = await _person.GetStringAsync("/api/outcomes", Ct);
        var keptBefore = await KeptRowsAsync();
        var tenantBefore = await RowsAsync("SELECT json_array(seq, occurred_at, actor_id, actor_email, action, subject, subject_name, detail) FROM tenant_events ORDER BY seq");

        Assert.True(todayBefore.GetProperty("billableTokens").GetInt64() > 0);
        Assert.True(spendBefore.TokensSpent > 0);

        var pass = await Services.GetRequiredService<LogRetention>().RunAsync(DateTimeOffset.UtcNow, Ct);

        // The pass removed rows of that workflow; its root and declaration stay for the outcome link.
        Assert.True(pass.Purged > 0, $"purged {pass.Purged}, retained {pass.Retained}");
        Assert.True(await CountAsync($"SELECT COUNT(*) FROM messages WHERE correlation_id = {workflow}") < rowsBefore);
        Assert.NotNull(await Log.FindAsync(workflow, Ct));

        Assert.Equal(spendBefore, await Log.GetWorkflowSpendAsync(workflow, Ct));
        Assert.Equal(sinceNudgeBefore, await Log.GetSpendSinceNudgeAsync(workflow, Ct));
        Assert.Equal(todayBefore.GetRawText(), (await SpentTodayAsync(trigger)).GetRawText());
        Assert.Equal(figuresBefore, await _person.GetStringAsync($"/api/outcomes/{outcome}", Ct));
        Assert.Equal(listBefore, await _person.GetStringAsync("/api/outcomes", Ct));
        Assert.Equal(keptBefore, await KeptRowsAsync());

        var tenantAfter = await RowsAsync("SELECT json_array(seq, occurred_at, actor_id, actor_email, action, subject, subject_name, detail) FROM tenant_events ORDER BY seq");
        Assert.Equal(tenantBefore, tenantAfter.Take(tenantBefore.Count).ToList());
        Assert.Contains("log.pruned", Assert.Single(tenantAfter.Skip(tenantBefore.Count)));
    }

    [Fact]
    public async Task The_setting_is_listed_with_its_sentence_and_the_pass_runs_off_the_request_path()
    {
        var listed = await _person.GetFromJsonAsync<JsonElement>("/api/tenant/settings", Ct);
        var setting = listed.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "log.retentionDays");

        Assert.Equal(90, setting.GetProperty("value").GetInt32());
        Assert.StartsWith(TenantSettings.LogRetentionSentence, setting.GetProperty("description").GetString());

        // A hosted loop, never a route.
        Assert.Single(Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<LogRetentionLoop>());

        var refused = await _person.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object?> { ["log.retentionDays"] = 3 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("log.retentionDays", await refused.Content.ReadAsStringAsync(Ct));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private async Task<string> CreateScheduleAsync()
    {
        var created = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", new Dictionary<string, object?>
        {
            ["name"] = "Poll Dev",
            ["kind"] = "every",
            ["container"] = "Dev",
            ["intervalSeconds"] = 300,
            ["idleOnly"] = false,
            ["instruction"] = "look",
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    private async Task SettleAsync()
    {
        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        var deadline = DateTime.UtcNow.AddSeconds(15);

        for (var quietPasses = 0; quietPasses < 5 && DateTime.UtcNow < deadline;)
        {
            var idle = new[] { Dev, Manager }.All(id => host.Find(id)?.State is null or ContainerState.Idle);
            quietPasses = idle ? quietPasses + 1 : 0;
            await Task.Delay(100, Ct);
        }
    }

    private async Task<Message> AwaitRowAsync(Func<Message, bool> match, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if ((await Log.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
                .FirstOrDefault(match) is { } row)
            {
                return row;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"No row: {what}.");
    }

    private async Task<JsonElement> SpentTodayAsync(string trigger)
    {
        var rows = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/containers/Dev/triggers", Ct);
        return rows.EnumerateArray().Single(r => r.GetProperty("id").GetString() == trigger).GetProperty("spentToday").Clone();
    }

    /// <summary>Every ledger row, admission hold, backlog dispatch and outcome link, as text.</summary>
    private async Task<List<string>> KeptRowsAsync()
    {
        var rows = new List<string>();
        foreach (var table in new[]
                 {
                     "usage_ledger", "workflow_ledger", "delivery_ledger", "nudge_ledger", "admission_holds",
                     "backlog_dispatches", "workflow_outcome_links", "outcomes",
                 })
        {
            rows.Add(table);
            rows.AddRange(await TableAsync(table));
        }

        return rows;
    }

    private async Task<List<string>> TableAsync(string table)
    {
        var rows = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(JsonSerializer.Serialize(values));
        }

        return rows;
    }

    private async Task<List<string>> RowsAsync(string sql)
    {
        var rows = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        return rows;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        return connection;
    }
}
