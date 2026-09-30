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

    private AgentInvocation Invocation(string agent, IReadOnlyDictionary<string, string>? environment = null)
    {
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);

        return new AgentInvocation(
            new ContainerId("alpha", "worker"), "You are a probe.", "hello", work,
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
