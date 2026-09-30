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
/// A WORKFLOW THE PLATFORM ALREADY DECLARED DOES NOT WAKE THE MANAGER. A person (or the Concierge)
/// tells a plugin member directly; the plugin cannot declare, so the platform declares the workflow
/// as the run ends (<see cref="UndeclarableWorkflows"/>). The run's `completed` row says so with
/// <see cref="PayloadFields.WorkflowDeclared"/>, and the pump passes over the Manager on it - a paid
/// model run into a closed workflow, to learn nothing needs doing, is what this removes.
///
/// What it does NOT suppress: an event trigger naming the type, a failure, a run answering a
/// member's own `tell`, and every agent member's run.
///
/// On the real Host, with the real sample-echo plugin and a fake agent for the Manager and Dev.
/// </summary>
public sealed class WorkflowDeclaredWakeTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-declared-wake-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

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
            .CreateAsync("Declared", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

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

    /// <summary>A person tells <paramref name="who"/> through the `tell` route; returns the
    /// terminal row of the run that answers it.</summary>
    private async Task<Message> PersonTellsAsync(ContainerId who, string instruction)
    {
        var after = await Log.HighestSeqAsync(Ct);

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{who.Name}/tell", new { instruction }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var row = await AwaitRowAsync(
            [MessageTypes.Completed, MessageTypes.Failed],
            m => m.Seq > after && m.Source == who.ToString(),
            $"{who.Name}'s terminal row");
        await SettleAsync(row, who);
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
            var idle = host.Find(Manager)!.State == ContainerState.Idle
                && host.Find(member)!.State == ContainerState.Idle
                && host.Find(Dev)!.State == ContainerState.Idle;
            quietPasses = read && idle ? quietPasses + 1 : 0;
            await Task.Delay(100, Ct);
        }
    }

    /// <summary>How many Manager runs <paramref name="row"/> started: its `started` rows name the
    /// row that woke it.</summary>
    private async Task<int> ManagerRunsOnAsync(Message row) =>
        (await Log.ReadAfterAsync(row.Seq, [MessageTypes.Started], int.MaxValue, Ct))
            .Count(m => m.Source == Manager.ToString() && m.CausationSeq == row.Seq);

    private static bool Declared(Message row) =>
        JsonDocument.Parse(row.Payload).RootElement.TryGetProperty(PayloadFields.WorkflowDeclared, out var value)
        && value.ValueKind == JsonValueKind.True;

    private async Task<Message?> DeclarationOfAsync(long correlation) =>
        (await Log.ReadCorrelationAsync(correlation, Ct)).FirstOrDefault(m => m.Type == MessageTypes.WorkflowCompleted);

    [Fact]
    public async Task A_person_telling_a_plugin_directly_closes_the_workflow_without_waking_the_manager()
    {
        var row = await PersonTellsAsync(Echo, "go");

        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.True(Declared(row));

        // Declared by the platform on Echo's behalf, BEFORE the row that says so.
        var declared = await DeclarationOfAsync(row.CorrelationId);
        Assert.NotNull(declared);
        Assert.True(declared.Seq < row.Seq);
        Assert.Equal(Echo.ToString(), declared.Source);
        Assert.True(JsonDocument.Parse(declared.Payload).RootElement
            .GetProperty(UndeclarableWorkflows.DeclaredByPlatformField).GetBoolean());

        Assert.Equal(0, await ManagerRunsOnAsync(row));
        Assert.Equal(0, _agents.RunsFor(Manager));

        // The runs route shows why, beside how it ended.
        var run = (await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/members/Echo/runs", Ct))
            .GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("seq").GetInt64() == row.Seq);
        Assert.True(run.GetProperty("workflowDeclared").GetBoolean());
        Assert.False(run.GetProperty("quiet").GetBoolean());
    }

    [Fact]
    public async Task An_event_trigger_naming_completed_still_fires_on_that_row()
    {
        var trigger = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", new
        {
            name = "On echo done",
            kind = "event",
            container = "Dev",
            eventType = MessageTypes.Completed,
            filter = "source eq " + Echo,
            instruction = "Echo finished.",
        }, Ct);
        Assert.True(trigger.IsSuccessStatusCode, await trigger.Content.ReadAsStringAsync(Ct));

        var row = await PersonTellsAsync(Echo, "go");
        Assert.True(Declared(row));

        await AwaitRowAsync([MessageTypes.InstructionFor(Dev)], m => m.CausationSeq == row.Seq, "the trigger's instruction");
        await AwaitRowAsync([MessageTypes.Completed], m => m.Source == Dev.ToString(), "Dev's run");

        Assert.Equal(1, _agents.RunsFor(Dev));
        Assert.Equal(0, await ManagerRunsOnAsync(row));
    }

    [Fact]
    public async Task The_same_run_failing_still_wakes_the_manager()
    {
        var row = await PersonTellsAsync(Echo, "fail:the mailbox is gone");

        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.False(Declared(row));
        Assert.Equal(1, await ManagerRunsOnAsync(row));

        // The failed run declared nothing: a declaration, if any, came from the Manager's own run
        // ending successfully afterwards.
        Assert.False(await DeclarationOfAsync(row.CorrelationId) is { } declared && declared.Seq < row.Seq);
    }

    /// <summary>
    /// A Manager's `tell` roots a workflow addressed to the plugin, which the platform DOES declare -
    /// but the Manager is waiting for the answer, so the row closing its `tell` never carries the
    /// key and wakes it once, as it always did.
    /// </summary>
    [Fact]
    public async Task A_plugin_run_answering_a_managers_tell_still_wakes_it_once()
    {
        var told = await Log.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(Echo), """{"instruction":"go"}""", Manager.ToString()), Ct);

        var row = await AwaitRowAsync(
            [MessageTypes.Completed], m => m.CausationSeq == told.Seq, "Echo's answer to the Manager");
        await SettleAsync(row, Echo);

        Assert.False(Declared(row));
        Assert.Equal(1, await ManagerRunsOnAsync(row));
    }

    [Fact]
    public async Task An_agent_members_run_is_unchanged()
    {
        var row = await PersonTellsAsync(Dev, "look");

        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.False(Declared(row));
        Assert.Equal(1, await ManagerRunsOnAsync(row));
    }
}
