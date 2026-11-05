using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// RUN NOW IS THE FIRE THE SCHEDULE MAKES, AT A PERSON'S HAND. `POST
/// /api/teams/{team}/triggers/{id}/run` goes through <see cref="TriggerSweep.RunNowAsync"/>: source
/// `schedule:&lt;id&gt;`, the trigger's wakeManager on its instruction, its daily cap asked, and a
/// capped Run now skipped with its `schedule.skipped` row. Whatever happens, one `schedule.run-now`
/// tenant row names the person. The stored next due time is not moved by a fire.
///
/// On the real Host with a fake agent member (Dev, 1,500 billable tokens a run) and the background
/// runner off, so nothing fires but what a test asks for.
/// </summary>
public sealed class TriggerRunNowTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-run-now-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;

    /// <summary>The Host's clock, twelve hours from midnight (see <see cref="NoonClock"/>): every time
    /// this class fires or arms at is read from it, so no run here sees the day change.</summary>
    private readonly NoonClock _clock = new();
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Manager => new(_team, TeamRegistry.DefaultManagerName);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _agents.Behaviour = invocation => Task.FromResult(invocation.Container.Name == "Dev"
            ? new AgentResult(0, "nothing new", Usage: new InvocationUsage(1000, 500, "test"))
            : new AgentResult(0, "noted", Usage: new InvocationUsage(600, 100, "test")));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting("ScheduleRunnerEnabled", "false")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents).AddSingleton<TimeProvider>(_clock)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("RunNow", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

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

    private async Task<string> CreateAsync(object body)
    {
        var created = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", body, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    /// <summary>Daily at 03:00 UTC on Dev: never due while a test runs.</summary>
    private Task<string> DailyAsync(long? cap = null) => CreateAsync(new
    {
        name = "Nightly",
        kind = "cron",
        expression = "0 0 3 * * *",
        timezone = "UTC",
        container = "Dev",
        idleOnly = false,
        instruction = "look",
        wakeManager = "never",
        dailyTokenCap = cap,
    });

    private async Task<JsonElement> RunNowAsync(string id)
    {
        var response = await _person.PostAsync($"/api/teams/{_team}/triggers/{id}/run", null, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<Message> AwaitRowAsync(IReadOnlyCollection<string> types, Func<Message, bool> match, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if ((await Log.ReadAfterAsync(0, types, int.MaxValue, Ct)).FirstOrDefault(match) is { } row) return row;
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"No row: {what}.");
    }

    /// <summary>Waits for Dev's run that <paramref name="after"/> started to end, and the team to go quiet.</summary>
    private async Task SettleAsync(long after)
    {
        var row = await AwaitRowAsync(
            [MessageTypes.Completed, MessageTypes.Failed], m => m.Seq > after && m.Source == Dev.ToString(), "Dev's terminal row");

        var cursors = Services.GetRequiredService<ICursors>();
        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        var deadline = DateTime.UtcNow.AddSeconds(15);

        for (var quietPasses = 0; quietPasses < 5 && DateTime.UtcNow < deadline;)
        {
            var read = await cursors.PositionAsync(Manager, Ct) >= row.Seq;
            var idle = host.Find(Manager)!.State == ContainerState.Idle && host.Find(Dev)!.State == ContainerState.Idle;
            quietPasses = read && idle ? quietPasses + 1 : 0;
            await Task.Delay(100, Ct);
        }
    }

    private async Task<IReadOnlyList<TenantEvent>> TenantRowsAsync(string action, string id) =>
        [.. (await Services.GetRequiredService<ITenantLog>().ReadAsync(take: 10_000, ct: Ct)).Events
            .Where(e => e.Action == action && e.Subject == id)];

    private async Task<IReadOnlyList<Message>> InstructionsAsync(string id) =>
        [.. (await Log.ReadAfterAsync(0, [MessageTypes.InstructionFor(Dev)], int.MaxValue, Ct)).Where(m => m.Source == $"schedule:{id}")];

    /// <summary>
    /// A SCHEDULED RUN IS A TRIGGER'S, WITH NO PERSON, through the real sweep: the fired instruction, every row that
    /// follows from it, the run's snapshot and its ledger row carry <c>origin_kind = trigger</c> and the trigger's
    /// source, and no actor - though a person set the schedule up.
    /// </summary>
    [Fact]
    public async Task A_scheduled_fire_and_every_row_after_it_carry_the_trigger_and_no_person()
    {
        var id = await DailyAsync();
        var due = (await Services.GetRequiredService<ITriggerStore>().FindAsync(id, Ct))!.NextDueAt!.Value;
        var after = await Log.HighestSeqAsync(Ct);

        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(due, Ct);
        await SettleAsync(after);

        var instruction = Assert.Single(await InstructionsAsync(id));
        var source = $"schedule:{id}";

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=false");
        await connection.OpenAsync(Ct);

        async Task<List<object?[]>> RowsAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$c", instruction.CorrelationId);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(Ct))
            {
                rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i))]);
            }

            return rows;
        }

        var workflow = await RowsAsync(
            "SELECT origin_kind, origin_actor_id, origin_actor_email, origin_trigger, depth FROM messages WHERE correlation_id = $c ORDER BY seq");
        // The fire itself, and the run's started and terminal rows caused by it.
        Assert.True(workflow.Count >= 3, $"{workflow.Count} rows");
        Assert.Contains(workflow, r => Convert.ToInt64(r[4]) >= 1);
        Assert.All(workflow, r => Assert.Equal(new object?[] { OriginKinds.Trigger, null, null, source }, r[..4]));

        var snapshot = Assert.Single(await RowsAsync(
            "SELECT origin_kind, origin_actor_id, origin_actor_email, origin_trigger FROM execution_snapshots WHERE correlation = $c AND member = 'Dev'"));
        Assert.Equal(new object?[] { OriginKinds.Trigger, null, null, source }, snapshot);
        var ledger = Assert.Single(await RowsAsync("SELECT origin_kind, origin_actor_id FROM usage_ledger WHERE correlation = $c AND member = 'Dev'"));
        Assert.Equal(new object?[] { OriginKinds.Trigger, null }, ledger);
    }

    [Fact]
    public async Task Run_now_fires_the_schedules_own_instruction_and_writes_a_tenant_row_naming_the_person()
    {
        var id = await DailyAsync();
        var store = Services.GetRequiredService<ITriggerStore>();
        var due = (await store.FindAsync(id, Ct))!.NextDueAt;
        var after = await Log.HighestSeqAsync(Ct);

        var run = await RunNowAsync(id);

        Assert.Equal("fired", run.GetProperty("outcome").GetString());
        var instruction = Assert.Single(await InstructionsAsync(id));
        Assert.Equal(instruction.Seq, run.GetProperty("seq").GetInt64());
        var payload = JsonDocument.Parse(instruction.Payload).RootElement;
        Assert.Equal("look", payload.GetProperty(PayloadFields.Instruction).GetString());
        Assert.Equal("never", payload.GetProperty(PayloadFields.WakeManager).GetString());

        await SettleAsync(after);
        Assert.Equal(1, _agents.RunsFor(Dev));

        var row = (await store.FindAsync(id, Ct))!;
        Assert.Equal("fired", row.LastOutcome);
        Assert.Equal(instruction.Seq, row.LastSeq);
        Assert.Equal(due, row.NextDueAt);

        var tenant = Assert.Single(await TenantRowsAsync(TenantActions.ScheduleRunNow, id));
        Assert.Equal(Email, tenant.ActorEmail);
        var detail = JsonDocument.Parse(tenant.Detail!).RootElement;
        Assert.Equal("fired", detail.GetProperty("outcome").GetString());
        Assert.Equal(instruction.Seq, detail.GetProperty("seq").GetInt64());

        // The schedule's own `schedule.fired` row, as the sweep's fire writes it.
        var fired = Assert.Single(await TenantRowsAsync(TenantActions.ScheduleFired, id));
        Assert.Equal($"schedule:{id}", fired.ActorId);
        Assert.True(JsonDocument.Parse(fired.Detail!).RootElement.GetProperty("runNow").GetBoolean());
    }

    [Fact]
    public async Task A_capped_run_now_is_skipped_with_its_schedule_skipped_row_and_fires_nothing()
    {
        var id = await DailyAsync(cap: 1000);
        var after = await Log.HighestSeqAsync(Ct);

        Assert.Equal("fired", (await RunNowAsync(id)).GetProperty("outcome").GetString());
        await SettleAsync(after);

        var capped = await RunNowAsync(id);
        Assert.Equal("capped", capped.GetProperty("outcome").GetString());
        Assert.Equal(MessageTypes.ScheduleSkippedCapReason, capped.GetProperty("reason").GetString());
        Assert.True(capped.GetProperty("trigger").GetProperty("capReachedToday").GetBoolean());

        var skip = Assert.Single(
            await Log.ReadAfterAsync(0, [MessageTypes.ScheduleSkipped], int.MaxValue, Ct), m => m.Source == $"schedule:{id}");
        Assert.StartsWith(
            MessageTypes.ScheduleSkippedCapReason,
            JsonDocument.Parse(skip.Payload).RootElement.GetProperty("reason").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(InterruptionReasons.TokenCap, JsonDocument.Parse(skip.Payload).RootElement.GetProperty(PayloadFields.ReasonCode).GetString());
        Assert.Single(await TenantRowsAsync(TenantActions.ScheduleSkipped, id));

        Assert.Single(await InstructionsAsync(id));
        Assert.Equal(1, _agents.RunsFor(Dev));

        var runs = await TenantRowsAsync(TenantActions.ScheduleRunNow, id);
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, e => JsonDocument.Parse(e.Detail!).RootElement.GetProperty("outcome").GetString() == "capped");
    }

    /// <summary>A person's pause and a busy member skip a Run now, each with its own code from the closed set
    /// beside the sentence: the code is the cause the sweep found, never read off the words.</summary>
    [Fact]
    public async Task A_paused_team_and_a_busy_member_skip_with_paused_by_person_and_busy()
    {
        var daily = await DailyAsync();
        var idle = await CreateAsync(new
        {
            name = "Idle only",
            kind = "cron",
            expression = "0 0 3 * * *",
            timezone = "UTC",
            container = "Dev",
            idleOnly = true,
            instruction = "look",
            wakeManager = "never",
        });

        (await _person.PostAsync($"/api/teams/{_team}/pause", null, Ct)).EnsureSuccessStatusCode();
        var paused = await RunNowAsync(daily);
        Assert.Equal(("skipped", MessageTypes.ScheduleSkippedPausedReason),
            (paused.GetProperty("outcome").GetString(), paused.GetProperty("reason").GetString()));
        Assert.Equal(InterruptionReasons.PausedByPerson, await SkipCodeAsync(daily));
        (await _person.PostAsync($"/api/teams/{_team}/resume", null, Ct)).EnsureSuccessStatusCode();

        // Dev is mid-run until the gate opens, so the idle-only trigger finds it busy.
        var gate = new TaskCompletionSource();
        var running = new TaskCompletionSource();
        var behaviour = _agents.Behaviour;
        _agents.Behaviour = async invocation =>
        {
            if (invocation.Container.Name == "Dev")
            {
                running.TrySetResult();
                await gate.Task;
            }

            return await behaviour(invocation);
        };

        var after = await Log.HighestSeqAsync(Ct);
        await Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(Dev), """{"instruction":"work"}""", "console"), Ct);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var busy = await RunNowAsync(idle);
        Assert.Equal(("skipped", MessageTypes.ScheduleSkippedBusyReason),
            (busy.GetProperty("outcome").GetString(), busy.GetProperty("reason").GetString()));
        Assert.Equal(InterruptionReasons.Busy, await SkipCodeAsync(idle));

        gate.SetResult();
        await SettleAsync(after);
    }

    private async Task<string?> SkipCodeAsync(string id)
    {
        var skip = Assert.Single(
            await Log.ReadAfterAsync(0, [MessageTypes.ScheduleSkipped], int.MaxValue, Ct), m => m.Source == $"schedule:{id}");
        return JsonDocument.Parse(skip.Payload).RootElement.GetProperty(PayloadFields.ReasonCode).GetString();
    }

    /// <summary>A solution's run-at-install is this same call with its own action and the clock
    /// counted on from now: at the cap it is skipped with the cap's row, like a person's Run now.</summary>
    [Fact]
    public async Task A_capped_run_at_install_goes_through_the_same_call_and_is_skipped_with_its_row()
    {
        var id = await DailyAsync(cap: 1000);
        var after = await Log.HighestSeqAsync(Ct);

        Assert.Equal("fired", (await RunNowAsync(id)).GetProperty("outcome").GetString());
        await SettleAsync(after);

        var run = await Services.GetRequiredService<TriggerSweep>().RunNowAsync(
            id, _clock.GetUtcNow(), "installer-id", "installer@example.test", TenantActions.ScheduleRunAtInstall,
            countOnFromNow: true, Ct);

        Assert.NotNull(run);
        Assert.Equal(("capped", MessageTypes.ScheduleSkippedCapReason), (run.Outcome, run.Reason));
        Assert.Single(
            await Log.ReadAfterAsync(0, [MessageTypes.ScheduleSkipped], int.MaxValue, Ct), m => m.Source == $"schedule:{id}");
        Assert.Single(await TenantRowsAsync(TenantActions.ScheduleSkipped, id));
        Assert.Single(await InstructionsAsync(id));

        var installed = Assert.Single(await TenantRowsAsync(TenantActions.ScheduleRunAtInstall, id));
        Assert.Equal("installer@example.test", installed.ActorEmail);
        Assert.Equal("capped", JsonDocument.Parse(installed.Detail!).RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Run_now_refuses_an_event_trigger_and_an_unknown_one()
    {
        var id = await CreateAsync(new
        {
            name = "On file",
            kind = "event",
            eventType = "file.changed",
            container = "Dev",
            instruction = "look",
        });

        var refused = await _person.PostAsync($"/api/teams/{_team}/triggers/{id}/run", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty(await TenantRowsAsync(TenantActions.ScheduleRunNow, id));

        var missing = await _person.PostAsync($"/api/teams/{_team}/triggers/no-such/run", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
