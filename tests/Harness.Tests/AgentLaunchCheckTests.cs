using Harness.Contracts;
using System.Text.Json;
using Harness.Host;
using Harness.Tests.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE LAUNCH CHECK GOES THROUGH THE MEMBER LAUNCH PATH. A fake CLI that aborts under a data limit
/// is started by <see cref="ProcessAgentRunner.CheckLaunchAsync"/> with the runner's own memory
/// limit and reads failed with its exit code and stderr tail; the same CLI with no limit reads ok;
/// a preset with no free invocation reads not checked and nothing is started.
/// </summary>
public sealed class AgentLaunchCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-launch-check-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// A CLI whose runtime refuses to start under a data limit below 1 GB, as one did on arm64 under a
    /// per-process cap: it reads its own RLIMIT_DATA, so only a limit the launch really applied
    /// aborts it (the test process may itself run under a larger one). It records the arguments and the isolation and update-off variables it was given.
    /// </summary>
    private string Script(string seen) =>
        "#!/bin/sh\n"
        + $"echo \"$* isolated=$FAKE_ISOLATED update=$FAKE_UPDATE_OFF\" > '{seen}'\n"
        + "limit=$(ulimit -d)\n"
        + "if [ \"$limit\" != unlimited ] && [ \"$limit\" -lt 1048576 ]; then\n"
        + "  echo \"fatal: cannot reserve the heap under a data limit of ${limit} kB: Cannot allocate memory\" >&2\n"
        + "  kill -ABRT $$\n"
        + "fi\n"
        + "echo 'fake-cli 1.2.3'\n";

    private async Task<(ProcessAgentRunner Runner, string Seen)> RunnerAsync(
        RunMemoryLimits memory, IReadOnlyList<string>? launchCheck)
    {
        var seen = Path.Combine(_root, "seen-" + Guid.NewGuid().ToString("N")[..6]);
        var program = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "bin-" + Guid.NewGuid().ToString("N")[..6])).FullName, "fake-cli");
        await TestExecutable.WriteAsync(program, Script(seen));

        var catalog = new AgentCatalog(
        [
            new AgentDefinition(
                "fake", AgentMode.Headless, new AgentLaunch(program, ["-p", "{userPrompt}"]),
                Isolation: new AgentIsolation(["--isolate"], new Dictionary<string, string> { ["FAKE_ISOLATED"] = "1" }),
                Updates: new AgentUpdates(new Dictionary<string, string> { ["FAKE_UPDATE_OFF"] = "1" }, ["--no-update"]),
                LaunchCheck: launchCheck),
            new AgentDefinition("fake-terminal", AgentMode.Interactive, new AgentLaunch(program, []), LaunchCheck: ["--version"]),
        ]);

        return (new ProcessAgentRunner(catalog, new RunHeartbeat(), memory: memory), seen);
    }

    [Fact]
    public async Task A_cli_that_aborts_under_the_run_memory_limit_reads_failed_with_its_exit_code_and_stderr_tail()
    {
        var prlimit = SystemCommand.Find("prlimit");
        Assert.SkipWhen(!OperatingSystem.IsLinux() || prlimit is null,
            "Needs Linux and prlimit in a root-owned system directory to apply RLIMIT_DATA.");

        var memory = RunMemoryLimits.Decide(new CgroupFacts(null, "the test's cgroup is not used"), prlimit,
            () => new RunMemoryLimit(64, "runs.memoryLimitMb is set to 64 MB", Set: true));
        var (runner, seen) = await RunnerAsync(memory, ["--version"]);

        var launch = await runner.CheckLaunchAsync("fake", TestContext.Current.CancellationToken);

        Assert.Equal(AgentLaunchReport.Failed, launch.Result);
        Assert.Equal(134, launch.ExitCode);
        Assert.Contains("data limit of 65536 kB", launch.StderrTail);
        Assert.Contains("64 MB", launch.Detail);
        Assert.Contains("exited 134", launch.Detail);

        // The member launch's update-off and isolation environment, the declared invocation, and
        // nothing of the prompt launch: no `-p`, no prompt, no isolation switch.
        Assert.Equal("--no-update --version isolated=1 update=1", File.ReadAllText(seen).Trim());
    }

    [Fact]
    public async Task A_cli_that_starts_reads_ok_and_an_unset_rlimit_applies_no_data_limit()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        var (runner, _) = await RunnerAsync(RunMemoryLimits.NotAvailable("no mechanism in this test"), ["--version"]);
        var launch = await runner.CheckLaunchAsync("fake", TestContext.Current.CancellationToken);

        Assert.Equal(AgentLaunchReport.Ok, launch.Result);
        Assert.Equal(0, launch.ExitCode);
        Assert.Null(launch.StderrTail);
        Assert.Contains("exited 0", launch.Detail);

        if (OperatingSystem.IsLinux() && SystemCommand.Find("prlimit") is { } prlimit)
        {
            var unset = RunMemoryLimits.Decide(new CgroupFacts(null, "the test's cgroup is not used"), prlimit,
                () => new RunMemoryLimit(1792, "computed, never applied under rlimit"));
            var (rlimitRunner, _) = await RunnerAsync(unset, ["--version"]);
            Assert.Equal(AgentLaunchReport.Ok, (await rlimitRunner.CheckLaunchAsync("fake", TestContext.Current.CancellationToken)).Result);
        }
    }

    [Fact]
    public async Task A_preset_with_no_free_invocation_reads_not_checked_and_nothing_is_started()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        var (runner, seen) = await RunnerAsync(RunMemoryLimits.NotAvailable("no mechanism in this test"), launchCheck: null);

        var undeclared = await runner.CheckLaunchAsync("fake", TestContext.Current.CancellationToken);
        Assert.Equal(AgentLaunchReport.NotChecked, undeclared.Result);
        Assert.Null(undeclared.ExitCode);
        Assert.Null(undeclared.StderrTail);
        Assert.Contains("launchCheck", undeclared.Detail);

        var interactive = await runner.CheckLaunchAsync("fake-terminal", TestContext.Current.CancellationToken);
        Assert.Equal(AgentLaunchReport.NotChecked, interactive.Result);

        Assert.False(File.Exists(seen));
    }

    [Fact]
    public async Task An_issued_presets_launch_check_runs_with_its_credential_and_own_home()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        var seen = Path.Combine(_root, "seen-issued");
        var program = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "bin-issued")).FullName, "fake-cli");
        await TestExecutable.WriteAsync(program, $"#!/bin/sh\necho \"key=$FAKE_KEY home=$HOME\" > '{seen}'\n");

        var catalog = new AgentCatalog(
            [new AgentDefinition("fake", AgentMode.Headless, new AgentLaunch(program, ["-p", "{userPrompt}"]), LaunchCheck: ["--version"])]);
        var issued = new RunCredential(
            CredentialSource.Issued, new Dictionary<string, string> { ["FAKE_KEY"] = "fake-launch-check-key" }, [], [], true, null);

        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat(), credentials: new Fixed(issued));
        var launch = await runner.CheckLaunchAsync("fake", TestContext.Current.CancellationToken);

        Assert.Equal(AgentLaunchReport.Ok, launch.Result);
        var line = File.ReadAllText(seen).Trim();
        Assert.StartsWith("key=fake-launch-check-key home=", line);

        // A home of its own, gone once the check is done.
        var home = line[(line.IndexOf("home=", StringComparison.Ordinal) + 5)..];
        Assert.StartsWith(RunHome.Prefix, Path.GetFileName(home));
        Assert.NotEqual(Environment.GetEnvironmentVariable("HOME"), home);
        Assert.False(Directory.Exists(home));

        // And one whose credential is not set fails as a member run would, starting nothing.
        File.Delete(seen);
        var missing = new ProcessAgentRunner(catalog, new RunHeartbeat(), credentials: new Fixed(RunCredential.NotSet("the credential is not set")));
        var refused = await missing.CheckLaunchAsync("fake", TestContext.Current.CancellationToken);
        Assert.Equal(AgentLaunchReport.Failed, refused.Result);
        Assert.Equal("the credential is not set", refused.Detail);
        Assert.False(File.Exists(seen));
    }

    private sealed class Fixed(RunCredential credential) : IRunCredentials
    {
        public Task<RunCredential> ResolveAsync(string agent, AgentDefinition? definition, CancellationToken ct) =>
            Task.FromResult(credential);
    }

    [Fact]
    public void Every_built_in_headless_model_preset_declares_a_free_invocation_and_no_other_preset_does()
    {
        foreach (var preset in AgentCatalogFile.BuiltIns())
        {
            var declares = preset.LaunchCheck is { Count: > 0 };
            Assert.Equal(preset.Mode == AgentMode.Headless && preset.Launch.LanguageModel, declares);
        }
    }

    /// <summary>
    /// An idle-output floor is declared only from a measurement, the way the launch check is
    /// declared: on an interactive built-in, with what was measured. Until a person measures one, it
    /// reads the platform's default - said as `default`, never as measured.
    /// </summary>
    [Fact]
    public void Every_built_in_interactive_preset_declares_an_idle_output_floor_or_reads_default_and_no_headless_preset_declares_one()
    {
        foreach (var preset in AgentCatalogFile.BuiltIns())
        {
            if (preset.Mode == AgentMode.Headless)
            {
                Assert.Null(preset.IdleOutput);
                continue;
            }

            var floor = OutputFloor.Of(preset);
            if (preset.IdleOutput is { } declared)
            {
                Assert.Equal(OutputFloor.Declared, floor.Source);
                Assert.True(declared.BytesPerMinute > 0, preset.Name);
                Assert.False(string.IsNullOrWhiteSpace(declared.MeasuredWith), preset.Name);
            }
            else
            {
                Assert.Equal(OutputFloor.PlatformDefault, floor);
                Assert.Equal(OutputFloor.Default, floor.Source);
                Assert.Null(floor.MeasuredWith);
            }
        }
    }

    [Fact]
    public void The_launch_report_serialises_to_the_contract_shape()
    {
        var report = new AgentAuthReport("fake", "fake-cli", true, true, "Authenticated.",
            Launch: new AgentLaunchReport(AgentLaunchReport.Failed, 134, "fatal: Cannot allocate memory", "exited 134"));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, JsonSerializerOptions.Web));
        var launch = json.RootElement.GetProperty("launch");
        Assert.Equal(["result", "exitCode", "stderrTail", "detail"], launch.EnumerateObject().Select(p => p.Name));
        Assert.Equal("failed", launch.GetProperty("result").GetString());
        Assert.Equal(134, launch.GetProperty("exitCode").GetInt32());

        using var notChecked = JsonDocument.Parse(JsonSerializer.Serialize(AgentLaunchReport.Unchecked("none declared"), JsonSerializerOptions.Web));
        Assert.Equal("not checked", notChecked.RootElement.GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Null, notChecked.RootElement.GetProperty("exitCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, notChecked.RootElement.GetProperty("stderrTail").ValueKind);
    }
}

/// <summary>
/// <c>GET /api/agents/auth</c> carries every preset's launch check beside its sign-in, and a preset
/// that declares no free invocation reads not checked there.
/// </summary>
public sealed class AgentLaunchCheckRouteTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task Every_preset_carries_a_launch_and_one_without_a_free_invocation_is_not_checked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();

        using var reports = JsonDocument.Parse(await client.GetStringAsync("/api/agents/auth", ct));
        var catalog = host.Services.GetRequiredService<AgentCatalog>();

        foreach (var report in reports.RootElement.EnumerateArray())
        {
            var launch = report.GetProperty("launch");
            Assert.Contains(launch.GetProperty("result").GetString(),
                new[] { AgentLaunchReport.Ok, AgentLaunchReport.Failed, AgentLaunchReport.NotChecked });
            Assert.True(launch.TryGetProperty("exitCode", out _));
            Assert.True(launch.TryGetProperty("stderrTail", out _));
            Assert.True(launch.TryGetProperty("detail", out _));

            var definition = catalog.Definition(report.GetProperty("agent").GetString()!)!;
            if (definition.LaunchCheck is not { Count: > 0 })
            {
                Assert.Equal(AgentLaunchReport.NotChecked, launch.GetProperty("result").GetString());
            }
        }
    }

    [Fact]
    public async Task The_doctor_reads_the_launch_check_the_host_recorded_per_command()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();

        using var reports = JsonDocument.Parse(await client.GetStringAsync("/api/agents/auth", ct));
        var record = AgentLaunchChecksRecord.Read(host.DataRoot);
        Assert.NotNull(record);

        // The Host's record is the check the route serves, preset by preset.
        foreach (var report in reports.RootElement.EnumerateArray())
        {
            var preset = Assert.Single(record.Presets, p => p.Preset == report.GetProperty("agent").GetString());
            Assert.Equal(report.GetProperty("launch").GetProperty("result").GetString(), preset.Launch.Result);
        }

        // And the doctor, another process, states it per command from that record, never by running one.
        var doctor = await HostDoctor.ReportAsync(host.DataRoot, ct);
        Assert.All(doctor.Agents, agent =>
        {
            Assert.NotNull(agent.Launch);
            Assert.Equal(record.ForCommand(agent.Agent), agent.Launch);
            var mine = record.Presets.Where(p => p.Command is { } c && Path.GetFileName(c) == agent.Agent).ToArray();
            if (mine.Length == 0 || mine.All(p => p.Launch.Result == AgentLaunchReport.NotChecked))
            {
                Assert.Equal(AgentLaunchReport.NotChecked, agent.Launch.Result);
            }
        });
    }
}

