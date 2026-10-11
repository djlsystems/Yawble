using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE SIGN-IN PROBE IS ASKED OF A WORKER. Every command on the shared home goes to one worker in one
/// request; an issued preset is answered in control from the resolver and asks nobody; no worker, or
/// none that answers, is not measured and never signed out; the answer is cached for thirty seconds;
/// the doctor reads the probe control recorded and asks nothing. The probe itself, on the worker,
/// runs the status command with the CLI's update-off.
/// </summary>
public sealed class SignInOnWorkerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-signin-worker-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AgentCatalog Catalog = new(
    [
        new AgentDefinition("claude-headless", AgentMode.Headless, new AgentLaunch("claude", ["-p"])),
        new AgentDefinition("claude", AgentMode.Interactive, new AgentLaunch("claude", [])),
        new AgentDefinition("codex", AgentMode.Headless, new AgentLaunch("codex", ["exec"])),
    ]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_probe_asks_one_worker_for_every_home_command_in_one_request()
    {
        var worker = Answering("w1", (command, i) => new SignInProbeResult(command, true, i == 0, $"{command} from w1"));
        var probe = new AgentAuthProbe(Catalog, asks: AsksOf(worker), dataRoot: _root);

        var reports = await probe.ReportsAsync(Ct);

        var asked = Assert.IsType<ProbeSignIn>(Assert.Single(worker.Sent));
        Assert.Equal(["claude", "codex"], asked.Commands.Select(c => c.Command));
        Assert.Equal("ANTHROPIC_API_KEY", asked.Commands[0].CredentialVariable);

        Assert.All(reports.Where(r => r.Command == "claude"), r => Assert.Equal((true, (bool?)true, "claude from w1"), (r.Installed!.Value, r.Authenticated, r.Detail)));
        var codex = Assert.Single(reports, r => r.Agent == "codex");
        Assert.Equal((true, (bool?)false, "codex from w1"), (codex.Installed!.Value, codex.Authenticated, codex.Detail));

        // Recorded for the doctor: when, on which worker, and each command's answer.
        var record = AgentAuthRecord.Read(_root)!;
        Assert.Equal("w1", record.Worker);
        Assert.Equal(new CommandSignIn("codex", true, false, "codex from w1"), record.For("codex"));
    }

    [Fact]
    public async Task An_issued_preset_is_answered_in_control_and_asks_no_worker()
    {
        var worker = Answering("w1", (command, _) => new SignInProbeResult(command, true, true, "from w1"));
        var issued = new RunCredential(
            CredentialSource.Issued, new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "fake-issued-value" }, [], [], true, null);
        var catalog = new AgentCatalog([Catalog.Definitions[0]]);

        var reports = await new AgentAuthProbe(catalog, credentials: new OneCredential(issued), asks: AsksOf(worker)).ReportsAsync(Ct);

        var report = Assert.Single(reports);
        Assert.Equal("issued", report.Source);
        Assert.True(report.Authenticated);
        Assert.Empty(worker.Sent);
    }

    [Fact]
    public async Task No_worker_reads_not_measured_never_signed_in()
    {
        var asks = new WorkerAsks(() => throw new InvalidOperationException("No worker is connected."));

        var reports = await new AgentAuthProbe(Catalog, asks: asks, dataRoot: _root).ReportsAsync(Ct);

        Assert.Equal(3, reports.Count);
        Assert.All(reports, r =>
        {
            Assert.Null(r.Installed);
            Assert.Null(r.Authenticated);
            Assert.Equal("Not measured: no worker is connected to ask this CLI.", r.Detail);
        });

        var record = AgentAuthRecord.Read(_root)!;
        Assert.Null(record.Worker);
        Assert.All(record.Commands, c => Assert.Null(c.Installed));
    }

    [Fact]
    public async Task A_worker_that_does_not_answer_reads_not_measured()
    {
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var silent = new ScriptedCliWorker("w2");
        silent.Received += _ => sent.TrySetResult();
        var asks = new WorkerAsks(() => silent, clock);
        silent.Asks = asks;

        var reading = new AgentAuthProbe(Catalog, asks: asks).ReportsAsync(Ct);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Inside the bound it still waits; past it, nobody answered.
        clock.Advance(AgentAuthProbe.Bound(2) - TimeSpan.FromSeconds(1));
        await Task.Delay(50, Ct);
        Assert.False(reading.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));

        var reports = await reading.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.All(reports, r =>
        {
            Assert.Null(r.Installed);
            Assert.Null(r.Authenticated);
            Assert.Equal("Not measured: worker w2 did not answer.", r.Detail);
        });
    }

    [Fact]
    public async Task The_cache_holds_for_thirty_seconds_and_forget_clears_it()
    {
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var worker = Answering("w1", (command, _) => new SignInProbeResult(command, true, true, "from w1"));
        var probe = new AgentAuthProbe(Catalog, asks: AsksOf(worker), clock: clock);

        await probe.ReportsAsync(Ct);
        clock.Advance(TimeSpan.FromSeconds(29));
        await probe.ReportsAsync(Ct);
        Assert.Single(worker.Sent);

        clock.Advance(TimeSpan.FromSeconds(1));
        await probe.ReportsAsync(Ct);
        Assert.Equal(2, worker.Sent.Count);

        probe.Forget();
        await probe.ReportsAsync(Ct);
        Assert.Equal(3, worker.Sent.Count);
    }

    [Fact]
    public async Task The_doctor_reads_controls_last_probe_and_runs_nothing()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        var catalog = new AgentCatalog([.. AgentAuthProbe.LoadSpecs().Keys.Select(command =>
            new AgentDefinition(command + "-headless", AgentMode.Headless, new AgentLaunch(command, [])))]);
        var worker = Answering("w7", (command, _) => new SignInProbeResult(command, true, command == "claude", $"{command}: what worker w7 saw"));

        await new AgentAuthProbe(catalog, asks: AsksOf(worker), dataRoot: _root, clock: clock).ReportsAsync(Ct);
        var report = await HostDoctor.ReportAsync(_root, Ct);

        // The worker's words, at the probe's time, on the worker that answered: a doctor that asked a
        // CLI itself would have its own words, and no worker to name.
        Assert.Single(worker.Sent);
        Assert.All(report.Agents, agent =>
        {
            Assert.Equal($"{agent.Agent}: what worker w7 saw", agent.Detail);
            Assert.True(agent.Installed);
            Assert.True(agent.InstalledAsMeasured);
            Assert.Equal(agent.Agent == "claude", agent.Authenticated);
            Assert.Equal(clock.GetUtcNow(), agent.MeasuredAt);
            Assert.Equal("w7", agent.MeasuredOn);
        });

        using var json = System.Text.Json.JsonDocument.Parse(HostDoctor.ToJson(report));
        var claude = json.RootElement.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("agent").GetString() == "claude");
        Assert.True(claude.GetProperty("installed").GetBoolean());
        Assert.Equal("w7", claude.GetProperty("measuredOn").GetString());
        Assert.Equal(clock.GetUtcNow(), claude.GetProperty("measuredAt").GetDateTimeOffset());

        // With no record, nothing is measured: installed is null on the wire, never "not installed".
        File.Delete(Path.Combine(_root, AgentAuthRecord.FileName));
        using var bare = System.Text.Json.JsonDocument.Parse(HostDoctor.ToJson(await HostDoctor.ReportAsync(_root, Ct)));
        Assert.All(bare.RootElement.GetProperty("agents").EnumerateArray(), a =>
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Null, a.GetProperty("installed").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, a.GetProperty("measuredAt").ValueKind);
            Assert.StartsWith("Not measured: the Host has recorded no sign-in probe yet.", a.GetProperty("detail").GetString());
        });
    }

    [Fact]
    public async Task The_probe_runs_its_status_command_with_update_off()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        var said = Path.Combine(_root, "said");
        var cli = Path.Combine(_root, "fake-cli");
        await TestExecutable.WriteAsync(cli, $"#!/bin/sh\necho \"$* UPDATER=$FAKE_UPDATER\" > '{said}'\nexit 0\n");

        var worker = new WorkerAgentCli(new WorkerId("w1"), null, new RunHomes(null, RunHomeRemoval.For(null)));
        var answer = Assert.IsType<SignInProbed>(await worker.AnswerAsync(new ProbeSignIn("r1",
        [
            new SignInProbeSpec(cli, null, null, ["auth", "status"], new Dictionary<string, string> { ["FAKE_UPDATER"] = "off" }, ["--no-update"]),
        ]), Ct));

        Assert.Equal("r1", answer.Request);
        Assert.Equal(new SignInProbeResult(cli, true, true, "The status command exited 0."), Assert.Single(answer.Results));
        Assert.Equal("--no-update auth status UPDATER=off", (await File.ReadAllTextAsync(said, Ct)).Trim());
    }

    [Fact]
    public async Task The_probe_records_each_commands_install_against_the_worker_that_answered()
    {
        var worker = Answering("w1", (command, _) => new SignInProbeResult(command, command == "codex", null, "from w1"));
        var installs = new WorkerInstalls(() => [new WorkerId("w1")]);

        await new AgentAuthProbe(Catalog, asks: AsksOf(worker), measured: installs).ReportsAsync(Ct);

        Assert.Equal([("w1", true)], installs.For("codex").Select(m => (m.Worker, m.Installed)));
        Assert.Equal([("w1", false)], installs.For("claude").Select(m => (m.Worker, m.Installed)));
    }

    [Fact]
    public async Task With_no_worker_the_probe_records_nothing()
    {
        var installs = new WorkerInstalls(() => [new WorkerId("w1")]);
        var asks = new WorkerAsks(() => throw new InvalidOperationException("No worker is connected."));

        await new AgentAuthProbe(Catalog, asks: asks, measured: installs).ReportsAsync(Ct);

        Assert.Empty(installs.For("claude"));
        Assert.Empty(installs.For("codex"));
    }

    [Fact]
    public async Task A_worker_that_does_not_answer_in_time_records_nothing()
    {
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var silent = new ScriptedCliWorker("w2");
        silent.Received += _ => sent.TrySetResult();
        var asks = new WorkerAsks(() => silent, clock);
        silent.Asks = asks;
        var installs = new WorkerInstalls(() => [new WorkerId("w2")]);

        var reading = new AgentAuthProbe(Catalog, asks: asks, measured: installs).ReportsAsync(Ct);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        clock.Advance(AgentAuthProbe.Bound(2) + TimeSpan.FromSeconds(1));
        await reading.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Empty(installs.For("claude"));
        Assert.Empty(installs.For("codex"));
    }

    /// <summary>
    /// IN CONTROL AN ISSUED PRESET'S INSTALL IS THE WORKERS' MEASUREMENT, never control's own PATH:
    /// `sh` is on every PATH and a worker measured it missing; the other command is on none and a worker
    /// measured it installed; a third nobody measured is null.
    /// </summary>
    [Fact]
    public async Task In_control_an_issued_presets_install_reads_the_workers_measurement()
    {
        Assert.NotNull(Harness.Pty.PathSearch.Find("sh"));
        Assert.Null(Harness.Pty.PathSearch.Find(AbsentCli));
        Assert.Null(Harness.Pty.PathSearch.Find(UnmeasuredCli));
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("sh-headless", AgentMode.Headless, new AgentLaunch("sh", [])),
            new AgentDefinition("absent-headless", AgentMode.Headless, new AgentLaunch(AbsentCli, [])),
            new AgentDefinition("unmeasured-headless", AgentMode.Headless, new AgentLaunch(UnmeasuredCli, [])),
        ]);
        var installs = new WorkerInstalls(() => [new WorkerId("w1")]);
        installs.Record(new WorkerId("w1"), [("sh", false), (AbsentCli, true)]);
        var worker = Answering("w1", (command, _) => new SignInProbeResult(command, true, true, "from w1"));

        var reports = await new AgentAuthProbe(
            catalog, credentials: new OneCredential(Issued), asks: AsksOf(worker),
            installs: new AgentInstallProbe(workers: installs), measured: installs).ReportsAsync(Ct);

        Assert.Equal(
            [("sh-headless", (bool?)false), ("absent-headless", true), ("unmeasured-headless", null)],
            reports.Select(r => (r.Agent, r.Installed)));
        Assert.All(reports, r => Assert.Equal("issued", r.Source));
        Assert.Empty(worker.Sent);
    }

    [Fact]
    public async Task In_all_an_issued_presets_install_is_this_machines_path_as_before()
    {
        Assert.NotNull(Harness.Pty.PathSearch.Find("sh"));
        Assert.Null(Harness.Pty.PathSearch.Find(AbsentCli));
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("sh-headless", AgentMode.Headless, new AgentLaunch("sh", [])),
            new AgentDefinition("absent-headless", AgentMode.Headless, new AgentLaunch(AbsentCli, [])),
        ]);
        var worker = Answering("w1", (command, _) => new SignInProbeResult(command, true, true, "from w1"));

        var reports = await new AgentAuthProbe(catalog, credentials: new OneCredential(Issued), asks: AsksOf(worker)).ReportsAsync(Ct);

        Assert.Equal([("sh-headless", (bool?)true), ("absent-headless", false)], reports.Select(r => (r.Agent, r.Installed)));
        Assert.Empty(worker.Sent);
    }

    private const string AbsentCli = "no-such-cli-7f3a9c";

    private const string UnmeasuredCli = "no-such-cli-unmeasured-41d2";

    private static readonly RunCredential Issued = new(
        CredentialSource.Issued, new Dictionary<string, string> { ["SOME_API_KEY"] = "fake-issued-value" }, [], [], true, null);

    private static ScriptedCliWorker Answering(string id, Func<string, int, SignInProbeResult> result) =>
        new(id, message => message is ProbeSignIn probe
            ? new SignInProbed(probe.Request, [.. probe.Commands.Select((c, i) => result(c.Command, i))])
            : null);

    private static WorkerAsks AsksOf(ScriptedCliWorker worker)
    {
        var asks = new WorkerAsks(() => worker);
        worker.Asks = asks;
        return asks;
    }

    private sealed class OneCredential(RunCredential credential) : IRunCredentials
    {
        public Task<RunCredential> ResolveAsync(string preset, AgentDefinition? definition, CancellationToken ct) => Task.FromResult(credential);
    }
}
