using System.Collections.Concurrent;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// AN AGENT CLI UPDATES ONLY WHEN THE PLATFORM SAYS SO. Every launch the Host makes of a
/// self-updating CLI - a member, the Concierge's terminal, the sign-in probe - carries what turns its
/// own updater off, and an update the platform runs waits for the runs in flight and holds new
/// launches until it is done.
///
/// Stub programs only, at absolute paths in this test's own folder: no real agent starts
/// (<c>AgentCliIsolation</c>), and nothing here changes the process's PATH.
/// </summary>
public sealed class AgentUpdatesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-agent-updates-").FullName;

    public void Dispose()
    {
        MemberTempCleanup.Remove(Path.Combine(_root, "work"));
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>What each self-updating CLI's built-in presets must carry, as it was found in the CLI.</summary>
    public static TheoryData<string, string, string?, string?> SelfUpdating => new()
    {
        { "claude", "claude", "DISABLE_AUTOUPDATER", "1" },
        { "claude-headless", "claude", "DISABLE_AUTOUPDATER", "1" },
        { "copilot", "copilot", "COPILOT_AUTO_UPDATE", "false" },
        { "copilot-headless", "copilot", "COPILOT_AUTO_UPDATE", "false" },
        { "grok", "grok", "GROK_DISABLE_AUTOUPDATER", "1" },
        { "grok-headless", "grok", "GROK_DISABLE_AUTOUPDATER", "1" },
        { "codex", "codex", null, null },
        { "codex-headless", "codex", null, null },
    };

    private static AgentCatalog BuiltIns() => new([.. AgentCatalogFile.BuiltIns()]);

    [Theory]
    [MemberData(nameof(SelfUpdating))]
    public void Every_launch_of_a_self_updating_preset_carries_its_update_off(
        string preset, string command, string? variable, string? value)
    {
        var catalog = BuiltIns();
        var definition = catalog.Definition(preset)!;
        var launch = definition.Mode == AgentMode.Headless ? catalog.For(preset)! : catalog.Interactive(preset)!;

        Assert.Equal(command, launch.FileName);
        Assert.NotNull(definition.Updates);

        if (variable is not null)
        {
            Assert.Equal(value, launch.UpdateEnvironment![variable]);
        }
        else
        {
            // codex has no variable for it: the key, for this launch alone, after its own arguments.
            Assert.Equal(["-c", "check_for_update_on_startup=false"], launch.Arguments.TakeLast(2));
        }

        // The platform's own update is declared, so a person can ask for one.
        Assert.NotEmpty(definition.Updates!.Update!);

        // The sign-in probe is keyed by command, and gets the same.
        var probe = AgentUpdates.ForCommand(command)!;
        if (variable is not null) Assert.Equal(value, probe.Environment![variable]);
        else Assert.Equal(["-c", "check_for_update_on_startup=false"], probe.Arguments!);
    }

    [Fact]
    public void A_preset_that_runs_no_model_carries_no_update_off()
    {
        var catalog = BuiltIns();

        Assert.Null(catalog.For("echo")!.UpdateEnvironment);
        Assert.Null(catalog.Definition("echo")!.Updates);
    }

    [Fact]
    public async Task A_members_launch_sets_the_update_off_last_so_no_preset_or_team_env_turns_it_back_on()
    {
        var program = Path.Combine(_root, "stub-env");
        await TestExecutable.WriteAsync(program, "#!/bin/sh\ncat >/dev/null\nenv\n");
        var claude = BuiltIns().Definition("claude-headless")!;

        var catalog = new AgentCatalog(
        [
            new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch(program, [], LanguageModel: false),
                Env: new Dictionary<string, string> { ["DISABLE_AUTOUPDATER"] = "0" },
                Updates: claude.Updates),
        ]);

        var result = await new ProcessAgentRunner(catalog, new RunHeartbeat()).RunAsync(
            Invocation("probe", new Dictionary<string, string> { ["DISABLE_AUTOUPDATER"] = "0" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("DISABLE_AUTOUPDATER=1", result.Output.Split('\n'));
        Assert.DoesNotContain("DISABLE_AUTOUPDATER=0", result.Output.Split('\n'));
    }

    /// <summary>
    /// A member's launch carries BOTH declarations: the preset's isolation (the platform's tools only)
    /// and its update-off. Neither replaces the other's arguments or variables.
    /// </summary>
    [Theory]
    [InlineData("claude-headless", "ENABLE_CLAUDEAI_MCP_SERVERS", "DISABLE_AUTOUPDATER", null)]
    [InlineData("grok-headless", "GROK_MEMORY", "GROK_DISABLE_AUTOUPDATER", null)]
    [InlineData("copilot-headless", null, "COPILOT_AUTO_UPDATE", "--disable-builtin-mcps")]
    [InlineData("codex-headless", null, null, "--ignore-user-config")]
    public void A_members_launch_carries_its_isolation_and_its_update_off(
        string preset, string? isolationVariable, string? updateVariable, string? isolationArgument)
    {
        var launch = BuiltIns().For(preset)!;

        if (isolationVariable is not null) Assert.True(launch.IsolationEnvironment!.ContainsKey(isolationVariable));
        if (isolationArgument is not null) Assert.Contains(isolationArgument, launch.Arguments);
        if (updateVariable is not null) Assert.True(launch.UpdateEnvironment!.ContainsKey(updateVariable));
        else Assert.Equal(["-c", "check_for_update_on_startup=false"], launch.Arguments.TakeLast(2));
    }

    [Fact]
    public async Task A_members_process_gets_the_isolation_and_the_update_off_variables_together()
    {
        var program = Path.Combine(_root, "stub-env");
        await TestExecutable.WriteAsync(program, "#!/bin/sh\ncat >/dev/null\nenv\n");
        var claude = BuiltIns().Definition("claude-headless")!;

        var catalog = new AgentCatalog(
        [
            new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch(program, [], LanguageModel: false),
                Isolation: claude.Isolation, Updates: claude.Updates),
        ]);

        var result = await new ProcessAgentRunner(catalog, new RunHeartbeat()).RunAsync(
            Invocation("probe", new Dictionary<string, string>()), TestContext.Current.CancellationToken);

        var lines = result.Output.Split('\n');
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("DISABLE_AUTOUPDATER=1", lines);
        Assert.Contains("ENABLE_CLAUDEAI_MCP_SERVERS=false", lines);
        Assert.Contains("CLAUDE_CODE_DISABLE_AUTO_MEMORY=1", lines);
    }

    [Theory]
    [InlineData("claude", "DISABLE_AUTOUPDATER", "1")]
    [InlineData("copilot", "COPILOT_AUTO_UPDATE", "false")]
    [InlineData("grok", "GROK_DISABLE_AUTOUPDATER", "1")]
    [InlineData("codex", null, null)]
    public async Task The_Concierges_terminal_carries_the_update_off(string preset, string? variable, string? value)
    {
        var catalog = BuiltIns();
        var factory = new ConciergeLaunchFactory(
            new TeamPaths(_root), "http://localhost:5000", new MintingPrincipals(), catalog);

        var spec = await factory.ForAsync(
            team: "", teamLabel: "this instance", user: "user-1", login: "person@example.com",
            preset, teamEnv: new Dictionary<string, string> { ["DISABLE_AUTOUPDATER"] = "0" },
            ct: TestContext.Current.CancellationToken);

        if (variable is not null) Assert.Equal(value, spec.Env![variable]);
        else
        {
            var at = spec.Argv!.ToList().IndexOf("check_for_update_on_startup=false");
            Assert.True(at > 1, string.Join(' ', spec.Argv!));
            Assert.Equal("-c", spec.Argv![at - 1]);
        }
    }

    private sealed class RecordedReports : IMemberReports
    {
        public ConcurrentQueue<string> Progress { get; } = new();

        public Task<MemberReportOutcome> ProgressAsync(ContainerId member, string status, CancellationToken ct = default)
        {
            Progress.Enqueue(status);
            return Task.FromResult(MemberReportOutcome.Ok);
        }

        private static Task<MemberReportOutcome> Unexpected() => throw new InvalidOperationException("only progress");

        public Task<MemberReportOutcome> BlockedAsync(ContainerId member, string reason, int? item = null, CancellationToken ct = default) => Unexpected();
        public Task<MemberReportOutcome> DeferAsync(ContainerId member, int item, string reason, CancellationToken ct = default) => Unexpected();
        public Task<MemberReportOutcome> NeedsDecisionAsync(ContainerId member, string question, CancellationToken ct = default) => Unexpected();
        public Task<MemberReportOutcome> HandbackAsync(ContainerId member, string delivered, CancellationToken ct = default) => Unexpected();
        public Task<MemberReportOutcome> PublishAsync(ContainerId member, string type, string payload, CancellationToken ct = default) => Unexpected();
    }

    [Fact]
    public async Task A_platform_update_waits_for_the_run_in_flight_holds_a_new_launch_and_that_launch_then_starts()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = Path.Combine(_root, "order.log");
        var release = Path.Combine(_root, "release");

        // ONE STUB CLI, launched by two presets as `claude` and `claude-headless` launch one program.
        // A run logs its start, waits until released, and logs its end; `--version` answers at once;
        // `update` logs itself, which is what the platform's declared update runs.
        var cli = Path.Combine(_root, "stub-cli");
        await TestExecutable.WriteAsync(cli, $$"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "stub 1.0"; exit 0; fi
            if [ "$1" = "update" ]; then echo "update" >> '{{log}}'; exit 0; fi
            cat >/dev/null
            echo "start $1" >> '{{log}}'
            if [ "$1" = "first" ]; then while [ ! -f '{{release}}' ]; do sleep 0.05; done; fi
            echo "end $1" >> '{{log}}'
            """);

        var updates = new AgentUpdates(new Dictionary<string, string> { ["STUB_AUTOUPDATE"] = "off" }, Update: [cli, "update"]);
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("first", AgentMode.Headless, new AgentLaunch(cli, ["first"], LanguageModel: false), Updates: updates),
            new AgentDefinition("second", AgentMode.Headless, new AgentLaunch(cli, ["second"], LanguageModel: false), Updates: updates),
        ]);

        var gate = new AgentUpdateGate();
        var reports = new RecordedReports();
        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat(), reports: reports, updates: gate);
        var updater = new AgentCliUpdater(catalog, gate, dataRoot: _root);

        // A run in flight.
        var first = runner.RunAsync(Invocation("first"), ct);
        await WaitUntilAsync(() => Lines(log).Contains("start first"), ct);
        Assert.Equal(1, gate.Running(cli));

        // The person's update: asked for, and waiting for that run.
        var update = updater.UpdateAsync("first", ct);
        await WaitUntilAsync(() => gate.Updating(cli), ct);

        // A new launch now is HELD, not failed, and says so once.
        var second = runner.RunAsync(Invocation("second"), ct);
        await WaitUntilAsync(() => !reports.Progress.IsEmpty, ct);
        await Task.Delay(300, ct);
        Assert.False(second.IsCompleted);
        Assert.False(update.IsCompleted);
        Assert.DoesNotContain("update", Lines(log));
        Assert.DoesNotContain("start second", Lines(log));
        Assert.Contains("the platform is updating", Assert.Single(reports.Progress));

        // The run in flight ends; the update runs; the held launch then starts and completes.
        await File.WriteAllTextAsync(release, string.Empty, ct);

        var firstResult = await first;
        var updateResult = await update;
        var secondResult = await second;

        Assert.Equal(0, firstResult.ExitCode);
        Assert.True(updateResult!.Updated, updateResult.Detail);
        Assert.Equal("stub 1.0", updateResult.VersionAfter);
        Assert.Equal(0, secondResult.ExitCode);
        Assert.Null(secondResult.LaunchError);
        Assert.Equal(["start first", "end first", "update", "start second", "end second"], Lines(log));
        Assert.Equal(0, gate.Running(cli));

        // And the update is in the version history, marked as the platform's.
        var history = await CliVersionHistory.In(_root).ReadAsync(1, ct);
        Assert.Equal("update", Assert.Single(history).By);
        Assert.Equal("stub 1.0", history[0].Versions[cli]);
    }

    [Fact]
    public async Task A_persons_update_asked_while_a_run_is_in_flight_answers_at_once_names_that_run_then_updates_then_says_its_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = Path.Combine(_root, "order.log");
        var release = Path.Combine(_root, "release");
        var go = Path.Combine(_root, "go");

        // `update` itself waits until told, so "updating" is a state the test can see, not a race.
        var cli = Path.Combine(_root, "stub-cli");
        await TestExecutable.WriteAsync(cli, $$"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "stub 1.0"; exit 0; fi
            if [ "$1" = "update" ]; then while [ ! -f '{{go}}' ]; do sleep 0.05; done; echo "update" >> '{{log}}'; exit 0; fi
            cat >/dev/null
            echo "start $1" >> '{{log}}'
            if [ "$1" = "first" ]; then while [ ! -f '{{release}}' ]; do sleep 0.05; done; fi
            echo "end $1" >> '{{log}}'
            """);

        var updates = new AgentUpdates(new Dictionary<string, string> { ["STUB_AUTOUPDATE"] = "off" }, Update: [cli, "update"]);
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("first", AgentMode.Headless, new AgentLaunch(cli, ["first"], LanguageModel: false), Updates: updates),
            new AgentDefinition("second", AgentMode.Headless, new AgentLaunch(cli, ["second"], LanguageModel: false), Updates: updates),
        ]);

        var gate = new AgentUpdateGate();
        var reports = new RecordedReports();
        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat(), reports: reports, updates: gate);
        var updater = new AgentCliUpdater(catalog, gate, dataRoot: _root);

        // alpha/worker's run is in flight.
        var first = runner.RunAsync(Invocation("first"), ct);
        await WaitUntilAsync(() => Lines(log).Contains("start first"), ct);

        // ASKED, NOT AWAITED: the answer is already there, and it is the gate's: waiting for that run.
        var finals = new List<AgentUpdateState>();
        var (asked, started) = updater.Request("first", "person@example.test", s => { lock (finals) finals.Add(s); return Task.CompletedTask; })!.Value;
        Assert.True(started);
        Assert.Equal(AgentUpdatePhases.Waiting, asked.Phase);
        Assert.Equal(1, asked.Running);
        Assert.Equal([new AgentRunHolder("alpha", "worker")], asked.InFlight);
        Assert.Equal("person@example.test", asked.RequestedBy);

        // A launch now is held, and the state names it; its card is told what it waits for.
        var second = runner.RunAsync(Invocation("second", container: new ContainerId("beta", "builder")), ct);
        await WaitUntilAsync(() => gate.StateOf(cli).Held.Count == 1, ct);
        Assert.Equal([new AgentRunHolder("beta", "builder")], gate.StateOf(cli).Held);
        await WaitUntilAsync(() => !reports.Progress.IsEmpty, ct);
        Assert.StartsWith($"Waiting for the {cli} update", Assert.Single(reports.Progress));

        // Asked again while it waits: the same update, not a second.
        var (again, startedAgain) = updater.Request("second", "other@example.test")!.Value;
        Assert.False(startedAgain);
        Assert.Equal(AgentUpdatePhases.Waiting, again.Phase);
        Assert.Equal("person@example.test", again.RequestedBy);

        // The run ends: the update runs, and cannot be cancelled now.
        await File.WriteAllTextAsync(release, string.Empty, ct);
        await first;
        await WaitUntilAsync(() => gate.StateOf(cli).Phase == AgentUpdatePhases.Updating, ct);
        var updating = gate.StateOf(cli);
        Assert.Equal(0, updating.Running);
        Assert.Empty(updating.InFlight);
        Assert.NotNull(updating.StartedAt);
        Assert.Equal((null, true), gate.Cancel(cli, "person@example.test"));

        // It finishes: the outcome is what was measured, and the held launch starts.
        await File.WriteAllTextAsync(go, string.Empty, ct);
        await WaitUntilAsync(() => gate.StateOf(cli).Phase == AgentUpdatePhases.Done, ct);
        var done = gate.StateOf(cli);
        Assert.True(done.Result!.Updated, done.Result.Detail);
        Assert.Equal("stub 1.0", done.Result.VersionBefore);
        Assert.Equal("stub 1.0", done.Result.VersionAfter);
        Assert.NotNull(done.FinishedAt);

        Assert.Equal(0, (await second).ExitCode);
        Assert.Equal(["start first", "end first", "update", "start second", "end second"], Lines(log));
        await WaitUntilAsync(() => { lock (finals) return finals.Count == 1; }, ct);
        Assert.Equal(AgentUpdatePhases.Done, finals[0].Phase);
    }

    [Fact]
    public async Task Cancelling_a_waiting_update_removes_it_and_releases_the_launches_it_held()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new AgentUpdateGate();
        var ran = false;

        var inFlight = await gate.EnterRunAsync("stub", null, ct, new AgentRunHolder("alpha", "worker"));
        var (asked, _) = gate.Request("stub", "stub-headless", "person@example.test", _ =>
        {
            ran = true;
            return Task.FromResult<AgentUpdateResult>(null!);
        });
        Assert.Equal(AgentUpdatePhases.Waiting, asked.Phase);

        var held = gate.EnterRunAsync("stub", null, ct, new AgentRunHolder("beta", "builder"));
        await WaitUntilAsync(() => gate.StateOf("stub").Held.Count == 1, ct);
        Assert.False(held.IsCompleted);

        var (cancelled, running) = gate.Cancel("stub", "person@example.test");
        Assert.False(running);
        Assert.Equal(AgentUpdatePhases.Cancelled, cancelled!.Phase);
        Assert.Equal("person@example.test", cancelled.CancelledBy);

        // The held launch goes ahead; the update is gone and nothing ran, even once the run ends.
        using (await held.WaitAsync(TimeSpan.FromSeconds(5), ct)) { }
        Assert.False(gate.Updating("stub"));
        inFlight.Dispose();
        await Task.Delay(100, ct);
        Assert.False(ran);
        Assert.Equal(AgentUpdatePhases.Cancelled, gate.StateOf("stub").Phase);
        Assert.Empty(gate.StateOf("stub").Held);
        Assert.Equal((null, false), gate.Cancel("stub", "person@example.test"));

        // And a new update can be asked for.
        var (next, started) = gate.Request("stub", "stub-headless", null, _ => Task.FromResult(
            new AgentUpdateResult("stub-headless", "stub", true, 0, "1", "2", DateTimeOffset.UtcNow, "ok")));
        Assert.True(started);
        await WaitUntilAsync(() => gate.StateOf("stub").Phase == AgentUpdatePhases.Done, ct);
    }

    [Fact]
    public async Task A_launch_stopped_while_held_by_an_update_is_an_interruption_and_releases_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var cli = Path.Combine(_root, "stub-quick");
        await TestExecutable.WriteAsync(cli, "#!/bin/sh\ncat >/dev/null\necho ran\n");

        var catalog = new AgentCatalog(
            [new AgentDefinition("quick", AgentMode.Headless, new AgentLaunch(cli, [], LanguageModel: false))]);
        var gate = new AgentUpdateGate();
        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat(), updates: gate);

        var finish = new TaskCompletionSource<int>();
        var update = gate.UpdateAsync(cli, _ => finish.Task, ct);
        await WaitUntilAsync(() => gate.Updating(cli), ct);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(200);
        var result = await runner.RunAsync(Invocation("quick"), stop.Token);

        Assert.Equal(FailureClasses.Interrupted, result.FailureClass);
        Assert.Contains("waited for the platform's update", result.LaunchError);

        finish.SetResult(0);
        await update;
        Assert.Equal(0, gate.Running(cli));
        Assert.False(gate.Updating(cli));
    }

    [Fact]
    public void The_version_history_says_when_the_installed_version_arrived()
    {
        CliVersionsAtStart At(string when, string? claude) =>
            new(DateTimeOffset.Parse(when), new Dictionary<string, string?> { ["claude"] = claude });

        // Newest first: 2.1.285 arrived at the 20:46 start, and the person's update kept it.
        var history = new[]
        {
            At("2026-09-30T09:00:00Z", "2.1.285"),
            At("2026-09-29T20:46:00Z", "2.1.285"),
            At("2026-09-29T19:05:00Z", "2.1.284"),
            At("2026-09-25T15:22:00Z", "2.1.284"),
        };

        var now = CliVersionHistory.Now("claude", history);

        Assert.Equal("2.1.285", now.Version);
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T20:46:00Z"), now.UpdatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T15:22:00Z"), now.Since);

        // Never changed in the kept history: no update time, and how far back it looked.
        var steady = CliVersionHistory.Now("claude", history[2..]);
        Assert.Null(steady.UpdatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T15:22:00Z"), steady.Since);
    }

    private AgentInvocation Invocation(
        string agent, IReadOnlyDictionary<string, string>? environment = null, ContainerId? container = null)
    {
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);

        return new AgentInvocation(
            container ?? new ContainerId("alpha", "worker"), "You are a probe.", "hello", work,
            environment ?? new Dictionary<string, string>(), Agent: agent);
    }

    private static IReadOnlyList<string> Lines(string path) =>
        File.Exists(path) ? [.. File.ReadAllLines(path).Where(l => l.Length > 0)] : [];

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold in time.");
            await Task.Delay(25, ct);
        }
    }
}
