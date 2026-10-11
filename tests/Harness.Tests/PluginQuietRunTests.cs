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
/// A PLUGIN RUN CAN FINISH QUIETLY. A plugin polling on a schedule would otherwise wake its Manager
/// (a paid model run) every few minutes to learn that nothing happened. A result record carrying
/// <c>quiet:true</c> still writes its `completed` row, marked quiet, and the pump wakes nobody on
/// that row. What a quiet run does NOT suppress: a failure, an event it published, a hand-back.
///
/// On the real Host, with the real sample-echo plugin (its `quiet:` prefix) woken by a real
/// schedule trigger fired through <see cref="TriggerSweep"/>.
/// </summary>
public sealed class PluginQuietRunTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-quiet-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private ContainerId Echo => new(_team, "Echo");

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Manager => new(_team, TeamRegistry.DefaultManagerName);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);

        _agents.Behaviour = invocation => Task.FromResult(new AgentResult(0, $"noted by {invocation.Container.Name}"));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Quiet", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo",
            agent = "plugin:sample-echo",
            config = new { mode = "upper" },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);

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

    /// <summary>
    /// A schedule trigger on Echo every five minutes, fired once by the sweep as if five minutes
    /// had passed - the background sweep never reaches it during a test. Returns Echo's terminal
    /// row for that run.
    /// </summary>
    private async Task<Message> ScheduleAndFireAsync(string instruction)
    {
        // `always`: what `quiet` means is pinned under the wake choice that behaves as before it
        // existed. Under the default a finished run wakes nobody anyway (TriggerCostControlTests).
        var created = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", new
        {
            name = "Poll",
            kind = "every",
            container = "Echo",
            intervalSeconds = 300,
            instruction,
            wakeManager = WakeManagerPolicy.Always,
        }, Ct);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));

        var after = await Services.GetRequiredService<IMessageLog>().HighestSeqAsync(Ct);
        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(DateTimeOffset.UtcNow.AddSeconds(301), Ct);

        var row = await AwaitRowAsync(
            [MessageTypes.Completed, MessageTypes.Failed],
            m => m.Seq > after && m.Source == Echo.ToString(),
            "Echo's terminal row");

        // The run the schedule fired, not one a person told.
        var instructionRow = (await Services.GetRequiredService<IMessageLog>().ReadCorrelationAsync(row.CorrelationId, Ct))
            .First(m => m.Type == MessageTypes.InstructionFor(Echo));
        Assert.StartsWith("schedule:", instructionRow.Source, StringComparison.Ordinal);

        return row;
    }

    private async Task<Message> AwaitRowAsync(IReadOnlyCollection<string> types, Func<Message, bool> match, string what)
    {
        var log = Services.GetRequiredService<IMessageLog>();
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if ((await log.ReadAfterAsync(0, types, int.MaxValue, Ct)).FirstOrDefault(match) is { } row) return row;
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"No row: {what}.");
    }

    /// <summary>
    /// Waits until the Manager's pump has read past <paramref name="row"/>, and anything it was
    /// handed has run: after this, a wake that row was going to cause has happened.
    /// </summary>
    private async Task SettleManagerAsync(Message row)
    {
        var cursors = Services.GetRequiredService<ICursors>();
        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        var deadline = DateTime.UtcNow.AddSeconds(15);

        for (var quietPasses = 0; quietPasses < 5 && DateTime.UtcNow < deadline;)
        {
            var read = await cursors.PositionAsync(Manager, Ct) >= row.Seq;
            var idle = host.Find(Manager)!.State == ContainerState.Idle && host.Find(Echo)!.State == ContainerState.Idle;
            quietPasses = read && idle ? quietPasses + 1 : 0;
            await Task.Delay(100, Ct);
        }
    }

    private static bool Quiet(Message row) =>
        JsonDocument.Parse(row.Payload).RootElement.TryGetProperty(PayloadFields.Quiet, out var value)
        && value.ValueKind == JsonValueKind.True;

    [Fact]
    public async Task A_scheduled_plugin_run_that_finishes_quiet_wakes_nobody()
    {
        var row = await ScheduleAndFireAsync("quiet:nothing new");

        // Still a completed row the card, the run history and the feed show - marked quiet.
        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.True(Quiet(row));

        // The workflow the schedule started is Echo's, and ends exactly as a non-quiet one does:
        // declared by the platform on Echo's behalf.
        var declared = await AwaitRowAsync(
            [MessageTypes.WorkflowCompleted], m => m.CorrelationId == row.CorrelationId, "the workflow's declaration");
        Assert.Equal(Echo.ToString(), declared.Source);
        Assert.True(JsonDocument.Parse(declared.Payload).RootElement
            .GetProperty(UndeclarableWorkflows.DeclaredByPlatformField).GetBoolean());
        Assert.Empty(await Services.GetRequiredService<IMessageLog>().OpenWorkflowsAmongAsync([row.CorrelationId], Ct));

        await SettleManagerAsync(row);

        Assert.Empty(_agents.Invocations);
    }

    [Fact]
    public async Task The_runs_route_marks_a_quiet_run_quiet_and_a_loud_one_not()
    {
        var quiet = await ScheduleAndFireAsync("quiet:nothing new");
        await SettleManagerAsync(quiet);
        var loud = await ScheduleAndFireAsync("something new");
        await SettleManagerAsync(loud);

        var runs = (await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/members/Echo/runs", Ct))
            .GetProperty("runs").EnumerateArray()
            .ToDictionary(run => run.GetProperty("seq").GetInt64());

        Assert.True(runs[quiet.Seq].GetProperty("quiet").GetBoolean());
        Assert.False(runs[loud.Seq].GetProperty("quiet").GetBoolean());
    }

    [Fact]
    public async Task The_same_scheduled_run_not_quiet_wakes_the_manager_exactly_once()
    {
        var row = await ScheduleAndFireAsync("something new");

        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.False(Quiet(row));

        await SettleManagerAsync(row);

        Assert.Equal(1, _agents.RunsFor(Manager));
        Assert.Equal(0, _agents.RunsFor(Dev));
    }

    [Fact]
    public async Task A_quiet_run_that_publishes_an_event_still_wakes_the_events_trigger()
    {
        var trigger = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", new
        {
            name = "On echo",
            kind = "event",
            container = "Dev",
            eventType = "plugin.sample-echo.done",
            filter = "source eq " + Echo,
            instruction = "Echo found {event.length} characters.",
        }, Ct);
        Assert.True(trigger.IsSuccessStatusCode, await trigger.Content.ReadAsStringAsync(Ct));

        var row = await ScheduleAndFireAsync("quiet:mail");
        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.True(Quiet(row));

        var published = await AwaitRowAsync(
            ["plugin.sample-echo.done"], m => m.CorrelationId == row.CorrelationId, "the plugin's event");
        await AwaitRowAsync(
            [MessageTypes.InstructionFor(Dev)], m => m.CausationSeq == published.Seq, "the trigger's instruction");
        await AwaitRowAsync(
            [MessageTypes.Completed], m => m.Source == Dev.ToString(), "Dev's run");

        Assert.Equal(1, _agents.RunsFor(Dev));
    }

    [Fact]
    public async Task A_quiet_run_that_fails_still_wakes_the_manager()
    {
        var row = await ScheduleAndFireAsync("quiet:fail:the mailbox is gone");

        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.False(Quiet(row));

        await SettleManagerAsync(row);

        Assert.Equal(1, _agents.RunsFor(Manager));
    }
}
