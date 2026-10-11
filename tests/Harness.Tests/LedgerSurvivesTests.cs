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
/// THE LEDGER IS ACCOUNTING, NOT MEMORY. On the real Host, with a fake agent member (Dev), the real
/// sample-echo plugin (Echo) and a Manager: every finished run writes its ledger row - a plugin's
/// with no tokens - and Reset's "Delete memory" and team deletion, which take or orphan the log
/// rows, leave the ledger, the workflow's spend, the trigger's spend today and the workflow's
/// elapsed time as they were. The export answers the rows to a person and refuses a machine.
/// </summary>
public sealed class LedgerSurvivesTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string TeamLabel = "Ledger books";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-ledger-survives-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    /// <summary>When set, a Manager run waits on it, so a delivery can be held in the Manager's queue.</summary>
    private TaskCompletionSource? _holdManager;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private IUsageLedger Ledger => Services.GetRequiredService<IUsageLedger>();

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Echo => new(_team, "Echo");

    private ContainerId Manager => new(_team, TeamRegistry.DefaultManagerName);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);

        _agents.Behaviour = async invocation =>
        {
            if (invocation.Container.Name == "Dev")
            {
                await Services.GetRequiredService<MemberReports>().HandbackAsync(invocation.Container, "found one", Ct);
                return new AgentResult(0, "handed back", Usage: new InvocationUsage(1000, 500, "test", cachedIn: 2000));
            }

            if (invocation.Container.Name == TeamRegistry.DefaultManagerName && _holdManager is { } hold)
            {
                await hold.Task.WaitAsync(Ct);
            }

            return new AgentResult(0, $"noted by {invocation.Container.Name}", Usage: new InvocationUsage(600, 100, "test"));
        };

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync(TeamLabel, "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo",
            agent = "plugin:sample-echo",
            config = new { mode = "upper" },
        }, Ct)).StatusCode);

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
    public async Task A_plugin_run_writes_a_row_with_no_tokens_and_its_agent_time_and_an_agent_run_writes_its_own()
    {
        var delivery = await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Echo), JsonSerializer.Serialize(new { instruction = "hello" }), "console"), Ct);
        var terminal = await AwaitRowAsync(m => m.Source == Echo.ToString() && m.CorrelationId == delivery.CorrelationId, "Echo's terminal row");

        var row = Assert.Single(await Ledger.ReadRecentRunsAsync(Echo, 10, Ct));
        Assert.Equal(terminal.Seq, row.RunSeq);
        Assert.Equal(MemberRef.PluginKind, row.MemberKind);
        Assert.Equal("plugin:sample-echo", row.AgentPreset);
        Assert.True(row.Measured);
        Assert.Null(row.TokensIn);
        Assert.Null(row.TokensOut);
        Assert.Null(row.TokensCachedIn);
        Assert.Null(row.TokensCacheCreation);
        Assert.Null(row.TokensCombined);
        Assert.Equal(0, row.Billable);
        Assert.Equal(delivery.OccurredAt, row.QueuedAt);
        Assert.NotNull(row.StartedAt);
        Assert.True(row.EndedAt >= row.StartedAt);
        Assert.Equal(TeamLabel, row.TeamName);

        var devDelivery = await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = "look" }), "console"), Ct);
        await AwaitRowAsync(m => m.Source == Dev.ToString() && m.CorrelationId == devDelivery.CorrelationId, "Dev's terminal row");

        var dev = Assert.Single(await Ledger.ReadRecentRunsAsync(Dev, 10, Ct));
        Assert.Equal(MemberRef.AgentKind, dev.MemberKind);
        Assert.Equal("claude-headless", dev.AgentPreset);
        Assert.Equal(1000 + 500 + 200, dev.Billable);
    }

    [Fact]
    public async Task Reset_with_delete_memory_deletes_the_log_rows_and_leaves_the_ledger_the_workflow_spend_and_the_trigger_spend_today()
    {
        var (trigger, devRow, managerRow) = await FireScheduleAsync();

        // The Manager declares the workflow; the ledger keeps the declaration.
        var declared = await Log.AppendAsync(new NewMessage(
            MessageTypes.WorkflowCompleted, "{}", Manager.ToString(), managerRow.Seq), Ct);

        var workflowBefore = await Log.GetWorkflowSpendAsync(devRow.CorrelationId, Ct);
        var sinceNudgeBefore = await Log.GetSpendSinceNudgeAsync(devRow.CorrelationId, Ct);
        var todayBefore = await SpentTodayAsync(trigger);
        var elapsedBefore = (await Ledger.ReadWorkflowAsync(devRow.CorrelationId, Ct))!.ElapsedSeconds;
        var ledgerBefore = await LedgerRowsAsync();
        var recentBefore = await _person.GetStringAsync($"/api/teams/{_team}/containers/Dev/cost", Ct);

        // Dev's run and the Manager run it woke are both measured, and both are the trigger's.
        Assert.Equal(1000 + 500 + 200 + 600 + 100, todayBefore.GetProperty("billableTokens").GetInt64());
        Assert.Equal(2, todayBefore.GetProperty("measuredRuns").GetInt32());
        Assert.NotNull(elapsedBefore);

        var reset = await _person.PostAsJsonAsync($"/api/teams/{_team}/reset", new
        {
            members = new[] { "Dev", TeamRegistry.DefaultManagerName },
            forgetHistory = true,
            purge = true,
            clearTranscripts = true,
        }, Ct);
        Assert.True(reset.IsSuccessStatusCode, await reset.Content.ReadAsStringAsync(Ct));

        // The member's memory is gone from the log: its run, the Manager's run and the declaration.
        Assert.Null(await Log.FindAsync(devRow.Seq, Ct));
        Assert.Null(await Log.FindAsync(managerRow.Seq, Ct));
        Assert.Null(await Log.FindAsync(declared.Seq, Ct));

        // The accounting is not.
        Assert.Equal(ledgerBefore, await LedgerRowsAsync());
        Assert.Equal(workflowBefore, await Log.GetWorkflowSpendAsync(devRow.CorrelationId, Ct));
        Assert.Equal(sinceNudgeBefore, await Log.GetSpendSinceNudgeAsync(devRow.CorrelationId, Ct));
        Assert.Equal(todayBefore.GetRawText(), (await SpentTodayAsync(trigger)).GetRawText());
        Assert.Equal(recentBefore, await _person.GetStringAsync($"/api/teams/{_team}/containers/Dev/cost", Ct));

        // The workflow's elapsed time is read from the workflow ledger, and a backlog dispatch of
        // that workflow still reads completed with it.
        var workflow = await Ledger.ReadWorkflowAsync(devRow.CorrelationId, Ct);
        Assert.NotNull(workflow);
        Assert.Equal(declared.Seq, workflow.CloseSeq);
        Assert.Equal(elapsedBefore, workflow.ElapsedSeconds);

        var stats = await BacklogExecutionRecord.ForAsync(
            new BacklogDispatch(1, 1, _team, TeamLabel, devRow.CorrelationId, "", "", null, null), Log, Ct, Ledger);
        Assert.Equal(BacklogExecutionRecord.Completed, stats.Outcome);
        Assert.Equal(elapsedBefore, stats.ElapsedSeconds);
        Assert.Equal(workflowBefore.TokensSpent, stats.Tokens);
    }

    [Fact]
    public async Task A_reset_while_a_manager_run_is_queued_behind_the_handback_leaves_its_row_queued_at_trigger_and_the_trigger_spend_today()
    {
        // The Manager is busy on work of its own, so the hand-back Dev's scheduled run writes waits
        // in the Manager's queue.
        _holdManager = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Manager), JsonSerializer.Serialize(new { instruction = "hold" }), "console"), Ct);
        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        await UntilAsync(() => host.Find(Manager)?.State == ContainerState.Running, "the Manager running its own work");

        var trigger = await CreateScheduleAsync();
        var after = await Log.HighestSeqAsync(Ct);
        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(DateTimeOffset.UtcNow.AddSeconds(301), Ct);

        var dev = await AwaitRowAsync(m => m.Seq > after && m.Source == Dev.ToString(), "Dev's terminal row");
        var handback = (await Log.ReadAfterAsync(after, [MessageTypes.Handback], int.MaxValue, Ct))
            .Single(m => m.Source == Dev.ToString());
        await UntilAsync(() => host.Find(Dev)?.State is ContainerState.Idle && host.Find(Manager)?.QueueDepth > 0,
            "Dev idle and the hand-back queued for the Manager");

        // Dev's memory is deleted while the Manager's run of the hand-back is still queued. Nothing
        // on the log cites the hand-back yet, but the queued delivery does: it is retained, or the
        // Manager's run would spend and write no row at all. The rest of Dev's run goes.
        var reset = await _person.PostAsJsonAsync($"/api/teams/{_team}/reset", new
        {
            members = new[] { "Dev" },
            forgetHistory = true,
            purge = true,
        }, Ct);
        Assert.True(reset.IsSuccessStatusCode, await reset.Content.ReadAsStringAsync(Ct));
        Assert.Null(await Log.FindAsync(dev.Seq, Ct));
        Assert.NotNull(await Log.FindAsync(handback.Seq, Ct));

        _holdManager.SetResult();
        await AwaitRowAsync(m => m.Source == Manager.ToString() && m.CausationSeq == busy.Seq, "the Manager's own run");
        await SettleAsync();

        var managerRun = (await Ledger.ReadRecentRunsAsync(Manager, 10, Ct))
            .Single(r => r.Correlation == dev.CorrelationId);
        Assert.Equal(handback.OccurredAt, managerRun.QueuedAt);
        Assert.Equal($"schedule:{trigger}", managerRun.TriggerSource);
        Assert.NotNull(managerRun.TriggerFiredAt);

        // Dev's run and the Manager run it woke are both still the trigger's.
        var today = await SpentTodayAsync(trigger);
        Assert.Equal(1000 + 500 + 200 + 600 + 100, today.GetProperty("billableTokens").GetInt64());
        Assert.Equal(2, today.GetProperty("measuredRuns").GetInt32());
    }

    [Fact]
    public async Task A_reset_that_deletes_the_nudge_leaves_the_spend_since_the_nudge_on_the_same_window()
    {
        var (_, devRow, _) = await FireScheduleAsync();
        var correlation = devRow.CorrelationId;

        var after = await Log.HighestSeqAsync(Ct);
        var nudged = await _person.PostAsync($"/api/teams/{_team}/workflows/{correlation}/nudge", null, Ct);
        Assert.True(nudged.IsSuccessStatusCode, await nudged.Content.ReadAsStringAsync(Ct));
        var nudge = (await Log.ReadRangeAsync(after, int.MaxValue, Ct))
            .Single(m => m.Type == MessageTypes.InstructionFor(Manager) && m.CausationSeq == correlation);
        await AwaitRowAsync(m => m.Source == Manager.ToString() && m.CausationSeq == nudge.Seq, "the nudged Manager's run");
        await SettleAsync();

        // Only the Manager's run after the nudge is in the window, not the whole workflow.
        var sinceNudge = await Log.GetSpendSinceNudgeAsync(correlation, Ct);
        Assert.Equal(600 + 100, sinceNudge.TokensSpent);
        Assert.True(sinceNudge.TokensSpent < (await Log.GetWorkflowSpendAsync(correlation, Ct)).TokensSpent);

        // Resetting the Manager deletes the nudge - an instruction addressed to it - with its run.
        var reset = await _person.PostAsJsonAsync($"/api/teams/{_team}/reset", new
        {
            members = new[] { "Dev", TeamRegistry.DefaultManagerName },
            forgetHistory = true,
            purge = true,
        }, Ct);
        Assert.True(reset.IsSuccessStatusCode, await reset.Content.ReadAsStringAsync(Ct));
        Assert.Null(await Log.FindAsync(nudge.Seq, Ct));

        Assert.Equal(sinceNudge, await Log.GetSpendSinceNudgeAsync(correlation, Ct));
    }

    [Fact]
    public async Task A_deleted_teams_ledger_rows_remain_and_keep_its_name()
    {
        var (_, devRow, _) = await FireScheduleAsync();
        var before = await LedgerRowsAsync();
        Assert.NotEmpty(before);

        var deleted = await _person.DeleteAsync($"/api/teams/{_team}", Ct);
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        Assert.Null(Services.GetRequiredService<TeamRegistry>().ExistingName(_team));

        Assert.Equal(before, await LedgerRowsAsync());
        var run = Assert.Single(await Ledger.ReadRecentRunsAsync(Dev, 10, Ct));
        Assert.Equal(devRow.Seq, run.RunSeq);
        Assert.Equal(_team, run.TeamId);
        Assert.Equal(TeamLabel, run.TeamName);
    }

    [Fact]
    public async Task The_export_answers_the_rows_newest_first_with_the_cursor_and_a_machine_principal_gets_403()
    {
        await FireScheduleAsync();
        var echo = await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Echo), JsonSerializer.Serialize(new { instruction = "hello" }), "console"), Ct);
        await AwaitRowAsync(m => m.Source == Echo.ToString() && m.CorrelationId == echo.CorrelationId, "Echo's terminal row");
        await Log.AppendAsync(new NewMessage(
            MessageTypes.WorkflowClosed, JsonSerializer.Serialize(new { team = _team }), "person-1", echo.Seq), Ct);

        var all = await ExportAsync("");
        var instanceId = all.GetProperty("instanceId").GetString();
        Assert.True(Guid.TryParse(instanceId, out _));
        Assert.False(string.IsNullOrEmpty(all.GetProperty("ledgerStartedAt").GetString()));
        Assert.Equal(JsonValueKind.Null, all.GetProperty("nextBefore").ValueKind);

        var everything = Seqs(all);
        Assert.True(everything.Count >= 4, $"{everything.Count} rows");
        Assert.Equal(everything.OrderDescending(), everything);

        // Paged two at a time by the cursor, the pages are the whole answer, in order, once each.
        var paged = new List<long>();
        string? before = null;
        do
        {
            var page = await ExportAsync($"take=2{(before is null ? "" : $"&before={before}")}");
            var seqs = Seqs(page);
            Assert.True(seqs.Count <= 2);
            paged.AddRange(seqs);
            before = page.GetProperty("nextBefore").ValueKind == JsonValueKind.Null
                ? null
                : page.GetProperty("nextBefore").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        while (before is not null);
        Assert.Equal(everything, paged);

        // `since` leaves out what ended before it.
        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"));
        Assert.Empty(Seqs(await ExportAsync($"since={future}")));

        // A run's row carries its figures under the names the ledger keeps them.
        var run = all.GetProperty("runs").EnumerateArray().First(r => r.GetProperty("member").GetString() == "Dev");
        Assert.Equal(_team, run.GetProperty("teamId").GetString());
        Assert.True(run.GetProperty("measured").GetBoolean());
        Assert.Equal(1700, run.GetProperty("billable").GetInt64());

        // A machine principal - even one of this team holding every permit - is refused.
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            Dev.ToString(), PrincipalKind.Container, _team, Permits.All, ct: Ct);
        var machine = _factory.CreateClient();
        machine.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        Assert.Equal(HttpStatusCode.Forbidden, (await machine.GetAsync("/api/ledger/export", Ct)).StatusCode);
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>A schedule on Dev fired now: Dev hands back and finishes, and the hand-back wakes
    /// the Manager. Answers the trigger id, Dev's terminal row and the Manager's.</summary>
    private async Task<(string Trigger, Message Dev, Message Manager)> FireScheduleAsync()
    {
        var trigger = await CreateScheduleAsync();

        var after = await Log.HighestSeqAsync(Ct);
        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(DateTimeOffset.UtcNow.AddSeconds(301), Ct);

        var dev = await AwaitRowAsync(m => m.Seq > after && m.Source == Dev.ToString(), "Dev's terminal row");
        var manager = await AwaitRowAsync(
            m => m.Seq > after && m.Source == Manager.ToString() && m.CorrelationId == dev.CorrelationId,
            "the Manager's terminal row");

        await SettleAsync();
        return (trigger, dev, manager);
    }

    /// <summary>A schedule on Dev, due in 300 seconds. Answers its id.</summary>
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

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Never: {what}.");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>Waits until every member of the team has been idle for several passes, so nothing is
    /// still running when a figure is read before and after.</summary>
    private async Task SettleAsync()
    {
        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        var deadline = DateTime.UtcNow.AddSeconds(15);

        for (var quietPasses = 0; quietPasses < 5 && DateTime.UtcNow < deadline;)
        {
            var idle = new[] { Dev, Echo, Manager }.All(id => host.Find(id)?.State is null or ContainerState.Idle);
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

    private async Task<JsonElement> ExportAsync(string query)
    {
        var response = await _person.GetAsync($"/api/ledger/export?{query}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Clone();
    }

    private static List<long> Seqs(JsonElement page) =>
        [.. page.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("runSeq").GetInt64())
            .Concat(page.GetProperty("workflows").EnumerateArray().Select(w => w.GetProperty("closeSeq").GetInt64()))
            .OrderDescending()];

    /// <summary>Every ledger row of both tables, as text, for a before-and-after comparison.</summary>
    private async Task<List<string>> LedgerRowsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT json_array(run_seq, correlation, team_id, team_name, member, member_kind, agent_preset, run_outcome,
                              queued_at, started_at, ended_at, measured, tokens_in, tokens_cached_in,
                              tokens_cache_creation, tokens_out, tokens_combined, billable, trigger_source,
                              trigger_fired_at, backfilled)
            FROM usage_ledger
            UNION ALL
            SELECT json_array(close_seq, correlation, team_id, team_name, root_at, closed_at, how_closed,
                              outcome_id_at_close, backfilled)
            FROM workflow_ledger
            """;

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        return rows;
    }
}
