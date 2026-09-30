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
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

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
        Assert.Single(await TenantRowsAsync(TenantActions.ScheduleSkipped, id));

        Assert.Single(await InstructionsAsync(id));
        Assert.Equal(1, _agents.RunsFor(Dev));

        var runs = await TenantRowsAsync(TenantActions.ScheduleRunNow, id);
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, e => JsonDocument.Parse(e.Detail!).RootElement.GetProperty("outcome").GetString() == "capped");
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
