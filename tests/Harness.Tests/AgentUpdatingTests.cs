using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;
using Harness.Pty;

namespace Harness.Tests;

/// <summary>
/// WHILE THE PLATFORM'S UPDATE HOLDS A COMMAND IT READS UPDATING - waiting for its runs, or running on a
/// named worker - in the install state, the sign-in, the launch check and the tool listing, and never
/// <c>AgentNotInstalled</c>. An answer taken across a hold is dropped, never recorded, and when the hold
/// ends every worker is measured again and the command reads installed.
///
/// The install-state cases invert this machine's PATH as the control-role tests do: the command held is
/// <c>sh</c>, which is on every PATH, measured MISSING by a worker before the hold, or (in all) one on no
/// PATH - so a state that fell through to a PATH lookup or to the old answer reads the opposite. Every
/// update is a task the test completes: nothing runs one.
/// </summary>
public sealed class AgentUpdatingTests
{
    private const string Absent = "no-such-cli-7f3a9c";
    private const string OnPath = "sh";

    private static readonly WorkerId W1 = new("worker-1");
    private static readonly WorkerId W2 = new("worker-2");
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void PathIsAsAssumed()
    {
        Assert.NotNull(PathSearch.Find(OnPath));
        Assert.Null(PathSearch.Find(Absent));
    }

    private static AgentDefinition Preset(string name, string command) =>
        new(name, AgentMode.Headless, new AgentLaunch(command, []));

    // ---------------------------------------------------------------------------------------------
    // The install state.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_command_whose_update_waits_reads_updating_and_never_not_installed()
    {
        PathIsAsAssumed();
        var gate = new AgentUpdateGate();
        var installs = new WorkerInstalls(() => [W1]);
        installs.Record(W1, [(OnPath, false)]);
        var probe = new AgentInstallProbe(workers: installs, gate: gate);
        Assert.Equal(AgentInstallStates.NotInstalled, probe.Probe(Preset("sh-headless", OnPath)).State);

        using var run = await gate.EnterRunAsync(OnPath, null, Ct);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = gate.UpdateAsync(OnPath, _ => release.Task, Ct);

        var held = probe.Probe(Preset("sh-headless", OnPath), referenced: true);
        Assert.Equal(AgentInstallStates.Updating, held.State);
        Assert.Equal(
            "Updating sh: waiting for 1 run of it to finish, then it runs on one worker; it is measured again when the update ends.",
            held.Message);
        Assert.Equal(AgentUpdatePhases.Waiting, held.Updating!.Phase);
        Assert.Null(held.Updating.Worker);
        Assert.DoesNotContain("worker-1", held.Message, StringComparison.Ordinal);
        Assert.Null(held.ResolvedPath);
        Assert.Null(probe.Measured(Preset("sh-headless", OnPath)));

        run.Dispose();
        release.SetResult(true);
        await update.WaitAsync(Bound, Ct);
    }

    [Fact]
    public async Task A_command_whose_update_runs_reads_updating_on_the_worker_running_it()
    {
        PathIsAsAssumed();
        var gate = new AgentUpdateGate();
        var installs = new WorkerInstalls(() => [W1]);
        installs.Record(W1, [(OnPath, false)]);
        var probe = new AgentInstallProbe(workers: installs, gate: gate);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = gate.UpdateAsync(OnPath, _ => release.Task, Ct);
        gate.RunsOn(OnPath, "worker-1");

        var held = probe.Probe(Preset("sh-headless", OnPath));

        Assert.Equal(AgentInstallStates.Updating, held.State);
        Assert.Equal("Updating sh on worker-1; it is measured again when the update ends.", held.Message);
        Assert.Equal((AgentUpdatePhases.Updating, "worker-1"), (held.Updating!.Phase, held.Updating.Worker));

        release.SetResult(true);
        await update.WaitAsync(Bound, Ct);
        Assert.Equal(AgentInstallStates.NotInstalled, probe.Probe(Preset("sh-headless", OnPath)).State);
        Assert.Null(probe.Probe(Preset("sh-headless", OnPath)).Updating);
    }

    [Fact]
    public async Task In_all_a_held_command_reads_updating_on_this_machine_never_not_found()
    {
        PathIsAsAssumed();
        var gate = new AgentUpdateGate();
        var probe = new AgentInstallProbe(gate: gate);
        Assert.Equal(AgentInstallStates.NotInstalled, probe.Probe(Preset("absent-headless", Absent)).State);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = gate.UpdateAsync(Absent, _ => release.Task, Ct);

        var held = probe.Probe(Preset("absent-headless", Absent));
        Assert.Equal(AgentInstallStates.Updating, held.State);
        Assert.Equal($"Updating {Absent} on this machine; it is measured again when the update ends.", held.Message);
        Assert.Null(held.MeasuredOn);

        release.SetResult(true);
        await update.WaitAsync(Bound, Ct);
    }

    // ---------------------------------------------------------------------------------------------
    // Answers taken across a hold are dropped: three ways a hold and an ask can overlap.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_answer_taken_during_a_hold_is_discarded_and_the_older_one_stands()
    {
        var gate = new AgentUpdateGate();
        var installs = new WorkerInstalls(() => [W1]);
        installs.Record(W1, [(OnPath, true)]);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = gate.UpdateAsync(OnPath, _ => release.Task, Ct);

        var turn = gate.Turn;
        installs.Record(W1, [(OnPath, false)], command => gate.HeldSince(command, turn));

        Assert.True(Assert.Single(installs.For(OnPath)).Installed);
        release.SetResult(true);
        await update.WaitAsync(Bound, Ct);
    }

    [Fact]
    public async Task An_answer_asked_before_a_hold_and_returned_after_it_ended_is_discarded()
    {
        var gate = new AgentUpdateGate();
        var installs = new WorkerInstalls(() => [W1]);
        installs.Record(W1, [(OnPath, true)]);

        var turn = gate.Turn;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = gate.UpdateAsync(OnPath, _ => release.Task, Ct);
        release.SetResult(true);
        await update.WaitAsync(Bound, Ct);
        Assert.Null(gate.Holding(OnPath));

        installs.Record(W1, [(OnPath, false)], command => gate.HeldSince(command, turn));

        Assert.True(Assert.Single(installs.For(OnPath)).Installed);
    }

    [Fact]
    public async Task An_answer_asked_during_a_hold_and_returned_after_it_is_discarded()
    {
        var gate = new AgentUpdateGate();
        var installs = new WorkerInstalls(() => [W1]);
        installs.Record(W1, [(OnPath, true)]);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = gate.UpdateAsync(OnPath, _ => release.Task, Ct);

        var turn = gate.Turn;
        release.SetResult(true);
        await update.WaitAsync(Bound, Ct);

        installs.Record(W1, [(OnPath, false)], command => gate.HeldSince(command, turn));

        Assert.True(Assert.Single(installs.For(OnPath)).Installed);

        // An answer asked after the hold ended is recorded.
        var after = gate.Turn;
        installs.Record(W1, [(OnPath, false)], command => gate.HeldSince(command, after));
        Assert.False(Assert.Single(installs.For(OnPath)).Installed);
    }

    [Fact]
    public async Task The_join_measurement_does_not_ask_about_a_held_command_and_drops_an_answer_from_across_a_hold()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        var held = await bed.HoldAsync(RemeasureBed.Cli, AgentUpdatePhases.Updating, "w1");

        await MeasureInstallsWhenAWorkerJoins.MeasureAsync(
            w1.Scripted, [RemeasureBed.Cli, RemeasureBed.Other], bed.Asks, bed.Installs, ct: Ct, gate: bed.Gate);

        Assert.Equal([RemeasureBed.Other], Assert.Single(w1.Probes).Commands.Select(c => c.Command));
        Assert.Empty(bed.Installs.For(RemeasureBed.Cli));
        await held.EndAsync();

        // The hold begins while the probe is out: its answer is dropped.
        w1.Installed[RemeasureBed.Cli] = false;
        RemeasureBed.Held? during = null;
        w1.BeforeProbeAnswer = _ => during ??= bed.HoldAsync(RemeasureBed.Cli, AgentUpdatePhases.Updating).GetAwaiter().GetResult();
        await MeasureInstallsWhenAWorkerJoins.MeasureAsync(w1.Scripted, [RemeasureBed.Cli], bed.Asks, bed.Installs, ct: Ct, gate: bed.Gate);
        Assert.Empty(bed.Installs.For(RemeasureBed.Cli));
        await during!.EndAsync();
    }

    // ---------------------------------------------------------------------------------------------
    // The sign-in.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AgentUpdatePhases.Waiting)]
    [InlineData(AgentUpdatePhases.Updating)]
    public async Task The_sign_in_of_a_held_command_is_not_asked_and_reads_updating(string phase)
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        var held = await bed.HoldAsync(RemeasureBed.Cli, phase, phase == AgentUpdatePhases.Updating ? "w1" : null);

        var reports = await bed.Auth.ReportsAsync(Ct);

        Assert.Equal([RemeasureBed.Other], Assert.Single(w1.Probes).Commands.Select(c => c.Command));
        var claude = Assert.Single(reports, r => r.Agent == RemeasureBed.Preset);
        Assert.Null(claude.Installed);
        Assert.Null(claude.Authenticated);
        Assert.StartsWith("Updating claude", claude.Updating);
        Assert.Equal(claude.Updating, claude.Detail);
        if (phase == AgentUpdatePhases.Waiting) Assert.DoesNotContain("w1", claude.Updating, StringComparison.Ordinal);
        else Assert.Contains("on w1", claude.Updating, StringComparison.Ordinal);

        var recorded = bed.AuthRecord!.For(RemeasureBed.Cli)!;
        Assert.Equal((null, null, claude.Updating), (recorded.Installed, recorded.Authenticated, recorded.Updating));
        Assert.True(bed.AuthRecord!.For(RemeasureBed.Other)!.Installed);
        Assert.Empty(bed.Installs.For(RemeasureBed.Cli));

        await held.EndAsync();
    }

    [Fact]
    public async Task A_cached_sign_in_from_before_the_hold_reads_updating_during_it_and_not_after()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        Assert.True(Assert.Single(await bed.Auth.ReportsAsync(Ct), r => r.Agent == RemeasureBed.Preset).Installed);

        var held = await bed.HoldAsync(RemeasureBed.Cli, AgentUpdatePhases.Updating, "w1");
        var during = Assert.Single(await bed.Auth.ReportsAsync(Ct), r => r.Agent == RemeasureBed.Preset);
        Assert.Null(during.Installed);
        Assert.NotNull(during.Updating);
        Assert.Single(w1.Probes);

        await held.EndAsync();
        var after = Assert.Single(await bed.Auth.ReportsAsync(Ct), r => r.Agent == RemeasureBed.Preset);
        Assert.True(after.Installed);
        Assert.Null(after.Updating);
        Assert.Equal(2, w1.Probes.Count);
    }

    // ---------------------------------------------------------------------------------------------
    // The launch check.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AgentUpdatePhases.Waiting)]
    [InlineData(AgentUpdatePhases.Updating)]
    public async Task The_launch_check_of_a_held_command_reads_updating(string phase)
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        var held = await bed.HoldAsync(RemeasureBed.Cli, phase, phase == AgentUpdatePhases.Updating ? "w1" : null);

        var report = await bed.Runner.CheckLaunchAsync(RemeasureBed.Preset, Ct);

        Assert.Equal(AgentLaunchReport.Updating, report.Result);
        Assert.StartsWith("Updating claude", report.Detail);
        if (phase == AgentUpdatePhases.Waiting) Assert.DoesNotContain("w1", report.Detail, StringComparison.Ordinal);
        else Assert.Contains("on w1", report.Detail, StringComparison.Ordinal);
        Assert.Empty(w1.Checks);

        await held.EndAsync();
    }

    [Fact]
    public async Task A_cached_launch_check_from_before_the_hold_reads_updating_during_it()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        Assert.Equal(AgentLaunchReport.Ok, (await bed.Launch.ReportsAsync(Ct))[RemeasureBed.Preset].Result);
        var checks = w1.Checks.Count;

        var held = await bed.HoldAsync(RemeasureBed.Cli, AgentUpdatePhases.Waiting);
        var during = await bed.Launch.ReportsAsync(Ct);

        Assert.Equal(AgentLaunchReport.Updating, during[RemeasureBed.Preset].Result);
        Assert.Equal(AgentLaunchReport.Ok, during[RemeasureBed.OtherPreset].Result);
        Assert.Equal(checks, w1.Checks.Count);
        await held.EndAsync();
    }

    [Fact]
    public async Task A_cached_updating_launch_check_does_not_outlive_the_hold()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        var held = await bed.HoldAsync(RemeasureBed.Cli, AgentUpdatePhases.Updating, "w1");
        Assert.Equal(AgentLaunchReport.Updating, (await bed.Launch.ReportsAsync(Ct))[RemeasureBed.Preset].Result);

        await held.EndAsync();
        var after = await bed.Launch.ReportsAsync(Ct);

        Assert.Equal(AgentLaunchReport.Ok, after[RemeasureBed.Preset].Result);
        Assert.Contains(w1.Checks, c => c.Launch.FileName == RemeasureBed.Cli);
    }

    [Fact]
    public async Task In_all_the_launch_check_of_a_held_command_reads_updating_on_this_machine()
    {
        PathIsAsAssumed();
        var gate = new AgentUpdateGate();
        var catalog = new AgentCatalog([new AgentDefinition("absent-headless", AgentMode.Headless, new AgentLaunch(Absent, []), LaunchCheck: ["--version"])]);
        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat(), updates: gate);
        Assert.Equal(AgentLaunchReport.NotChecked, (await runner.CheckLaunchAsync("absent-headless", Ct)).Result);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = gate.UpdateAsync(Absent, _ => release.Task, Ct);
        var report = await runner.CheckLaunchAsync("absent-headless", Ct);

        Assert.Equal(AgentLaunchReport.Updating, report.Result);
        Assert.Equal($"Updating {Absent} on this machine; it is measured again when the update ends.", report.Detail);
        release.SetResult(true);
        await update.WaitAsync(Bound, Ct);
    }

    // ---------------------------------------------------------------------------------------------
    // The tool pre-flight.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_tool_pre_flight_does_not_list_a_held_command()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        var held = await bed.HoldAsync(RemeasureBed.Cli, AgentUpdatePhases.Updating, "w1");

        var record = await bed.Tools.PassAsync(Ct);

        Assert.DoesNotContain(w1.Listings, l => l.Commands.Any(c => c.FileName == RemeasureBed.Cli));
        var claude = Assert.Single(record.Presets, p => p.Preset == RemeasureBed.Preset);
        Assert.Equal(ToolVerdicts.NotMeasured, claude.Verdict);
        Assert.Equal("Updating claude on w1; it is measured again when the update ends.", claude.Detail);
        Assert.Empty(claude.Ran);

        await held.EndAsync();
        var after = Assert.Single((await bed.Tools.PassAsync(Ct)).Presets, p => p.Preset == RemeasureBed.Preset);
        Assert.Equal(ToolVerdicts.Isolated, after.Verdict);
        Assert.Contains(w1.Listings, l => l.Commands.Any(c => c.FileName == RemeasureBed.Cli));
    }

    // ---------------------------------------------------------------------------------------------
    // The live scenario, and the end of the update.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A worker joins as the start's update is asked: its join measurement either has its probe out when
    /// the hold begins (and the worker answers "not installed" from the half-replaced install), or starts
    /// after it (and does not ask). Neither ever reads <c>AgentNotInstalled</c>, before or after the end.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_update_held_at_the_join_never_lights_not_installed(bool probeOutFirst)
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1", w => w.Installed[RemeasureBed.Cli] = false);
        var claude = bed.Catalog.Definition(RemeasureBed.Preset)!;
        RemeasureBed.Held? held = null;

        if (probeOutFirst)
        {
            w1.BeforeProbeAnswer = _ => held ??= bed.HoldAsync(RemeasureBed.Cli, AgentUpdatePhases.Updating, "w1").GetAwaiter().GetResult();
        }
        else
        {
            held = await bed.HoldAsync(RemeasureBed.Cli, AgentUpdatePhases.Waiting);
        }

        await MeasureInstallsWhenAWorkerJoins.MeasureAsync(
            w1.Scripted, MeasureInstallsWhenAWorkerJoins.Commands(bed.Catalog), bed.Asks, bed.Installs, ct: Ct, gate: bed.Gate);
        await bed.Auth.ReportsAsync(Ct);

        Assert.NotNull(held);
        Assert.Equal(AgentInstallStates.Updating, bed.Probe.Probe(claude, referenced: true).State);
        Assert.DoesNotContain(bed.Probe.ProbeAll(bed.Catalog.Definitions, new HashSet<string> { RemeasureBed.Preset }),
            i => i.State == AgentInstallStates.NotInstalled);

        // The swap is over: the worker has it again, and is asked again when the hold ends.
        w1.Installed[RemeasureBed.Cli] = true;
        await held!.EndAsync();
        Assert.NotEqual(AgentInstallStates.NotInstalled, bed.Probe.Probe(claude).State);
        await MeasureInstallsWhenAWorkerJoins.MeasureAsync(w1.Scripted, [RemeasureBed.Cli], bed.Asks, bed.Installs, ct: Ct, gate: bed.Gate);
        var after = bed.Probe.Probe(claude);
        Assert.Null(after.State);
        Assert.Equal("claude is installed on w1.", after.Message);
    }

    [Fact]
    public async Task When_the_update_ends_every_worker_is_measured_again_and_reads_installed()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        var w2 = bed.Join("w2");
        var ended = Wire(bed);
        var claude = bed.Catalog.Definition(RemeasureBed.Preset)!;
        var held = await bed.HoldAsync(RemeasureBed.Cli, AgentUpdatePhases.Updating, "w1");
        Assert.Equal(AgentInstallStates.Updating, bed.Probe.Probe(claude).State);

        await held.EndAsync();
        await ended.WaitAsync(Bound, Ct);
        await bed.SettleAsync(1);

        Assert.All([w1, w2], w => Assert.Contains(w.Probes, p => p.Commands.Any(c => c.Command == RemeasureBed.Cli)));
        var after = bed.Probe.Probe(claude);
        Assert.Null(after.State);
        Assert.Equal("claude is installed on w1 and w2.", after.Message);
        EverySurfaceReadsInstalled(bed);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task A_failed_or_cancelled_update_is_measured_again_too(string ending)
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        var ended = Wire(bed);
        var claude = bed.Catalog.Definition(RemeasureBed.Preset)!;
        var run = await bed.Gate.EnterRunAsync(RemeasureBed.Cli, null, Ct);
        var release = new TaskCompletionSource<AgentUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        bed.Gate.Request(RemeasureBed.Cli, RemeasureBed.Preset, null, _ => release.Task);
        Assert.Equal(AgentInstallStates.Updating, bed.Probe.Probe(claude).State);

        if (ending == "cancelled")
        {
            Assert.NotNull(bed.Gate.Cancel(RemeasureBed.Cli, "person@example.test").State);
        }
        else
        {
            run.Dispose();
            release.SetException(new InvalidOperationException("the update broke"));
        }

        await ended.WaitAsync(Bound, Ct);
        await bed.SettleAsync(1);
        run.Dispose();

        Assert.Contains(w1.Probes, p => p.Commands.Any(c => c.Command == RemeasureBed.Cli));
        Assert.Null(bed.Probe.Probe(claude).State);
        Assert.Equal("claude is installed on w1.", bed.Probe.Probe(claude).Message);
        EverySurfaceReadsInstalled(bed);
    }

    [Fact]
    public async Task After_the_update_with_no_worker_left_it_reads_not_measured_never_installed()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        var ended = Wire(bed);
        var claude = bed.Catalog.Definition(RemeasureBed.Preset)!;
        var held = await bed.HoldAsync(RemeasureBed.Cli, AgentUpdatePhases.Updating, "w1");

        bed.Pool.Leave(w1.Scripted);
        await held.EndAsync();
        await ended.WaitAsync(Bound, Ct);
        await bed.SettleAsync(1);

        var after = bed.Probe.Probe(claude);
        Assert.Null(after.State);
        Assert.Equal("claude has not been measured: no worker is connected.", after.Message);
        Assert.Null(bed.AuthRecord!.Worker);
        Assert.Null(bed.AuthRecord!.For(RemeasureBed.Cli)!.Installed);
        Assert.Null(bed.AuthRecord!.For(RemeasureBed.Cli)!.Updating);
    }

    [Fact]
    public async Task Between_the_end_and_the_pass_nothing_reads_updating_or_not_installed()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        var claude = bed.Catalog.Definition(RemeasureBed.Preset)!;
        await MeasureInstallsWhenAWorkerJoins.MeasureAsync(w1.Scripted, [RemeasureBed.Cli], bed.Asks, bed.Installs, ct: Ct, gate: bed.Gate);
        Assert.Null(bed.Probe.Probe(claude).State);
        var ended = Wire(bed);

        // During the swap the worker finds no program; it is not asked, and nothing it says is kept.
        var held = await bed.HoldAsync(RemeasureBed.Cli, AgentUpdatePhases.Updating, "w1");
        w1.Installed[RemeasureBed.Cli] = false;
        await MeasureInstallsWhenAWorkerJoins.MeasureAsync(w1.Scripted, [RemeasureBed.Cli, RemeasureBed.Other], bed.Asks, bed.Installs, ct: Ct, gate: bed.Gate);
        w1.Installed[RemeasureBed.Cli] = true;
        await held.EndAsync();
        await ended.WaitAsync(Bound, Ct);
        Assert.Equal(0, bed.Pass.Passes);

        // Inside the quiet window, before any pass: neither updating nor not installed, anywhere.
        var install = bed.Probe.Probe(claude);
        Assert.Null(install.State);
        Assert.Null(install.Updating);
        var auth = Assert.Single(await bed.Auth.ReportsAsync(Ct), r => r.Agent == RemeasureBed.Preset);
        Assert.Equal((true, (string?)null), (auth.Installed, auth.Updating));
        Assert.Equal(AgentLaunchReport.Ok, (await bed.Launch.ReportsAsync(Ct))[RemeasureBed.Preset].Result);
    }

    /// <summary>The end-of-update wiring as control composes it, over the bed: a poke, and every worker measured.</summary>
    private static Task Wire(RemeasureBed bed)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RemeasureWhenWorkersChange.Wire(
            control: true, _ => { }, _ => { },
            onEnded => bed.Gate.UpdateEnded += command => onEnded(command),
            () => bed.Pass,
            async command =>
            {
                await Task.WhenAll(bed.Pool.Workers.Select(w => bed.Pool.For(w)).OfType<IRunWorker>().Select(worker =>
                    MeasureInstallsWhenAWorkerJoins.MeasureAsync(worker, [command], bed.Asks, bed.Installs, ct: Ct, gate: bed.Gate)));
                ended.TrySetResult();
            });
        return ended.Task;
    }

    /// <summary>After the pass: every record reads the CLI installed and listed, and none still says updating.</summary>
    private static void EverySurfaceReadsInstalled(RemeasureBed bed)
    {
        var auth = bed.AuthRecord!.For(RemeasureBed.Cli)!;
        Assert.Equal((true, (string?)null), (auth.Installed, auth.Updating));
        Assert.Equal(AgentLaunchReport.Ok, bed.LaunchRecord!.ForCommand(RemeasureBed.Cli).Result);
        var tools = Assert.Single(bed.ToolsRecord!.Presets, p => p.Preset == RemeasureBed.Preset);
        Assert.Equal(ToolVerdicts.Isolated, tools.Verdict);
        foreach (var file in new[] { AgentAuthRecord.FileName, AgentLaunchChecksRecord.FileName, AgentToolsRecord.FileName })
        {
            Assert.DoesNotContain("Updating claude", File.ReadAllText(Path.Combine(bed.Root, file)), StringComparison.Ordinal);
        }
    }
}
