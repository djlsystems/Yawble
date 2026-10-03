using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// IN CONTROL, EVERYTHING MEASURED THROUGH A WORKER IS MEASURED AGAIN WHEN THE WORKERS CHANGE: the
/// sign-in probe, the launch check, the tool pre-flight and the CLI version record, as one pass once
/// the changes have been quiet for <see cref="RemeasureWhenWorkersChange.Quiet"/> - so a burst of joins
/// measures once - and postponed by a steady stream of changes by at most
/// <see cref="RemeasureWhenWorkersChange.Ceiling"/>. With the last worker gone every record reads not
/// measured, at once for a read and in the files once the pass has run. Every worker is scripted, the
/// clock is the test's, and no CLI or update is run.
/// </summary>
public sealed class RemeasureOnWorkersTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_worker_joining_after_start_measures_the_sign_in_the_launch_check_and_the_tools_on_that_worker()
    {
        using var bed = new RemeasureBed();

        // At start no worker has joined: every record says so.
        bed.Pass.Poke("start");
        await bed.SettleAsync(1);
        Assert.Null(bed.AuthRecord!.Worker);
        Assert.Contains(bed.ToolsRecord!.Presets, p => p.Detail?.Contains(WorkerListingRunner.NoWorkerText) == true);
        Assert.Equal(ProcessAgentRunner.NotCheckedHere, bed.LaunchRecord!.Presets[0].Launch.Detail);

        var w1 = bed.Join("w1");
        bed.Pass.Poke("w1 can be asked");
        await bed.SettleAsync(2);

        var auth = bed.AuthRecord!;
        Assert.Equal("w1", auth.Worker);
        Assert.All(auth.Commands, c => Assert.Equal((true, (bool?)true, $"{c.Command} on w1"), (c.Installed!.Value, c.Authenticated, c.Detail)));

        Assert.All(bed.LaunchRecord!.Presets, p => Assert.Equal((AgentLaunchReport.Ok, "started on w1"), (p.Launch.Result, p.Launch.Detail)));
        Assert.Equal(2, w1.Checks.Count);

        var tools = bed.ToolsRecord!;
        Assert.DoesNotContain(tools.Presets, p => p.Detail?.Contains(WorkerListingRunner.NoWorkerText) == true);
        var claude = Assert.Single(tools.Presets, p => p.Preset == RemeasureBed.Preset);
        Assert.Equal(ToolVerdicts.Isolated, claude.Verdict);
        Assert.NotEmpty(claude.Ran);
        Assert.Contains(w1.Listings, l => l.Commands.Any(c => c.FileName == RemeasureBed.Cli));
    }

    [Fact]
    public async Task The_join_writes_the_doctors_agent_tools_row_measured_without_a_catalog_save()
    {
        using var bed = new RemeasureBed();
        bed.Pass.Poke("start");
        await bed.SettleAsync(1);
        var before = await HostDoctor.ReportAsync(bed.Root, Ct);
        Assert.All(before.AgentTools!.Presets.Where(p => p.Preset == RemeasureBed.Preset), p => Assert.Equal(ToolVerdicts.NotMeasured, p.Verdict));

        bed.Join("w1");
        bed.Pass.Poke("w1 can be asked");
        await bed.SettleAsync(2);

        var after = await HostDoctor.ReportAsync(bed.Root, Ct);
        Assert.Equal(ToolVerdicts.Isolated, Assert.Single(after.AgentTools!.Presets, p => p.Preset == RemeasureBed.Preset).Verdict);
        Assert.True(after.AgentTools.At > before.AgentTools.At);
    }

    [Fact]
    public async Task A_burst_of_joins_measures_once()
    {
        using var bed = new RemeasureBed();
        var workers = new[] { bed.Join("w1"), bed.Join("w2"), bed.Join("w3") };
        foreach (var worker in workers) bed.Pass.Poke($"{worker.Id} joined");

        await bed.SettleAsync(1);
        await Task.Delay(100, Ct);

        Assert.Equal(1, bed.Pass.Passes);
        Assert.Single(workers.SelectMany(w => w.Probes));
        Assert.Equal(2, workers.Sum(w => w.Checks.Count));
        Assert.Single(workers.SelectMany(w => w.VersionRuns));
        // One listing round: each listing command once, for the one preset listed.
        var listed = workers.SelectMany(w => w.Listings).SelectMany(l => l.Commands).Select(c => string.Join(' ', [c.FileName, .. c.Arguments])).ToList();
        Assert.Equal(listed.Distinct().Count(), listed.Count);
    }

    [Fact]
    public async Task Nothing_is_measured_before_the_quiet_window_has_passed()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        bed.Pass.Poke("w1 joined");

        bed.Clock.Advance(RemeasureWhenWorkersChange.Quiet - TimeSpan.FromMilliseconds(1));
        await Task.Delay(200, Ct);
        Assert.Equal(0, bed.Pass.Passes);
        Assert.Empty(w1.Scripted.Sent);

        bed.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await bed.Pass.WaitForPassAsync(1).WaitAsync(Bound, Ct);
        Assert.Single(w1.Probes);
    }

    [Fact]
    public async Task A_burst_spread_wider_than_the_window_measures_once_after_the_last_join()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        bed.Pass.Poke("w1 joined");
        bed.Clock.Advance(TimeSpan.FromSeconds(2));
        bed.Join("w2");
        bed.Pass.Poke("w2 joined");
        bed.Clock.Advance(TimeSpan.FromSeconds(2));
        bed.Join("w3");
        bed.Pass.Poke("w3 joined");
        bed.Clock.Advance(TimeSpan.FromSeconds(2));

        // Six seconds since the first, two since the last: nothing yet.
        await Task.Delay(200, Ct);
        Assert.Equal(0, bed.Pass.Passes);
        Assert.Empty(w1.Probes);

        bed.Clock.Advance(TimeSpan.FromSeconds(1));
        await bed.Pass.WaitForPassAsync(1).WaitAsync(Bound, Ct);
        await Task.Delay(100, Ct);
        Assert.Equal(1, bed.Pass.Passes);
    }

    [Fact]
    public async Task A_steady_stream_of_pokes_postpones_the_pass_by_at_most_thirty_seconds()
    {
        using var bed = new RemeasureBed();
        bed.Join("w1");

        // A change every two seconds, never quiet for three.
        for (var at = 0; at < 30; at += 2)
        {
            bed.Pass.Poke("a worker came and went");
            bed.Clock.Advance(TimeSpan.FromSeconds(2));
            if (at + 2 < 30)
            {
                await Task.Delay(20, Ct);
                Assert.Equal(0, bed.Pass.Passes);
            }
        }

        // Thirty seconds after the first poke the pass ran, however the pokes went on.
        await bed.Pass.WaitForPassAsync(1).WaitAsync(Bound, Ct);
        Assert.Equal(1, bed.Pass.Passes);
        Assert.Equal(RemeasureWhenWorkersChange.Ceiling, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_join_during_a_pass_measures_once_more_after_it()
    {
        using var bed = new RemeasureBed();
        var going = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var w1 = bed.Join("w1", w => w.BeforeProbeAnswer = _ =>
        {
            if (going.TrySetResult()) release.Task.Wait(Bound);
        });
        bed.Pass.Poke("w1 joined");
        bed.Clock.Advance(RemeasureWhenWorkersChange.Quiet);
        await going.Task.WaitAsync(Bound, Ct);

        // w2 joins while the first pass is out asking.
        bed.Join("w2");
        bed.Pass.Poke("w2 joined");
        bed.Clock.Advance(RemeasureWhenWorkersChange.Quiet);
        release.SetResult();

        await bed.Pass.WaitForPassAsync(2).WaitAsync(Bound, Ct);
        await Task.Delay(200, Ct);
        Assert.Equal(2, bed.Pass.Passes);
    }

    [Fact]
    public async Task The_last_worker_leaving_reads_not_measured_in_every_record()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        bed.Pass.Poke("w1 joined");
        await bed.SettleAsync(1);
        var (auth, launch, tools) = (bed.AuthRecord!, bed.LaunchRecord!, bed.ToolsRecord!);
        Assert.Equal("w1", auth.Worker);
        await Task.Delay(20, Ct);

        bed.Pool.Leave(w1.Scripted);
        bed.Pass.Poke("w1 is gone");
        await bed.SettleAsync(2);

        // Every record was written again, later, and keeps nothing w1 said.
        var authAfter = bed.AuthRecord!;
        Assert.True(authAfter.At > auth.At);
        Assert.Null(authAfter.Worker);
        Assert.All(authAfter.Commands, c => Assert.Equal((null, null, AgentAuthProbe.NoWorkerText), (c.Installed, c.Authenticated, c.Detail)));

        var launchAfter = bed.LaunchRecord!;
        Assert.True(launchAfter.At > launch.At);
        Assert.All(launchAfter.Presets, p => Assert.Equal((AgentLaunchReport.NotChecked, ProcessAgentRunner.NotCheckedHere), (p.Launch.Result, p.Launch.Detail)));

        var toolsAfter = bed.ToolsRecord!;
        Assert.True(toolsAfter.At > tools.At);
        var claude = Assert.Single(toolsAfter.Presets, p => p.Preset == RemeasureBed.Preset);
        Assert.Equal(ToolVerdicts.NotMeasured, claude.Verdict);
        Assert.Contains(WorkerListingRunner.NoWorkerText, claude.Detail);

        foreach (var file in new[] { AgentAuthRecord.FileName, AgentLaunchChecksRecord.FileName, AgentToolsRecord.FileName })
        {
            Assert.DoesNotContain("w1", File.ReadAllText(Path.Combine(bed.Root, file)), StringComparison.Ordinal);
        }

        var install = bed.Probe.Probe(bed.Catalog.Definition(RemeasureBed.Preset)!);
        Assert.Null(install.State);
        Assert.Equal("claude has not been measured: no worker is connected.", install.Message);
    }

    [Fact]
    public async Task A_read_in_the_quiet_window_after_the_last_worker_left_reads_not_measured()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        bed.Pass.Poke("w1 joined");
        await bed.SettleAsync(1);
        Assert.True(Assert.Single(await bed.Auth.ReportsAsync(Ct), r => r.Agent == RemeasureBed.Preset).Installed);
        Assert.Equal(AgentLaunchReport.Ok, (await bed.Launch.ReportsAsync(Ct))[RemeasureBed.Preset].Result);

        bed.Pool.Leave(w1.Scripted);
        bed.Pass.Poke("w1 is gone");

        // No pass yet: the caches were dropped at the poke, so a read now asks, and nobody is there.
        Assert.Equal(1, bed.Pass.Passes);
        var auth = Assert.Single(await bed.Auth.ReportsAsync(Ct), r => r.Agent == RemeasureBed.Preset);
        Assert.Equal((null, null, AgentAuthProbe.NoWorkerText), (auth.Installed, auth.Authenticated, auth.Detail));
        Assert.Equal(ProcessAgentRunner.NotCheckedHere, (await bed.Launch.ReportsAsync(Ct))[RemeasureBed.Preset].Detail);
    }

    [Fact]
    public async Task One_of_two_workers_leaving_measures_again_on_the_one_left()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        var w2 = bed.Join("w2");
        bed.Pass.Poke("joined");
        await bed.SettleAsync(1);
        var first = bed.AuthRecord!.Worker!;
        var (gone, left) = first == "w1" ? (w1, w2) : (w2, w1);

        bed.Pool.Leave(gone.Scripted);
        bed.Pass.Poke($"{gone.Id} is gone");
        await bed.SettleAsync(2);

        Assert.Equal(left.Id, bed.AuthRecord!.Worker);
        Assert.All(bed.LaunchRecord!.Presets, p => Assert.Equal($"started on {left.Id}", p.Launch.Detail));
        Assert.Single(left.Probes);
    }

    [Fact]
    public async Task With_two_workers_the_records_name_the_worker_with_most_headroom()
    {
        using var bed = new RemeasureBed();
        // w2 joins last, but w1 has room: it holds no run and w2 holds two.
        bed.Pool.Placed = worker => worker.Value == "w2" ? 2 : 0;
        var w1 = bed.Join("w1");
        var w2 = bed.Join("w2");
        bed.Pass.Poke("w2 joined");
        await bed.SettleAsync(1);

        Assert.Equal("w1", bed.AuthRecord!.Worker);
        Assert.All(bed.LaunchRecord!.Presets, p => Assert.Equal("started on w1", p.Launch.Detail));
        Assert.Empty(w2.Probes);
        Assert.Single(w1.Probes);
    }

    [Fact]
    public async Task A_worker_coming_back_is_measured_again_once_it_can_be_sent_to()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        bed.Pool.Drop(w1.Scripted.Id);
        w1.Scripted.Dropped = true;
        bed.Pass.Poke("w1 dropped");
        await bed.SettleAsync(1);
        Assert.Null(bed.AuthRecord!.Worker);

        // Back: control hears Changed before the socket is attached, and Attached once it is.
        bed.Pool.Back(w1.Scripted.Id);
        bed.Pass.Poke("w1 is back");
        w1.Scripted.Dropped = false;
        bed.Pass.Poke("w1 can be asked");
        await bed.SettleAsync(2);

        Assert.Equal("w1", bed.AuthRecord!.Worker);
        Assert.All(bed.AuthRecord!.Commands, c => Assert.True(c.Installed));
        Assert.DoesNotContain("did not answer", File.ReadAllText(Path.Combine(bed.Root, AgentToolsRecord.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_poke_is_wired_from_changed_attached_and_the_end_of_an_update()
    {
        using var bed = new RemeasureBed();
        Action? changed = null;
        Action<WorkerId>? attached = null;
        Action<string>? ended = null;

        var wired = RemeasureWhenWorkersChange.Wire(
            control: true, c => changed += c, a => attached += a, e => ended += e, () => bed.Pass);
        Assert.Same(bed.Pass, wired);

        changed!();
        await bed.SettleAsync(1);
        attached!(new WorkerId("w1"));
        await bed.SettleAsync(2);
        ended!(RemeasureBed.Cli);
        await bed.SettleAsync(3);

        Assert.Null(RemeasureWhenWorkersChange.Wire(control: false, _ => throw new InvalidOperationException(), _ => { }, _ => { }, () => bed.Pass));
    }

    // ---------------------------------------------------------------------------------------------
    // The CLI version record.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_version_record_gains_a_measured_line_only_when_a_version_changed()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        bed.Pass.Poke("w1 joined");
        await bed.SettleAsync(1);

        var lines = await bed.Versions.ReadAsync(CliVersionHistory.MaxTake, Ct);
        var line = Assert.Single(lines);
        Assert.Equal((CliVersionsWhenWorkersChange.By, "w1"), (line.By, line.Worker));
        Assert.Equal("2.0.1 (Claude Code)", line.Versions[RemeasureBed.Cli]);

        // The same versions again: nothing is appended.
        bed.Pass.Poke("again");
        await bed.SettleAsync(2);
        Assert.Single(await bed.Versions.ReadAsync(CliVersionHistory.MaxTake, Ct));

        // A different one: a line, and the version reads as measured.
        w1.Version = "2.0.2 (Claude Code)";
        bed.Pass.Poke("again");
        await bed.SettleAsync(3);
        var now = CliVersionHistory.Now(RemeasureBed.Cli, await bed.Versions.ReadAsync(CliVersionHistory.MaxTake, Ct));
        Assert.Equal(("2.0.2 (Claude Code)", CliVersionsWhenWorkersChange.By), (now.Version, now.UpdatedBy));
    }

    [Fact]
    public async Task The_version_measurement_runs_with_updates_off()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        bed.Pass.Poke("w1 joined");
        await bed.SettleAsync(1);

        var sent = w1.Scripted.Sent.OfType<RunAgentCommands>().SelectMany(r => r.Commands).ToList();
        Assert.NotEmpty(sent);
        Assert.All(sent, run => Assert.False(run.WithUpdatesOn));
        Assert.DoesNotContain(sent, run => run.FileName == RemeasureBed.Updates.Update![0]);

        var claude = Assert.Single(Assert.Single(w1.VersionRuns).Commands, c => c.FileName == RemeasureBed.Cli);
        Assert.Equal([.. RemeasureBed.Updates.Arguments!, "--version"], claude.Arguments);
        Assert.Equal("1", claude.Environment["DISABLE_AUTOUPDATER"]);
    }

    [Fact]
    public async Task The_version_measurement_skips_a_held_command()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        var held = await bed.HoldAsync(RemeasureBed.Cli, AgentUpdatePhases.Updating, "w1");

        await CliVersionsWhenWorkersChange.MeasureAsync(bed.Catalog, bed.Asks, bed.Root, bed.Gate, ct: Ct);

        Assert.DoesNotContain(Assert.Single(w1.VersionRuns).Commands, c => c.FileName == RemeasureBed.Cli);
        Assert.DoesNotContain(RemeasureBed.Cli, Assert.Single(await bed.Versions.ReadAsync(CliVersionHistory.MaxTake, Ct)).Versions.Keys);
        await held.EndAsync();
    }

    [Fact]
    public async Task The_last_worker_leaving_keeps_the_version_line_with_its_date_and_writes_none()
    {
        using var bed = new RemeasureBed();
        var w1 = bed.Join("w1");
        bed.Pass.Poke("w1 joined");
        await bed.SettleAsync(1);
        var before = Assert.Single(await bed.Versions.ReadAsync(CliVersionHistory.MaxTake, Ct));

        bed.Pool.Leave(w1.Scripted);
        bed.Pass.Poke("w1 is gone");
        await bed.SettleAsync(2);

        var after = Assert.Single(await bed.Versions.ReadAsync(CliVersionHistory.MaxTake, Ct));
        Assert.Equal(before.At, after.At);
        Assert.Equal(before.Versions[RemeasureBed.Cli], after.Versions[RemeasureBed.Cli]);
    }

    [Fact]
    public async Task A_worker_that_does_not_answer_writes_no_version_line()
    {
        using var bed = new RemeasureBed();
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var silent = new ScriptedCliWorker("w9");
        var asks = new WorkerAsks(() => silent, clock);
        silent.Asks = asks;

        var measuring = CliVersionsWhenWorkersChange.MeasureAsync(bed.Catalog, asks, bed.Root, bed.Gate, ct: Ct);
        while (silent.Sent.IsEmpty) await Task.Delay(10, Ct);
        clock.Advance(TimeSpan.FromHours(1));
        await measuring.WaitAsync(Bound, Ct);

        Assert.Empty(await bed.Versions.ReadAsync(CliVersionHistory.MaxTake, Ct));
    }
}
