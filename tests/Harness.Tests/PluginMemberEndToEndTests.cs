using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE PROOF OF CONCEPT, END TO END, ON THE REAL HOST: the sample plugin in
/// <c>samples/plugins/sample-echo</c>, built with the tests, installed under a data root the way
/// docs/plugins.md tells an operator to, hired by a person through the ordinary member route into
/// an ordinary team beside an agent member, and told work through the ordinary `tell` route.
///
/// One test per point the spec names:
/// 1. existing agent members are unchanged;           <see cref="P1_An_agent_member_beside_it_runs_exactly_as_before"/>
/// 2. the plugin appears as a member of the team;     <see cref="P2_The_plugin_is_hired_and_listed_as_a_member"/>
/// 3. it runs the normal member lifecycle;            <see cref="P3_It_runs_the_normal_lifecycle_including_Stop"/>
/// 4. it receives work through the event queue;       <see cref="P4_P5_P6_Work_told_to_it_is_run_by_its_executable_and_the_result_is_visible"/>
/// 5. it is invoked through the generic runtime;      (same)
/// 6. its result and status are visible as usual;    (same)
/// 7. nothing plugin-specific is in the pump.         <see cref="P7_Nothing_plugin_specific_is_in_the_pump"/>
/// </summary>
public sealed class PluginMemberEndToEndTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string TokenKey = "SAMPLE_ECHO_E2E_TOKEN";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-e2e-{Guid.NewGuid():N}");
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
        InstallSampleEcho(_dataRoot);

        // A secret the member binds by LOGICAL KEY, set the way `secret set` sets one: in the
        // Host's environment. Unique to this class, so no other test can see or collide with it.
        Environment.SetEnvironmentVariable(TokenKey, "abcd1234");

        // Agents answer through a fake, as everywhere in this suite; the plugin runs for real.
        _agents.Behaviour = invocation => Task.FromResult(new AgentResult(0, $"noted by {invocation.Container.Name}"));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Mixed", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        // THE ORDINARY HIRE ROUTE, a person's, with the plugin's configuration and a secret binding.
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo",
            agent = "plugin:sample-echo",
            config = new { mode = "reverse" },
            secrets = new { token = TokenKey },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);

        var dev = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct);
        Assert.Equal(HttpStatusCode.OK, dev.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(TokenKey, null);
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>
    /// What docs/plugins.md tells an operator to do: the manifest, the launcher, the skills, and
    /// the published build under <c>lib/</c>, in <c>plugins/sample-echo/0.1.0</c>, with
    /// <c>active</c> naming it.
    /// </summary>
    internal static void InstallSampleEcho(string dataRoot)
    {
        var sample = SamplePath();
        var version = Path.Combine(dataRoot, "plugins", "sample-echo", "0.1.0");
        Directory.CreateDirectory(Path.Combine(version, "lib"));
        Directory.CreateDirectory(Path.Combine(version, "skills"));

        File.Copy(Path.Combine(sample, "plugin.json"), Path.Combine(version, "plugin.json"));
        File.Copy(Path.Combine(sample, "sample-echo"), Path.Combine(version, "sample-echo"));
        File.Copy(Path.Combine(sample, "skills", "sample-echo.md"), Path.Combine(version, "skills", "sample-echo.md"));
        File.SetUnixFileMode(Path.Combine(version, "sample-echo"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        var build = new[] { "Debug", "Release" }
            .Select(configuration => Path.Combine(sample, "src", "bin", configuration, "net10.0"))
            .Where(d => File.Exists(Path.Combine(d, "SampleEcho.dll")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "SampleEcho.dll")))
            .FirstOrDefault()
            ?? throw new InvalidOperationException("SampleEcho is not built; the test project builds it.");

        foreach (var file in Directory.EnumerateFiles(build))
        {
            File.Copy(file, Path.Combine(version, "lib", Path.GetFileName(file)));
        }

        File.WriteAllText(Path.Combine(dataRoot, "plugins", "sample-echo", PluginCatalog.ActiveFile), "0.1.0\n");
    }

    private static string SamplePath([CallerFilePath] string caller = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "..", "..", "samples", "plugins", "sample-echo"));

    /// <summary>Tells <paramref name="who"/> through the person's `tell` route and waits for the
    /// terminal row that run writes.</summary>
    private async Task<Message> TellAndAwaitAsync(ContainerId who, string instruction)
    {
        var after = await LastTerminalSeqAsync(who);

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{who.Name}/tell", new { instruction }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        return await NextTerminalAsync(who, after);
    }

    private async Task<long> LastTerminalSeqAsync(ContainerId who) =>
        (await TerminalRowsAsync(who)).Select(m => m.Seq).DefaultIfEmpty(0).Max();

    private async Task<IReadOnlyList<Message>> TerminalRowsAsync(ContainerId who) =>
        [.. (await Services.GetRequiredService<IMessageLog>()
                .ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
            .Where(m => m.Source == who.ToString())];

    private async Task<Message> NextTerminalAsync(ContainerId who, long after)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if ((await TerminalRowsAsync(who)).FirstOrDefault(m => m.Seq > after) is { } row) return row;
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"{who} wrote no terminal row after seq {after}.");
    }

    private static JsonElement Payload(Message row) => JsonDocument.Parse(row.Payload).RootElement.Clone();

    /// <summary>The member's snapshot as the BROWSER reads it: from its team on `GET /api/teams`.</summary>
    private async Task<JsonElement> SnapshotAsync(ContainerId who)
    {
        using var teams = JsonDocument.Parse(await _person.GetStringAsync("/api/teams", Ct));

        return teams.RootElement.EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == _team)
            .GetProperty("containers").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == who.Name)
            .Clone();
    }

    private static bool IsIdle(JsonElement snapshot) =>
        snapshot.GetProperty("state") is var state
        && (state.ValueKind == JsonValueKind.Number ? state.GetInt32() == 0 : state.GetString() == "Idle");

    private async Task AwaitIdleAsync(ContainerId who)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Services.GetRequiredService<ContainerHost>().Find(who)!.State != ContainerState.Idle && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task P1_An_agent_member_beside_it_runs_exactly_as_before()
    {
        var row = await TellAndAwaitAsync(Dev, "build the thing");

        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.Equal("noted by Dev", Payload(row).GetProperty("output").GetString());

        // The agent is still handed a composed role prompt, its credential and the prompt text -
        // byte for byte what it was before the refactor, which MemberGoldenTests pins on the goldens
        // recorded at step 0.
        var invocation = Assert.Single(_agents.Invocations, i => i.Container == Dev);
        Assert.StartsWith($"You are Dev, a member of {_team}.", invocation.SystemPrompt);
        Assert.True(invocation.Environment.ContainsKey("HARNESS_KEY"));
        Assert.Contains("build the thing", invocation.Prompt);
        Assert.Equal("agent", (await SnapshotAsync(Dev)).GetProperty("kind").GetString());

        // And no agent was ever invoked for the plugin member.
        Assert.DoesNotContain(_agents.Invocations, i => i.Container == Echo);
    }

    [Fact]
    public async Task P2_The_plugin_is_hired_and_listed_as_a_member()
    {
        var snapshot = await SnapshotAsync(Echo);

        Assert.Equal("Echo", snapshot.GetProperty("name").GetString());
        Assert.Equal("plugin:sample-echo", snapshot.GetProperty("agent").GetString());
        Assert.Equal("plugin", snapshot.GetProperty("kind").GetString());
        Assert.True(IsIdle(snapshot));

        // On the team's roster, beside the agent members.
        var members = Services.GetRequiredService<ContainerHost>().Snapshots().Where(s => s.Team == _team).ToList();
        Assert.Contains(members, s => s.Id == "Echo" && s.Kind == MemberRef.PluginKind);
        Assert.Contains(members, s => s.Id == "Dev" && s.Kind == MemberRef.AgentKind);

        // Persisted like any member, its settings beside it - the secret as a KEY, never a value.
        var row = (await Services.GetRequiredService<ITeamStore>().MembersAsync(Ct)).Single(m => m.Team == _team && m.Name == "Echo");
        Assert.Equal("plugin:sample-echo", row.Agent);
        var settings = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(Echo, Ct);
        Assert.Equal("reverse", settings.Config["mode"].GetString());
        Assert.Equal(TokenKey, settings.Secrets["token"]);

        // No credential was minted for it.
        Assert.Null(await Services.GetRequiredService<IPrincipalStore>().TeamForAsync(Echo.ToString(), Ct));
    }

    [Fact]
    public async Task P3_It_runs_the_normal_lifecycle_including_Stop()
    {
        var states = new ConcurrentQueue<ContainerState>();
        var host = Services.GetRequiredService<ContainerHost>();
        host.Changed += snapshot => { if (snapshot.Id == "Echo" && snapshot.Team == _team) states.Enqueue(snapshot.State); };

        var first = await TellAndAwaitAsync(Echo, "abc");
        Assert.Equal(MessageTypes.Completed, first.Type);
        await AwaitIdleAsync(Echo);
        Assert.Contains(ContainerState.Running, states);
        Assert.Equal(ContainerState.Idle, host.Find(Echo)!.State);

        // Stopped from the board, mid-run: the child is killed, the row says a person stopped it,
        // and the member takes its next instruction normally.
        var before = await LastTerminalSeqAsync(Echo);
        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/Echo/tell", new { instruction = "sleep:30" }, Ct);
        told.EnsureSuccessStatusCode();

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (host.Find(Echo)!.State != ContainerState.Running && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
        await Task.Delay(300, Ct);

        var stop = await _person.PostAsync($"/api/teams/{_team}/containers/Echo/stop", null, Ct);
        Assert.True(stop.IsSuccessStatusCode, await stop.Content.ReadAsStringAsync(Ct));

        var stopped = await NextTerminalAsync(Echo, before);
        Assert.True(stopped.Type == MessageTypes.Failed, stopped.Payload);
        Assert.True(Payload(stopped).GetProperty("stoppedByPerson").GetBoolean());
        Assert.Equal(FailureClasses.Interrupted, host.Find(Echo)!.Snapshot().FailureClass);

        var next = await TellAndAwaitAsync(Echo, "xyz");
        Assert.Equal(MessageTypes.Completed, next.Type);
        Assert.Null(host.Find(Echo)!.Snapshot().Failed);
    }

    [Fact]
    public async Task P4_P5_P6_Work_told_to_it_is_run_by_its_executable_and_the_result_is_visible()
    {
        // 5. THE GENERIC RUNTIME: the same MemberRuntime type every agent member is, over the one
        // IMemberRunner the Host holds, which routes this member to the plugin runner.
        var host = Services.GetRequiredService<ContainerHost>();
        Assert.IsType<MemberRuntime>(host.Find(Echo));
        Assert.IsType<MemberRuntime>(host.Find(Dev));
        Assert.IsType<PluginMemberRunner>(Assert.IsType<MemberRunnerRouter>(Services.GetRequiredService<IMemberRunner>()).Plugins);

        // 4. THE EVENT QUEUE: a person's `tell` is an instruction row on the log; the pump delivers
        // it to the member's queue, and the run it causes is caused by that row.
        var row = await TellAndAwaitAsync(Echo, "handback:shipped it");

        var instruction = (await Services.GetRequiredService<IMessageLog>()
                .ReadAfterAsync(0, [MessageTypes.InstructionFor(Echo)], int.MaxValue, Ct))
            .Last();
        Assert.Equal(instruction.Seq, row.CausationSeq);

        // 6. THE RESULT, WHERE A RESULT ALWAYS IS: the completed row's output - the executable really
        // ran: `mode: reverse` from its configuration, and the bound secret reached it (its length
        // only, as the sample reports it) - the card, and the hand-back mark.
        Assert.Equal(MessageTypes.Completed, row.Type);
        var payload = Payload(row);
        Assert.Equal("ti deppihs\ntoken length 8", payload.GetProperty("output").GetString());
        Assert.True(payload.GetProperty("handedBack").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("tokensIn").ValueKind);
        Assert.DoesNotContain("abcd1234", row.Payload, StringComparison.Ordinal);

        var snapshot = await SnapshotAsync(Echo);
        Assert.Equal("shipped it", snapshot.GetProperty("handedBack").GetString());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("failed").ValueKind);

        // Its progress record reached the card's feed through the same path an agent's does.
        Assert.Contains(
            await Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [MessageTypes.Progress], int.MaxValue, Ct),
            m => m.Source == Echo.ToString() && m.Payload.Contains("transforming 1 message(s) (reverse)", StringComparison.Ordinal));

        // THE MANAGER IS WOKEN, AND ONCE: by the hand-back, not again by the completion that carries
        // `handedBack` - the same one-wake rule an agent member's hand-back follows.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_agents.Invocations.Any(i => i.Container == Manager && i.Prompt.Contains("shipped it", StringComparison.Ordinal))
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        await Task.Delay(500, Ct);
        Assert.Single(_agents.Invocations, i => i.Container == Manager && i.Prompt.Contains("shipped it", StringComparison.Ordinal));

        // AND THE PLUGIN RAN ONCE. The platform's idle-workflow offer - "declare it or say what you
        // are waiting on" - goes only to a member holding the permit to answer it; a plugin holds
        // none, and handing it that prose would have it run the prose as work.
        Assert.Single(
            await Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [MessageTypes.Started], int.MaxValue, Ct),
            m => m.Source == Echo.ToString());
    }

    /// <summary>
    /// E1 (part 1 live test): a workflow a person starts by telling a plugin directly is the
    /// plugin's to declare, and a plugin holds no credential to declare with - so it stayed open
    /// forever, and the Manager was refused. The platform now declares it on the owner's behalf
    /// when a run in it ends successfully and nothing is left working it. No agent is involved:
    /// the declaration is written before the Manager is even woken by the plugin's result.
    /// </summary>
    [Fact]
    public async Task E1_A_workflow_a_person_starts_by_telling_a_plugin_ends_completed()
    {
        var log = Services.GetRequiredService<IMessageLog>();
        var row = await TellAndAwaitAsync(Echo, "abc");
        Assert.Equal(MessageTypes.Completed, row.Type);
        var correlation = row.CorrelationId;

        Message? declared = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (declared is null && DateTime.UtcNow < deadline)
        {
            declared = (await log.ReadCorrelationAsync(correlation, Ct)).FirstOrDefault(m => m.Type == MessageTypes.WorkflowCompleted);
            if (declared is null) await Task.Delay(50, Ct);
        }

        Assert.NotNull(declared);
        Assert.Equal(Echo.ToString(), declared.Source);
        Assert.True(Payload(declared).GetProperty(UndeclarableWorkflows.DeclaredByPlatformField).GetBoolean());
        Assert.Empty(await log.OpenWorkflowsAmongAsync([correlation], Ct));

        // NO AGENT DECLARED IT: nothing the Manager did under this workflow comes before it.
        var thread = await log.ReadCorrelationAsync(correlation, Ct);
        Assert.DoesNotContain(thread, m => m.Seq < declared.Seq && m.Source == Manager.ToString());
    }

    [Fact]
    public async Task A_plugin_failure_is_a_failed_run_with_the_plugins_own_words()
    {
        var row = await TellAndAwaitAsync(Echo, "fail:the bucket is gone");

        Assert.Equal(MessageTypes.Failed, row.Type);
        var snapshot = await SnapshotAsync(Echo);
        Assert.Equal("asked to fail: the bucket is gone", snapshot.GetProperty("failed").GetString());
        Assert.Equal(FailureClasses.Unknown, snapshot.GetProperty("failureClass").GetString());
    }

    [Fact]
    public async Task A_hire_with_configuration_the_manifest_refuses_is_refused_by_name()
    {
        var refused = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo2",
            agent = "plugin:sample-echo",
            config = new { mode = "sideways" },
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("must be one of: upper, reverse", await refused.Content.ReadAsStringAsync(Ct));
        Assert.Null(Services.GetRequiredService<ContainerHost>().Find(new ContainerId(_team, "Echo2")));
    }

    /// <summary>
    /// D2 (card 621 verification): a CLONE of the team re-hires the plugin member WITH its
    /// configuration and its secret bindings - logical keys, never values - so the clone's Echo
    /// reverses, as the source's does, and still reaches its secret. Before, it came back
    /// upper-casing with no secret, and the clone reported no failure.
    /// </summary>
    [Fact]
    public async Task A_cloned_team_keeps_the_plugin_members_configuration_and_secret_bindings()
    {
        var clone = await _person.PostAsJsonAsync($"/api/teams/{_team}/clone", new { name = "MixedClone" }, Ct);
        var body = await clone.Content.ReadAsStringAsync(Ct);
        Assert.True(clone.IsSuccessStatusCode, body);

        using var result = JsonDocument.Parse(body);
        Assert.Empty(result.RootElement.GetProperty("failures").EnumerateArray());
        var cloned = result.RootElement.GetProperty("team").GetProperty("id").GetString()!;

        var settings = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(cloned, "Echo"), Ct);
        Assert.Equal("reverse", settings.Config["mode"].GetString());
        Assert.Equal(TokenKey, settings.Secrets["token"]);

        var saved = _team;
        _team = cloned;
        try
        {
            var row = await TellAndAwaitAsync(new ContainerId(cloned, "Echo"), "abc");
            Assert.Equal(MessageTypes.Completed, row.Type);
            Assert.Equal("cba\ntoken length 8", Payload(row).GetProperty("output").GetString());
        }
        finally
        {
            _team = saved;
        }
    }

    /// <summary>
    /// 7. THE PUMP KNOWS NOTHING ABOUT PLUGINS, mechanically. Harness.Containers - the pump
    /// (ContainerHost) and the member runtime - has no code that names a plugin, and cannot: it
    /// does not reference the assembly the plugin runner lives in. The only branch between the two
    /// kinds is <see cref="MemberRunnerRouter"/>, in the Host.
    /// </summary>
    [Fact]
    public void P7_Nothing_plugin_specific_is_in_the_pump()
    {
        var containers = Path.GetFullPath(Path.Combine(SamplePath(), "..", "..", "..", "src", "Harness.Containers"));

        foreach (var file in Directory.EnumerateFiles(containers, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal)))
        {
            var code = File.ReadAllLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

            Assert.DoesNotContain(code, line => line.Contains("plugin", StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain("plugin", File.ReadAllText(Path.Combine(containers, "ContainerHost.cs")), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Harness.Host", File.ReadAllText(Path.Combine(containers, "Harness.Containers.csproj")), StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(MemberRuntime).Assembly.GetReferencedAssemblies(), a => a.Name == "Harness.Host");
    }
}
