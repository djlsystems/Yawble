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
/// A CAPPED TRIGGER SAYS SO ONCE AND SLEEPS. A scheduled trigger whose daily token cap is reached
/// writes one `schedule.skipped` row and one tenant row, naming when it resumes, and its stored
/// `next_due_at` moves to its first occurrence of the next day in its timezone; nothing wakes it
/// before then but a person's change. An event trigger logs its first capped skip of the day and
/// counts the rest.
///
/// On the real Host, with a fake agent member (Dev, 1,500 billable tokens a run) and the real
/// sample-echo plugin (Echo). Fires go through <see cref="TriggerSweep"/> at chosen instants, with the
/// background runner off, so only the instants a test names are ever swept.
/// </summary>
public sealed class CappedTriggerSleepTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-capped-sleep-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private ITriggerStore Store => Services.GetRequiredService<ITriggerStore>();

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Echo => new(_team, "Echo");

    private ContainerId Manager => new(_team, TeamRegistry.DefaultManagerName);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);

        _agents.Behaviour = invocation => Task.FromResult(invocation.Container.Name == "Dev"
            ? new AgentResult(0, "nothing new", Usage: new InvocationUsage(1000, 500, "test"))
            : new AgentResult(0, "noted", Usage: new InvocationUsage(600, 100, "test")));

        await StartAsync();

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Capped", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        await SignInAsync();

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

    private Task StartAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting("ScheduleRunnerEnabled", "false")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _ = _factory.Services;
        return Task.CompletedTask;
    }

    private async Task SignInAsync()
    {
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    /// <summary>Stops this Host and starts another on the same data root: nothing survives but
    /// what was stored.</summary>
    private async Task RestartAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        await StartAsync();
        await SignInAsync();
    }

    // ---- helpers ----------------------------------------------------------------------------

    private async Task<string> CreateAsync(object body)
    {
        var created = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", body, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    /// <summary>Every minute on Dev, capped at <paramref name="cap"/>.</summary>
    private Task<string> EveryMinuteAsync(long cap) => CreateAsync(new
    {
        name = "Every minute",
        kind = "every",
        container = "Dev",
        intervalSeconds = 60,
        idleOnly = false,
        instruction = "look",
        dailyTokenCap = cap,
    });

    private async Task<TriggerRow> RowAsync(string id) => (await Store.FindAsync(id, Ct))!;

    private async Task<DateTimeOffset> DueAsync(string id) => (await RowAsync(id)).NextDueAt!.Value;

    /// <summary>Sweeps at <paramref name="at"/> and waits for <paramref name="member"/>'s run that
    /// sweep started to end and settle.</summary>
    private async Task<Message> FireAsync(ContainerId member, DateTimeOffset at)
    {
        var after = await Log.HighestSeqAsync(Ct);
        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(at, Ct);

        var row = await AwaitRowAsync(
            [MessageTypes.Completed, MessageTypes.Failed],
            m => m.Seq > after && m.Source == member.ToString(),
            $"{member.Name}'s terminal row");

        await SettleAsync(row, member);
        return row;
    }

    /// <summary>Fires the trigger at its stored due time, so nothing counts as missed.</summary>
    private async Task<Message> FireAtDueAsync(string id) => await FireAsync(Dev, await DueAsync(id));

    private async Task SweepAsync(DateTimeOffset at)
    {
        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(at, Ct);
        await Task.Delay(200, Ct);
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

    private async Task<IReadOnlyList<Message>> SkipRowsAsync(string source) =>
        [.. (await Log.ReadAfterAsync(0, [MessageTypes.ScheduleSkipped], int.MaxValue, Ct)).Where(m => m.Source == source)];

    private async Task<IReadOnlyList<TenantEvent>> TenantRowsAsync(string action, string id) =>
        [.. (await Services.GetRequiredService<ITenantLog>().ReadAsync(take: 10_000, ct: Ct)).Events
            .Where(e => e.Action == action && e.Subject == id)];

    private async Task<JsonElement> ViewAsync(string member, string id)
    {
        var rows = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/containers/{member}/triggers", Ct);
        return rows.EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
    }

    private async Task<HttpResponseMessage> PatchAsync(string id, object body) =>
        await _person.PatchAsync($"/api/teams/{_team}/triggers/{id}", JsonContent.Create(body), Ct);

    /// <summary>
    /// The first minute of the every-minute chain through <paramref name="due"/> that falls on or
    /// after the start of the day after <paramref name="now"/>, in UTC. Worked out here, not by the
    /// code under test.
    /// </summary>
    private static DateTimeOffset FirstOfNextUtcDay(DateTimeOffset due, DateTimeOffset now)
    {
        var boundary = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        var steps = (long)Math.Ceiling((boundary - due).TotalSeconds / 60);
        return due.AddSeconds(steps * 60);
    }

    /// <summary>Caps an every-minute trigger on Dev at 4,000: three runs of 1,500 reach it, and the
    /// fourth fire is the first skipped. Returns the trigger and that fourth due time.</summary>
    private async Task<(string Id, DateTimeOffset Skipped)> CapOnTheThirdRunAsync()
    {
        var id = await EveryMinuteAsync(4000);

        for (var run = 1; run <= 3; run++) await FireAtDueAsync(id);
        Assert.Equal(3, _agents.RunsFor(Dev));

        var fourth = await DueAsync(id);
        await SweepAsync(fourth);
        return (id, fourth);
    }

    // ---- a schedule sleeps until the next day ------------------------------------------------

    [Fact]
    public async Task A_schedule_capped_on_its_third_run_logs_one_skip_and_sleeps_until_the_next_days_first_occurrence()
    {
        var (id, fourth) = await CapOnTheThirdRunAsync();
        var resumesAt = FirstOfNextUtcDay(fourth, fourth);

        // One skip row and one tenant row, naming when it resumes.
        var skip = Assert.Single(await SkipRowsAsync($"schedule:{id}"));
        Assert.Equal(
            $"{MessageTypes.ScheduleSkippedCapReason}; resumes at {resumesAt:yyyy-MM-dd'T'HH:mm:sszzz}",
            JsonDocument.Parse(skip.Payload).RootElement.GetProperty("reason").GetString());
        var tenant = Assert.Single(await TenantRowsAsync(TenantActions.ScheduleSkipped, id));
        Assert.Contains("resumes at", tenant.Detail, StringComparison.Ordinal);

        // Stored, capped, asleep until then.
        var row = await RowAsync(id);
        Assert.Equal("capped", row.LastOutcome);
        Assert.Equal(resumesAt, row.NextDueAt);

        // No fire and no skip before the next day's first occurrence, however often it is swept.
        foreach (var at in new[] { fourth.AddMinutes(1), fourth.AddMinutes(30), fourth.AddHours(3), resumesAt.AddSeconds(-1) })
        {
            await SweepAsync(at);
        }

        Assert.Single(await SkipRowsAsync($"schedule:{id}"));
        Assert.Single(await TenantRowsAsync(TenantActions.ScheduleSkipped, id));
        Assert.Equal(3, _agents.RunsFor(Dev));

        // The dialog reads it as capped until then.
        var view = await ViewAsync("Dev", id);
        Assert.True(view.GetProperty("capReachedToday").GetBoolean());
        Assert.Equal(resumesAt, view.GetProperty("cappedUntil").GetDateTimeOffset());

        // At that time it fires: a new day counts from zero.
        var fired = await FireAsync(Dev, resumesAt);
        Assert.Equal(MessageTypes.Completed, fired.Type);
        Assert.Equal(4, _agents.RunsFor(Dev));
        Assert.Equal("fired", (await RowAsync(id)).LastOutcome);
    }

    [Fact]
    public async Task A_host_restarted_while_the_trigger_sleeps_waits_for_the_stored_time_and_fires_at_it()
    {
        var (id, fourth) = await CapOnTheThirdRunAsync();
        var resumesAt = (await RowAsync(id)).NextDueAt!.Value;
        Assert.Equal(FirstOfNextUtcDay(fourth, fourth), resumesAt);

        await RestartAsync();

        Assert.Equal(resumesAt, (await RowAsync(id)).NextDueAt);

        await SweepAsync(fourth.AddMinutes(1));
        await SweepAsync(resumesAt.AddSeconds(-1));
        Assert.Single(await SkipRowsAsync($"schedule:{id}"));
        Assert.Equal(3, _agents.RunsFor(Dev));

        var fired = await FireAsync(Dev, resumesAt);
        Assert.Equal(MessageTypes.Completed, fired.Type);
        Assert.Equal(4, _agents.RunsFor(Dev));
    }

    [Fact]
    public async Task A_host_down_across_the_resume_time_records_it_missed_once_and_fires_at_the_next_occurrence()
    {
        var (id, _) = await CapOnTheThirdRunAsync();
        var resumesAt = (await RowAsync(id)).NextDueAt!.Value;

        await RestartAsync();

        // Back two and a half minutes after it should have resumed: the resume and the two minutes
        // after it were missed, recorded once, and it is armed for the next future minute.
        await SweepAsync(resumesAt.AddSeconds(150));

        var missed = Assert.Single(await TenantRowsAsync(TenantActions.ScheduleMissed, id));
        Assert.Contains("\"missed\":2", missed.Detail, StringComparison.Ordinal);
        var row = await RowAsync(id);
        Assert.Equal("missed", row.LastOutcome);
        Assert.Equal(resumesAt.AddMinutes(3), row.NextDueAt);
        Assert.Equal(3, _agents.RunsFor(Dev));

        var fired = await FireAsync(Dev, resumesAt.AddMinutes(3));
        Assert.Equal(MessageTypes.Completed, fired.Type);
        Assert.Equal(4, _agents.RunsFor(Dev));
        Assert.Single(await TenantRowsAsync(TenantActions.ScheduleMissed, id));
    }

    // ---- a person's change wakes it; lowering the cap does not ------------------------------

    [Theory]
    [InlineData("raise")]
    [InlineData("clear")]
    public async Task Raising_the_cap_above_todays_spend_or_clearing_it_makes_it_fire_at_its_next_occurrence_from_now(string change)
    {
        var (id, _) = await CapOnTheThirdRunAsync();
        var changes = (await TenantRowsAsync(TenantActions.ScheduleChanged, id)).Count;

        var before = DateTimeOffset.UtcNow;
        var patched = change == "raise"
            ? await PatchAsync(id, new { dailyTokenCap = 100_000 })
            : await PatchAsync(id, new { dailyTokenCap = (long?)null });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        // Its next occurrence from now: a minute from the change, not tomorrow.
        var due = await DueAsync(id);
        Assert.InRange(due, before.AddSeconds(59), DateTimeOffset.UtcNow.AddSeconds(61));
        Assert.Equal(changes + 1, (await TenantRowsAsync(TenantActions.ScheduleChanged, id)).Count);
        Assert.Equal(JsonValueKind.Null, (await ViewAsync("Dev", id)).GetProperty("cappedUntil").ValueKind);

        var fired = await FireAsync(Dev, due);
        Assert.Equal(MessageTypes.Completed, fired.Type);
        Assert.Equal(4, _agents.RunsFor(Dev));
    }

    [Fact]
    public async Task Editing_the_schedule_while_it_sleeps_rearms_it_from_now()
    {
        var (id, _) = await CapOnTheThirdRunAsync();

        var before = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(id, new { intervalSeconds = 120 })).StatusCode);

        var due = await DueAsync(id);
        Assert.InRange(due, before.AddSeconds(119), DateTimeOffset.UtcNow.AddSeconds(121));

        // Raised in the same breath, it runs then.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(id, new { dailyTokenCap = 100_000 })).StatusCode);
        var fired = await FireAsync(Dev, await DueAsync(id));
        Assert.Equal(MessageTypes.Completed, fired.Type);
        Assert.Equal(4, _agents.RunsFor(Dev));
    }

    [Fact]
    public async Task An_edited_schedule_still_over_its_cap_is_skipped_again_without_a_second_row()
    {
        var (id, fourth) = await CapOnTheThirdRunAsync();
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(id, new { intervalSeconds = 120 })).StatusCode);

        var due = await DueAsync(id);
        await SweepAsync(due);

        // Counted, not logged, and asleep again until the next day.
        Assert.Single(await SkipRowsAsync($"schedule:{id}"));
        Assert.Single(await TenantRowsAsync(TenantActions.ScheduleSkipped, id));
        Assert.Equal(3, _agents.RunsFor(Dev));
        var row = await RowAsync(id);
        Assert.Equal("capped", row.LastOutcome);
        Assert.True(row.NextDueAt >= new DateTimeOffset(fourth.UtcDateTime.Date.AddDays(1), TimeSpan.Zero));
    }

    [Theory]
    [InlineData(3000L)]
    [InlineData(4200L)]
    public async Task Lowering_the_cap_or_raising_it_still_under_todays_spend_leaves_it_asleep(long cap)
    {
        var (id, _) = await CapOnTheThirdRunAsync();
        var asleepUntil = await DueAsync(id);
        var changes = (await TenantRowsAsync(TenantActions.ScheduleChanged, id)).Count;

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(id, new { dailyTokenCap = cap })).StatusCode);

        Assert.Equal(asleepUntil, await DueAsync(id));
        Assert.Equal(changes + 1, (await TenantRowsAsync(TenantActions.ScheduleChanged, id)).Count);
        Assert.Equal(asleepUntil, (await ViewAsync("Dev", id)).GetProperty("cappedUntil").GetDateTimeOffset());
    }

    // ---- an event trigger logs its first capped skip of the day and counts the rest ----------

    [Fact]
    public async Task An_event_trigger_at_its_cap_logs_its_first_skip_of_the_day_and_counts_the_rest()
    {
        var id = await CreateAsync(new
        {
            name = "On echo",
            kind = "event",
            container = "Dev",
            eventType = "plugin.sample-echo.done",
            instruction = "Echo found something.",
            dailyTokenCap = 1000,
        });
        var echo = await CreateAsync(new
        {
            name = "Poll Echo", kind = "every", container = "Echo", intervalSeconds = 300, idleOnly = false,
            instruction = "first",
        });

        // The first event fires Dev: 1,500, over the cap.
        await FireAsync(Echo, await DueAsync(echo));
        await AwaitRowAsync([MessageTypes.Completed], m => m.Source == Dev.ToString(), "Dev's run");
        Assert.Equal(1, _agents.RunsFor(Dev));
        Assert.Equal(0, (await ViewAsync("Dev", id)).GetProperty("skippedToday").GetInt32());

        // Three more events: three skips, one row of each kind.
        for (var i = 0; i < 3; i++)
        {
            await FireAsync(Echo, await DueAsync(echo));
            await Task.Delay(300, Ct);
        }

        var skip = Assert.Single(await SkipRowsAsync($"trigger:{id}"));
        Assert.Equal(MessageTypes.ScheduleSkippedCapReason,
            JsonDocument.Parse(skip.Payload).RootElement.GetProperty("reason").GetString());
        Assert.Single(await TenantRowsAsync(TenantActions.ScheduleSkipped, id));
        Assert.Equal(1, _agents.RunsFor(Dev));

        var view = await ViewAsync("Dev", id);
        Assert.Equal(3, view.GetProperty("skippedToday").GetInt32());
        Assert.Equal("capped", view.GetProperty("lastOutcome").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("cappedUntil").ValueKind);
    }

    // ---- the day boundary is the trigger's timezone's -----------------------------------------

    [Fact]
    public void The_resume_time_is_the_first_occurrence_after_midnight_in_the_triggers_timezone()
    {
        // 22:30 on the 27th in New York (EDT, UTC-4) is 02:30 UTC on the 28th: in UTC the day has
        // already turned, in New York it turns at 04:00 UTC.
        var now = new DateTimeOffset(2026, 9, 28, 2, 30, 0, TimeSpan.Zero);
        var quarterHourly = Row("cron", expression: "0 */15 * * * *", timezone: "America/New_York");

        Assert.Equal(
            new DateTimeOffset(2026, 9, 28, 4, 0, 0, TimeSpan.Zero),
            TriggerCost.ResumeAt(quarterHourly, now, next: now.AddMinutes(15)));

        // The same instant in UTC resumes at the next UTC midnight, a day later.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
            TriggerCost.ResumeAt(quarterHourly with { Timezone = "UTC" }, now, next: now.AddMinutes(15)));

        // A daily 09:00 in Tokyo, capped at 10:00 local: the next day's first occurrence is 09:00.
        var tokyoMorning = Row("cron", expression: "0 0 9 * * *", timezone: "Asia/Tokyo");
        var tenInTokyo = new DateTimeOffset(2026, 9, 28, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
            TriggerCost.ResumeAt(tokyoMorning, tenInTokyo, next: new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero)));

        // An anchorless every-N keeps its own phase: the first of ITS times on or after midnight.
        var everySeven = Row("every", intervalSeconds: 420);
        var next = new DateTimeOffset(2026, 9, 28, 23, 58, 0, TimeSpan.Zero);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 29, 0, 5, 0, TimeSpan.Zero),
            TriggerCost.ResumeAt(everySeven, new DateTimeOffset(2026, 9, 28, 23, 51, 0, TimeSpan.Zero), next));

        // A one-off has no next occurrence to sleep until.
        Assert.Null(TriggerCost.ResumeAt(Row("once", fireAt: now.AddHours(-1)), now, next: null));
    }

    [Fact]
    public async Task A_schedule_in_a_non_utc_zone_sleeps_until_midnight_there()
    {
        // Every minute by cron in Tokyo (UTC+9, no daylight saving): its day ends at 15:00 UTC.
        var id = await CreateAsync(new
        {
            name = "Tokyo minute", kind = "cron", container = "Dev", expression = "0 * * * * *",
            timezone = "Asia/Tokyo", idleOnly = false, instruction = "look", dailyTokenCap = 1000,
        });

        await FireAtDueAsync(id);
        var second = await DueAsync(id);
        await SweepAsync(second);

        var tokyo = TimeSpan.FromHours(9);
        var localNow = second.ToOffset(tokyo);
        var midnight = new DateTimeOffset(localNow.Date.AddDays(1), tokyo);

        var row = await RowAsync(id);
        Assert.Equal("capped", row.LastOutcome);
        Assert.Equal(midnight, row.NextDueAt);
        Assert.Equal(TimeSpan.FromHours(15), row.NextDueAt!.Value.UtcDateTime.TimeOfDay);

        var skip = Assert.Single(await SkipRowsAsync($"schedule:{id}"));
        Assert.EndsWith(
            $"resumes at {midnight:yyyy-MM-dd}T00:00:00+09:00",
            JsonDocument.Parse(skip.Payload).RootElement.GetProperty("reason").GetString(),
            StringComparison.Ordinal);

        // Asleep until then, and fired at it.
        await SweepAsync(midnight.AddSeconds(-1));
        Assert.Equal(1, _agents.RunsFor(Dev));
        await FireAsync(Dev, midnight);
        Assert.Equal(2, _agents.RunsFor(Dev));
    }

    private static TriggerRow Row(
        string kind,
        string? expression = null,
        string? timezone = null,
        int? intervalSeconds = null,
        DateTimeOffset? fireAt = null) =>
        new(
            "t", "Team", "Dev", "Row", "look", kind, expression, timezone, intervalSeconds, fireAt,
            IdleOnly: false, Enabled: true, NextDueAt: null, LastFiredAt: null, LastOutcome: null,
            LastSeq: null, MissedCount: 0, CreatedAt: DateTimeOffset.UnixEpoch, CreatedBy: "test",
            DailyTokenCap: 1000);
}
