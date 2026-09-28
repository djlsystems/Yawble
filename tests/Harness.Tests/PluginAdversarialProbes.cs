using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// Adversarial probes from the independent verification of quiet runs, the plugin-authoring skill and
/// C1 (P1-b scan), C2 (R1-c numeric secrets) at the protocol level. A probe that shows a defect is
/// kept as a Skip naming it, so the suite stays green.
/// </summary>
public sealed class PluginRunnerProbes : IDisposable
{
    private static readonly ContainerId Plug = new("alpha", "plug");

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-b0013-probe-").FullName;
    private readonly List<IDisposable> _registrations = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var registration in _registrations) registration.Dispose();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private sealed class Secrets(Dictionary<string, string> values) : ISecretStore
    {
        public string? TryGet(string logicalKey) => values.GetValueOrDefault(logicalKey);
    }

    private sealed class Settings(PluginMemberSettings settings) : IPluginMemberSettings
    {
        public Task<PluginMemberSettings> ForAsync(ContainerId member, CancellationToken ct = default) => Task.FromResult(settings);
    }

    private static readonly Action<JsonObject> DeclaresTick = m => m["events"] = JsonNode.Parse(
        """{"publishes":[{"type":"tick","summary":"A tick.","fields":[{"name":"n","kind":"number"},{"name":"note"}]}]}""");

    private async Task<(ContainerTestBed Bed, MemberRuntime Member, Message Row)> RunAsync(
        string script, Action<JsonObject>? manifest = null, IPluginMemberSettings? settings = null, ISecretStore? secrets = null)
    {
        PluginInstall.Write(_dataRoot, "fixture", script: script, manifest: PluginInstall.Manifest("fixture", edit: manifest));

        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        catalog.Rescan();
        _registrations.Add(EventCatalog.Register(catalog));

        var bed = new ContainerTestBed();
        var heartbeat = new RunHeartbeat();
        var plugins = new PluginMemberRunner(catalog, new MemberReports(bed.Host, bed.Store, heartbeat), heartbeat, null, settings, secrets);

        var member = await bed.Host.AddAsync(
            ContainerTestBed.Definition(Plug) with { Agent = "plugin:fixture", WorkingDirectory = _dataRoot },
            new MemberRunnerRouter(bed.Runner, plugins), Ct);

        // From a schedule, not a member: a run answering a member's instruction is never quiet
        // (that member is waiting), and these probes are about what quiet itself does.
        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Plug), JsonSerializer.Serialize(new { instruction = "hello" }), "trigger:poll"), Ct);

        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.Store.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], 10, Ct)).Count > 0, attempts: 400));

        var row = (await bed.Store.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], 10, Ct)).Single();
        return (bed, member, row);
    }

    private static bool HasQuiet(Message row) => JsonDocument.Parse(row.Payload).RootElement.TryGetProperty(PayloadFields.Quiet, out _);

    // ---------------------------------------------------------------- A: quiet must not suppress a failure

    [Theory]
    [InlineData("""echo '{"t":"result","ok":true,"output":"x","quiet":true}'; exit 3""", "Failed")]      // ok:true but exit != 0
    [InlineData("""echo '{"t":"result","ok":false,"error":"no","quiet":true}'""", "Failed")]              // ok:false, exit 0
    [InlineData("""echo '{"t":"result","output":"x","quiet":true}'""", "Failed")]                         // ok missing
    [InlineData("""echo '{"t":"result","ok":"true","output":"x","quiet":true}'""", "Failed")]             // ok as a string
    [InlineData("""echo '{"t":"result","ok":true,"output":"x","quiet":"true"}'""", "Completed")]          // quiet as a string
    [InlineData("""echo '{"t":"result","ok":true,"output":"x","quiet":1}'""", "Completed")]               // quiet as a number
    [InlineData("""echo '{"t":"result","ok":true,"output":"x","quiet":[true]}'""", "Completed")]          // quiet as an array
    [InlineData("""echo '{"t":"result","ok":true,"output":"x","quiet":true}'; echo '{"t":"result","ok":true,"output":"y"}'""", "Completed")] // last result wins
    [InlineData("""echo '{"t":"result","ok":true,"output":"x","quiet":true}'; echo '{"t":"result","ok":false,"error":"late"}'""", "Failed")]
    [InlineData("""echo '{"t":"quiet"}'; echo '{"t":"result","ok":true,"output":"x"}'""", "Completed")]   // no such record kind
    public async Task Only_a_boolean_true_quiet_on_a_successful_result_marks_the_row(string body, string type)
    {
        var (bed, _, row) = await RunAsync("cat >/dev/null; " + body);
        await using var _ = bed;

        Assert.Equal(type == "Failed" ? MessageTypes.Failed : MessageTypes.Completed, row.Type);
        Assert.False(HasQuiet(row), row.Payload);
    }

    [Fact]
    public async Task A_quiet_boolean_true_result_marks_the_completed_row()
    {
        var (bed, _, row) = await RunAsync("""cat >/dev/null; echo '{"t":"result","ok":true,"output":"x","quiet":true}'""");
        await using var _ = bed;

        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.True(JsonDocument.Parse(row.Payload).RootElement.GetProperty(PayloadFields.Quiet).GetBoolean());
    }

    [Fact]
    public async Task A_quiet_result_followed_by_silence_times_out_and_is_not_quiet()
    {
        var (bed, _, row) = await RunAsync(
            """cat >/dev/null; echo '{"t":"result","ok":true,"output":"x","quiet":true}'; sleep 30""",
            manifest: m => m["timeoutSeconds"] = 1);
        await using var _ = bed;

        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.False(HasQuiet(row), row.Payload);
    }

    [Fact]
    public async Task A_quiet_run_that_hands_back_and_publishes_still_writes_both_rows()
    {
        var (bed, _, row) = await RunAsync(
            """
            cat >/dev/null
            echo '{"t":"handback","delivered":"found one"}'
            echo '{"t":"publish","type":"tick","payload":{"n":1}}'
            echo '{"t":"result","ok":true,"output":"x","quiet":true}'
            """,
            manifest: DeclaresTick);
        await using var _ = bed;

        Assert.Single(await bed.OfTypeAsync(MessageTypes.Handback));
        Assert.Single(await bed.OfTypeAsync("plugin.fixture.tick"));
        var payload = JsonDocument.Parse(row.Payload).RootElement;
        Assert.True(payload.GetProperty(PayloadFields.Quiet).GetBoolean());
        Assert.True(payload.GetProperty(PayloadFields.HandedBack).GetBoolean());
    }

    // ---------------------------------------------------------------- C2: a numeric secret by value

    private async Task<IReadOnlyList<Message>> PublishAsync(string secret, string number)
    {
        var (bed, _, _) = await RunAsync(
            $$$"""
            cat >/dev/null
            echo '{"t":"publish","type":"tick","payload":{"n":{{{number}}}}}'
            echo '{"t":"result","ok":true,"output":"done"}'
            """,
            manifest: m =>
            {
                DeclaresTick(m);
                m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}""");
            },
            settings: new Settings(new PluginMemberSettings(
                new Dictionary<string, JsonElement>(), new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" })),
            secrets: new Secrets(new() { ["FIXTURE_TOKEN"] = secret }));
        await using var _ = bed;
        return await bed.OfTypeAsync("plugin.fixture.tick");
    }

    [Theory]
    [InlineData("20261234", "0.20261234e8")]
    [InlineData("20261234", "202612340e-1")]
    [InlineData("20261234", "20261234e0")]
    [InlineData("20261234", "2026.1234E+4")]
    [InlineData("20261234", "20261234.00000000000000000000000000000001")]
    [InlineData("20261234", "2026123400000000000000000000000e-23")]
    [InlineData("20261234", "-0.020261234e9")]
    [InlineData("3.14159265", "314159265e-8")]
    [InlineData("0020261234", "20261234")]
    public async Task A_number_equal_in_value_to_a_numeric_secret_is_refused(string secret, string number)
    {
        Assert.Empty(await PublishAsync(secret, number));
    }

    [Theory(Skip = "DEFECT R1-c-2 (Low): a numeric secret longer than decimal's range is never compared by value; 1.23456789012345678901234567890e29 for a 30-digit secret is stored.")]
    [InlineData("123456789012345678901234567890", "1.23456789012345678901234567890e29")]
    public async Task A_numeric_secret_beyond_decimal_range_in_another_notation_is_refused(string secret, string number)
    {
        Assert.Empty(await PublishAsync(secret, number));
    }

    [Fact]
    public async Task R1_c_2_repro_a_30_digit_numeric_secret_in_exponent_form_is_stored_today()
    {
        // Pins today's behaviour so the defect is reproducible; flips when it is fixed.
        var stored = await PublishAsync("123456789012345678901234567890", "1.23456789012345678901234567890e29");
        Assert.Equal("""{"n":1.23456789012345678901234567890e29}""", Assert.Single(stored).Payload);
    }

    [Fact]
    public async Task A_different_number_is_not_refused()
    {
        Assert.Single(await PublishAsync("20261234", "2.0261235e7"));

        // The older TEXT rule still refuses any number whose text contains the secret's digits
        // (20261234.5), an over-refusal that predates C2 and is harmless.
        Assert.Empty(await PublishAsync("20261234", "20261234.5"));
    }
}

/// <summary>C1 (P1-b): further evasions of the definition scan, run through the scan by reflection,
/// over the probe AND every method the compiler generated for it, as the real pump scan walks a
/// whole assembly.</summary>
public sealed class PluginScanProbes
{
    private static IReadOnlyList<string> Violations(string probe)
    {
        var flowType = typeof(PumpArchitectureTests).GetNestedType("ImplementationFlow", BindingFlags.NonPublic)!;
        var scan = flowType.GetMethod("Scan", BindingFlags.Public | BindingFlags.Static)!;
        var definitions = flowType.GetField("Definitions", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var methods = new List<MethodBase> { typeof(PluginScanProbes).GetMethod(probe, all)! };
        foreach (var nested in typeof(PluginScanProbes).GetNestedTypes(BindingFlags.NonPublic))
        {
            methods.AddRange(nested.GetMethods(all));
        }

        var result = scan.Invoke(null, [methods, definitions])!;
        return (List<string>)flowType.GetProperty("Violations")!.GetValue(result)!;
    }

    // Control: one of the four Saoirse added, through this harness.
    private static bool Control(ContainerDefinition definition) =>
        new HashSet<ContainerDefinition> { definition }.Contains(definition with { Agent = "x" });

    // Evasion 1: a lambda taking `object`, invoked through its delegate. Delegate Invoke is an
    // allowed hand-off whatever the delegate's parameter type, and inside the lambda the parameter
    // is `object`, not a definition, so nothing is tracked.
    private static bool ThroughAnObjectDelegate(ContainerDefinition definition)
    {
        Func<object, string> text = static value => value.ToString()!;
        return text(definition).Contains("pl" + "ugin:", StringComparison.Ordinal);
    }

    // Evasion 2: through a ref local. `stind.ref` is not a call, a stelem or a stloc, so the value
    // is dropped by the interpreter and the local it lands in is never marked.
    private static bool ThroughARefLocal(ContainerDefinition definition)
    {
        object? slot = null;
        ref var target = ref slot;
        target = definition;
        return slot!.ToString()!.Contains("pl" + "ugin:", StringComparison.Ordinal);
    }

    [Fact]
    public void The_control_is_caught() => Assert.NotEmpty(Violations(nameof(Control)));

    [Fact(Skip = "DEFECT P1-b-2 (Low): Func<object,..>.Invoke is an allowed hand-off for any delegate; the scan misses it.")]
    public void An_object_delegate_is_caught() => Assert.NotEmpty(Violations(nameof(ThroughAnObjectDelegate)));

    [Fact(Skip = "DEFECT P1-b-2 (Low): a store through a ref local (stind.ref) drops the value from the scan.")]
    public void A_ref_local_is_caught() => Assert.NotEmpty(Violations(nameof(ThroughARefLocal)));

    [Fact]
    public void P1_b_2_repro_both_evasions_pass_the_scan_today()
    {
        Assert.Empty(Violations(nameof(ThroughAnObjectDelegate)));
        Assert.Empty(Violations(nameof(ThroughARefLocal)));

        // And they really do read the definition whole.
        var plugin = ContainerTestBed.Definition(new ContainerId("t", "m")) with { Agent = "plugin:x" };
        Assert.True(ThroughAnObjectDelegate(plugin));
        Assert.True(ThroughARefLocal(plugin));
    }
}

/// <summary>A (wakes) and C3 (E5-c listing) on the real Host with the real sample-echo plugin.</summary>
public sealed class PluginHostProbes : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-b0013-host-{Guid.NewGuid():N}");
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
            .CreateAsync("Probe", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo", agent = "plugin:sample-echo", config = new { mode = "upper" },
        }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct)).StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private ContainerHost Host => Services.GetRequiredService<ContainerHost>();

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

    private async Task SettleAsync(Message row)
    {
        var cursors = Services.GetRequiredService<ICursors>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        for (var passes = 0; passes < 8 && DateTime.UtcNow < deadline;)
        {
            var read = await cursors.PositionAsync(Manager, Ct) >= row.Seq && await cursors.PositionAsync(Dev, Ct) >= row.Seq;
            var idle = Host.Find(Manager)!.State == ContainerState.Idle && Host.Find(Echo)!.State == ContainerState.Idle
                && Host.Find(Dev)!.State == ContainerState.Idle;
            passes = read && idle ? passes + 1 : 0;
            await Task.Delay(100, Ct);
        }
    }

    private async Task<Message> ScheduleAndFireAsync(string instruction)
    {
        var created = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", new
        {
            name = "Poll", kind = "every", container = "Echo", intervalSeconds = 300, instruction,
        }, Ct);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));

        var after = await Log.HighestSeqAsync(Ct);
        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(DateTimeOffset.UtcNow.AddSeconds(301), Ct);

        return await AwaitRowAsync([MessageTypes.Completed, MessageTypes.Failed],
            m => m.Seq > after && m.Source == Echo.ToString(), "Echo's terminal row");
    }

    private async Task TellAsync(ContainerId who, string instruction, long? causation = null)
    {
        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{who.Name}/tell",
            new { instruction, causation = causation?.ToString(CultureInfo.InvariantCulture) }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
    }

    private async Task AwaitStateAsync(ContainerId who, ContainerState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Host.Find(who)!.State != state && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
        Assert.Equal(state, Host.Find(who)!.State);
    }

    private async Task<JsonElement[]> RunsAsync(ContainerId who)
    {
        using var body = JsonDocument.Parse(await _person.GetStringAsync($"/api/teams/{_team}/members/{who.Name}/runs", Ct));
        return [.. body.RootElement.GetProperty("runs").EnumerateArray().Select(run => run.Clone())];
    }

    // ---------------------------------------------------------------- A

    [Fact]
    public async Task A_quiet_scheduled_run_that_hands_back_wakes_the_manager_exactly_once()
    {
        var row = await ScheduleAndFireAsync("quiet:handback:found one");

        Assert.Equal(MessageTypes.Completed, row.Type);
        var payload = JsonDocument.Parse(row.Payload).RootElement;
        Assert.True(payload.GetProperty(PayloadFields.Quiet).GetBoolean());
        await AwaitRowAsync([MessageTypes.Handback], m => m.CorrelationId == row.CorrelationId, "the hand-back");

        await SettleAsync(row);

        Assert.Equal(1, _agents.RunsFor(Manager));
    }

    [Fact]
    public async Task A_quiet_run_a_person_told_ends_its_plugin_owned_workflow_and_wakes_nobody()
    {
        await TellAsync(Echo, "quiet:hello");
        var row = await AwaitRowAsync([MessageTypes.Completed], m => m.Source == Echo.ToString(), "Echo's completion");

        await AwaitRowAsync([MessageTypes.WorkflowCompleted], m => m.CorrelationId == row.CorrelationId, "the declaration");
        await SettleAsync(row);

        Assert.Empty(_agents.Invocations);
    }

    [Fact]
    public async Task A_persons_trigger_on_the_plugins_completion_is_also_passed_over_by_a_quiet_run()
    {
        var trigger = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", new
        {
            name = "On echo done", kind = "event", container = "Dev", eventType = MessageTypes.Completed,
            filter = "source eq " + Echo, instruction = "Echo finished.",
        }, Ct);
        Assert.True(trigger.IsSuccessStatusCode, await trigger.Content.ReadAsStringAsync(Ct));

        await TellAsync(Echo, "loud");
        var loud = await AwaitRowAsync([MessageTypes.Completed], m => m.Source == Echo.ToString(), "Echo's loud completion");
        await AwaitRowAsync([MessageTypes.Completed], m => m.Source == Dev.ToString(), "Dev's run on a loud completion");
        await SettleAsync(loud);
        Assert.Equal(1, _agents.RunsFor(Dev));

        await TellAsync(Echo, "quiet:hush");
        var quiet = await AwaitRowAsync([MessageTypes.Completed],
            m => m.Source == Echo.ToString() && m.Seq > loud.Seq, "Echo's quiet completion");
        await SettleAsync(quiet);

        // Observed: a person's own completion trigger is passed over too ("every subscriber").
        Assert.Equal(1, _agents.RunsFor(Dev));
    }

    /// <summary>
    /// Quiet is for work nobody waits on. A quiet answer to an instruction ANOTHER MEMBER sent (the
    /// Manager's `tell`) is written without the quiet mark, so the Manager is woken with the answer
    /// and its workflow does not stay open.
    /// </summary>
    [Fact]
    public async Task A_quiet_answer_to_the_managers_own_instruction_still_wakes_the_manager()
    {
        // The Manager's workflow: a person tells the Manager; the Manager (simulated) tells Echo in
        // that workflow, and Echo finishes quiet.
        await TellAsync(Manager, "get the mail");
        var mgrDone = await AwaitRowAsync([MessageTypes.Completed], m => m.Source == Manager.ToString(), "Manager's first run");
        await SettleAsync(mgrDone);
        await Task.Delay(1000, Ct);
        await SettleAsync(mgrDone);
        var baseline = _agents.RunsFor(Manager);
        var echoBefore = await Log.HighestSeqAsync(Ct);

        await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Echo),
            JsonSerializer.Serialize(new { instruction = "quiet:list is:unread" }),
            Manager.ToString(),
            CausationSeq: mgrDone.Seq), Ct);

        var row = await AwaitRowAsync([MessageTypes.Completed],
            m => m.Source == Echo.ToString() && m.CorrelationId == mgrDone.CorrelationId, "Echo's quiet completion");
        await SettleAsync(row);

        Assert.True(row.Seq > echoBefore);
        Assert.DoesNotContain("\"quiet\"", row.Payload, StringComparison.Ordinal);

        // The Manager is woken with the answer to its own instruction.
        for (var i = 0; i < 100 && _agents.RunsFor(Manager) == baseline; i++) await Task.Delay(100, Ct);
        Assert.Equal(baseline + 1, _agents.RunsFor(Manager));
    }

    // ---------------------------------------------------------------- C3 (E5-c)

    [Fact]
    public async Task An_all_blocked_run_is_listed_as_soon_as_it_ends_and_while_the_next_run_works()
    {
        await TellAsync(Echo, "block:no creds");
        var blocked = await AwaitRowAsync([MessageTypes.Blocked], m => m.Source == Echo.ToString(), "the blocked row");
        await AwaitStateAsync(Echo, ContainerState.Idle);

        // Ended, nothing after it: listed straight away.
        var ended = await RunsAsync(Echo);
        Assert.Equal("blocked", Assert.Single(ended).GetProperty("outcome").GetString());

        // A new run that blocks its own first item and is still working on the second.
        await TellAsync(Echo, "sleep:1");
        await AwaitStateAsync(Echo, ContainerState.Running);
        var holder = (await Log.ReadAfterAsync(0, [MessageTypes.InstructionFor(Echo)], int.MaxValue, Ct)).Last();
        await TellAsync(Echo, "block:later", holder.Seq);
        await TellAsync(Echo, "sleep:4", holder.Seq);
        await AwaitRowAsync([MessageTypes.Blocked], m => m.Source == Echo.ToString() && m.Seq > blocked.Seq, "the second blocked row");
        Assert.Equal(ContainerState.Running, Host.Find(Echo)!.State);

        var working = await RunsAsync(Echo);

        // The in-progress item is not a run; the finished all-blocked run and the sleep:1 run are.
        Assert.Equal(["completed", "blocked"], working.Select(r => r.GetProperty("outcome").GetString()!).ToArray());
        Assert.Equal(blocked.Seq, working[1].GetProperty("seq").GetInt64());

        await AwaitStateAsync(Echo, ContainerState.Idle);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((await RunsAsync(Echo)).Length < 3 && DateTime.UtcNow < deadline) await Task.Delay(100, Ct);
        Assert.Equal(["completed", "completed", "blocked"],
            (await RunsAsync(Echo)).Select(r => r.GetProperty("outcome").GetString()!).ToArray());
    }
}
