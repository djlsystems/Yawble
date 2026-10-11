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
/// TRIGGER COST CONTROL. A trigger chooses what a run it started does to the
/// Manager when it ends (`wakeManager`), and may carry a daily token cap over what its runs, and the
/// Manager runs they woke, measured today.
///
/// On the real Host: an agent member (Dev, a fake agent), the real sample-echo plugin (Echo), and a
/// Manager whose invocations are counted. Schedules are fired through <see cref="TriggerSweep"/> as
/// if their due time had come - the background sweep never reaches them during a test.
/// </summary>
public sealed class TriggerCostControlTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-trigger-cost-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    /// <summary>What Dev's run does. Default: finishes without handing back, measured at 1,500.</summary>
    private Func<AgentInvocation, Task<AgentResult>> _dev = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Echo => new(_team, "Echo");

    private ContainerId Manager => new(_team, TeamRegistry.DefaultManagerName);

    private static InvocationUsage Measured(int tokensIn, int tokensOut) => new(tokensIn, tokensOut, "test");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);

        _dev = _ => Task.FromResult(new AgentResult(0, "nothing new", Usage: Measured(1000, 500)));
        _agents.Behaviour = invocation => invocation.Container.Name == "Dev"
            ? _dev(invocation)
            : Task.FromResult(new AgentResult(0, $"noted by {invocation.Container.Name}", Usage: Measured(600, 100)));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Cost", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

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

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>An every-5-minutes schedule on <paramref name="member"/>, made through the route.
    /// <paramref name="wakeManager"/> null leaves the field out, so the route's default applies.</summary>
    private async Task<JsonElement> ScheduleAsync(
        string member, string instruction, string? wakeManager = null, long? dailyTokenCap = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = $"Poll {member}",
            ["kind"] = "every",
            ["container"] = member,
            ["intervalSeconds"] = 300,
            ["idleOnly"] = false,
            ["instruction"] = instruction,
        };
        if (wakeManager is not null) body["wakeManager"] = wakeManager;
        if (dailyTokenCap is not null) body["dailyTokenCap"] = dailyTokenCap;

        var created = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", body, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));

        return await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>Sweeps at <paramref name="at"/> and returns <paramref name="member"/>'s terminal
    /// row for the run that sweep fired, after the Manager has read past it and anything it was
    /// handed has run.</summary>
    private async Task<Message> FireAsync(ContainerId member, DateTimeOffset at)
    {
        var after = await Log.HighestSeqAsync(Ct);
        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(at, Ct);

        var row = await AwaitRowAsync(
            [MessageTypes.Completed, MessageTypes.Failed],
            m => m.Seq > after && m.Source == member.ToString(),
            $"{member.Name}'s terminal row");

        var instruction = (await Log.ReadCorrelationAsync(row.CorrelationId, Ct))
            .First(m => m.Type == MessageTypes.InstructionFor(member));
        Assert.StartsWith("schedule:", instruction.Source, StringComparison.Ordinal);

        await SettleAsync(row, member);
        return row;
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

    /// <summary>Waits until the Manager's pump has read past <paramref name="row"/> and everyone
    /// is idle: after this, a wake that row was going to cause has happened.</summary>
    private async Task SettleAsync(Message row, ContainerId member)
    {
        var cursors = Services.GetRequiredService<ICursors>();
        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        var deadline = DateTime.UtcNow.AddSeconds(15);

        for (var quietPasses = 0; quietPasses < 5 && DateTime.UtcNow < deadline;)
        {
            var read = await cursors.PositionAsync(Manager, Ct) >= row.Seq;
            var idle = host.Find(Manager)!.State == ContainerState.Idle && host.Find(member)!.State == ContainerState.Idle;
            quietPasses = read && idle ? quietPasses + 1 : 0;
            await Task.Delay(100, Ct);
        }
    }

    private static string? WakeKey(Message row) =>
        JsonDocument.Parse(row.Payload).RootElement.TryGetProperty(PayloadFields.WakeManager, out var value)
            ? value.GetString()
            : null;

    private async Task<AgentResult> HandBack(AgentInvocation invocation, string delivered, InvocationUsage usage)
    {
        await Services.GetRequiredService<MemberReports>().HandbackAsync(invocation.Container, delivered, Ct);
        return new AgentResult(0, "handed back", Usage: usage);
    }

    private async Task<JsonElement> TriggerAsync(string member, string id)
    {
        var rows = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/containers/{member}/triggers", Ct);
        return rows.EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
    }

    private static DateTimeOffset Due => DateTimeOffset.UtcNow.AddSeconds(301);

    /// <summary>Fires trigger <paramref name="id"/> again, now: armed for a moment ahead and swept
    /// at exactly that moment, so nothing is counted missed.</summary>
    private async Task<Message> FireAgainAsync(string id, ContainerId member)
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(3600);
        var armed = await _person.PatchAsJsonAsync($"/api/teams/{_team}/triggers/{id}", new { nextDueAt = at }, Ct);
        Assert.Equal(HttpStatusCode.OK, armed.StatusCode);
        return await FireAsync(member, at);
    }

    /// <summary>Whether a Manager run answered <paramref name="row"/>: the Manager's terminal row
    /// is caused by the row that woke it.</summary>
    private async Task<bool> ManagerAnsweredAsync(Message row) =>
        (await Log.ReadAfterAsync(row.Seq, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
            .Any(m => m.Source == Manager.ToString() && m.CausationSeq == row.Seq);

    // ---- A: whom a triggered run wakes ------------------------------------------------------

    [Fact]
    public async Task A_new_trigger_defaults_to_waking_the_manager_only_on_a_handback_or_a_failure()
    {
        var created = await ScheduleAsync("Dev", "look");

        Assert.Equal(WakeManagerPolicy.OnHandbackOrFailure, created.GetProperty("wakeManager").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("dailyTokenCap").ValueKind);
        Assert.False(created.GetProperty("capReachedToday").GetBoolean());
        Assert.Equal(0, created.GetProperty("spentToday").GetProperty("billableTokens").GetInt64());
    }

    [Fact]
    public async Task A_scheduled_agent_run_that_finishes_without_handing_back_wakes_nobody_under_the_default()
    {
        await ScheduleAsync("Dev", "look");

        var row = await FireAsync(Dev, Due);

        // Still a completed row the card and the feed show, carrying the trigger's choice.
        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.Equal(WakeManagerPolicy.OnHandbackOrFailure, WakeKey(row));
        Assert.Equal(0, _agents.RunsFor(Manager));

        // One run, not two: no idle offer is spent on it, and the platform declares the workflow.
        Assert.Equal(1, _agents.RunsFor(Dev));
        var declared = await AwaitRowAsync(
            [MessageTypes.WorkflowCompleted], m => m.CorrelationId == row.CorrelationId, "the workflow's declaration");
        Assert.True(JsonDocument.Parse(declared.Payload).RootElement
            .GetProperty(UndeclarableWorkflows.DeclaredByPlatformField).GetBoolean());
    }

    /// <summary>The pump moves a member's cursor only after its batch, so a quick run can end
    /// while its own wake still reads as undelivered. The team is paused here to hold the cursor
    /// there for certain, where a loaded host only sometimes does.</summary>
    [Fact]
    public async Task A_quiet_run_ending_before_the_pump_moves_its_cursor_is_still_declared()
    {
        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        await host.SetPausedAsync(_team, true);

        var wake = await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev),
            WakeManagerPolicy.InstructionPayload("look", WakeManagerPolicy.OnHandbackOrFailure),
            "schedule:held"), Ct);
        Assert.True(await Services.GetRequiredService<ICursors>().PositionAsync(Dev, Ct) < wake.Seq);

        var declared = await Services.GetRequiredService<UndeclarableWorkflows>()
            .OnRunEndingAsync(Dev, wake.Seq, succeeded: true, Ct);

        Assert.True(declared);
        Assert.Contains(
            await Log.ReadCorrelationAsync(wake.CorrelationId, Ct),
            m => m.Type == MessageTypes.WorkflowCompleted);
    }

    [Fact]
    public async Task The_same_run_handing_back_wakes_the_manager_exactly_once()
    {
        _dev = invocation => HandBack(invocation, "found one", Measured(1000, 500));
        await ScheduleAsync("Dev", "look");

        var row = await FireAsync(Dev, Due);

        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.Equal(1, _agents.RunsFor(Manager));
    }

    [Fact]
    public async Task A_failure_wakes_the_manager_under_the_default()
    {
        _dev = _ => Task.FromResult(new AgentResult(1, "the mailbox is gone"));
        await ScheduleAsync("Dev", "look");

        var row = await FireAsync(Dev, Due);

        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.Equal(1, _agents.RunsFor(Manager));
    }

    [Fact]
    public async Task A_failure_under_never_wakes_nobody_and_still_shows_on_the_card_and_the_feed()
    {
        _dev = _ => Task.FromResult(new AgentResult(1, "the mailbox is gone"));
        await ScheduleAsync("Dev", "look", wakeManager: WakeManagerPolicy.Never);

        var row = await FireAsync(Dev, Due);

        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.Equal(WakeManagerPolicy.Never, WakeKey(row));
        Assert.NotNull(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(Dev)!.Snapshot().Failed);
        Assert.Equal(0, _agents.RunsFor(Manager));
    }

    [Fact]
    public async Task Never_still_lets_a_handback_wake_the_manager_on_its_own_row()
    {
        _dev = invocation => HandBack(invocation, "found one", Measured(1000, 500));
        await ScheduleAsync("Dev", "look", wakeManager: WakeManagerPolicy.Never);

        await FireAsync(Dev, Due);

        Assert.Equal(1, _agents.RunsFor(Manager));
    }

    [Fact]
    public async Task Always_behaves_as_today()
    {
        await ScheduleAsync("Dev", "look", wakeManager: WakeManagerPolicy.Always);

        var row = await FireAsync(Dev, Due);

        // Byte for byte what a fire was before the choice: no key on the instruction or the row.
        var instruction = (await Log.ReadCorrelationAsync(row.CorrelationId, Ct))
            .First(m => m.Type == MessageTypes.InstructionFor(Dev));
        Assert.Equal("""{"instruction":"look"}""", instruction.Payload);
        Assert.Null(WakeKey(row));

        // As today: the run's completion wakes the Manager.
        Assert.True(await ManagerAnsweredAsync(row));
    }

    [Fact]
    public async Task A_manager_dispatched_run_still_wakes_the_manager()
    {
        // Even an instruction from the Manager that claims a choice is not read: only a trigger's is.
        var told = await Log.AppendAsync(
            new NewMessage(
                MessageTypes.InstructionFor(Dev),
                """{"instruction":"do this","wakeManager":"never"}""",
                Manager.ToString()),
            Ct);

        var row = await AwaitRowAsync(
            [MessageTypes.Completed], m => m.CausationSeq == told.Seq, "Dev's answer to the Manager");
        await SettleAsync(row, Dev);

        Assert.Null(WakeKey(row));
        Assert.True(await ManagerAnsweredAsync(row));
    }

    [Fact]
    public async Task A_scheduled_plugin_run_that_finishes_wakes_nobody_under_the_default()
    {
        await ScheduleAsync("Echo", "something new");

        var row = await FireAsync(Echo, Due);

        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.Equal(WakeManagerPolicy.OnHandbackOrFailure, WakeKey(row));
        Assert.Equal(0, _agents.RunsFor(Manager));
    }

    [Fact]
    public async Task A_scheduled_plugin_run_handing_back_wakes_the_manager_exactly_once()
    {
        await ScheduleAsync("Echo", "handback:found one");

        var row = await FireAsync(Echo, Due);

        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.Equal(1, _agents.RunsFor(Manager));
    }

    [Fact]
    public async Task A_scheduled_plugin_failure_wakes_the_manager_under_the_default_and_not_under_never()
    {
        await ScheduleAsync("Echo", "fail:the mailbox is gone");
        var failed = await FireAsync(Echo, Due);

        Assert.Equal(MessageTypes.Failed, failed.Type);
        Assert.Equal(1, _agents.RunsFor(Manager));

        var created = await ScheduleAsync("Echo", "fail:the mailbox is gone", wakeManager: WakeManagerPolicy.Never);
        var never = await FireAgainAsync(created.GetProperty("id").GetString()!, Echo);

        Assert.Equal(MessageTypes.Failed, never.Type);
        Assert.Equal(WakeManagerPolicy.Never, WakeKey(never));
        Assert.Equal(1, _agents.RunsFor(Manager));
    }

    [Fact]
    public async Task A_scheduled_plugin_run_under_always_wakes_the_manager_as_today()
    {
        await ScheduleAsync("Echo", "something new", wakeManager: WakeManagerPolicy.Always);

        var row = await FireAsync(Echo, Due);

        Assert.Null(WakeKey(row));
        Assert.True(await ManagerAnsweredAsync(row));
    }

    [Fact]
    public async Task An_unknown_choice_or_a_cap_below_one_is_refused_and_a_patch_can_change_and_clear_them()
    {
        var bad = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", new
        {
            name = "Bad", kind = "every", container = "Dev", intervalSeconds = 300, instruction = "x",
            wakeManager = "sometimes",
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("wakeManager", await bad.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var zero = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", new
        {
            name = "Bad", kind = "every", container = "Dev", intervalSeconds = 300, instruction = "x",
            dailyTokenCap = 0,
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);

        var id = (await ScheduleAsync("Dev", "look", dailyTokenCap: 5000)).GetProperty("id").GetString();

        var changed = await _person.PatchAsJsonAsync(
            $"/api/teams/{_team}/triggers/{id}", new { wakeManager = "never", dailyTokenCap = 9000 }, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var body = await changed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("never", body.GetProperty("wakeManager").GetString());
        Assert.Equal(9000, body.GetProperty("dailyTokenCap").GetInt64());

        var nullWake = await _person.PatchAsync(
            $"/api/teams/{_team}/triggers/{id}", JsonContent.Create(new { wakeManager = (string?)null }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, nullWake.StatusCode);

        var cleared = await _person.PatchAsync(
            $"/api/teams/{_team}/triggers/{id}", JsonContent.Create(new { dailyTokenCap = (long?)null }), Ct);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var read = await TriggerAsync("Dev", id!);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("dailyTokenCap").ValueKind);
        Assert.Equal("never", read.GetProperty("wakeManager").GetString());
    }

    [Fact]
    public async Task A_trigger_turned_off_turned_on_and_its_cap_set_each_say_what_changed_on_its_tenant_row()
    {
        var id = (await ScheduleAsync("Dev", "look", dailyTokenCap: 200_000)).GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await _person.PatchAsJsonAsync($"/api/teams/{_team}/triggers/{id}", new { enabled = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _person.PatchAsJsonAsync($"/api/teams/{_team}/triggers/{id}", new { enabled = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _person.PatchAsJsonAsync($"/api/teams/{_team}/triggers/{id}", new { dailyTokenCap = 250_000 }, Ct)).StatusCode);

        var rows = (await Services.GetRequiredService<ITenantLog>().ReadAsync(take: 10_000, ct: Ct)).Events
            .Where(e => e.Action == TenantActions.ScheduleChanged && e.Subject == id)
            .OrderBy(e => e.Seq)
            .Select(e => JsonDocument.Parse(e.Detail!).RootElement.GetProperty("changed").EnumerateArray().Single())
            .ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(("enabled", true, false), (rows[0].GetProperty("field").GetString(), rows[0].GetProperty("from").GetBoolean(), rows[0].GetProperty("to").GetBoolean()));
        Assert.Equal(("enabled", false, true), (rows[1].GetProperty("field").GetString(), rows[1].GetProperty("from").GetBoolean(), rows[1].GetProperty("to").GetBoolean()));
        Assert.Equal(("dailyTokenCap", 200_000L, 250_000L), (rows[2].GetProperty("field").GetString(), rows[2].GetProperty("from").GetInt64(), rows[2].GetProperty("to").GetInt64()));
    }

    [Fact]
    public async Task A_changed_instruction_is_named_on_its_tenant_row_without_its_words()
    {
        var id = (await ScheduleAsync("Dev", "look")).GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await _person.PatchAsJsonAsync($"/api/teams/{_team}/triggers/{id}", new { instruction = "look harder" }, Ct)).StatusCode);

        var row = (await Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.ScheduleChanged, id, Ct))!;
        var change = JsonDocument.Parse(row.Detail!).RootElement.GetProperty("changed").EnumerateArray().Single();
        Assert.Equal("instruction", change.GetProperty("field").GetString());
        Assert.False(change.TryGetProperty("to", out _));
        Assert.DoesNotContain("look harder", row.Detail, StringComparison.Ordinal);
    }

    // ---- B: the daily cap -------------------------------------------------------------------

    [Fact]
    public async Task A_trigger_at_its_cap_skips_its_next_fire_with_the_reason_and_the_tenant_row_and_fires_again_the_next_day()
    {
        var id = (await ScheduleAsync("Dev", "look", dailyTokenCap: 1000)).GetProperty("id").GetString()!;

        // One measured run of 1,500 billable tokens: over the cap of 1,000.
        await FireAsync(Dev, Due);
        Assert.Equal(1, _agents.RunsFor(Dev));

        var spent = await TriggerAsync("Dev", id);
        Assert.Equal(1500, spent.GetProperty("spentToday").GetProperty("billableTokens").GetInt64());
        Assert.True(spent.GetProperty("capReachedToday").GetBoolean());

        // The next fire, the same day, is skipped.
        var before = await Log.HighestSeqAsync(Ct);
        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(DateTimeOffset.UtcNow.AddSeconds(602), Ct);

        var skipped = await AwaitRowAsync(
            [MessageTypes.ScheduleSkipped], m => m.Seq > before, "the skip");
        Assert.StartsWith($"{MessageTypes.ScheduleSkippedCapReason}; resumes at ",
            JsonDocument.Parse(skipped.Payload).RootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal($"schedule:{id}", skipped.Source);

        var audit = await Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.ScheduleSkipped, id, Ct);
        Assert.NotNull(audit);
        Assert.Contains(MessageTypes.ScheduleSkippedCapReason, audit.Detail, StringComparison.Ordinal);

        var row = (await Services.GetRequiredService<ITriggerStore>().FindAsync(id, Ct))!;
        Assert.Equal("capped", row.LastOutcome);
        Assert.NotNull(row.NextDueAt);
        await Task.Delay(300, Ct);
        Assert.Equal(1, _agents.RunsFor(Dev));

        // The next day (UTC: this trigger has no timezone) it fires again.
        var tomorrow = TriggerCost.StartOfDay(null, DateTimeOffset.UtcNow).AddDays(1).AddHours(1);
        await _person.PatchAsJsonAsync($"/api/teams/{_team}/triggers/{id}", new { nextDueAt = tomorrow }, Ct);

        var fired = await FireAsync(Dev, tomorrow);

        Assert.Equal(MessageTypes.Completed, fired.Type);
        Assert.Equal(2, _agents.RunsFor(Dev));
        Assert.Equal("fired", (await Services.GetRequiredService<ITriggerStore>().FindAsync(id, Ct))!.LastOutcome);
    }

    [Fact]
    public async Task Spent_today_counts_the_manager_runs_its_runs_woke_and_never_counts_an_unmeasured_run_as_zero()
    {
        // Dev hands back (1,500) and so wakes the Manager (700).
        _dev = invocation => HandBack(invocation, "found one", Measured(1000, 500));
        var id = (await ScheduleAsync("Dev", "look", dailyTokenCap: 1_000_000)).GetProperty("id").GetString()!;

        await FireAsync(Dev, Due);
        Assert.Equal(1, _agents.RunsFor(Manager));

        var spent = (await TriggerAsync("Dev", id)).GetProperty("spentToday");
        Assert.Equal(2200, spent.GetProperty("billableTokens").GetInt64());
        Assert.Equal(2, spent.GetProperty("measuredRuns").GetInt32());
        Assert.Equal(0, spent.GetProperty("unmeasuredRuns").GetInt32());

        // A run that reported no usage is unmeasured: counted as such, adding nothing.
        _dev = _ => Task.FromResult(new AgentResult(0, "nothing new"));
        await FireAgainAsync(id, Dev);

        spent = (await TriggerAsync("Dev", id)).GetProperty("spentToday");
        Assert.Equal(2200, spent.GetProperty("billableTokens").GetInt64());
        Assert.Equal(2, spent.GetProperty("measuredRuns").GetInt32());
        Assert.Equal(1, spent.GetProperty("unmeasuredRuns").GetInt32());
    }

    [Fact]
    public async Task Unmeasured_runs_never_reach_a_cap()
    {
        _dev = _ => Task.FromResult(new AgentResult(0, "nothing new"));
        var id = (await ScheduleAsync("Dev", "look", dailyTokenCap: 1)).GetProperty("id").GetString()!;

        await FireAsync(Dev, Due);
        await FireAgainAsync(id, Dev);

        var read = await TriggerAsync("Dev", id);
        Assert.False(read.GetProperty("capReachedToday").GetBoolean());
        Assert.Equal(2, read.GetProperty("spentToday").GetProperty("unmeasuredRuns").GetInt32());
        Assert.Equal(2, _agents.RunsFor(Dev));
    }

    [Fact]
    public async Task An_event_trigger_at_its_cap_skips_the_next_event_with_the_reason()
    {
        var created = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", new
        {
            name = "On echo",
            kind = "event",
            container = "Dev",
            eventType = "plugin.sample-echo.done",
            instruction = "Echo found something.",
            dailyTokenCap = 1000,
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;

        await ScheduleAsync("Echo", "first");

        // The first event fires Dev (1,500, over the cap).
        await FireAsync(Echo, Due);
        await AwaitRowAsync([MessageTypes.Completed], m => m.Source == Dev.ToString(), "Dev's run");
        Assert.Equal(1, _agents.RunsFor(Dev));

        // The second is skipped.
        var before = await Log.HighestSeqAsync(Ct);
        await FireAsync(Echo, DateTimeOffset.UtcNow.AddSeconds(602));

        var skipped = await AwaitRowAsync(
            [MessageTypes.ScheduleSkipped], m => m.Seq > before, "the skip");
        Assert.Equal(MessageTypes.ScheduleSkippedCapReason,
            JsonDocument.Parse(skipped.Payload).RootElement.GetProperty("reason").GetString());
        Assert.Equal($"trigger:{id}", skipped.Source);
        Assert.NotNull(await Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.ScheduleSkipped, id, Ct));
        Assert.Equal("capped", (await Services.GetRequiredService<ITriggerStore>().FindAsync(id, Ct))!.LastOutcome);
        await Task.Delay(300, Ct);
        Assert.Equal(1, _agents.RunsFor(Dev));
    }

    [Fact]
    public void The_day_starts_at_midnight_in_the_triggers_timezone()
    {
        var now = new DateTimeOffset(2026, 9, 28, 2, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero), TriggerCost.StartOfDay(null, now));

        // 22:30 the evening before in New York (EDT, UTC-4): its day began at 04:00 UTC on the 27th.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 27, 4, 0, 0, TimeSpan.Zero),
            TriggerCost.StartOfDay("America/New_York", now).ToUniversalTime());
    }

    // ---- the measured recent cost of a member -----------------------------------------------

    [Fact]
    public async Task A_members_recent_cost_is_the_median_of_its_measured_runs_and_counts_the_unmeasured()
    {
        var usages = new Queue<InvocationUsage?>([Measured(100, 0), Measured(300, 0), null, Measured(200, 0)]);
        _dev = _ => Task.FromResult(new AgentResult(0, "done", Usage: usages.Dequeue()));

        var id = (await ScheduleAsync("Dev", "look")).GetProperty("id").GetString()!;
        for (var i = 0; i < 4; i++) await FireAgainAsync(id, Dev);

        var cost = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/containers/Dev/cost", Ct);
        Assert.Equal(4, cost.GetProperty("lastRuns").GetInt32());
        Assert.Equal(3, cost.GetProperty("measuredRuns").GetInt32());
        Assert.Equal(1, cost.GetProperty("unmeasuredRuns").GetInt32());
        Assert.Equal(200, cost.GetProperty("medianBillableTokens").GetInt64());
        Assert.Equal("agent", cost.GetProperty("kind").GetString());

        var echo = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/containers/Echo/cost", Ct);
        Assert.Equal(0, echo.GetProperty("lastRuns").GetInt32());
        Assert.Equal(JsonValueKind.Null, echo.GetProperty("medianBillableTokens").ValueKind);
        Assert.Equal("plugin", echo.GetProperty("kind").GetString());

        var missing = await _person.GetAsync($"/api/teams/{_team}/containers/Nobody/cost", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
